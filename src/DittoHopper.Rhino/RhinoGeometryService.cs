using DittoHopper.Core;
using Rhino.Geometry;

namespace DittoHopper.Rhino;

/// <summary>
/// Answers the geometry ladder's questions from two snapshots' geometry. A deviation is only reported when
/// the two shapes also have the same topology summary (closed or solid, boundary-loop count) and the same
/// length, area and volume within a tolerance-scaled bound; otherwise the pair is not the same shape. The
/// deviation itself is two-sided: samples on each shape are measured to the other. Samples cover every
/// non-empty knot span and the Greville points (where each control point pulls hardest); when that needs more
/// samples than the budget, the shape is not measured, so the ladder never claims Data-only or Moved on a
/// guess. Breps and meshes also compare their naked edges both ways, so a removed face or a new or moved hole
/// can't hide inside the other shape. Types with no shape to sample (text, dots, hatches, lights) compare their
/// canonical hash after the move, when the hash grid is fine enough. Each pair's geometry is touched by one call at a time.
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
        _ = a.Geometry; // throws if either snapshot was disposed
        _ = b.Geometry;
    }

    public ShapeDeviation? Deviation(Guid ia, Guid ib, Xform? moveA, double tolerance)
    {
        if (!a.Geometry.TryGetValue(ia, out var ea) || !b.Geometry.TryGetValue(ib, out var eb))
            return null;
        using var movedA = moveA is null ? null : Transformed(ea.Geometry, ToRhino(moveA));
        using var scaledB = scale == 1 ? null : Transformed(eb.Geometry, Transform.Scale(Point3d.Origin, scale));
        var x = movedA ?? ea.Geometry;
        var y = scaledB ?? eb.Geometry;
        if (Measure.Deviation(x, y, tolerance) is not { } d)
            return SameHash(x, eb.Signature.Hash, tolerance);
        // The snapshots measured both summaries once; a rigid move doesn't change them, and B's scale into A's units.
        return Measure.SameSummary(x, ea.Signature, y, eb.Signature, scale, tolerance) ? d : null;
    }

    public PointCorrespondence? CorrespondingPoints(Guid ia, Guid ib)
    {
        if (!a.Geometry.TryGetValue(ia, out var ea) || !b.Geometry.TryGetValue(ib, out var eb))
            return null;
        // Frames of text, hatches and lights need one arm length on both sides; A's size sets it.
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

    public StructureArrays? Structure(Guid ia, Guid ib)
    {
        if (!a.Geometry.TryGetValue(ia, out var ea) || !b.Geometry.TryGetValue(ib, out var eb))
            return null;
        using var scaledB = scale == 1 ? null : Transformed(eb.Geometry, Transform.Scale(Point3d.Origin, scale));
        return Measure.Structure(ea.Geometry, scaledB ?? eb.Geometry);
    }

    /// <summary>
    /// For text, dots, hatches, lights, and shapes over the sample budget: equal canonical hashes on B's grid mean
    /// the same thing, but only when that grid is no coarser than the shape tolerance; otherwise nothing is claimed.
    /// </summary>
    private ShapeDeviation? SameHash(GeometryBase x, ulong hashB, double tolerance)
    {
        if (b.Model.Info.HashGrid * scale > tolerance * (1 + 1e-9))
            return null;
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

/// <summary>Sampling, summaries and structure helpers behind <see cref="RhinoGeometryService"/>.</summary>
internal static class Measure
{
    private const int MinSamples = 5;
    /// <summary>Sample budgets: per curve or surface direction, and per face grid. Beyond them a shape isn't measured.</summary>
    private const int MaxPerDirection = 1000, MaxPerFace = 40_000;

    /// <summary>Two-sided deviation; null for types that aren't sampled or shapes that would exceed the budget.</summary>
    public static ShapeDeviation? Deviation(GeometryBase x, GeometryBase y, double tol) => (x, y) switch
    {
        (Point p, Point q) => new ShapeDeviation(p.Location.DistanceTo(q.Location)),
        (PointCloud p, PointCloud q) when p.Count == q.Count =>
            new ShapeDeviation(p.GetPoints().Zip(q.GetPoints()).Max(z => z.First.DistanceTo(z.Second))),
        (Curve c, Curve d) => Curves(c, d, tol),
        (Mesh m, Mesh n) => Meshes(m, n, tol),
        _ => Breps(x, y, tol),
    };

    /// <summary>
    /// The same shape must have the same topology summary and nearly the same measures. Lengths may differ by
    /// 2·tol + 1e-4·L; areas by tol·(2·boundary + 8·√A) + 1e-6·A, since moving a surface by up to tol changes
    /// its area through its boundary and its curvature; volumes by 2·tol·A + 1e-6·V. Summaries and measures come
    /// from the snapshot signatures, B's scaled by <paramref name="scale"/>; the geometries only say the types.
    /// </summary>
    public static bool SameSummary(GeometryBase x, GeometrySignature sx, GeometryBase y, GeometrySignature sy, double scale, double tol)
    {
        if (!HasTopology(x) || !HasTopology(y))
            return true;   // points, clouds and the like have no topology to compare
        if (sx.Closed != sy.Closed || sx.BoundaryLoops != sy.BoundaryLoops)
            return false;
        var (length, area, volume) = (sy.Length * scale, sy.Area * scale * scale, sy.Volume * scale * scale * scale);
        double boundary = Math.Max(sx.BoundaryLength, sy.BoundaryLength * scale), larger = Math.Max(sx.Area, area);
        return Math.Abs(sx.Length - length) <= 2 * tol + 1e-4 * Math.Max(sx.Length, length)
            && Math.Abs(sx.Area - area) <= tol * (2 * boundary + 8 * Math.Sqrt(larger)) + 1e-6 * larger
            && Math.Abs(sx.Volume - volume) <= 2 * tol * larger + 1e-6 * Math.Max(sx.Volume, volume);
    }

    private static bool HasTopology(GeometryBase g) => g is Curve or Mesh or Brep or Extrusion or Surface or SubD;

    /// <summary>
    /// The topology summary the snapshot signature stores: closed (or solid), faces, boundary loops (naked edges
    /// joined within <paramref name="tol"/>) and their total length. Null for types without topology.
    /// </summary>
    public static (bool Closed, int Faces, int Loops, double Boundary)? Summary(GeometryBase g, double tol)
    {
        switch (g)
        {
            case Curve c:
                return (c.IsClosed, 0, 0, 0);
            case Mesh m:
                var naked = m.GetNakedEdges() ?? [];
                return (m.IsClosed, m.Faces.Count, naked.Length, naked.Sum(p => p.Length));
            default:
                using (AsBrep(g, out var brep))
                {
                    if (brep is null) return null;
                    var edges = brep.DuplicateNakedEdgeCurves(true, true) ?? [];
                    try
                    {
                        var loops = edges.Length == 0 ? [] : Curve.JoinCurves(edges, tol) ?? [];
                        int count = loops.Length;
                        foreach (var l in loops) l.Dispose();
                        return (brep.IsSolid, brep.Faces.Count, count, edges.Sum(e => e.GetLength()));
                    }
                    finally
                    {
                        foreach (var e in edges) e.Dispose();
                    }
                }
        }
    }

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
            case Light l: return [l.Location, l.Location + l.Direction, l.Location + l.Length, l.Location + l.Width];
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
    /// Best effort, from geometric facts first: the curve type changed (line ↔ NURBS, NURBS ↔ polyline), a
    /// closed curve's seam moved, its direction flipped; then from structure: same control points with a new
    /// domain or knot spacing (reparameterized) or not (within tolerance), knots added or removed, else rebuilt.
    /// </summary>
    public static string CurveReason(Curve c, Curve d, double tol)
    {
        var reasons = new List<string>();
        string tc = TypeLabel(c), td = TypeLabel(d);
        if (tc != td) reasons.Add($"{tc} ↔ {td}");
        else if (IsPolyline(c) != IsPolyline(d)) reasons.Add("NURBS ↔ polyline");
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
            bool sameDomain = Math.Abs(c.Domain.T0 - d.Domain.T0) < 1e-9 && Math.Abs(c.Domain.T1 - d.Domain.T1) < 1e-9;
            return sameDomain && kc.Zip(kd).All(k => Math.Abs(k.First - k.Second) < 1e-9) ? "within tolerance" : "reparameterized";
        }
        var (fewer, more) = nc.Points.Count < nd.Points.Count ? (kc, kd) : (kd, kc);
        bool superset = fewer.Distinct().All(k => more.Any(m => Math.Abs(m - k) < 1e-9));
        return !superset ? "rebuilt" : nc.Points.Count < nd.Points.Count ? "knots added" : "knots removed";
    }

    private static string TypeLabel(Curve c) => c switch
    {
        LineCurve => "line",
        PolylineCurve => "polyline",
        ArcCurve => "arc",
        NurbsCurve => "NURBS",
        PolyCurve => "polycurve",
        _ => c.GetType().Name,
    };

    private static ShapeDeviation? Curves(Curve c, Curve d, double tol)
    {
        if (CurveSamples(c) is not { } sc || CurveSamples(d) is not { } sd)
            return null;
        double limit = 2 * tol, max = 0, sum = 0;
        int n = 0;
        foreach (var (from, to) in new[] { (sc, d), (sd, c) })
            foreach (var p in from)
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
        var boundary = Boundaries([.. (m.GetNakedEdges() ?? []).Select(p => new PolylineCurve(p))],
            [.. (n.GetNakedEdges() ?? []).Select(p => new PolylineCurve(p))], limit);
        return boundary is double b ? new ShapeDeviation(Math.Max(max, b), count > 0 ? sum / count : null) : null;
    }

    /// <summary>
    /// The two-sided distance between two sets of boundary curves (naked edges), up to <paramref name="limit"/>.
    /// A hole, moved or new, lies inside the other shape's surface, but its boundary has nothing near it on the
    /// other side. Null when the boundaries are too detailed to sample. Disposes the curves.
    /// </summary>
    private static double? Boundaries(Curve[] x, Curve[] y, double limit)
    {
        try
        {
            double max = 0;
            foreach (var (from, to) in new[] { (x, y), (y, x) })
                foreach (var e in from)
                {
                    if (CurveSamples(e) is not { } samples) return null;
                    foreach (var p in samples)
                    {
                        double best = double.PositiveInfinity;
                        foreach (var other in to)
                            if (other.ClosestPoint(p, out double t, limit)) best = Math.Min(best, p.DistanceTo(other.PointAt(t)));
                        if (best > limit) return limit;
                        max = Math.Max(max, best);
                    }
                }
            return max;
        }
        finally
        {
            foreach (var e in x.Concat(y)) e.Dispose();
        }
    }

    /// <summary>Surfaces, polysurfaces, extrusions and SubDs, compared as Breps. Null for anything else, or over budget.</summary>
    private static ShapeDeviation? Breps(GeometryBase x, GeometryBase y, double tol)
    {
        using var ownedX = AsBrep(x, out var bx);
        using var ownedY = AsBrep(y, out var by);
        if (bx is null || by is null || BrepSamples(bx) is not { } sx || BrepSamples(by) is not { } sy)
            return null;

        double limit = 2 * tol, max = 0, sum = 0;
        int n = 0;
        foreach (var (from, to) in new[] { (sx, by), (sy, bx) })
            foreach (var p in from)
            {
                if (!to.ClosestPoint(p, out var cp, out _, out _, out _, limit, out _)) return new ShapeDeviation(limit);
                double dist = p.DistanceTo(cp);
                (max, sum, n) = (Math.Max(max, dist), sum + dist, n + 1);
            }

        var boundary = Boundaries(bx.DuplicateNakedEdgeCurves(true, true) ?? [], by.DuplicateNakedEdgeCurves(true, true) ?? [], limit);
        return boundary is double b ? new ShapeDeviation(Math.Max(max, b), n > 0 ? sum / n : null) : null;
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

    /// <summary>Per face, a grid covering every knot span and Greville point, inside the trims; plus each edge. Null when over budget.</summary>
    private static List<Point3d>? BrepSamples(Brep brep)
    {
        var samples = new List<Point3d>();
        foreach (var face in brep.Faces)
        {
            using var n = face.UnderlyingSurface().ToNurbsSurface();
            if (n is null) return null;
            var us = Spread(Enumerable.Range(0, n.Points.CountU).Select(i => n.Points.GetGrevillePoint(i, 0).X),
                Enumerable.Range(0, n.KnotsU.Count).Select(i => n.KnotsU[i]), n.Domain(0), face.Domain(0));
            var vs = Spread(Enumerable.Range(0, n.Points.CountV).Select(j => n.Points.GetGrevillePoint(0, j).Y),
                Enumerable.Range(0, n.KnotsV.Count).Select(i => n.KnotsV[i]), n.Domain(1), face.Domain(1));
            if (us is null || vs is null || us.Count * vs.Count > MaxPerFace) return null;
            foreach (double u in us)
                foreach (double v in vs)
                    if (face.IsPointOnFace(u, v) != PointFaceRelation.Exterior)
                        samples.Add(face.PointAt(u, v));
        }
        foreach (var edge in brep.Edges)
        {
            if (CurveSamples(edge) is not { } e) return null;
            samples.AddRange(e);
        }
        return samples;
    }

    /// <summary>Samples covering every knot span and Greville point of the curve; null when over budget.</summary>
    private static List<Point3d>? CurveSamples(Curve c)
    {
        using var n = c.ToNurbsCurve();
        if (n is null) return null;
        var ts = Spread(n.GrevilleParameters(), Enumerable.Range(0, n.Knots.Count).Select(i => n.Knots[i]), n.Domain, n.Domain);
        return ts is null ? null : [.. ts.Select(n.PointAt)];
    }

    /// <summary>
    /// Normalized parameters at the Greville abscissae and halfway between them, plus each non-empty knot span's
    /// ends and middle, so a control point moved anywhere is sampled where it pulls; at least a few evenly
    /// spaced ones. Mapped from the NURBS domain onto the target domain. Null when the budget can't cover them.
    /// </summary>
    private static List<double>? Spread(IEnumerable<double> greville, IEnumerable<double> knots, Interval from, Interval to)
    {
        var g = greville.Select(from.NormalizedParameterAt).Order().ToList();
        var r = new List<double>();
        for (int i = 0; i < g.Count; i++)
        {
            if (i > 0) r.Add((g[i - 1] + g[i]) / 2);
            r.Add(g[i]);
        }
        var k = knots.Select(from.NormalizedParameterAt).ToList();
        for (int i = 0; i + 1 < k.Count; i++)
            if (k[i + 1] - k[i] > 1e-12)
                r.AddRange([k[i], (k[i] + k[i + 1]) / 2, k[i + 1]]);
        r.AddRange(Enumerable.Range(0, MinSamples).Select(i => i / (MinSamples - 1.0)));
        var distinct = r.Where(t => t is >= 0 and <= 1).Order().Distinct().ToList();
        return distinct.Count > MaxPerDirection ? null : [.. distinct.Select(to.ParameterAt)];
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

    private static List<double> Knots(NurbsCurve n) => Normalized(n.Knots.Count, i => n.Knots[i]);

    private static List<double> Normalized(int count, Func<int, double> knot)
    {
        if (count == 0) return [];
        double k0 = knot(0), span = knot(count - 1) - k0;
        return [.. Enumerable.Range(0, count).Select(i => span > 0 ? (knot(i) - k0) / span : 0)];
    }

    private static bool Same(List<double> x, List<double> y) => x.Count == y.Count && x.Zip(y).All(k => Math.Abs(k.First - k.Second) < 1e-9);

    /// <summary>
    /// Ladder step 4's arrays: control points (curves, surfaces, each Brep face's grid in turn) or vertices
    /// (meshes, SubD control nets, point clouds), when both structures are the same; for two polylines, their
    /// vertices whatever the counts. Closed curves list each control point once, without the repeats that close them.
    /// </summary>
    public static StructureArrays? Structure(GeometryBase x, GeometryBase y)
    {
        switch (x, y)
        {
            case (Curve c, Curve d):
                using (var nc = c.ToNurbsCurve())
                using (var nd = d.ToNurbsCurve())
                {
                    if (nc is null || nd is null) return null;
                    bool polylines = nc.Degree == 1 && nd.Degree == 1;
                    bool same = nc.Degree == nd.Degree && nc.Points.Count == nd.Points.Count && nc.IsRational == nd.IsRational
                        && nc.IsPeriodic == nd.IsPeriodic && Same(Knots(nc), Knots(nd))
                        && Enumerable.Range(0, nc.Points.Count).All(i => Math.Abs(nc.Points[i].Weight - nd.Points[i].Weight) < 1e-9);
                    if (!same && !polylines) return null;
                    var order = c.IsClosed && d.IsClosed ? PointOrder.Closed : !c.IsClosed && !d.IsClosed ? PointOrder.Open : PointOrder.Fixed;
                    return new StructureArrays(CurvePoints(nc, order == PointOrder.Closed), CurvePoints(nd, order == PointOrder.Closed), same, polylines, order);
                }
            case (Mesh m, Mesh n):
                bool sameFaces = m.Vertices.Count == n.Vertices.Count && m.Faces.Count == n.Faces.Count
                    && Enumerable.Range(0, m.Faces.Count).All(i => m.Faces[i].Equals(n.Faces[i]));
                return sameFaces ? new StructureArrays(Vecs(m.Vertices.ToPoint3dArray()), Vecs(n.Vertices.ToPoint3dArray()), true, false, PointOrder.Fixed) : null;
            case (SubD s, SubD t):
                return SubDNet(s) is var (ps, fs) && SubDNet(t) is var (pt, ft) && fs.SequenceEqual(ft) && ps.Count == pt.Count
                    ? new StructureArrays(ps, pt, true, false, PointOrder.Fixed) : null;
            case (PointCloud p, PointCloud q):
                return p.Count == q.Count ? new StructureArrays(Vecs(p.GetPoints()), Vecs(q.GetPoints()), true, false, PointOrder.Fixed) : null;
            default:
                using (AsBrep(x, out var bx))
                using (AsBrep(y, out var by))
                    return bx is null || by is null ? null : FaceGrids(bx, by);
        }
    }

    private static List<Vec3> Vecs(IEnumerable<Point3d> points) => [.. points.Select(GeometryHasher.Vec)];

    private static List<Vec3> CurvePoints(NurbsCurve n, bool once)
    {
        var points = Enumerable.Range(0, n.Points.Count).Select(i => n.Points[i].Location).ToList();
        int repeats = !once ? 0 : n.IsPeriodic ? n.Degree : points.Count > 1 && points[0].DistanceTo(points[^1]) <= 1e-12 * (1 + points[0].DistanceTo(Point3d.Origin)) ? 1 : 0;
        return Vecs(points.Take(points.Count - repeats));
    }

    /// <summary>A SubD's control net points and its faces as vertex-index lists, both in vertex list order.</summary>
    private static (List<Vec3> Points, List<int> Faces) SubDNet(SubD s)
    {
        var vertices = GeometryHasher.Vertices(s).ToList();
        var index = new Dictionary<uint, int>();
        foreach (var v in vertices) index[v.Id] = index.Count;
        var faces = new List<int>();
        foreach (var f in s.Faces)
        {
            faces.Add(-f.VertexCount);
            for (int i = 0; i < f.VertexCount; i++) faces.Add(index[f.VertexAt(i).Id]);
        }
        return (Vecs(vertices.Select(v => v.ControlNetPoint)), faces);
    }

    /// <summary>Every face's underlying control grid in turn, when both Breps' faces have the same degrees, counts and knots.</summary>
    private static StructureArrays? FaceGrids(Brep x, Brep y)
    {
        if (x.Faces.Count != y.Faces.Count) return null;
        var (pa, pb) = (new List<Vec3>(), new List<Vec3>());
        for (int f = 0; f < x.Faces.Count; f++)
        {
            using var u = x.Faces[f].UnderlyingSurface().ToNurbsSurface();
            using var v = y.Faces[f].UnderlyingSurface().ToNurbsSurface();
            if (u is null || v is null || u.Degree(0) != v.Degree(0) || u.Degree(1) != v.Degree(1) || u.IsRational != v.IsRational
                || u.Points.CountU != v.Points.CountU || u.Points.CountV != v.Points.CountV
                || !Same(Normalized(u.KnotsU.Count, i => u.KnotsU[i]), Normalized(v.KnotsU.Count, i => v.KnotsU[i]))
                || !Same(Normalized(u.KnotsV.Count, i => u.KnotsV[i]), Normalized(v.KnotsV.Count, i => v.KnotsV[i])))
                return null;
            for (int i = 0; i < u.Points.CountU; i++)
                for (int j = 0; j < u.Points.CountV; j++)
                {
                    pa.Add(GeometryHasher.Vec(u.Points.GetControlPoint(i, j).Location));
                    pb.Add(GeometryHasher.Vec(v.Points.GetControlPoint(i, j).Location));
                }
        }
        return new StructureArrays(pa, pb, true, false, PointOrder.Fixed);
    }
}
