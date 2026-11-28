using System.Globalization;

namespace DittoHopper.Core;

/// <summary>
/// The one property vocabulary for Diff Settings' Ignore and Diff Filter's Kind: each name, its aliases (moved is
/// placement), the toggle it switches and the change kinds it covers.
/// </summary>
public static class Vocabulary
{
    private sealed record Property(string Name, string[] Aliases, PropertyKind[] Kinds,
        Func<PropertyToggles, bool> Get, Func<PropertyToggles, bool, PropertyToggles> Set);

    private static readonly Property[] Properties =
    [
        new("placement", ["moved", "move", "transform"], [PropertyKind.Transform], t => t.Placement, (t, v) => t with { Placement = v }),
        new("shape", ["geometry"], [PropertyKind.Geometry], t => t.Shape, (t, v) => t with { Shape = v }),
        new("layer", [], [PropertyKind.Layer], t => t.Layer, (t, v) => t with { Layer = v }),
        new("block", [], [PropertyKind.Block, PropertyKind.BlockContent], t => t.BlockDefinition, (t, v) => t with { BlockDefinition = v }),
        new("groups", ["group"], [PropertyKind.Groups], t => t.GroupMembership, (t, v) => t with { GroupMembership = v }),
        new("display order", [], [PropertyKind.DisplayOrder], t => t.DisplayOrder, (t, v) => t with { DisplayOrder = v }),
        new("color", ["colour"], [PropertyKind.Color], t => t.Color, (t, v) => t with { Color = v }),
        new("material", [], [PropertyKind.Material], t => t.Material, (t, v) => t with { Material = v }),
        new("linetype", [], [PropertyKind.Linetype], t => t.Linetype, (t, v) => t with { Linetype = v }),
        new("print width", [], [PropertyKind.PrintWidth], t => t.PrintWidth, (t, v) => t with { PrintWidth = v }),
        new("name", [], [PropertyKind.Name], t => t.Name, (t, v) => t with { Name = v }),
        new("user text", [], [PropertyKind.UserText], t => t.UserText, (t, v) => t with { UserText = v }),
    ];

    public static IEnumerable<string> Names => Properties.Select(p => p.Name);

    /// <summary>
    /// The compared properties none of <paramref name="changed"/> touches, in vocabulary order: those switched on, less
    /// those that don't apply (shape for a block instance, which has a placement and a block instead; block for
    /// anything else). What an object card lists as unchanged, and what Diff Filter counts.
    /// </summary>
    public static IReadOnlyList<string> Unchanged(PropertyToggles toggles, bool instance, IEnumerable<PropertyKind> changed)
    {
        var hit = changed.ToHashSet();
        return [.. Properties
            .Where(p => p.Get(toggles) && (p.Name == "block" ? instance : !(instance && p.Name == "shape")) && !p.Kinds.Any(hit.Contains))
            .Select(p => p.Name)];
    }

    /// <summary>Lower case, without spaces, hyphens or underscores, so "User Text", "user-text" and "usertext" agree.</summary>
    public static string Normalize(string s) =>
        new([.. s.Trim().ToLower(CultureInfo.InvariantCulture).Where(c => c is not (' ' or '-' or '_'))]);

    private static Property? Find(string raw)
    {
        string n = Normalize(raw);
        return Properties.FirstOrDefault(p => Normalize(p.Name) == n || p.Aliases.Any(a => Normalize(a) == n));
    }

    /// <summary>The canonical property name for a name or alias, or null.</summary>
    public static string? Canonical(string raw) => Find(raw)?.Name;

    /// <summary>The change kinds a canonical property name covers.</summary>
    public static IReadOnlyList<PropertyKind> KindsOf(string name) => Find(name)?.Kinds ?? [];

    public static PropertyToggles Ignore(PropertyToggles toggles, IEnumerable<string> names, out List<string> unknown)
    {
        unknown = [];
        foreach (string raw in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            if (Find(raw) is { } property) toggles = property.Set(toggles, false);
            else unknown.Add(raw.Trim());
        }
        return toggles;
    }

    public static string Describe(DiffSettings s)
    {
        var ignored = Properties.Where(p => !p.Get(s.Toggles)).Select(p => p.Name).ToList();
        string tol = s.ShapeTolerance is double t ? t.ToString("G4", CultureInfo.InvariantCulture) : "auto";
        return $"Key {s.StableKey} · Tolerance {tol} · Ignoring {(ignored.Count == 0 ? "nothing" : string.Join(", ", ignored))}" +
            (s.ScopeGlob is { } scope ? $" · Scope {scope}" : "");
    }
}
