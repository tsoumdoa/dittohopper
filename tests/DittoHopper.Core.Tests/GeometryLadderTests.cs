using Xunit;

namespace DittoHopper.Core.Tests;

/// <summary>The geometry ladder through a scripted geometry service: what the Rhino adapter would answer.</summary>
public class GeometryLadderTests
{
    /// <summary>Answers by A id: the unmoved deviation, the points, the deviation once the fitted move is applied, and step 4's arrays.</summary>
    private sealed class FakeGeometry : IGeometryService
    {
        public readonly Dictionary<Guid, double> Unmoved = [], Moved = [];
        public readonly Dictionary<Guid, (Vec3[] A, Vec3[] B)> Points = [];
        public readonly Dictionary<Guid, StructureArrays> Arrays = [];

        public ShapeDeviation? Deviation(Guid a, Guid b, Xform? moveA, double tolerance) =>
            (moveA is null ? Unmoved : Moved).TryGetValue(a, out var d) ? new ShapeDeviation(d, d / 2) : null;

        public PointCorrespondence? CorrespondingPoints(Guid a, Guid b) =>
            Points.TryGetValue(a, out var p) ? new PointCorrespondence(p.A, p.B) : null;

        public string? DataOnlyReason(Guid a, Guid b) => "rebuilt";

        public StructureArrays? Structure(Guid a, Guid b) => Arrays.GetValueOrDefault(a);
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
        // Not the same shape at the default tolerance: same topology, so step 5 reports it Reshaped.
        Assert.Equal(GeometryLevel.Reshaped, Assert.Single(ModelDiff.Diff(a.Build(), b.Build(), geometry: g).Geometry).Level);
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

    [Fact]
    public void TheSameShapeInAnotherRepresentationIsDataOnly_AndStillMatchesByHash()
    {
        var a = new SnapshotBuilder();
        var line = a.Object(a.Layer("L"));
        var b = a.Clone();
        var fresh = b.Rebake(line);                // a new id: only pass 3 can pair it
        b.Edit(fresh, o => o with { Geometry = o.Geometry with { Representation = 42 } });

        var noService = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Equal(MatchedBy.GeometryHash, Assert.Single(noService.Matches).By);
        var change = Assert.Single(noService.Geometry);
        Assert.Equal((GeometryLevel.DataOnly, "representation changed"), (change.Level, change.Detail));
        Assert.Empty(noService.ChangeSets);

        var withService = ModelDiff.Diff(a.Build(), b.Build(), geometry: new FakeGeometry());
        Assert.Equal("rebuilt", Assert.Single(withService.Geometry).Detail);
        Assert.Empty(withService.ChangeSets);
    }

    private static Vec3[] Hexagon() => [.. Enumerable.Range(0, 6).Select(i => new Vec3(100 * Math.Cos(i * Math.PI / 3), 100 * Math.Sin(i * Math.PI / 3), i * 7))];

    private static Vec3[] With(Vec3[] points, int index, Vec3 offset)
    {
        var copy = (Vec3[])points.Clone();
        copy[index] += offset;
        return copy;
    }

    [Fact]
    public void Step4_SameStructureListsWhichPointsMovedAndHowFar()
    {
        var (a, b, ids) = Edited(1);
        var g = new FakeGeometry();
        g.Arrays[ids[0]] = new StructureArrays(Corners, With(Corners, 2, new Vec3(0, 0, 50)), true, false, PointOrder.Fixed);

        var r = ModelDiff.Diff(a.Build(), b.Build(), geometry: g);
        var change = Assert.Single(r.Geometry);
        Assert.Equal((GeometryLevel.Edited, "1 of 4 control points moved: #2 by 50 mm", 50.0), (change.Level, change.Detail, change.MaxDeviation));
        Assert.Equal([2], change.MovedIndices);
        Assert.Null(change.Rigid);
        Assert.Equal(new PropertyChange(PropertyKind.Geometry, null, null, "edited"), Assert.Single(r.ChangeSets).Change);
    }

