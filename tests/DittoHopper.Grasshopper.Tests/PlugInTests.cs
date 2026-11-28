using System.Drawing;
using System.Runtime.Loader;
using DittoHopper.Core;
using DittoHopper.PlugIn;
using DittoHopper.Rhino;
using DittoHopper.Rhino.Tests;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Microsoft.Win32;
using Rhino;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;
using RhinoPlugIn = Rhino.PlugIns.PlugIn;

namespace DittoHopper.Grasshopper.Tests;

/// <summary>Loads DittoHopper.rhp from the test folder into the headless Rhino. Call only inside RhinoHost.Run.</summary>
internal static class PlugInLoader
{
    private static readonly string Key = $@"Software\McNeel\Rhinoceros\8.0\Plug-Ins\{ReviewHub.PlugInId}";
    private static readonly string Settings = $@"Software\McNeel\Rhinoceros\8.0\Scheme: Default\Plug-ins\{ReviewHub.PlugInId}";
    private static readonly string RhpPath = Path.Combine(AppContext.BaseDirectory, "DittoHopper.rhp");
    private static bool loaded;

    /// <summary>
    /// Rhino remembers a plug-in loaded by path (HKCU ...\Plug-Ins\{id}) and its panel's settings, which would make the
    /// owner's Rhino load this test build. Forget both, but only when the registration points here or there is none:
    /// never touch a real install's.
    /// </summary>
    public static void Forget()
    {
        string? file;
        using (var plugIn = Registry.CurrentUser.OpenSubKey(Key + @"\PlugIn")) file = plugIn?.GetValue("FileName") as string;
        bool ours = file is not null && string.Equals(Path.GetFullPath(file), RhpPath, StringComparison.OrdinalIgnoreCase);
        bool registered;
        using (var key = Registry.CurrentUser.OpenSubKey(Key)) registered = key is not null;
        if (ours) Registry.CurrentUser.DeleteSubKeyTree(Key, throwOnMissingSubKey: false);
        if (ours || !registered) Registry.CurrentUser.DeleteSubKeyTree(Settings, throwOnMissingSubKey: false);
    }

