namespace DittoHopper.Core;

/// <summary>
/// Geometry ladder steps 4 and 5, for a pair that is neither the same shape nor rigidly moved. Step 4, Edited:
/// the same structure, so control points or vertices compare index by index (after undoing a seam move or a
/// flip), and the ones that moved are listed. When most of them moved, a rigid fit of the majority comes first,
/// so a moved and edited object reads "moved …, then 1 of 16 control points moved" rather than all 16 moving.
/// Polylines whose vertex counts differ get a sequence diff instead. Step 5: the same topology means Reshaped,
/// with the deviation; otherwise Topology changed, with what broke.
/// </summary>
internal static class GeometryEdits
{
    /// <summary>Points listed by index in a detail; the rest are counted.</summary>
    private const int Listed = 5;

    /// <summary>Above this many points, a closed curve is compared as stored, without trying every seam position.</summary>
    private const int MaxCycle = 2048;

    /// <summary>Above this many vertex pairs (n × m), polylines aren't sequence-diffed.</summary>
    private const long MaxSequenceCells = 4_000_000;

    public static GeometryChange? Edited(DiffContext ctx, ObjectRecord a, StructureArrays s, GeometryChange change)
    {
        double tol = ctx.ShapeTolerance;
        var units = ctx.Header.Units;
        if (s.SameStructure && s.A.Count == s.B.Count && s.A.Count > 0)
        {
            int n = s.A.Count;
            var (b, note) = Align(s.A, s.B, s.Order, tol);
            var moved = MovedPoints(s.A, b, tol, null);
            if (moved.Count == 0)
                return null;
            string what = s.Polylines ? "vertices" : PointWord(a.Kind);

            if (2 * moved.Count > n && RigidFit.FitMajority(s.A, b, tol) is { } fit
                && !fit.Motion.Transform.AlmostEquals(Xform.Identity, tol) && fit.Inliers > n - moved.Count
                && MovedPoints(s.A, b, tol, fit.Motion.Transform) is { Count: > 0 } residual)
            {
                return change with
                {
                    Level = GeometryLevel.Edited,
                    Detail = Note(note, RigidFit.Describe(fit.Motion.Transform, units, tol) + ", then " + Listing(residual, n, what, units, tol)),
                    Rigid = GeometryLadder.Round(fit.Motion.Transform, tol),
                    MaxDeviation = GeometryLadder.Round(residual.Max(m => m.Distance), tol),
                    MovedIndices = [.. residual.Select(m => m.Index)],
                };
            }
            return change with
            {
                Level = GeometryLevel.Edited,
                Detail = Note(note, Listing(moved, n, what, units, tol)),
                MaxDeviation = GeometryLadder.Round(moved.Max(m => m.Distance), tol),
                MovedIndices = [.. moved.Select(m => m.Index)],
            };
        }

        if (s.Polylines && Sequence(s.A, s.B, s.Order, tol) is { Untouched: > 0 } diff
            && diff.Inserted + diff.Removed + diff.Moved.Count > 0)
        {
            var parts = new List<string>();
            if (diff.Inserted > 0) parts.Add($"{diff.Inserted} inserted");
            if (diff.Removed > 0) parts.Add($"{diff.Removed} removed");
            if (diff.Moved.Count > 0) parts.Add($"{diff.Moved.Count} moved ({Indexed(diff.Moved, units, tol)})");
            parts.Add($"{diff.Untouched} untouched");
            return change with
            {
                Level = GeometryLevel.Edited,
                Detail = Note(diff.Note, "vertices: " + string.Join(", ", parts)),
                MaxDeviation = diff.Moved.Count > 0 ? GeometryLadder.Round(diff.Moved.Max(m => m.Distance), tol) : null,
                MovedIndices = [.. diff.Moved.Select(m => m.Index)],
            };
        }
        return null;
    }

