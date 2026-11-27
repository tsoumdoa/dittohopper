using Xunit;

namespace DittoHopper.Core.Tests;

/// <summary>Ladder steps 2–3 through a scripted geometry service: what the Rhino adapter would answer.</summary>
public class GeometryLadderTests
{
    /// <summary>Answers by A id: the unmoved deviation, the points, and the deviation once the fitted move is applied.</summary>
    private sealed class FakeGeometry : IGeometryService
    {
        public readonly Dictionary<Guid, double> Unmoved = [], Moved = [];
        public readonly Dictionary<Guid, (Vec3[] A, Vec3[] B)> Points = [];

        public ShapeDeviation? Deviation(Guid a, Guid b, Xform? moveA, double tolerance) =>
            (moveA is null ? Unmoved : Moved).TryGetValue(a, out var d) ? new ShapeDeviation(d, d / 2) : null;

        public PointCorrespondence? CorrespondingPoints(Guid a, Guid b) =>
            Points.TryGetValue(a, out var p) ? new PointCorrespondence(p.A, p.B) : null;

        public string? DataOnlyReason(Guid a, Guid b) => "rebuilt";
    }

    private static readonly Vec3[] Corners = [new(0, 0, 0), new(100, 0, 0), new(100, 50, 0), new(0, 50, 200)];

    /// <summary>A with <paramref name="count"/> curves; B is A with every curve's geometry hash changed.</summary>
    private static (SnapshotBuilder A, SnapshotBuilder B, List<Guid> Ids) Edited(int count)
    {
        var a = new SnapshotBuilder();
        var ids = a.Many(a.Layer("L"), count);
        var b = a.Clone();
        foreach (var id in ids) b.Edit(id, o => o with { Geometry = o.Geometry with { Hash = o.Geometry.Hash + 1 } });
        return (a, b, ids);
    }

    private static void MoveBy(FakeGeometry g, Guid id, Vec3 offset, double after = 0)
    {
        var x = Xform.Translation(offset.X, offset.Y, offset.Z);
        g.Points[id] = (Corners, [.. Corners.Select(x.Apply)]);
        g.Moved[id] = after;
    }

    [Fact]
    public void WithoutAServiceEveryHashDifferenceStaysUnclassified()
    {
        var (a, b, _) = Edited(2);
        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.All(r.Geometry, g => Assert.Equal(GeometryLevel.Unclassified, g.Level));
        Assert.Equal(new PropertyChange(PropertyKind.Geometry, null, null, null), Assert.Single(r.ChangeSets).Change);
    }

    [Fact]
    public void SameShapeIsDataOnly_ListedButNeverAChange()
    {
        var (a, b, ids) = Edited(1);
        var g = new FakeGeometry();
        g.Unmoved[ids[0]] = 0.004;
        var r = ModelDiff.Diff(a.Build(), b.Build(), geometry: g);
        var change = Assert.Single(r.Geometry);
        Assert.Equal((GeometryLevel.DataOnly, "rebuilt", 0.004, 0.002), (change.Level, change.Detail, change.MaxDeviation, change.MeanDeviation));
        Assert.Empty(r.ChangeSets);
        Assert.Equal(1, r.UnchangedObjects);
    }

