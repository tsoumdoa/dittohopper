using System.Globalization;
using DittoHopper.Core;

namespace DittoHopper.Grasshopper;

/// <summary>Plain-English labels for results, change sets, table events and match evidence.</summary>
public static class Describe
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// The one-line summary: "2,104 of 2,416 objects identical · 6 change sets · 3 table events".
    /// Stub until M3's summary helper lands in Core; then this forwards to it.
    /// </summary>
    public static string Summary(DiffResult r)
    {
        var parts = new List<string>
        {
            $"{Count(r.UnchangedObjects)} of {Count(ObjectsInA(r))} objects identical",
            Plural(r.ChangeSets.Count, "change set"),
            Plural(r.TableEvents.Count, "table event"),
        };
        int flagged = r.ChangeSets.Count(s => s.Flags.Count > 0);
        if (flagged > 0) parts.Add($"{Count(flagged)} flagged");
        return string.Join(" · ", parts);
    }

    /// <summary>Objects in A: matched or removed.</summary>
    public static int ObjectsInA(DiffResult r) =>
        r.Matches.Count + r.ChangeSets.Where(s => s.Kind == ChangeKind.Removed).Sum(s => s.Objects.Count);

    /// <summary>The summary, then where A and B came from, how objects were matched, and the header notes.</summary>
    public static string Report(DiffResult r)
    {
        var lines = new List<string>
        {
            Summary(r),
            $"A {Label(r.A.Source)} → B {Label(r.B.Source)} · {Unit(r.Header.Units)}, tolerance {Number(r.Header.Tolerance)}",
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
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The evidence badge for a match: GUID, key, hash, shape 0.93, fuzzy 0.81.</summary>
    public static string Evidence(Match m) => m.By switch
    {
        MatchedBy.ShapeSignature or MatchedBy.Fuzzy => Badge(m.By) + " " + m.Confidence.ToString("0.00", Inv),
        _ => Badge(m.By),
    };

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
        _ => $"{Name(c.Property)} {Value(c.From, c.FromSource)} → {Value(c.To, c.ToSource)}",
    };

    /// <summary>A change set in one line, for Assert Unchanged's Why.</summary>
    public static string Set(ChangeSet s, IEnumerable<string> layers)
    {
        string where = string.Join(", ", layers.Distinct().Order(StringComparer.OrdinalIgnoreCase));
        int n = s.Objects.Count;
        string text = s.Kind switch
        {
            ChangeKind.Added => $"{Plural(n, "object")} added on {s.Layer}",
            ChangeKind.Removed => $"{Plural(n, "object")} removed from {s.Layer}",
            _ => $"{Plural(n, "object")} on {where}: {(s.Change is { } c ? Change(c) : "changed")}",
        };
        if (s.Flags.Count > 0) text += " · flagged: " + string.Join(", ", s.Flags);
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

    public static string Plural(int n, string noun) => $"{Count(n)} {noun}{(n == 1 ? "" : "s")}";

    private static string Number(double v) => v.ToString("0.######", Inv);
}
