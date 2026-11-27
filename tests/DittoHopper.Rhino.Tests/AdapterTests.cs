using DittoHopper.Core;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using RhinoDoc = Rhino.RhinoDoc;

namespace DittoHopper.Rhino.Tests;

[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class AdapterTests(RhinoHost host)
{
    private static readonly HashNames NoNames = new(new Dictionary<int, string>(), new Dictionary<Guid, string>(), new Dictionary<Guid, string>());

    private static ulong Hash(GeometryBase g) => GeometryHasher.Hash(g, Model.Tolerance, NoNames);

    private static GeometryBase Moved(GeometryBase g, double dx)
    {
        var copy = g.Duplicate();
        copy.Translate(dx, 0, 0);
        return copy;
    }

    [RhinoFact]
    public void EveryTypeHashesEqualWhenRebuiltAndDifferentWhenMoved() => host.Run(() =>
    {
        foreach (var (name, make) in Samples.All)
        {
            var g = make();
            Assert.True(Hash(g) == Hash(make()), $"{name}: a re-bake must hash equal");
            Assert.True(Hash(g) == Hash(Moved(g, Model.Tolerance / 10)), $"{name}: a move within one grid cell hashes equal");
            Assert.True(Hash(g) != Hash(Moved(g, 3 * Model.Tolerance)), $"{name}: a move of 3 grid cells must hash differently");
        }
    });

    [RhinoFact]
    public void StructuralEditsHashDifferently() => host.Run(() =>
    {
        var curve = (NurbsCurve)Samples.All.Single(s => s.Name == "nurbs-curve").Make();
        var dragged = (NurbsCurve)curve.Duplicate();
        dragged.Points.SetPoint(1, dragged.Points[1].Location + new Vector3d(0, 0, 1));
        var inserted = (NurbsCurve)curve.Duplicate();
        inserted.Knots.InsertKnot(inserted.Domain.Mid);
        var reweighted = (NurbsCurve)curve.Duplicate();
        reweighted.Points.SetPoint(1, reweighted.Points[1].Location, 2);
        Assert.Equal(4, new[] { curve, dragged, inserted, reweighted }.Select(Hash).Distinct().Count());

        var box = Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(400, 200, 3000)));
        var open = Brep.JoinBreps(box.Faces.Skip(1).Select(f => f.DuplicateFace(false)), Model.Tolerance).Single();
        Assert.NotEqual(Hash(box), Hash(open));

        var text = TextEntity.Create("Level 03", Plane.WorldXY, new DimensionStyle(), false, 0, 0);
        var renamed = TextEntity.Create("Level 04", Plane.WorldXY, new DimensionStyle(), false, 0, 0);
        Assert.NotEqual(Hash(text), Hash(renamed));
        Assert.NotEqual(Hash(new TextDot("A", Point3d.Origin)), Hash(new TextDot("B", Point3d.Origin)));

        // A line and the same degree-1 NURBS curve are the same shape: equal hashes, different representations.
        var line = new LineCurve(new Point3d(0, 0, 0), new Point3d(100, 0, 0));
        Assert.Equal(Hash(line), Hash(line.ToNurbsCurve()));
        Assert.NotEqual(GeometryHasher.Signature(line, 0, Model.Tolerance).Representation, GeometryHasher.Signature(line.ToNurbsCurve(), 0, Model.Tolerance).Representation);
    });

    [RhinoFact]
    public void RotationsMirrorsAndSharedMovesAreClassified() => host.Run(() =>
    {
        var turn = Transform.Rotation(Math.PI / 2, Vector3d.ZAxis, new Point3d(1000, 0, 0));
        var shift = Transform.Translation(120, 0, 0);
        var pair = FixturePair.Create("moves",
            f =>
            {
                Model.Setup(f);
                int l = Model.Layer(f, "Parts");
                Model.Add(f, Samples.All.Single(s => s.Name == "box").Make(), l, "rotated");
                Model.Add(f, Samples.All.Single(s => s.Name == "nurbs-curve").Make(), l, "mirrored");
                foreach (var name in new[] { "extrusion", "mesh", "text-dot", "text" })
                    Model.Add(f, Samples.All.Single(s => s.Name == name).Make(), l, "shifted-" + name);
                int chair = Model.Block(f, "Chair", [new LineCurve(Point3d.Origin, new Point3d(500, 0, 0))], [new ObjectAttributes { LayerIndex = l }]);
                f.Objects.AddInstanceObject(chair, Transform.Translation(3000, 3000, 0), Model.Attr(l, "shifted-chair"));
            },
            b =>
            {
                Model.Transform(b, "rotated", turn);
                Model.Transform(b, "mirrored", Transform.Mirror(new Plane(new Point3d(0, 0, 500), Vector3d.ZAxis)));
                foreach (var name in new[] { "extrusion", "mesh", "text-dot", "text", "chair" })
                    Model.Transform(b, "shifted-" + name, shift);
            });
        var r = Fixture.Run(pair).Result;
        var details = r.ChangeSets.Select(s => (s.Change!.To, s.Objects.Count)).ToList();
        Assert.Contains(("rotated 90° about Z through (1000, 0, 0)", 1), details);
        Assert.Contains(("mirrored across the plane Z = 500 mm", 1), details);
        Assert.Contains(("moved 120 mm in X", 5), details);
        Assert.Equal(3, details.Count);
        Assert.All(r.Geometry, g => Assert.Equal(GeometryLevel.Moved, g.Level));
    });

    [RhinoFact]
    public void LinkedBlocksAreSkippedAndNoted() => host.Run(() =>
    {
        var source = FixturePair.Create("linked-source", f => { Model.Setup(f); Model.Add(f, new LineCurve(Point3d.Origin, new Point3d(1, 0, 0)), Model.Layer(f, "L"), "x"); }, _ => { });
        var pair = FixturePair.Create("linked", f => { Model.Setup(f); f.AllInstanceDefinitions.AddLinked(source.A, "Site", ""); }, _ => { });
        using var snapshot = RhinoSnapshot.FromFile(pair.A);
        Assert.Contains(snapshot.Model.Info.Skipped, s => s.StartsWith("linked block definition 'Site'", StringComparison.Ordinal));
        Assert.Equal(0UL, Assert.Single(snapshot.Model.Blocks).ContentHash);
    });

    [RhinoFact]
    public void DocumentMatchesItsFileAndDiffsAgainstItsLastSave() => host.Run(() =>
    {
        var pair = FixturePair.Create("document", f => FixtureTestsModel(f), _ => { });
        using var doc = RhinoDoc.OpenHeadless(pair.A);
        Assert.NotNull(doc);

        // Opening a file in Rhino is not a change.
        using var file = RhinoSnapshot.FromFile(pair.A);
        using var live = RhinoSnapshot.FromDocument(doc);
        var opened = Fixture.Diff(file, live, TimeSpan.Zero);
        Assert.True(opened.Rows == 0, DiffJson.Serialize(opened.Result with { Matches = [], TableMatches = [] }));
        Assert.Equal(doc.Layers.CurrentLayer.Id, live.Model.CurrentLayerId);
        Assert.Equal(doc.ModelAngleToleranceRadians, live.Model.Info.AngleTolerance);
        Assert.Equal(doc.ModelAngleToleranceRadians, file.Model.Info.AngleTolerance);

        var wall = doc.Objects.FindByUserString(Model.TruthKey, "wall-0", true).Single();
        doc.Objects.Transform(wall, Transform.Translation(0, 250, 0), true);
        var (lastSave, current) = RhinoSnapshot.DocumentVsLastSave(doc);
        using var _1 = lastSave;
        using var _2 = current;
        var r = Fixture.Diff(lastSave, current, TimeSpan.Zero).Result;
        Assert.Equal("A.3dm (last save)", r.A.Source);
        Assert.Equal(new PropertyChange(PropertyKind.Transform, null, null, "moved 250 mm in Y"), Assert.Single(r.ChangeSets).Change);

        using var unsaved = RhinoDoc.CreateHeadless(null);
        var e = Assert.Throws<InvalidOperationException>(() => RhinoSnapshot.DocumentVsLastSave(unsaved));
        Assert.Contains("has not been saved yet", e.Message);
    });

    private static void FixtureTestsModel(File3dm f)
    {
        Model.Setup(f);
        int walls = Model.Layer(f, "Walls");
        for (int i = 0; i < 3; i++)
            Model.Add(f, Brep.CreateFromBox(new BoundingBox(new Point3d(i * 5000, 0, 0), new Point3d(i * 5000 + 4000, 200, 3000))), walls, $"wall-{i}");
        Samples.AddAll(f, Model.Layer(f, "Samples"));
    }
}
