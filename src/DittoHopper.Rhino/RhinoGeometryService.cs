using DittoHopper.Core;
using Rhino.Geometry;

namespace DittoHopper.Rhino;

/// <summary>
/// Answers the geometry ladder's questions from two snapshots' geometry. Deviation is two-sided: samples on
/// each shape are measured to the other (Greville points and the spans between them, so a moved control
/// point is seen), plus Rhino's curve-to-curve distance and, for Breps, the naked edges, so a removed face or
/// a new hole can't hide inside the other shape. Types with no shape to sample (text, dots, hatches) compare
/// their canonical hash after the move. Each pair's geometry is touched by one call at a time.
/// </summary>
public sealed class RhinoGeometryService : IGeometryService
{
    private readonly RhinoSnapshot a, b;
    /// <summary>Multiplies B lengths into A's units.</summary>
    private readonly double scale;
    /// <summary>A's absolute tolerance, for the Data-only reason's structural comparisons.</summary>
    private readonly double tolerance;

    public RhinoGeometryService(RhinoSnapshot a, RhinoSnapshot b)
    {
        this.a = a;
        this.b = b;
        scale = Units.MetersPer(a.Model.Info.Units) is double ma && Units.MetersPer(b.Model.Info.Units) is double mb ? mb / ma : 1;
        tolerance = a.Model.Info.AbsoluteTolerance;
    }

    public ShapeDeviation? Deviation(Guid ia, Guid ib, Xform? moveA, double tolerance)
    {
        if (!a.Geometry.TryGetValue(ia, out var ea) || !b.Geometry.TryGetValue(ib, out var eb))
            return null;
        using var movedA = moveA is null ? null : Transformed(ea.Geometry, ToRhino(moveA));
        using var scaledB = scale == 1 ? null : Transformed(eb.Geometry, Transform.Scale(Point3d.Origin, scale));
        var x = movedA ?? ea.Geometry;
        var y = scaledB ?? eb.Geometry;
        return Measure.Deviation(x, y, tolerance) ?? SameHash(x, eb.Hash);
    }

    public PointCorrespondence? CorrespondingPoints(Guid ia, Guid ib)
    {
        if (!a.Geometry.TryGetValue(ia, out var ea) || !b.Geometry.TryGetValue(ib, out var eb))
            return null;
        // Frames of text and hatches need one arm length on both sides; A's size sets it.
        var box = ea.Geometry.GetBoundingBox(true);
        double arm = Math.Max(box.IsValid ? box.Diagonal.Length : 1, 1);
        var pa = Measure.Points(ea.Geometry, arm);
        var pb = Measure.Points(eb.Geometry, arm / scale);
        if (pa is null || pb is null || pa.Count != pb.Count)
            return null;
        return new PointCorrespondence([.. pa.Select(GeometryHasher.Vec)], [.. pb.Select(p => GeometryHasher.Vec(p) * scale)]);
    }

    public string? DataOnlyReason(Guid ia, Guid ib)
    {
        if (!a.Geometry.TryGetValue(ia, out var ea) || !b.Geometry.TryGetValue(ib, out var eb))
            return null;
        using var scaledB = scale == 1 ? null : Transformed(eb.Geometry, Transform.Scale(Point3d.Origin, scale));
        var y = scaledB ?? eb.Geometry;
        return (ea.Geometry, y) switch
        {
            (Curve c, Curve d) => Measure.CurveReason(c, d, tolerance),
            (Extrusion, Brep) or (Brep, Extrusion) => "Extrusion ↔ Brep",
            (Brep x, Brep z) when x.Faces.Count != z.Faces.Count => "faces split or merged",
            (Mesh m, Mesh n) when m.Vertices.Count != n.Vertices.Count || m.Faces.Count != n.Faces.Count => "remeshed",
            _ when CorrespondingPoints(ia, ib) is { } p && p.A.Zip(p.B).All(q => Vec3.Distance(q.First, q.Second) <= tolerance) => "within tolerance",
            _ => "rebuilt",
        };
    }

