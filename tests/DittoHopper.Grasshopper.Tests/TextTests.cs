using DittoHopper.Core;
using DittoHopper.Core.Tests;
using Xunit;

namespace DittoHopper.Grasshopper.Tests;

/// <summary>Layer patterns, settings names and result wording. Plain tests: no Rhino needed.</summary>
public sealed class TextTests
{
    [Theory]
    [InlineData("Structure::*", "Structure", true)]
    [InlineData("Structure::*", "Structure::Columns", true)]
    [InlineData("Structure::*", "structure::columns::L1", true)]
    [InlineData("Structure::*", "Structures", false)]
    [InlineData("Structure::*", "Site::Structure", false)]
    [InlineData("*wall*", "Architecture::A-WALL", true)]
    [InlineData("Level-0?", "Level-01", true)]
    [InlineData("Level-0?", "Level-012", false)]
    [InlineData("Default", "Default", true)]
    [InlineData("Default", "Default::Sub", false)]
    public void LayerGlobMatchesPaths(string pattern, string path, bool expected) =>
        Assert.Equal(expected, new LayerGlob([pattern]).Matches(path));

    [Fact]
    public void EmptyGlobMatchesEverythingAndSeveralMatchAny()
    {
        Assert.True(new LayerGlob([]).Matches("anything"));
        Assert.True(new LayerGlob(["", " "]).MatchesAll);
        var glob = new LayerGlob(["Walls", "Doors::*"]);
        Assert.True(glob.Matches("Doors::Frames"));
        Assert.False(glob.Matches("Windows"));
        Assert.Equal("Walls, Doors::*", glob.ToString());
    }

    [Fact]
    public void IgnoreTurnsPropertiesOffAndReportsUnknownNames()
    {
        var toggles = Vocabulary.Ignore(new PropertyToggles(), ["Color", "user-text", "Moved", "colour", "bogus"], out var unknown);
        Assert.False(toggles.Color);
        Assert.False(toggles.UserText);
        Assert.False(toggles.Placement);
        Assert.True(toggles.Shape);
        Assert.Equal(["bogus"], unknown);
        Assert.Equal("placement", Vocabulary.Canonical("Moved"));
        Assert.Equal("user text", Vocabulary.Canonical("UserText"));
        Assert.Null(Vocabulary.Canonical("bogus"));
        Assert.Equal("Key mark · Tolerance 0.5 · Ignoring placement, display order, color, print width, user text",
            Vocabulary.Describe(new DiffSettings { StableKey = "mark", ShapeTolerance = 0.5, Toggles = toggles }));
        Assert.Equal("Key diff:uid · Tolerance auto · Ignoring display order, print width", Vocabulary.Describe(new DiffSettings()));
    }

    [Fact]
    public void ResultWordingReadsLikeTheDesignDoc()
    {
        var a = new SnapshotBuilder();
        var walls = a.Layer("Walls");
        var defaultLayer = a.Layer("Default");
        var moved = a.Many(walls, 3);
        a.Many(walls, 2);
        var b = a.Clone();
        b.EditLayer(walls, l => l with { Name = "A-WALL" });
        b.Move(moved, defaultLayer);
        b.Edit(moved[0], o => o with { UserText = new Dictionary<string, string> { ["supplier"] = "FX-02" } });
        b.Object(walls);

        var r = ModelDiff.Diff(a.Build(), b.Build());

        Assert.Equal("2 of 5 objects identical · 3 change sets · 1 table event", Describe.Summary(r));
        Assert.Equal(["Layer renamed: Walls → A-WALL · 3 objects"], r.TableEvents.Select(Describe.Event));
        var changes = r.ChangeSets.Where(s => s.Change is not null).Select(s => Describe.Change(s.Change!)).ToList();
        Assert.Contains("layer Walls → Default", changes);
        Assert.Contains("user text + supplier = FX-02", changes);
        Assert.Equal("GUID", Describe.Evidence(r.Matches[0]));
        Assert.Contains("Matched 5 by GUID", Describe.Report(r));
    }
}
