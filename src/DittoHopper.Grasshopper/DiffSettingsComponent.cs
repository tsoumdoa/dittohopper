using System.Globalization;
using DittoHopper.Core;
using Grasshopper.Kernel;

namespace DittoHopper.Grasshopper;

/// <summary>Builds the settings for Model Diff: the stable key, the shape tolerance and which properties to ignore.</summary>
public sealed class DiffSettingsComponent : DittoComponent
{
    public DiffSettingsComponent()
        : base("Diff Settings", "Settings",
            "What Model Diff compares and how. Optional: Model Diff uses these defaults without it.",
            DiffPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a02");
    public override GH_Exposure Exposure => GH_Exposure.secondary;
    protected override ComponentIcon IconKind => ComponentIcon.DiffSettings;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddTextParameter("Key", "Key",
            "User-text key that holds each object's own id, e.g. mark. Objects with the same value are the same object, " +
            "whatever happened to their GUID. diff:uid (written by Stamp IDs) always counts too.",
            GH_ParamAccess.item, DiffSettings.DefaultStableKey);
        p.AddNumberParameter("Tol", "Tol",
            "Shape tolerance in model A's units: smaller deviations are not real changes. Empty: auto, the models' absolute tolerance.",
            GH_ParamAccess.item);
        p[1].Optional = true;
        p.AddTextParameter("Ignore", "Ignore",
            "Properties not to compare: " + string.Join(", ", Settings.Names) + ". Print width and display order are ignored by default.",
            GH_ParamAccess.list);
        p[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddGenericParameter("Cfg", "Cfg", "Diff settings. Connect to Cfg of Model Diff.", GH_ParamAccess.item);

    protected override void SolveInstance(IGH_DataAccess da)
    {
        string key = DiffSettings.DefaultStableKey;
        da.GetData(0, ref key);
        double tol = double.NaN;
        da.GetData(1, ref tol);
        var ignore = Texts(da, 2);

        if (string.IsNullOrWhiteSpace(key)) key = DiffSettings.DefaultStableKey;
        double? shapeTolerance = null;
        if (!double.IsNaN(tol))
        {
            if (!(tol > 0) || double.IsInfinity(tol))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Tol must be a positive number, or empty for auto.");
                return;
            }
            shapeTolerance = tol;
        }
        var toggles = Settings.Ignore(new PropertyToggles(), ignore, out var unknown);
        if (unknown.Count > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"Unknown property {string.Join(", ", unknown)}. Use: {string.Join(", ", Settings.Names)}.");

        var settings = new DiffSettings { StableKey = key.Trim(), ShapeTolerance = shapeTolerance, Toggles = toggles };
        // M3 adds the Scope input after Ignore, setting settings.ScopeGlob.
        da.SetData(0, new SettingsGoo(settings));
    }
}

/// <summary>Property names for Ignore and a readable summary of settings.</summary>
public static class Settings
{
    private static readonly (string Name, Func<PropertyToggles, bool> Get, Func<PropertyToggles, bool, PropertyToggles> Set)[] Properties =
    [
        ("placement", t => t.Placement, (t, v) => t with { Placement = v }),
        ("shape", t => t.Shape, (t, v) => t with { Shape = v }),
        ("layer", t => t.Layer, (t, v) => t with { Layer = v }),
        ("block", t => t.BlockDefinition, (t, v) => t with { BlockDefinition = v }),
        ("groups", t => t.GroupMembership, (t, v) => t with { GroupMembership = v }),
        ("display order", t => t.DisplayOrder, (t, v) => t with { DisplayOrder = v }),
        ("color", t => t.Color, (t, v) => t with { Color = v }),
        ("material", t => t.Material, (t, v) => t with { Material = v }),
        ("linetype", t => t.Linetype, (t, v) => t with { Linetype = v }),
        ("print width", t => t.PrintWidth, (t, v) => t with { PrintWidth = v }),
        ("name", t => t.Name, (t, v) => t with { Name = v }),
        ("user text", t => t.UserText, (t, v) => t with { UserText = v }),
    ];

    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["moved"] = "placement", ["move"] = "placement", ["transform"] = "placement",
        ["geometry"] = "shape", ["colour"] = "color", ["group"] = "groups", ["usertext"] = "user text",
    };

    public static IEnumerable<string> Names => Properties.Select(p => p.Name);

    /// <summary>Lower case, without spaces, hyphens or underscores, so "User Text", "user-text" and "usertext" agree.</summary>
    public static string Normalize(string s) =>
        new([.. s.Trim().ToLower(CultureInfo.InvariantCulture).Where(c => c is not (' ' or '-' or '_'))]);

    public static PropertyToggles Ignore(PropertyToggles toggles, IEnumerable<string> names, out List<string> unknown)
    {
        unknown = [];
        foreach (string raw in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            string n = Normalize(raw);
            n = Aliases.TryGetValue(n, out var alias) ? Normalize(alias) : n;
            var property = Properties.FirstOrDefault(p => Normalize(p.Name) == n);
            if (property.Name is null) unknown.Add(raw.Trim());
            else toggles = property.Set(toggles, false);
        }
        return toggles;
    }

    public static string Describe(DiffSettings s)
    {
        var ignored = Properties.Where(p => !p.Get(s.Toggles)).Select(p => p.Name).ToList();
        string tol = s.ShapeTolerance is double t ? t.ToString("0.######", CultureInfo.InvariantCulture) : "auto";
        return $"Key {s.StableKey} · Tol {tol} · Ignoring {(ignored.Count == 0 ? "nothing" : string.Join(", ", ignored))}";
    }
}
