using System.Drawing;
using DittoHopper.Core;
using DittoHopper.Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace DittoHopper.PlugIn;

/// <summary>A 2D thing the HUD draws, in viewport pixels.</summary>
public abstract record HudShape;
/// <summary>A filled, rounded box; a circle when the radius is half its size.</summary>
public sealed record HudBox(RectangleF Rect, float Radius, Color Fill, Color Stroke, float StrokeWidth = 0) : HudShape;
/// <summary>One line of text with its top-left corner at <see cref="At"/>.</summary>
public sealed record HudText(string Text, PointF At, Color Color, int Height) : HudShape;
public sealed record HudLine(PointF From, PointF To, Color Color, float Width) : HudShape;

/// <summary>
/// The viewport HUD, laid out from a <see cref="Review"/> as plain shapes and then drawn: the card (top left), the
/// strip (bottom centre), the tags on rows that need a look and on the selected row, and a ring round each tiny nudge.
/// Layout is separate from drawing so it can be tested without a viewport.
/// </summary>
public static class Hud
{
    /// <summary>Measures one line of text at a pixel height.</summary>
    public delegate SizeF Measure(string text, int height);

    /// <summary>
    /// A row with where it is on screen (null when it has nothing to draw or is off screen). Give one for each tagged row
    /// (<see cref="Tagged"/>) and each row to ring; the layout picks which get what.
    /// </summary>
    public readonly record struct Anchor(ReviewRow Row, PointF? At);

    public const string Font = "Segoe UI";
    private const int Small = 11, Body = 12, Title = 14, Pad = 10, CardWidth = 330;

    private static readonly Color Panel = Color.FromArgb(220, 20, 22, 26);
    private static readonly Color Text = Color.FromArgb(230, 232, 235);
    private static readonly Color Dim = Color.FromArgb(154, 161, 171);
    private static readonly Color ChipFill = Color.FromArgb(42, 47, 54);
    private static readonly Color Look = Color.FromArgb(251, 146, 60);
    private static readonly Color LookFill = Color.FromArgb(70, 251, 146, 60);
    private static readonly Color Pass = Color.FromArgb(134, 239, 172);
    private static readonly Color Fail = Color.FromArgb(252, 165, 165);
    private static readonly Color Selected = Color.FromArgb(37, 99, 235);

    public static Color ColorOf(ReviewStatus s) => s switch
    {
        ReviewStatus.Added => DiffScene.Added,
        ReviewStatus.Removed => DiffScene.Removed,
        ReviewStatus.Event => DiffScene.Event,
        _ => DiffScene.Modified,
    };

    public static Color ColorOf(ChipKind k) => k switch
    {
        ChipKind.NeedsLook => Look,
        ChipKind.Modified => DiffScene.Modified,
        ChipKind.Added => DiffScene.Added,
        ChipKind.Removed => DiffScene.Removed,
        ChipKind.Events => DiffScene.Event,
        _ => Color.FromArgb(107, 114, 128),
    };

    /// <summary>The rows that get a tag: every row that needs a look, and the selected one.</summary>
    public static IEnumerable<ReviewRow> Tagged(Review review, ReviewRow? selected) =>
        review.Rows.Where(r => r.Group == ReviewGroupKind.NeedsLook || ReferenceEquals(r, selected));

    /// <summary>
    /// The HUD's shapes. Tags and nudge rings belong to Card + tags. A <paramref name="stale"/> review says so on the card
    /// and the strip, and its gate shows as stale, never as passing.
    /// </summary>
    public static List<HudShape> Layout(Review review, ReviewRow? selected, HudMode mode, Size view, Measure measure, IReadOnlyList<Anchor> anchors,
        bool stale = false)
    {
        var shapes = new List<HudShape>();
        if (mode == HudMode.Off) return shapes;
        if (mode == HudMode.CardAndTags)
        {
            // Rings first, under everything: a change too small to see.
            foreach (var a in anchors)
                if (a.Row.Ring && a.At is PointF p)
                    shapes.Add(new HudBox(new RectangleF(p.X - 18, p.Y - 18, 36, 36), 18, Color.Transparent, DiffScene.Modified, 2));
            var tagged = Tagged(review, selected).ToHashSet();
            Tags(shapes, [.. anchors.Where(a => tagged.Contains(a.Row))], selected, view, measure);
        }
        if (mode is HudMode.CardAndTags or HudMode.Card) Card(shapes, review, selected, measure, stale);
        else Strip(shapes, review, view, measure, stale);
        return shapes;
    }

