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
            n => Assert.Equal("B skipped: linked block 'Site'", n));
        // 1.5 m is 1500 mm: the instance did not move.
        Assert.Empty(r.ChangeSets);
    }
}
