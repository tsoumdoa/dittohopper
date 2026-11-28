using System.Runtime.InteropServices;
using DittoHopper.Core;
using Eto.Drawing;
using Eto.Forms;
using Rhino.UI;

namespace DittoHopper.PlugIn;

/// <summary>
/// The dockable review panel: A → B, Scope, Gate, the summary and Refresh; the Changes and Unchanged tabs; the
/// object card with Zoom to and Select in Rhino; and the HUD mode. A view of <see cref="ReviewSession"/>: it shows
/// the session and calls it, and holds no review state of its own.
/// </summary>
[Guid("7f93f52a-2df2-4b9c-bf1e-75f0247cae6a")]
public sealed class ReviewPanel : Panel, IPanel
{
    private static readonly (HudMode Mode, string Text)[] Modes =
        [(HudMode.CardAndTags, "Card + tags"), (HudMode.Card, "Card"), (HudMode.Strip, "Strip"), (HudMode.Off, "Off")];

    private readonly ReviewPainter painter = new();
    private readonly List<ReviewPainter.Hit> hits = [];
    private readonly Label sides = Label(ReviewPainter.Dim), sentence = Label(ReviewPainter.Ink), gateResult = Label(ReviewPainter.Dim);
    private readonly Label status = Label(ReviewPainter.Dim);
    private readonly TextBox scope = new() { PlaceholderText = "I meant to change, e.g. Facade::*" };
    private readonly TextBox gate = new() { PlaceholderText = "Must not change, e.g. Structure::*" };
    private readonly DropDown mode = new();
    private readonly Drawable rows = new() { CanFocus = true }, unchanged = new(), card = new() { BackgroundColor = ReviewPainter.CardBack };
    private readonly Scrollable rowsScroll, unchangedScroll;
    private readonly TabPage changesPage, unchangedPage;
    private readonly Button zoom = new() { Text = "Zoom to (F)" }, select = new() { Text = "Select in Rhino" };

    private static ReviewSession Session => ReviewSession.Current;