    private static void Card(List<HudShape> shapes, Review review, ReviewRow? selected, Measure measure, bool stale)
    {
        var body = new List<HudShape>();
        float x = 12 + Pad, y = 30 + Pad, inner = CardWidth - 2 * Pad;
        foreach (var line in Wrap(review.Header, inner, Small, measure)) y = Line(body, line, x, y, Dim, Small, measure);
        if (stale)
            foreach (var line in Wrap(ReviewSession.StaleText, inner, Body, measure)) y = Line(body, line, x, y + 2, Look, Body, measure);
        y += 4;
        foreach (var line in Wrap(review.Sentence, inner, Title, measure)) y = Line(body, line, x, y, Text, Title, measure);
        y += 6;
        y = Chips(body, review.Chips, x, y, inner, measure, wrap: true);
        if (review.Gate is { } gate)
        {
            y += 6;
            body.Add(new HudLine(new PointF(x, y), new PointF(x + inner, y), Color.FromArgb(51, 57, 65), 1));
            y += 5;
            // The first reason, without its flags: the card is a glance, the panel has the rest.
            string first = gate.Why.FirstOrDefault()?.Split(" · flagged:")[0] ?? "";
            // A stale verdict is withdrawn: the model it was checked on has changed.
            string verdict = stale ? "stale, Refresh to check it again"
                : gate.Pass ? "PASS" : $"FAIL · {first}" + (gate.Why.Count > 1 ? $" (+{gate.Why.Count - 1} more)" : "");
            foreach (var line in Wrap($"Gate {gate.Layers}: {verdict}", inner, Body, measure))
                y = Line(body, line, x, y, stale ? Look : gate.Pass ? Pass : Fail, Body, measure);
        }
        if (selected is not null)
        {
            y += 3;
            foreach (var line in Wrap("Showing: " + selected.Name, inner, Small, measure)) y = Line(body, line, x, y, Dim, Small, measure);
        }
        shapes.Add(new HudBox(new RectangleF(12, 30, CardWidth, y + Pad - 30), 8, Panel, Color.Transparent));
        shapes.AddRange(body);
    }

