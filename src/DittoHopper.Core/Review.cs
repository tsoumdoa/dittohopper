namespace DittoHopper.Core;

/// <summary>What a review row is about, and so the colour it is drawn in.</summary>
public enum ReviewStatus { Modified, Added, Removed, Event, Pairing }

/// <summary>The Changes tab's groups, in display order.</summary>
public enum ReviewGroupKind { NeedsLook, TableEvents, InScope, Changes }

public enum ChipKind { NeedsLook, Modified, Added, Removed, Events, Unchanged }

/// <summary>One count on the HUD card and the strip: "2 need a look", "4 modified".</summary>
public sealed record ReviewChip(ChipKind Kind, int Count, string Text);

/// <summary>
/// One row of the Changes tab: a change set or a table event. Objects are the pairs to focus, zoom to and select: a
/// set's own objects; for a table event, the objects it changed (through the layer, block, material or linetype they
/// use, or as the root cause of their own change).
/// </summary>
public sealed record ReviewRow
{
    public required string Id { get; init; }
    public required ReviewGroupKind Group { get; init; }
    public required ReviewStatus Status { get; init; }
    /// <summary>The object's name, a few names, "212 objects", or the table entry's label.</summary>
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>Detector flags as short chip labels.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];
    /// <summary>How the objects were matched: "key", "fuzzy 0.81 · needs review", "id"; "—" when only one side has them.</summary>
    public required string Badge { get; init; }
    public IReadOnlyList<ObjectPair> Objects { get; init; } = [];
    /// <summary>The viewport tag: "C-08 · moved 0.04 mm in X · Tiny nudge".</summary>
    public required string Tag { get; init; }
    /// <summary>A tiny nudge: too small to see, so the viewport rings it.</summary>
    public bool Ring { get; init; }
    public ChangeSet? Set { get; init; }
    public TableEvent? Event { get; init; }
}

public sealed record ReviewGroup(ReviewGroupKind Kind, string Label, IReadOnlyList<ReviewRow> Rows);

/// <summary>
/// One line of an object card: a property, from → to (null is "none" or "not there"), or, when the change has no two
/// values to set side by side (a move, a shape edit, an added object), <see cref="Text"/>, which says what happened.
/// </summary>
public sealed record CardChange(string Property, string? From, string? To)
{
    public string? Text { get; init; }

    /// <summary>A change described in words, with no from → to.</summary>
    public static CardChange Described(string property, string text) => new(property, null, null) { Text = text };
}

/// <summary>
/// The object card for a row: status, title, badge and where it sits; <c>Changed · N</c> with from → to; why it is
/// flagged and any side effect; <c>Unchanged · M</c>, the compared properties that stayed the same.
/// </summary>
public sealed record ReviewCard(
    string Status, string Title, string Badge, string Meta,
    IReadOnlyList<CardChange> Changed, IReadOnlyList<string> Why, IReadOnlyList<string> Unchanged);

/// <summary>
/// One A layer on the Unchanged tab: of its <see cref="Objects"/>, how many came through identical, how many of those
/// look different only through a table entry they use, and the first few names.
/// </summary>
public sealed record UnchangedLayer(string Path, int Identical, int Objects, int ThroughTables, string Names);

/// <summary>
/// The one presentation of a diff that the review panel, the viewport HUD and the Grasshopper text share: the summary,
/// the count chips, the Changes tab's groups and rows, each row's object card, the Unchanged tab and an optional gate.
/// Plain data, built once per result; every text comes from <see cref="Describe"/>, <see cref="DiffSummary"/> and
/// <see cref="Detectors"/>.
/// </summary>
public sealed class Review
{
    /// <summary>Most names listed for one row or one layer; the rest are counted.</summary>
    public const int Names = 3;
    /// <summary>Most lines in a card's <c>Changed</c> list.</summary>
    public const int CardChanges = 12;

    private Review(DiffIndex run) => Run = run;