    /// <summary>Step 5. Null deviation (the pair can't be measured) leaves the change as it was: Unclassified.</summary>
    public static GeometryChange Reshaped(DiffContext ctx, IGeometryService service, ObjectRecord a, ObjectRecord b, GeometryChange change)
    {
        var broke = TopologyChanges(a, b);
        if (broke.Count > 0)
            return change with { Level = GeometryLevel.TopologyChanged, Detail = string.Join(", ", broke) };

        double tol = ctx.ShapeTolerance;
        var (ba, bb) = (a.Geometry.BBox, b.Geometry.BBox);
        var lo = new Vec3(Math.Min(ba.Min.X, bb.Min.X * ctx.Scale), Math.Min(ba.Min.Y, bb.Min.Y * ctx.Scale), Math.Min(ba.Min.Z, bb.Min.Z * ctx.Scale));
        var hi = new Vec3(Math.Max(ba.Max.X, bb.Max.X * ctx.Scale), Math.Max(ba.Max.Y, bb.Max.Y * ctx.Scale), Math.Max(ba.Max.Z, bb.Max.Z * ctx.Scale));
        // Ask for the whole deviation: the service may stop early only once it passes the reach given.
        double reach = Math.Max(Vec3.Distance(lo, hi), tol);
        if (service.Deviation(a.Id, b.Id, null, reach) is not { } d)
            return change;
        string detail = "deviation max " + RigidFit.Length(d.Max, ctx.Header.Units, tol)
            + (d.Mean is double mean ? ", mean " + RigidFit.Length(mean, ctx.Header.Units, tol) : "");
        return change with
        {
            Level = GeometryLevel.Reshaped,
            Detail = detail,
            MaxDeviation = GeometryLadder.Round(d.Max, tol),
            MeanDeviation = d.Mean is double m ? GeometryLadder.Round(m, tol) : null,
        };
    }

    /// <summary>What broke, from the topology summaries, worst first: closed or solid lost or gained, faces split or removed, boundary loops.</summary>
    private static List<string> TopologyChanges(ObjectRecord a, ObjectRecord b)
    {
        var (x, y) = (a.Geometry, b.Geometry);
        var broke = new List<string>();
        if (x.Closed != y.Closed)
        {
            switch (ObjectKinds.Family(a.Kind))
            {
                case ObjectKind.Brep or ObjectKind.SubD:
                    broke.Add(x.Closed ? "solid became open" : "open became solid");
                    break;
                case ObjectKind.Curve:
                    broke.Add(x.Closed ? "closed curve became open" : "open curve became closed");
                    break;
                case ObjectKind.Mesh:
                    broke.Add(x.Closed ? "closed mesh became open" : "open mesh became closed");
                    break;
            }
        }
        if (y.Faces > x.Faces) broke.Add("face split or added");
        else if (y.Faces < x.Faces) broke.Add("face removed or merged");
        else if (x.Closed == y.Closed && y.BoundaryLoops > x.BoundaryLoops) broke.Add("boundary loop added");
        else if (x.Closed == y.Closed && y.BoundaryLoops < x.BoundaryLoops) broke.Add("boundary loop removed");
        return broke;
    }

    private readonly record struct MovedPoint(int Index, double Distance);

    /// <summary>Indices whose points are more than <paramref name="tol"/> apart, after moving A's by <paramref name="move"/>.</summary>
    private static List<MovedPoint> MovedPoints(IReadOnlyList<Vec3> a, IReadOnlyList<Vec3> b, double tol, Xform? move)
    {
        var moved = new List<MovedPoint>();
        for (int i = 0; i < a.Count; i++)
        {
            double d = Vec3.Distance(move is null ? a[i] : move.Apply(a[i]), b[i]);
            if (d > tol) moved.Add(new MovedPoint(i, d));
        }
        return moved;
    }

    /// <summary>
    /// B's points in A's order: reversed when that leaves more points in place (a flipped curve), and for a closed
    /// curve also rotated to its best seam. A change of order is noted in the detail. Ties keep B as stored.
    /// </summary>
    private static (IReadOnlyList<Vec3> B, string? Note) Align(IReadOnlyList<Vec3> a, IReadOnlyList<Vec3> b, PointOrder order, double tol)
    {
        if (order == PointOrder.Fixed || (order == PointOrder.Closed && a.Count > MaxCycle))
            return (b, null);
        int n = a.Count, best = Untouched(a, b, tol);
        var result = (B: b, Note: (string?)null);
        foreach (bool flip in new[] { false, true })
        {
            var source = flip ? [.. b.Reverse()] : b;
            for (int k = 0; k < (order == PointOrder.Closed ? n : 1); k++)
            {
                if (!flip && k == 0) continue;
                var candidate = k == 0 ? source : [.. Enumerable.Range(0, n).Select(i => source[(i + k) % n])];
                int untouched = Untouched(a, candidate, tol);
                if (untouched > best)
                    (best, result) = (untouched, (candidate, string.Join(", ", new[] { k != 0 ? "seam moved" : null, flip ? "direction flipped" : null }.OfType<string>())));
            }
        }
        return result;
    }