    /// <summary>
    /// One line at the bottom: the chips, then the gate. A stale review leads with its warning and the withdrawn gate, and
    /// drops chips from the end until the line fits the view, so the warning is never cut off.
    /// </summary>
    private static void Strip(List<HudShape> shapes, Review review, Size view, Measure measure, bool stale)
    {
        float y = view.Height - 36, room = view.Width - 36;
        var words = new List<(string Text, Color Color)>();
        if (stale) words.Add((ReviewSession.StaleText, Look));
        if (review.Gate is { } gate)
            words.Add(stale ? ("Gate stale", Look) : ($"Gate {(gate.Pass ? "passes" : "fails")}", gate.Pass ? Pass : Fail));
        float wordsWidth = words.Sum(w => measure(w.Text, Small).Width + 8);

        var chips = review.Chips.ToList();
        var chipShapes = new List<HudShape>();
        float chipsWidth = 0, end = y + measure("Ag", Small).Height + 4;
        while (chips.Count > 0)
        {
            chipShapes.Clear();
            end = Chips(chipShapes, chips, 0, y, float.MaxValue, measure, wrap: false, out chipsWidth);
            if (!stale || chipsWidth + wordsWidth <= room) break;
            chips.RemoveAt(chips.Count - 1);
            chipsWidth = 0;
        }
        if (chips.Count == 0) chipShapes.Clear();
        if (stale && chipsWidth + wordsWidth > room)
        {
            // Even without chips it doesn't fit: shorten the warning, never the gate.
            float rest = wordsWidth - (measure(words[0].Text, Small).Width + 8);
            words[0] = (Ellipsize(words[0].Text, room - rest - 8, Small, measure), Look);
        }

        var body = new List<HudShape>();
        float x = stale ? 0 : chipsWidth + 8;
        foreach (var (text, color) in words)
        {
            body.Add(new HudText(text, new PointF(x, y + 2), color, Small));
            x += measure(text, Small).Width + 8;
        }
        body.AddRange(stale ? chipShapes.Select(s => Shift(s, x)) : chipShapes);
        float width = stale ? x + chipsWidth : x - 8;
        float left = Math.Max(8, (view.Width - width) / 2);
        shapes.Add(new HudBox(new RectangleF(left - 10, y - 5, width + 20, end - y + 10), (end - y + 10) / 2, Panel, Color.Transparent));
        shapes.AddRange(body.Select(s => Shift(s, left)));
    }

    /// <summary>
    /// One-line tags with a leader from each anchor. A tag is cut with an ellipsis to a sensible length (the panel and the
    /// card have the whole text), goes up-right or up-left alternately so neighbours overlap less, flips side when it
    /// would leave the view, and is kept inside it.
    /// </summary>
    private static void Tags(List<HudShape> shapes, IReadOnlyList<Anchor> anchors, ReviewRow? selected, Size view, Measure measure)
    {
        float longest = Math.Clamp(view.Width - 40, 60, 320);
        int i = 0;
        foreach (var a in anchors)
        {
            if (a.At is not PointF p) continue;
            bool look = a.Row.Group == ReviewGroupKind.NeedsLook, sel = ReferenceEquals(a.Row, selected);
            string text = Ellipsize(a.Row.Tag, longest, Small, measure);
            var size = measure(text, Small);
            float w = size.Width + 16, h = size.Height + 8;
            bool right = i++ % 2 == 0;
            if (right && p.X + 28 + w > view.Width - 4) right = false;
            else if (!right && p.X - 28 - w < 4) right = true;
            float x = Math.Clamp(right ? p.X + 28 : p.X - 28 - w, 4, Math.Max(4, view.Width - w - 4));
            float y = Math.Clamp(p.Y - 34 - h / 2, 4, Math.Max(4, view.Height - h - 4));
            var rect = new RectangleF(x, y, w, h);
            var end = new PointF(right ? rect.Left : rect.Right, rect.Top + h / 2);
            shapes.Add(new HudLine(p, end, Color.FromArgb(20, 22, 26), 1.5f));
            shapes.Add(new HudBox(new RectangleF(p.X - 3, p.Y - 3, 6, 6), 3, Color.FromArgb(20, 22, 26), Color.Transparent));
            shapes.Add(new HudBox(rect, h / 2, sel ? Selected : Panel, look && !sel ? Look : Color.Transparent, look && !sel ? 1.5f : 0));
            shapes.Add(new HudText(text, new PointF(rect.X + 8, rect.Y + 4), Color.White, Small));
        }
    }

    private static float Chips(List<HudShape> shapes, IReadOnlyList<ReviewChip> chips, float x, float y, float width, Measure measure, bool wrap) =>
        Chips(shapes, chips, x, y, width, measure, wrap, out _);

