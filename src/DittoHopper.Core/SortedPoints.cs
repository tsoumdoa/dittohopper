namespace DittoHopper.Core;

/// <summary>
/// A minimal spatial index for matching pass 4: points sorted along their longest axis. A query walks outward
/// from its own position on that axis and stops once the gap on that axis alone exceeds the best distance.
/// Results depend only on the points, never on timing, and ties go to the lower index.
/// </summary>
internal sealed class SortedPoints
{
    private readonly IReadOnlyList<Vec3> points;
    private readonly int[] order;
    private readonly double[] keys;
    private readonly int axis;

    public SortedPoints(IReadOnlyList<Vec3> points)
    {
        this.points = points;
        if (points.Count > 0)
        {
            double Extent(int ax) => points.Max(p => Coord(p, ax)) - points.Min(p => Coord(p, ax));
            axis = Enumerable.Range(0, 3).OrderByDescending(Extent).ThenBy(ax => ax).First();
        }
        order = [.. Enumerable.Range(0, points.Count).OrderBy(i => Coord(points[i], axis)).ThenBy(i => i)];
        keys = [.. order.Select(i => Coord(points[i], axis))];
    }

    /// <summary>The nearest point within <paramref name="limit"/> that <paramref name="accept"/> allows, or (-1, ∞).</summary>
    public (int Index, double Distance) Nearest(Vec3 p, double limit, Func<int, bool> accept)
    {
        double c = Coord(p, axis);
        int hi = LowerBound(c), lo = hi - 1, best = -1;
        double bestDistance = double.PositiveInfinity;
        while (lo >= 0 || hi < order.Length)
        {
            double gapLo = lo >= 0 ? c - keys[lo] : double.PositiveInfinity;
            double gapHi = hi < order.Length ? keys[hi] - c : double.PositiveInfinity;
            if (Math.Min(gapLo, gapHi) > Math.Min(bestDistance, limit))
                break;
            int k = gapLo <= gapHi ? order[lo--] : order[hi++];
            double d = Vec3.Distance(p, points[k]);
            if (d > limit || !accept(k))
                continue;
            if (d < bestDistance || (d == bestDistance && k < best))
                (best, bestDistance) = (k, d);
        }
        return (best, bestDistance);
    }

    public Vec3 Point(int index) => points[index];

    /// <summary>Every point within <paramref name="radius"/>, in index order.</summary>
    public List<int> Within(Vec3 p, double radius)
    {
        double c = Coord(p, axis);
        var found = new List<int>();
        for (int i = LowerBound(c - radius); i < order.Length && keys[i] <= c + radius; i++)
            if (Vec3.Distance(p, points[order[i]]) <= radius)
                found.Add(order[i]);
        found.Sort();
        return found;
    }

    private int LowerBound(double c)
    {
        int lo = 0, hi = keys.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid] < c) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private static double Coord(Vec3 p, int axis) => axis switch { 0 => p.X, 1 => p.Y, _ => p.Z };
}
