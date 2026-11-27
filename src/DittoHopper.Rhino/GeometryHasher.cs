using System.Globalization;
using DittoHopper.Core;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace DittoHopper.Rhino;

/// <summary>
/// Table entries the hash needs, by name (hatch patterns by <see cref="GeometryHasher.PatternKey"/>: name, fill
/// and lines), so geometry that refers to a table hashes alike in any file.
/// </summary>
internal sealed record HashNames(
    IReadOnlyDictionary<int, string> HatchPatterns,
    IReadOnlyDictionary<Guid, string> DimStyles,
    IReadOnlyDictionary<Guid, string> Blocks);

/// <summary>
/// The canonical geometry hash, per geometry type: a type tag and the structure (degrees, counts, knots,
/// weights, indices, trims) as exact integers, and every coordinate snapped to the grid by
/// <see cref="CanonicalHash"/>. Rhino's own serialization is never hashed: it carries non-geometric data.
/// Curves and surfaces go through their NURBS form with knots normalized to the domain, so the hash is shape
/// identity only: a line and the same degree-1 NURBS curve, or a domain change, hash alike. How a shape is
/// represented is the separate <see cref="Representation"/>, which the ladder lists as Data-only when it differs.
/// </summary>
internal static class GeometryHasher
{
    public static ulong Hash(GeometryBase g, double grid, HashNames names)
    {
        var h = new CanonicalHash(Tag(g), grid);
        switch (g)
        {
            case InstanceReferenceGeometry i:
                h.Add(names.Blocks.GetValueOrDefault(i.ParentIdefId));
                AddXform(h, i.Xform);
                break;
            case Point p:
                Add(h, p.Location);
                break;
            case PointCloud pc:
                h.Add((long)pc.Count);
                foreach (var p in pc.GetPoints()) Add(h, p);
                break;
            case Curve c:
                AddCurve(h, c);
                break;
            case Brep b:
                AddBrep(h, b);
                break;
            case Extrusion e:
                AddExtrusion(h, e);
                break;
            case Surface s:
                AddSurface(h, s);
                break;
            case Mesh m:
                AddMesh(h, m);
                break;
            case SubD s:
                AddSubD(h, s);
                break;
            case TextDot d:
                Add(h, d.Point);
                h.Add(d.Text).Add(d.SecondaryText).Add((long)d.FontHeight).Add(d.FontFace);
                break;
            case AnnotationBase a:
                AddPlane(h, a.Plane);
                h.Add(a.PlainText).Add(a.TextHeight).Add(names.DimStyles.GetValueOrDefault(a.DimensionStyleId));
                AddBox(h, a.GetBoundingBox(true));
                break;
            case Hatch hatch:
                AddHatch(h, hatch, names);
                break;
            case Light light:
                AddLight(h, light);
                break;
            default:
                // Anything newer: the type and where it is.
                h.Add((long)g.ObjectType);
                AddBox(h, g.GetBoundingBox(true));
                break;
        }
        return h.Finish();
    }

    /// <summary>
    /// An instance nested in a block definition contributes its transform only: the nested definition is
    /// compared through the block map, so an inner edit or rename changes only the inner definition.
    /// </summary>
    public static ulong NestedPlacement(InstanceReferenceGeometry i, double grid) =>
        AddXform(new CanonicalHash("nested-instance", grid), i.Xform).Finish();

