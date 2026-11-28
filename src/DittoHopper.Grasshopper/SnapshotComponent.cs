using DittoHopper.Core;
using DittoHopper.Rhino;
using Grasshopper.Kernel;
using Rhino;
using Rhino.FileIO;

namespace DittoHopper.Grasshopper;

/// <summary>
/// Captures one model: a .3dm file, the active document as it is now, or the active document as last saved.
/// File reads are cached per file version (path, size, last write time), so re-solving never re-reads an
/// unchanged file. A live snapshot is cached per document version (<see cref="DocumentChanges"/>, the one list of the
/// document events a snapshot depends on), so it is re-captured after any change it would see, and this component
/// expires shortly after one; a last-saved one when the document is saved or another is opened.
/// <para>
/// Ownership: snapshots this component publishes are never disposed by it, because downstream data may still
/// use them after the cache moves on. Dropping a cache entry just lets the GC finalize the geometry.
/// </para>
/// </summary>
public sealed class SnapshotComponent : DittoComponent
{
    private readonly Dictionary<string, RhinoSnapshot> cache = [];
    private readonly Dictionary<(RhinoSnapshot, string), RhinoSnapshot> filtered = [];
    private readonly HashSet<string> used = [];
    private readonly HashSet<(RhinoSnapshot, string)> usedViews = [];
    /// <summary>Serial numbers of the documents this solution captured live, and read the last save of.</summary>
    private uint? liveDoc, savedDoc;
    private bool watching, refreshScheduled;

    public SnapshotComponent()
        : base("Snapshot", "Snapshot",
            "Captures a Rhino model for Model Diff: a .3dm file (read without opening it), or the active document. " +
            "File reads are cached until the file changes. A live document snapshot updates itself shortly after you edit; " +
            "a last-saved one when you save.",
            DiffPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a01");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.Snapshot;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddTextParameter("Source", "Source",
            "Path of a .3dm file. Leave empty for the active Rhino document. A relative path is relative to this Grasshopper file.",
            GH_ParamAccess.item);
        p[0].Optional = true;
        p.AddTextParameter("Layers", "Layers",
            "Only objects on these layers. " + LayerPatterns.Help + " Filter both sides the same way: an object that left the " +
            "filtered layers shows as removed.",
            GH_ParamAccess.list);
        p[1].Optional = true;
        p.AddBooleanParameter("Saved", "Saved",
            "Only when Source is empty. True: the active document as last saved on disk. False: the active document as it is now. " +
            "Diff a Saved snapshot against a live one to see what you changed this session.",
            GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddGenericParameter("Snapshot", "Snapshot", "The model snapshot. Connect to A or B of Model Diff.", GH_ParamAccess.item);

    protected override void BeforeSolveInstance()
    {
        used.Clear();
        usedViews.Clear();
        liveDoc = savedDoc = null;
        Message = null;
        base.BeforeSolveInstance();
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        string? source = null;
        da.GetData(0, ref source);
        var glob = new LayerPatterns(Texts(da, 1));
        bool saved = false;
        da.GetData(2, ref saved);
        if (saved && !string.IsNullOrWhiteSpace(source))
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Saved only applies to the active document, when Source is empty: ignored.");

        RhinoSnapshot? snapshot;
        try
        {
            snapshot = string.IsNullOrWhiteSpace(source) ? FromDocument(saved) : FromFile(Resolve(source));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException or UnauthorizedAccessException)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            return;
        }
        if (snapshot is null) return;

        if (!glob.MatchesAll)
        {
            snapshot = Filter(snapshot, glob);
            if (snapshot.Model.Objects.Count == 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"No objects on layers matching {glob}.");
        }
        da.SetData(0, new SnapshotGoo(snapshot));
    }

    protected override void AfterSolveInstance()
    {
        foreach (var key in cache.Keys.Where(k => !used.Contains(k)).ToList())
            Forget(key);
        foreach (var key in filtered.Keys.Where(k => !usedViews.Contains(k)).ToList())
            filtered.Remove(key);
        Watch(liveDoc is not null || savedDoc is not null);
        base.AfterSolveInstance();
    }

