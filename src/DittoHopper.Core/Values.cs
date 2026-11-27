using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DittoHopper.Core;

/// <summary>A point or vector, in the snapshot's units.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator *(Vec3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public double Length => Math.Sqrt(Dot(this, this));

    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    public static double Distance(Vec3 a, Vec3 b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>An axis-aligned bounding box.</summary>
public readonly record struct BBox(Vec3 Min, Vec3 Max);

/// <summary>A 4x4 transform, stored row-major (translation in column 3). Serializes as 16 numbers.</summary>
[JsonConverter(typeof(XformJsonConverter))]
public sealed class Xform : IEquatable<Xform>
{
    private readonly double[] m;

    public Xform(IReadOnlyList<double> rowMajor)
    {
        if (rowMajor.Count != 16)
            throw new ArgumentException("A transform needs 16 values.", nameof(rowMajor));
        m = [.. rowMajor];
    }

    public static Xform Identity => Translation(0, 0, 0);

    public static Xform Translation(double x, double y, double z) =>
        new([1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1]);

    public double this[int row, int col] => m[row * 4 + col];

    public IReadOnlyList<double> Values => m;

    /// <summary>
    /// Equal when the translations are at most <paramref name="translationTol"/> apart (as a vector, so a
    /// diagonal offset counts its full length) and every other entry is within 1e-9.
    /// </summary>
    public bool AlmostEquals(Xform other, double translationTol)
    {
        for (int i = 0; i < 16; i++)
            if (i is not (3 or 7 or 11) && Math.Abs(m[i] - other.m[i]) > 1e-9)
                return false;
        double dx = m[3] - other.m[3], dy = m[7] - other.m[7], dz = m[11] - other.m[11];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz) <= translationTol;
    }

    /// <summary>This transform applied after <paramref name="first"/>: the matrix product this × first.</summary>
    public Xform After(Xform first)
    {
        var v = new double[16];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                for (int k = 0; k < 4; k++)
                    v[r * 4 + c] += m[r * 4 + k] * first.m[k * 4 + c];
        return new Xform(v);
    }

    /// <summary>The inverse of an affine transform, or null when it is singular or projective.</summary>
    public Xform? Inverse()
    {
        if (m[12] != 0 || m[13] != 0 || m[14] != 0 || m[15] != 1)
            return null;
        double a = m[0], b = m[1], c = m[2], d = m[4], e = m[5], f = m[6], g = m[8], h = m[9], i = m[10];
        double det = a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);
        if (!(Math.Abs(det) > 1e-12))
            return null;
        double[] l =
        [
            (e * i - f * h) / det, (c * h - b * i) / det, (b * f - c * e) / det,
            (f * g - d * i) / det, (a * i - c * g) / det, (c * d - a * f) / det,
            (d * h - e * g) / det, (b * g - a * h) / det, (a * e - b * d) / det,
        ];
        double tx = m[3], ty = m[7], tz = m[11];
        return new Xform(
        [
            l[0], l[1], l[2], -(l[0] * tx + l[1] * ty + l[2] * tz),
            l[3], l[4], l[5], -(l[3] * tx + l[4] * ty + l[5] * tz),
            l[6], l[7], l[8], -(l[6] * tx + l[7] * ty + l[8] * tz),
            0, 0, 0, 1,
        ]);
    }

    /// <summary>Applies the transform to a point.</summary>
    public Vec3 Apply(Vec3 p) => new(
        m[0] * p.X + m[1] * p.Y + m[2] * p.Z + m[3],
        m[4] * p.X + m[5] * p.Y + m[6] * p.Z + m[7],
        m[8] * p.X + m[9] * p.Y + m[10] * p.Z + m[11]);

    /// <summary>The same transform with its translation multiplied by <paramref name="s"/> (a unit change).</summary>
    public Xform ScaleTranslation(double s)
    {
        var v = (double[])m.Clone();
        v[3] *= s; v[7] *= s; v[11] *= s;
        return new Xform(v);
    }

    public bool Equals(Xform? other) => other is not null && m.AsSpan().SequenceEqual(other.m);
    public override bool Equals(object? obj) => Equals(obj as Xform);

    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (double v in m) h.Add(v);
        return h.ToHashCode();
    }

    public override string ToString() =>
        "[" + string.Join(", ", m.Select(v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
}

internal sealed class XformJsonConverter : JsonConverter<Xform>
{
    public override Xform Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        new(JsonSerializer.Deserialize<double[]>(ref reader, options) ?? throw new JsonException("Expected 16 numbers."));

    public override void Write(Utf8JsonWriter writer, Xform value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (double v in value.Values) writer.WriteNumberValue(v);
        writer.WriteEndArray();
    }
}

/// <summary>Model length units. <see cref="Other"/> and <see cref="None"/> can't be converted.</summary>
public enum UnitSystem { None, Other, Microns, Millimeters, Centimeters, Meters, Kilometers, Inches, Feet, Yards, Miles }

public static class Units
{
    /// <summary>Meters per unit, or null when the unit can't be converted.</summary>
    public static double? MetersPer(UnitSystem u) => u switch
    {
        UnitSystem.Microns => 1e-6,
        UnitSystem.Millimeters => 1e-3,
        UnitSystem.Centimeters => 1e-2,
        UnitSystem.Meters => 1,
        UnitSystem.Kilometers => 1e3,
        UnitSystem.Inches => 0.0254,
        UnitSystem.Feet => 0.3048,
        UnitSystem.Yards => 0.9144,
        UnitSystem.Miles => 1609.344,
        _ => null,
    };
}