    public ReviewPanel()
    {
        BackgroundColor = ReviewPainter.Back;
        foreach (var (_, text) in Modes) mode.Items.Add(text);
        mode.SelectedIndex = 0;
        rowsScroll = new Scrollable { Content = rows, ExpandContentWidth = true, Border = BorderType.None, BackgroundColor = ReviewPainter.Back };
        unchangedScroll = new Scrollable { Content = unchanged, ExpandContentWidth = true, Border = BorderType.None, BackgroundColor = ReviewPainter.Back };
        changesPage = new TabPage { Text = "Changes", Content = rowsScroll };
        unchangedPage = new TabPage { Text = "Unchanged", Content = unchangedScroll };
        var refresh = new Button { Text = "Refresh" };

        refresh.Click += (_, _) => Session.Refresh();
        scope.KeyDown += (_, e) => { if (e.Key == Keys.Enter) Session.SetScope(scope.Text); };
        scope.LostFocus += (_, _) => Session.SetScope(scope.Text);
        gate.KeyDown += (_, e) => { if (e.Key == Keys.Enter) Session.SetGate(gate.Text); };
        gate.LostFocus += (_, _) => Session.SetGate(gate.Text);
        mode.SelectedIndexChanged += (_, _) => { if (mode.SelectedIndex >= 0) Session.SetMode(Modes[mode.SelectedIndex].Mode); };
        zoom.Click += (_, _) => Session.ZoomTo(Session.SelectedRow);
        select.Click += (_, _) => Session.SelectInRhino(Session.SelectedRow);

        rows.Paint += (_, e) => { if (Session.Review is { } r) painter.Rows(e.Graphics, r, rows.Width, Session.Selected, Session.Hover, hits); };
        unchanged.Paint += (_, e) => { if (Session.Review is { } r) painter.Unchanged(e.Graphics, r, unchanged.Width); };
        card.Paint += (_, e) => { if (Session.Review is { } r) painter.Card(e.Graphics, r, Session.SelectedRow, card.Width); };
        rows.MouseDown += (_, e) =>
        {
            rows.Focus();
            if (HitAt(e.Location) is int i) Session.Select(i);
        };
        rows.MouseMove += (_, e) => Session.SetHover(HitAt(e.Location) ?? -1);
        rows.MouseLeave += (_, _) => Session.SetHover(-1);
        rows.KeyDown += (_, e) => e.Handled = Key(e.Key);
        rows.SizeChanged += (_, _) => Measure();
        unchanged.SizeChanged += (_, _) => Measure();
        card.SizeChanged += (_, _) => Measure();

        var header = new TableLayout
        {
            Padding = new Padding(10, 8),
            Spacing = new Size(6, 6),
            Rows =
            {
                new TableRow(new TableCell(Label(ReviewPainter.Ink, "DittoHopper Review", bold: true), true), refresh),
                new TableRow(new TableCell(sides, true)) ,
                new TableRow(new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(Label(ReviewPainter.Faint, "Scope"), new TableCell(scope, true)) } }),
                new TableRow(new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(Label(ReviewPainter.Faint, "Gate"), new TableCell(gate, true), gateResult) } }),
                new TableRow(new TableCell(sentence, true)),
                new TableRow(new TableLayout { Spacing = new Size(6, 0), Rows = { new TableRow(Label(ReviewPainter.Faint, "Viewport"), mode, null) } }),
            },
        };
        Content = new TableLayout
        {
            Rows =
            {
                header,
                new TableRow(new TabControl { Pages = { changesPage, unchangedPage } }) { ScaleHeight = true },
                card,
                new TableLayout { Padding = new Padding(10, 4), Spacing = new Size(6, 0), Rows = { new TableRow(zoom, select, null) } },
                new TableLayout { Padding = new Padding(10, 2, 10, 8), Rows = { status, Label(ReviewPainter.Faint, "J / K: next / previous row · F: zoom to it") } },
            },
        };
        Session.Changed += Update;
        Update();
    }

    private static Label Label(Color color, string text = "", bool bold = false) => new()
    {
        Text = text,
        TextColor = color,
        Wrap = WrapMode.Word,
        Font = bold ? new Font(FontFamilies.Sans, 9.5f, FontStyle.Bold, FontDecoration.None) : new Font(FontFamilies.Sans, 8.5f, FontStyle.None, FontDecoration.None),
    };

    /// <summary>J, K and F on the list. True when the key was one of them.</summary>
    public bool Key(Keys key)
    {
        switch (key)
        {
            case Keys.J or Keys.Down: Session.Move(+1); break;
            case Keys.K or Keys.Up: Session.Move(-1); break;
            case Keys.F: Session.ZoomTo(Session.SelectedRow); break;
            default: return false;
        }
        Reveal();
        return true;
    }

    private int? HitAt(PointF p) => hits.FirstOrDefault(h => h.Rect.Contains(p)) is { Rect.Height: > 0 } hit ? hit.Index : null;

    /// <summary>Scrolls the selected row into view.</summary>
    private void Reveal()
    {
        if (hits.FirstOrDefault(h => h.Index == Session.Selected) is not { Rect.Height: > 0 } hit) return;
        var visible = rowsScroll.VisibleRect;
        if (hit.Rect.Top < visible.Top || hit.Rect.Bottom > visible.Bottom)
            rowsScroll.ScrollPosition = new Point(0, Math.Max(0, (int)hit.Rect.Top - 24));
    }

    /// <summary>Shows the session: fields, counts, and the drawn parts at their measured heights.</summary>
    private void Update()
    {
        var review = Session.Review;
        sides.Text = review?.Header ?? "No review yet: run DittoReview, or use Open Review in Grasshopper.";
        sentence.Text = review?.Sentence ?? "";
        if (!scope.HasFocus) scope.Text = Session.Scope;
        if (!gate.HasFocus) gate.Text = Session.Gate;
        gateResult.Text = review?.Gate is { } g ? (g.Pass ? "✓ passes" : $"✕ {g.Why.Count}") : "";
        gateResult.TextColor = review?.Gate is { Pass: false } ? Color.FromArgb(252, 165, 165, 255) : Color.FromArgb(134, 239, 172, 255);
        gateResult.ToolTip = review?.Gate is { } why ? string.Join(Environment.NewLine, why.Why) : null;
        mode.SelectedIndex = Array.FindIndex(Modes, m => m.Mode == Session.Mode);
        changesPage.Text = $"Changes {Describe.Count(review?.Rows.Count ?? 0)}";
        unchangedPage.Text = $"Unchanged {Describe.Count(review?.Run.Result.UnchangedObjects ?? 0)}";
        zoom.Enabled = Session.SelectedRow is not null;
        select.Enabled = Session.SelectedRow is not null && Session.BDocument is not null;
        select.ToolTip = Session.BDocument is null ? "B is a file: there is nothing in the document to select." : null;
        status.Text = Session.Status ?? "";
        status.Visible = Session.Status is not null;
        Measure();
        rows.Invalidate();
        unchanged.Invalidate();
        card.Invalidate();
    }

    /// <summary>Sizes the drawn parts to their content at the current width, so the list scrolls and the card fits.</summary>
    private void Measure()
    {
        if (Session.Review is not { } review) return;
        Fit(rows, painter.Rows(null, review, rows.Width, Session.Selected, Session.Hover, hits));
        Fit(unchanged, painter.Unchanged(null, review, unchanged.Width));
        Fit(card, painter.Card(null, review, Session.SelectedRow, card.Width));
    }

    private static void Fit(Drawable d, float height)
    {
        int h = (int)Math.Ceiling(height);
        if (d.Height != h) d.Height = h;
    }

    public void PanelShown(uint documentSerialNumber, ShowPanelReason reason)
    {
        if (Session.Run is not null) ReviewConduit.Instance.Enabled = true;
    }

    public void PanelHidden(uint documentSerialNumber, ShowPanelReason reason) { }

    /// <summary>Closing the panel stops the viewport drawing; reopening it shows the review again.</summary>
    public void PanelClosing(uint documentSerialNumber, bool onCloseDocument) => Session.Hide();
}
