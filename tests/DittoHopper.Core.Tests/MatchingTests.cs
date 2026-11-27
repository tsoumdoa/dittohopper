using Xunit;

namespace DittoHopper.Core.Tests;

/// <summary>Each certain pass must make zero wrong pairings: every test states the full expected pairing.</summary>
public class MatchingTests
{
    private static Dictionary<string, string> Uid(string v) => new() { ["diff:uid"] = v };

    private static void AssertPairs(DiffResult r, MatchedBy by, params (Guid A, Guid B)[] expected)
    {
        Assert.Equal(expected.OrderBy(p => p.A), r.Matches.Select(m => (m.A, m.B)));
        Assert.All(r.Matches, m => Assert.Equal(by, m.By));
    }

    [Fact]
    public void Pass1_StableKeySurvivesNewIdsAndEditedGeometry()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var x = a.Object(layer, o => o with { UserText = Uid("x") });
        var y = a.Object(layer, o => o with { UserText = Uid("y") });
        var b = a.Clone();
        var y2 = b.Rebake(y);
        var x2 = b.Rebake(x);
        b.Edit(x2, o => o with { Geometry = o.Geometry with { Hash = 1 } });
        b.Edit(y2, o => o with { Geometry = o.Geometry with { Hash = 2 } });

        AssertPairs(ModelDiff.Diff(a.Build(), b.Build()), MatchedBy.StableKey, (x, x2), (y, y2));
    }

    [Fact]
    public void Pass1_NamesCountOnlyWhenUniqueInBothModels_AndNeverOverrideDifferentKeys()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var c1 = a.Object(layer, o => o with { Name = "C-1" });
        var dup1 = a.Object(layer, o => o with { Name = "dup" });
        var dup2 = a.Object(layer, o => o with { Name = "dup" });
        var keyed = a.Object(layer, o => o with { Name = "K", UserText = Uid("k1") });
        var b = a.Clone();
        foreach (var id in new[] { c1, dup1, dup2, keyed })
        {
            var fresh = b.Rebake(id);
            b.Edit(fresh, o => o with { Geometry = o.Geometry with { Hash = (ulong)fresh.GetHashCode() } });
        }
        var c1b = b.Objects.Single(o => o.Name == "C-1").Id;
        b.Edit(b.Objects.Single(o => o.Name == "K").Id, o => o with { UserText = Uid("k2") });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        AssertPairs(r, MatchedBy.StableKey, (c1, c1b));
        Assert.Equal(3, r.ChangeSets.Single(s => s.Kind == ChangeKind.Removed).Objects.Count);
    }

    [Fact]
    public void Pass1_DuplicatedKeyBreaksTiesBySameLayerThenNearestCentroid()
    {
        var a = new SnapshotBuilder();
        var l1 = a.Layer("L1");
        var l2 = a.Layer("L2");
        var x = a.Object(l1, o => o with { UserText = Uid("x") }, at: new Vec3(0, 0, 0));
        var b = a.Clone();
        // Pasted copies with the same key: one near but on another layer, one far on the same layer, one nearer still on the same layer.
        var nearOtherLayer = b.Object(l2, o => o with { UserText = Uid("x") }, at: new Vec3(1, 0, 0));
        var farSameLayer = b.Object(l1, o => o with { UserText = Uid("x") }, at: new Vec3(900, 0, 0));
        b.RemoveObject(x);
        var nearSameLayer = b.Object(l1, o => o with { UserText = Uid("x") }, at: new Vec3(50, 0, 0));

        var r = ModelDiff.Diff(a.Build(), b.Build());
        AssertPairs(r, MatchedBy.StableKey, (x, nearSameLayer));
        Assert.Equal([nearOtherLayer, farSameLayer], r.ChangeSets.Where(s => s.Kind == ChangeKind.Added)
            .SelectMany(s => s.Objects).Select(p => p.B!.Value).Order());
    }

    [Fact]
    public void Pass2_SameGuidNeedsTheSameKind()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var kept = a.Object(layer);
        var changedKind = a.Object(layer);
        var b = a.Clone();
        b.Edit(kept, o => o with { Geometry = o.Geometry with { Hash = 7 } });
        b.Edit(changedKind, o => o with { Kind = ObjectKind.Brep, Geometry = o.Geometry with { Hash = 8 } });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        AssertPairs(r, MatchedBy.Guid, (kept, kept));
        Assert.Equal([ChangeKind.Modified, ChangeKind.Removed, ChangeKind.Added], r.ChangeSets.Select(s => s.Kind));
        Assert.Equal(PropertyKind.Geometry, r.ChangeSets[0].Change!.Property);
        Assert.Equal(GeometryLevel.Unclassified, Assert.Single(r.Geometry).Level);
    }

    [Fact]
    public void Pass3_IdenticalCopiesPairBySameMappedLayerThenGuidOrder()
    {
        var a = new SnapshotBuilder();
        var l1 = a.Layer("L1");
        var l2 = a.Layer("L2");
        var onL1 = a.Object(l1, hash: 42, at: new Vec3());
        var onL2 = a.Object(l2, hash: 42, at: new Vec3());
        var twin1 = a.Object(l1, hash: 43, at: new Vec3());
        var twin2 = a.Object(l1, hash: 43, at: new Vec3());
        var b = a.Clone();
        // Re-bake in reverse order so the new GUIDs don't line up with the old ones by accident.
        var twin2b = b.Rebake(twin2);
        var twin1b = b.Rebake(twin1);
        var onL2b = b.Rebake(onL2);
        var onL1b = b.Rebake(onL1);

        var r = ModelDiff.Diff(a.Build(), b.Build());
        // Same-layer wins for 42. The 43 twins are fully tied, so the lower A id takes the lower B id.
        AssertPairs(r, MatchedBy.GeometryHash, (onL1, onL1b), (onL2, onL2b), (twin1, twin2b), (twin2, twin1b));
        Assert.Empty(r.ChangeSets);
    }

    [Fact]
    public void Pass3_IsSkippedWhenHashGridsDiffer()
    {
        var (a, b) = Fixtures.GhRebake();
        b.Info = b.Info with { HashGrid = 0.001 };

        var r = Fixtures.Diff((a, b));
        Assert.Empty(r.Matches);
        Assert.False(r.Header.HashGridsMatch);
        Assert.Contains(r.Header.Notes, n => n.StartsWith("Hash grids differ"));
    }

    [Fact]
    public void DifferentHashGrids_LeaveEveryPairsGeometryUnclassified()
    {
        var (a, b) = Fixtures.LayerRename();
        b.Info = b.Info with { HashGrid = 0.001 };

        var r = Fixtures.Diff((a, b));
        Assert.Equal(4, r.Geometry.Count);
        Assert.All(r.Geometry, g => Assert.Equal(GeometryLevel.Unclassified, g.Level));
    }

    [Fact]
    public void Pass1_ACopyKeepsTheKeyButTheOriginalKeepsItsGuid()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var g = a.Object(layer, o => o with { UserText = Uid("x") }, at: new Vec3(0, 0, 0));
        var b = a.Clone();
        b.Edit(g, o => o with { Geometry = o.Geometry with { Hash = 1, Centroid = new Vec3(10, 0, 0) } });
        // Rhino's Copy keeps user text: the copy sits where the original was.
        var copy = b.Object(layer, o => o with { UserText = Uid("x") }, hash: a.Objects[0].Geometry.Hash, at: new Vec3(0, 0, 0));

        var r = ModelDiff.Diff(a.Build(), b.Build());
        AssertPairs(r, MatchedBy.StableKey, (g, g));
        Assert.Equal(copy, r.ChangeSets.Single(s => s.Kind == ChangeKind.Added).Objects.Single().B);
    }

    [Fact]
    public void Pass3_NeverPairsObjectsWithDifferentStableKeys()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var first = a.Object(layer, o => o with { UserText = Uid("first") }, hash: 5);
        var b = a.Clone();
        b.RemoveObject(first);
        b.Object(layer, o => o with { UserText = Uid("second") }, hash: 5);

        Assert.Empty(ModelDiff.Diff(a.Build(), b.Build()).Matches);
    }

    [Fact]
    public void Pass3_CoincidentCopiesPairByOwnAttributesBeforeGuidOrder()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var red = a.Object(layer, o => o with { Color = new Sourced<int>(Fixtures.Red, ValueSource.ByObject) }, hash: 9, at: new Vec3());
        var blue = a.Object(layer, o => o with { Color = new Sourced<int>(unchecked((int)0xFF0000FF), ValueSource.ByObject) }, hash: 9, at: new Vec3());
        var b = a.Clone();
        // Re-baked blue first, so GUID order alone would cross them.
        var blue2 = b.Rebake(blue);
        var red2 = b.Rebake(red);

        var r = ModelDiff.Diff(a.Build(), b.Build());
        AssertPairs(r, MatchedBy.GeometryHash, (red, red2), (blue, blue2));
        Assert.Empty(r.ChangeSets);
    }

    [Fact]
    public void HashGridsCompareAsLengths()
    {
        var (a, b) = Fixtures.GhRebake();
        a.Info = a.Info with { HashGrid = 1 };
        b.Info = b.Info with { Units = UnitSystem.Meters, AbsoluteTolerance = 0.00001, HashGrid = 0.001 };

        var r = Fixtures.Diff((a, b));
        Assert.True(r.Header.HashGridsMatch);
        Assert.All(r.Matches, m => Assert.Equal(MatchedBy.GeometryHash, m.By));
        Assert.Equal(4, r.Matches.Count);
    }
}