    public DiffIndex Run { get; }
    /// <summary>Where A and B came from, the units and the tolerance.</summary>
    public string Header { get; private init; } = "";
    public string Sentence { get; private init; } = "";
    public IReadOnlyList<ReviewChip> Chips { get; private init; } = [];
    public IReadOnlyList<ReviewGroup> Groups { get; private init; } = [];
    /// <summary>Every row in group order: the order J and K walk.</summary>
    public IReadOnlyList<ReviewRow> Rows { get; private init; } = [];
    /// <summary>The Unchanged tab's opening line: what was checked and found identical.</summary>
    public string UnchangedSummary { get; private init; } = "";
    public IReadOnlyList<UnchangedLayer> Unchanged { get; private init; } = [];
    /// <summary>The gate on the layers given to <see cref="Build"/>, or null without one.</summary>
    public GateResult? Gate { get; private init; }

    /// <summary>The HUD's one-line strip: the chips, then the gate.</summary>
    public string Strip =>
        string.Join(" · ", Chips.Select(c => c.Text)) + (Gate is { } g ? $" · Gate {g.Layers} {(g.Pass ? "passes" : "fails")}" : "");

    /// <summary>
    /// Builds the review of one diff. <paramref name="gate"/> is a QA gate as in Assert Unchanged: layer patterns, comma
    /// or semicolon separated; empty means no gate (not the whole model, which every change would fail).
    /// </summary>
    public static Review Build(DiffIndex run, string? gate = null)
    {
        var r = run.Result;
        var rows = new List<ReviewRow>();
        foreach (var e in r.TableEvents) rows.Add(EventRow(run, e));
        foreach (var s in r.ChangeSets) rows.Add(SetRow(run, s));
        // Causes first, objects second: within a group, table events come before change sets (the sort is stable).
        var groups = rows.GroupBy(x => x.Group).OrderBy(g => g.Key)
            .Select(g => new ReviewGroup(g.Key, GroupLabel(r, g.Key), [.. g])).ToList();
        int needs = groups.FirstOrDefault(g => g.Kind == ReviewGroupKind.NeedsLook)?.Rows.Count ?? 0;
        var patterns = gate?.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        return new Review(run)
        {
            Header = Describe.Sides(r),
            Sentence = DiffSummary.Sentence(r),
            Chips =
            [
                new(ChipKind.NeedsLook, needs, needs == 1 ? "1 needs a look" : $"{Describe.Count(needs)} need a look"),
                new(ChipKind.Modified, run.Modified.Count, $"{Describe.Count(run.Modified.Count)} modified"),
                new(ChipKind.Added, run.Added.Count, $"{Describe.Count(run.Added.Count)} added"),
                new(ChipKind.Removed, run.Removed.Count, $"{Describe.Count(run.Removed.Count)} removed"),
                new(ChipKind.Events, r.TableEvents.Count, Describe.Plural(r.TableEvents.Count, "table event")),
                new(ChipKind.Unchanged, r.UnchangedObjects, $"{Describe.Count(r.UnchangedObjects)} unchanged"),
            ],
            Groups = groups,
            Rows = [.. groups.SelectMany(g => g.Rows)],
            UnchangedSummary = UnchangedLine(run),
            Unchanged = UnchangedLayers(run),
            Gate = patterns.Length == 0 ? null : Core.Gate.Check(run, new LayerPatterns(patterns)),
        };
    }

    public static string GroupLabel(DiffResult r, ReviewGroupKind kind) => kind switch
    {
        ReviewGroupKind.NeedsLook => "Needs a look",
        ReviewGroupKind.TableEvents => "Table events",
        ReviewGroupKind.InScope => $"In scope · {r.Settings.ScopeGlob!.Trim()}",
        _ => "Changes",
    };

    private static bool HasScope(DiffResult r) => !string.IsNullOrWhiteSpace(r.Settings.ScopeGlob);

