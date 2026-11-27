using System.Diagnostics;
using System.Drawing;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;
using RhinoDoc = Rhino.RhinoDoc;

namespace DittoHopper.Rhino.Tests;

/// <summary>Skipped unless DITTOHOPPER_RHINO_TESTS=1 and DITTOHOPPER_NOISE_REPORT=1, so the hook never runs it.</summary>
public sealed class NoiseFactAttribute : FactAttribute
{
    public NoiseFactAttribute()
    {
        if (!RhinoHost.Enabled || System.Environment.GetEnvironmentVariable("DITTOHOPPER_NOISE_REPORT") != "1")
            Skip = "Noise report: set DITTOHOPPER_RHINO_TESTS=1 and DITTOHOPPER_NOISE_REPORT=1 (see README.md).";
    }
}

/// <summary>
/// Realistic noise on generated building models (a few thousand objects, nested layers and blocks, groups,
/// user text): reported rows against true changes, wrong pairings, and timings, printed for each pair. It
/// reports rather than gates; nothing is written to the repository.
/// </summary>
[Collection("Rhino")]
[Trait("Category", "NoiseReport")]
public sealed class NoiseReportTests(RhinoHost host, ITestOutputHelper output)
{
    private const int Levels = 10;
    private const double Storey = 4000;

    [NoiseFact]
    public void Report() => host.Run(() =>
    {
        output.WriteLine("| pair | objects A / B | true changes | rows | noise | wrong pairings | snapshot A | snapshot B | diff |");
        output.WriteLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");

        Measure("(a) consultant revision", FixturePair.Create("noise-consultant", Building, ConsultantRevision), trueChanges: 6);
        Measure("(b) GH re-bake of every Facade::Panels layer", FixturePair.Create("noise-rebake", Building, b => Rebake(b, "Panels", jitter: 0)), 0);
        Measure("(b') same, geometry jittered 1e-6 mm", FixturePair.Create("noise-rebake-jitter", Building, b => Rebake(b, "Panels", jitter: 1e-6)), 0);
        Measure("(b'') same, geometry jittered 1e-4 mm", FixturePair.Create("noise-rebake-jitter4", Building, b => Rebake(b, "Panels", jitter: 1e-4)), 0);
        Measure("(d) L03 columns converted to Breps in place, GUIDs kept", FixturePair.Create("noise-convert", Building, ConvertInPlace), 0);

        var roundTrip = FixturePair.Create("noise-roundtrip", Building, _ => { });
        using var doc = RhinoDoc.OpenHeadless(roundTrip.A) ?? throw new InvalidOperationException("Could not open A.");
        string saved = Path.Combine(Path.GetDirectoryName(roundTrip.A)!, "B-saved.3dm");
        Assert.True(doc.Write3dmFile(saved, new FileWriteOptions()));
        Measure("(c) open in Rhino, save, no edits", roundTrip with { B = saved }, 0);
        MeasureDoc("(c') open in Rhino vs its file", roundTrip.A, doc);
    });

    private void Measure(string name, FixturePair pair, int trueChanges)
    {
        var clock = Stopwatch.StartNew();
        var a = RhinoSnapshot.FromFile(pair.A);
        var snapA = clock.Elapsed;
        clock.Restart();
        var b = RhinoSnapshot.FromFile(pair.B);
        Print(name, a, b, snapA, clock.Elapsed, trueChanges);
    }

    private void MeasureDoc(string name, string file, RhinoDoc doc)
    {
        var clock = Stopwatch.StartNew();
        var a = RhinoSnapshot.FromFile(file);
        var snapA = clock.Elapsed;
        clock.Restart();
        var b = RhinoSnapshot.FromDocument(doc);
        Print(name, a, b, snapA, clock.Elapsed, 0);
    }

