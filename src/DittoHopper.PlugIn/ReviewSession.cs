using DittoHopper.Core;
using DittoHopper.Rhino;
using Rhino;
using Rhino.Geometry;

namespace DittoHopper.PlugIn;

/// <summary>What the viewport overlay shows. Card + tags is the default.</summary>
public enum HudMode { CardAndTags, Card, Strip, Off }

/// <summary>
/// Where a review's A and B come from, so Refresh can read them again. A null path is the document:
/// <see cref="APath"/> null is its last save, <see cref="BPath"/> null is the document as it is now.
/// </summary>
public sealed record ReviewSource(uint Document, string? APath, string? BPath)
{
    /// <summary>Snapshots both sides. Throws when the document is closed or has never been saved and A is its last save.</summary>
    public (RhinoSnapshot A, RhinoSnapshot B) Load()
    {
        RhinoDoc Doc() => RhinoDoc.FromRuntimeSerialNumber(Document)
            ?? throw new InvalidOperationException("The document this review compares is closed.");
        if (APath is null && BPath is null) return RhinoSnapshot.DocumentVsLastSave(Doc());
        var a = APath is null ? LastSave(Doc()) : RhinoSnapshot.FromFile(APath);
        try
        {
            return (a, BPath is null ? RhinoSnapshot.FromDocument(Doc(), Name(Doc()) + " (open)") : RhinoSnapshot.FromFile(BPath));
        }
        catch
        {
            a.Dispose();
            throw;
        }
    }

    private static RhinoSnapshot LastSave(RhinoDoc doc)
    {
        if (string.IsNullOrEmpty(doc.Path) || !File.Exists(doc.Path))
            throw new InvalidOperationException("This document has not been saved yet, so there is no last save to compare with.");
        using var file = global::Rhino.FileIO.File3dm.Read(doc.Path) ?? throw new IOException($"Could not read the last save, '{doc.Path}'.");
        return RhinoSnapshot.FromFile(file, Name(doc) + " (last save)");
    }

    private static string Name(RhinoDoc doc) => string.IsNullOrEmpty(doc.Name) ? "Untitled" : doc.Name;
}

/// <summary>
/// The one review the panel and the viewport show, per Rhino session: the diff, its <see cref="Core.Review"/>, the
/// selected and hovered rows and the HUD mode. The panel and the conduit only read it and call its methods; every
/// change raises <see cref="Changed"/>. Use on Rhino's UI thread.
/// Snapshots are dropped, not disposed, when a review is replaced: a result from Grasshopper is still Grasshopper's.
/// </summary>
public sealed class ReviewSession
{
    public static ReviewSession Current { get; } = new();

    private List<DiffScene.Item>? scene;
    private HashSet<Guid> bIds = [];

    public DiffRun? Run { get; private set; }
    public Review? Review { get; private set; }
    /// <summary>How to read A and B again on Refresh; null for a result from Grasshopper, which Refresh re-diffs.</summary>
    public ReviewSource? Source { get; private set; }
    public string Scope { get; private set; } = "";
    public string Gate { get; private set; } = "";
    /// <summary>Index into <see cref="Core.Review.Rows"/>, or -1.</summary>
    public int Selected { get; private set; } = -1;
    public int Hover { get; private set; } = -1;
    public HudMode Mode { get; private set; } = HudMode.CardAndTags;
    /// <summary>The last error or note, for the panel's status line.</summary>
    public string? Status { get; private set; }

    public event Action? Changed;

    public ReviewRow? SelectedRow => Row(Selected);
    public ReviewRow? HoverRow => Row(Hover);
    private ReviewRow? Row(int i) => Review is { } r && i >= 0 && i < r.Rows.Count ? r.Rows[i] : null;

    /// <summary>The open document B is, live, or null when B is a file. Selection works only then.</summary>
    public RhinoDoc? BDocument => Run?.BDocument;

    /// <summary>The document the review draws in: B's when B is live, else the active one.</summary>
    public RhinoDoc? Document => BDocument ?? RhinoDoc.ActiveDoc;

    /// <summary>Shows a result: replaces the review, keeps the gate and HUD mode, and opens the panel.</summary>
    public void Show(DiffRun run, ReviewSource? source)
    {
        Source = source;
        Scope = run.Result.Settings.ScopeGlob ?? "";
        Status = null;
        Set(run);
        Selected = Hover = -1;
        ReviewConduit.Instance.Enabled = true;
        Notify();
        OpenPanel?.Invoke();
    }

    /// <summary>Opens the panel; set by the plug-in, so a headless session can run without one.</summary>
    public Action? OpenPanel { get; set; }

    /// <summary>Reads A and B again when the review knows where they came from, then diffs them with the current scope.</summary>
    public void Refresh()
    {
        if (Run is null) return;
        try
        {
            var (a, b) = Source is { } s ? s.Load() : (Run.A, Run.B);
            Rediff(a, b);
            Status = null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or ArgumentException)
        {
            Status = ex.Message;
        }
        Notify();
    }

    /// <summary>"I meant to change": a layer pattern, empty for none. Diffs again, since scope flags change sets.</summary>
    public void SetScope(string scope)
    {
        scope = scope.Trim();
        if (Run is null || scope == Scope) return;
        Scope = scope;
        Rediff(Run.A, Run.B);
        Notify();
    }

