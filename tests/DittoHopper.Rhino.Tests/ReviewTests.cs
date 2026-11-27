using System.Drawing;
using DittoHopper.Core;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using Xunit;
using Point = Rhino.Geometry.Point;
using RhinoDoc = Rhino.RhinoDoc;

namespace DittoHopper.Rhino.Tests;

/// <summary>Regression tests for the M2 review round 1 findings: one focused case each.</summary>
[Collection("Rhino")]
[Trait("Category", "RhinoIntegration")]
public sealed class ReviewTests(RhinoHost host)
{
    /// <summary>Diffs one object in two in-memory files, paired by stable key.</summary>
    private static DiffResult Compare(GeometryBase a, GeometryBase b, DiffSettings? settings = null)
    {
        using var af = new File3dm();
        using var bf = new File3dm();
        foreach (var f in new[] { af, bf })
        {
            Model.Setup(f);
            Model.Layer(f, "Base");
        }
        var at = Model.Attr(0, "x");
        at.SetUserString("diff:uid", "same");
        Model.Add(af, a, at);
        Model.Add(bf, b, at);
        using var sa = RhinoSnapshot.FromFile(af, "A");
        using var sb = RhinoSnapshot.FromFile(bf, "B");
        return RhinoDiff.Diff(sa, sb, settings);
    }

    private static GeometryLevel Level(DiffResult r) => Assert.Single(r.Geometry).Level;