    /// <summary>
    /// Length, area, volume, centroid, tight bounding box and topology summary (<see cref="Measure.Summary"/>,
    /// naked edges joined within <paramref name="tol"/>), measured once per snapshot.
    /// </summary>
    public static GeometrySignature Signature(GeometryBase g, ulong hash, double tol)
    {
        var box = g.GetBoundingBox(true);
        double length = 0, area = 0, volume = 0;
        Point3d? centroid = null;
        var summary = g is Extrusion ? null : Measure.Summary(g, tol);
        switch (g)
        {
            case Point p:
                centroid = p.Location;
                break;
            case Curve c:
                using (var lp = LengthMassProperties.Compute(c, true, true, false, false))
                    if (lp is not null) (length, centroid) = (lp.Length, lp.Centroid);
                break;
            case Brep b:
                (area, volume, centroid) = Mass(b);
                break;
            case Extrusion e:
                using (var b = e.ToBrep(true))
                    if (b is not null) ((area, volume, centroid), summary) = (Mass(b), Measure.Summary(b, tol));
                break;
            case Surface s:
                using (var ap = AreaMassProperties.Compute(s, true, true, false, false))
                    if (ap is not null) (area, centroid) = (ap.Area, ap.Centroid);
                break;
            case Mesh m:
                using (var ap = AreaMassProperties.Compute(m, true, true, false, false))
                    if (ap is not null) (area, centroid) = (ap.Area, ap.Centroid);
                if (m.IsClosed)
                    using (var vp = VolumeMassProperties.Compute(m, true, false, false, false))
                        volume = vp?.Volume ?? 0;
                break;
            case Hatch hatch:
                using (var ap = AreaMassProperties.Compute(hatch))
                    if (ap is not null) (area, centroid) = (ap.Area, ap.Centroid);
                break;
        }
        return new GeometrySignature
        {
            Hash = hash,
            Representation = g is Curve curve ? Representation(curve, length) : 0,
            Length = length,
            Area = area,
            Volume = volume,
            Centroid = Vec(centroid ?? (box.IsValid ? box.Center : Point3d.Origin)),
            BBox = box.IsValid ? new BBox(Vec(box.Min), Vec(box.Max)) : default,
            Closed = summary?.Closed ?? false,
            Faces = summary?.Faces ?? 0,
            BoundaryLoops = summary?.Loops ?? 0,
            BoundaryLength = summary?.Boundary ?? 0,
            Components = summary?.Components ?? 0,
        };
    }

    public static Vec3 Vec(Point3d p) => new(p.X, p.Y, p.Z);

    /// <summary>
    /// A curve's representation: its type and its domain divided by its length, so the same line in millimetres
    /// and in metres represents alike but a domain change or a line turned NURBS does not.
    /// </summary>
    private static ulong Representation(Curve c, double length)
    {
        double per = length > 0 ? 1 / length : 1;
        return new CanonicalHash("representation:curve", 1).Add(c.GetType().Name)
            .Add(Exact(c.Domain.T0 * per)).Add(Exact(c.Domain.T1 * per)).Finish();
    }

    private static (double Area, double Volume, Point3d? Centroid) Mass(Brep b)
    {
        using var ap = AreaMassProperties.Compute(b, true, true, false, false);
        double volume = 0;
        if (b.IsSolid)
            using (var vp = VolumeMassProperties.Compute(b, true, false, false, false))
                volume = vp?.Volume ?? 0;
        return (ap?.Area ?? 0, volume, ap?.Centroid);
    }

    private static string Tag(GeometryBase g) => g switch
    {
        InstanceReferenceGeometry => "instance",
        Point => "point",
        PointCloud => "pointcloud",
        Curve => "curve",
        Brep => "brep",
        Extrusion => "extrusion",
        Surface => "surface",
        Mesh => "mesh",
        SubD => "subd",
        TextDot => "textdot",
        AnnotationBase a => "annotation:" + a.GetType().Name,
        Hatch => "hatch",
        _ => "other:" + g.GetType().Name,
    };

    /// <summary>A value that is compared, not snapped to the coordinate grid (weights, normalized knots, unit vectors, settings), to 1e-9.</summary>
    internal static long Exact(double v) => (long)Math.Round(v * 1e9, MidpointRounding.AwayFromZero);

    private static void Add(CanonicalHash h, Point3d p) => h.Add(p.X).Add(p.Y).Add(p.Z);

    private static void AddUnit(CanonicalHash h, Vector3d v) => h.Add(Exact(v.X)).Add(Exact(v.Y)).Add(Exact(v.Z));

    private static void AddPlane(CanonicalHash h, Plane p)
    {
        Add(h, p.Origin);
        AddUnit(h, p.XAxis);
        AddUnit(h, p.YAxis);
    }

