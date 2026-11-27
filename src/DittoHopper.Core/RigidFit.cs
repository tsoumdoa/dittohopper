using System.Globalization;

namespace DittoHopper.Core;

/// <summary>A rigid transform fitted to point pairs, and the largest distance a point pair is left apart.</summary>
public sealed record RigidMotion(Xform Transform, double MaxResidual, bool Mirrored);

/// <summary>
/// Fits a rigid motion (rotation or mirror, plus translation) to corresponding points, and describes one in
/// words. The rotation is the least-squares (Kabsch) optimum, computed in Horn's quaternion form, which
/// stays well defined for planar and collinear point sets where an SVD needs special cases.
/// </summary>
public static class RigidFit
{
    private static readonly double[] Identity3 = [1, 0, 0, 0, 1, 0, 0, 0, 1];
    private static readonly double[] MirrorX = [-1, 0, 0, 0, 1, 0, 0, 0, 1];

    /// <summary>
    /// The rigid motion that maps <paramref name="from"/> onto <paramref name="to"/>, index by index, leaving
    /// every pair within <paramref name="tolerance"/>, or null when none does. A pure translation is preferred,
    /// then a rotation, then a mirror (determinant −1), so a planar set that fits either way reports a rotation.
    /// </summary>
    public static RigidMotion? Fit(IReadOnlyList<Vec3> from, IReadOnlyList<Vec3> to, double tolerance)
    {
        if (from.Count != to.Count || from.Count == 0)
            return null;
        Vec3 ca = Centroid(from), cb = Centroid(to);

        var translation = Motion(Identity3, ca, cb, from, to, mirrored: false);
        if (translation.MaxResidual <= tolerance)
            return translation;
        var rotation = Motion(Rotation(from, ca, to, cb, mirror: false), ca, cb, from, to, mirrored: false);
        if (rotation.MaxResidual <= tolerance)
            return rotation;
        var mirror = Motion(Mul(Rotation(from, ca, to, cb, mirror: true), MirrorX), ca, cb, from, to, mirrored: true);
        return mirror.MaxResidual <= tolerance ? mirror : null;
    }

    /// <summary>
    /// A human description of a transform, e.g. "moved 120 mm in X", "rotated 90° about Z through (100, 0, 0)" or
    /// "mirrored across the plane X = 50 mm". Lengths are rounded to the decimals <paramref name="tolerance"/>
    /// resolves, so equal motions describe equally. A non-rigid transform is listed as its rounded matrix.
    /// </summary>
    public static string Describe(Xform t, UnitSystem units, double tolerance)
    {
        int decimals = Math.Clamp((int)Math.Ceiling(-Math.Log10(tolerance)), 0, 6);
        string unit = UnitLabel(units);
        string Len(double v) => Number(v, decimals) + " " + unit;
        string Point(Vec3 p) => $"({Number(p.X, decimals)}, {Number(p.Y, decimals)}, {Number(p.Z, decimals)})";

        double[] l = [t[0, 0], t[0, 1], t[0, 2], t[1, 0], t[1, 1], t[1, 2], t[2, 0], t[2, 1], t[2, 2]];
        var tr = new Vec3(t[0, 3], t[1, 3], t[2, 3]);
        bool affine = t[3, 0] == 0 && t[3, 1] == 0 && t[3, 2] == 0 && t[3, 3] == 1;
        if (!affine || !IsOrthonormal(l))
            return "transformed by " + Matrix(t, decimals);

        string Moved(Vec3 v)
        {
            var parts = new[] { ("X", v.X), ("Y", v.Y), ("Z", v.Z) }.Where(p => Number(p.Item2, decimals) != "0").ToList();
            if (parts.Count == 0)
                return "moved less than " + Len(Math.Pow(10, -decimals));
            if (parts.Count == 1)
                return $"moved {Len(Math.Abs(parts[0].Item2))} in {(parts[0].Item2 < 0 ? "-" : "")}{parts[0].Item1}";
            return $"moved {Len(v.Length)} ({string.Join(", ", parts.Select(p => p.Item1 + " " + Number(p.Item2, decimals)))})";
        }

        if (Det(l) > 0)
        {
            double angle = Math.Acos(Math.Clamp((l[0] + l[4] + l[8] - 1) / 2, -1, 1));
            if (angle < 1e-9)
                return Moved(tr);
            var skew = new Vec3(l[7] - l[5], l[2] - l[6], l[3] - l[1]);
            var axis = skew.Length > 1e-6 ? skew * (1 / skew.Length) : LongestColumn(Add(l, Identity3));
            (axis, angle) = ReadsNegative(axis) ? (axis * -1, -angle) : (axis, angle);

            // A rotation about a line through p, plus a slide along it: t = (I - R)p + (t·u)u, with p ⊥ u.
            double along = Vec3.Dot(tr, axis);
            var perp = tr - axis * along;
            var through = (perp + Vec3.Cross(axis, perp) * (1 / Math.Tan(angle / 2))) * 0.5;
            string text = $"rotated {Number(angle * 180 / Math.PI, 3)}° about {AxisLabel(axis)}";
            if (Point(through) != Point(default))
                text += " through " + Point(through);
            if (Number(along, decimals) != "0")
                text += $", moved {Len(along)} along the axis";
            return text;
        }

        bool pureMirror = IsSymmetric(l); // orthonormal and symmetric: a reflection in one plane
        if (!pureMirror)
            return "mirrored and rotated: " + Matrix(t, decimals);
        var normal = LongestColumn(Sub(Identity3, l));
        if (ReadsNegative(normal))
            normal *= -1;
        double offset = Vec3.Dot(tr, normal);
        var glide = tr - normal * offset;
        string label = AxisLabel(normal);
        string plane = label.Length == 1
            ? $"the plane {label} = {Len(offset / 2)}"
            : $"the plane with normal {label}, {Len(offset / 2)} from the origin";
        string mirrored = "mirrored across " + plane;
        return Number(glide.Length, decimals) == "0" ? mirrored : mirrored + ", then " + Moved(glide);
    }

