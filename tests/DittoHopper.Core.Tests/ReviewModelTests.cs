using Xunit;

namespace DittoHopper.Core.Tests;

/// <summary>The shared presentation model behind the review panel, the HUD and the Grasshopper text.</summary>
public class ReviewModelTests
{
    private static readonly string[] Compared =
        ["placement", "shape", "layer", "groups", "color", "material", "linetype", "name", "user text"];

    /// <summary>
    /// The mock's revision in miniature. Facade::Panels is the intended work: its colour changed, one panel renamed, one
    /// removed and one added. Outside it, Grid-C was dropped on Default and a column plate nudged 0.05 mm. Slabs untouched.
    /// </summary>
    private static (SnapshotBuilder A, SnapshotBuilder B) Revision()
    {
        var a = new SnapshotBuilder();
        var structure = a.Layer("Structure");
        var columns = a.Layer("Columns", structure);
        var slabs = a.Layer("Slabs", structure);
        var panels = a.Layer("Panels", a.Layer("Facade"));
        var grid = a.Layer("Grid");
        var dflt = a.Layer("Default");
        foreach (var n in new[] { "C-01", "C-02", "C-03" }) a.Object(columns, o => o with { Name = n });
        var plate = a.Instance(columns, a.Block("Plate"));
        a.Edit(plate, o => o with { Name = "C-08" });
        a.Object(slabs, o => o with { Name = "Slab L1" });
        var s04 = a.Object(panels, o => o with { Name = "FP-S04" });
        a.Object(panels, o => o with { Name = "FP-S05" });
        var n06 = a.Object(panels, o => o with { Name = "FP-N06" });
        a.Object(grid, o => o with { Name = "Grid-A" });
        var gridC = a.Object(grid, o => o with { Name = "Grid-C" });

        var b = a.Clone();
        b.EditLayer(panels, l => l with { Color = Fixtures.Red });
        b.Edit(s04, o => o with { Name = "FP-S04b" });
        b.RemoveObject(n06);
        b.Object(panels, o => o with { Name = "FP-N06a" });
        b.Move([gridC], dflt);
        b.Edit(plate, o => o with { InstanceXform = Xform.Translation(0.05, 0, 0) });
        return (a, b);
    }

    private static Review Build(DiffSettings? settings = null, string? gate = null)
    {
        var (a, b) = Revision();
        var (ma, mb) = (a.Build(), b.Build());
        return Review.Build(new DiffIndex(ModelDiff.Diff(ma, mb, settings), ma, mb), gate);
    }

    private static readonly DiffSettings Scoped = new() { ScopeGlob = "Facade::*" };

    [Fact]
    public void ChangesAreGroupedNeedsALookThenTableEventsThenInScope()
    {
        var review = Build(Scoped);

        Assert.Equal([ReviewGroupKind.NeedsLook, ReviewGroupKind.TableEvents, ReviewGroupKind.InScope], review.Groups.Select(g => g.Kind));
        Assert.Equal(["Needs a look", "Table events", "In scope · Facade::*"], review.Groups.Select(g => g.Label));
        Assert.Equal(["C-08", "Grid-C"], review.Groups[0].Rows.Select(r => r.Name).Order());
        var colour = Assert.Single(review.Groups[1].Rows);
        Assert.Equal(ReviewStatus.Event, colour.Status);
        Assert.Equal("Facade::Panels", colour.Name);
        Assert.Equal(["FP-N06", "FP-N06a", "FP-S04b"], review.Groups[2].Rows.Select(r => r.Name).Order());
        // J and K walk the groups in display order.
        Assert.Equal(review.Groups.SelectMany(g => g.Rows), review.Rows);
    }

    [Fact]
    public void WithoutAScopeTheLastGroupIsChangesAndOnlyDetectorsFlag()
    {
        var review = Build();

        Assert.Equal(["Needs a look", "Table events", "Changes"], review.Groups.Select(g => g.Label));
        // Only the detectors flag now: Default layer and the tiny nudge.
        var look = review.Groups[0].Rows.ToDictionary(r => r.Name);
        Assert.Equal(["Default layer"], look["Grid-C"].Flags);
        Assert.Equal(["Tiny nudge"], look["C-08"].Flags);
        Assert.StartsWith("6 of 10 objects are identical.", review.Sentence);
    }

