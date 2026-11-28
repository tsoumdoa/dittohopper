using System.Drawing;
using DittoHopper.PlugIn;
using DittoHopper.Rhino.Tests;
using Rhino;
using Rhino.Display;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace DittoHopper.Grasshopper.Tests;

/// <summary>Runs only with DITTOHOPPER_RHINO_TESTS=1 and DITTOHOPPER_SCREENSHOTS set to an output folder.</summary>
public sealed class ScreenshotFactAttribute : FactAttribute
{
    public ScreenshotFactAttribute()
    {
        if (!RhinoHost.Enabled || RhinoHost.ScreenshotFolder is null)
            Skip = "Screenshots: set DITTOHOPPER_RHINO_TESTS=1 and DITTOHOPPER_SCREENSHOTS to a folder (see README.md).";
    }
}

/// <summary>
/// Evidence, not a gate: the mock's revision reviewed in a hidden-window Rhino, the viewport captured with the review
/// conduit drawing, and what the panel shows written out as text (WPF, and so the Eto panel, doesn't render in this
/// in-process host). One view capture per run: a second one crashed Rhino natively.
/// DITTOHOPPER_SCREENSHOT_SELECT names a row to select; DITTOHOPPER_SCREENSHOT_HUD picks the HUD mode.
/// </summary>
[Collection("Rhino")]
[Trait("Category", "Screenshots")]
public sealed class ScreenshotTests(RhinoHost host, ITestOutputHelper output)
{
    [ScreenshotFact]
    public void CaptureTheReviewOfTheMocksRevision() => host.Run(() =>
    {
        PlugInLoader.Load();
        string folder = Directory.CreateDirectory(RhinoHost.ScreenshotFolder!).FullName;
        string select = System.Environment.GetEnvironmentVariable("DITTOHOPPER_SCREENSHOT_SELECT") ?? "";
        var mode = Enum.TryParse<HudMode>(System.Environment.GetEnvironmentVariable("DITTOHOPPER_SCREENSHOT_HUD"), out var m) ? m : HudMode.CardAndTags;
        string name = $"review-{mode}{(select.Length > 0 ? "-" + select : "")}".ToLowerInvariant();

        // A is the saved revision; B is the open document, edited in place.
        var pair = FixturePair.Create("screenshot", Revision, _ => { });
        var doc = RhinoDoc.Open(pair.A, out _) ?? throw new InvalidOperationException("Could not open A.");
        Edit(doc);

        var session = ReviewSession.Current;
        session.SetGate("Structure::*");
        DittoReviewCommand.Review(new ReviewSource(doc.RuntimeSerialNumber, null, null));
        session.SetScope("Facade::*");
        session.SetMode(mode);
        if (select.Length > 0) session.Select(session.Review!.Rows.ToList().FindIndex(r => r.Name == select));
        foreach (var row in session.Review!.Rows) output.WriteLine($"[{row.Group}] {row.Name} · {row.Description} · {string.Join(", ", row.Flags)}");
        output.WriteLine(session.Review.Sentence);

        File.WriteAllText(Path.Combine(folder, name + "-panel.txt"), PanelText(session));

        var view = doc.Views.Find("Perspective", false) ?? doc.Views.ActiveView ?? throw new InvalidOperationException("No view.");
        doc.Views.ActiveView = view;
        view.Maximized = true;
        view.ActiveViewport.DisplayMode = DisplayModeDescription.FindByName("Shaded") ?? view.ActiveViewport.DisplayMode;
        var box = BoundingBox.Empty;
        foreach (var item in session.Scene) box.Union(item.Geometry.GetBoundingBox(item.Xform));
        view.ActiveViewport.ZoomBoundingBox(box);
        view.Redraw();
        var capture = new ViewCapture { Width = 1400, Height = 860, ScaleScreenItems = false, DrawAxes = false, DrawGrid = true, TransparentBackground = false };
        using var shot = capture.CaptureToBitmap(view) ?? throw new InvalidOperationException("View capture returned nothing.");
        string path = Path.Combine(folder, name + "-viewport.png");
        shot.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        output.WriteLine(path);
        // The shutdown of a windowed Rhino can hang, so don't leave the plug-in registered until then.
        PlugInLoader.Forget();
    });

