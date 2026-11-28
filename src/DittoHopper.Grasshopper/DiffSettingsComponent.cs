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