    [Fact]
    public void ARowSaysWhatChangedHowItWasMatchedAndWhyItIsFlagged()
    {
        var review = Build(Scoped);
        var grid = review.Rows.Single(r => r.Name == "Grid-C");

        Assert.Equal(ReviewStatus.Modified, grid.Status);
        Assert.Equal("Layer Grid → Default", grid.Description);
        Assert.Equal(["Default layer", "Outside scope"], grid.Flags);
        Assert.Equal("key", grid.Badge); // names are unique on both sides: matching pass 1
        Assert.Equal("Grid-C · layer Grid → Default · Default layer", grid.Tag);
        Assert.False(grid.Ring);

        var nudge = review.Rows.Single(r => r.Name == "C-08");
        Assert.True(nudge.Ring);
        Assert.StartsWith("Moved 0.05 mm in X", nudge.Description);
        Assert.EndsWith(" · Structure::Columns", nudge.Description);

        var removed = review.Rows.Single(r => r.Status == ReviewStatus.Removed);
        Assert.Equal("1 object removed from Facade::Panels", removed.Description);
        Assert.Equal("—", removed.Badge);
        Assert.Equal("1 object added on Facade::Panels", review.Rows.Single(r => r.Status == ReviewStatus.Added).Description);
    }

    [Fact]
    public void ChipsCountObjectsAndEvents()
    {
        var review = Build(Scoped);

        Assert.Equal(["2 need a look", "3 modified", "1 added", "1 removed", "1 table event", "6 unchanged"], review.Chips.Select(c => c.Text));
        Assert.Equal(review.Run.Result.UnchangedObjects, review.Chips.Single(c => c.Kind == ChipKind.Unchanged).Count);
        Assert.Equal(review.Run.Modified.Count, review.Chips.Single(c => c.Kind == ChipKind.Modified).Count);
        Assert.Equal(string.Join(" · ", review.Chips.Select(c => c.Text)), review.Strip);
    }

    [Fact]
    public void TheCardListsFromToTheFlagReasonsAndWhatDidNotChange()
    {
        var review = Build(Scoped);
        var grid = review.Rows.Single(r => r.Name == "Grid-C");
        var card = review.Card(grid);

        Assert.Equal("Modified", card.Status);
        Assert.Equal("Grid-C", card.Title);
        Assert.Equal("key", card.Badge);
        Assert.Equal("Needs a look · 1 object", card.Meta);
        Assert.Equal([new CardChange("layer", "Grid", "Default")], card.Changed);
        Assert.Equal([Detectors.Describe(Detectors.DefaultLayer), Detectors.Describe(Detectors.OutsideScope)], card.Why);
        Assert.Equal(Compared.Where(p => p != "layer"), card.Unchanged);
        // The card lists the compared properties by name.
        Assert.Equal(card.Unchanged, review.UnchangedOf(grid.Objects[0]));

        // A block instance has a block and a placement, not a shape.
        var nudge = review.Card(review.Rows.Single(r => r.Name == "C-08"));
        var moved = Assert.Single(nudge.Changed);
        // A move has no two values to put side by side: it is described, not from → to.
        Assert.Equal("placement", moved.Property);
        Assert.Null(moved.From);
        Assert.Null(moved.To);
        Assert.StartsWith("moved 0.05 mm in X", moved.Text);
        Assert.Contains("block", nudge.Unchanged);
        Assert.DoesNotContain("shape", nudge.Unchanged);
        Assert.DoesNotContain("placement", nudge.Unchanged);
    }

    [Fact]
    public void ATableEventCardNamesItsSideEffect()
    {
        var review = Build(Scoped);
        var colour = review.Rows.Single(r => r.Status == ReviewStatus.Event);
        var card = review.Card(colour);

        Assert.Equal("Table event", card.Status);
        Assert.Equal("Table events · 3 ByLayer objects", card.Meta);
        var change = Assert.Single(card.Changed);
        Assert.Equal("layer color", change.Property);
        Assert.NotEqual(change.From, change.To);
        Assert.Contains(card.Why, w => w.StartsWith("3 objects take their color from the layer", StringComparison.Ordinal));
        Assert.Empty(card.Unchanged);
        // The objects it repaints: every ByLayer panel on the layer in B, the renamed one too.
        Assert.Equal(["FP-N06a", "FP-S04b", "FP-S05"], colour.Objects.Select(p => review.Run.ObjectB(p.B!.Value)!.Name).Order());
    }