    [Fact]
    public void Step4_AMovedSeamAndAFlipAreUndoneBeforeComparingIndices()
    {
        var (a, b, ids) = Edited(1);
        var ring = Hexagon();
        var edited = With(ring, 3, new Vec3(0, 0, 5));
        // Stored starting two points later and running the other way.
        var stored = Enumerable.Range(0, 6).Select(j => edited[(j - 2 + 6) % 6]).Reverse().ToArray();
        var g = new FakeGeometry();
        g.Arrays[ids[0]] = new StructureArrays(ring, stored, true, false, PointOrder.Closed);

        var change = Assert.Single(ModelDiff.Diff(a.Build(), b.Build(), geometry: g).Geometry);
        Assert.Equal("seam moved, direction flipped; 1 of 6 control points moved: #3 by 5 mm", change.Detail);
        Assert.Equal([3], change.MovedIndices);
    }

    [Fact]
    public void Step4_MovedAndEdited_TheRigidFitComesFirst()
    {
        var (a, b, ids) = Edited(1);
        var ring = Hexagon();
        var moved = With([.. ring.Select(p => p + new Vec3(120, 0, 0))], 3, new Vec3(0, 0, 30));
        var g = new FakeGeometry();
        g.Arrays[ids[0]] = new StructureArrays(ring, moved, true, false, PointOrder.Fixed);

        var r = ModelDiff.Diff(a.Build(), b.Build(), geometry: g);
        var change = Assert.Single(r.Geometry);
        Assert.Equal((GeometryLevel.Edited, "moved 120 mm in X, then 1 of 6 control points moved: #3 by 30 mm"), (change.Level, change.Detail));
        Assert.True(change.Rigid!.AlmostEquals(Xform.Translation(120, 0, 0), 1e-9));
        Assert.Equal([3], change.MovedIndices);
        // The rigid part is a placement change, so it shares a set with equal moves; the residual is a shape change.
        Assert.Equal(["edited", "moved 120 mm in X"], r.ChangeSets.Select(s => s.Change!.To!).Order(StringComparer.Ordinal));
        // With Shape off the move still reports: the toggle hides only the residual edit.
        var placementOnly = ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { Toggles = new PropertyToggles { Shape = false } }, g);
        Assert.Equal("moved 120 mm in X", Assert.Single(placementOnly.ChangeSets).Change!.To);
        Assert.Equal(0, placementOnly.UnchangedObjects);
    }

    [Fact]
    public void Step4_PolylinesWithDifferentVertexCountsGetASequenceDiff()
    {
        var (a, b, ids) = Edited(1);
        Vec3[] before = [new(0, 0, 0), new(100, 0, 0), new(200, 0, 0), new(300, 0, 0), new(400, 0, 0)];
        Vec3[] after = [new(0, 0, 0), new(100, 0, 0), new(150, 50, 0), new(200, 0, 0), new(300, 20, 0), new(400, 0, 0)];
        var g = new FakeGeometry();
        g.Arrays[ids[0]] = new StructureArrays(before, after, false, true, PointOrder.Open);

        var change = Assert.Single(ModelDiff.Diff(a.Build(), b.Build(), geometry: g).Geometry);
        Assert.Equal((GeometryLevel.Edited, "vertices: 1 inserted, 1 moved (#3 by 20 mm), 4 untouched", 20.0),
            (change.Level, change.Detail, change.MaxDeviation));
        Assert.Equal([3], change.MovedIndices);
    }

    [Fact]
    public void Step5_TheSameTopologyIsReshapedWithItsDeviation()
    {
        var (a, b, ids) = Edited(1);
        var g = new FakeGeometry();
        g.Unmoved[ids[0]] = 12.5;

        var r = ModelDiff.Diff(a.Build(), b.Build(), geometry: g);
        var change = Assert.Single(r.Geometry);
        Assert.Equal((GeometryLevel.Reshaped, "deviation max 12.5 mm, mean 6.25 mm", 12.5, 6.25),
            (change.Level, change.Detail, change.MaxDeviation, change.MeanDeviation));
        Assert.Equal("reshaped", Assert.Single(r.ChangeSets).Change!.To);
    }