    private static void AddBox(CanonicalHash h, BoundingBox box)
    {
        h.Add(box.IsValid ? 1L : 0L);
        if (box.IsValid) { Add(h, box.Min); Add(h, box.Max); }
    }

    private static CanonicalHash AddXform(CanonicalHash h, Transform x)
    {
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                if (c == 3 && r < 3) h.Add(x[r, c]);
                else h.Add(Exact(x[r, c]));
        return h;
    }

    private static void AddKnots(CanonicalHash h, int count, Func<int, double> knot)
    {
        h.Add((long)count);
        if (count == 0) return;
        double k0 = knot(0), span = knot(count - 1) - k0;
        for (int i = 0; i < count; i++)
            h.Add(Exact(span > 0 ? (knot(i) - k0) / span : 0));
    }

    private static void AddCurve(CanonicalHash h, Curve c)
    {
        using var n = c.ToNurbsCurve();
        if (n is null) { h.Add("no-nurbs"); return; }
        h.Add((long)n.Degree).Add((long)n.Points.Count).Add(n.IsRational ? 1L : 0L);
        for (int i = 0; i < n.Points.Count; i++)
        {
            var cp = n.Points[i];
            Add(h, cp.Location);
            if (n.IsRational) h.Add(Exact(cp.Weight));
        }
        AddKnots(h, n.Knots.Count, i => n.Knots[i]);
    }

    private static void AddSurface(CanonicalHash h, Surface s)
    {
        using var n = s.ToNurbsSurface();
        if (n is null) { h.Add("no-nurbs"); return; }
        int cu = n.Points.CountU, cv = n.Points.CountV;
        h.Add((long)n.Degree(0)).Add((long)n.Degree(1)).Add((long)cu).Add((long)cv).Add(n.IsRational ? 1L : 0L);
        for (int u = 0; u < cu; u++)
            for (int v = 0; v < cv; v++)
            {
                var cp = n.Points.GetControlPoint(u, v);
                Add(h, cp.Location);
                if (n.IsRational) h.Add(Exact(cp.Weight));
            }
        AddKnots(h, n.KnotsU.Count, i => n.KnotsU[i]);
        AddKnots(h, n.KnotsV.Count, i => n.KnotsV[i]);
    }

    /// <summary>Vertices, edges (3D curves and their vertices), and faces with their loops and trims (by edge).</summary>
    private static void AddBrep(CanonicalHash h, Brep b)
    {
        h.Add((long)b.Vertices.Count).Add((long)b.Edges.Count).Add((long)b.Faces.Count).Add((long)b.Loops.Count).Add((long)b.Trims.Count);
        foreach (var v in b.Vertices) Add(h, v.Location);
        foreach (var e in b.Edges)
        {
            AddCurve(h, e);
            h.Add((long)(e.StartVertex?.VertexIndex ?? -1)).Add((long)(e.EndVertex?.VertexIndex ?? -1));
        }
        foreach (var f in b.Faces)
        {
            AddSurface(h, f.UnderlyingSurface());
            h.Add(f.OrientationIsReversed ? 1L : 0L).Add((long)f.Loops.Count);
            foreach (var loop in f.Loops)
            {
                h.Add((long)loop.LoopType).Add((long)loop.Trims.Count);
                foreach (var t in loop.Trims)
                    h.Add((long)t.TrimType).Add((long)(t.Edge?.EdgeIndex ?? -1)).Add(t.IsReversed() ? 1L : 0L);
            }
        }
    }

    private static void AddExtrusion(CanonicalHash h, Extrusion e)
    {
        Add(h, e.PathStart);
        Add(h, e.PathEnd);
        h.Add(e.IsCappedAtBottom ? 1L : 0L).Add(e.IsCappedAtTop ? 1L : 0L);
        h.Add(e.IsMiteredAtStart ? 1L : 0L).Add(e.IsMiteredAtEnd ? 1L : 0L);
        AddUnit(h, e.MiterPlaneNormalAtStart);
        AddUnit(h, e.MiterPlaneNormalAtEnd);
        h.Add((long)e.ProfileCount);
        for (int i = 0; i < e.ProfileCount; i++)
            using (var profile = e.Profile3d(i, 0))
                if (profile is null) h.Add("no-profile");
                else AddCurve(h, profile);
    }