    [Fact]
    public void TheUnchangedTabAccountsForEveryIdenticalObjectPerLayer()
    {
        var review = Build(Scoped);
        var layers = review.Unchanged.ToDictionary(l => l.Path);

        Assert.Equal(review.Run.Result.UnchangedObjects, review.Unchanged.Sum(l => l.Identical));
        Assert.Equal(["Facade::Panels", "Grid", "Structure::Columns", "Structure::Slabs"], review.Unchanged.Select(l => l.Path));
        Assert.Equal(new UnchangedLayer("Structure::Columns", 3, 4, 0, "C-01, C-02, C-03"), layers["Structure::Columns"]);
        Assert.Equal(new UnchangedLayer("Structure::Slabs", 1, 1, 0, "Slab L1"), layers["Structure::Slabs"]);
        // FP-S05 is identical, but looks different: its colour comes from the recoloured layer.
        Assert.Equal(new UnchangedLayer("Facade::Panels", 1, 3, 1, "FP-S05"), layers["Facade::Panels"]);
        Assert.Equal("6 of 10 objects are identical in every property compared: " + string.Join(", ", Compared) + ", block. " +
            "1 of them look different only because a layer, block, material or linetype they use changed.", review.UnchangedSummary);
    }

    [Fact]
    public void TheGateHasAssertUnchangedsSemanticsAndIsOffWhenEmpty()
    {
        Assert.Null(Build(Scoped).Gate);
        Assert.Null(Build(Scoped, " ").Gate);

        var structure = Build(Scoped, "Structure::*").Gate!;
        Assert.False(structure.Pass);
        Assert.Equal("Structure::*", structure.Layers);
        Assert.StartsWith("1 object on Structure::Columns: moved 0.05 mm in X", Assert.Single(structure.Why));

        var slabs = Build(Scoped, "Structure::Slabs; Grid").Gate!;
        Assert.False(slabs.Pass); // Grid-C left Grid
        Assert.True(Build(Scoped, "Structure::Slabs").Gate!.Pass);
        Assert.StartsWith("No layer in A or B matches Strcture::*", Assert.Single(Build(Scoped, "Strcture::*").Gate!.Why));
        Assert.EndsWith(" · Gate Structure::* fails", Build(Scoped, "Structure::*").Strip);
    }

    [Fact]
    public void AFuzzyPairShowsItsWeakEvidence()
    {
        // Rebaked with a new id and a slightly larger shape: only matching pass 5 pairs it.
        var a = new SnapshotBuilder();
        var walls = a.Layer("Walls");
        var wall = a.Solid(walls, new Vec3(0, 0, 0));
        var b = a.Clone();
        b.Edit(b.Rebake(wall), o => o with { Geometry = SnapshotBuilder.SolidShape(99, new Vec3(0, 0, 0), 104) });
        var (ma, mb) = (a.Build(), b.Build());
        var review = Review.Build(new DiffIndex(ModelDiff.Diff(ma, mb), ma, mb));

        // Its shape changed too, so the row is a modification; the badge still says the pairing is uncertain.
        var row = Assert.Single(review.Rows);
        Assert.Equal(ReviewStatus.Modified, row.Status);
        Assert.Equal(ReviewGroupKind.NeedsLook, row.Group);
        Assert.Matches(@"^fuzzy 0\.\d\d · needs review$", row.Badge);
        Assert.Equal(["Fuzzy match"], row.Flags);
        Assert.Equal(row.Badge, review.Card(row).Badge);
    }

    [Fact]
    public void AGroupRenameReachesItsMembersWithoutPaintingThemChanged()
    {
        var a = new SnapshotBuilder();
        var doors = a.Layer("Doors");
        var set = a.Group("Door set");
        var members = Enumerable.Range(0, 3).Select(_ => a.Object(doors, o => o with { GroupIds = [set] })).ToList();
        a.Object(doors);
        var b = a.Clone();
        b.Groups[0] = b.Groups[0] with { Name = "Door set 2" };
        var (ma, mb) = (a.Build(), b.Build());
        var review = Review.Build(new DiffIndex(ModelDiff.Diff(ma, mb), ma, mb));

        var row = Assert.Single(review.Rows);
        Assert.Equal(ReviewStatus.Event, row.Status);
        // Highlight, zoom and select reach the members...
        Assert.Equal(members.Order(), row.Objects.Select(p => p.B!.Value).Order());
        Assert.All(row.Objects, p => Assert.Equal(p.A, p.B));
        // ...but a rename doesn't change how they look.
        Assert.Empty(review.Run.ChangedThroughTables);
    }

    [Fact]
    public void NothingChangedMeansNoRowsAndEveryObjectUnchanged()
    {
        var (a, _) = Revision();
        var m = a.Build();
        var review = Review.Build(new DiffIndex(ModelDiff.Diff(m, m), m, m));

        Assert.Empty(review.Rows);
        Assert.Empty(review.Groups);
        Assert.Equal("0 need a look", review.Chips[0].Text);
        Assert.Equal(review.Run.ObjectsInA, review.Unchanged.Sum(l => l.Identical));
    }
}
