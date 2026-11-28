using DittoHopper.Core;
using Eto.Drawing;

namespace DittoHopper.PlugIn;

/// <summary>
/// Paints the review panel's custom parts with Eto: the Changes list, the Unchanged tab and the object card, in the
/// mock's dark palette. Each method lays out and, when given a <see cref="Graphics"/>, paints; with null it only
/// measures. Every word comes from the shared <see cref="Review"/>.
/// </summary>
public sealed class ReviewPainter : IDisposable
{
    public static readonly Color Back = Rgb(30, 33, 38), Back2 = Rgb(38, 42, 48), Back3 = Rgb(47, 52, 59), CardBack = Rgb(26, 29, 33);
    public static readonly Color Rule = Rgb(58, 64, 72), Ink = Rgb(230, 232, 235), Dim = Rgb(154, 161, 171), Faint = Rgb(107, 114, 124);
    private static readonly Color SelectedBack = Rgb(37, 44, 56), HoverBack = Rgb(36, 40, 46), Blue = Rgb(59, 130, 246);
    private static readonly Color FlagBack = Color.FromArgb(251, 146, 60, 41), FlagInk = Rgb(253, 186, 116);
    private static readonly Color From = Rgb(248, 113, 113), To = Rgb(252, 211, 77), Tick = Rgb(74, 222, 128);

    private readonly Font name = new(FontFamilies.Sans, 9, FontStyle.Bold, FontDecoration.None);
    private readonly Font body = new(FontFamilies.Sans, 8.5f, FontStyle.None, FontDecoration.None);
    private readonly Font small = new(FontFamilies.Sans, 7.5f, FontStyle.None, FontDecoration.None);
    private readonly Font caps = new(FontFamilies.Sans, 7.5f, FontStyle.Bold, FontDecoration.None);
    private readonly Font mono = new(FontFamilies.Monospace, 7, FontStyle.None, FontDecoration.None);
    private readonly Font title = new(FontFamilies.Sans, 10, FontStyle.Bold, FontDecoration.None);

    public static Color Rgb(int r, int g, int b) => Color.FromArgb(r, g, b, 255);

    public static Color Of(System.Drawing.Color c) => Color.FromArgb(c.R, c.G, c.B, c.A);

    /// <summary>One clickable row of the Changes list, by its index in <see cref="Review.Rows"/>.</summary>
    public readonly record struct Hit(RectangleF Rect, int Index);

    /// <summary>The Changes list: each group's header, then its rows. Returns the height; fills <paramref name="hits"/>.</summary>
    public float Rows(Graphics? g, Review review, float width, int selected, int hover, List<Hit> hits)
    {
        hits.Clear();
        float y = 4;
        int index = 0;
        foreach (var group in review.Groups)
        {
            y += 6;
            Text(g, group.Label.ToUpperInvariant(), caps, Faint, 12, y);
            string count = Describe.Count(group.Rows.Count);
            Text(g, count, caps, Faint, width - 12 - caps.MeasureString(count).Width, y);
            y += caps.LineHeight + 4;
            foreach (var row in group.Rows)
            {
                float top = y;
                y = Row(null, row, 0, top, width);
                var rect = new RectangleF(0, top, width, y - top);
                if (g is not null)
                {
                    if (index == selected)
                    {
                        g.FillRectangle(SelectedBack, rect);
                        g.FillRectangle(Blue, new RectangleF(0, top, 2, rect.Height));
                    }
                    else if (index == hover) g.FillRectangle(HoverBack, rect);
                    Row(g, row, 0, top, width);
                }
                hits.Add(new Hit(rect, index++));
            }
        }
        if (review.Rows.Count == 0)
            y = Wrapped(g, "Nothing changed: every object and table entry in A is in B, identical.", body, Dim, 12, y + 8, width - 24);
        return y + 8;
    }

