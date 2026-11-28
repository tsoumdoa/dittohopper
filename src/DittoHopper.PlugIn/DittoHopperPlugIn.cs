using System.Drawing;
using System.Runtime.InteropServices;
using DittoHopper.Rhino;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace DittoHopper.PlugIn;

/// <summary>
/// The DittoHopper Rhino plug-in: the DittoReview command, the review panel and the viewport HUD. It registers the
/// panel with <see cref="ReviewHub"/>, so Grasshopper's Open Review can show results in it, and tells the review when
/// its document changes or closes.
/// </summary>
public sealed class DittoHopperPlugIn : global::Rhino.PlugIns.PlugIn
{
    /// <summary>The panel's icon, drawn by the same code as the Grasshopper tab's; kept for the session.</summary>
    private static Icon? panelIcon;

    private static ReviewSession Session => ReviewSession.Current;

    public override PlugInLoadTime LoadTime => PlugInLoadTime.WhenNeeded;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        panelIcon ??= LogoIcon(32);
        // One panel for the session: it shows the one review.
        Panels.RegisterPanel(this, typeof(ReviewPanel), "DittoHopper Review", panelIcon, PanelType.System);
        Session.OpenPanel = () => Panels.OpenPanel(typeof(ReviewPanel).GUID);
        ReviewHub.Register(run => Session.Show(run, null));
        // The one list of document events a snapshot depends on lives in DocumentChanges (DittoHopper.Rhino).
        DocumentChanges.Start();
        DocumentChanges.Changed += OnChanged;
        RhinoDoc.CloseDocument += OnClose;
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        DocumentChanges.Changed -= OnChanged;
        RhinoDoc.CloseDocument -= OnClose;
        ReviewHub.Register(null);
        ReviewConduit.Instance.Enabled = false;
        base.OnShutdown();
    }

    private static void OnChanged(RhinoDoc doc) => Session.ModelChanged(doc);

    private static void OnClose(object? sender, DocumentEventArgs e)
    {
        if (e.Document is { } doc) Session.DocumentClosed(doc);
    }

    /// <summary>An icon of the plug-in mark that owns its handle: the HICON GetHicon makes is copied, then destroyed.</summary>
    private static Icon LogoIcon(int size)
    {
        using var logo = Grasshopper.Icons.Logo(size);
        var handle = logo.GetHicon();
        try
        {
            using var borrowed = System.Drawing.Icon.FromHandle(handle);
            return (System.Drawing.Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