    /// <summary>Lays chips out left to right, wrapping when <paramref name="wrap"/>; returns the bottom, and the width used.</summary>
    private static float Chips(List<HudShape> shapes, IReadOnlyList<ReviewChip> chips, float x, float y, float width, Measure measure, bool wrap, out float used)
    {
        float cx = x, h = 0;
        used = 0;
        foreach (var chip in chips)
        {
            var size = measure(chip.Text, Small);
            float w = size.Width + 22;
            h = size.Height + 4;
            if (wrap && cx > x && cx + w > x + width)
            {
                cx = x;
                y += h + 4;
            }
            bool look = chip.Kind == ChipKind.NeedsLook;
            shapes.Add(new HudBox(new RectangleF(cx, y, w, h), h / 2, look ? LookFill : ChipFill, Color.Transparent));
            shapes.Add(new HudBox(new RectangleF(cx + 7, y + h / 2 - 3.5f, 7, 7), 2, ColorOf(chip.Kind), Color.Transparent));
            shapes.Add(new HudText(chip.Text, new PointF(cx + 17, y + 2), look ? Color.FromArgb(253, 186, 116) : Text, Small));
            cx += w + 5;
            used = Math.Max(used, cx - 5 - x);
        }
        return y + h;
    }

    private static float Line(List<HudShape> shapes, string text, float x, float y, Color color, int height, Measure measure)
    {
        shapes.Add(new HudText(text, new PointF(x, y), color, height));
        return y + Math.Max(measure(text, height).Height, height) + 2;
    }

    /// <summary>The text, cut with an ellipsis to fit <paramref name="width"/> pixels; at least the ellipsis.</summary>
    public static string Ellipsize(string text, float width, int height, Measure measure)
    {
        if (measure(text, height).Width <= width) return text;
        for (int n = text.Length - 1; n > 0; n--)
        {
            string cut = text[..n].TrimEnd() + "…";
            if (measure(cut, height).Width <= width) return cut;
        }
        return "…";
    }

    /// <summary>Greedy word wrap to <paramref name="width"/> pixels.</summary>
    public static List<string> Wrap(string text, float width, int height, Measure measure)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && measure(next, height).Width > width)
            {
                lines.Add(line);
                line = word;
            }
            else line = next;
        }
        if (line.Length > 0) lines.Add(line);
        return lines;
    }

    private static HudShape Shift(HudShape s, float dx) => s switch
    {
        HudBox b => b with { Rect = new RectangleF(b.Rect.X + dx, b.Rect.Y, b.Rect.Width, b.Rect.Height) },
        HudText t => t with { At = new PointF(t.At.X + dx, t.At.Y) },
        HudLine l => l with { From = new PointF(l.From.X + dx, l.From.Y), To = new PointF(l.To.X + dx, l.To.Y) },
        _ => s,
    };

    /// <summary>
    /// Measures widths with the pipeline's own text renderer. Its heights come back far too small, so a line is taken
    /// to be 1.3 times the pixel height.
    /// </summary>
    public static Measure MeasureWith(DisplayPipeline p) => (text, height) =>
        new SizeF(p.Measure2dText(text, new Point2d(0, 0), false, 0, height, Font).Width, height * 1.3f);

    public static void Draw(DisplayPipeline p, IEnumerable<HudShape> shapes)
    {
        foreach (var shape in shapes)
        {
            switch (shape)
            {
                case HudBox b:
                    p.DrawRoundedRectangle(new PointF(b.Rect.X + b.Rect.Width / 2, b.Rect.Y + b.Rect.Height / 2), b.Rect.Width, b.Rect.Height,
                        b.Radius, b.Stroke, b.StrokeWidth, b.Fill);
                    break;
                case HudText t:
                    // Centred on its line (1.3 × the height, as MeasureWith says), since the measured top isn't reliable.
                    double w = p.Measure2dText(t.Text, new Point2d(0, 0), false, 0, t.Height, Font).Width;
                    p.Draw2dText(t.Text, t.Color, new Point2d(t.At.X + w / 2, t.At.Y + t.Height * 0.65), true, t.Height, Font);
                    break;
                case HudLine l:
                    p.Draw2dLine(l.From, l.To, l.Color, l.Width);
                    break;
            }
        }
    }
}