    private static void AddMesh(CanonicalHash h, Mesh m)
    {
        h.Add((long)m.Vertices.Count).Add((long)m.Faces.Count);
        for (int i = 0; i < m.Vertices.Count; i++) Add(h, m.Vertices.Point3dAt(i));
        foreach (var f in m.Faces) h.Add((long)f.A).Add((long)f.B).Add((long)f.C).Add((long)f.D);
    }

    /// <summary>A SubD's vertices in list order (the 8.20 vertex list is a linked list, not enumerable).</summary>
    public static IEnumerable<SubDVertex> Vertices(SubD s)
    {
        for (var v = s.Vertices.First; v is not null; v = v.Next)
            yield return v;
    }

    /// <summary>The control net: vertex points and tags, edges by vertex index with tags, faces by vertex index.</summary>
    private static void AddSubD(CanonicalHash h, SubD s)
    {
        var index = new Dictionary<uint, long>();
        foreach (var v in Vertices(s)) index[v.Id] = index.Count;
        h.Add((long)s.Vertices.Count).Add((long)s.Edges.Count).Add((long)s.Faces.Count);
        foreach (var v in Vertices(s)) { Add(h, v.ControlNetPoint); h.Add((long)v.Tag); }
        foreach (var e in s.Edges) h.Add(index[e.VertexFrom.Id]).Add(index[e.VertexTo.Id]).Add((long)e.Tag);
        foreach (var f in s.Faces)
        {
            h.Add((long)f.VertexCount);
            for (int i = 0; i < f.VertexCount; i++) h.Add(index[f.VertexAt(i).Id]);
        }
    }

    /// <summary>A hatch pattern by content: name, fill type and every line's angle, base point, offset and dashes.</summary>
    public static string PatternKey(HatchPattern p)
    {
        static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        var lines = p.HatchLines.Select(l =>
            $"{N(l.Angle)},{N(l.BasePoint.X)},{N(l.BasePoint.Y)},{N(l.Offset.X)},{N(l.Offset.Y)}:{string.Join(",", l.GetDashes.Select(N))}");
        return $"{p.Name}|{p.FillType}|{string.Join(";", lines)}";
    }

    /// <summary>The editable properties of a light: style, on/off, intensity, colours, placement and spot shape.</summary>
    private static void AddLight(CanonicalHash h, Light l)
    {
        h.Add((long)l.LightStyle).Add(l.IsEnabled ? 1L : 0L).Add(Exact(l.Intensity)).Add(Exact(l.ShadowIntensity));
        foreach (var c in new[] { l.Diffuse, l.Specular, l.Ambient }) h.Add((long)c.ToArgb());
        Add(h, l.Location);
        foreach (var v in new[] { l.Direction, l.Length, l.Width }) h.Add(v.X).Add(v.Y).Add(v.Z);
        h.Add(Exact(l.SpotAngleRadians)).Add(Exact(l.HotSpot)).Add(Exact(l.SpotExponent)).Add(Exact(l.PowerWatts));
        var at = l.AttenuationVector;
        h.Add(Exact(at.X)).Add(Exact(at.Y)).Add(Exact(at.Z));
    }

    private static void AddHatch(CanonicalHash h, Hatch hatch, HashNames names)
    {
        // Built-in patterns (Solid -1, Hatch1 -2, ...) aren't in the table: their index is their identity.
        string pattern = names.HatchPatterns.TryGetValue(hatch.PatternIndex, out var key) ? key : "built-in " + hatch.PatternIndex;
        h.Add(pattern).Add(Exact(hatch.PatternScale)).Add(Exact(hatch.PatternRotation));
        AddPlane(h, hatch.Plane);
        Add(h, hatch.BasePoint);
        foreach (bool outer in new[] { true, false })
        {
            var curves = hatch.Get3dCurves(outer) ?? [];
            h.Add((long)curves.Length);
            foreach (var c in curves)
                using (c) AddCurve(h, c);
        }
    }
}
