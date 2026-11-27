using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DittoHopper.Grasshopper;

/// <summary>Component artwork, one glyph per component.</summary>
public enum ComponentIcon { Snapshot, DiffSettings, ModelDiff, DiffFilter, DiffPreview, AssertUnchanged, StampIds }

/// <summary>
/// Draws every icon in code on a 24-unit grid (the logo on a 64-unit grid), so they stay crisp at any size and no
/// image resources are needed: white glyphs on one dark tile, with the diff's status colours as accents. Uses only
/// System.Drawing, so tools/Generate-Icons.ps1 can compile this file on its own to write assets/icon.png.
/// </summary>
public static class Icons
{
    // The status palette from the design doc, tuned to read on the dark tile.
    public static readonly Color Added = Color.FromArgb(34, 197, 94);
    public static readonly Color Removed = Color.FromArgb(239, 68, 68);
    public static readonly Color Modified = Color.FromArgb(245, 158, 11);
    public static readonly Color Event = Color.FromArgb(59, 130, 246);
    public static readonly Color Unchanged = Color.FromArgb(156, 163, 175);

    private static readonly Color Tile = Color.FromArgb(38, 42, 48);
    private static readonly Color Ink = Color.White;
    private static readonly Color LogoTile = Color.FromArgb(217, 119, 6); // assets/icon.svg
    private const float W = 1.75f;

    public static Bitmap Create(ComponentIcon icon, int size = 24) => Render(size, 24, 5, Tile, g => Glyph(g, icon));

    /// <summary>The plug-in mark from assets/icon.svg: a white D on an amber tile.</summary>
    public static Bitmap Logo(int size = 64) => Render(size, 64, 14, LogoTile, g =>
    {
        using var family = Family();
        using var path = new GraphicsPath();
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        path.AddString("D", family, (int)FontStyle.Bold, 40, new RectangleF(0, 2, 64, 64), format);
        using var brush = new SolidBrush(Ink);
        g.FillPath(brush, path);
    });

    private static FontFamily Family()
    {
        try { return new FontFamily("Segoe UI"); }
        catch (ArgumentException) { return FontFamily.GenericSansSerif; }
    }

    private static Bitmap Render(int size, float unit, float corner, Color tile, Action<Graphics> draw)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / unit, size / unit);
        using (var path = Rounded(0, 0, unit, unit, corner)) using (var brush = new SolidBrush(tile)) g.FillPath(brush, path);
        draw(g);
        return bitmap;
    }

    private static void Glyph(Graphics g, ComponentIcon icon)
    {
        switch (icon)
        {
            case ComponentIcon.Snapshot: // A viewfinder framing a model.
                Lines(g, Ink, W, 4.5f, 9, 4.5f, 4.5f, 9, 4.5f);
                Lines(g, Ink, W, 15, 4.5f, 19.5f, 4.5f, 19.5f, 9);
                Lines(g, Ink, W, 19.5f, 15, 19.5f, 19.5f, 15, 19.5f);
                Lines(g, Ink, W, 9, 19.5f, 4.5f, 19.5f, 4.5f, 15);
                Fill(g, Unchanged, 8.5f, 8.5f, 7, 7, 1.5f);
                break;
            case ComponentIcon.DiffSettings: // Three sliders.
                foreach (var (y, x) in new[] { (6.5f, 14f), (12f, 8f), (17.5f, 16f) })
                {
                    Line(g, Unchanged, W * 0.8f, 4.5f, y, 19.5f, y);
                    Dot(g, Ink, x, y, 2.4f);
                }
                break;
            case ComponentIcon.ModelDiff: // A and B, overlapping: what moved.
                Outline(g, Ink, W, 4, 4, 11, 11, 2);
                Fill(g, Modified, 9, 9, 11, 11, 2);
                Outline(g, Ink, W * 0.6f, 9, 9, 6, 6, 0.5f);
                break;
            case ComponentIcon.DiffFilter: // A funnel letting one change through.
                using (var pen = Round(Ink, W))
                    g.DrawPolygon(pen, Points(4.5f, 5, 19.5f, 5, 13.5f, 12, 13.5f, 17, 10.5f, 19, 10.5f, 12));
                Dot(g, Modified, 18.5f, 18.5f, 2.6f);
                break;
            case ComponentIcon.DiffPreview: // An eye whose pupil is the change colour.
                using (var eye = new GraphicsPath())
                {
                    eye.AddBezier(3.5f, 12, 7.5f, 5.5f, 16.5f, 5.5f, 20.5f, 12);
                    eye.AddBezier(20.5f, 12, 16.5f, 18.5f, 7.5f, 18.5f, 3.5f, 12);
                    using var pen = Round(Ink, W); g.DrawPath(pen, eye);
                }
                Dot(g, Modified, 12, 12, 3.2f);
                Dot(g, Added, 4, 20, 1.6f); Dot(g, Removed, 8, 20, 1.6f); Dot(g, Event, 12, 20, 1.6f);
                break;
            case ComponentIcon.AssertUnchanged: // A tick in a ring: the gate passes.
                using (var pen = Round(Ink, W)) g.DrawEllipse(pen, 4, 4, 16, 16);
                Lines(g, Added, W * 1.3f, 8, 12.5f, 11, 15.5f, 16.5f, 9);
                break;
            case ComponentIcon.StampIds: // A rubber stamp: the one component that writes to the model.
                using (var pen = Round(Ink, W))
                {
                    g.DrawEllipse(pen, 9, 3.5f, 6, 5);
                    g.DrawLine(pen, 12, 8.5f, 12, 12);
                }
                Fill(g, Ink, 5, 12, 14, 4.5f, 1.2f);
                Line(g, Modified, W * 1.2f, 5, 19.5f, 19, 19.5f);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(icon), icon, null);
        }
    }

    private static Pen Round(Color color, float width) =>
        new(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };

    private static void Line(Graphics g, Color color, float width, float x1, float y1, float x2, float y2)
    {
        using var pen = Round(color, width);
        g.DrawLine(pen, x1, y1, x2, y2);
    }

    private static void Lines(Graphics g, Color color, float width, params float[] xy)
    {
        using var pen = Round(color, width);
        g.DrawLines(pen, Points(xy));
    }

    private static void Dot(Graphics g, Color color, float x, float y, float radius)
    {
        using var brush = new SolidBrush(color);
        g.FillEllipse(brush, x - radius, y - radius, 2 * radius, 2 * radius);
    }

    private static void Fill(Graphics g, Color color, float x, float y, float w, float h, float r)
    {
        using var path = Rounded(x, y, w, h, r);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    private static void Outline(Graphics g, Color color, float width, float x, float y, float w, float h, float r)
    {
        using var path = Rounded(x, y, w, h, r);
        using var pen = Round(color, width);
        g.DrawPath(pen, path);
    }

    private static PointF[] Points(params float[] xy)
    {
        var points = new PointF[xy.Length / 2];
        for (int i = 0; i < points.Length; i++) points[i] = new PointF(xy[2 * i], xy[2 * i + 1]);
        return points;
    }

    private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        float d = 2 * r;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
