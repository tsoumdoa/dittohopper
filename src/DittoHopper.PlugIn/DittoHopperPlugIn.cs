using System.Drawing;
using System.Runtime.InteropServices;
using DittoHopper.Rhino;
using Rhino;
using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
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

        RhinoDoc.AddRhinoObject += OnObject;
        RhinoDoc.DeleteRhinoObject += OnObject;
        RhinoDoc.UndeleteRhinoObject += OnObject;
        RhinoDoc.ReplaceRhinoObject += OnReplace;
        RhinoDoc.ModifyObjectAttributes += OnAttributes;
        RhinoDoc.LayerTableEvent += OnLayers;
        RhinoDoc.MaterialTableEvent += OnMaterials;
        RhinoDoc.LinetypeTableEvent += OnLinetypes;
        RhinoDoc.GroupTableEvent += OnGroups;
        RhinoDoc.InstanceDefinitionTableEvent += OnBlocks;
        RhinoDoc.DocumentPropertiesChanged += OnProperties;
        RhinoDoc.CloseDocument += OnClose;
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        RhinoDoc.AddRhinoObject -= OnObject;
        RhinoDoc.DeleteRhinoObject -= OnObject;
        RhinoDoc.UndeleteRhinoObject -= OnObject;
        RhinoDoc.ReplaceRhinoObject -= OnReplace;
        RhinoDoc.ModifyObjectAttributes -= OnAttributes;
        RhinoDoc.LayerTableEvent -= OnLayers;
        RhinoDoc.MaterialTableEvent -= OnMaterials;
        RhinoDoc.LinetypeTableEvent -= OnLinetypes;
        RhinoDoc.GroupTableEvent -= OnGroups;
        RhinoDoc.InstanceDefinitionTableEvent -= OnBlocks;
        RhinoDoc.DocumentPropertiesChanged -= OnProperties;
        RhinoDoc.CloseDocument -= OnClose;
        ReviewHub.Register(null);
        ReviewConduit.Instance.Enabled = false;
        base.OnShutdown();
    }

    // Anything that can change what a diff of the document would say makes a review of it stale.
    private static void OnObject(object? sender, RhinoObjectEventArgs e) => Changed(e.TheObject?.Document);
    private static void OnReplace(object? sender, RhinoReplaceObjectEventArgs e) => Changed(e.Document);
    private static void OnAttributes(object? sender, RhinoModifyObjectAttributesEventArgs e) => Changed(e.Document);
    private static void OnLayers(object? sender, LayerTableEventArgs e) => Changed(e.Document);
    private static void OnMaterials(object? sender, MaterialTableEventArgs e) => Changed(e.Document);
    private static void OnLinetypes(object? sender, LinetypeTableEventArgs e) => Changed(e.Document);
    private static void OnGroups(object? sender, GroupTableEventArgs e) => Changed(e.Document);
    private static void OnBlocks(object? sender, InstanceDefinitionTableEventArgs e) => Changed(e.Document);

    private static void OnProperties(object? sender, DocumentEventArgs e)
    {
        if (e.Document is { } doc) Session.PropertiesChanged(doc);
    }

    private static void OnClose(object? sender, DocumentEventArgs e)
    {
        if (e.Document is { } doc) Session.DocumentClosed(doc);
    }

    private static void Changed(RhinoDoc? doc)
    {
        if (doc is not null) Session.ModelChanged(doc);
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
