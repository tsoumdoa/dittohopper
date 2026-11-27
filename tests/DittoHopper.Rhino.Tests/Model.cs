using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;

namespace DittoHopper.Rhino.Tests;

/// <summary>
/// Builds fixture models: millimetres, tolerance 0.01, and a truth label in user text on every top-level
/// object, so a test can count wrong pairings. <see cref="Fixture"/> strips the label before diffing.
/// </summary>
internal static class Model
{
    public const string TruthKey = "fixture:truth";
    public const double Tolerance = 0.01;

    public static void Setup(File3dm f)
    {
        f.Settings.ModelUnitSystem = global::Rhino.UnitSystem.Millimeters;
        f.Settings.ModelAbsoluteTolerance = Tolerance;
        f.AllHatchPatterns.Add(HatchPattern.Defaults.Solid);
    }

    /// <summary>
    /// A layer. By default its id derives from its path, so a model built twice keeps its layer ids, as files
    /// of one lineage do; <paramref name="keepId"/> false gives a fresh id, as a new file does.
    /// </summary>
    public static int Layer(File3dm f, string name, Color? color = null, int parent = -1, bool keepId = true)
    {
        var parentLayer = parent >= 0 ? f.AllLayers.FindIndex(parent) : null;
        var layer = new Layer { Name = name, Color = color ?? Color.Black };
        if (parentLayer is not null) layer.ParentLayerId = parentLayer.Id;
        if (keepId) layer.Id = NameGuid("layer:" + (parentLayer is null ? "" : parentLayer.FullPath + "::") + name);
        f.AllLayers.Add(layer);
        foreach (var l in f.AllLayers)
            if (l.Name == name && l.ParentLayerId == layer.ParentLayerId) return l.Index;
        throw new InvalidOperationException($"Layer '{name}' was not added.");
    }

    public static ObjectAttributes Attr(int layer, string truth, string? name = null)
    {
        var a = new ObjectAttributes { LayerIndex = layer, Name = name };
        a.SetUserString(TruthKey, truth);
        return a;
    }

    /// <summary>Adds geometry with a truth label; returns its id.</summary>
    public static Guid Add(File3dm f, GeometryBase g, int layer, string truth, string? name = null) => Add(f, g, Attr(layer, truth, name));

    /// <summary>File3dm.Objects.Add refuses text, so text goes through AddText.</summary>
    public static Guid Add(File3dm f, GeometryBase g, ObjectAttributes a) => g is TextEntity t
        ? f.Objects.AddText(t.PlainText, t.Plane, t.TextHeight, "Arial", false, false, a)
        : f.Objects.Add(g, a);

    public static File3dmObject Find(File3dm f, string truth)
    {
        foreach (var o in f.Objects)
            if (o.Attributes.GetUserString(TruthKey) == truth) return o;
        throw new InvalidOperationException($"No object labelled '{truth}'.");
    }

    public static List<File3dmObject> All(File3dm f)
    {
        var list = new List<File3dmObject>();
        foreach (var o in f.Objects)
            if (!o.Attributes.IsInstanceDefinitionObject) list.Add(o);
        return list;
    }

    /// <summary>Gives an object new geometry and keeps its id and attributes, as an in-place edit does.</summary>
    public static void Replace(File3dm f, string truth, GeometryBase g)
    {
        var o = Find(f, truth);
        var attributes = o.Attributes.Duplicate();
        attributes.ObjectId = o.Id;
        Guid id = o.Id;
        f.Objects.Delete(id);
        if (Add(f, g, attributes) != id) throw new InvalidOperationException("The replaced object lost its id.");
    }

    /// <summary>The object's geometry, transformed, in place.</summary>
    public static void Transform(File3dm f, string truth, Transform x)
    {
        var g = Find(f, truth).Geometry.Duplicate();
        g.Transform(x);
        Replace(f, truth, g);
    }

    public static int Block(File3dm f, string name, IEnumerable<GeometryBase> geometry, IEnumerable<ObjectAttributes> attributes) =>
        f.AllInstanceDefinitions.Add(name, "", Point3d.Origin, geometry, attributes);

