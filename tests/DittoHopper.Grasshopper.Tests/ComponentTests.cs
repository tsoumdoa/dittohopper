using System.Drawing;
using DittoHopper.Rhino;
using DittoHopper.Rhino.Tests;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace DittoHopper.Grasshopper.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class ComponentTests(RhinoHost host, ITestOutputHelper output)
{
    /// <summary>Component GUIDs are stable forever: renaming a class must not change them.</summary>
    private static readonly Dictionary<string, (Guid Id, string Panel)> Expected = new()
    {
        ["Snapshot"] = (new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a01"), "Diff"),
        ["Diff Settings"] = (new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a02"), "Diff"),
        ["Model Diff"] = (new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a03"), "Diff"),
        ["Diff Filter"] = (new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a04"), "Review"),
        ["Diff Preview"] = (new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a05"), "Review"),
        ["Assert Unchanged"] = (new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a06"), "Review"),
        ["Stamp IDs"] = (new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a07"), "Write"),
        ["Open Review"] = (new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a08"), "Review"),
    };

    [RhinoFact]
    public void EveryComponentLoadsWithAnIconOnTheDittoHopperTab() => host.Run(() =>
    {
        Gh.Load();
        var types = typeof(AssemblyInfo).Assembly.GetTypes()
            .Where(t => typeof(GH_Component).IsAssignableFrom(t) && !t.IsAbstract).ToList();
        var components = types.Select(t => (GH_Component)Activator.CreateInstance(t)!).ToList();

        Assert.Equal(Expected.Keys.Order(), components.Select(c => c.Name).Order());
        foreach (var c in components)
        {
            output.WriteLine($"{c.Name} ({c.NickName}) · {c.SubCategory} · in: {Ports(c.Params.Input)} · out: {Ports(c.Params.Output)}");
            Assert.Equal(Expected[c.Name].Id, c.ComponentGuid);
            Assert.Equal("DittoHopper", c.Category);
            Assert.Equal(Expected[c.Name].Panel, c.SubCategory);
            Assert.False(string.IsNullOrWhiteSpace(c.Description));
            Assert.Equal(new Size(24, 24), c.Icon_24x24.Size);
            foreach (var p in c.Params.Input.Concat(c.Params.Output))
            {
                Assert.False(string.IsNullOrWhiteSpace(p.NickName));
                Assert.False(string.IsNullOrWhiteSpace(p.Description));
            }
            Assert.Equal(c.Params.Input.Count, c.Params.Input.Select(p => p.NickName).Distinct().Count());
        }
        var info = new AssemblyInfo();
        Assert.Equal(new Guid("e59805d1-fcad-4d4f-b916-57b439bf9d91"), info.Id);
        Assert.Equal(new Size(24, 24), info.Icon.Size);
        Assert.Equal(GH_LoadingInstruction.Proceed, new DittoHopperPriority().PriorityLoad());
    });

    /// <summary>Snapshot A and B → Model Diff → Diff Filter, Diff Preview and two Assert Unchanged, solved.</summary>
    private sealed record Definition(GH_Document Doc, SnapshotComponent A, SnapshotComponent B, ModelDiffComponent Diff,
        DiffFilterComponent Filter, DiffPreviewComponent Preview, AssertUnchangedComponent Gate, AssertUnchangedComponent Whole)
    {
        public ResultGoo Result => (ResultGoo)Assert.Single(Gh.Out(Diff, 4));
        public bool Pass(AssertUnchangedComponent gate) => ((GH_Boolean)Assert.Single(Gh.Out(gate, 0))).Value;
        public Dictionary<DiffScene.Status, int> Statuses =>
            Preview.Items.GroupBy(i => i.Status).ToDictionary(g => g.Key, g => g.Count());
    }

    private static Definition Solve(FixturePair pair, string? kind = null, string? gate = null)
    {
        var doc = new GH_Document { Enabled = true };
        var d = new Definition(doc, Gh.Add<SnapshotComponent>(doc), Gh.Add<SnapshotComponent>(doc), Gh.Add<ModelDiffComponent>(doc),
            Gh.Add<DiffFilterComponent>(doc), Gh.Add<DiffPreviewComponent>(doc), Gh.Add<AssertUnchangedComponent>(doc), Gh.Add<AssertUnchangedComponent>(doc));
        Gh.Set(d.A.Params.Input[0], new GH_String(pair.A));
        Gh.Set(d.B.Params.Input[0], new GH_String(pair.B));
        Gh.Wire(d.A.Params.Output[0], d.Diff.Params.Input[0]);
        Gh.Wire(d.B.Params.Output[0], d.Diff.Params.Input[1]);
        foreach (var c in new GH_Component[] { d.Filter, d.Preview, d.Gate, d.Whole })
            Gh.Wire(d.Diff.Params.Output[4], c.Params.Input[0]);
        if (kind is not null) Gh.Set(d.Filter.Params.Input[1], new GH_String(kind));
        if (gate is not null) Gh.Set(d.Gate.Params.Input[1], new GH_String(gate));
        Gh.Solve(doc);
        return d;
    }

    private static void SetGate(Definition d, string pattern)
    {
        Gh.Set(d.Gate.Params.Input[1], new GH_String(pattern));
        Gh.Solve(d.Doc);
    }

    /// <summary>Millimetres in the preview's units: the active document's, or A's (millimetres) without one.</summary>
    private static double MillimetresToDoc() =>
        RhinoDoc.ActiveDoc is { } doc ? RhinoMath.UnitScale(UnitSystem.Millimeters, doc.ModelUnitSystem) : 1;

    [RhinoFact]
    public void SnapshotDiffFilterAndAssertSolveOnAFixturePair() => host.Run(() =>
    {
        Gh.Load();
        var pair = FixturePair.Create("gh-definition", BuildModel, b =>
        {
            Model.Transform(b, "wall", Transform.Translation(500, 0, 0));
            Model.Add(b, new LineCurve(new Point3d(0, 1000, 0), new Point3d(4000, 1000, 0)), LayerIndex(b, "Walls"), "new-line");
        });
        var d = Solve(pair, kind: "moved", gate: "Structure::*");
        using var doc = d.Doc;

        foreach (var c in new GH_Component[] { d.A, d.B, d.Diff, d.Filter, d.Gate, d.Whole, d.Preview })
            Assert.True(Gh.Errors(c).Length == 0, $"{c.Name}: {Gh.Errors(c)}"); // remarks (header notes) are fine
        Assert.Equal("A.3dm", d.A.Message);

        string report = d.Result.ToString();
        output.WriteLine(report);
        Assert.StartsWith("2 of 3 objects are identical.", report);
        Assert.Contains("2 change sets · 0 table events", report);
        Assert.Contains("Layers: ", report);
        Assert.Contains("shape tolerance 0.01 (auto)", report);
        Assert.Contains("Matched 3 by key", report); // names unique in both models: pass 1
        Assert.Contains("• 1 object on Walls: moved 500 mm in X", report);
        Assert.Contains("• 1 object added on Walls", report);
        Assert.Single(Gh.Out(d.Diff, 0)); // added line
        Assert.Empty(Gh.Out(d.Diff, 1));
        Assert.IsType<GH_Brep>(Assert.Single(Gh.Out(d.Diff, 2))); // moved wall
        Assert.Empty(Gh.Out(d.Diff, 3));
        Assert.Equal("+1 −0 ~1 · 0 events", d.Diff.Message);

        Assert.IsType<GH_Brep>(Assert.Single(Gh.Out(d.Filter, 0)));
        var info = Assert.Single(Gh.Texts(d.Filter, 1));
        output.WriteLine(info);
        Assert.Matches(@"^W-01 · moved 500 mm in X · Walls · \d+ properties unchanged · matched by key$", info);

        Assert.True(d.Pass(d.Gate));
        Assert.Equal(["Nothing on Structure::* changed: 1 object checked."], Gh.Texts(d.Gate, 1)); // the column; the slab is on Structure itself
        Assert.False(d.Pass(d.Whole));
        Assert.Equal(["1 object on Walls: moved 500 mm in X", "1 object added on Walls"], Gh.Texts(d.Whole, 1));

        var statuses = d.Statuses;
        Assert.Equal(2, statuses[DiffScene.Status.Unchanged]);
        Assert.Equal(1, statuses[DiffScene.Status.Added]);
        Assert.Equal(1, statuses[DiffScene.Status.Modified]);
        Assert.Equal(1, statuses[DiffScene.Status.Previous]);
        Assert.True(d.Preview.ClippingBox.IsValid);

        // A pattern that matches no layer fails the gate instead of passing it.
        SetGate(d, "Strcture::*");
        Assert.False(d.Pass(d.Gate));
        Assert.StartsWith("No layer in A or B matches Strcture::*", Assert.Single(Gh.Texts(d.Gate, 1)));
        Gh.Set(d.Gate.Params.Input[1], new GH_String("Walls"), new GH_String("Fascade"));
        Gh.Solve(d.Doc);
        Assert.False(d.Pass(d.Gate));
        Assert.StartsWith("No layer in A or B matches Fascade,", Assert.Single(Gh.Texts(d.Gate, 1)));

        Gh.Set(d.Filter.Params.Input[1], new GH_String("bogus"));
        Gh.Solve(d.Doc);
        Assert.Empty(Gh.Out(d.Filter, 0));
        Assert.Contains("Unknown kind bogus", Gh.Errors(d.Filter));
        Gh.Set(d.Filter.Params.Input[1], new GH_String("moved"));
        Gh.Solve(d.Doc);

        // Scope: what I meant to change. Nothing outside Walls changed.
        var settings = Gh.Add<DiffSettingsComponent>(d.Doc);
        Gh.Set(settings.Params.Input[3], new GH_String("Walls"));
        Gh.Wire(settings.Params.Output[0], d.Diff.Params.Input[2]);
        Gh.Solve(d.Doc);
        Assert.StartsWith("Nothing outside Walls changed.", d.Result.ToString());
        Gh.Set(settings.Params.Input[3], new GH_String("Structure::*"));
        Gh.Solve(d.Doc);
        output.WriteLine(d.Result.ToString());
        Assert.StartsWith("Outside Structure::*, 2 objects changed in 2 sets.", d.Result.ToString());
        Assert.Contains(Gh.Texts(d.Filter, 1), i => i.Contains("[flagged: Outside the declared scope]", StringComparison.Ordinal));
        d.Diff.Params.Input[2].RemoveAllSources();
        Gh.Solve(d.Doc);

        // Re-solving with the same file versions reuses the cached snapshots and the diff.
        var before = d.Result.Value;
        d.A.ExpireSolution(false);
        Gh.Solve(d.Doc);
        Assert.Same(before, d.Result.Value);
    });

    [RhinoFact]
    public void RenamingAParentLayerFailsAGateOnItsSublayersAndCountsOnlyInScopeObjects() => host.Run(() =>
    {
        Gh.Load();
        var pair = FixturePair.Create("gh-rename-parent", BuildModel, b =>
        {
            foreach (var l in b.AllLayers) if (l.Name == "Structure") l.Name = "Frame";
            // One move across two layers: one change set, one object of it on Walls.
            Model.Transform(b, "wall", Transform.Translation(0, 0, 100));
            Model.Transform(b, "column", Transform.Translation(0, 0, 100));
        });
        var d = Solve(pair, gate: "Structure::Columns");
        using var doc = d.Doc;
        output.WriteLine(d.Result.ToString());

        Assert.False(d.Pass(d.Gate));
        Assert.Contains(Gh.Texts(d.Gate, 1), w => w.StartsWith("Layer renamed: Structure → Frame", StringComparison.Ordinal));
        SetGate(d, "Frame::*");
        Assert.False(d.Pass(d.Gate));
        SetGate(d, "Walls");
        Assert.Equal(["1 object on Walls: moved 100 mm in Z"], Gh.Texts(d.Gate, 1));
        Assert.Equal(2, d.Statuses[DiffScene.Status.Modified]);
        Assert.Equal(1, d.Statuses[DiffScene.Status.Event]); // the slab on the renamed layer
    });

    [RhinoFact]
    public void MixedUnitsComeOutInAsUnits() => host.Run(() =>
    {
        Gh.Load();
        var pair = FixturePair.Create("gh-units", BuildModel, b =>
        {
            b.Settings.ModelUnitSystem = UnitSystem.Meters;
            b.Settings.ModelAbsoluteTolerance = 1e-5;
            foreach (var name in new[] { "slab", "column", "wall" })
                Model.Transform(b, name, Transform.Scale(Point3d.Origin, 0.001));
            Model.Transform(b, "wall", Transform.Translation(0.5, 0, 0));
        });
        // The preview's document is in metres, A in millimetres, B in metres.
        using var rhino = RhinoDoc.CreateHeadless(null);
        rhino.ModelUnitSystem = UnitSystem.Meters;
        var active = RhinoDoc.ActiveDoc;
        RhinoDoc.ActiveDoc = rhino;
        try { MixedUnits(pair); }
        finally { RhinoDoc.ActiveDoc = active; }
    });

    private void MixedUnits(FixturePair pair)
    {
        var d = Solve(pair, kind: "placement");
        using var doc = d.Doc;
        output.WriteLine(d.Result.ToString());
        Assert.Equal(0.001, MillimetresToDoc(), 9);

        Assert.Equal("+0 −0 ~1 · 0 events", d.Diff.Message);
        var wall = ((GH_Brep)Assert.Single(Gh.Out(d.Diff, 2))).Value.GetBoundingBox(true);
        Assert.Equal(0.5, wall.Min.X, 6); // in the document's metres, as the preview draws it
        Assert.Equal(4.5, wall.Max.X, 6);
        Assert.Contains(d.Diff.RuntimeMessages(GH_RuntimeMessageLevel.Remark),
            m => m.StartsWith("Geometry is in the Rhino document's units (Meters)", StringComparison.Ordinal));
        var filtered = ((GH_Brep)Assert.Single(Gh.Out(d.Filter, 0))).Value.GetBoundingBox(true);
        Assert.Equal(0.5, filtered.Min.X, 6);

        // The preview draws A and B in the document's units, on top of each other.
        double toDoc = MillimetresToDoc();
        var slab = Assert.Single(d.Preview.Items, i => i.Status == DiffScene.Status.Unchanged
            && i.Geometry.GetBoundingBox(i.Xform).Max.Y > 7000 * toDoc);
        Assert.Equal(8000 * toDoc, slab.Geometry.GetBoundingBox(slab.Xform).Max.X, 3);
        var moved = Assert.Single(d.Preview.Items, i => i.Status == DiffScene.Status.Modified);
        Assert.Equal(500 * toDoc, moved.Geometry.GetBoundingBox(moved.Xform).Min.X, 3);
        var previous = Assert.Single(d.Preview.Items, i => i.Status == DiffScene.Status.Previous);
        Assert.Equal(4000 * toDoc, previous.Geometry.GetBoundingBox(previous.Xform).Max.X, 6);
    }

    [RhinoFact]
    public void BlocksDrawAsTheirDefinitionAndADataOnlyMemberEditIsNoChange() => host.Run(() =>
    {
        Gh.Load();
        // A line member in A, the same line as a NURBS curve in B: representation only, so no block event.
        var pair = FixturePair.CreateFromScratch("gh-block", f => Chair(f, nurbs: false), f => Chair(f, nurbs: true));
        var d = Solve(pair);
        using var doc = d.Doc;
        output.WriteLine(d.Result.ToString());

        Assert.Empty(Gh.Out(d.Diff, 3));
        Assert.Equal("+0 −0 ~0 · 0 events", d.Diff.Message);
        Assert.True(d.Pass(d.Whole));
        var member = Assert.Single(d.Preview.Items);
        Assert.IsAssignableFrom<Curve>(member.Geometry);
        double toDoc = MillimetresToDoc();
        var box = member.Geometry.GetBoundingBox(member.Xform);
        Assert.Equal(1000 * toDoc, box.Min.X, 3); // placed by the instance transform
        Assert.Equal(1500 * toDoc, box.Max.X, 3);

        static void Chair(File3dm f, bool nurbs)
        {
            Model.Setup(f);
            int l = Model.Layer(f, "Furniture");
            Curve seat = new LineCurve(Point3d.Origin, new Point3d(500, 0, 0));
            if (nurbs) seat = seat.ToNurbsCurve();
            int block = Model.Block(f, "Chair", [seat], [new ObjectAttributes { LayerIndex = l }]);
            var at = Model.Attr(l, "chair");
            at.SetUserString("diff:uid", "chair-1");
            f.Objects.AddInstanceObject(block, Transform.Translation(1000, 0, 0), at);
        }
    });

    [RhinoFact]
    public void ALayerColourChangeIsBlueOnlyOnObjectsThatInheritIt() => host.Run(() =>
    {
        Gh.Load();
        var pair = FixturePair.Create("gh-layer-colour", f =>
        {
            Model.Setup(f);
            int doors = Model.Layer(f, "Doors", Color.Gray);
            Model.Add(f, new LineCurve(Point3d.Origin, new Point3d(1000, 0, 0)), doors, "bylayer");
            var own = Model.Attr(doors, "own");
            own.ColorSource = ObjectColorSource.ColorFromObject;
            own.ObjectColor = Color.Red;
            Model.Add(f, new LineCurve(new Point3d(0, 500, 0), new Point3d(1000, 500, 0)), own);
        }, b => { foreach (var l in b.AllLayers) if (l.Name == "Doors") l.Color = Color.Blue; });
        var d = Solve(pair);
        using var doc = d.Doc;

        Assert.Equal("+0 −0 ~0 · 1 event", d.Diff.Message);
        Assert.Equal(1, d.Statuses[DiffScene.Status.Event]);
        Assert.Equal(1, d.Statuses[DiffScene.Status.Unchanged]);
    });

    [RhinoFact]
    public void AFuzzyPairingIsForReviewNotAChange() => host.Run(() =>
    {
        Gh.Load();
        // Rebuilt with a new GUID and a slightly taller shape: only matching pass 5 (fuzzy) can pair it.
        var pair = FixturePair.CreateFromScratch("gh-fuzzy", f => Box(f, 3000), f => Box(f, 3150));
        var d = Solve(pair, kind: "review", gate: "Walls");
        using var doc = d.Doc;
        string report = d.Result.ToString();
        output.WriteLine(report);
        output.WriteLine(string.Join(System.Environment.NewLine, Gh.Texts(d.Whole, 1)));

        Assert.Contains("1 pairing needs review.", report);
        Assert.Contains("• Pairings to review: 1 fuzzy match", report);
        Assert.DoesNotContain("pairing needs review ·", report);
        Assert.EndsWith(" · 1 to review", d.Diff.Message);
        var info = Assert.Single(Gh.Texts(d.Filter, 1));
        Assert.Contains("pairing needs review", info);
        Assert.Matches(@"matched by fuzzy 0\.\d\d · needs review$", info);
        Assert.False(d.Pass(d.Gate));
        Assert.Contains(Gh.Texts(d.Gate, 1), w => w.EndsWith("pairing needs review", StringComparison.Ordinal));
        // The pairing gets no colour of its own: the box is amber only for its real shape change.
        Assert.DoesNotContain(d.Preview.Items, i => i.Status == DiffScene.Status.Removed || i.Status == DiffScene.Status.Added);

        static void Box(File3dm f, double height)
        {
            Model.Setup(f);
            int walls = Model.Layer(f, "Walls");
            Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(4000, 200, height))), walls, "wall");
        }
    });

    [RhinoFact]
    public void LiveSnapshotAndStampIdsWorkOnTheActiveDocument() => host.Run(() =>
    {
        Gh.Load();
        using var rhino = RhinoDoc.CreateHeadless(null);
        var previous = RhinoDoc.ActiveDoc;
        RhinoDoc.ActiveDoc = rhino;
        try
        {
            var stamped = new ObjectAttributes();
            stamped.SetUserString("diff:uid", "already");
            var first = rhino.Objects.AddPoint(new Point3d(0, 0, 0));
            var second = rhino.Objects.AddPoint(new Point3d(1, 0, 0), stamped);
            var third = rhino.Objects.AddPoint(new Point3d(2, 0, 0));
            rhino.Objects.AddPoint(new Point3d(3, 0, 0), stamped); // not an input: duplicates count document-wide

            using var doc = new GH_Document { Enabled = true };
            var snap = Gh.Add<SnapshotComponent>(doc);
            var stamp = Gh.Add<StampIdsComponent>(doc);
            Gh.Set(stamp.Params.Input[2], new GH_Boolean(true)); // saved as True, with nothing connected yet
            Gh.Solve(doc);

            var snapshot = (SnapshotGoo)Assert.Single(Gh.Out(snap, 0));
            Assert.Equal(4, snapshot.Value.Model.Objects.Count);
            Assert.Equal("Live", snap.Message);
            Assert.Equal("Not run", stamp.Message);

            // Connecting objects while Run stays True is no press.
            Gh.Set<IGH_Goo>(stamp.Params.Input[0], new GH_Guid(first), new GH_Guid(second));
            Gh.Solve(doc);
            Assert.Null(rhino.Objects.FindId(first).Attributes.GetUserString("diff:uid"));
            Assert.Equal("Not run", stamp.Message);
            Assert.Contains("2 objects in this document share a diff:uid", Gh.Errors(stamp));

            // Release, then press with objects in two branches: one solution stamps both.
            Gh.Set(stamp.Params.Input[2], new GH_Boolean(false));
            var objects = (GH_PersistentParam<IGH_Goo>)stamp.Params.Input[0];
            objects.PersistentData.Clear();
            objects.PersistentData.Append(new GH_Guid(first), new GH_Path(0));
            objects.PersistentData.Append(new GH_Guid(second), new GH_Path(0));
            objects.PersistentData.Append(new GH_Guid(third), new GH_Path(1));
            objects.ExpireSolution(false);
            Gh.Solve(doc);
            Gh.Set(stamp.Params.Input[2], new GH_Boolean(true));
            Gh.Solve(doc);
            Assert.True(Guid.TryParse(rhino.Objects.FindId(first).Attributes.GetUserString("diff:uid"), out _));
            Assert.True(Guid.TryParse(rhino.Objects.FindId(third).Attributes.GetUserString("diff:uid"), out _));
            Assert.Equal("already", rhino.Objects.FindId(second).Attributes.GetUserString("diff:uid"));
            Assert.Equal("Stamped 2", stamp.Message);
            Assert.Equal(2, stamp.Params.Output[0].VolatileData.PathCount);
            // "Stamped 2" from one solution: both branches went through one Stamp call, so one undo record.
            // (A headless document can't undo, so the record itself is checked in Rhino.)

            // Still True: connecting another object or recomputing writes nothing.
            var fourth = rhino.Objects.AddPoint(new Point3d(4, 0, 0));
            objects.PersistentData.Append(new GH_Guid(fourth), new GH_Path(1));
            objects.ExpireSolution(false);
            Gh.Solve(doc);
            Assert.Null(rhino.Objects.FindId(fourth).Attributes.GetUserString("diff:uid"));

            var saved = Gh.Add<SnapshotComponent>(doc);
            Gh.Set(saved.Params.Input[2], new GH_Boolean(true));
            Gh.Solve(doc);
            Assert.Contains("not been saved", Gh.Errors(saved));
            Assert.Equal("Last saved", saved.Message);
            doc.RemoveObject(snap, false);
            doc.RemoveObject(saved, false);
        }
        finally
        {
            RhinoDoc.ActiveDoc = previous;
        }
    });

    /// <summary>Unchanged context once drew in the viewport's own grey and vanished.</summary>
    [RhinoFact]
    public void UnchangedObjectsStandOutFromTheDefaultViewportBackground() => host.Run(() =>
    {
        var unchanged = DiffScene.ColorOf(DiffScene.Status.Unchanged);
        var background = global::Rhino.ApplicationSettings.AppearanceSettings.ViewportBackgroundColor;
        output.WriteLine($"viewport background {background}, unchanged {unchanged}");
        Assert.True(Contrast(unchanged, background) >= 3, $"{Contrast(unchanged, background):0.00}:1 on {background}");
        Assert.True(Contrast(unchanged, Color.White) >= 3);

        static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }
        static double Luminance(Color c)
        {
            static double Channel(int v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        }
    });

    private static void BuildModel(File3dm f)
    {
        Model.Setup(f);
        int structure = Model.Layer(f, "Structure");
        int columns = Model.Layer(f, "Columns", parent: structure);
        int walls = Model.Layer(f, "Walls");
        Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(8000, 8000, 250))), structure, "slab", "Slab L1");
        Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(300, 300, 3000))), columns, "column", "C-01");
        Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(0, 4000, 0), new Point3d(4000, 4200, 3000))), walls, "wall", "W-01");
    }

    private static int LayerIndex(File3dm f, string name)
    {
        foreach (var l in f.AllLayers)
            if (l.Name == name) return l.Index;
        throw new InvalidOperationException($"No layer '{name}'.");
    }

    private static string Ports(IEnumerable<IGH_Param> ps) => string.Join(", ", ps.Select(p => p.Name == p.NickName ? p.Name : $"{p.Name} ({p.NickName})"));
}