    private float Row(Graphics? g, ReviewRow row, float x, float y, float width)
    {
        float left = x + 30, inner = width - left - 12;
        y += 6;
        g?.FillEllipse(Of(Hud.ColorOf(row.Status)), new RectangleF(x + 12, y + 4, 9, 9));
        var nameSize = name.MeasureString(row.Name);
        float nameWidth = Math.Min(nameSize.Width, inner - 60);
        TextClipped(g, row.Name, name, Ink, left, y, nameWidth);
        Pill(g, row.Badge, mono, Dim, Back3, left + nameWidth + 6, y + 1);
        y += name.LineHeight + 1;
        // At most two lines: the card has the rest.
        y = Wrapped(g, row.Description, body, Dim, left, y, inner, maxLines: 2);
        if (row.Flags.Count > 0)
        {
            y += 3;
            y = Flow(g, row.Flags, small, FlagInk, FlagBack, left, y, inner);
        }
        return y + 7;
    }

    /// <summary>The object card for the selected row, or a hint when nothing is selected.</summary>
    public float Card(Graphics? g, Review review, ReviewRow? row, float width)
    {
        float x = 12, inner = width - 24, y = 10;
        if (row is null)
            return Wrapped(g, "Select a row to see what changed on it, and what didn't. J and K move; F zooms to it.", body, Dim, x, y, inner) + 10;
        var card = review.Card(row);
        float pill = Pill(g, card.Status, caps, row.Status == ReviewStatus.Event ? Colors.White : Rgb(17, 17, 17), Of(Hud.ColorOf(row.Status)), x, y + 1);
        float titleWidth = Math.Min(title.MeasureString(card.Title).Width, inner - pill - 70);
        TextClipped(g, card.Title, title, Ink, x + pill + 7, y - 1, titleWidth);
        Pill(g, card.Badge, mono, Dim, Back3, x + pill + 7 + titleWidth + 7, y + 1);
        y += title.LineHeight + 4;
        y = Wrapped(g, card.Meta, small, Faint, x, y, inner) + 10;

        Text(g, $"CHANGED · {card.Changed.Count}", caps, Faint, x, y);
        y += caps.LineHeight + 4;
        float key = Math.Min(110, inner * 0.35f), vx = x + key, vw = inner - key;
        foreach (var c in card.Changed)
        {
            float after = Wrapped(g, c.Property, small, Dim, x, y, key - 6);
            y = Math.Max(after, Value(g, c, vx, y, vw)) + 3;
        }
        foreach (var why in card.Why) y = Wrapped(g, why, small, FlagInk, x, y + 2, inner);
        if (card.Unchanged.Count > 0)
        {
            y += 10;
            Text(g, $"UNCHANGED · {card.Unchanged.Count}", caps, Faint, x, y);
            y += caps.LineHeight + 4;
            y = Flow(g, card.Unchanged.Select(u => "✓ " + u).ToList(), small, Dim, Rgb(35, 40, 48), x, y, inner, Tick);
        }
        return y + 10;
    }

    /// <summary>
    /// One card line's value column, every wrapped line starting at <paramref name="x"/>: a described change as one
    /// value; else from → to, on one line when it fits, otherwise from, then → to below with its own lines after the arrow.
    /// Returns the y below it.
    /// </summary>
    private float Value(Graphics? g, CardChange c, float x, float y, float width)
    {
        if (c.Text is { } text) return Wrapped(g, text, body, Ink, x, y, width);
        string from = c.From ?? "none", to = c.To ?? "none";
        float arrow = small.MeasureString("→ ").Width;
        float fromWidth = mono.MeasureString(from).Width, toWidth = mono.MeasureString(to).Width;
        if (fromWidth + 4 + arrow + toWidth <= width)
        {
            Text(g, from, mono, c.From is null ? Faint : From, x, y);
            Text(g, "→", small, Dim, x + fromWidth + 4, y);
            Text(g, to, mono, c.To is null ? Faint : To, x + fromWidth + 4 + arrow, y);
            return y + mono.LineHeight;
        }
        y = Wrapped(g, from, mono, c.From is null ? Faint : From, x, y, width);
        Text(g, "→", small, Dim, x, y);
        return Wrapped(g, to, mono, c.To is null ? Faint : To, x + arrow, y, width - arrow);
    }