    /// <summary>For text, dots, hatches and other unsampled types: equal canonical hashes on B's grid mean the same thing.</summary>
    private ShapeDeviation? SameHash(GeometryBase x, ulong hashB)
    {
        using var inB = scale == 1 ? null : Transformed(x, Transform.Scale(Point3d.Origin, 1 / scale));
        return GeometryHasher.Hash(inB ?? x, b.Model.Info.HashGrid, a.Names) == hashB ? new ShapeDeviation(0) : null;
    }

    private static GeometryBase Transformed(GeometryBase g, Transform x)
    {
        var copy = g.Duplicate();
        copy.Transform(x);
        return copy;
    }

    private static Transform ToRhino(Xform x)
    {
        var t = new Transform(1);
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                t[r, c] = x[r, c];
        return t;
    }
}

/// <summary>Sampling and structure helpers behind <see cref="RhinoGeometryService"/>.</summary>
internal static class Measure
{
    private const int MinSamples = 5, MaxSamples = 81;

    public static ShapeDeviation? Deviation(GeometryBase x, GeometryBase y, double tol) => (x, y) switch
    {
        (Point p, Point q) => new ShapeDeviation(p.Location.DistanceTo(q.Location)),
        (PointCloud p, PointCloud q) when p.Count == q.Count =>
            new ShapeDeviation(p.GetPoints().Zip(q.GetPoints()).Max(z => z.First.DistanceTo(z.Second))),
        (Curve c, Curve d) => Curves(c, d, tol),
        (Mesh m, Mesh n) => Meshes(m, n, tol),
        _ => Breps(x, y, tol),
    };

    /// <summary>Points that move with the shape, index by index: control points, vertices or a frame.</summary>
    public static IReadOnlyList<Point3d>? Points(GeometryBase g, double arm)
    {
        switch (g)
        {
            case Point p: return [p.Location];
            case PointCloud pc: return pc.GetPoints();
            case TextDot d: return [d.Point];
            case AnnotationBase t: return Frame(t.Plane, arm);
            case Hatch h: return Frame(h.Plane, arm);
            case Curve c:
                using (var n = c.ToNurbsCurve())
                    return n is null ? null : [.. Enumerable.Range(0, n.Points.Count).Select(i => n.Points[i].Location)];
            case Brep brep: return BrepPoints(brep);
            case Extrusion e:
                using (var brep = e.ToBrep(true))
                    return brep is null ? null : BrepPoints(brep);
            case Surface s:
                using (var n = s.ToNurbsSurface())
                {
                    if (n is null) return null;
                    var list = new List<Point3d>();
                    for (int u = 0; u < n.Points.CountU; u++)
                        for (int v = 0; v < n.Points.CountV; v++) list.Add(n.Points.GetControlPoint(u, v).Location);
                    return list;
                }
            case Mesh m: return m.Vertices.ToPoint3dArray();
            case SubD s: return [.. GeometryHasher.Vertices(s).Select(v => v.ControlNetPoint)];
            default: return null;
        }
    }

    /// <summary>
    /// Best effort, from geometric facts first: NURBS ↔ polyline, a closed curve's seam moved, its direction
    /// flipped; then from structure: same control points (reparameterized, or just within tolerance), knots
    /// added or removed, else rebuilt.
    /// </summary>
    public static string CurveReason(Curve c, Curve d, double tol)
    {
        var reasons = new List<string>();
        if (IsPolyline(c) != IsPolyline(d)) reasons.Add("NURBS ↔ polyline");
        if (c.IsClosed && d.IsClosed && c.PointAtStart.DistanceTo(d.PointAtStart) > tol) reasons.Add("seam moved");
        if (Flipped(c, d)) reasons.Add("direction flipped");
        if (reasons.Count > 0) return string.Join(", ", reasons);

        using var nc = c.ToNurbsCurve();
        using var nd = d.ToNurbsCurve();
        if (nc is null || nd is null || nc.Degree != nd.Degree) return "rebuilt";
        var kc = Knots(nc);
        var kd = Knots(nd);
        if (nc.Points.Count == nd.Points.Count)
        {
            bool sameCvs = Enumerable.Range(0, nc.Points.Count).All(i => nc.Points[i].Location.DistanceTo(nd.Points[i].Location) <= tol);
            if (!sameCvs) return "rebuilt";
            return kc.Zip(kd).All(k => Math.Abs(k.First - k.Second) < 1e-9) ? "within tolerance" : "reparameterized";
        }
        var (fewer, more) = nc.Points.Count < nd.Points.Count ? (kc, kd) : (kd, kc);
        bool superset = fewer.Distinct().All(k => more.Any(m => Math.Abs(m - k) < 1e-9));
        return !superset ? "rebuilt" : nc.Points.Count < nd.Points.Count ? "knots added" : "knots removed";
    }

