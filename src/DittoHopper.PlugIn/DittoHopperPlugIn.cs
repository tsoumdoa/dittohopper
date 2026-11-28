using System.Drawing;
using DittoHopper.Rhino;
using Rhino;
using Rhino.PlugIns;
using Rhino.UI;

namespace DittoHopper.PlugIn;

/// <summary>
/// The DittoHopper Rhino plug-in: the DittoReview command, the review panel and the viewport HUD. It registers the
/// panel with <see cref="ReviewHub"/>, so Grasshopper's Open Review can show results in it.
/// </summary>
public sealed class DittoHopperPlugIn : global::Rhino.PlugIns.PlugIn
{
    public override PlugInLoadTime LoadTime => PlugInLoadTime.WhenNeeded;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        using (var logo = Grasshopper.Icons.Logo(32))
        {
            var handle = logo.GetHicon();
            // One panel for the session: it shows the one review.
            Panels.RegisterPanel(this, typeof(ReviewPanel), "DittoHopper Review", System.Drawing.Icon.FromHandle(handle), PanelType.System);
        }
        ReviewSession.Current.OpenPanel = () => Panels.OpenPanel(typeof(ReviewPanel).GUID);
        ReviewHub.Register(run => ReviewSession.Current.Show(run, null));
        RhinoDoc.ReplaceRhinoObject += (_, e) => ReviewSession.Current.DocumentChanged(e.Document);
        RhinoDoc.AddRhinoObject += (_, e) => { if (e.TheObject.Document is { } doc) ReviewSession.Current.DocumentChanged(doc); };
        RhinoDoc.DeleteRhinoObject += (_, e) => { if (e.TheObject.Document is { } doc) ReviewSession.Current.DocumentChanged(doc); };
        RhinoDoc.CloseDocument += (_, e) =>
        {
            if (ReviewSession.Current.BDocument?.RuntimeSerialNumber == e.Document.RuntimeSerialNumber) ReviewSession.Current.Hide();
        };
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        ReviewHub.Register(null);
        ReviewConduit.Instance.Enabled = false;
        base.OnShutdown();
    }
}
