namespace DittoHopper.Rhino;

/// <summary>
/// How Grasshopper's Open Review reaches the Rhino plug-in's review panel. The .gha and the .rhp both reference this
/// assembly, and Yak installs them in one package folder, so Rhino loads one copy of it and both plug-ins share this
/// static: the plug-in registers its panel here when it loads, and Open Review hands results to it. Neither plug-in
/// references the other.
/// </summary>
public static class ReviewHub
{
    /// <summary>The Rhino plug-in's id (DittoHopper.rhp). Never change it.</summary>
    public static readonly Guid PlugInId = new("a066bd22-92f8-42bf-95ad-9a98373f7e3a");

    private static Func<DiffRun, string?>? show;

    /// <summary>
    /// Called by the plug-in when it loads (and with null when it shuts down). The handler shows a result and returns
    /// null, or returns why it can't.
    /// </summary>
    public static void Register(Func<DiffRun, string?>? handler) => show = handler;

    /// <summary>True once the plug-in has registered.</summary>
    public static bool Ready => show is not null;

    /// <summary>
    /// Shows a result in the review panel, loading the plug-in first if Rhino hasn't yet. Call on Rhino's UI thread.
    /// Returns null when it was shown, else why not: the plug-in is not installed or did not load, or the panel
    /// refused the result.
    /// </summary>
    public static string? Show(DiffRun run)
    {
        if (show is null) global::Rhino.PlugIns.PlugIn.LoadPlugIn(PlugInId);
        return show is { } handler
            ? handler(run)
            : "The DittoHopper Rhino plug-in did not load, so there is no review panel. Reinstall the DittoHopper package.";
    }
}