    private static RigidMotion Motion(double[] l, Vec3 ca, Vec3 cb, IReadOnlyList<Vec3> from, IReadOnlyList<Vec3> to, bool mirrored)
    {
        var t = cb - Apply(l, ca);
        var xform = new Xform([l[0], l[1], l[2], t.X, l[3], l[4], l[5], t.Y, l[6], l[7], l[8], t.Z, 0, 0, 0, 1]);
        double max = 0;
        for (int i = 0; i < from.Count; i++)
            max = Math.Max(max, Vec3.Distance(xform.Apply(from[i]), to[i]));
        return new RigidMotion(xform, max, mirrored);
    }

    /// <summary>Horn (1987): the optimal rotation is the unit quaternion of N's largest eigenvalue.</summary>
    private static double[] Rotation(IReadOnlyList<Vec3> from, Vec3 ca, IReadOnlyList<Vec3> to, Vec3 cb, bool mirror)
    {
        double sxx = 0, sxy = 0, sxz = 0, syx = 0, syy = 0, syz = 0, szx = 0, szy = 0, szz = 0;
        for (int i = 0; i < from.Count; i++)
        {
            var a = from[i] - ca;
            if (mirror)
                a = new Vec3(-a.X, a.Y, a.Z);
            var b = to[i] - cb;
            sxx += a.X * b.X; sxy += a.X * b.Y; sxz += a.X * b.Z;
            syx += a.Y * b.X; syy += a.Y * b.Y; syz += a.Y * b.Z;
            szx += a.Z * b.X; szy += a.Z * b.Y; szz += a.Z * b.Z;
        }
        var n = new double[4, 4]
        {
            { sxx + syy + szz, syz - szy, szx - sxz, sxy - syx },
            { syz - szy, sxx - syy - szz, sxy + syx, szx + sxz },
            { szx - sxz, sxy + syx, -sxx + syy - szz, syz + szy },
            { sxy - syx, szx + sxz, syz + szy, -sxx - syy + szz },
        };
        var q = LargestEigenvector(n);
        double w = q[0], x = q[1], y = q[2], z = q[3];
        double norm = w * w + x * x + y * y + z * z;
        w /= Math.Sqrt(norm); x /= Math.Sqrt(norm); y /= Math.Sqrt(norm); z /= Math.Sqrt(norm);
        return
        [
            1 - 2 * (y * y + z * z), 2 * (x * y - w * z), 2 * (x * z + w * y),
            2 * (x * y + w * z), 1 - 2 * (x * x + z * z), 2 * (y * z - w * x),
            2 * (x * z - w * y), 2 * (y * z + w * x), 1 - 2 * (x * x + y * y),
        ];
    }