    /// <summary>The Unchanged tab: what was checked and found identical, per A layer.</summary>
    public float Unchanged(Graphics? g, Review review, float width)
    {
        float x = 12, inner = width - 24;
        float y = Wrapped(g, review.UnchangedSummary, body, Dim, x, 10, inner) + 4;
        foreach (var layer in review.Unchanged)
        {
            y += 8;
            string count = $"{Describe.Count(layer.Identical)} of {Describe.Count(layer.Objects)}";
            float countWidth = caps.MeasureString(count).Width;
            float top = y;
            y = Wrapped(g, layer.Path, caps, Ink, x, y, inner - countWidth - 8);
            Text(g, count, caps, Faint, x + inner - countWidth, top);
            y = Wrapped(g, layer.Names, small, Dim, x, y + 1, inner);
            if (layer.ThroughTables > 0)
                y = Wrapped(g, $"{Describe.Count(layer.ThroughTables)} of them look different only through a layer, block, material or linetype they use.",
                    small, Of(Rhino.DiffScene.Event), x, y + 1, inner);
        }
        return y + 10;
    }

    private static void Text(Graphics? g, string s, Font f, Color c, float x, float y) => g?.DrawText(f, c, x, y, s);

    /// <summary>One line, cut with an ellipsis to <paramref name="width"/>.</summary>
    private static void TextClipped(Graphics? g, string s, Font f, Color c, float x, float y, float width)
    {
        if (g is null) return;
        if (f.MeasureString(s).Width > width)
        {
            while (s.Length > 1 && f.MeasureString(s + "…").Width > width) s = s[..^1];
            s += "…";
        }
        g.DrawText(f, c, x, y, s);
    }

    /// <summary>Word-wrapped text, cut with an ellipsis after <paramref name="maxLines"/>; returns the y below it.</summary>
    private static float Wrapped(Graphics? g, string s, Font f, Color c, float x, float y, float width, int maxLines = int.MaxValue)
    {
        width = Math.Max(width, 20);
        Hud.Measure measure = (t, _) => ToSize(f.MeasureString(t));
        var lines = Hud.Wrap(s, width, 0, measure);
        if (lines.Count > maxLines)
        {
            lines = lines.Take(maxLines).ToList();
            // The text goes on past the last line shown: end that line with an ellipsis, trimmed until it fits.
            string last = lines[^1];
            while (last.Length > 0 && measure(last + "…", 0).Width > width) last = last[..^1];
            lines[^1] = last.TrimEnd() + "…";
        }
        foreach (var line in lines)
        {
            g?.DrawText(f, c, x, y, line);
            y += f.LineHeight;
        }
        return y;
    }

    /// <summary>A rounded label; returns its width.</summary>
    private static float Pill(Graphics? g, string s, Font f, Color ink, Color fill, float x, float y)
    {
        var size = f.MeasureString(s);
        float w = size.Width + 10;
        if (g is not null)
        {
            g.FillPath(fill, GraphicsPath.GetRoundRect(new RectangleF(x, y, w, size.Height + 2), 3));
            g.DrawText(f, ink, x + 5, y + 1, s);
        }
        return w;
    }

    /// <summary>Chips laid out left to right, wrapping; returns the y below them.</summary>
    private static float Flow(Graphics? g, IReadOnlyList<string> chips, Font f, Color ink, Color fill, float x, float y, float width, Color? first = null)
    {
        float cx = x, h = f.LineHeight + 2;
        foreach (var chip in chips)
        {
            float w = f.MeasureString(chip).Width + 12;
            if (cx > x && cx + w > x + width)
            {
                cx = x;
                y += h + 4;
            }
            if (g is not null)
            {
                g.FillPath(fill, GraphicsPath.GetRoundRect(new RectangleF(cx, y, w, h), 3));
                if (first is { } tick && chip.StartsWith("✓ ", StringComparison.Ordinal))
                {
                    g.DrawText(f, tick, cx + 6, y + 1, "✓");
                    g.DrawText(f, ink, cx + 6 + f.MeasureString("✓ ").Width, y + 1, chip[2..]);
                }
                else g.DrawText(f, ink, cx + 6, y + 1, chip);
            }
            cx += w + 4;
        }
        return y + h;
    }

    private static System.Drawing.SizeF ToSize(SizeF s) => new(s.Width, s.Height);

    public void Dispose()
    {
        foreach (var f in new[] { name, body, small, caps, mono, title }) f.Dispose();
    }
}
