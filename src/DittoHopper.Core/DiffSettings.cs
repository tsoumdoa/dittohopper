namespace DittoHopper.Core;

public sealed record DiffSettings
{
    /// <summary>The key Stamp IDs writes. Always identity, never data, even when another <see cref="StableKey"/> is configured.</summary>
    public const string DefaultStableKey = "diff:uid";

    /// <summary>User-text key whose value is an object's own stable id (matching pass 1).</summary>
    public string StableKey { get; init; } = DefaultStableKey;

    /// <summary>True for user-text keys that are identity rather than data, so never reported as changes.</summary>
    public bool IsStableKey(string key) => key == StableKey || key == DefaultStableKey;

    /// <summary>Share of an A layer's matched objects that must sit on one new B layer to infer it is that layer.</summary>
    public double LayerInferShare { get; init; } = 0.60;

    /// <summary>
    /// The deviation, in A's units, below which a geometry difference is not a real change (Data-only, or a
    /// rigid fit's residual). Null = the comparison's absolute tolerance.
    /// </summary>
    public double? ShapeTolerance { get; init; }

    public PropertyToggles Toggles { get; init; } = new();
}

/// <summary>What counts as a change. Defaults follow the design doc.</summary>
public sealed record PropertyToggles
{
    /// <summary>Rigid moves of geometry, and instance transforms.</summary>
    public bool Placement { get; init; } = true;
    /// <summary>Any other geometry change.</summary>
    public bool Shape { get; init; } = true;
    public bool Layer { get; init; } = true;
    /// <summary>Which definition an instance uses, and definition content edits.</summary>
    public bool BlockDefinition { get; init; } = true;
    public bool GroupMembership { get; init; } = true;
    public bool DisplayOrder { get; init; }
    public bool Color { get; init; } = true;
    public bool Material { get; init; } = true;
    public bool Linetype { get; init; } = true;
    public bool PrintWidth { get; init; }
    public bool Name { get; init; } = true;
    public bool UserText { get; init; } = true;
}