    /// <summary>Cyclic Jacobi on a symmetric 4x4 matrix; returns the eigenvector of the largest eigenvalue.</summary>
    private static double[] LargestEigenvector(double[,] a)
    {
        var v = new double[4, 4];
        for (int i = 0; i < 4; i++) v[i, i] = 1;
        for (int sweep = 0; sweep < 64; sweep++)
        {
            double off = 0, all = 0;
            for (int p = 0; p < 4; p++)
                for (int q = 0; q < 4; q++)
                {
                    all += a[p, q] * a[p, q];
                    if (p != q) off += a[p, q] * a[p, q];
                }
            if (off <= 1e-30 * all)
                break;
            for (int p = 0; p < 3; p++)
                for (int q = p + 1; q < 4; q++)
                {
                    if (a[p, q] == 0) continue;
                    double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                    double t = (theta >= 0 ? 1 : -1) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                    double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                    for (int k = 0; k < 4; k++)
                    {
                        double akp = a[k, p], akq = a[k, q];
                        a[k, p] = c * akp - s * akq;
                        a[k, q] = s * akp + c * akq;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double apk = a[p, k], aqk = a[q, k];
                        a[p, k] = c * apk - s * aqk;
                        a[q, k] = s * apk + c * aqk;
                    }
                    for (int k = 0; k < 4; k++)
                    {
                        double vkp = v[k, p], vkq = v[k, q];
                        v[k, p] = c * vkp - s * vkq;
                        v[k, q] = s * vkp + c * vkq;
                    }
                }
        }
        int best = 0;
        for (int i = 1; i < 4; i++)
            if (a[i, i] > a[best, best]) best = i;
        return [v[0, best], v[1, best], v[2, best], v[3, best]];
    }

    private static Vec3 Centroid(IReadOnlyList<Vec3> points)
    {
        var sum = new Vec3(0, 0, 0);
        foreach (var p in points) sum += p;
        return sum * (1.0 / points.Count);
    }

    private static Vec3 Apply(double[] l, Vec3 p) =>
        new(l[0] * p.X + l[1] * p.Y + l[2] * p.Z, l[3] * p.X + l[4] * p.Y + l[5] * p.Z, l[6] * p.X + l[7] * p.Y + l[8] * p.Z);

    private static double[] Mul(double[] a, double[] b)
    {
        var r = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
                for (int k = 0; k < 3; k++)
                    r[i * 3 + j] += a[i * 3 + k] * b[k * 3 + j];
        return r;
    }

    private static double[] Add(double[] a, double[] b) => [.. a.Zip(b, (x, y) => x + y)];
    private static double[] Sub(double[] a, double[] b) => [.. a.Zip(b, (x, y) => x - y)];
    private static double[] Transpose(double[] a) => [a[0], a[3], a[6], a[1], a[4], a[7], a[2], a[5], a[8]];
    private static bool Same(double[] a, double[] b) => a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= 1e-9);
    private static bool IsOrthonormal(double[] l) => Same(Mul(Transpose(l), l), Identity3);
    private static bool IsSymmetric(double[] l) => Same(l, Transpose(l));

    private static double Det(double[] l) =>
        l[0] * (l[4] * l[8] - l[5] * l[7]) - l[1] * (l[3] * l[8] - l[5] * l[6]) + l[2] * (l[3] * l[7] - l[4] * l[6]);

    private static Vec3 LongestColumn(double[] m)
    {
        var best = new Vec3(0, 0, 0);
        for (int c = 0; c < 3; c++)
        {
            var col = new Vec3(m[c], m[3 + c], m[6 + c]);
            if (col.Length > best.Length) best = col;
        }
        return best * (1 / best.Length);
    }

    /// <summary>True when the vector's largest component is negative, so callers can flip it to read positive.</summary>
    private static bool ReadsNegative(Vec3 u)
    {
        double largest = new[] { u.X, u.Y, u.Z }.MaxBy(Math.Abs);
        return largest < 0;
    }

    private static string AxisLabel(Vec3 u)
    {
        if (Math.Abs(u.X - 1) < 1e-9) return "X";
        if (Math.Abs(u.Y - 1) < 1e-9) return "Y";
        if (Math.Abs(u.Z - 1) < 1e-9) return "Z";
        return $"({Number(u.X, 3)}, {Number(u.Y, 3)}, {Number(u.Z, 3)})";
    }

    private static string Matrix(Xform t, int decimals) =>
        "[" + string.Join(", ", Enumerable.Range(0, 16).Select(i => Number(t[i / 4, i % 4], i is 3 or 7 or 11 ? decimals : 9))) + "]";

    private static string Number(double v, int decimals)
    {
        string s = Math.Round(v, decimals).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    private static string UnitLabel(UnitSystem u) => u switch
    {
        UnitSystem.Microns => "µm",
        UnitSystem.Millimeters => "mm",
        UnitSystem.Centimeters => "cm",
        UnitSystem.Meters => "m",
        UnitSystem.Kilometers => "km",
        UnitSystem.Inches => "in",
        UnitSystem.Feet => "ft",
        UnitSystem.Yards => "yd",
        UnitSystem.Miles => "mi",
        _ => "units",
    };
}
