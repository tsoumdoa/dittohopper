using System.Globalization;
using DittoHopper.Core;
using Grasshopper.Kernel;

namespace DittoHopper.Grasshopper;

/// <summary>Builds the settings for Model Diff: the stable key, the shape tolerance and which properties to ignore.</summary>
public sealed class DiffSettingsComponent : DittoComponent
{
    public DiffSettingsComponent()
        : base("Diff Settings", "Settings",
            "What Model Diff compares and how, and what you meant to change (Scope). Optional: Model Diff uses these defaults without it.",
            DiffPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a02");
    public override GH_Exposure Exposure => GH_Exposure.secondary;
    protected override ComponentIcon IconKind => ComponentIcon.DiffSettings;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddTextParameter("Key", "Key",
            "The user-text key that identifies an object across versions. An object in A and an object in B with the same value " +
            "under this key are matched as the same object first, whatever happened to their GUIDs. Default diff:uid, which " +
            "Stamp IDs writes; set your own key (e.g. mark) to match by that instead. A re-bake, copy or export keeps the match " +
            "only if it carries the user text forward. The key's value is identity, so it is never reported as a change.",
            GH_ParamAccess.item, DiffSettings.DefaultStableKey);
        p.AddNumberParameter("Tolerance", "Tolerance",
            "Shape tolerance in model A's units: geometry that deviates less is not a real change. Empty: auto, the larger of the " +
            "two models' absolute tolerances.",
            GH_ParamAccess.item);
        p[1].Optional = true;
        p.AddTextParameter("Ignore", "Ignore",
            "Properties not to compare: " + string.Join(", ", Vocabulary.Names) + ". Print width and display order are ignored by default.",
            GH_ParamAccess.list);
        p[2].Optional = true;
        p.AddTextParameter("Scope", "Scope",
            "I meant to change: a layer pattern such as Facade::* (every layer under Facade). Changes on any other layer are " +
            "flagged outside-scope and come first, and the summary says what changed outside it. * matches any text, :: " +
            "included; ? one character; case is ignored. Empty: no scope.",
            GH_ParamAccess.item);
        p[3].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddGenericParameter("Settings", "Settings", "Diff settings. Connect to Settings of Model Diff.", GH_ParamAccess.item);

    protected override void SolveInstance(IGH_DataAccess da)
    {
        string key = DiffSettings.DefaultStableKey;
        da.GetData(0, ref key);
        double tol = double.NaN;
        da.GetData(1, ref tol);
        var ignore = Texts(da, 2);
        string? scope = null;
        da.GetData(3, ref scope);

        if (string.IsNullOrWhiteSpace(key)) key = DiffSettings.DefaultStableKey;
        double? shapeTolerance = null;
        if (!double.IsNaN(tol))
        {
            if (!(tol > 0) || double.IsInfinity(tol))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Tolerance must be a positive number, or empty for auto.");
                return;
            }
            shapeTolerance = tol;
        }
        var toggles = Vocabulary.Ignore(new PropertyToggles(), ignore, out var unknown);
        if (unknown.Count > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"Unknown property {string.Join(", ", unknown)}. Use: {string.Join(", ", Vocabulary.Names)}.");

        var settings = new DiffSettings
        {
            StableKey = key.Trim(),
            ShapeTolerance = shapeTolerance,
            Toggles = toggles,
            ScopeGlob = string.IsNullOrWhiteSpace(scope) ? null : scope.Trim(),
        };
        da.SetData(0, new SettingsGoo(settings));
    }
}

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
        string tol = s.ShapeTolerance is double t ? t.ToString("0.######", CultureInfo.InvariantCulture) : "auto";
        return $"Key {s.StableKey} · Tolerance {tol} · Ignoring {(ignored.Count == 0 ? "nothing" : string.Join(", ", ignored))}" +
            (s.ScopeGlob is { } scope ? $" · Scope {scope}" : "");
    }
}