    private static ReviewRow EventRow(DiffIndex run, TableEvent e)
    {
        var objects = run.ObjectsOf(e);
        string description = Describe.Event(e);
        return new ReviewRow
        {
            Id = e.Id,
            Group = e.NeedsLook ? ReviewGroupKind.NeedsLook : ReviewGroupKind.TableEvents,
            Status = ReviewStatus.Event,
            Name = e.Label,
            Description = description,
            Flags = [.. e.Flags.Select(Detectors.Label)],
            Badge = EventBadge(run.Result, e),
            Objects = objects,
            Tag = $"{e.Label} · {Lower(description)}" + (e.Flags.Count > 0 ? " · " + Detectors.Label(e.Flags[0]) : ""),
            Event = e,
        };
    }

    private static ReviewRow SetRow(DiffIndex run, ChangeSet s)
    {
        var r = run.Result;
        var group = s.NeedsLook ? ReviewGroupKind.NeedsLook : HasScope(r) ? ReviewGroupKind.InScope : ReviewGroupKind.Changes;
        var status = s.Kind switch
        {
            ChangeKind.Added => ReviewStatus.Added,
            ChangeKind.Removed => ReviewStatus.Removed,
            _ => Describe.IsPairing(s) ? ReviewStatus.Pairing : ReviewStatus.Modified,
        };
        var single = s.Objects.Count == 1 ? s.Objects[0] : (ObjectPair?)null;
        string change = s.Kind switch
        {
            ChangeKind.Modified => s.Change is { } c ? Describe.Change(c, single is { } p ? run.GeometryOf(p) : null) : "changed",
            _ => Describe.Set(s, s.Objects.Count, run.LayersOf(s), flags: false),
        };
        string description = Capital(change);
        if (s.Kind == ChangeKind.Modified && s.Change?.Property is not (PropertyKind.Layer or PropertyKind.Pairing)
            && Describe.Layers(run.LayersOf(s)) is { Length: > 0 } layers)
            description += " · " + layers;
        if (s.RootCauseEventId is { } id && r.TableEvents.FirstOrDefault(e => e.Id == id) is { } cause)
            description += " · cause: " + Describe.Cause(cause);
        // A pairing's only flag is fuzzy-match, which its description already says.
        var flags = Describe.IsPairing(s) ? [] : s.Flags.Select(Detectors.Label).ToList();
        string name = RowName(run, s);
        return new ReviewRow
        {
            Id = s.Id,
            Group = group,
            Status = status,
            Name = name,
            Description = description,
            Flags = flags,
            Badge = SetBadge(run, s),
            Objects = s.Objects,
            Tag = $"{name} · {Lower(change)}" + (flags.Count > 0 ? " · " + flags[0] : ""),
            Ring = s.Flags.Contains(Detectors.TinyNudge),
            Set = s,
        };
    }