    /// <summary>The QA gate's layer patterns, as in Assert Unchanged; empty for no gate.</summary>
    public void SetGate(string gate)
    {
        gate = gate.Trim();
        if (gate == Gate) return;
        Gate = gate;
        if (Run is not null) Review = Core.Review.Build(Run, Gate);
        Notify();
    }

    public void Select(int index)
    {
        int clamped = Review is { Rows.Count: > 0 } r ? Math.Clamp(index, -1, r.Rows.Count - 1) : -1;
        if (clamped == Selected) return;
        Selected = clamped;
        Notify();
    }

    /// <summary>J and K: the next or previous row.</summary>
    public void Move(int delta) => Select(Selected < 0 ? (delta > 0 ? 0 : (Review?.Rows.Count ?? 0) - 1) : Math.Max(0, Selected + delta));

    public void SetHover(int index)
    {
        if (index == Hover) return;
        Hover = index;
        Notify();
    }

    public void SetMode(HudMode mode)
    {
        if (mode == Mode) return;
        Mode = mode;
        Notify();
    }

    /// <summary>Stops drawing; the review stays, so reopening the panel shows it again.</summary>
    public void Hide()
    {
        ReviewConduit.Instance.Enabled = false;
        Redraw();
    }

    /// <summary>The things the viewport draws, built on first use after a change. B comes from the live document when it is one.</summary>
    public IReadOnlyList<DiffScene.Item> Scene
    {
        get
        {
            if (scene is null && Run is { } run)
            {
                var live = BDocument;
                scene = DiffScene.Build(run, ghost: true, Document, live is null ? null : id => live.Objects.FindId(id)?.Geometry);
            }
            return scene ?? [];
        }
    }

    /// <summary>True for a document object that is part of live B, which the conduit draws in its status colour instead.</summary>
    public bool IsDrawnByReview(Guid id) => BDocument is not null && bIds.Contains(id);

    /// <summary>The objects of a row, A and B ids both, as the scene's items name them.</summary>
    public static HashSet<Guid> Ids(ReviewRow row) => [.. row.Objects.SelectMany(p => new[] { p.A, p.B }).OfType<Guid>()];

    /// <summary>A row's extent in the viewport document's units, from what the scene draws.</summary>
    public BoundingBox BoxOf(ReviewRow row)
    {
        var ids = Ids(row);
        var box = BoundingBox.Empty;
        foreach (var item in Scene)
            if (ids.Contains(item.Id)) box.Union(item.Geometry.GetBoundingBox(item.Xform));
        return box;
    }

    /// <summary>The live document changed: rebuild the scene from it on the next draw.</summary>
    public void DocumentChanged(RhinoDoc doc)
    {
        if (BDocument?.RuntimeSerialNumber != doc.RuntimeSerialNumber) return;
        scene = null;
    }

    /// <summary>F: zooms the active view to the row. False when it has nothing to draw.</summary>
    public bool ZoomTo(ReviewRow? row)
    {
        if (row is null || Document is not { } doc || doc.Views.ActiveView is not { } view) return false;
        var box = BoxOf(row);
        if (!box.IsValid) return false;
        // A tiny nudge's box is almost a point: give the zoom some room.
        double pad = Math.Max(box.Diagonal.Length * 0.5, 50 * doc.ModelAbsoluteTolerance);
        box.Inflate(pad);
        view.ActiveViewport.ZoomBoundingBox(box);
        view.Redraw();
        return true;
    }

    /// <summary>Selects the row's objects in the document. Only where B is the document; removed objects aren't in it.</summary>
    public int SelectInRhino(ReviewRow? row)
    {
        if (row is null) return 0;
        if (BDocument is not { } doc)
        {
            Status = "B is a file, so there is nothing in the document to select.";
            Notify();
            return 0;
        }
        doc.Objects.UnselectAll();
        int n = doc.Objects.Select(row.Objects.Select(p => p.B).OfType<Guid>().Where(id => doc.Objects.FindId(id) is not null));
        Status = n == row.Objects.Count ? null : $"Selected {Describe.Plural(n, "object")}; the rest are only in A.";
        doc.Views.Redraw();
        Notify();
        return n;
    }

    private void Rediff(RhinoSnapshot a, RhinoSnapshot b)
    {
        var settings = (Run?.Result.Settings ?? new DiffSettings()) with { ScopeGlob = Scope.Length == 0 ? null : Scope };
        var selected = SelectedRow?.Id;
        Set(new DiffRun(RhinoDiff.Diff(a, b, settings), a, b));
        // Change set and event ids are stable across runs, so the selection survives a refresh when its row does.
        Selected = selected is null || Review is null ? -1 : Review.Rows.ToList().FindIndex(r => r.Id == selected);
        Hover = -1;
    }

    private void Set(DiffRun run)
    {
        Run = run;
        Review = Core.Review.Build(run, Gate);
        bIds = [.. run.ModelB.Objects.Select(o => o.Id)];
        scene = null;
    }

    private void Notify()
    {
        Changed?.Invoke();
        Redraw();
    }

    private void Redraw()
    {
        if (Document is { } doc) doc.Views.Redraw();
    }
}
