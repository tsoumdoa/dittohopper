using System.Drawing;
using DittoHopper.Core.Tests;
using DittoHopper.Core;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using Xunit.Abstractions;

namespace DittoHopper.Rhino.Tests;

/// <summary>
/// The design doc's test plan on real .3dm pairs: each fixture is generated, snapshotted, diffed with the
/// geometry ladder, checked, and pinned to golden/&lt;name&gt;.json. Each also asserts the plan's two numbers:
/// noise (reported rows minus true changes) is 0, and no pair is wrong.
/// </summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class FixtureTests(RhinoHost host, ITestOutputHelper output)
{
    private FixtureRun Check(string name, FixturePair pair, int trueChanges, string expected)
    {
        var run = Fixture.Run(pair);
        output.WriteLine($"| {name} | {expected} | {Summary(run.Result)} | {run.Noise(trueChanges)} | {run.WrongPairings} |");
        Assert.Equal(0, run.WrongPairings);
        Assert.Equal(trueChanges, run.Rows);
        Golden.Verify(name, run.Result);
        return run;
    }

    private static string Summary(DiffResult r)
    {
        var parts = r.TableEvents.Select(e => $"{e.Table} {e.Kind} {e.Label}")
            .Concat(r.ChangeSets.Select(s => $"{s.Kind} {s.Change?.Property.ToString() ?? s.Layer} ×{s.Objects.Count}"))
            .Concat(r.Geometry.GroupBy(g => g.Level).Select(g => $"{g.Count()} {g.Key}"));
        return string.Join("; ", parts.DefaultIfEmpty("no changes"));
    }

    private static int Walls(File3dm f)
    {
        Model.Setup(f);
        int walls = Model.Layer(f, "Walls", Color.Firebrick);
        Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(4000, 200, 3000))), walls, "wall-box");
        Model.Add(f, new LineCurve(new Point3d(0, 0, 0), new Point3d(4000, 0, 0)), walls, "wall-line");
        Model.Add(f, Samples.All.Single(s => s.Name == "extrusion").Make(), walls, "wall-extrusion");
        Model.Add(f, Samples.All.Single(s => s.Name == "mesh").Make(), walls, "wall-mesh");
        return walls;
    }

    private static Extrusion Column(double x, double y) => Extrusion.Create(
        new PolylineCurve([new Point3d(x, y, 0), new Point3d(x + 300, y, 0), new Point3d(x + 300, y + 300, 0), new Point3d(x, y + 300, 0), new Point3d(x, y, 0)]),
        3000, true);

    private static void Columns(File3dm f, int count)
    {
        Model.Setup(f);
        int columns = Model.Layer(f, "Columns");
        for (int i = 0; i < count; i++)
            Model.Add(f, Column(i * 6000, 0), columns, $"column-{i}");
    }

    private static Layer LayerNamed(File3dm f, string name)
    {
        foreach (var l in f.AllLayers) if (l.Name == name) return l;
        throw new InvalidOperationException($"No layer '{name}'.");
    }

    [RhinoFact]
    public void LayerRename() => host.Run(() =>
    {
        var run = Check("layer-rename", FixturePair.Create("layer-rename", f => Walls(f), b => LayerNamed(b, "Walls").Name = "A-WALL"),
            1, "1 Renamed event, 0 object rows");
        var e = Assert.Single(run.Result.TableEvents);
        Assert.Equal((TableEventKind.Renamed, "Walls", "A-WALL", 4), (e.Kind, e.From, e.To, e.AffectedObjects));
    });

    [RhinoFact]
    public void LayerReparent() => host.Run(() =>
    {
        var run = Check("layer-reparent", FixturePair.Create("layer-reparent",
            f =>
            {
                Model.Setup(f);
                int furniture = Model.Layer(f, "Furniture");
                for (int i = 0; i < 3; i++) Model.Add(f, new LineCurve(new Point3d(i * 100, 0, 0), new Point3d(i * 100, 500, 0)), furniture, $"chair-{i}");
            },
            b =>
            {
                // A File3dm drops a parent that comes after its child in the table, so Furniture is re-added
                // after Interior, keeping its id, and its objects follow it.
                int interior = Model.Layer(b, "Interior");
                var old = LayerNamed(b, "Furniture");
                var moved = new Layer { Name = old.Name, Color = old.Color, ParentLayerId = b.AllLayers.FindIndex(interior).Id, Id = old.Id };
                var objects = Model.All(b);
                b.AllLayers.Delete(old);
                b.AllLayers.Add(moved);
                int index = LayerNamed(b, "Furniture").Index;
                foreach (var o in objects) o.Attributes.LayerIndex = index;
            }),
            2, "1 Reparented + 1 Added layer event");
        Assert.Equal([TableEventKind.Added, TableEventKind.Reparented], run.Result.TableEvents.Select(e => e.Kind).Order());
    });

    [RhinoFact]
    public void LayerSplit() => host.Run(() =>
    {
        var run = Check("layer-split", FixturePair.Create("layer-split",
            f =>
            {
                Model.Setup(f);
                int slabs = Model.Layer(f, "Slabs");
                for (int i = 0; i < 12; i++)
                    Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(i * 9000, 0, 0), new Point3d(i * 9000 + 8000, 8000, 250))), slabs, $"slab-{i}");
            },
            b =>
            {
                int l1 = Model.Layer(b, "Slabs-L01"), l2 = Model.Layer(b, "Slabs-L02");
                foreach (var o in Model.All(b))
                    o.Attributes.LayerIndex = int.Parse(o.Attributes.GetUserString(Model.TruthKey)["slab-".Length..]) < 7 ? l1 : l2;
                b.AllLayers.Delete(LayerNamed(b, "Slabs"));
            }),
            1, "1 Split event, inferred, with shares");
        var e = Assert.Single(run.Result.TableEvents);
        Assert.Equal((TableEventKind.Split, true), (e.Kind, e.Inferred));
        Assert.Equal([("Slabs-L01", 7), ("Slabs-L02", 5)], e.Shares.Select(s => (s.Label, s.Objects)));
    });

    [RhinoFact]
    public void ByLayerColor() => host.Run(() =>
    {
        var run = Check("bylayer-color", FixturePair.Create("bylayer-color",
            f =>
            {
                Model.Setup(f);
                int doors = Model.Layer(f, "Doors");
                for (int i = 0; i < 3; i++) Model.Add(f, new LineCurve(new Point3d(i * 900, 0, 0), new Point3d(i * 900, 2100, 0)), doors, $"door-{i}");
                var own = Model.Attr(doors, "door-red");
                own.ColorSource = ObjectColorSource.ColorFromObject;
                own.ObjectColor = Color.Red;
                f.Objects.AddLine(new Line(new Point3d(5000, 0, 0), new Point3d(5000, 2100, 0)), own);
            },
            b => LayerNamed(b, "Doors").Color = Color.Red),
            1, "1 layer event, 0 own object changes");
        Assert.Equal(PropertyKind.Color, Assert.Single(run.Result.TableEvents).Property);
    });

    /// <summary>Every geometry type, plus a block instance, deleted and baked again: new GUIDs, same geometry.</summary>
    private static void Everything(File3dm f, bool sameLayerIds = true)
    {
        Model.Setup(f);
        int gh = Model.Layer(f, "GH", keepId: sameLayerIds);
        int other = Model.Layer(f, "GH-other", keepId: sameLayerIds);
        Samples.AddAll(f, gh);
        int chair = Model.Block(f, "Chair", [Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(500, 500, 450)))],
            [new ObjectAttributes { LayerIndex = other }]);
        f.Objects.AddInstanceObject(chair, Transform.Translation(1000, 1000, 0), Model.Attr(gh, "chair-1"));
    }

    [RhinoFact]
    public void GhRebake() => host.Run(() =>
    {
        var run = Check("gh-rebake", FixturePair.CreateFromScratch("gh-rebake", f => Everything(f), b => Everything(b)),
            0, "All matched by hash, 0 changes");
        Assert.Equal(Samples.All.Count + 1, run.Result.Matches.Count);
        Assert.All(run.Result.Matches, m => Assert.Equal(MatchedBy.GeometryHash, m.By));
    });

    [RhinoFact]
    public void PasteBetweenFiles() => host.Run(() =>
    {
        // A new file: its layers get new ids and a different table order; the pasted objects get new GUIDs.
        var run = Check("paste-between-files", FixturePair.CreateFromScratch("paste-between-files",
            f => Everything(f),
            b =>
            {
                Model.Setup(b);
                Model.Layer(b, "Unrelated", keepId: false);
                Everything(b, sameLayerIds: false);
            }),
            1, "Matched by hash or shape signature");
        Assert.Equal((TableEventKind.Added, "Unrelated"), (Assert.Single(run.Result.TableEvents).Kind, run.Result.TableEvents[0].Label));
        Assert.All(run.Result.Matches, m => Assert.Equal(MatchedBy.GeometryHash, m.By));
        Assert.All(run.Result.TableMatches.Where(t => t.Table == TableKind.Layer), t => Assert.Equal(TableMatchedBy.Path, t.By));
    });

    [RhinoFact]
    public void TinyNudge() => host.Run(() =>
    {
        var run = Check("tiny-nudge", FixturePair.Create("tiny-nudge", f => Columns(f, 4), b => Model.Transform(b, "column-2", Transform.Translation(0.8, 0, 0))),
            1, "Moved (the Tiny nudge flag comes in M3)");
        var set = Assert.Single(run.Result.ChangeSets);
        Assert.Equal(new PropertyChange(PropertyKind.Transform, null, null, "moved 0.8 mm in X"), set.Change);
        Assert.Equal(GeometryLevel.Moved, Assert.Single(run.Result.Geometry).Level);
    });

    [RhinoFact]
    public void DefaultLayer() => host.Run(() =>
    {
        var run = Check("default-layer", FixturePair.Create("default-layer",
            f =>
            {
                Model.Setup(f);
                Model.Layer(f, "Default");
                int walls = Model.Layer(f, "Walls");
                for (int i = 0; i < 25; i++) Model.Add(f, new LineCurve(new Point3d(i * 500, 0, 0), new Point3d(i * 500, 3000, 0)), walls, $"wall-{i}");
            },
            b =>
            {
                int dflt = LayerNamed(b, "Default").Index;
                foreach (var o in Model.All(b).Take(23)) o.Attributes.LayerIndex = dflt;
            }),
            1, "1 change set (the flag comes in M3)");
        var set = Assert.Single(run.Result.ChangeSets);
        Assert.Equal((new PropertyChange(PropertyKind.Layer, null, "Walls", "Default"), 23), (set.Change, set.Objects.Count));
    });

    private static NurbsCurve Wavy() => Curve.CreateInterpolatedCurve(
        [new Point3d(0, 0, 0), new Point3d(200, 150, 0), new Point3d(450, -80, 0), new Point3d(700, 120, 30), new Point3d(1000, 0, 0)], 3).ToNurbsCurve();

    [RhinoFact]
    public void CurveRebuild() => host.Run(() =>
    {
        var run = Check("curve-rebuild", FixturePair.Create("curve-rebuild",
            f =>
            {
                Model.Setup(f);
                int l = Model.Layer(f, "Curves");
                Model.Add(f, Wavy(), l, "rebuilt");
                Model.Add(f, Wavy(), l, "knot-inserted");
                Model.Add(f, new LineCurve(new Point3d(0, 500, 0), new Point3d(1000, 500, 0)), l, "line");
            },
            b =>
            {
                var rebuilt = Wavy().Rebuild(80, 3, true);
                Assert.True(Curve.GetDistancesBetweenCurves(Wavy(), rebuilt, 1e-6, out double max, out _, out _, out _, out _, out _) && max < Model.Tolerance);
                Model.Replace(b, "rebuilt", rebuilt);
                var inserted = Wavy();
                inserted.Knots.InsertKnot(inserted.Domain.ParameterAt(0.3));
                Model.Replace(b, "knot-inserted", inserted);
                var line = new LineCurve(new Point3d(0, 500, 0), new Point3d(1000, 500, 0)).ToNurbsCurve();
                line.IncreaseDegree(3);
                Model.Replace(b, "line", line);
            }),
            0, "Data-only");
        Assert.All(run.Result.Geometry, g => Assert.Equal(GeometryLevel.DataOnly, g.Level));
        Assert.Equal(["knots added", "line ↔ NURBS", "rebuilt"], run.Result.Geometry.Select(g => g.Detail!).Order(StringComparer.Ordinal));
    });

    [RhinoFact]
    public void CvEdit() => host.Run(() =>
    {
        var surface = () => (NurbsSurface)Samples.All.Single(s => s.Name == "nurbs-surface").Make();
        var run = Check("cv-edit", FixturePair.Create("cv-edit",
            f =>
            {
                Model.Setup(f);
                int l = Model.Layer(f, "Shapes");
                Model.Add(f, Wavy(), l, "curve");
                Model.Add(f, surface(), l, "surface");
            },
            b =>
            {
                var c = Wavy();
                c.Points.SetPoint(2, c.Points[2].Location + new Vector3d(0, 0, 50));
                Model.Replace(b, "curve", c);
                var s = surface();
                s.Points.SetPoint(1, 1, s.Points.GetControlPoint(1, 1).Location + new Vector3d(0, 0, 50));
                Model.Replace(b, "surface", s);
            }),
            1, "Hash differs; Edited with CV index comes in M3");
        Assert.All(run.Result.Geometry, g => Assert.Equal(GeometryLevel.Unclassified, g.Level));
        Assert.Equal(2, run.Result.Geometry.Count);
    });

    [RhinoFact]
    public void SeamAndFlip() => host.Run(() =>
    {
        var circle = () => new ArcCurve(new Circle(new Point3d(0, 0, 0), 500)).ToNurbsCurve();
        var run = Check("seam-and-flip", FixturePair.Create("seam-and-flip",
            f =>
            {
                Model.Setup(f);
                int l = Model.Layer(f, "Curves");
                Model.Add(f, circle(), l, "seam");
                Model.Add(f, Wavy(), l, "flip");
            },
            b =>
            {
                var c = circle();
                Assert.True(c.ChangeClosedCurveSeam(c.Domain.ParameterAt(0.3)));
                Model.Replace(b, "seam", c);
                var w = Wavy();
                Assert.True(w.Reverse());
                Model.Replace(b, "flip", w);
            }),
            0, "Both Data-only");
        Assert.All(run.Result.Geometry, g => Assert.Equal(GeometryLevel.DataOnly, g.Level));
        Assert.Equal(["direction flipped", "seam moved"], run.Result.Geometry.Select(g => g.Detail!).Order(StringComparer.Ordinal));
    });

    [RhinoFact]
    public void BrepOpen() => host.Run(() =>
    {
        var box = () => Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(1000, 600, 400)));
        var run = Check("brep-open", FixturePair.Create("brep-open",
            f =>
            {
                Model.Setup(f);
                Model.Add(f, box(), Model.Layer(f, "Solids"), "box");
            },
            b =>
            {
                var solid = box();
                var open = Brep.JoinBreps(solid.Faces.Skip(1).Select(face => face.DuplicateFace(false)), Model.Tolerance).Single();
                Assert.False(open.IsSolid);
                Model.Replace(b, "box", open);
            }),
            1, "Hash differs; Topology changed comes in M3");
        Assert.Equal(GeometryLevel.Unclassified, Assert.Single(run.Result.Geometry).Level);
    });

    /// <summary>Inner (a box) is nested in Outer (Inner + a line). Three Outer and two Inner instances, ids kept.</summary>
    private static void Nested(File3dm f, double innerHeight)
    {
        Model.Setup(f);
        int l = Model.Layer(f, "Blocks");
        var memberAttributes = new ObjectAttributes { LayerIndex = l };
        int inner = Model.Block(f, "Inner", [Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(100, 100, innerHeight)))], [memberAttributes]);
        var innerId = Guid.Empty;
        foreach (var d in f.AllInstanceDefinitions) if (d.Name == "Inner") innerId = d.Id;
        int outer = Model.Block(f, "Outer",
            [new InstanceReferenceGeometry(innerId, Transform.Translation(50, 0, 0)), new LineCurve(new Point3d(0, 0, 0), new Point3d(300, 0, 0))],
            [memberAttributes, memberAttributes]);
        for (int i = 0; i < 3; i++)
            f.Objects.AddInstanceObject(outer, Transform.Translation(i * 1000, 0, 0), WithId(Model.Attr(l, $"outer-{i}"), $"outer-{i}"));
        for (int i = 0; i < 2; i++)
            f.Objects.AddInstanceObject(inner, Transform.Translation(0, (i + 1) * 1000, 0), WithId(Model.Attr(l, $"inner-{i}"), $"inner-{i}"));
    }

    private static ObjectAttributes WithId(ObjectAttributes a, string key)
    {
        a.ObjectId = Model.NameGuid("object:" + key);
        return a;
    }

    [RhinoFact]
    public void NestedBlock() => host.Run(() =>
    {
        var run = Check("nested-block", FixturePair.CreateFromScratch("nested-block", f => Nested(f, 100), b => Nested(b, 180)),
            2, "Each instance reports it; definition is root cause");
        var e = Assert.Single(run.Result.TableEvents);
        Assert.Equal((TableKind.Block, TableEventKind.PropertyChanged, "Inner", 5), (e.Table, e.Kind, e.Label, e.AffectedObjects));
        var set = Assert.Single(run.Result.ChangeSets);
        Assert.Equal((new PropertyChange(PropertyKind.BlockContent, "Inner", null, null), 3, e.Id), (set.Change, set.Objects.Count, set.RootCauseEventId));
    });

    [RhinoFact]
    public void UserText() => host.Run(() =>
    {
        var run = Check("user-text", FixturePair.Create("user-text",
            f =>
            {
                Model.Setup(f);
                int panels = Model.Layer(f, "Panels", parent: Model.Layer(f, "Facade"));
                var a = Model.Attr(panels, "panel");
                a.SetUserString("diff:uid", "P-1");
                a.SetUserString("panel_type", "A");
                a.SetUserString("temp_note", "check");
                f.Objects.Add(Samples.All.Single(s => s.Name == "plane-surface").Make(), a);
            },
            b =>
            {
                var a = Model.Find(b, "panel").Attributes;
                a.SetUserString("panel_type", "B");
                a.SetUserString("supplier", "FX-02");
                a.DeleteUserString("temp_note");
            }),
            3, "One change per key");
        Assert.Equal(["panel_type", "supplier", "temp_note"], run.Result.ChangeSets.Select(s => s.Change!.Key!));
        Assert.Equal(MatchedBy.StableKey, Assert.Single(run.Result.Matches).By);
    });

    [RhinoFact]
    public void CopyInPlace() => host.Run(() =>
    {
        var run = Check("copy-in-place", FixturePair.Create("copy-in-place", f => Columns(f, 3),
            b =>
            {
                var original = Model.Find(b, "column-1");
                var copy = original.Attributes.Duplicate();
                copy.ObjectId = Guid.Empty;
                copy.SetUserString(Model.TruthKey, "column-1-copy");
                b.Objects.Add(original.Geometry.Duplicate(), copy);
            }),
            1, "Added (the Duplicate flag comes in M3)");
        var set = Assert.Single(run.Result.ChangeSets);
        Assert.Equal((ChangeKind.Added, 1), (set.Kind, set.Objects.Count));
        Assert.All(run.Result.Matches, m => Assert.Equal(MatchedBy.Guid, m.By));
    });
}
