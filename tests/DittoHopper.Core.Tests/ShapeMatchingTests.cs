using Xunit;

namespace DittoHopper.Core.Tests;

/// <summary>Kind families, pass 4 (shape signature + nearest) and pass 5 (fuzzy score). Every test states the full pairing.</summary>
public class ShapeMatchingTests
{
    private static void AssertPairs(DiffResult r, params (Guid A, Guid B, MatchedBy By)[] expected) =>
        Assert.Equal(expected.OrderBy(p => p.A), r.Matches.Select(m => (m.A, m.B, m.By)));

    /// <summary>B's copy of the object, given a new id (as a re-bake does), then edited.</summary>
    private static Guid Rebaked(SnapshotBuilder b, Guid id, Func<ObjectRecord, ObjectRecord> edit)
    {
        var fresh = b.Rebake(id);
        b.Edit(fresh, o => edit(o) with { Geometry = edit(o).Geometry with { Hash = (ulong)fresh.GetHashCode() } });
        return fresh;
    }

    private static Func<ObjectRecord, ObjectRecord> MovedBy(double dx) => o => o with
    {
        Geometry = SnapshotBuilder.SolidShape(0, o.Geometry.Centroid + new Vec3(dx, 0, 0), o.Geometry.BBox.Max.X - o.Geometry.BBox.Min.X),
    };

    [Fact]
    public void Pass2_ExtrusionAndBrepAreOneFamily()
    {
        var a = new SnapshotBuilder();
        var column = a.Solid(a.Layer("Columns"), new Vec3(), kind: ObjectKind.Extrusion);
        var b = a.Clone();
        b.Edit(column, o => o with { Kind = ObjectKind.Brep, Geometry = o.Geometry with { Hash = 7 } });

        AssertPairs(ModelDiff.Diff(a.Build(), b.Build()), (column, column, MatchedBy.Guid));
    }

    [Fact]
    public void Pass4_AMovedObjectWithANewIdPairsByShapeThenNearest()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var ids = Enumerable.Range(0, 3).Select(i => a.Solid(layer, new Vec3(i * 1000, 0, 0))).ToList();
        var b = a.Clone();
        var moved = Rebaked(b, ids[1], MovedBy(50));