    private void Print(string name, RhinoSnapshot a, RhinoSnapshot b, TimeSpan snapA, TimeSpan snapB, int trueChanges)
    {
        var run = Fixture.Diff(a, b, snapA + snapB);
        output.WriteLine($"| {name} | {a.Model.Objects.Count} / {b.Model.Objects.Count} | {trueChanges} | {run.Rows} | {run.Noise(trueChanges)} | " +
            $"{run.WrongPairings} | {snapA.TotalMilliseconds:F0} ms | {snapB.TotalMilliseconds:F0} ms | {run.DiffTime.TotalMilliseconds:F0} ms |");
        foreach (var e in run.Result.TableEvents)
            output.WriteLine($"    event: {e.Table} {e.Kind} {e.Label} {e.Property} {e.From} → {e.To}");
        foreach (var s in run.Result.ChangeSets)
            output.WriteLine($"    set: {s.Kind} {s.Layer} {s.Change?.Property} {s.Change?.Key} {s.Change?.From} → {s.Change?.To} ×{s.Objects.Count}");
        foreach (var g in run.Result.Geometry.GroupBy(g => (g.Level, g.Detail)))
            output.WriteLine($"    geometry: {g.Key.Level} {g.Key.Detail} ×{g.Count()}");
        output.WriteLine("    matched by: " + string.Join(", ", run.Result.Matches.GroupBy(m => m.By).Select(g => $"{g.Key} {g.Count()}")));
    }