    private static int Untouched(IReadOnlyList<Vec3> a, IReadOnlyList<Vec3> b, double tol)
    {
        int count = 0;
        for (int i = 0; i < a.Count; i++)
            if (Vec3.Distance(a[i], b[i]) <= tol) count++;
        return count;
    }

    private sealed record SequenceDiff(int Inserted, int Removed, IReadOnlyList<MovedPoint> Moved, int Untouched, string? Note);

    /// <summary>
    /// A text-diff of two polylines' vertices: the longest common subsequence under tolerance equality is
    /// untouched; between two untouched runs, A and B vertices pair up as moved, and the extras are removed or
    /// inserted. B may run either way, and a closed B starts at its vertex nearest A's first; the larger common
    /// part wins, forward on a tie. Moved indices are A's.
    /// </summary>
    private static SequenceDiff? Sequence(IReadOnlyList<Vec3> a, IReadOnlyList<Vec3> b, PointOrder order, double tol)
    {
        if (a.Count == 0 || b.Count == 0 || (long)a.Count * b.Count > MaxSequenceCells)
            return null;
        SequenceDiff? best = null;
        foreach (bool flip in new[] { false, true })
        {
            IReadOnlyList<Vec3> source = flip ? [.. b.Reverse()] : b;
            string? note = flip ? "direction flipped" : null;
            if (order == PointOrder.Closed)
            {
                int start = Enumerable.Range(0, source.Count).MinBy(j => Vec3.Distance(source[j], a[0]));
                if (start != 0 && Vec3.Distance(source[start], a[0]) <= tol)
                {
                    source = [.. Enumerable.Range(0, source.Count).Select(j => source[(j + start) % source.Count])];
                    note = note is null ? "seam moved" : "seam moved, " + note;
                }
            }
            else if (order == PointOrder.Fixed && flip)
            {
                break;
            }
            var diff = Lcs(a, source, tol) with { Note = note };
            if (best is null || diff.Untouched > best.Untouched)
                best = diff;
        }
        return best;
    }

    private static SequenceDiff Lcs(IReadOnlyList<Vec3> a, IReadOnlyList<Vec3> b, double tol)
    {
        int n = a.Count, m = b.Count;
        bool Same(int i, int j) => Vec3.Distance(a[i], b[j]) <= tol;
        var length = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                length[i, j] = Same(i, j) ? length[i + 1, j + 1] + 1 : Math.Max(length[i + 1, j], length[i, j + 1]);

        int inserted = 0, removed = 0, untouched = 0;
        var moved = new List<MovedPoint>();
        var gapA = new List<int>();
        var gapB = new List<int>();
        void Flush()
        {
            int paired = Math.Min(gapA.Count, gapB.Count);
            for (int k = 0; k < paired; k++)
                moved.Add(new MovedPoint(gapA[k], Vec3.Distance(a[gapA[k]], b[gapB[k]])));
            removed += gapA.Count - paired;
            inserted += gapB.Count - paired;
            gapA.Clear();
            gapB.Clear();
        }
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (Same(x, y) && length[x, y] == length[x + 1, y + 1] + 1)
            {
                Flush();
                untouched++;
                x++;
                y++;
            }
            else if (length[x + 1, y] >= length[x, y + 1]) gapA.Add(x++);
            else gapB.Add(y++);
        }
        while (x < n) gapA.Add(x++);
        while (y < m) gapB.Add(y++);
        Flush();
        return new SequenceDiff(inserted, removed, moved, untouched, null);
    }

    private static string PointWord(ObjectKind kind) => kind switch
    {
        ObjectKind.Mesh or ObjectKind.SubD => "vertices",
        ObjectKind.PointCloud => "points",
        _ => "control points",
    };

    private static string Listing(List<MovedPoint> moved, int total, string what, UnitSystem units, double tol) =>
        $"{moved.Count} of {total} {what} moved: {Indexed(moved, units, tol)}";

    /// <summary>"#2 by 50 mm, #5 by 3 mm", the first few by index, then how many more.</summary>
    private static string Indexed(IReadOnlyList<MovedPoint> moved, UnitSystem units, double tol)
    {
        string text = string.Join(", ", moved.Take(Listed).Select(m => $"#{m.Index} by {RigidFit.Length(m.Distance, units, tol)}"));
        return moved.Count > Listed ? text + $", and {moved.Count - Listed} more" : text;
    }

    private static string Note(string? note, string detail) => string.IsNullOrEmpty(note) ? detail : note + "; " + detail;
}