    private static string RowName(DiffIndex run, ChangeSet s)
    {
        if (s.Objects.Count > Names) return Describe.Plural(s.Objects.Count, "object");
        return string.Join(", ", s.Objects
            .Select(p => p.B is Guid b ? Describe.Object(run.ObjectB(b), b) : Describe.Object(run.ObjectA(p.A!.Value), p.A.Value))
            .Order(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The weakest evidence among a set's pairs, so a fuzzy pairing never hides behind a certain one.</summary>
    private static string SetBadge(DiffIndex run, ChangeSet s)
    {
        var weakest = s.Objects.Select(p => p.A is Guid a ? run.MatchOfA(a) : null).OfType<Match>()
            .OrderByDescending(m => m.By).ThenBy(m => m.Confidence).FirstOrDefault();
        return weakest is null ? "—" : Describe.Evidence(weakest);
    }

    private static string EventBadge(DiffResult r, TableEvent e)
    {
        var m = e.A is Guid a ? r.TableMatches.FirstOrDefault(t => t.Table == e.Table && t.A == a) : null;
        return m?.By switch
        {
            null => "—",
            TableMatchedBy.Id => "id",
            TableMatchedBy.Path => "path",
            TableMatchedBy.Name => "name",
            var by => $"{by.ToString()!.ToLowerInvariant()} {m.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}",
        };
    }

    /// <summary>The object card for a row of this review.</summary>
    public ReviewCard Card(ReviewRow row)
    {
        var r = Run.Result;
        string status = row.Status switch
        {
            ReviewStatus.Event => "Table event",
            var s => s.ToString(),
        };
        int count = row.Event is { } e && e.AffectedObjects > 0 ? e.AffectedObjects : row.Objects.Count;
        string noun = row.Event is { } ev ? Describe.Affected(ev) : "object";
        string meta = $"{GroupLabel(r, row.Group)} · {Describe.Plural(count, noun)}";

        var why = new List<string>();
        var flags = row.Set?.Flags ?? row.Event?.Flags ?? [];
        why.AddRange(flags.Where(f => !(row.Status == ReviewStatus.Pairing && f == Detectors.FuzzyMatch)).Select(Detectors.Describe));
        if (row.Set?.RootCauseEventId is { } id && r.TableEvents.FirstOrDefault(x => x.Id == id) is { } cause)
            why.Add("Cause: " + Describe.Cause(cause) + ".");
        if (row.Event is { } t)
        {
            if (t is { Table: TableKind.Layer, Kind: TableEventKind.PropertyChanged, AffectedObjects: > 0 }
                && t.Property is PropertyKind.Color or PropertyKind.Material or PropertyKind.Linetype or PropertyKind.PrintWidth)
                why.Add($"{Describe.Plural(t.AffectedObjects, "object")} take their {Describe.Name(t.Property)} from the layer, so they look " +
                    "different though their own settings did not change.");
            if (t.Inferred)
                why.Add($"Matched by the objects on it, not by id, path or name: confidence {t.Confidence.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}.");
        }

        List<CardChange> changed;
        if (row.Event is { } te) changed = EventChanges(te);
        else
        {
            // The row's own change first, then anything else that changed on the same objects.
            var sets = new List<ChangeSet> { row.Set! };
            sets.AddRange(row.Objects.SelectMany(Run.SetsOf).Where(s => s.Id != row.Set!.Id).DistinctBy(s => s.Id));
            var single = row.Objects.Count == 1 ? row.Objects[0] : (ObjectPair?)null;
            changed = [.. sets.Select(s => SetChange(s, single)).Distinct().Take(CardChanges)];
        }

        IReadOnlyList<string> unchanged = [];
        var pairs = row.Objects.Where(p => p.A is not null && p.B is not null).ToList();
        if (row.Event is null && pairs.Count > 0)
            unchanged = pairs.Select(UnchangedOf).Aggregate((x, y) => [.. x.Intersect(y)]);
        return new ReviewCard(status, row.Name, row.Badge, meta, changed, why, unchanged);
    }

    private CardChange SetChange(ChangeSet s, ObjectPair? single)
    {
        string layer = Describe.Layers(Run.LayersOf(s));
        return s.Kind switch
        {
            ChangeKind.Added => CardChange.Described("object", $"added on {layer}"),
            ChangeKind.Removed => CardChange.Described("object", $"removed from {layer}"),
            _ => s.Change is not { } c ? CardChange.Described("object", "changed") : c.Property switch
            {
                PropertyKind.Transform or PropertyKind.Geometry =>
                    CardChange.Described(Describe.Name(c.Property), Describe.Change(c, single is { } p ? Run.GeometryOf(p) : null)),
                PropertyKind.UserText => new($"user text {c.Key}", c.From, c.To),
                PropertyKind.BlockContent => CardChange.Described($"nested block {c.Key}", "edited"),
                PropertyKind.Pairing => CardChange.Described("pairing", "needs review"),
                _ => new(Describe.Name(c.Property), Describe.Value(c.From, c.FromSource), Describe.Value(c.To, c.ToSource)),
            },
        };
    }

    private static List<CardChange> EventChanges(TableEvent e) => e.Kind switch
    {
        TableEventKind.Renamed => [new("name", e.From, e.To)],
        TableEventKind.Reparented => [new("parent", e.From ?? "top level", e.To ?? "top level")],
        TableEventKind.Added => [CardChange.Described(e.Table.ToString().ToLowerInvariant(), "added")],
        TableEventKind.Removed => [CardChange.Described(e.Table.ToString().ToLowerInvariant(), "deleted")],
        TableEventKind.Split => [.. e.Shares.Select(s => CardChange.Described("→ " + s.Label,
            $"{Describe.Plural(s.Objects, "object")} ({s.Share.ToString("P0", System.Globalization.CultureInfo.InvariantCulture)})"))],
        TableEventKind.PropertyChanged when e.Property == PropertyKind.Content => [CardChange.Described(e.Table == TableKind.Block ? "definition" : "settings", "edited")],
        _ => [new($"{e.Table.ToString().ToLowerInvariant()} {Describe.Name(e.Property)}", e.From, e.To)],
    };

    /// <summary>
    /// The compared properties that came through unchanged on a matched pair: every property switched on that applies
    /// to it, less those any of its change sets changed. (Grasshopper's Diff Filter counts per user-text key instead:
    /// <see cref="Match.UnchangedProperties"/>.)
    /// </summary>
    public IReadOnlyList<string> UnchangedOf(ObjectPair pair)
    {
        var run = Run;
        bool instance = pair.B is Guid b && run.ObjectB(b)?.Kind == ObjectKind.InstanceReference;
        var changed = run.SetsOf(pair).Where(s => s.Kind == ChangeKind.Modified && s.Change is not null).Select(s => s.Change!.Property);
        return Vocabulary.Unchanged(run.Result.Settings.Toggles, instance, changed);
    }

    private static string UnchangedLine(DiffIndex run)
    {
        var r = run.Result;
        // Everything switched on: what an ordinary object is compared on, then block, for block instances.
        var compared = Vocabulary.Unchanged(r.Settings.Toggles, instance: false, []).Union(Vocabulary.Unchanged(r.Settings.Toggles, instance: true, []));
        int through = Identical(run).Count(m => run.ChangedThroughTables.Contains(m.B));
        string text = $"{Describe.Count(r.UnchangedObjects)} of {Describe.Plural(run.ObjectsInA, "object")} " +
            $"{(r.UnchangedObjects == 1 ? "is" : "are")} identical in every property compared: {string.Join(", ", compared)}.";
        if (through > 0)
            text += $" {Describe.Count(through)} of them look different only because a layer, block, material or linetype they use changed.";
        return text;
    }

    /// <summary>Matched pairs in no change set: the identical objects.</summary>
    private static IEnumerable<Match> Identical(DiffIndex run) =>
        run.Result.Matches.Where(m => run.SetsOf(new ObjectPair(m.A, m.B)).Count == 0);

    private static List<UnchangedLayer> UnchangedLayers(DiffIndex run)
    {
        var onLayer = run.ModelA.Objects.GroupBy(o => o.LayerId).ToDictionary(g => g.Key, g => g.Count());
        return [.. Identical(run)
            .Select(m => (Match: m, Record: run.ObjectA(m.A)))
            .Where(x => x.Record is not null)
            .GroupBy(x => x.Record!.LayerId)
            .Select(g =>
            {
                var items = g.ToList();
                var names = items.Select(x => Describe.Object(x.Record, x.Match.A)).Order(StringComparer.OrdinalIgnoreCase).Take(Names).ToList();
                if (items.Count > Names) names.Add($"and {Describe.Count(items.Count - Names)} more");
                return new UnchangedLayer(
                    run.LayerPathsA.GetValueOrDefault(g.Key) ?? "?",
                    items.Count,
                    onLayer.GetValueOrDefault(g.Key),
                    items.Count(x => run.ChangedThroughTables.Contains(x.Match.B)),
                    string.Join(", ", names));
            })
            .OrderBy(l => l.Path, StringComparer.OrdinalIgnoreCase)];
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static string Lower(string s) => s.Length > 1 && char.IsUpper(s[0]) && !char.IsUpper(s[1]) ? char.ToLowerInvariant(s[0]) + s[1..] : s;
}