    private static ShapeDeviation? Curves(Curve c, Curve d, double tol)
    {
        double limit = 2 * tol, max = 0, sum = 0;
        int n = 0;
        foreach (var (from, to) in new[] { (c, d), (d, c) })
            foreach (var p in CurveSamples(from))
            {
                if (!to.ClosestPoint(p, out double t, limit)) return new ShapeDeviation(limit);
                double dist = p.DistanceTo(to.PointAt(t));
                (max, sum, n) = (Math.Max(max, dist), sum + dist, n + 1);
            }
        if (Curve.GetDistancesBetweenCurves(c, d, tol / 10, out double rhinoMax, out _, out _, out _, out _, out _))
            max = Math.Max(max, rhinoMax);
        return new ShapeDeviation(max, n > 0 ? sum / n : null);
    }

    private static ShapeDeviation? Meshes(Mesh m, Mesh n, double tol)
    {
        double limit = 2 * tol, max = 0, sum = 0;
        int count = 0;
        foreach (var (from, to) in new[] { (m, n), (n, m) })
        {
            var samples = from.Vertices.ToPoint3dArray().Concat(Enumerable.Range(0, from.Faces.Count).Select(i => from.Faces.GetFaceCenter(i)));
            foreach (var p in samples)
            {
                if (to.ClosestPoint(p, out var cp, limit) < 0) return new ShapeDeviation(limit);
                double dist = p.DistanceTo(cp);
                (max, sum, count) = (Math.Max(max, dist), sum + dist, count + 1);
            }
        }
        return new ShapeDeviation(max, count > 0 ? sum / count : null);
    }

    /// <summary>Surfaces, polysurfaces, extrusions and SubDs, compared as Breps. Null for anything else.</summary>
    private static ShapeDeviation? Breps(GeometryBase x, GeometryBase y, double tol)
    {
        using var ownedX = AsBrep(x, out var bx);
        using var ownedY = AsBrep(y, out var by);
        if (bx is null || by is null) return null;

        double limit = 2 * tol, max = 0, sum = 0;
        int n = 0;
        foreach (var (from, to) in new[] { (bx, by), (by, bx) })
            foreach (var p in BrepSamples(from))
            {
                if (!to.ClosestPoint(p, out var cp, out _, out _, out _, limit, out _)) return new ShapeDeviation(limit);
                double dist = p.DistanceTo(cp);
                (max, sum, n) = (Math.Max(max, dist), sum + dist, n + 1);
            }

        // A hole or a missing face can lie inside the other shape's surface; the boundaries still differ.
        var nakedX = bx.DuplicateNakedEdgeCurves(true, true) ?? [];
        var nakedY = by.DuplicateNakedEdgeCurves(true, true) ?? [];
        try
        {
            foreach (var (from, to) in new[] { (nakedX, nakedY), (nakedY, nakedX) })
                foreach (var p in from.SelectMany(CurveSamples))
                {
                    double best = double.PositiveInfinity;
                    foreach (var e in to)
                        if (e.ClosestPoint(p, out double t, limit)) best = Math.Min(best, p.DistanceTo(e.PointAt(t)));
                    if (best > limit) return new ShapeDeviation(limit);
                    max = Math.Max(max, best);
                }
        }
        finally
        {
            foreach (var e in nakedX.Concat(nakedY)) e.Dispose();
        }
        return new ShapeDeviation(max, n > 0 ? sum / n : null);
    }

