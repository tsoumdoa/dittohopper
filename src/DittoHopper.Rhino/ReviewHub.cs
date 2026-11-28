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

    private static Action<DiffRun>? show;

    /// <summary>Called by the plug-in when it loads (and with null when it shuts down).</summary>
    public static void Register(Action<DiffRun>? handler) => show = handler;

    /// <summary>True once the plug-in has registered.</summary>
    public static bool Ready => show is not null;

    /// <summary>
    /// Shows a result in the review panel, loading the plug-in first if Rhino hasn't yet. Call on Rhino's UI thread.
    /// False when the plug-in is not installed or did not load.
    /// </summary>
    public static bool Show(DiffRun run)
    {
        if (show is null) global::Rhino.PlugIns.PlugIn.LoadPlugIn(PlugInId);
        if (show is not { } handler) return false;
        handler(run);
        return true;
    }
}
