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
/// <para>
/// A review belongs to a host document: B's when B is the open document, else the document it was started from. It
/// draws only there, and it is retired when that document closes. When B is the open document and that document
/// changes, the review is <see cref="Stale"/> until a Refresh: its statuses and gate no longer describe the model.
/// </para>
/// Snapshots the command read are disposed when their review is replaced or retired; a result from Grasshopper is
/// borrowed, since its snapshots are still Grasshopper's.
/// </summary>
public sealed class ReviewSession
{
    public static ReviewSession Current { get; } = new();

    /// <summary>The panel's and the HUD's words for a stale review.</summary>
    public const string StaleText = "Model changed since this review — Refresh";

    private List<DiffScene.Item>? scene;
    private Dictionary<Guid, BoundingBox> boxes = [];
    private BoundingBox extent = BoundingBox.Empty;
    private HashSet<Guid> bIds = [];
    private uint? host;
    private global::Rhino.UnitSystem? sceneUnits;
    /// <summary>True when the review read its snapshots itself (the command), so it disposes them.</summary>
    private bool owned;

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

    /// <summary>
    /// B is the open document and it changed since the review: the statuses and the gate are out of date. Only a
    /// successful Refresh clears it; changing the scope or the gate re-uses the same snapshots, so it doesn't.
    /// </summary>
    public bool Stale { get; private set; }

    public event Action? Changed;

    public ReviewRow? SelectedRow => Row(Selected);
    public ReviewRow? HoverRow => Row(Hover);
    private ReviewRow? Row(int i) => Review is { } r && i >= 0 && i < r.Rows.Count ? r.Rows[i] : null;

    /// <summary>The open document B is, live, or null when B is a file. Selection works only then.</summary>
    public RhinoDoc? BDocument => Run?.BDocument;

    /// <summary>The document the review belongs to and draws in, while it is open.</summary>
    public RhinoDoc? Document => host is uint sn ? RhinoDoc.FromRuntimeSerialNumber(sn) : null;

    public bool IsHost(RhinoDoc? doc) => doc is not null && host == doc.RuntimeSerialNumber;

    /// <summary>
    /// Shows a result: replaces the review, keeps the gate and HUD mode, and opens the panel. <paramref name="source"/>
    /// is how the command read it, and the review then owns the snapshots; null for a result from Grasshopper.
    /// A result whose B document has changed since it was captured (a cached Grasshopper result, say) starts stale. One
    /// whose B document has closed is refused, and the current review stays: it is never moved to another document.
    /// Returns null when shown, else why not.
    /// </summary>
    public string? Show(DiffRun run, ReviewSource? source)
    {
        if (run.BDocumentClosed)
        {
            Status = "B's document was closed, so this result can't be reviewed. Diff the model again.";
            Changed?.Invoke();
            return Status;
        }
        Set(run);
        owned = source is not null;
        Source = source;
        host = run.BDocument?.RuntimeSerialNumber ?? source?.Document ?? RhinoDoc.ActiveDoc?.RuntimeSerialNumber;
        Scope = run.Result.Settings.ScopeGlob ?? "";
        Status = null;
        Stale = run.BOutOfDate;
        Selected = Hover = -1;
        ReviewConduit.Instance.Enabled = true;
        Notify();
        OpenPanel?.Invoke();
        return null;
    }

    /// <summary>Opens the panel; set by the plug-in, so a headless session can run without one.</summary>
    public Action? OpenPanel { get; set; }

    /// <summary>
    /// Reads A and B again when the review knows where they came from, then diffs them with the current scope; that
    /// clears <see cref="Stale"/>. A result from Grasshopper can only be diffed again: recompute it there.
    /// </summary>
    public void Refresh()
    {
        if (Run is null) return;
        if (Source is not { } source)
        {
            Rediff(Run.A, Run.B);
            Status = Stale ? "This review came from Grasshopper: recompute the definition to review the model as it is now." : null;
            Notify();
            return;
        }
        try
        {
            var (a, b) = source.Load();
            try
            {
                Rediff(a, b);
            }
            catch
            {
                // The diff failed: these snapshots never became the review's.
                a.Dispose();
                b.Dispose();
                throw;
            }
            Status = null;
            Stale = false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or InvalidDataException or ArgumentException)
        {
            Status = ex.Message;
        }
        Notify();
    }

    /// <summary>"I meant to change": a layer pattern, empty for none. Diffs the same snapshots again, since scope flags change sets.</summary>
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

    /// <summary>Ends the review because its document closed. The panel shows its empty state.</summary>
    public void Retire()
    {
        ReviewConduit.Instance.Enabled = false;
        Release(null);
        Run = null;
        Review = null;
        Source = null;
        host = null;
        owned = Stale = false;
        Selected = Hover = -1;
        scene = null;
        boxes = [];
        extent = BoundingBox.Empty;
        bIds = [];
        Status = "The review's document was closed.";
        Changed?.Invoke();
    }

