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
}