    [Fact]
    public void ShapeToleranceDecidesWhatIsTheSameShape()
    {
        var (a, b, ids) = Edited(1);
        var g = new FakeGeometry();
        g.Unmoved[ids[0]] = 0.05;
        Assert.Equal(GeometryLevel.Unclassified, Assert.Single(ModelDiff.Diff(a.Build(), b.Build(), geometry: g).Geometry).Level);
        var loose = new DiffSettings { ShapeTolerance = 0.1 };
        Assert.Equal(GeometryLevel.DataOnly, Assert.Single(ModelDiff.Diff(a.Build(), b.Build(), loose, g).Geometry).Level);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { ShapeTolerance = 0 }));
    }

    [Fact]
    public void EqualMovesOfGeometryAndInstancesAreOnePlacementSet()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var curves = a.Many(layer, 3);
        var block = a.Block("Column");
        var instances = Enumerable.Range(0, 2).Select(i => a.Instance(layer, block, Xform.Translation(i * 1000, 500, 0))).ToList();
        var b = a.Clone();
        var g = new FakeGeometry();
        foreach (var id in curves)
        {
            b.Edit(id, o => o with { Geometry = o.Geometry with { Hash = o.Geometry.Hash + 1 } });
            MoveBy(g, id, new Vec3(120, 0, 0));
        }
        foreach (var id in instances)
            b.Edit(id, o => o with { InstanceXform = Xform.Translation(120, 0, 0).After(o.InstanceXform!) });

        var r = ModelDiff.Diff(a.Build(), b.Build(), geometry: g);
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal(new PropertyChange(PropertyKind.Transform, null, null, "moved 120 mm in X"), set.Change);
        Assert.Equal(5, set.Objects.Count);
        Assert.All(r.Geometry, x => Assert.Equal((GeometryLevel.Moved, "moved 120 mm in X"), (x.Level, x.Detail)));
        Assert.True(r.Geometry[0].Rigid!.AlmostEquals(Xform.Translation(120, 0, 0), 1e-9));
    }

    [Fact]
    public void AFitIsRejectedWhenTheMovedShapeStillDeviates()
    {
        var (a, b, ids) = Edited(3);
        var g = new FakeGeometry();
        MoveBy(g, ids[0], new Vec3(0, 0, 10), after: 0.5);   // the points moved, the shape didn't follow
        MoveBy(g, ids[1], new Vec3(0, 0, 0));                 // nothing moved: no fit to report
        var r = ModelDiff.Diff(a.Build(), b.Build(), geometry: g);
        Assert.All(r.Geometry, x => Assert.Equal(GeometryLevel.Unclassified, x.Level));
        Assert.Equal(3, Assert.Single(r.ChangeSets).Objects.Count);
    }

    [Fact]
    public void PlacementToggleOffIgnoresMoves()
    {
        var (a, b, ids) = Edited(1);
        var g = new FakeGeometry();
        MoveBy(g, ids[0], new Vec3(120, 0, 0));
        var r = ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { Toggles = new PropertyToggles { Placement = false } }, g);
        Assert.Equal(GeometryLevel.Moved, Assert.Single(r.Geometry).Level);
        Assert.Empty(r.ChangeSets);
    }

    [Fact]
    public void InstanceRotationIsADeltaSoDifferentPlacesShareTheSet()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var block = a.Block("Chair");
        var ids = Enumerable.Range(0, 3).Select(i => a.Instance(layer, block, Xform.Translation(i * 700, 0, 0))).ToList();
        var b = a.Clone();
        var turn = new Xform([0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);
        foreach (var id in ids) b.Edit(id, o => o with { InstanceXform = turn.After(o.InstanceXform!) });

        var set = Assert.Single(ModelDiff.Diff(a.Build(), b.Build()).ChangeSets);
        Assert.Equal("rotated 90° about Z", set.Change!.To);
        Assert.Equal(3, set.Objects.Count);
    }

    [Fact]
    public void ADiagonalMoveCountsItsFullLength()
    {
        var (a, b, ids) = Edited(1);
        var g = new FakeGeometry();
        MoveBy(g, ids[0], new Vec3(0.009, 0.009, 0));   // each component below 0.01, the length above
        var change = Assert.Single(ModelDiff.Diff(a.Build(), b.Build(), geometry: g).Geometry);
        Assert.Equal(GeometryLevel.Moved, change.Level);
    }

    [Fact]
    public void EqualHashesAreRecheckedWhenShapeToleranceIsFinerThanTheGrid()
    {
        var a = new SnapshotBuilder();
        var ids = a.Many(a.Layer("L"), 2);
        var b = a.Clone();                       // same hashes on the 0.01 grid
        var g = new FakeGeometry();
        MoveBy(g, ids[0], new Vec3(0.004, 0.004, 0));
        g.Unmoved[ids[1]] = 0;
        var fine = new DiffSettings { ShapeTolerance = 0.001 };

        var r = ModelDiff.Diff(a.Build(), b.Build(), fine, g);
        var moved = Assert.Single(r.Geometry);   // the unmoved pair is unchanged, not listed as Data-only
        Assert.Equal((ids[0], GeometryLevel.Moved), (moved.A, moved.Level));
        Assert.Contains(r.Header.Notes, n => n.Contains("also compared by shape"));

        var withoutService = ModelDiff.Diff(a.Build(), b.Build(), fine);
        Assert.Empty(withoutService.Geometry);
        Assert.Contains(withoutService.Header.Notes, n => n.Contains("smaller differences are not reported"));
        Assert.DoesNotContain(ModelDiff.Diff(a.Build(), b.Build(), geometry: g).Header.Notes, n => n.Contains("hash grid"));
    }
}