    public static DittoHopperPlugIn Load()
    {
        if (!loaded)
        {
            Forget();
            RhinoHost.OnShutdown(Forget);
            loaded = true;
        }
        if (RhinoPlugIn.Find(ReviewHub.PlugInId) is DittoHopperPlugIn loadedAlready) return loadedAlready;
        var result = RhinoPlugIn.LoadPlugIn(RhpPath, out var id);
        Assert.Equal(ReviewHub.PlugInId, id);
        Assert.True(RhinoPlugIn.Find(id) is DittoHopperPlugIn, $"DittoHopper.rhp did not load: {result}");
        return (DittoHopperPlugIn)RhinoPlugIn.Find(id);
    }
}

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class PlugInTests(RhinoHost host, ITestOutputHelper output)
{
    [RhinoFact]
    public void ThePlugInLoadsAndSharesOneCopyOfTheHubWithGrasshopper() => host.Run(() =>
    {
        Gh.Load();
        var plugIn = PlugInLoader.Load();

        Assert.Contains("DittoReview", RhinoPlugIn.GetEnglishCommandNames(ReviewHub.PlugInId));
        Assert.True(ReviewHub.Ready); // registered by the plug-in, seen from here: one static
        _ = typeof(AssemblyInfo).Assembly; // the .gha
        // Both plug-ins and this test see one copy of each shared assembly, loaded from the one folder.
        foreach (var name in new[] { "DittoHopper", "DittoHopper.Grasshopper", "DittoHopper.Rhino", "DittoHopper.Core" })
        {
            var copies = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == name).ToList();
            foreach (var a in copies) output.WriteLine($"{name}: {a.Location} in {AssemblyLoadContext.GetLoadContext(a)?.Name}");
            Assert.Single(copies);
        }
        Assert.Same(typeof(DittoHopperPlugIn).Assembly, plugIn.GetType().Assembly);
        Assert.Equal(Path.GetDirectoryName(typeof(ReviewHub).Assembly.Location), Path.GetDirectoryName(plugIn.Assembly.Location));
    });

    /// <summary>The mock's workflow: the open document against its last save, then scope, gate, select and refresh.</summary>
    [RhinoFact]
    public void DittoReviewComparesTheDocumentWithItsLastSave() => host.Run(() =>
    {
        PlugInLoader.Load();
        var pair = FixturePair.Create("review-doc", BuildModel, _ => { });
        using var doc = RhinoDoc.OpenHeadless(pair.A);
        Move(doc, "wall", 500);
        Move(doc, "column", 0.05); // a tiny nudge

        var session = ReviewSession.Current;
        var run = DittoReviewCommand.Review(new ReviewSource(doc.RuntimeSerialNumber, null, null));
        var review = session.Review!;
        output.WriteLine(review.Header);
        output.WriteLine(review.Sentence);
        foreach (var row in review.Rows) output.WriteLine($"[{row.Group}] {row.Name} · {row.Description} · {string.Join(", ", row.Flags)} · {row.Badge}");

        Assert.Same(run, session.Run);
        Assert.Same(doc, session.BDocument);
        Assert.Equal("A A.3dm (last save) → B A.3dm (open) · mm, shape tolerance 0.01 (auto)", review.Header);
        Assert.Equal(["Needs a look", "Changes"], review.Groups.Select(g => g.Label));
        var nudge = Assert.Single(review.Groups[0].Rows);
        Assert.True(nudge.Ring);
        Assert.Equal("C-01", nudge.Name);
        Assert.Equal("W-01", Assert.Single(review.Groups[1].Rows).Name);

        // The scene draws the live document: the wall where it is now.
        var wall = session.Scene.Single(i => i.Status == DiffScene.Status.Modified && i.Geometry.GetBoundingBox(i.Xform).Min.X > 400);
        Assert.Equal(500, wall.Geometry.GetBoundingBox(wall.Xform).Min.X, 6);
        Assert.Contains(session.Scene, i => i.Status == DiffScene.Status.Previous);
        Assert.True(session.IsDrawnByReview(wall.Id));

        // Scope re-diffs: the nudge is now also outside it, and the wall is in scope.
        session.SetScope("Walls");
        Assert.Equal(["Needs a look", "In scope · Walls"], session.Review!.Groups.Select(g => g.Label));
        Assert.Contains("Outside scope", session.Review.Rows[0].Flags);
        session.SetGate("Structure::*");
        Assert.False(session.Review.Gate!.Pass);
        session.SetGate("Walls");
        Assert.False(session.Review.Gate!.Pass);
        session.SetGate("");
        Assert.Null(session.Review.Gate);

        // Select in Rhino selects B's objects in the document.
        session.Select(1);
        Assert.Equal("W-01", session.SelectedRow!.Name);
        Assert.Equal(1, session.SelectInRhino(session.SelectedRow));
        Assert.Single(doc.Objects.GetSelectedObjects(false, false));
        Assert.True(session.BoxOf(session.SelectedRow).IsValid);
        session.Move(-1);
        Assert.Equal("C-01", session.SelectedRow!.Name);

        // Refresh reads the document again and keeps the selection by id.
        Move(doc, "wall", -500);
        session.Refresh();
        Assert.Null(session.Status);
        Assert.Single(session.Review.Rows);
        Assert.Equal("C-01", session.SelectedRow!.Name);
    });

    [RhinoFact]
    public void DittoReviewComparesTwoFilesInTheDocumentsUnits() => host.Run(() =>
    {
        PlugInLoader.Load();
        var pair = FixturePair.Create("review-files", BuildModel, b => Model.Transform(b, "wall", Transform.Translation(500, 0, 0)));
        using var doc = RhinoDoc.CreateHeadless(null);
        doc.ModelUnitSystem = global::Rhino.UnitSystem.Meters;
        var previous = RhinoDoc.ActiveDoc;
        RhinoDoc.ActiveDoc = doc;
        try
        {
            var session = ReviewSession.Current;
            DittoReviewCommand.Review(new ReviewSource(doc.RuntimeSerialNumber, pair.A, pair.B));
            Assert.Null(session.BDocument);
            Assert.Equal("A A.3dm → B B.3dm · mm, shape tolerance 0.01 (auto)", session.Review!.Header);
            // Drawn from the snapshots, in the document's metres.
            var wall = session.Scene.Single(i => i.Status == DiffScene.Status.Modified);
            Assert.Equal(0.5, wall.Geometry.GetBoundingBox(wall.Xform).Min.X, 6);
            Assert.False(session.IsDrawnByReview(wall.Id));
            session.Select(0);
            Assert.Equal(0, session.SelectInRhino(session.SelectedRow));
            Assert.StartsWith("B is a file", session.Status);

            // A missing file is an error message, not a crash.
            Assert.ThrowsAny<IOException>(() => new ReviewSource(doc.RuntimeSerialNumber, pair.A + ".missing", pair.B).Load());
            // An unsaved document has no last save.
            var e = Assert.Throws<InvalidOperationException>(() => DittoReviewCommand.Review(new ReviewSource(doc.RuntimeSerialNumber, null, null)));
            Assert.Contains("not been saved", e.Message);
        }
        finally
        {
            RhinoDoc.ActiveDoc = previous;
        }
    });

    [RhinoFact]
    public void OpenReviewHandsEachNewResultToThePanelOnce() => host.Run(() =>
    {
        Gh.Load();
        // Rhino knows an installed plug-in by its id, so the hub can load it on first use; here it is loaded from the test folder.
        PlugInLoader.Load();
        var pair = FixturePair.Create("open-review", BuildModel, b => Model.Transform(b, "wall", Transform.Translation(500, 0, 0)));
        using var doc = new GH_Document { Enabled = true };
        var a = Gh.Add<SnapshotComponent>(doc);
        var b = Gh.Add<SnapshotComponent>(doc);
        var diff = Gh.Add<ModelDiffComponent>(doc);
        var open = Gh.Add<OpenReviewComponent>(doc);
        Gh.Set(a.Params.Input[0], new GH_String(pair.A));
        Gh.Set(b.Params.Input[0], new GH_String(pair.B));
        Gh.Wire(a.Params.Output[0], diff.Params.Input[0]);
        Gh.Wire(b.Params.Output[0], diff.Params.Input[1]);
        Gh.Wire(diff.Params.Output[4], open.Params.Input[0]);
        Gh.Solve(doc);

        // The first solve shows Model Diff's result.
        var session = ReviewSession.Current;
        var result = ((ResultGoo)Assert.Single(Gh.Out(diff, 4))).Value;
        Assert.True(Gh.Errors(open).Length == 0, Gh.Errors(open));
        Assert.Equal("Sent", open.Message);
        Assert.Same(result, session.Run);
        Assert.Null(session.Source);

        // A recompute with the same result doesn't send it again.
        int shown = 0;
        void Count() => shown++;
        session.Changed += Count;
        try
        {
            open.ExpireSolution(false);
            Gh.Solve(doc);
            Assert.Equal(0, shown);
            // A new result does.
            Gh.Set(b.Params.Input[0], new GH_String(pair.A));
            Gh.Solve(doc);
            Assert.Equal(1, shown);
            Assert.NotSame(result, session.Run);
            Assert.Empty(session.Review!.Rows);
        }
        finally
        {
            session.Changed -= Count;
        }
    });

    [RhinoFact]
    public void TheHudLaysOutEveryModeAndThePanelPaints() => host.Run(() =>
    {
        PlugInLoader.Load();
        var pair = FixturePair.Create("review-hud", BuildModel, b =>
        {
            Model.Transform(b, "wall", Transform.Translation(500, 0, 0));
            Model.Transform(b, "column", Transform.Translation(0.05, 0, 0));
        });
        using var doc = RhinoDoc.CreateHeadless(null);
        var session = ReviewSession.Current;
        DittoReviewCommand.Review(new ReviewSource(doc.RuntimeSerialNumber, pair.A, pair.B));
        session.SetGate("Structure::*");
        var review = session.Review!;
        static SizeF Measure(string text, int height) => new(text.Length * height * 0.55f, height * 1.3f);
        var size = new Size(1200, 800);
        var nudge = review.Rows.Single(r => r.Ring);
        var anchors = review.Rows.Select((r, i) => new Hud.Anchor(r, new PointF(300 + 100 * i, 400))).ToList();

        var tags = Hud.Layout(review, null, HudMode.CardAndTags, size, Measure, anchors);
        var texts = tags.OfType<HudText>().Select(t => t.Text).ToList();
        Assert.Contains(nudge.Tag, texts); // it needs a look
        Assert.DoesNotContain(review.Rows.Single(r => !r.Ring).Tag, texts); // not selected, nothing to look at
        Assert.Contains(tags.OfType<HudBox>(), b => b.Radius == 18 && b.Stroke == DiffScene.Modified); // the nudge's ring
        Assert.Contains(texts, t => t.StartsWith("Gate Structure::*: FAIL", StringComparison.Ordinal));
        Assert.Contains("2 modified", texts);
        Assert.Equal(review.Sentence, string.Join(" ", Hud.Wrap(review.Sentence, 310, 14, Measure)));

        var selected = review.Rows.Single(r => !r.Ring);
        var withSelection = Hud.Layout(review, selected, HudMode.CardAndTags, size, Measure, anchors).OfType<HudText>().Select(t => t.Text).ToList();
        Assert.Contains(selected.Tag, withSelection);
        Assert.Contains("Showing: " + selected.Name, withSelection);
        Assert.DoesNotContain(Hud.Layout(review, null, HudMode.Card, size, Measure, anchors).OfType<HudText>(), t => t.Text == nudge.Tag);
        var strip = Hud.Layout(review, null, HudMode.Strip, size, Measure, anchors);
        Assert.All(strip.OfType<HudText>(), t => Assert.True(t.At.Y > 700, "the strip sits at the bottom"));
        Assert.Contains(strip.OfType<HudText>(), t => t.Text == "Gate fails");
        Assert.Empty(Hud.Layout(review, null, HudMode.Off, size, Measure, anchors));

        // The conduit's layout projects each tagged row through a real viewport.
        using var viewport = new global::Rhino.Display.RhinoViewport();
        viewport.Size = size;
        viewport.ZoomBoundingBox(new BoundingBox(new Point3d(-1000, -1000, -1000), new Point3d(9000, 9000, 4000)));
        var shapes = ReviewConduit.Layout(viewport, Measure, size)!;
        Assert.Contains(shapes.OfType<HudText>(), t => t.Text == nudge.Tag);

        // The panel builds on the session, and its painter lays out and paints the list, the card and the Unchanged
        // tab. (WPF doesn't render in this in-process host, so the pixels can't be checked; the painting code runs.)
        session.Select(review.Rows.ToList().IndexOf(selected));
        var panel = new ReviewPanel();
        using var painter = new ReviewPainter();
        using var bitmap = new Eto.Drawing.Bitmap(390, 900, Eto.Drawing.PixelFormat.Format32bppRgba);
        using (var g = new Eto.Drawing.Graphics(bitmap))
        {
            var hits = new List<ReviewPainter.Hit>();
            Assert.True(painter.Rows(g, review, 390, session.Selected, 0, hits) > 60);
            Assert.Equal(review.Rows.Count, hits.Count);
            Assert.True(painter.Card(g, review, session.SelectedRow, 390) > painter.Card(null, review, null, 390));
            Assert.True(painter.Unchanged(g, review, 390) > 30);
        }
        Assert.True(panel.Key(Eto.Forms.Keys.J));
        Assert.False(panel.Key(Eto.Forms.Keys.A));
    });

    private static void Move(RhinoDoc doc, string truth, double dx)
    {
        foreach (var o in doc.Objects)
            if (o.Attributes.GetUserString(Model.TruthKey) == truth)
            {
                doc.Objects.Transform(o, Transform.Translation(dx, 0, 0), true);
                return;
            }
        throw new InvalidOperationException($"No {truth}.");
    }

    /// <summary>A column on Structure::Columns, a slab on Structure and a wall on Walls, named so they match by key.</summary>
    private static void BuildModel(File3dm f)
    {
        Model.Setup(f);
        int structure = Model.Layer(f, "Structure");
        int columns = Model.Layer(f, "Columns", parent: structure);
        int walls = Model.Layer(f, "Walls");
        Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(8000, 8000, 200))), structure, "slab", "S-01");
        Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(300, 300, 3000))), columns, "column", "C-01");
        Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(0, 1000, 0), new Point3d(4000, 1200, 3000))), walls, "wall", "W-01");
    }
}
