using System.Drawing;
using DittoHopper.Rhino.Tests;
using Grasshopper.Kernel;
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

    [RhinoFact]
    public void SnapshotDiffFilterAndAssertSolveOnAFixturePair() => host.Run(() =>
    {
        Gh.Load();
        var pair = FixturePair.Create("gh-definition", BuildModel, b =>
        {
            Model.Transform(b, "wall", Transform.Translation(500, 0, 0));
            Model.Add(b, new LineCurve(new Point3d(0, 1000, 0), new Point3d(4000, 1000, 0)), LayerIndex(b, "Walls"), "new-line");
        });

        using var doc = new GH_Document { Enabled = true };
        var a = Gh.Add<SnapshotComponent>(doc);
        var b = Gh.Add<SnapshotComponent>(doc);
        var diff = Gh.Add<ModelDiffComponent>(doc);
        var filter = Gh.Add<DiffFilterComponent>(doc);
        var structure = Gh.Add<AssertUnchangedComponent>(doc);
        var everything = Gh.Add<AssertUnchangedComponent>(doc);
        var preview = Gh.Add<DiffPreviewComponent>(doc);
        Gh.Set(a.Params.Input[0], new GH_String(pair.A));
        Gh.Set(b.Params.Input[0], new GH_String(pair.B));
        Gh.Wire(a.Params.Output[0], diff.Params.Input[0]);
        Gh.Wire(b.Params.Output[0], diff.Params.Input[1]);
        foreach (var c in new GH_Component[] { filter, structure, everything, preview })
            Gh.Wire(diff.Params.Output[4], c.Params.Input[0]);
        Gh.Set(filter.Params.Input[1], new GH_String("moved"));
        Gh.Set(structure.Params.Input[1], new GH_String("Structure::*"));
        Gh.Solve(doc);

        foreach (var c in new GH_Component[] { a, b, diff, filter, structure, everything, preview })
            Assert.True(c.RuntimeMessageLevel < GH_RuntimeMessageLevel.Warning, $"{c.Name}: {Gh.Errors(c)}");

        var result = (ResultGoo)Assert.Single(Gh.Out(diff, 4));
        output.WriteLine(result.ToString());
        Assert.StartsWith("2 of 3 objects identical · 2 change sets · 0 table events", result.ToString());
        Assert.Contains("Matched 3 by key", result.ToString()); // names unique in both models: pass 1
        Assert.Single(Gh.Out(diff, 0)); // added line
        Assert.Empty(Gh.Out(diff, 1));
        Assert.IsType<GH_Brep>(Assert.Single(Gh.Out(diff, 2))); // moved wall
        Assert.Empty(Gh.Out(diff, 3));
        Assert.Equal("+1  −0  ~1", diff.Message);

        Assert.IsType<GH_Brep>(Assert.Single(Gh.Out(filter, 0)));
        Assert.Equal(["W-01 · moved 500 mm in X · Walls · matched by key"], Gh.Texts(filter, 1));

        Assert.True(((GH_Boolean)Assert.Single(Gh.Out(structure, 0))).Value);
        Assert.Equal(["Nothing on Structure::* changed: 2 objects checked."], Gh.Texts(structure, 1));
        Assert.False(((GH_Boolean)Assert.Single(Gh.Out(everything, 0))).Value);
        Assert.Equal(["1 object on Walls: moved 500 mm in X", "1 object added on Walls"], Gh.Texts(everything, 1));

        var statuses = preview.Items.GroupBy(i => i.Status).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(2, statuses[DiffPreviewComponent.Status.Unchanged]);
        Assert.Equal(1, statuses[DiffPreviewComponent.Status.Added]);
        Assert.Equal(1, statuses[DiffPreviewComponent.Status.Modified]);
        Assert.Equal(1, statuses[DiffPreviewComponent.Status.Previous]);
        Assert.True(preview.ClippingBox.IsValid);

        // Re-solving with the same file versions reuses the cached snapshots and the diff.
        var before = ((ResultGoo)Gh.Out(diff, 4)[0]).Value;
        a.ExpireSolution(false);
        Gh.Solve(doc);
        Assert.Same(before, ((ResultGoo)Gh.Out(diff, 4)[0]).Value);
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

            using var doc = new GH_Document { Enabled = true };
            var snap = Gh.Add<SnapshotComponent>(doc);
            var stamp = Gh.Add<StampIdsComponent>(doc);
            Gh.Set<IGH_Goo>(stamp.Params.Input[0], new GH_Guid(first), new GH_Guid(second));
            Gh.Solve(doc);

            var snapshot = (SnapshotGoo)Assert.Single(Gh.Out(snap, 0));
            Assert.Equal(2, snapshot.Value.Model.Objects.Count);
            Assert.Equal("Live", snap.Message);
            Assert.Null(rhino.Objects.FindId(first).Attributes.GetUserString("diff:uid"));
            Assert.Equal("Not run", stamp.Message);

            Gh.Set(stamp.Params.Input[2], new GH_Boolean(true));
            Gh.Solve(doc);
            Assert.True(Guid.TryParse(rhino.Objects.FindId(first).Attributes.GetUserString("diff:uid"), out _));
            Assert.Equal("already", rhino.Objects.FindId(second).Attributes.GetUserString("diff:uid"));
            Assert.Equal("Stamped 1", stamp.Message);
            Assert.Equal(2, Gh.Out(stamp, 0).Count);

            var saved = Gh.Add<SnapshotComponent>(doc);
            Gh.Set(saved.Params.Input[2], new GH_Boolean(true));
            Gh.Solve(doc);
            Assert.Contains("not been saved", Gh.Errors(saved));
            doc.RemoveObject(snap, false);
        }
        finally
        {
            RhinoDoc.ActiveDoc = previous;
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
