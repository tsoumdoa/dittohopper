using DittoHopper.Core;
using DittoHopper.Rhino;
using Grasshopper.Kernel;
using Rhino;
using Rhino.FileIO;

namespace DittoHopper.Grasshopper;

/// <summary>
/// Captures one model: a .3dm file, the active document as it is now, or the active document as last saved.
/// File reads are cached per file version (path, size, last write time), so re-solving never re-reads an
/// unchanged file. The active document is watched and re-captured shortly after it changes.
/// </summary>
public sealed class SnapshotComponent : DittoComponent
{
    private readonly Dictionary<string, RhinoSnapshot> cache = [];
    private readonly Dictionary<(RhinoSnapshot, string), RhinoSnapshot> filtered = [];
    private readonly HashSet<string> used = [];
    private readonly HashSet<(RhinoSnapshot, string)> usedViews = [];
    private bool watchDocument, watching, refreshScheduled;
    private int documentGeneration;

    public SnapshotComponent()
        : base("Snapshot", "Snapshot",
            "Captures a Rhino model for Model Diff: a .3dm file (read without opening it), or the active document. " +
            "File reads are cached until the file changes. The active document updates itself when you edit it.",
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
            "Only objects on these layers, e.g. Structure::* (the whole Structure tree). * matches any text. Empty: every layer. " +
            "Filter both sides the same way: an object that left the filtered layers shows as removed.",
            GH_ParamAccess.list);
        p[1].Optional = true;
        p.AddBooleanParameter("Saved", "Saved",
            "Only when Source is empty. True: the active document as last saved on disk. False: the active document as it is now. " +
            "Diff a Saved snapshot against a live one to see what you changed this session.",
            GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddGenericParameter("Snap", "Snap", "The model snapshot. Connect to A or B of Model Diff.", GH_ParamAccess.item);

    protected override void BeforeSolveInstance()
    {
        used.Clear();
        usedViews.Clear();
        watchDocument = false;
        Message = null;
        base.BeforeSolveInstance();
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        string? source = null;
        da.GetData(0, ref source);
        var glob = new LayerGlob(Texts(da, 1));
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
        // Snapshots own native geometry copies: release the ones this solution no longer uses.
        foreach (var key in cache.Keys.Where(k => !used.Contains(k)).ToList())
            Release(key);
        foreach (var key in filtered.Keys.Where(k => !usedViews.Contains(k)).ToList())
            filtered.Remove(key);
        Watch(watchDocument);
        base.AfterSolveInstance();
    }

    private RhinoSnapshot? FromDocument(bool saved)
    {
        watchDocument = true;
        var doc = OnPingDocument()?.RhinoDocument ?? RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "There is no active Rhino document.");
            return null;
        }
        if (!saved)
        {
            Message = "Live";
            return Cached($"doc:{doc.RuntimeSerialNumber}:{documentGeneration}", () => RhinoSnapshot.FromDocument(doc));
        }
        string? path = doc.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException(
                "This document has not been saved yet, so there is no last save to compare with. Save it once, then try again.");
        Message = "Last save";
        return FromFile(path, Path.GetFileName(path) + " (last save)");
    }

    private RhinoSnapshot FromFile(string path, string? label = null)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException($"File not found: {path}");
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
    private RhinoSnapshot Filter(RhinoSnapshot s, LayerGlob glob)
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

    private void Release(string key)
    {
        if (!cache.Remove(key, out var s)) return;
        foreach (var k in filtered.Keys.Where(k => ReferenceEquals(k.Item1, s)).ToList())
            filtered.Remove(k);
        s.Dispose(); // snapshots own native geometry copies
    }

    private void ReleaseAll()
    {
        foreach (var key in cache.Keys.ToList()) Release(key);
        filtered.Clear();
    }

    private void Watch(bool on)
    {
        if (on == watching) return;
        watching = on;
        if (on)
        {
            RhinoDoc.AddRhinoObject += OnObjectEvent;
            RhinoDoc.DeleteRhinoObject += OnObjectEvent;
            RhinoDoc.UndeleteRhinoObject += OnObjectEvent;
            RhinoDoc.ReplaceRhinoObject += OnReplace;
            RhinoDoc.ModifyObjectAttributes += OnAttributes;
            RhinoDoc.LayerTableEvent += OnTableEvent;
            RhinoDoc.InstanceDefinitionTableEvent += OnTableEvent;
            RhinoDoc.MaterialTableEvent += OnTableEvent;
            RhinoDoc.GroupTableEvent += OnTableEvent;
            RhinoDoc.LinetypeTableEvent += OnTableEvent;
            RhinoDoc.DocumentPropertiesChanged += OnDocumentEvent;
            RhinoDoc.EndSaveDocument += OnDocumentEvent;
        }
        else
        {
            RhinoDoc.AddRhinoObject -= OnObjectEvent;
            RhinoDoc.DeleteRhinoObject -= OnObjectEvent;
            RhinoDoc.UndeleteRhinoObject -= OnObjectEvent;
            RhinoDoc.ReplaceRhinoObject -= OnReplace;
            RhinoDoc.ModifyObjectAttributes -= OnAttributes;
            RhinoDoc.LayerTableEvent -= OnTableEvent;
            RhinoDoc.InstanceDefinitionTableEvent -= OnTableEvent;
            RhinoDoc.MaterialTableEvent -= OnTableEvent;
            RhinoDoc.GroupTableEvent -= OnTableEvent;
            RhinoDoc.LinetypeTableEvent -= OnTableEvent;
            RhinoDoc.DocumentPropertiesChanged -= OnDocumentEvent;
            RhinoDoc.EndSaveDocument -= OnDocumentEvent;
        }
    }

    private void OnObjectEvent(object? sender, global::Rhino.DocObjects.RhinoObjectEventArgs e) => Refresh();
    private void OnReplace(object? sender, global::Rhino.DocObjects.RhinoReplaceObjectEventArgs e) => Refresh();
    private void OnAttributes(object? sender, global::Rhino.DocObjects.RhinoModifyObjectAttributesEventArgs e) => Refresh();
    private void OnTableEvent<T>(object? sender, T e) => Refresh();
    private void OnDocumentEvent(object? sender, DocumentEventArgs e) => Refresh();

    /// <summary>Re-captures the active document once edits pause, rather than on every event.</summary>
    private void Refresh()
    {
        documentGeneration++;
        if (refreshScheduled || Locked || OnPingDocument() is not { } doc) return;
        refreshScheduled = true;
        doc.ScheduleSolution(400, _ =>
        {
            refreshScheduled = false;
            ExpireSolution(false);
        });
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        Watch(false);
        ReleaseAll();
        base.RemovedFromDocument(document);
    }

    public override void DocumentContextChanged(GH_Document document, GH_DocumentContext context)
    {
        switch (context)
        {
            case GH_DocumentContext.Close:
                Watch(false);
                ReleaseAll();
                break;
            case GH_DocumentContext.Unloaded:
                Watch(false);
                break;
            case GH_DocumentContext.Loaded when watchDocument:
                // Edits made while this definition was in the background weren't seen.
                Watch(true);
                Refresh();
                break;
        }
        base.DocumentContextChanged(document, context);
    }
}
