using Xunit;

namespace DittoHopper.Core.Tests;

public class CompareTests
{
    [Fact]
    public void AppearanceComparesValueAndSource()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var toOverride = a.Object(layer);
        var staleOwnValue = a.Object(layer, o => o with { Color = new Sourced<int>(1, ValueSource.ByLayer) });
        var b = a.Clone();
        b.Edit(toOverride, o => o with { Color = new Sourced<int>(Fixtures.Red, ValueSource.ByObject) });
        // The object's own color changed while it still follows its layer: nothing visible changed.
        b.Edit(staleOwnValue, o => o with { Color = new Sourced<int>(2, ValueSource.ByLayer) });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal(new PropertyChange(PropertyKind.Color, null, null, "#FF0000", ValueSource.ByLayer, ValueSource.ByObject), set.Change);
        Assert.Equal([new ObjectPair(toOverride, toOverride)], set.Objects);
    }

    [Fact]
    public void ToggledOffPropertiesAreNeitherChangedNorCounted()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var o1 = a.Object(layer, o => o with { PrintWidth = new Sourced<double>(0.5, ValueSource.ByObject) });
        var b = a.Clone();
        b.Edit(o1, o => o with
        {
            PrintWidth = new Sourced<double>(0.7, ValueSource.ByObject),
            Color = new Sourced<int>(Fixtures.Red, ValueSource.ByObject),
        });

        var byDefault = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Equal([PropertyKind.Color], byDefault.ChangeSets.Select(s => s.Change!.Property));

        var settings = new DiffSettings { Toggles = new PropertyToggles { PrintWidth = true, Color = false } };
        var toggled = ModelDiff.Diff(a.Build(), b.Build(), settings);
        Assert.Equal([PropertyKind.PrintWidth], toggled.ChangeSets.Select(s => s.Change!.Property));
        Assert.Equal(byDefault.Matches[0].UnchangedProperties, toggled.Matches[0].UnchangedProperties);
    }

    [Fact]
    public void UnchangedPropertiesAreCountedPerPair()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var o1 = a.Object(layer, o => o with { UserText = new Dictionary<string, string> { ["mark"] = "C-12", ["fire"] = "R60" } });
        var b = a.Clone();
        b.Edit(o1, o => o with { Name = "W-042" });

        var m = Assert.Single(ModelDiff.Diff(a.Build(), b.Build()).Matches);
        // layer, color, material, linetype, 2 user-text keys, groups, geometry; name changed.
        Assert.Equal(8, m.UnchangedProperties);
    }

    [Fact]
    public void BlockDefinitionEditIsOneRow_InstanceGetsItsOwnRowOnlyForItsOwnChange()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("Windows");
        var window = a.Block("Window-A", content: 1);
        var instances = Enumerable.Range(0, 48).Select(i => a.Instance(layer, window, Xform.Translation(i * 900, 0, 0))).ToList();
        var b = a.Clone();
        b.EditBlock(window, d => d with { ContentHash = 2 });
        b.Edit(instances[5], o => o with { InstanceXform = Xform.Translation(5 * 900, 120, 0) });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        var e = Assert.Single(r.TableEvents);
        Assert.Equal((TableEventKind.PropertyChanged, PropertyKind.Content, "Window-A", 48), (e.Kind, e.Property, e.Label, e.AffectedObjects));
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal(PropertyKind.Transform, set.Change!.Property);
        Assert.Equal([new ObjectPair(instances[5], instances[5])], set.Objects);
    }

    [Fact]
    public void UngroupingIsOneSetLinkedToTheGroupRemoval_ReGroupingMatchesByMembers()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var panels = a.Group("Panels");
        var bolts = a.Group();
        var inPanels = a.Many(layer, 3, o => o with { GroupIds = [panels] });
        var inBolts = a.Many(layer, 4, o => o with { GroupIds = [bolts] });
        var b = a.Clone();
        b.Groups.RemoveAll(g => g.Id == panels);
        foreach (var id in inPanels) b.Edit(id, o => o with { GroupIds = [] });
        // Bolts is grouped again: a new group id with the same members.
        b.Groups.RemoveAll(g => g.Id == bolts);
        var bolts2 = b.Group();
        foreach (var id in inBolts) b.Edit(id, o => o with { GroupIds = [bolts2] });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Contains(r.TableMatches, m => (m.Table, m.A, m.B, m.By) == (TableKind.Group, bolts, bolts2, TableMatchedBy.Members));
        var removed = Assert.Single(r.TableEvents);
        Assert.Equal((TableEventKind.Removed, "Panels"), (removed.Kind, removed.Label));
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal(new PropertyChange(PropertyKind.Groups, null, "Panels", null), set.Change);
        Assert.Equal(3, set.Objects.Count);
        Assert.Equal(removed.Id, set.RootCauseEventId);
    }

    [Fact]
    public void RebuiltFileLayersAreInferredFromWhereTheObjectsWent()
    {
        var a = new SnapshotBuilder();
        var walls = a.Layer("Walls");
        var slabs = a.Layer("Slabs");
        a.Many(walls, 2);
        var slabObjects = a.Many(slabs, 20);
        // B is a rebuilt file: no layer ids survive.
        var b = a.Clone();
        var walls2 = b.Layer("Walls");
        var floor = b.Layer("Floor");
        b.Move(b.On(walls), walls2);
        b.Move(slabObjects.Take(19), floor);
        b.Move(slabObjects.Skip(19), walls2);
        b.RemoveLayer(walls);
        b.RemoveLayer(slabs);

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Equal(
            [(walls, walls2, TableMatchedBy.Path, 1.0), (slabs, floor, TableMatchedBy.Inferred, 0.95)],
            r.TableMatches.Select(m => (m.A, m.B, m.By, m.Confidence)).OrderBy(m => m.A));
        var e = Assert.Single(r.TableEvents);
        Assert.Equal((TableEventKind.Renamed, "Slabs", "Floor", true, 0.95), (e.Kind, e.From, e.To, e.Inferred, e.Confidence));
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal(new PropertyChange(PropertyKind.Layer, null, "Slabs", "Walls"), set.Change);
    }

    [Fact]
    public void UnitsAndTolerancesAreReconciledToA()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var def = a.Block("Bolt");
        var bolt = a.Instance(layer, def, Xform.Translation(1500, 0, 0));
        var b = a.Clone();
        b.Info = b.Info with { Units = UnitSystem.Meters, AbsoluteTolerance = 0.0001, HashGrid = 0.0001, Skipped = ["linked block 'Site'"] };
        b.Edit(bolt, o => o with { InstanceXform = Xform.Translation(1.5, 0, 0) });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Equal(UnitSystem.Millimeters, r.Header.Units);
        Assert.Equal(0.1, r.Header.Tolerance, 12);
        Assert.False(r.Header.HashGridsMatch);
        Assert.Collection(r.Header.Notes,
            n => Assert.StartsWith("Units differ: A is Millimeters, B is Meters.", n),
            n => Assert.StartsWith("Tolerances differ: A 0.01, B 0.1", n),
            n => Assert.StartsWith("Hash grids differ", n),
            n => Assert.StartsWith("Block definition content could not be compared", n),
            n => Assert.Equal("B skipped: linked block 'Site'", n));
        // 1.5 m is 1500 mm: the instance did not move.
        Assert.Empty(r.ChangeSets);
    }

    [Fact]
    public void BlockContentIsNotComparedAcrossHashGrids()
    {
        var (a, b, _, _) = Fixtures.NestedBlock();
        b.Info = b.Info with { HashGrid = 0.001 };

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Empty(r.TableEvents);
        Assert.Empty(r.ChangeSets);
        Assert.Contains(r.Header.Notes, n => n.StartsWith("Block definition content could not be compared"));
    }

    [Fact]
    public void SplitTargetsReportTheirAppearanceAgainstTheSourceAsTableEvents()
    {
        var (a, b) = Fixtures.LayerSplit();
        var targets = b.Layers.Where(l => l.Name.StartsWith("Slabs-")).Select(l => l.Id).ToList();
        b.EditLayer(targets[0], l => l with { Color = Fixtures.Red });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        Assert.Equal([TableEventKind.Split, TableEventKind.PropertyChanged], r.TableEvents.Select(e => e.Kind).Order());
        var e = r.TableEvents.Single(e => e.Kind == TableEventKind.PropertyChanged);
        Assert.Equal((PropertyKind.Color, "#000000", "#FF0000", targets[0], 7, true), (e.Property, e.From, e.To, e.B, e.AffectedObjects, e.Inferred));
        Assert.Empty(r.ChangeSets);
    }

    [Fact]
    public void GroupChangesGroupByIdsAndCause_NotByLabels()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var ab = a.Group("A, B");
        var ga = a.Group("A");
        var gb = a.Group("B");
        var leaves = a.Object(layer, o => o with { GroupIds = [ab] });
        var orphaned = a.Object(layer, o => o with { GroupIds = [ga, gb] });
        var b = a.Clone();
        b.Groups.RemoveAll(g => g.Id == ga || g.Id == gb);
        b.Edit(leaves, o => o with { GroupIds = [] });
        b.Edit(orphaned, o => o with { GroupIds = [] });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        // Both render as "A, B" → none, but one left a group that still exists and the other lost two deleted groups.
        Assert.Equal(2, r.ChangeSets.Count);
        Assert.Single(r.ChangeSets, s => s.Objects.Single().A == leaves);
        Assert.Single(r.ChangeSets, s => s.Objects.Single().A == orphaned);
    }

    [Fact]
    public void GroupCauseNeedsExactlyOneExplainingEvent()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var ga = a.Group("A");
        var gb = a.Group("B");
        var inBoth = a.Object(layer, o => o with { GroupIds = [ga, gb] });
        var inA = a.Object(layer, o => o with { GroupIds = [ga] });
        var b = a.Clone();
        b.Groups.Clear();
        b.Edit(inBoth, o => o with { GroupIds = [] });
        b.Edit(inA, o => o with { GroupIds = [] });

        var r = ModelDiff.Diff(a.Build(), b.Build());
        // Two independent deletes explain inBoth's row; neither is the cause. One delete explains inA's.
        Assert.Null(r.ChangeSets.Single(s => s.Objects.Single().A == inBoth).RootCauseEventId);
        var deleteA = r.TableEvents.Single(e => e.A == ga).Id;
        Assert.Equal(deleteA, r.ChangeSets.Single(s => s.Objects.Single().A == inA).RootCauseEventId);
    }

    [Fact]
    public void SwappedOrReassignedNestedPlacementsAreDefinitionEdits()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var x = a.Block("InnerX", content: 1);
        var y = a.Block("InnerY", content: 2);
        var parent = a.Block("Parent", content: 3);
        a.EditBlock(parent, d => d with { Nested = [new(x, 100), new(y, 200)] });
        a.Instance(layer, parent);

        // Same placements, same set of nested definitions, but X and Y trade places.
        var swapped = a.Clone();
        swapped.EditBlock(parent, d => d with { Nested = [new(y, 100), new(x, 200)] });
        var e = Assert.Single(ModelDiff.Diff(a.Build(), swapped.Build()).TableEvents);
        Assert.Equal((PropertyKind.Content, "Parent"), (e.Property, e.Label));

        // One of three placements moves to a definition the parent already uses.
        var three = a.Clone();
        three.EditBlock(parent, d => d with { Nested = [new(x, 100), new(x, 200), new(y, 300)] });
        var reassigned = three.Clone();
        reassigned.EditBlock(parent, d => d with { Nested = [new(x, 100), new(y, 200), new(y, 300)] });
        Assert.Single(ModelDiff.Diff(three.Build(), reassigned.Build()).TableEvents, ev => ev.Label == "Parent");

        // Listing the same occurrences in another order is no change.
        var reordered = a.Clone();
        reordered.EditBlock(parent, d => d with { Nested = [new(y, 200), new(x, 100)] });
        Assert.Empty(ModelDiff.Diff(a.Build(), reordered.Build()).TableEvents);
    }

    [Fact]
    public void SplitPriorityIsReRankedAfterEachClaim()
    {
        var a = new SnapshotBuilder();
        var other = a.Layer("Other");
        var alpha = a.Layer("Alpha");
        var beta = a.Layer("Beta");
        var gamma = a.Layer("Gamma");
        var b = a.Clone();
        var (lx, ly, lz, lw, lv) = (b.Layer("X"), b.Layer("Y"), b.Layer("Z"), b.Layer("W"), b.Layer("V"));
        void Flow(Guid from, Guid to, int count)
        {
            foreach (var id in a.Many(from, count)) b.Objects.Add(a.Objects.Single(o => o.Id == id) with { LayerId = to });
        }
        Flow(alpha, lx, 5); Flow(alpha, ly, 5);                                   // Alpha: 100%
        Flow(beta, lx, 2); Flow(beta, lz, 4); Flow(beta, lw, 3); Flow(beta, other, 1); // Beta: 90%, 70% once X is gone
        Flow(gamma, lz, 4); Flow(gamma, lv, 3); Flow(gamma, other, 1);            // Gamma: 87.5%
        foreach (var l in new[] { alpha, beta, gamma }) b.RemoveLayer(l);

        var splits = ModelDiff.Diff(a.Build(), b.Build()).TableEvents.Where(e => e.Kind == TableEventKind.Split)
            .ToDictionary(e => e.A!.Value, e => e.Shares.Select(s => s.B).ToList());
        // Alpha takes X; Gamma (87.5%) now beats Beta (70%) to Z; Beta keeps only W, too little to split.
        Assert.Equal([alpha, gamma], splits.Keys.Order());
        Assert.Equal([lz, lv], splits[gamma].Order());
    }

    [Fact]
    public void MovingOffALayerThatIsThenDeletedIsNotCausedByTheDelete()
    {
        var a = new SnapshotBuilder();
        var keep = a.Layer("Walls");
        var temp = a.Layer("Temp");
        var moved = a.Object(temp);
        var deleted = a.Object(temp);
        var b = a.Clone();
        b.Move([moved], keep);
        b.RemoveObject(deleted);
        b.RemoveLayer(temp);

        var r = ModelDiff.Diff(a.Build(), b.Build());
        var delete = Assert.Single(r.TableEvents);
        Assert.Null(r.ChangeSets.Single(s => s.Kind == ChangeKind.Modified).RootCauseEventId);
        Assert.Equal(delete.Id, r.ChangeSets.Single(s => s.Kind == ChangeKind.Removed).RootCauseEventId);
    }

    [Fact]
    public void CompetingSplitsAreClaimedByCoverageNotByName()
    {
        var a = new SnapshotBuilder();
        var other = a.Layer("Other");
        var alpha = a.Layer("Alpha");
        var beta = a.Layer("Beta");
        var alphaObjects = a.Many(alpha, 4);
        var betaObjects = a.Many(beta, 2);
        var b = a.Clone();
        var x = b.Layer("X");
        var y = b.Layer("Y");
        var z = b.Layer("Z");
        // Alpha: 1 to X, 2 to Y, 1 to Other (75% covered). Beta: 1 to X, 1 to Z (100% covered).
        b.Move([alphaObjects[0], betaObjects[0]], x);
        b.Move(alphaObjects.Skip(1).Take(2), y);
        b.Move([alphaObjects[3]], other);
        b.Move([betaObjects[1]], z);
        b.RemoveLayer(alpha);
        b.RemoveLayer(beta);

        var split = Assert.Single(ModelDiff.Diff(a.Build(), b.Build()).TableEvents, e => e.Kind == TableEventKind.Split);
        Assert.Equal(beta, split.A);
        Assert.Equal([x, z], split.Shares.Select(s => s.B));
    }

    [Fact]
    public void NestedReferencesCompareThroughTheBlockMap()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var inner = a.Block("Inner", content: 1);
        var other = a.Block("Other", content: 2);
        var middle = a.Block("Middle", content: 3, inner);
        var oldDef = a.Block("Old", content: 4);
        var newDef = a.Block("New", content: 5, inner);
        var viaMiddle = a.Instance(layer, middle);
        var switched = a.Instance(layer, oldDef);
        var stays = a.Instance(layer, newDef);

        // Renaming Inner leaves Middle's hash alone under the contract: only a rename.
        var renamed = a.Clone();
        renamed.EditBlock(inner, d => d with { Name = "Renamed" });
        var r1 = ModelDiff.Diff(a.Build(), renamed.Build());
        Assert.Equal(TableEventKind.Renamed, Assert.Single(r1.TableEvents).Kind);
        Assert.Empty(r1.ChangeSets);

        // Middle swapping Inner for Other (same own hash) is an edit of Middle.
        var swapped = a.Clone();
        swapped.EditBlock(middle, d => d with { Nested = [new NestedBlock(other, 0)] });
        var e = Assert.Single(ModelDiff.Diff(a.Build(), swapped.Build()).TableEvents);
        Assert.Equal((TableEventKind.PropertyChanged, PropertyKind.Content, "Middle"), (e.Kind, e.Property, e.Label));

        // An instance switched Old → New doesn't inherit New's nested Inner edit; one that was on New does.
        var edited = a.Clone();
        edited.EditBlock(inner, d => d with { ContentHash = 9 });
        edited.Edit(switched, o => o with { BlockDefinitionId = newDef });
        var nested = ModelDiff.Diff(a.Build(), edited.Build()).ChangeSets.Single(s => s.Change!.Property == PropertyKind.BlockContent);
        Assert.Equal([new ObjectPair(viaMiddle, viaMiddle), new ObjectPair(stays, stays)], nested.Objects);
    }

    [Fact]
    public void ConfiguredStableKeyAndDiffUidAreBothIdentity()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var o1 = a.Object(layer, o => o with { UserText = new Dictionary<string, string> { ["diff:uid"] = "u1", ["mark"] = "C-12" } });
        var b = a.Clone();
        b.Edit(o1, o => o with { UserText = new Dictionary<string, string> { ["diff:uid"] = "u2", ["mark"] = "C-12" } });

        var r = ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { StableKey = "mark" });
        Assert.Equal(MatchedBy.StableKey, Assert.Single(r.Matches).By);
        Assert.Empty(r.ChangeSets);
    }

    [Fact]
    public void GroupMembershipMatchingIgnoresLayerInferShare()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("L");
        var group = a.Group("Bolts");
        var members = a.Many(layer, 4, o => o with { GroupIds = [group] });
        var b = a.Clone();
        b.Groups.RemoveAll(g => g.Id == group);
        var regrouped = b.Group("Bolts");
        foreach (var id in members.Take(3)) b.Edit(id, o => o with { GroupIds = [regrouped] });
        b.Edit(members[3], o => o with { GroupIds = [] });

        // 3 of 4 members regrouped: Jaccard 0.75, above the fixed cutoff, whatever the layer share says.
        var r = ModelDiff.Diff(a.Build(), b.Build(), new DiffSettings { LayerInferShare = 0.9 });
        Assert.Contains(r.TableMatches, m => (m.Table, m.A, m.B, m.By) == (TableKind.Group, group, regrouped, TableMatchedBy.Members));
    }
}
