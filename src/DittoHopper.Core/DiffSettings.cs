namespace DittoHopper.Core;

public sealed record DiffSettings
{
    /// <summary>User-text key whose value is an object's own stable id (matching pass 1).</summary>
    public string StableKey { get; init; } = "diff:uid";

    /// <summary>Share of an A layer's matched objects that must sit on one new B layer to infer it is that layer.</summary>
    public double LayerInferShare { get; init; } = 0.60;

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