    /// <summary>Ten storeys: columns grouped with base plates, slabs, walls, facade panels and mullions, windows
    /// and workstations as (nested) blocks, annotation; plus site terrain and a grid. About 3,000 objects.</summary>
    private static void Building(File3dm f)
    {
        Model.Setup(f);
        var random = new Random(20261128);
        int site = Model.Layer(f, "Site", Color.OliveDrab), grid = Model.Layer(f, "Grid", Color.Gray), blocks = Model.Layer(f, "Blocks");
        var member = new ObjectAttributes { LayerIndex = blocks };

        int mullion = Model.Block(f, "Mullion", [new LineCurve(Point3d.Origin, new Point3d(0, 0, 1500))], [member]);
        int window = Model.Block(f, "Window",
            [new PolylineCurve([new Point3d(0, 0, 0), new Point3d(1200, 0, 0), new Point3d(1200, 0, 1500), new Point3d(0, 0, 1500), new Point3d(0, 0, 0)]),
             new PlaneSurface(new Plane(Point3d.Origin, Vector3d.XAxis, Vector3d.ZAxis), new Interval(0, 1200), new Interval(0, 1500)),
             new InstanceReferenceGeometry(IdefId(f, mullion), Transform.Translation(400, 0, 0)),
             new InstanceReferenceGeometry(IdefId(f, mullion), Transform.Translation(800, 0, 0))],
            [member, member, member, member]);
        int desk = Model.Block(f, "Desk", [Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(1600, 800, 740)))], [member]);
        int chair = Model.Block(f, "Chair", [Brep.CreateFromBox(new BoundingBox(Point3d.Origin, new Point3d(500, 500, 450)))], [member]);
        int workstation = Model.Block(f, "Workstation",
            [new InstanceReferenceGeometry(IdefId(f, desk), Transform.Identity), new InstanceReferenceGeometry(IdefId(f, chair), Transform.Translation(550, 900, 0))],
            [member, member]);

        var terrain = Mesh.CreateFromPlane(new Plane(new Point3d(-20000, -20000, -500), Vector3d.ZAxis), new Interval(0, 100000), new Interval(0, 80000), 50, 50);
        for (int i = 0; i < terrain.Vertices.Count; i++)
        {
            var p = terrain.Vertices.Point3dAt(i);
            terrain.Vertices.SetVertex(i, p.X, p.Y, p.Z + 300 * Math.Sin(p.X / 9000) * Math.Cos(p.Y / 7000));
        }
        Model.Add(f, terrain, site, "terrain");
        for (int i = 0; i < 8; i++) Model.Add(f, new LineCurve(new Point3d(i * 7500, -2000, 0), new Point3d(i * 7500, 32000, 0)), grid, $"grid-x{i}");
        for (int j = 0; j < 6; j++) Model.Add(f, new LineCurve(new Point3d(-2000, j * 6000, 0), new Point3d(54500, j * 6000, 0)), grid, $"grid-y{j}");

        for (int level = 0; level < Levels; level++)
        {
            double z = level * Storey;
            string l = $"L{level:00}";
            int root = Model.Layer(f, l);
            int structure = Model.Layer(f, "Structure", parent: root);
            int columns = Model.Layer(f, "Columns", parent: structure), slabs = Model.Layer(f, "Slabs", parent: structure);
            int walls = Model.Layer(f, "Walls", Color.Firebrick, root);
            int facade = Model.Layer(f, "Facade", parent: root);
            int panels = Model.Layer(f, "Panels", Color.SteelBlue, facade), mullions = Model.Layer(f, "Mullions", parent: facade);
            int windows = Model.Layer(f, "Windows", parent: facade);
            int furniture = Model.Layer(f, "Furniture", parent: root), notes = Model.Layer(f, "Annotation", parent: root);

            int n = 0;
            for (int i = 0; i < 8; i++)
                for (int j = 0; j < 6; j++, n++)
                {
                    double x = i * 7500, y = j * 6000;
                    int group = f.AllGroups.AddGroup();
                    var column = Model.Attr(columns, $"column-{level}-{n}");
                    column.SetUserString("mark", $"C-{level}-{n}");
                    column.SetUserString("fire", "R60");
                    column.AddToGroup(group);
                    f.Objects.AddExtrusion(Extrusion.Create(Rectangle(x - 200, y - 200, z, 400, 400), Storey - 250, true), column);
                    var plate = Model.Attr(columns, $"plate-{level}-{n}");
                    plate.AddToGroup(group);
                    f.Objects.AddBrep(Brep.CreateFromBox(new BoundingBox(new Point3d(x - 400, y - 400, z), new Point3d(x + 400, y + 400, z + 20))), plate);
                }
            for (int s = 0; s < 4; s++)
                Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(s * 13125, 0, z - 250), new Point3d(s * 13125 + 13125, 30000, z))), slabs, $"slab-{level}-{s}");
            for (int w = 0; w < 20; w++)
            {
                double x = random.Next(0, 50000), y = random.Next(0, 28000);
                Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(x, y, z), new Point3d(x + 3000 + random.Next(0, 4000), y + 200, z + 3500))), walls, $"wall-{level}-{w}");
            }
            for (int p = 0; p < 60; p++)
            {
                double x = p * 875;
                var panel = Model.Attr(panels, $"panel-{level}-{p}");
                panel.SetUserString("panel_type", p % 3 == 0 ? "P-B" : "P-A");
                f.Objects.AddBrep(Brep.CreateFromCornerPoints(new Point3d(x, -300, z), new Point3d(x + 875, -300, z),
                    new Point3d(x + 875, -300, z + Storey), new Point3d(x, -300, z + Storey), Model.Tolerance), panel);
                Model.Add(f, new LineCurve(new Point3d(x, -350, z), new Point3d(x, -350, z + Storey)), mullions, $"mullion-{level}-{p}");
            }
            for (int w = 0; w < 30; w++)
                f.Objects.AddInstanceObject(window, Transform.Translation(w * 1750 + 200, -300, z + 900), Model.Attr(windows, $"window-{level}-{w}"));
            for (int d = 0; d < 20; d++)
                f.Objects.AddInstanceObject(workstation,
                    Transform.Translation(2000 + (d % 5) * 4000, 3000 + (d / 5) * 3000, z) * Transform.Rotation(d % 2 * Math.PI / 2, Point3d.Origin),
                    Model.Attr(furniture, $"workstation-{level}-{d}"));
            f.Objects.AddText($"Level {level:00}", new Plane(new Point3d(-1500, -1500, z), Vector3d.ZAxis), 350, "Arial", false, false, Model.Attr(notes, $"title-{level}"));
            for (int r = 0; r < 5; r++)
                Model.Add(f, new TextDot($"Room {level}{r:00}", new Point3d(5000 + r * 9000, 15000, z + 100)), notes, $"room-{level}-{r}");
            Model.Add(f, Hatch.Create(new ArcCurve(new Circle(new Point3d(52000, 28000, z), 900)), 0, 0, 1, Model.Tolerance)[0], notes, $"core-{level}");
        }
        Survey(f);
    }

    /// <summary>
    /// Six intended edits, plus a partial rebuild that loses GUIDs: L04's panels and mullions re-created as
    /// they were, and L04's columns re-created as Breps (exported through another tool).
    /// </summary>
    private static void ConsultantRevision(File3dm b)
    {
        foreach (int n in new[] { 10, 11, 12 }) Model.Transform(b, $"column-5-{n}", Transform.Translation(50, 0, 0));
        LayerAt(b, "L02::Walls").Color = Color.Red;
        LayerAt(b, "L09::Furniture").Name = "FF&E";
        b.Objects.Delete(Enumerable.Range(0, 5).Select(w => Model.Find(b, $"window-7-{w}").Id).ToList());
        int walls = LayerAt(b, "L08::Walls").Index;
        for (int w = 0; w < 4; w++)
            Model.Add(b, Brep.CreateFromBox(new BoundingBox(new Point3d(w * 5000 + 40000, 70000, 32000), new Point3d(w * 5000 + 44000, 70200, 35500))), walls, $"new-wall-{w}");
        foreach (int n in new[] { 3, 4 }) Model.Find(b, $"column-1-{n}").Attributes.SetUserString("fire", "R90");

        Recreate(b, o => LayerOf(b, o) is "L04::Facade::Panels" or "L04::Facade::Mullions", g => g);
        Recreate(b, o => LayerOf(b, o) == "L04::Structure::Columns" && o.Geometry is Extrusion, g => ((Extrusion)g).ToBrep(true));
    }

    /// <summary>
    /// Places the model in survey coordinates (an odd offset and a 17.3° project north), so coordinates are
    /// arbitrary decimals, as in real projects, rather than round numbers far from any hash-grid boundary.
    /// </summary>
    private static void Survey(File3dm f)
    {
        var world = Transform.Translation(31234.567, 52345.678, 12.345) * Transform.Rotation(17.3 * Math.PI / 180, Point3d.Origin);
        var all = Model.All(f).Select(o => (Geometry: o.Geometry.Duplicate(), Attributes: o.Attributes.Duplicate(), o.Id)).ToList();
        f.Objects.Delete(all.Select(o => o.Id).ToList());
        foreach (var (geometry, attributes, id) in all)
        {
            geometry.Transform(world);
            attributes.ObjectId = id;
            Model.Add(f, geometry, attributes);
        }
    }

    /// <summary>ConvertExtrusion: the same objects, now Breps, keeping their GUIDs.</summary>
    private static void ConvertInPlace(File3dm b)
    {
        foreach (var o in Model.All(b).Where(o => LayerOf(b, o) == "L03::Structure::Columns" && o.Geometry is Extrusion))
            Model.Replace(b, o.Attributes.GetUserString(Model.TruthKey), ((Extrusion)o.Geometry).ToBrep(true));
    }

    /// <summary>A Grasshopper re-bake: every object on the named leaf layers deleted and baked again, in reverse order.</summary>
    private static void Rebake(File3dm b, string leaf, double jitter) =>
        Recreate(b, o => LayerOf(b, o).EndsWith("::" + leaf, StringComparison.Ordinal), g =>
        {
            if (jitter != 0) g.Translate(jitter, -jitter, jitter);
            return g;
        });

    /// <summary>Deletes matching objects and adds them again with new GUIDs (same attributes otherwise), last first.</summary>
    private static void Recreate(File3dm b, Func<File3dmObject, bool> which, Func<GeometryBase, GeometryBase> rebuild)
    {
        var chosen = Model.All(b).Where(which).Select(o => (Geometry: o.Geometry.Duplicate(), Attributes: o.Attributes.Duplicate(), o.Id)).ToList();
        b.Objects.Delete(chosen.Select(c => c.Id).ToList());
        foreach (var (geometry, attributes, _) in Enumerable.Reverse(chosen))
        {
            attributes.ObjectId = Guid.Empty;
            Model.Add(b, rebuild(geometry), attributes);
        }
    }

    private static string LayerOf(File3dm f, File3dmObject o) => f.AllLayers.FindIndex(o.Attributes.LayerIndex).FullPath;

    private static Layer LayerAt(File3dm f, string path)
    {
        foreach (var l in f.AllLayers) if (l.FullPath == path) return l;
        throw new InvalidOperationException($"No layer '{path}'.");
    }

    private static Guid IdefId(File3dm f, int index)
    {
        foreach (var d in f.AllInstanceDefinitions) if (d.Index == index) return d.Id;
        throw new InvalidOperationException($"No block {index}.");
    }

    private static PolylineCurve Rectangle(double x, double y, double z, double w, double h) =>
        new([new Point3d(x, y, z), new Point3d(x + w, y, z), new Point3d(x + w, y + h, z), new Point3d(x, y + h, z), new Point3d(x, y, z)]);
}