    /// <summary>The panel's content, as the shared review model gives it to the panel.</summary>
    private static string PanelText(ReviewSession session)
    {
        var r = session.Review!;
        var lines = new List<string> { r.Header, $"Scope: {session.Scope} · Gate: {session.Gate} " + (r.Gate is { } g ? (g.Pass ? "passes" : $"fails ({g.Why.Count})") : ""), r.Sentence, "" };
        lines.Add($"== Changes ({r.Rows.Count})");
        foreach (var group in r.Groups)
        {
            lines.Add($"-- {group.Label} ({group.Rows.Count})");
            foreach (var row in group.Rows)
                lines.Add($"  [{row.Status}] {row.Name} [{row.Badge}] · {row.Description}" + (row.Flags.Count > 0 ? $" · flags: {string.Join(", ", row.Flags)}" : ""));
        }
        if (session.SelectedRow is { } selected)
        {
            var card = r.Card(selected);
            lines.Add("");
            lines.Add($"== Card: {card.Status} · {card.Title} [{card.Badge}] · {card.Meta}");
            lines.Add($"Changed · {card.Changed.Count}");
            lines.AddRange(card.Changed.Select(c => $"  {c.Property}: {c.From ?? "—"} → {c.To ?? "—"}"));
            lines.AddRange(card.Why.Select(w => "  why: " + w));
            lines.Add($"Unchanged · {card.Unchanged.Count}: {string.Join(", ", card.Unchanged)}");
        }
        lines.Add("");
        lines.Add($"== Unchanged ({r.Run.Result.UnchangedObjects})");
        lines.Add(r.UnchangedSummary);
        foreach (var l in r.Unchanged)
            lines.Add($"  {l.Path}: {l.Identical} of {l.Objects} · {l.Names}" + (l.ThroughTables > 0 ? $" · {l.ThroughTables} look different through a table entry" : ""));
        lines.Add("");
        lines.Add("Strip: " + r.Strip);
        return string.Join(System.Environment.NewLine, lines);
    }

    /// <summary>The mock's 42-object model: slab, 15 columns, 16 facade panels, 5 grid lines.</summary>
    private static void Revision(File3dm f)
    {
        Model.Setup(f);
        int structure = Model.Layer(f, "Structure");
        int columns = Model.Layer(f, "Columns", parent: structure);
        int slabs = Model.Layer(f, "Slabs", parent: structure);
        int panels = Model.Layer(f, "Panels", parent: Model.Layer(f, "Facade"));
        int grid = Model.Layer(f, "Grid", Color.FromArgb(43, 48, 56));
        Model.Layer(f, "Default");
        Model.Add(f, Box(-400, -400, -250, 24400, 12400, 0), slabs, "slab", "Slab L1");
        int n = 0;
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 5; i++)
            {
                double x = i * 6000, y = j * 6000;
                string id = $"C-{++n:00}";
                Model.Add(f, Box(x - 200, y - 200, 0, x + 200, y + 200, 3500), columns, id, id);
            }
        for (int k = 0; k < 8; k++)
        {
            Model.Add(f, Box(k * 3000, -300, 0, k * 3000 + 3000, -100, 3500), panels, $"FP-S{k + 1:00}", $"FP-S{k + 1:00}");
            Model.Add(f, Box(k * 3000, 12100, 0, k * 3000 + 3000, 12300, 3500), panels, $"FP-N{k + 1:00}", $"FP-N{k + 1:00}");
        }
        for (int i = 0; i < 5; i++)
            Model.Add(f, new LineCurve(new Point3d(i * 6000, -600, 0), new Point3d(i * 6000, 12600, 0)), grid, $"Grid-{"ABCDE"[i]}", $"Grid-{"ABCDE"[i]}");
    }

    /// <summary>The mock's edits, made in the open document.</summary>
    private static void Edit(RhinoDoc doc)
    {
        RhinoObject Find(string truth) => doc.Objects.First(o => o.Attributes.GetUserString(Model.TruthKey) == truth);
        var panels = doc.Layers.FindByFullPath("Facade::Panels", -1);
        var layer = doc.Layers[panels];
        layer.Color = Color.SteelBlue;
        doc.Layers.Modify(layer, panels, true);
        doc.Objects.Transform(Find("FP-S04"), Transform.Translation(0, -150, 0), true);
        doc.Objects.Replace(Find("FP-S07").Id, Box(18000, -300, 0, 21000, -100, 4200));
        var n06 = Find("FP-N06");
        var attributes = n06.Attributes;
        doc.Objects.Delete(n06, true);
        foreach (var (label, x0, x1) in new[] { ("FP-N06a", 15000, 16500), ("FP-N06b", 16500, 18000) })
        {
            var a = attributes.Duplicate();
            a.ObjectId = Guid.Empty;
            a.Name = label;
            a.SetUserString(Model.TruthKey, label);
            doc.Objects.AddBrep(Box(x0, 12100, 0, x1, 12300, 3500), a);
        }
        var gridC = Find("Grid-C");
        var moved = gridC.Attributes.Duplicate();
        moved.LayerIndex = doc.Layers.FindByFullPath("Default", -1);
        doc.Objects.ModifyAttributes(gridC, moved, true);
        doc.Objects.Transform(Find("C-08"), Transform.Translation(0.04, 0, 0), true);
    }

    private static Brep Box(double x0, double y0, double z0, double x1, double y1, double z1) =>
        Brep.CreateFromBox(new BoundingBox(new Point3d(x0, y0, z0), new Point3d(x1, y1, z1)));
}