    [Fact]
    public void Step5_OtherwiseTheTopologyChangedAndSaysWhatBroke()
    {
        var a = new SnapshotBuilder();
        var box = a.Object(a.Layer("Solids"), o => o with { Kind = ObjectKind.Brep, Geometry = o.Geometry with { Closed = true, Faces = 6 } });
        var b = a.Clone();
        b.Edit(box, o => o with { Geometry = o.Geometry with { Hash = 1, Closed = false, Faces = 5, BoundaryLoops = 1 } });

        var r = ModelDiff.Diff(a.Build(), b.Build(), geometry: new FakeGeometry());
        var change = Assert.Single(r.Geometry);
        Assert.Equal((GeometryLevel.TopologyChanged, "solid became open, face removed or merged"), (change.Level, change.Detail));
        Assert.Equal("topology changed: solid became open, face removed or merged", Assert.Single(r.ChangeSets).Change!.To);
    }

    [Fact]
    public void Step4_ATopologyFlipIsNeverAnEdit()
    {
        // An open polyline closed by moving its last vertex onto its first; then two mesh pieces joined into one.
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var curve = a.Object(layer);
        var mesh = a.Object(layer, o => o with { Kind = ObjectKind.Mesh, Geometry = o.Geometry with { Closed = true, Faces = 8, Components = 2 } });
        var b = a.Clone();
        b.Edit(curve, o => o with { Geometry = o.Geometry with { Hash = 1, Closed = true } });
        b.Edit(mesh, o => o with { Geometry = o.Geometry with { Hash = 2, Components = 1 } });
        Vec3[] open = [new(0, 0, 0), new(100, 0, 0), new(100, 100, 0), new(0, 100, 0)];
        var g = new FakeGeometry();
        g.Arrays[curve] = new StructureArrays(open, [open[0], open[1], open[2], open[0]], true, true, PointOrder.Fixed);
        g.Unmoved[mesh] = 30;

        var r = ModelDiff.Diff(a.Build(), b.Build(), geometry: g);
        Assert.Equal([(GeometryLevel.TopologyChanged, "open curve became closed"), (GeometryLevel.TopologyChanged, "pieces joined")],
            r.Geometry.OrderBy(x => x.A).Select(x => (x.Level, x.Detail!)));
    }

    [Fact]
    public void Step4_AReversedMoveIsFittedTheRightWayRound_AndATieAbstains()
    {
        var (a, b, ids) = Edited(1);
        Vec3[] start = [new(0, 0, 0), new(100, 0, 0), new(100, 100, 0), new(0, 100, 0), new(10, 10, 0)];
        var end = start.Select(p => p + new Vec3(500, 0, 0)).ToArray();
        end[4] += new Vec3(0, 10, 0);
        var g = new FakeGeometry();
        g.Arrays[ids[0]] = new StructureArrays(start, [.. Enumerable.Reverse(end)], true, true, PointOrder.Open);
        var change = Assert.Single(ModelDiff.Diff(a.Build(), b.Build(), geometry: g).Geometry);
        Assert.Equal("direction flipped; moved 500 mm in X, then 1 of 5 vertices moved: #4 by 10 mm", change.Detail);

        // A Z shape is its own reverse rotated 180°: both ways round explain three of four points, differently.
        Vec3[] z = [new(0, 0, 0), new(100, 0, 0), new(0, 100, 0), new(100, 100, 0)];
        var zEnd = z.Select(p => p + new Vec3(500, 0, 0)).ToArray();
        zEnd[1] += new Vec3(0, 0, 10);
        g.Arrays[ids[0]] = new StructureArrays(z, zEnd, true, true, PointOrder.Open);
        g.Unmoved[ids[0]] = 600;
        Assert.Equal(GeometryLevel.Reshaped, Assert.Single(ModelDiff.Diff(a.Build(), b.Build(), geometry: g).Geometry).Level);
    }
}