    /// <summary>
    /// Something a snapshot reads changed in <paramref name="doc"/> (<see cref="DocumentChanges"/>). The drawing follows
    /// the host document's units, so its scene is rebuilt when they changed. When it is live B, the review no longer
    /// describes the model: it is stale until a Refresh.
    /// </summary>
    public void ModelChanged(RhinoDoc doc)
    {
        if (IsHost(doc) && sceneUnits is { } units && units != doc.ModelUnitSystem) scene = null;
        if (Stale || !IsBDocument(doc)) return;
        Stale = true;
        Changed?.Invoke();
    }

    /// <summary>A document closed: the review goes with its host.</summary>
    public void DocumentClosed(RhinoDoc doc)
    {
        if (IsHost(doc)) Retire();
    }

    private bool IsBDocument(RhinoDoc doc) => BDocument?.RuntimeSerialNumber == doc.RuntimeSerialNumber;

    /// <summary>
    /// The things the viewport draws, in the host document's units, built on first use after a change: always the
    /// reviewed snapshots, so the drawing matches the statuses.
    /// </summary>
    public IReadOnlyList<DiffScene.Item> Scene
    {
        get
        {
            if (scene is null && Run is { } run)
            {
                scene = DiffScene.Build(run, ghost: true, Document);
                sceneUnits = Document?.ModelUnitSystem;
                // Each object's extent once per build: the conduit's bounding box and the HUD's anchors use them every frame.
                boxes = [];
                extent = BoundingBox.Empty;
                foreach (var item in scene)
                {
                    var box = item.Geometry.GetBoundingBox(item.Xform);
                    boxes[item.Id] = boxes.TryGetValue(item.Id, out var b) ? BoundingBox.Union(b, box) : box;
                    extent.Union(box);
                }
            }
            return scene ?? [];
        }
    }

    /// <summary>Everything the scene draws.</summary>
    public BoundingBox Extent
    {
        get
        {
            _ = Scene;
            return extent;
        }
    }

    /// <summary>
    /// True for a document object that is part of live B and that the conduit draws in its status colour instead. Not
    /// while stale: the document then draws itself as it is, with the reviewed snapshot over it.
    /// </summary>
    public bool IsDrawnByReview(Guid id) => !Stale && BDocument is not null && bIds.Contains(id);

    /// <summary>The objects of a row, A and B ids both, as the scene's items name them.</summary>
    public static HashSet<Guid> Ids(ReviewRow row) => [.. row.Objects.SelectMany(p => new[] { p.A, p.B }).OfType<Guid>()];

    /// <summary>A row's extent in the host document's units, from what the scene draws.</summary>
    public BoundingBox BoxOf(ReviewRow row)
    {
        _ = Scene;
        var box = BoundingBox.Empty;
        foreach (var id in Ids(row))
            if (boxes.TryGetValue(id, out var b)) box.Union(b);
        return box;
    }

    /// <summary>
    /// F: zooms the active view to the row. This works for two files too: the review draws their snapshots in the host
    /// document's view, so zooming to that drawing is useful even though nothing there can be selected. False when the
    /// row has nothing drawn.
    /// </summary>
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
        var inB = row.Objects.Select(p => p.B).OfType<Guid>().Distinct().ToList();
        int onlyInA = row.Objects.Count(p => p.B is null);
        int selected = doc.Objects.Select(inB.Where(id => doc.Objects.FindId(id) is not null));
        var missed = new List<string>();
        if (onlyInA > 0) missed.Add($"{Describe.Count(onlyInA)} only in A");
        if (inB.Count > selected) missed.Add($"{Describe.Count(inB.Count - selected)} locked, hidden or deleted since the review");
        Status = missed.Count == 0 ? null : $"Selected {Describe.Plural(selected, "object")}; not selected: {string.Join(", ", missed)}.";
        doc.Views.Redraw();
        Notify();
        return selected;
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
        Release(run);
        Run = run;
        Review = Core.Review.Build(run, Gate);
        bIds = [.. run.ModelB.Objects.Select(o => o.Id)];
        scene = null;
    }

    /// <summary>Disposes the current review's snapshots when it owns them and <paramref name="next"/> doesn't use them.</summary>
    private void Release(DiffRun? next)
    {
        if (!owned || Run is not { } old) return;
        if (!ReferenceEquals(old.A, next?.A) && !ReferenceEquals(old.A, next?.B)) old.A.Dispose();
        if (!ReferenceEquals(old.B, next?.A) && !ReferenceEquals(old.B, next?.B)) old.B.Dispose();
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
