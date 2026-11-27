using Xunit;

namespace DittoHopper.Core.Tests;

/// <summary>One test per accidental-change detector, plus the "needs a look" rule.</summary>
public class DetectorTests
{
    private static readonly int Blue = unchecked((int)0xFF0000FF);

    private static Func<ObjectRecord, ObjectRecord> OwnColor(int color) => o => o with { Color = new Sourced<int>(color, ValueSource.ByObject) };

    private static ChangeSet SetOf(DiffResult r, Guid a) => r.ChangeSets.Single(s => s.Objects.Any(p => p.A == a || p.B == a));

    [Fact]
    public void OutsideScope_FlagsChangesWhoseAOrBLayerMissesTheGlob()
    {
        var a = new SnapshotBuilder();
        var facade = a.Layer("Facade");
        var panels = a.Layer("Panels", facade);
        var columns = a.Layer("Columns", a.Layer("Structure"));
        var panel = a.Object(panels, OwnColor(Blue));
        var column = a.Object(columns, OwnColor(Blue));
        var leaving = a.Object(panels);
        var b = a.Clone();
        b.Edit(panel, OwnColor(Fixtures.Red));
        b.Edit(column, OwnColor(Fixtures.Red));
        b.Move([leaving], columns);

        var r = ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { ScopeGlob = "facade::*" });
        Assert.Empty(SetOf(r, panel).Flags);
        Assert.Equal([Detectors.OutsideScope], SetOf(r, column).Flags);
        Assert.Equal([Detectors.OutsideScope], SetOf(r, leaving).Flags);
        Assert.True(LayerGlob.IsMatch("Facade::*", "Facade::Panels::Glass"));
        Assert.False(LayerGlob.IsMatch("Facade::*", "Facade"));
    }

    [Fact]
    public void DefaultLayer_FlagsMovesToDefaultOrTheCurrentLayer()
    {
        var a = new SnapshotBuilder();
        var dflt = a.Layer("Default");
        var walls = a.Layer("Walls");
        var current = a.Layer("Sketch");
        var nested = a.Layer("Default", walls);
        var x = a.Object(walls);
        var y = a.Object(walls);
        var z = a.Object(walls);
        var b = a.Clone();
        b.CurrentLayer = current;
        b.Move([x], dflt);
        b.Move([y], current);
        b.Move([z], nested);   // a sublayer that happens to be called Default isn't the Default layer

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Equal([Detectors.DefaultLayer], SetOf(r, x).Flags);
        Assert.Equal([Detectors.DefaultLayer], SetOf(r, y).Flags);
        Assert.Empty(SetOf(r, z).Flags);
    }

    private sealed class MoveEverything(double dx) : IGeometryService
    {
        public ShapeDeviation? Deviation(Guid a, Guid b, Xform? moveA, double tolerance) => moveA is null ? null : new ShapeDeviation(0);
        public PointCorrespondence? CorrespondingPoints(Guid a, Guid b) =>
            new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [new(dx, 0, 0), new(1 + dx, 0, 0), new(dx, 1, 0)]);
        public string? DataOnlyReason(Guid a, Guid b) => null;
        public StructureArrays? Structure(Guid a, Guid b) => null;
    }

    [Theory]
    [InlineData(0.05, true)]    // tolerance 0.01 < 0.05 < nudge threshold 0.1
    [InlineData(0.5, false)]
    public void TinyNudge_FlagsMovesBetweenToleranceAndTheNudgeThreshold(double dx, bool flagged)
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("Columns");
        var column = a.Object(layer);
        var block = a.Block("Plate");
        var plate = a.Instance(layer, block);
        var b = a.Clone();
        b.Edit(column, o => o with { Geometry = o.Geometry with { Hash = 1 } });
        b.Edit(plate, o => o with { InstanceXform = Xform.Translation(dx, 0, 0) });

        var set = Assert.Single(ModelDiff.Diff(a.Build(), b.Build(), geometry: new MoveEverything(dx)).ChangeSets);
        Assert.Equal(2, set.Objects.Count);
        Assert.Equal(flagged ? [Detectors.TinyNudge] : [], set.Flags);
        // A wider nudge threshold catches the bigger slip too.
        var wide = ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { NudgeFactor = 100 }, new MoveEverything(dx));
        Assert.Equal([Detectors.TinyNudge], Assert.Single(wide.ChangeSets).Flags);
    }

    [Fact]
    public void ByLayerOverride_FlagsAByLayerValueSetOnTheObject()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("Slabs");
        var inherited = a.Object(layer);
        var own = a.Object(layer, OwnColor(Fixtures.Red));
        var b = a.Clone();
        b.Edit(inherited, OwnColor(Fixtures.Red));
        b.Edit(own, OwnColor(Blue));

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Equal([Detectors.ByLayerOverride], SetOf(r, inherited).Flags);
        Assert.Empty(SetOf(r, own).Flags);
    }

    [Fact]
    public void Duplicate_FlagsAnAddedObjectWithAnExistingHash()
    {
        var r = Fixtures.Diff(Fixtures.CopyInPlace());
        Assert.Equal([Detectors.Duplicate], Assert.Single(r.ChangeSets).Flags);

        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        a.Object(layer);
        var b = a.Clone();
        b.Object(layer);   // new, with a hash of its own
        Assert.Empty(Assert.Single(ModelDiff.Diff(a.Build(), b.Build()).ChangeSets).Flags);
    }

    [Fact]
    public void LockedOrHiddenLayer_FlagsChangesOnLayersLockedOrHiddenInA()
    {
        var a = new SnapshotBuilder();
        var locked = a.Layer("Locked", locked: true);
        var hidden = a.Layer("Hidden", visible: false);
        var child = a.Layer("Child", hidden);
        var open = a.Layer("Open");
        var x = a.Object(locked);
        var y = a.Object(child);
        var z = a.Object(open);
        var gone = a.Object(locked);
        var b = a.Clone();
        foreach (var id in new[] { x, y, z }) b.Edit(id, OwnColor(Fixtures.Red));
        b.RemoveObject(gone);
        b.Object(locked);

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Contains(Detectors.LockedOrHiddenLayer, SetOf(r, x).Flags);
        Assert.Contains(Detectors.LockedOrHiddenLayer, SetOf(r, y).Flags);
        Assert.DoesNotContain(Detectors.LockedOrHiddenLayer, SetOf(r, z).Flags);
        Assert.All(r.ChangeSets.Where(s => s.Kind != ChangeKind.Modified), s => Assert.Contains(Detectors.LockedOrHiddenLayer, s.Flags));
    }

    [Fact]
    public void LoneDeletion_FlagsAFewRemovalsFromALayerWhereNothingElseChanged()
    {
        var a = new SnapshotBuilder();
        var quiet = a.Layer("Quiet");
        var busy = a.Layer("Busy");
        var many = a.Layer("Many");
        var q = a.Many(quiet, 5);
        var k = a.Many(busy, 5);
        var m = a.Many(many, 6);
        var b = a.Clone();
        q.Take(2).ToList().ForEach(b.RemoveObject);
        k.Take(2).ToList().ForEach(b.RemoveObject);
        b.Edit(k[4], OwnColor(Fixtures.Red));
        m.Take(4).ToList().ForEach(b.RemoveObject);

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Equal([Detectors.LoneDeletion], SetOf(r, q[0]).Flags);
        Assert.Empty(SetOf(r, k[0]).Flags);   // something else changed on Busy
        Assert.Empty(SetOf(r, m[0]).Flags);   // more than three
    }

    [Fact]
    public void MassChange_FlagsTheSameChangeAcrossThreeUnrelatedLayers()
    {
        var a = new SnapshotBuilder();
        var facade = a.Layer("Facade");
        var siblings = new[] { a.Layer("Panels", facade), a.Layer("Mullions", facade), a.Layer("Glass", facade) };
        var unrelated = new[] { a.Layer("Walls"), a.Layer("Doors"), a.Layer("Slabs") };
        var onSiblings = siblings.Select(l => a.Object(l)).ToList();
        var onUnrelated = unrelated.Select(l => a.Object(l)).ToList();
        var b = a.Clone();
        onSiblings.ForEach(id => b.Edit(id, OwnColor(Blue)));
        onUnrelated.ForEach(id => b.Edit(id, OwnColor(Fixtures.Red)));

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.DoesNotContain(Detectors.MassChange, SetOf(r, onSiblings[0]).Flags);
        Assert.Contains(Detectors.MassChange, SetOf(r, onUnrelated[0]).Flags);
    }

    [Fact]
    public void ASetNeedsALookUntilItIsMarkedIntended()
    {
        var r = Fixtures.Diff(Fixtures.DefaultLayer());
        var set = Assert.Single(r.ChangeSets);
        Assert.True(set.NeedsLook);
        Assert.False(r.WithReview(set.Id, ReviewState.Intended).ChangeSets[0].NeedsLook);
        Assert.Equal("default-layer", Detectors.DefaultLayer);
        Assert.Equal("Moved to the Default or current layer", Detectors.Describe(Detectors.DefaultLayer));
    }
}
