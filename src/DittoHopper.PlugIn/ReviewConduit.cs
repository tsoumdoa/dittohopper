using System.Drawing;
using DittoHopper.Core;
using DittoHopper.Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Status = DittoHopper.Rhino.DiffScene.Status;

namespace DittoHopper.PlugIn;

/// <summary>
/// Draws the current review in the viewport. Display only: every action is in the panel.
/// <list type="bullet">
/// <item>The status colours and the ghost of A, from <see cref="DiffScene"/> as Diff Preview draws them. When B is the
/// open document its objects are drawn in their status colour instead of their own (selected objects are left to
/// Rhino, so selection still shows); for two files, the snapshots' geometry is drawn, in the document's units.</item>
/// <item>A selected row dims everything else; a hovered row is outlined.</item>
/// <item>The HUD (<see cref="Hud"/>): card, strip, tags and nudge rings.</item>
/// </list>
/// </summary>
public sealed class ReviewConduit : DisplayConduit
{
    public static ReviewConduit Instance { get; } = new();

    private static readonly Color HoverWire = Color.FromArgb(230, 255, 255, 255);

    private static ReviewSession Session => ReviewSession.Current;

    protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
    {
        foreach (var item in Session.Scene) e.IncludeBoundingBox(item.Geometry.GetBoundingBox(item.Xform));
    }

    protected override void PreDrawObject(DrawObjectEventArgs e)
    {
        var o = e.RhinoObject;
        if (Session.IsDrawnByReview(o.Id) && o.IsSelected(false) == 0 && o.Document?.RuntimeSerialNumber == Session.BDocument?.RuntimeSerialNumber)
            e.DrawObject = false;
    }

    protected override void PostDrawObjects(DrawEventArgs e) => DrawScene(e.Display);

    protected override void DrawForeground(DrawEventArgs e) => DrawHud(e.Display, e.Viewport);

    /// <summary>The status-coloured objects: shaded faces first, then wires, then the hovered row's outline on top.</summary>
    public static void DrawScene(DisplayPipeline p)
    {
        var session = Session;
        var focus = session.SelectedRow is { } row ? ReviewSession.Ids(row) : null;
        var hover = session.HoverRow is { } h ? ReviewSession.Ids(h) : null;
        var items = session.Scene;
        foreach (var item in items)
        {
            if (item.Status is not (Status.Added or Status.Modified or Status.Removed)) continue;
            if (focus is not null && !focus.Contains(item.Id)) continue;
            DiffScene.DrawShaded(p, item, DiffScene.ColorOf(item.Status), item.Status == Status.Removed ? 0.75 : 0.45);
        }
        foreach (var item in items)
        {
            var color = DiffScene.ColorOf(item.Status);
            bool quiet = item.Status is Status.Unchanged or Status.Previous;
            if (focus is not null && !focus.Contains(item.Id))
                DiffScene.DrawWires(p, item, Color.FromArgb(55, color), 1);
            else
                DiffScene.DrawWires(p, item, color, quiet ? 1 : focus is null ? 2 : 3);
        }
        if (hover is not null)
            foreach (var item in items)
                if (hover.Contains(item.Id)) DiffScene.DrawWires(p, item, HoverWire, 2);
    }

    /// <summary>Lays out and draws the HUD for one viewport.</summary>
    public static void DrawHud(DisplayPipeline p, RhinoViewport viewport)
    {
        // The frame, not the viewport: a view capture draws a frame of its own size.
        if (Layout(viewport, Hud.MeasureWith(p), p.FrameSize) is { } shapes) Hud.Draw(p, shapes);
    }

    /// <summary>The HUD's shapes for a viewport: tags and rings anchored at the projected centre of each row's objects.</summary>
    public static List<HudShape>? Layout(RhinoViewport viewport, Hud.Measure measure, Size size)
    {
        var session = Session;
        if (session.Review is not { } review) return null;
        var selected = session.SelectedRow;
        var anchors = new List<Hud.Anchor>();
        if (session.Mode != HudMode.Off)
        {
            var rows = Hud.Tagged(review, selected).Concat(review.Rows.Where(r => r.Ring)).Distinct();
            foreach (var row in rows)
            {
                var box = session.BoxOf(row);
                PointF? at = null;
                if (box.IsValid && (box.Center - viewport.CameraLocation) * viewport.CameraDirection > 0)
                {
                    // In front of the camera; tag it only where it lands on screen.
                    var s = viewport.WorldToClient(box.Center);
                    if (s.X >= 0 && s.Y >= 0 && s.X <= size.Width && s.Y <= size.Height)
                        at = new PointF((float)s.X, (float)s.Y);
                }
                anchors.Add(new Hud.Anchor(row, at));
            }
        }
        return Hud.Layout(review, selected, session.Mode, size, measure, anchors);
    }
}