        var r = ModelDiff.Diff(a.Build(), b.Build());
        AssertPairs(r, (ids[0], ids[0], MatchedBy.Guid), (ids[1], moved, MatchedBy.ShapeSignature), (ids[2], ids[2], MatchedBy.Guid));
        Assert.Equal(0.9, r.Matches.Single(m => m.B == moved).Confidence);
    }

    [Fact]
    public void Pass4_ReBakedCopiesJitteredAcrossTheHashGridPairInPlace()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("Panels");
        var ids = Enumerable.Range(0, 10).Select(i => a.Solid(layer, new Vec3(i * 200, 0, 0))).ToList();
        var b = a.Clone();
        // Reverse order, so new GUIDs don't line up with the old ones by accident.
        var fresh = Enumerable.Reverse(ids).Select(id => (id, Rebaked(b, id, MovedBy(0.004)))).ToList();

        AssertPairs(ModelDiff.Diff(a.Build(), b.Build()), [.. fresh.Select(p => (p.id, p.Item2, MatchedBy.ShapeSignature))]);
    }

    [Fact]
    public void Pass4_IdenticalNeighboursCloserThanTwiceTheMoveStayUnpaired()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var left = a.Solid(layer, new Vec3(0, 0, 0));
        var right = a.Solid(layer, new Vec3(150, 0, 0));
        var b = a.Clone();
        Rebaked(b, left, MovedBy(60));
        Rebaked(b, right, MovedBy(60));

        // Each B object's nearest A is 60 away, but the other A is only 90 away: which is which can't be told.
        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Empty(r.Matches);
        Assert.Equal([ChangeKind.Removed, ChangeKind.Added], r.ChangeSets.Select(s => s.Kind));
    }

    [Fact]
    public void Pass4_NeverPairsAcrossMoreThanTheObjectsOwnSize()
    {
        var a = new SnapshotBuilder();
        var only = a.Solid(a.Layer("L"), new Vec3());
        var b = a.Clone();
        Rebaked(b, only, MovedBy(500));   // diagonal ≈ 173

        Assert.Empty(ModelDiff.Diff(a.Build(), b.Build()).Matches);
    }

    [Fact]
    public void Pass4_AnExtrusionRebuiltAsABrepWithANewIdPairsWithinItsFamily()
    {
        var a = new SnapshotBuilder();
        var column = a.Solid(a.Layer("Columns"), new Vec3(), kind: ObjectKind.Extrusion);
        var b = a.Clone();
        var rebuilt = Rebaked(b, column, o => o with { Kind = ObjectKind.Brep });

        AssertPairs(ModelDiff.Diff(a.Build(), b.Build()), (column, rebuilt, MatchedBy.ShapeSignature));
    }

    [Fact]
    public void Pass4_NeedsTheSameTopology_OtherwiseOnlyAFuzzyPairThatNeedsReview()
    {
        var a = new SnapshotBuilder();
        var box = a.Solid(a.Layer("L"), new Vec3());
        var b = a.Clone();
        var opened = Rebaked(b, box, o => o with { Geometry = o.Geometry with { Closed = false } });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        AssertPairs(r, (box, opened, MatchedBy.Fuzzy));
        Assert.True(r.Matches[0].NeedsReview);
    }

    [Fact]
    public void Pass4_InstancesPairByTheirDefinitionThroughTheBlockMap()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var chair = a.Block("Chair");
        var desk = a.Block("Desk");
        var instance = a.Instance(layer, chair);
        a.Edit(instance, o => o with { Geometry = SnapshotBuilder.SolidShape(o.Geometry.Hash, new Vec3(), 500) with { Area = 0, Volume = 0 } });
        var b = a.Clone();
        var moved = Rebaked(b, instance, o => o with { Geometry = o.Geometry with { Centroid = new Vec3(100, 0, 0) } });
        var other = b.Instance(layer, desk);
        b.Edit(other, o => o with { Geometry = o.Geometry with { Centroid = new Vec3(10, 0, 0), BBox = a.Objects[0].Geometry.BBox } });

        AssertPairs(ModelDiff.Diff(a.Build(), b.Build()), (instance, moved, MatchedBy.ShapeSignature));
    }

    [Fact]
    public void Pass5_AnEditedObjectWithANewIdIsAFuzzyPairThatNeedsALook()
    {
        var a = new SnapshotBuilder();
        var box = a.Solid(a.Layer("L"), new Vec3());
        var b = a.Clone();
        var grown = Rebaked(b, box, o => o with { Geometry = SnapshotBuilder.SolidShape(0, new Vec3(5, 0, 0), 110) });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        AssertPairs(r, (box, grown, MatchedBy.Fuzzy));
        // Kind 0.15 + overlap 0.35 × 0.751 + size 0.25 × 0.751 + layer 0.15 + no names 0.05.
        Assert.Equal(0.801, r.Matches[0].Confidence);
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal([Detectors.FuzzyMatch], set.Flags);
        Assert.True(set.NeedsLook);
    }

    [Fact]
    public void Pass5_TheThresholdIsASetting()
    {
        var a = new SnapshotBuilder();
        var box = a.Solid(a.Layer("L"), new Vec3());
        var b = a.Clone();
        Rebaked(b, box, o => o with { Geometry = SnapshotBuilder.SolidShape(0, new Vec3(5, 0, 0), 110) });

        Assert.Empty(ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { FuzzyThreshold = 0.9 }).Matches);
        Assert.Throws<ArgumentOutOfRangeException>(() => ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { FuzzyThreshold = 0 }));
    }

    [Fact]
    public void Pass5_NeedsOverlappingBoxesAndNeverPairsDifferentStableKeys()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var far = a.Solid(layer, new Vec3());
        var keyed = a.Solid(layer, new Vec3(5000, 0, 0), edit: o => o with { UserText = new Dictionary<string, string> { ["diff:uid"] = "k1" } });
        var b = a.Clone();
        Rebaked(b, far, o => o with { Geometry = SnapshotBuilder.SolidShape(0, new Vec3(300, 0, 0), 110) });
        Rebaked(b, keyed, o => o with
        {
            UserText = new Dictionary<string, string> { ["diff:uid"] = "k2" },
            Geometry = SnapshotBuilder.SolidShape(0, new Vec3(5005, 0, 0), 110),
        });

        Assert.Empty(ModelDiff.Diff(a.Build(), b.Build()).Matches);
    }
}
