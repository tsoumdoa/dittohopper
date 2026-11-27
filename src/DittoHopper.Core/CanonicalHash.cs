using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace DittoHopper.Core;

/// <summary>
/// Canonical geometry hash: a type tag, then a stream of values, with every coordinate snapped to a grid
/// (usually the model's absolute tolerance) before hashing with xxHash64. Two geometries hash equal when
/// they have the same tag, the same value sequence, and coordinates within one grid cell of each other.
/// A coordinate near a cell boundary can snap either way; that only costs a ladder step, never correctness.
/// </summary>
public sealed class CanonicalHash
{
    private const byte CoordTag = 1, IntTag = 2;
    private readonly XxHash64 hash = new();
    private readonly double grid;
    private readonly byte[] buffer = new byte[9];

    public CanonicalHash(string typeTag, double grid)
    {
        if (!(grid > 0) || double.IsInfinity(grid))
            throw new ArgumentOutOfRangeException(nameof(grid), "The hash grid must be a positive number.");
        this.grid = grid;
        Span<byte> length = stackalloc byte[4];
        byte[] tag = Encoding.UTF8.GetBytes(typeTag);
        BinaryPrimitives.WriteInt32LittleEndian(length, tag.Length);
        hash.Append(length);
        hash.Append(tag);
    }

    /// <summary>Adds a coordinate, snapped to the grid.</summary>
    public CanonicalHash Add(double coordinate)
    {
        if (!double.IsFinite(coordinate))
            throw new ArgumentOutOfRangeException(nameof(coordinate), "Coordinates must be finite.");
        return Write(CoordTag, checked((long)Math.Round(coordinate / grid, MidpointRounding.AwayFromZero)));
    }

    public CanonicalHash Add(Vec3 p) => Add(p.X).Add(p.Y).Add(p.Z);

    /// <summary>Adds an exact integer, such as a degree, a count or an index.</summary>
    public CanonicalHash Add(long value) => Write(IntTag, value);

    public ulong Finish() => hash.GetCurrentHashAsUInt64();

    private CanonicalHash Write(byte tag, long value)
    {
        buffer[0] = tag;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(1), value);
        hash.Append(buffer);
        return this;
    }
}

/// <summary>Short ids derived from content, so the same inputs give the same ids on every run.</summary>
internal static class StableId
{
    public static string From(string prefix, params string?[] parts)
    {
        var hash = new XxHash64();
        Span<byte> length = stackalloc byte[4];
        foreach (string? part in parts)
        {
            // Length-prefixed, with -1 for null, so no two part lists encode the same bytes.
            byte[] bytes = part is null ? [] : Encoding.UTF8.GetBytes(part);
            BinaryPrimitives.WriteInt32LittleEndian(length, part is null ? -1 : bytes.Length);
            hash.Append(length);
            hash.Append(bytes);
        }
        return prefix + "-" + hash.GetCurrentHashAsUInt64().ToString("x16");
    }
}