    /// <summary>The Brep form of a surface-like geometry; returns the copy to dispose, if one was made.</summary>
    private static Brep? AsBrep(GeometryBase g, out Brep? brep)
    {
        brep = g switch
        {
            Brep b => b,
            Extrusion e => e.ToBrep(true),
            Surface s => s.ToBrep(),
            SubD s => s.ToBrep(),
            _ => null,
        };
        return ReferenceEquals(brep, g) ? null : brep;
    }

    /// <summary>Per face, a grid at the Greville parameters and halfway between them, inside the trims; plus each edge.</summary>
    private static IEnumerable<Point3d> BrepSamples(Brep brep)
    {
        foreach (var face in brep.Faces)
        {
            using var n = face.UnderlyingSurface().ToNurbsSurface();
            if (n is null) continue;
            var us = Spread(Enumerable.Range(0, n.Points.CountU).Select(i => n.Points.GetGrevillePoint(i, 0).X), n.Domain(0), face.Domain(0));
            var vs = Spread(Enumerable.Range(0, n.Points.CountV).Select(j => n.Points.GetGrevillePoint(0, j).Y), n.Domain(1), face.Domain(1));
            foreach (double u in us)
                foreach (double v in vs)
                    if (face.IsPointOnFace(u, v) != PointFaceRelation.Exterior)
                        yield return face.PointAt(u, v);
        }
        foreach (var edge in brep.Edges)
            foreach (var p in CurveSamples(edge))
                yield return p;
    }

    private static IEnumerable<Point3d> CurveSamples(Curve c)
    {
        using var n = c.ToNurbsCurve();
        if (n is null) yield break;
        foreach (double t in Spread(n.GrevilleParameters(), n.Domain, n.Domain))
            yield return n.PointAt(t);
    }

    /// <summary>
    /// Parameters at the Greville abscissae (where each control point pulls hardest) and halfway between, mapped
    /// from the NURBS domain onto the target domain; evenly spaced when there are too few or too many.
    /// </summary>
    private static List<double> Spread(IEnumerable<double> greville, Interval from, Interval to)
    {
        var g = greville.Select(from.NormalizedParameterAt).Order().ToList();
        var r = new List<double>();
        for (int i = 0; i < g.Count; i++)
        {
            if (i > 0) r.Add((g[i - 1] + g[i]) / 2);
            r.Add(g[i]);
        }
        if (r.Count < MinSamples || r.Count > MaxSamples)
        {
            int count = Math.Clamp(r.Count, MinSamples, MaxSamples);
            r = [.. Enumerable.Range(0, count).Select(i => i / (count - 1.0))];
        }
        return [.. r.Select(to.ParameterAt)];
    }

    private static List<Point3d> BrepPoints(Brep brep) =>
        [.. brep.Vertices.Select(v => v.Location), .. brep.Edges.Select(e => e.PointAt(e.Domain.Mid))];

    private static Point3d[] Frame(Plane p, double arm) =>
        [p.Origin, p.Origin + p.XAxis * arm, p.Origin + p.YAxis * arm, p.Origin + p.ZAxis * arm];

    /// <summary>Degree 1: Rhino's IsPolyline() also accepts a straight curve of any degree.</summary>
    private static bool IsPolyline(Curve c) => c.Degree == 1;

    /// <summary>True when the curves run opposite ways: compare tangents where B's middle meets A.</summary>
    private static bool Flipped(Curve c, Curve d)
    {
        double mid = d.Domain.Mid;
        if (!c.ClosestPoint(d.PointAt(mid), out double t)) return false;
        return c.TangentAt(t) * d.TangentAt(mid) < 0;
    }

    private static List<double> Knots(NurbsCurve n)
    {
        double k0 = n.Knots[0], span = n.Knots[n.Knots.Count - 1] - k0;
        return [.. Enumerable.Range(0, n.Knots.Count).Select(i => span > 0 ? (n.Knots[i] - k0) / span : 0)];
    }
}
