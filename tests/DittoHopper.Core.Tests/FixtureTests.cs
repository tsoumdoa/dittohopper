using Xunit;

namespace DittoHopper.Core.Tests;

/// <summary>The design doc's test plan, for the cases decidable without real geometry. Each pins a golden file.</summary>
public class FixtureTests
{
    [Fact]
    public void LayerRename_IsOneEventAndNoObjectRows()
    {
        var r = Fixtures.Diff(Fixtures.LayerRename());
        var e = Assert.Single(r.TableEvents);
        Assert.Equal((TableEventKind.Renamed, "Walls", "A-WALL", 4), (e.Kind, e.From, e.To, e.AffectedObjects));
        Assert.Empty(r.ChangeSets);
        Assert.Equal(4, r.UnchangedObjects);
        Golden.Verify("layer-rename", r);
    }

    [Fact]
    public void LayerReparent_IsReparentedPlusAddedParent()
    {
        var r = Fixtures.Diff(Fixtures.LayerReparent());
        Assert.Equal(
            [(TableEventKind.Reparented, "Furniture", null, "Interior"), (TableEventKind.Added, "Interior", null, null)],
            r.TableEvents.Select(e => (e.Kind, e.Label, e.From, e.To)).OrderBy(x => x.Kind == TableEventKind.Added));
        Assert.Empty(r.ChangeSets);
        Golden.Verify("layer-reparent", r);
    }

    [Fact]
    public void LayerSplit_IsOneInferredSplitWithShares()
    {
        var r = Fixtures.Diff(Fixtures.LayerSplit());
        var e = Assert.Single(r.TableEvents);
        Assert.Equal((TableEventKind.Split, "Slabs", true, 1.0), (e.Kind, e.Label, e.Inferred, e.Confidence));
        Assert.Equal([("Slabs-L01", 7, 7 / 12.0), ("Slabs-L02", 5, 5 / 12.0)], e.Shares.Select(s => (s.Label, s.Objects, s.Share)));
        Assert.Empty(r.ChangeSets);
        Golden.Verify("layer-split", r);
    }

    [Fact]
    public void ByLayerColor_IsOneLayerEventAndNoOwnObjectChanges()
    {
        var r = Fixtures.Diff(Fixtures.ByLayerColor());
        var e = Assert.Single(r.TableEvents);
        Assert.Equal((TableEventKind.PropertyChanged, PropertyKind.Color, "#000000", "#FF0000"), (e.Kind, e.Property, e.From, e.To));
        Assert.Empty(r.ChangeSets);
        Golden.Verify("bylayer-color", r);
    }

    [Fact]
    public void GhRebake_AllMatchedByHashWithNoChanges()
    {
        var r = Fixtures.Diff(Fixtures.GhRebake());
        Assert.Equal(4, r.Matches.Count);
        Assert.All(r.Matches, m => Assert.Equal(MatchedBy.GeometryHash, m.By));
        Assert.Empty(r.TableEvents);
        Assert.Empty(r.ChangeSets);
        Golden.Verify("gh-rebake", r);
    }

    [Fact]
    public void DefaultLayer_23ObjectsAreOneChangeSet()
    {
        var r = Fixtures.Diff(Fixtures.DefaultLayer());
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal(new PropertyChange(PropertyKind.Layer, null, "Walls", "Default"), set.Change);
        Assert.Equal(23, set.Objects.Count);
        Assert.Null(set.RootCauseEventId);
        Golden.Verify("default-layer", r);
    }

    [Fact]
    public void UserText_OneChangePerKey_StableKeyIgnored()
    {
        var r = Fixtures.Diff(Fixtures.UserText());
        Assert.Equal(
            [new(PropertyKind.UserText, "panel_type", "A", "B"),
             new(PropertyKind.UserText, "supplier", null, "FX-02"),
             new PropertyChange(PropertyKind.UserText, "temp_note", "check", null)],
            r.ChangeSets.Select(s => s.Change));
        Assert.Equal(MatchedBy.StableKey, Assert.Single(r.Matches).By);
        Golden.Verify("user-text", r);
    }

    [Fact]
    public void NestedBlock_EachOuterInstanceReportsIt_InnerDefinitionIsRootCause()
    {
        var (a, b, inner) = Fixtures.NestedBlock();
        var r = ModelDiff.Diff(a.Build(), b.Build());
        var e = Assert.Single(r.TableEvents);
        Assert.Equal((TableEventKind.PropertyChanged, PropertyKind.Content, inner, 5), (e.Kind, e.Property, e.A, e.AffectedObjects));
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal(new PropertyChange(PropertyKind.BlockContent, "Inner", null, null), set.Change);
        Assert.Equal(3, set.Objects.Count);
        Assert.Equal(e.Id, set.RootCauseEventId);
        Golden.Verify("nested-block", r);
    }

    [Fact]
    public void CopyInPlace_IsOneAddedObject_OriginalKeepsItsPair()
    {
        var r = Fixtures.Diff(Fixtures.CopyInPlace());
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal((ChangeKind.Added, "Columns", 1), (set.Kind, set.Layer, set.Objects.Count));
        Assert.All(r.Matches, m => Assert.Equal(MatchedBy.Guid, m.By));
        Assert.Equal(3, r.UnchangedObjects);
        Golden.Verify("copy-in-place", r);
    }

    [Fact]
    public void DeleteLayer_ItsObjectsAreRemovedRowsLinkedToTheDelete()
    {
        var r = Fixtures.Diff(Fixtures.DeleteLayer());
        var e = Assert.Single(r.TableEvents);
        Assert.Equal((TableEventKind.Removed, "Temp", 4), (e.Kind, e.Label, e.AffectedObjects));
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal((ChangeKind.Removed, "Temp", 4, e.Id), (set.Kind, set.Layer, set.Objects.Count, set.RootCauseEventId));
        Golden.Verify("delete-layer", r);
    }

    [Fact]
    public void RootCauses_362RowsCollapseIntoSixCauses()
    {
        var r = Fixtures.Diff(Fixtures.RootCauses());
        Assert.Equal(
            [(TableEventKind.PropertyChanged, "Doors"), (TableEventKind.Reparented, "Furniture"), (TableEventKind.Added, "Interior"),
             (TableEventKind.Split, "Slabs"), (TableEventKind.Removed, "Temp"), (TableEventKind.Renamed, "Walls")],
            r.TableEvents.Select(e => (e.Kind, e.Label)));
        Assert.Equal(362, r.TableEvents.Sum(e => e.AffectedObjects));
        // The only object rows are the 14 removed with Temp, and they point at its Delete.
        var set = Assert.Single(r.ChangeSets);
        Assert.Equal((ChangeKind.Removed, 14), (set.Kind, set.Objects.Count));
        Assert.Equal(r.TableEvents.Single(e => e.Kind == TableEventKind.Removed).Id, set.RootCauseEventId);
        Assert.Equal(212 + 36 + 88 + 12, r.UnchangedObjects);
    }
}
