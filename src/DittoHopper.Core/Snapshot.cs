namespace DittoHopper.Core;

/// <summary>
/// Everything the diff needs from one model, captured once. References between records are ids,
/// never names. Record order doesn't matter: the diff sorts everything it reads.
/// </summary>
public sealed record ModelSnapshot
{
    public required SnapshotInfo Info { get; init; }
    public IReadOnlyList<LayerRecord> Layers { get; init; } = [];
    public IReadOnlyList<BlockRecord> Blocks { get; init; } = [];
    public IReadOnlyList<TableRecord> Materials { get; init; } = [];
    public IReadOnlyList<TableRecord> Linetypes { get; init; } = [];
    public IReadOnlyList<GroupRecord> Groups { get; init; } = [];
    public IReadOnlyList<ObjectRecord> Objects { get; init; } = [];
}

/// <summary>Where a snapshot came from and how its coordinates were hashed.</summary>
public sealed record SnapshotInfo
{
    /// <summary>A display label such as a file name. Not used by the diff.</summary>
    public string Source { get; init; } = "";

    public DateTimeOffset? CapturedAt { get; init; }
    public required UnitSystem Units { get; init; }
    public required double AbsoluteTolerance { get; init; }

    /// <summary>Grid spacing the geometry hashes were snapped to, in <see cref="Units"/>. Hashes compare only on equal grids.</summary>
    public required double HashGrid { get; init; }

    /// <summary>What the snapshot left out, e.g. worksession references or linked blocks. Copied into the result header.</summary>
    public IReadOnlyList<string> Skipped { get; init; } = [];
}

/// <summary>Where an appearance value comes from. ByLayer first, so it is the default.</summary>
public enum ValueSource { ByLayer, ByObject, ByParent, ByMaterial }

/// <summary>An appearance value with its source. The value only counts when the source is ByObject.</summary>
public readonly record struct Sourced<T>(T Value, ValueSource Source);

/// <summary>A layer. Its full path is derived from <see cref="Name"/> and the parent chain.</summary>
public sealed record LayerRecord
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    /// <summary>Null for a top-level layer.</summary>
    public Guid? ParentId { get; init; }
    /// <summary>ARGB.</summary>
    public int Color { get; init; } = unchecked((int)0xFF000000);
    /// <summary><see cref="Guid.Empty"/> = the default (continuous) linetype.</summary>
    public Guid LinetypeId { get; init; }
    /// <summary><see cref="Guid.Empty"/> = the default material.</summary>
    public Guid MaterialId { get; init; }
    public double PrintWidth { get; init; }
    public bool Visible { get; init; } = true;
    public bool Locked { get; init; }
}

/// <summary>A material or linetype. <see cref="ContentHash"/> covers its settings, not its name.</summary>
public sealed record TableRecord
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public ulong ContentHash { get; init; }
}

/// <summary>A block definition.</summary>
public sealed record BlockRecord
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// Hash of the definition's own objects. A nested instance contributes its transform, attributes and the
    /// nested definition's name, not that definition's content, so an inner edit changes only the inner hash.
    /// </summary>
    public ulong ContentHash { get; init; }

    /// <summary>Definitions referenced directly by instances inside this one.</summary>
    public IReadOnlyList<Guid> NestedBlockIds { get; init; } = [];
}

/// <summary>A group. Membership lives on the objects (<see cref="ObjectRecord.GroupIds"/>).</summary>
public sealed record GroupRecord
{
    public required Guid Id { get; init; }
    public string? Name { get; init; }
}

public enum ObjectKind
{
    Point, PointCloud, Curve, Surface, Brep, Extrusion, Mesh, SubD,
    InstanceReference, Annotation, Hatch, TextDot, Light, Other,
}

/// <summary>One model-space object.</summary>
public sealed record ObjectRecord
{
    public required Guid Id { get; init; }
    public required ObjectKind Kind { get; init; }
    public required Guid LayerId { get; init; }
    public string? Name { get; init; }
    /// <summary>ARGB.</summary>
    public Sourced<int> Color { get; init; }
    public Sourced<Guid> Material { get; init; }
    public Sourced<Guid> Linetype { get; init; }
    public Sourced<double> PrintWidth { get; init; }
    public int DisplayOrder { get; init; }
    public IReadOnlyList<Guid> GroupIds { get; init; } = [];
    public IReadOnlyDictionary<string, string> UserText { get; init; } = new Dictionary<string, string>();
    /// <summary>Set for <see cref="ObjectKind.InstanceReference"/>.</summary>
    public Guid? BlockDefinitionId { get; init; }
    /// <summary>Set for <see cref="ObjectKind.InstanceReference"/>.</summary>
    public Xform? InstanceXform { get; init; }
    public GeometrySignature Geometry { get; init; } = new();
}

/// <summary>
/// The geometry fingerprint: a canonical hash (see <see cref="CanonicalHash"/>) plus position-free and
/// placement measures. Structure, topology and CV arrays join this in M2, with the geometry ladder.
/// </summary>
public sealed record GeometrySignature
{
    public ulong Hash { get; init; }
    public double Length { get; init; }
    public double Area { get; init; }
    public double Volume { get; init; }
    public Vec3 Centroid { get; init; }
    public BBox BBox { get; init; }
}
