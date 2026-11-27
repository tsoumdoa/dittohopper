using System.Text.Json.Serialization;

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
    /// <summary>The layer matrix rows: what touched each layer. <see cref="DiffSummary"/> turns them into a status.</summary>
    public IReadOnlyList<LayerRow> Layers { get; init; } = [];

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
    /// <summary>False when the snapshots hashed on different grids: matching pass 3 is skipped and no geometry is equal by hash, so every pair goes down the geometry ladder.</summary>
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
    /// <summary>Detector ids: <see cref="Detectors.OutsideScope"/> for a layer event outside the declared scope.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];
    public ReviewState Review { get; init; }

    /// <summary>Not yet marked intended, and flagged, as for <see cref="ChangeSet.NeedsLook"/>.</summary>
    [JsonIgnore]
    public bool NeedsLook => Review != ReviewState.Intended && Flags.Count > 0;
}

public enum MatchedBy { StableKey, Guid, GeometryHash, ShapeSignature, Fuzzy, Manual }

/// <summary>One A object paired with one B object, with the evidence for it. Fuzzy pairs carry their score as confidence.</summary>
public sealed record Match(Guid A, Guid B, MatchedBy By, double Confidence, int UnchangedProperties)
{
    /// <summary>A fuzzy pair (matching pass 5): plausible, not certain, so it belongs in the review bucket.</summary>
    [JsonIgnore]
    public bool NeedsReview => By == MatchedBy.Fuzzy;
}

/// <summary>What a change is about. Object properties and table properties share one vocabulary.</summary>
public enum PropertyKind
{
    Layer, Name, Color, Material, Linetype, PrintWidth, DisplayOrder, UserText, Groups,
    Block, BlockContent, Transform, Geometry, Visible, Locked, Content,
    /// <summary>A fuzzy pairing with no other change: the set that keeps it in review.</summary>
    Pairing,
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

/// <summary>
/// The geometry ladder's verdict. <see cref="Unclassified"/>: the hash differs but the ladder can't say how, because
/// there is no geometry service or the pair can't be measured (edited text, for example).
/// <see cref="DataOnly"/> pairs are listed but never count as changes. <see cref="Edited"/> with a
/// <see cref="GeometryChange.Rigid"/> is "moved + edited": the transform, then the points it doesn't explain.
/// </summary>
public enum GeometryLevel { Unclassified, Unchanged, DataOnly, Moved, Edited, Reshaped, TopologyChanged }

public sealed record GeometryChange
{
    public required Guid A { get; init; }
    public required Guid B { get; init; }
    public required GeometryLevel Level { get; init; }
    public string? Detail { get; init; }
    /// <summary>Moved: the transform. Edited: the rigid part of a "moved + edited" pair, else null.</summary>
    public Xform? Rigid { get; init; }
    /// <summary>Edited: the farthest moved point. Otherwise the shape deviation.</summary>
    public double? MaxDeviation { get; init; }
    public double? MeanDeviation { get; init; }
    /// <summary>Edited: A's control point or vertex indices that moved (after the rigid part, if any).</summary>
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
    /// <summary>
    /// Detector ids (<see cref="Detectors"/>), sorted. Objects with different per-object flags never share a set;
    /// <see cref="Detectors.LoneDeletion"/> and <see cref="Detectors.MassChange"/> are judged on the whole set.
    /// </summary>
    public IReadOnlyList<string> Flags { get; init; } = [];
    public string? RootCauseEventId { get; init; }
    public ReviewState Review { get; init; }

    /// <summary>Not yet marked intended, and flagged (being outside the declared scope is a flag).</summary>
    [JsonIgnore]
    public bool NeedsLook => Review != ReviewState.Intended && Flags.Count > 0;
}

/// <summary>
/// One row of the layer matrix: an A layer and its B counterpart, or a B layer with no A counterpart (an added layer
/// or a split target). Lists the change sets with an object on it, on either side, and the layer table events about it.
/// </summary>
public sealed record LayerRow
{
    public Guid? A { get; init; }
    public Guid? B { get; init; }
    /// <summary>A's full path, or B's for a B-only row.</summary>
    public required string Path { get; init; }
    /// <summary>Objects on the layer in A (in B for a B-only row).</summary>
    public int Objects { get; init; }
    public IReadOnlyList<string> ChangeSets { get; init; } = [];
    public IReadOnlyList<string> Events { get; init; } = [];
}