    public static Guid NameGuid(string s) => new(MD5.HashData(Encoding.UTF8.GetBytes(s)));
}

/// <summary>One fresh instance of each geometry type the hash covers, for re-bake and hash tests.</summary>
internal static class Samples
{
    public static IReadOnlyList<(string Name, Func<GeometryBase> Make)> All { get; } =
    [
        ("point", () => new global::Rhino.Geometry.Point(new Point3d(10, 20, 30))),
        ("point-cloud", () => new PointCloud([new Point3d(0, 0, 0), new Point3d(5, 5, 5), new Point3d(9, 1, 2)])),
        ("line", () => new LineCurve(new Point3d(0, 0, 0), new Point3d(4000, 0, 0))),
        ("polyline", () => new PolylineCurve([new Point3d(0, 0, 0), new Point3d(1000, 0, 0), new Point3d(1000, 800, 0), new Point3d(0, 800, 0)])),
        ("arc", () => new ArcCurve(new Arc(new Point3d(0, 0, 0), new Point3d(500, 300, 0), new Point3d(1000, 0, 0)))),
        ("circle", () => new ArcCurve(new Circle(new Point3d(2000, 2000, 0), 350))),
        ("nurbs-curve", () => Curve.CreateInterpolatedCurve([new Point3d(0, 0, 0), new Point3d(300, 200, 0), new Point3d(700, -100, 50), new Point3d(1000, 0, 0)], 3)),
        ("polycurve", () =>
        {
            var pc = new PolyCurve();
            pc.Append(new Line(new Point3d(0, 0, 0), new Point3d(500, 0, 0)));
            pc.Append(new Arc(new Point3d(500, 0, 0), new Vector3d(1, 0, 0), new Point3d(500, 500, 0)));
            return pc;
        }),
        ("plane-surface", () => new PlaneSurface(Plane.WorldXY, new Interval(0, 1200), new Interval(0, 600))),
        ("nurbs-surface", () => NurbsSurface.CreateThroughPoints(
            [.. from i in Enumerable.Range(0, 4) from j in Enumerable.Range(0, 4) select new Point3d(i * 300, j * 300, (i * j % 3) * 40.0)],
            4, 4, 3, 3, false, false)),
        ("box", () => Brep.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(400, 200, 3000)))),
        ("trimmed", () => Brep.CreatePlanarBreps(
            [new PolylineCurve([new Point3d(0, 0, 0), new Point3d(2000, 0, 0), new Point3d(2000, 1000, 0), new Point3d(0, 1000, 0), new Point3d(0, 0, 0)]),
             new ArcCurve(new Circle(new Point3d(1000, 500, 0), 200))], Model.Tolerance)[0]),
        ("extrusion", () => Extrusion.Create(new PolylineCurve([new Point3d(0, 0, 0), new Point3d(300, 0, 0), new Point3d(300, 300, 0), new Point3d(0, 300, 0), new Point3d(0, 0, 0)]), 3000, true)),
        ("mesh", () => Mesh.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(600, 600, 600)), 2, 2, 2)),
        ("subd", () => SubD.CreateFromMesh(Mesh.CreateFromBox(new BoundingBox(new Point3d(0, 0, 0), new Point3d(500, 500, 500)), 1, 1, 1))),
        ("text", () => TextEntity.Create("Level 03", new Plane(new Point3d(100, 100, 0), Vector3d.ZAxis), new DimensionStyle(), false, 0, 0)),
        ("text-dot", () => new TextDot("C-12", new Point3d(400, 400, 0))),
        ("hatch", () => Hatch.Create(new ArcCurve(new Circle(new Point3d(3000, 0, 0), 250)), 0, 0, 1, Model.Tolerance)[0]),
    ];

    /// <summary>Adds every sample with its name as truth label.</summary>
    public static void AddAll(File3dm f, int layer)
    {
        foreach (var (name, make) in All)
            Model.Add(f, make(), layer, name);
    }
}
