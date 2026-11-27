using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DittoHopper.Core;

/// <summary>A point or vector, in the snapshot's units.</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator *(Vec3 v, double s) => new(v.X * s, v.Y * s, v.Z * s);

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

    /// <summary>Equal within <paramref name="translationTol"/> on the translation column and 1e-9 elsewhere.</summary>
    public bool AlmostEquals(Xform other, double translationTol)
    {
        for (int i = 0; i < 16; i++)
        {
            double tol = i is 3 or 7 or 11 ? translationTol : 1e-9;
            if (Math.Abs(m[i] - other.m[i]) > tol)
                return false;
        }
        return true;
    }

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
