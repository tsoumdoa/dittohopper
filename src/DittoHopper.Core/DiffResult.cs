namespace DittoHopper.Core;

/// <summary>
/// The whole answer for one A → B comparison. Plain data, every list in a stable order, so the same inputs
/// serialize to the same bytes (<see cref="DiffJson"/>) and a saved result can be signed off and re-checked.
/// </summary>
public sealed record DiffResult
{
    public const int CurrentFormat = 1;

    public int Format { get; init; } = CurrentFormat;
    public required SnapshotInfo A { get; init; }
    public required SnapshotInfo B { get; init; }
    public required DiffHeader Header { get; init; }
    public required DiffSettings Settings { get; init; }
    public IReadOnlyList<TableMatch> TableMatches { get; init; } = [];
    public IReadOnlyList<TableEvent> TableEvents { get; init; } = [];
    public IReadOnlyList<Match> Matches { get; init; } = [];
    public IReadOnlyList<GeometryChange> Geometry { get; init; } = [];
    public IReadOnlyList<ChangeSet> ChangeSets { get; init; } = [];
    /// <summary>Matched pairs with no change at all.</summary>
    public int UnchangedObjects { get; init; }

    /// <summary>A copy with the review state of one change set or table event set. Unknown ids throw.</summary>
    public DiffResult WithReview(string id, ReviewState state)
    {
        bool found = false;
        var sets = ChangeSets.Select(c => c.Id == id ? Found(c with { Review = state }) : c).ToList();
        var events = TableEvents.Select(e => e.Id == id ? Found(e with { Review = state }) : e).ToList();
        if (!found)
            throw new KeyNotFoundException($"No change set or table event with id '{id}'.");
        return this with { ChangeSets = sets, TableEvents = events };

        T Found<T>(T item) { found = true; return item; }
    }
}

/// <summary>How A and B were reconciled before comparing, and what was left out.</summary>
public sealed record DiffHeader
{
    /// <summary>Units of the comparison: always A's. B's coordinates are converted when its units differ.</summary>
    public required UnitSystem Units { get; init; }
    /// <summary>The larger of the two absolute tolerances, in <see cref="Units"/>.</summary>
    public required double Tolerance { get; init; }
    /// <summary>False when the snapshots hashed on different grids: matching pass 3 is skipped and every pair's geometry is unclassified.</summary>
    public required bool HashGridsMatch { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public enum TableKind { Layer, Block, Material, Linetype, Group }

public enum TableMatchedBy { Id, Path, Name, Members, Inferred }

/// <summary>One entry of a table's A → B map.</summary>
public sealed record TableMatch(TableKind Table, Guid A, Guid B, TableMatchedBy By, double Confidence);

public enum TableEventKind { Added, Removed, Renamed, Reparented, Split, PropertyChanged }

/// <summary>One target of a layer split: how many of the A layer's matched objects went there.</summary>
public sealed record SplitShare(Guid B, string Label, int Objects, double Share);

/// <summary>A change to a table entry: a root cause that object change sets can point to.</summary>
public sealed record TableEvent
{
    public required string Id { get; init; }
    public required TableKind Table { get; init; }
    public required TableEventKind Kind { get; init; }
    public Guid? A { get; init; }
    public Guid? B { get; init; }
    /// <summary>A's full path or name when the entry exists in A, else B's.</summary>
    public required string Label { get; init; }
    /// <summary>Set for <see cref="TableEventKind.PropertyChanged"/>.</summary>
    public PropertyKind? Property { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    /// <summary>Set for <see cref="TableEventKind.Split"/>.</summary>
    public IReadOnlyList<SplitShare> Shares { get; init; } = [];
    /// <summary>True when the entry was matched from object membership rather than id, path or name.</summary>
    public bool Inferred { get; init; }
    public double Confidence { get; init; } = 1;
    /// <summary>
    /// Objects on the layer, top-level instances using the block (directly or nested), objects using the
    /// material or linetype, or group members. Counted in B, or in A when the entry is gone from B.
    /// </summary>
    public int AffectedObjects { get; init; }
    public ReviewState Review { get; init; }
}

public enum MatchedBy { StableKey, Guid, GeometryHash, ShapeSignature, Fuzzy, Manual }

/// <summary>One A object paired with one B object, with the evidence for it.</summary>
public sealed record Match(Guid A, Guid B, MatchedBy By, double Confidence, int UnchangedProperties);

/// <summary>What a change is about. Object properties and table properties share one vocabulary.</summary>
public enum PropertyKind
{
    Layer, Name, Color, Material, Linetype, PrintWidth, DisplayOrder, UserText, Groups,
    Block, BlockContent, Transform, Geometry, Visible, Locked, Content,
}

/// <summary>
/// One property difference. Records compare by value, so identical changes on different objects are the
/// same change; that equality is the change-set signature. References show A's label in From and B's in To.
/// </summary>
public sealed record PropertyChange(
    PropertyKind Property,
    string? Key,
    string? From,
    string? To,
    ValueSource? FromSource = null,
    ValueSource? ToSource = null);

/// <summary>The geometry ladder's verdict. <see cref="Unclassified"/>: the hash differs and the ladder (M2) hasn't run.</summary>
public enum GeometryLevel { Unclassified, Unchanged, DataOnly, Moved, Edited, Reshaped, TopologyChanged }

public sealed record GeometryChange
{
    public required Guid A { get; init; }
    public required Guid B { get; init; }
    public required GeometryLevel Level { get; init; }
    public string? Detail { get; init; }
    public Xform? Rigid { get; init; }
    public double? MaxDeviation { get; init; }
    public double? MeanDeviation { get; init; }
    public IReadOnlyList<int>? MovedIndices { get; init; }
}

public enum ChangeKind { Modified, Removed, Added }

/// <summary>An object in a change set. Modified has both ids; Removed only A; Added only B.</summary>
public readonly record struct ObjectPair(Guid? A, Guid? B);

public enum ReviewState { Unreviewed, Intended, Restored }

/// <summary>
/// Objects that share one identical change. Modified sets carry the <see cref="Change"/>; Added and Removed
/// sets are grouped by <see cref="Layer"/>. <see cref="Id"/> is a hash of that signature, stable across runs.
/// </summary>
public sealed record ChangeSet
{
    public required string Id { get; init; }
    public required ChangeKind Kind { get; init; }
    public PropertyChange? Change { get; init; }
    /// <summary>For Added (B's layer) and Removed (A's layer) sets.</summary>
    public string? Layer { get; init; }
    public required IReadOnlyList<ObjectPair> Objects { get; init; }
    /// <summary>Detector ids. Detectors arrive after M1, so this is empty for now.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];
    public string? RootCauseEventId { get; init; }
    public ReviewState Review { get; init; }
}