    private RhinoSnapshot? FromDocument(bool saved)
    {
        var doc = OnPingDocument()?.RhinoDocument ?? RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "There is no active Rhino document.");
            return null;
        }
        if (!saved)
        {
            liveDoc = doc.RuntimeSerialNumber;
            Message = "Live";
            DocumentChanges.Start();
            return Cached($"doc:{doc.RuntimeSerialNumber}:{DocumentChanges.Count(doc)}", () => RhinoSnapshot.FromDocument(doc));
        }
        savedDoc = doc.RuntimeSerialNumber;
        Message = "Last saved";
        string? path = doc.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException(
                "This document has not been saved yet, so there is no last save to compare with. Save it once, then try again.");
        return FromFile(path, Path.GetFileName(path) + " (last saved)");
    }

    private RhinoSnapshot FromFile(string path, string? label = null)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException($"File not found: {path}");
        if (label is null) Message = file.Name;
        string key = $"file:{file.FullName.ToUpperInvariant()}:{file.Length}:{file.LastWriteTimeUtc.Ticks}:{label}";
        return Cached(key, () =>
        {
            using var model = File3dm.Read(file.FullName) ?? throw new IOException($"Could not read '{path}' as a Rhino file.");
            return RhinoSnapshot.FromFile(model, label ?? file.Name);
        });
    }

    private RhinoSnapshot Cached(string key, Func<RhinoSnapshot> create)
    {
        used.Add(key);
        if (!cache.TryGetValue(key, out var s))
            cache[key] = s = create();
        return s;
    }

    /// <summary>Strips the quotes Explorer's "Copy as path" adds, and resolves relative paths against this definition's folder.</summary>
    private string Resolve(string source)
    {
        string path = source.Trim().Trim('"');
        if (!Path.IsPathRooted(path) && OnPingDocument()?.FilePath is { Length: > 0 } gh)
            path = Path.Combine(Path.GetDirectoryName(gh)!, path);
        return path;
    }

    /// <summary>Keeps matching layers (and their parents, so paths stay whole) and the objects on matching layers.</summary>
    private RhinoSnapshot Filter(RhinoSnapshot s, LayerPatterns glob)
    {
        var key = (s, string.Join("\n", glob.Text));
        usedViews.Add(key);
        if (filtered.TryGetValue(key, out var hit)) return hit;

        var model = s.Model;
        var paths = DiffRun.LayerPaths(model);
        var byId = model.Layers.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        var matching = model.Layers.Where(l => glob.Matches(paths.GetValueOrDefault(l.Id))).Select(l => l.Id).ToHashSet();
        var keep = new HashSet<Guid>(matching);
        foreach (var id in matching)
            for (var p = byId[id].ParentId; p is Guid parent && byId.ContainsKey(parent) && keep.Add(parent);)
                p = byId[parent].ParentId;

        var view = s.WithModel(model with
        {
            Layers = [.. model.Layers.Where(l => keep.Contains(l.Id))],
            Objects = [.. model.Objects.Where(o => matching.Contains(o.LayerId))],
        });
        filtered[key] = view;
        return view;
    }

    /// <summary>Drops a cache entry. Never disposes: published data may still use the snapshot.</summary>
    private void Forget(string key)
    {
        if (!cache.Remove(key, out var s)) return;
        foreach (var k in filtered.Keys.Where(k => ReferenceEquals(k.Item1, s)).ToList())
            filtered.Remove(k);
    }

    private void ForgetAll()
    {
        cache.Clear();
        filtered.Clear();
    }

    private void Watch(bool on)
    {
        if (on == watching) return;
        watching = on;
        if (on)
        {
            DocumentChanges.Start();
            DocumentChanges.Changed += Edited;
            RhinoDoc.EndSaveDocument += OnSaved;
            RhinoDoc.EndOpenDocument += OnSwitched;
            RhinoDoc.NewDocument += OnSwitched;
            RhinoDoc.CloseDocument += OnSwitched;
            RhinoDoc.ActiveDocumentChanged += OnSwitched;
        }
        else
        {
            DocumentChanges.Changed -= Edited;
            RhinoDoc.EndSaveDocument -= OnSaved;
            RhinoDoc.EndOpenDocument -= OnSwitched;
            RhinoDoc.NewDocument -= OnSwitched;
            RhinoDoc.CloseDocument -= OnSwitched;
            RhinoDoc.ActiveDocumentChanged -= OnSwitched;
        }
    }

    /// <summary>A change a snapshot would see expires a live snapshot of that document; the cache key has moved on already.</summary>
    private void Edited(RhinoDoc doc)
    {
        if (doc.RuntimeSerialNumber == liveDoc) Refresh();
    }

    /// <summary>Saving changes the file a last-saved snapshot reads (its cache key follows the file version).</summary>
    private void OnSaved(object? sender, DocumentSaveEventArgs e)
    {
        if (e.Document is { } doc && doc.RuntimeSerialNumber == savedDoc) Refresh();
    }

    /// <summary>Another document may now be the active one: both kinds of document snapshot look again.</summary>
    private void OnSwitched(object? sender, EventArgs e) => Refresh();

    /// <summary>
    /// Expires this component once edits pause, on the UI thread, rather than on every event. The cache keys decide
    /// what is captured again: a live one by the document's change count, a file one by the file's version.
    /// </summary>
    private void Refresh() => RhinoApp.InvokeOnUiThread(new Action(() =>
    {
        if (refreshScheduled || Locked || OnPingDocument() is not { } doc) return;
        refreshScheduled = true;
        doc.ScheduleSolution(400, _ =>
        {
            refreshScheduled = false;
            ExpireSolution(false);
        });
    }));

    public override void RemovedFromDocument(GH_Document document)
    {
        Watch(false);
        ForgetAll();
        base.RemovedFromDocument(document);
    }

    public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
    {
        switch (context)
        {
            case GH_DocumentContext.Close:
                Watch(false);
                ForgetAll();
                break;
            case GH_DocumentContext.Unloaded:
                Watch(false);
                break;
            case GH_DocumentContext.Loaded when liveDoc is not null || savedDoc is not null:
                // Edits made while this definition was in the background weren't seen.
                Watch(true);
                Refresh();
                break;
        }
        base.DocumentContextChanged(document, context);
    }
}
