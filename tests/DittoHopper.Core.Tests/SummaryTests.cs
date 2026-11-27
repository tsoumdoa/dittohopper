using Xunit;

namespace DittoHopper.Core.Tests;

/// <summary>Level 1 of the review UI: the summary sentence and each layer's status.</summary>
public class SummaryTests
{
    /// <summary>Facade::Panels gets an intended edit; Walls gets a slip; Doors and Slabs are left alone.</summary>
    private static (SnapshotBuilder A, SnapshotBuilder B) Revision()
    {
        var a = new SnapshotBuilder();
        var panels = a.Layer("Panels", a.Layer("Facade"));
        var walls = a.Layer("Walls");
        a.Layer("Doors");
        a.Many(a.Layer("Slabs"), 2);
        var p = a.Many(panels, 3);
        var w = a.Many(walls, 2);
        var b = a.Clone();
        p.ForEach(id => b.Edit(id, o => o with { Name = "P" }));
        b.Edit(w[0], o => o with { Name = "W" });
        return (a, b);
    }

    [Fact]
    public void WithoutAScope_CountsIdenticalObjectsAndSuspiciousSets()
    {
        var r = Fixtures.Diff(Fixtures.DefaultLayer());
        Assert.Equal("2 of 25 objects are identical. 1 change set looks suspicious.", DiffSummary.Sentence(r));
        var signedOff = r.WithReview(r.ChangeSets[0].Id, ReviewState.Intended);
        Assert.Equal("2 of 25 objects are identical. 0 change sets look suspicious.", DiffSummary.Sentence(signedOff));
    }

    [Fact]
    public void WithAScope_CountsWhatChangedOutsideItAndTheUntouchedLayers()
    {
        var r = Fixtures.Diff(Revision(), new DiffSettings { ScopeGlob = "Facade::*" });
        Assert.Equal("Outside Facade::*, 1 object changed in 1 set. 3 layers are untouched.", DiffSummary.Sentence(r));

        var walls = r.ChangeSets.Single(s => s.Flags.Contains(Detectors.OutsideScope));
        Assert.Equal("Nothing outside Facade::* changed. 3 layers are untouched.",
            DiffSummary.Sentence(r.WithReview(walls.Id, ReviewState.Intended)));
    }

    [Fact]
    public void LayersAreUntouchedExpectedOrToCheck()
    {
        var r = Fixtures.Diff(Revision(), new DiffSettings { ScopeGlob = "Facade::*" });
        var status = DiffSummary.Layers(r).ToDictionary(s => s.Row.Path, s => (s.State, s.ToCheck, s.Row.Objects));
        Assert.Equal((LayerState.Untouched, 0, 0), status["Facade"]);
        Assert.Equal((LayerState.Expected, 0, 3), status["Facade::Panels"]);
        Assert.Equal((LayerState.ToCheck, 1, 2), status["Walls"]);
        Assert.Equal((LayerState.Untouched, 0, 0), status["Doors"]);
        Assert.Equal((LayerState.Untouched, 0, 2), status["Slabs"]);
    }

    [Fact]
    public void ATableEventTouchesItsLayers_AndAMovedObjectTouchesBoth()
    {
        var rename = DiffSummary.Layers(Fixtures.Diff(Fixtures.LayerRename()));
        Assert.Equal(LayerState.Expected, Assert.Single(rename).State);

        var moved = DiffSummary.Layers(Fixtures.Diff(Fixtures.DefaultLayer())).ToDictionary(s => s.Row.Path, s => s.State);
        Assert.Equal(LayerState.ToCheck, moved["Default"]);
        Assert.Equal(LayerState.ToCheck, moved["Walls"]);
    }

    [Fact]
    public void ScopeAppliesToLayerTableEventsToo()
    {
        var a = new SnapshotBuilder();
        var facade = a.Layer("Facade");
        var roof = a.Layer("Roof");
        a.Object(facade);
        a.Object(roof);
        var b = a.Clone();
        b.EditLayer(roof, l => l with { Color = Fixtures.Red });

        var r = Fixtures.Diff((a, b), new DiffSettings { ScopeGlob = "Facade" });
        Assert.Equal([Detectors.OutsideScope], Assert.Single(r.TableEvents).Flags);
        Assert.Equal("Outside Facade, 1 layer change. 1 layer is untouched.", DiffSummary.Sentence(r));
        Assert.Equal((LayerState.ToCheck, 1), DiffSummary.Layers(r).Where(l => l.Row.Path == "Roof").Select(l => (l.State, l.ToCheck)).Single());
    }

    private sealed class SameShape : IGeometryService
    {
        public ShapeDeviation? Deviation(Guid a, Guid b, Xform? moveA, double tolerance) => new ShapeDeviation(0);
        public PointCorrespondence? CorrespondingPoints(Guid a, Guid b) => null;
        public string? DataOnlyReason(Guid a, Guid b) => "rebuilt";
        public StructureArrays? Structure(Guid a, Guid b) => null;
    }

    [Fact]
    public void APairingUnderReviewIsNotAnObjectChange()
    {
        // A Data-only rebake on Roof, paired only by fuzzy score, with Facade in scope.
        var a = new SnapshotBuilder();
        a.Layer("Facade");
        var box = a.Solid(a.Layer("Roof"), new Vec3());
        var b = a.Clone();
        b.Edit(b.Rebake(box), o => o with { Geometry = SnapshotBuilder.SolidShape(1, new Vec3(), 100.015) });

        var r = ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { ScopeGlob = "Facade" }, new SameShape());
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal((PropertyKind.Pairing, Detectors.FuzzyMatch), (set.Change!.Property, Assert.Single(set.Flags)));
        Assert.Equal("Nothing outside Facade changed. 2 layers are untouched. 1 pairing needs review.", DiffSummary.Sentence(r));
        Assert.All(DiffSummary.Layers(r), l => Assert.Equal(LayerState.Untouched, l.State));
    }
}
