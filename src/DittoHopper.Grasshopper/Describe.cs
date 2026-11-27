using System.Globalization;
using DittoHopper.Core;

namespace DittoHopper.Grasshopper;

/// <summary>Plain-English labels for results, change sets, table events and match evidence.</summary>
public static class Describe
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A Pairing set is review of a fuzzy pairing, not a change.</summary>
    public static bool IsPairing(ChangeSet s) => s.Change?.Property == PropertyKind.Pairing;

    /// <summary>
    /// Core's Level 1 sentence (<see cref="DiffSummary.Sentence"/>), then the counts: change sets (pairings to review
    /// are not changes) and table events.
    /// </summary>
    public static string Summary(DiffResult r) =>
        DiffSummary.Sentence(r) + Environment.NewLine +
        $"{Plural(r.ChangeSets.Count(s => !IsPairing(s)), "change set")} · {Plural(r.TableEvents.Count, "table event")}";

    /// <summary>Objects in A: matched or removed.</summary>
    public static int ObjectsInA(DiffResult r) =>
        r.Matches.Count + r.ChangeSets.Where(s => s.Kind == ChangeKind.Removed).Sum(s => s.Objects.Count);

    /// <summary>Most change sets listed in Report; the rest are counted.</summary>
    public const int ReportedSets = 20;

    /// <summary>
    /// The summary; where A and B came from and the effective shape tolerance; how objects were matched; the header
    /// notes; then one line per change set, flagged ones first. <paramref name="layersOf"/> gives a set's object
    /// layers, when known.
    /// </summary>
    public static string Report(DiffResult r, Func<ChangeSet, IEnumerable<string>>? layersOf = null)
    {
        double shapeTolerance = r.Settings.ShapeTolerance ?? r.Header.Tolerance;
        var lines = new List<string>
        {
            Summary(r),
            $"A {Label(r.A.Source)} → B {Label(r.B.Source)} · {Unit(r.Header.Units)}, shape tolerance {Number(shapeTolerance)}" +
                (r.Settings.ShapeTolerance is null ? " (auto)" : ""),
        };
        if (r.Matches.Count > 0)
        {
            var by = r.Matches.GroupBy(m => m.By).OrderBy(g => g.Key).Select(g => $"{Count(g.Count())} by {Badge(g.Key)}");
            lines.Add("Matched " + string.Join(", ", by));
        }
        int dataOnly = r.Geometry.Count(g => g.Level == GeometryLevel.DataOnly);
        if (dataOnly > 0)
            lines.Add($"{Plural(dataOnly, "object")} with the same shape but different data (rebuilt, flipped…): not counted as changes");
        lines.AddRange(r.Header.Notes);

        var layers = DiffSummary.Layers(r);
        if (layers.Count > 0)
            lines.Add($"Layers: {Count(layers.Count(l => l.State == LayerState.Untouched))} untouched, " +
                $"{Count(layers.Count(l => l.State == LayerState.Expected))} changed as expected, " +
                $"{Count(layers.Count(l => l.State == LayerState.ToCheck))} to check");

        var events = r.TableEvents.ToDictionary(e => e.Id);
        // Changes that need a look first; the sort is stable, so the result's order holds within each group.
        var sets = r.ChangeSets.Where(c => !IsPairing(c)).OrderByDescending(c => c.NeedsLook).ToList();
        int pairings = r.Matches.Count(m => m.NeedsReview);
        if (pairings > 0)
            lines.Add($"• Pairings to review: {Count(pairings)} fuzzy {(pairings == 1 ? "match" : "matches")}, not counted as changes (Diff Filter, Kind review)");
        foreach (var set in sets.Take(ReportedSets))
        {
            IEnumerable<string> on = set.Layer is { } l ? [l] : layersOf?.Invoke(set) ?? [];
            string? cause = set.RootCauseEventId is { } id && events.TryGetValue(id, out var e) ? Cause(e) : null;
            lines.Add("• " + Set(set, set.Objects.Count, on) + (cause is null ? "" : $" · cause: {cause}"));
        }
        if (sets.Count > ReportedSets)
            lines.Add($"• …and {Plural(sets.Count - ReportedSets, "more change set")}: use Diff Filter to list them.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The evidence badge for a match: GUID, key, hash, shape 0.93, "fuzzy 0.81 · needs review".</summary>
    public static string Evidence(Match m) => m.By switch
    {
        MatchedBy.ShapeSignature or MatchedBy.Fuzzy => Badge(m.By) + " " + m.Confidence.ToString("0.00", Inv),
        _ => Badge(m.By),
    } + (m.NeedsReview ? " · needs review" : "");

    /// <summary>Detector flags in words, from Core.</summary>
    public static string Flags(IEnumerable<string> flags) => string.Join("; ", flags.Select(Detectors.Describe));

    private static string Badge(MatchedBy by) => by switch
    {
        MatchedBy.StableKey => "key",
        MatchedBy.Guid => "GUID",
        MatchedBy.GeometryHash => "hash",
        MatchedBy.ShapeSignature => "shape",
        MatchedBy.Fuzzy => "fuzzy",
        _ => "manual",
    };

    /// <summary>One table event: "Layer renamed: Walls → A-WALL · 212 objects".</summary>
    public static string Event(TableEvent e)
    {
        string table = e.Table.ToString();
        string text = e.Kind switch
        {
            TableEventKind.Renamed => $"{table} renamed: {e.From} → {e.To}",
            TableEventKind.Reparented => $"{table} {e.Label} moved from {e.From ?? "top level"} to {e.To ?? "top level"}",
            TableEventKind.Added => $"{table} added: {e.Label}",
            TableEventKind.Removed => $"{table} deleted: {e.Label}",
            TableEventKind.Split => $"{table} split: {e.Label} → " +
                string.Join(", ", e.Shares.Select(s => $"{s.Label} {Count(s.Objects)} ({s.Share.ToString("P0", Inv)})")),
            TableEventKind.PropertyChanged when e.Property == PropertyKind.Content =>
                $"{table} {e.Label} {(e.Table == TableKind.Block ? "definition" : "settings")} changed",
            TableEventKind.PropertyChanged => $"{table} {e.Label} {Name(e.Property)}: {e.From} → {e.To}",
            _ => $"{table} {e.Label} {e.Kind}",
        };
        if (e.AffectedObjects > 0) text += " · " + Plural(e.AffectedObjects, Affected(e));
        if (e.Inferred) text += " · inferred " + e.Confidence.ToString("0.00", Inv);
        return text;
    }

    /// <summary>A table event as the cause of an object change: "layer Temp deleted".</summary>
    public static string Cause(TableEvent e) => e.Kind switch
    {
        TableEventKind.Removed => $"{e.Table.ToString().ToLowerInvariant()} {e.Label} deleted",
        TableEventKind.Added => $"{e.Table.ToString().ToLowerInvariant()} {e.Label} added",
        TableEventKind.PropertyChanged when e.Property == PropertyKind.Content => $"{e.Table.ToString().ToLowerInvariant()} {e.Label} edited",
        _ => Event(e),
    };

    private static string Affected(TableEvent e) => e.Table switch
    {
        TableKind.Block => "instance",
        TableKind.Group => "member",
        TableKind.Layer when e.Kind == TableEventKind.PropertyChanged
            && e.Property is PropertyKind.Color or PropertyKind.Material or PropertyKind.Linetype or PropertyKind.PrintWidth => "ByLayer object",
        _ => "object",
    };

    /// <summary>What one property change did: "layer Walls → Default", "moved 120 mm in X", "user text + supplier = FX-02".</summary>
    public static string Change(PropertyChange c, GeometryChange? geometry = null) => c.Property switch
    {
        PropertyKind.Transform => c.From is null && c.To is not null ? c.To : "transform changed",
        PropertyKind.Geometry => geometry is { Level: not (GeometryLevel.Unclassified or GeometryLevel.Unchanged) } g
            ? Level(g.Level) + (string.IsNullOrEmpty(g.Detail) ? "" : ": " + g.Detail)
            : "shape changed",
        PropertyKind.UserText when c.From is null => $"user text + {c.Key} = {c.To}",
        PropertyKind.UserText when c.To is null => $"user text − {c.Key}",
        PropertyKind.UserText => $"user text {c.Key}: {c.From} → {c.To}",
        PropertyKind.BlockContent => $"nested block {c.Key} changed",
        PropertyKind.Pairing => "pairing needs review",
        _ => $"{Name(c.Property)} {Value(c.From, c.FromSource)} → {Value(c.To, c.ToSource)}",
    };

    /// <summary>A change set in one line: count, change, layers and flags. <paramref name="count"/> may be the in-scope part.</summary>
    public static string Set(ChangeSet s, int count, IEnumerable<string> layers)
    {
        var distinct = layers.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        string where = distinct.Count == 0 ? "" : distinct.Count <= 3
            ? " on " + string.Join(", ", distinct)
            : $" on {string.Join(", ", distinct.Take(3))} and {Plural(distinct.Count - 3, "more layer")}";
        string text = s.Kind switch
        {
            ChangeKind.Added => $"{Plural(count, "object")} added on {s.Layer}",
            ChangeKind.Removed => $"{Plural(count, "object")} removed from {s.Layer}",
            _ => $"{Plural(count, "object")}{where}: {(s.Change is { } c ? Change(c) : "changed")}",
        };
        // A pairing's only flag is fuzzy-match, which its text already says.
        if (s.Flags.Count > 0 && !IsPairing(s)) text += " · flagged: " + Flags(s.Flags);
        return text;
    }

    /// <summary>An object's name, else its kind and short id.</summary>
    public static string Object(ObjectRecord? o, Guid id) =>
        o?.Name is { Length: > 0 } name ? name : $"{o?.Kind.ToString() ?? "Object"} {id.ToString("N")[..8]}";

    public static string Name(PropertyKind? p) => p switch
    {
        PropertyKind.PrintWidth => "print width",
        PropertyKind.DisplayOrder => "display order",
        PropertyKind.UserText => "user text",
        PropertyKind.BlockContent => "nested block",
        PropertyKind.Transform => "placement",
        PropertyKind.Geometry => "shape",
        null => "property",
        _ => p.Value.ToString().ToLowerInvariant(),
    };

    private static string Level(GeometryLevel level) => level switch
    {
        GeometryLevel.TopologyChanged => "topology changed",
        GeometryLevel.DataOnly => "same shape, different data",
        _ => level.ToString().ToLowerInvariant(),
    };

    private static string Value(string? value, ValueSource? source) =>
        value ?? (source is ValueSource s && s != ValueSource.ByObject ? s.ToString() : "none");

    private static string Label(string source) => string.IsNullOrEmpty(source) ? "(unnamed)" : source;

    private static string Unit(UnitSystem u) => u switch
    {
        UnitSystem.Millimeters => "mm",
        UnitSystem.Centimeters => "cm",
        UnitSystem.Meters => "m",
        UnitSystem.Kilometers => "km",
        UnitSystem.Inches => "in",
        UnitSystem.Feet => "ft",
        _ => u.ToString().ToLowerInvariant(),
    };

    public static string Count(int n) => n.ToString("N0", Inv);

    public static string Plural(int n, string noun, string? plural = null) => $"{Count(n)} {(n == 1 ? noun : plural ?? noun + "s")}";

    private static string Number(double v) => v.ToString("0.######", Inv);
}