    [RhinoFact]
    public void AMeshWithAnOffCentreHoleIsNotDataOnly() => host.Run(() =>
    {
        var full = new Mesh();
        foreach (var (x, y) in new[] { (0, 0), (100, 0), (100, 100), (0, 100) }) full.Vertices.Add(x, y, 0);
        full.Faces.AddFace(0, 1, 2, 3);
        var holed = new Mesh();
        int[] c = [0, 70, 80, 100];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++) holed.Vertices.Add(c[x], c[y], 0);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
                if (x != 1 || y != 1) holed.Faces.AddFace(y * 4 + x, y * 4 + x + 1, y * 4 + x + 5, y * 4 + x + 4);
        Assert.Equal(GeometryLevel.Unclassified, Level(Compare(full, holed)));
    });

    /// <summary>50 control points in U with ten knots clustered near 0, and a 1 mm bump among them.</summary>
    private static Brep Clustered(bool bump)
    {
        var s = NurbsSurface.Create(3, false, 4, 2, 50, 5);
        for (int u = 0; u < s.KnotsU.Count; u++) s.KnotsU[u] = u <= 10 ? 0.0001 * u : (u - 10) / (s.KnotsU.Count - 11.0);
        s.KnotsV.CreateUniformKnots(1);
        for (int u = 0; u < 50; u++)
            for (int v = 0; v < 5; v++)
                s.Points.SetPoint(u, v, new Point3d(u * 10, v * 10, v == 2 && u == 29 ? 5 : v == 2 && u == 5 && bump ? 1 : 0));
        Assert.True(s.IsValid);
        return s.ToBrep();
    }

    [RhinoFact]
    public void ALocalBumpAmongClusteredKnotsIsSeen() => host.Run(() =>
    {
        Assert.Equal(GeometryLevel.Unclassified, Level(Compare(Clustered(false), Clustered(true))));
        var movedBump = Clustered(true);
        movedBump.Translate(0, 0, 20);
        Assert.Equal(GeometryLevel.Unclassified, Level(Compare(Clustered(false), movedBump)));
        var moved = Clustered(false);
        moved.Translate(0, 0, 20);
        Assert.Equal(GeometryLevel.Moved, Level(Compare(Clustered(false), moved)));
    });

    [RhinoFact]
    public void AShapeToleranceFinerThanTheHashGridRechecksEqualHashes() => host.Run(() =>
    {
        var r = Compare(new Point(Point3d.Origin), new Point(new Point3d(0.004, 0.004, 0)), new DiffSettings { ShapeTolerance = 0.001 });
        var g = Assert.Single(r.Geometry);
        Assert.Equal((GeometryLevel.Moved, "moved 0.006 mm (X 0.004, Y 0.004)"), (g.Level, g.Detail));
        Assert.Empty(Compare(new Point(Point3d.Origin), new Point(Point3d.Origin), new DiffSettings { ShapeTolerance = 0.001 }).Geometry);
    });

    [RhinoFact]
    public void ADiagonalMoveSmallerThanTolerancePerAxisIsMoved() => host.Run(() =>
        Assert.Equal(GeometryLevel.Moved, Level(Compare(new Point(Point3d.Origin), new Point(new Point3d(0.009, 0.009, 0))))));

    [RhinoFact]
    public void ACurveTypeOrDomainChangeIsDataOnlyWithAReason() => host.Run(() =>
    {
        var line = new LineCurve(Point3d.Origin, new Point3d(100, 0, 0));
        var nurbs = line.ToNurbsCurve();
        nurbs.Domain = new Interval(40, 80);
        var r = Compare(line, nurbs);
        var g = Assert.Single(r.Geometry);
        Assert.Equal((GeometryLevel.DataOnly, "line ↔ NURBS"), (g.Level, g.Detail));
        Assert.Empty(r.ChangeSets);

        var shifted = nurbs.DuplicateCurve();
        shifted.Domain = new Interval(0, 1);
        var d = Assert.Single(Compare(nurbs, shifted).Geometry);
        Assert.Equal((GeometryLevel.DataOnly, "reparameterized"), (d.Level, d.Detail));
    });

    private static void MemberOn(File3dm f, string memberLayer)
    {
        Model.Setup(f);
        Model.Layer(f, "Red", Color.Red);
        Model.Layer(f, "Blue", Color.Blue);
        int block = Model.Block(f, "Mark", [new LineCurve(Point3d.Origin, new Point3d(100, 0, 0))],
            [new ObjectAttributes { LayerIndex = memberLayer == "Blue" ? 1 : 0 }]);
        f.Objects.AddInstanceObject(block, Transform.Identity, Model.Attr(0, "mark"));
    }

    [RhinoFact]
    public void ABlockMemberMovedToAnotherLayerIsAContentChange() => host.Run(() =>
    {
        var moved = Fixture.Run(FixturePair.Create("member-layer", f => MemberOn(f, "Red"),
            b => { foreach (var o in b.Objects) if (o.Attributes.IsInstanceDefinitionObject) o.Attributes.LayerIndex = 1; })).Result;
        var e = Assert.Single(moved.TableEvents);
        Assert.Equal((TableKind.Block, PropertyKind.Content), (e.Table, e.Property));

        var renamed = Fixture.Run(FixturePair.Create("member-layer-rename", f => MemberOn(f, "Red"),
            b => { foreach (var l in b.AllLayers) if (l.Name == "Red") l.Name = "Crimson"; })).Result;
        Assert.Equal(TableKind.Layer, Assert.Single(renamed.TableEvents).Table);
    });

    private static void Hatched(File3dm f, bool lines)
    {
        Model.Setup(f);
        var pattern = new HatchPattern(lines ? HatchPattern.Defaults.Hatch1 : HatchPattern.Defaults.Solid) { Name = "Review" };
        f.AllHatchPatterns.Add(pattern);
        var stored = f.AllHatchPatterns.FindName("Review");
        var hatch = Hatch.Create(new ArcCurve(new Circle(Point3d.Origin, 50)), stored.Index, 0, 1, Model.Tolerance)[0];
        var at = Model.Attr(Model.Layer(f, "Hatches"), "hatch");
        at.SetUserString("diff:uid", "hatch");
        f.Objects.Add(hatch, at);
    }

    [RhinoFact]
    public void HatchFillAndLightSettingsAreGeometryChanges() => host.Run(() =>
    {
        var hatch = Fixture.Run(FixturePair.CreateFromScratch("hatch-fill", f => Hatched(f, false), f => Hatched(f, true))).Result;
        Assert.Equal(PropertyKind.Geometry, Assert.Single(hatch.ChangeSets).Change!.Property);

        using var doc = RhinoDoc.CreateHeadless(null);
        var light = new Light { LightStyle = LightStyle.WorldPoint, Location = new Point3d(12, 12, 12), Intensity = 0.25 };
        int index = doc.Lights.Add(light);
        using var dim = RhinoSnapshot.FromDocument(doc, "A");
        light.Intensity = 0.75;
        Assert.True(doc.Lights.Modify(index, light));
        using var bright = RhinoSnapshot.FromDocument(doc, "B");
        Assert.Equal(GeometryLevel.Unclassified, Level(RhinoDiff.Diff(dim, bright)));
    });

    [RhinoFact]
    public void ADisposedSnapshotKeepsItsModelButReleasesItsGeometry() => host.Run(() =>
    {
        using var f = new File3dm();
        Model.Setup(f);
        Samples.AddAll(f, Model.Layer(f, "Samples"));
        var snapshot = RhinoSnapshot.FromFile(f, "A");
        snapshot.Dispose();
        snapshot.Dispose();
        Assert.Equal(Samples.All.Count, snapshot.Model.Objects.Count);
        Assert.Throws<ObjectDisposedException>(() => RhinoDiff.Diff(snapshot, snapshot));
    });
}
