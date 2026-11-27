namespace DittoHopper.Core;

/// <summary>
/// Answers the geometry questions a snapshot can't: how far apart a pair's shapes are and which points
/// correspond. The Rhino adapter implements it; <see cref="ModelDiff.Diff"/> works without one, leaving every
/// hash-differing pair <see cref="GeometryLevel.Unclassified"/>. Ids are snapshot object ids, and B is measured
/// in A's units. Calls for different pairs run in parallel, so an implementation must allow that.
/// </summary>
public interface IGeometryService
{
    /// <summary>
    /// The two-sided distance between A's shape, moved by <paramref name="moveA"/> when given, and B's; null when
    /// the pair can't be measured. Once it is known to exceed <paramref name="tolerance"/> an implementation may
    /// stop, so a Max above the tolerance is only a lower bound.
    /// </summary>
    ShapeDeviation? Deviation(Guid a, Guid b, Xform? moveA, double tolerance);

    /// <summary>The same points on A and on B, index by index (control points or vertices); null when the structures differ.</summary>
    PointCorrespondence? CorrespondingPoints(Guid a, Guid b);

    /// <summary>Why a same-shape pair's data differs, best effort: "rebuilt", "direction flipped", "seam moved" and so on.</summary>
    string? DataOnlyReason(Guid a, Guid b);
}

public readonly record struct ShapeDeviation(double Max, double? Mean = null);

public sealed record PointCorrespondence(IReadOnlyList<Vec3> A, IReadOnlyList<Vec3> B);

/// <summary>
/// Geometry ladder steps 2 and 3, for a matched pair whose hash differs (step 1). Step 2: the same shape within
/// shape tolerance means Data-only. Step 3: a rigid fit of the corresponding points, accepted when both the
/// fit's residual and the moved shape's deviation are within shape tolerance, means Moved. Anything else stays
/// Unclassified for steps 4–5.
/// </summary>
internal static class GeometryLadder
{
    public static GeometryChange Classify(DiffContext ctx, IGeometryService? service, ObjectRecord a, ObjectRecord b)
    {
        var change = new GeometryChange { A = a.Id, B = b.Id, Level = GeometryLevel.Unclassified };
        if (service is null)
            return change;
        double tol = ctx.ShapeTolerance;

        // Equal shapes have bounding boxes within tolerance, so this cheap test skips the sampling for most moves.
        if (BoxesWithin(a.Geometry.BBox, b.Geometry.BBox, ctx.Scale, tol)
            && service.Deviation(a.Id, b.Id, null, tol) is { } same && same.Max <= tol)
        {
            return change with
            {
                Level = GeometryLevel.DataOnly,
                Detail = service.DataOnlyReason(a.Id, b.Id) ?? "same shape",
                MaxDeviation = Round(same.Max, tol),
                MeanDeviation = same.Mean is double mean ? Round(mean, tol) : null,
            };
        }

        if (service.CorrespondingPoints(a.Id, b.Id) is { } points
            && RigidFit.Fit(points.A, points.B, tol) is { } fit
            && !fit.Transform.AlmostEquals(Xform.Identity, tol)
            && service.Deviation(a.Id, b.Id, fit.Transform, tol) is { } moved && moved.Max <= tol)
        {
            return change with
            {
                Level = GeometryLevel.Moved,
                Detail = RigidFit.Describe(fit.Transform, ctx.Header.Units, tol),
                Rigid = fit.Transform,
                MaxDeviation = Round(moved.Max, tol),
                MeanDeviation = moved.Mean is double mean ? Round(mean, tol) : null,
            };
        }
        return change;
    }

    private static bool BoxesWithin(BBox a, BBox b, double scale, double tol)
    {
        double margin = 2 * tol;
        return Vec3.Distance(a.Min, b.Min * scale) <= margin * Math.Sqrt(3) && Vec3.Distance(a.Max, b.Max * scale) <= margin * Math.Sqrt(3);
    }

    /// <summary>Deviations to a thousandth of the tolerance: finer digits are sampling noise and would churn golden files.</summary>
    private static double Round(double v, double tol)
    {
        int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(tol)) + 3, 0, 15);
        return Math.Round(v, decimals);
    }
}
