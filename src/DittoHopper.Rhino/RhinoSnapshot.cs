using DittoHopper.Core;
using Rhino.DocObjects;
using Rhino.FileIO;
using Rhino.Geometry;
using RhinoDoc = Rhino.RhinoDoc;

namespace DittoHopper.Rhino;

/// <summary>
/// A Core <see cref="ModelSnapshot"/> of a .3dm file or a live document, plus the geometry it was taken from,
/// which <see cref="RhinoGeometryService"/> needs for the geometry ladder. Call from a thread that may use
/// RhinoCommon; hashing runs in parallel internally.
/// <para>
/// The snapshot owns copies of the geometry. Dispose it only when nothing else can still use it; otherwise drop
/// it and the geometry's finalizers release it (the Grasshopper components do this, since their outputs may
/// outlive their caches). <see cref="Model"/> stays usable after disposal, since it is plain data; diffing with
/// the geometry ladder needs both snapshots undisposed.
/// </para>
/// </summary>
public sealed class RhinoSnapshot : IDisposable
{
    internal readonly record struct Entry(GeometryBase Geometry, GeometrySignature Signature);

    private readonly IReadOnlyDictionary<Guid, Entry> geometry;
    /// <summary>Set on a <see cref="WithModel"/> view: the snapshot that owns the shared geometry.</summary>
    private readonly RhinoSnapshot? owner;
    private bool disposed;

    private readonly IReadOnlyDictionary<Guid, IReadOnlyList<GeometryBase>> definitions;

    internal RhinoSnapshot(ModelSnapshot model, IReadOnlyDictionary<Guid, Entry> geometry, HashNames names,
        IReadOnlyDictionary<Guid, IReadOnlyList<GeometryBase>> definitions, RhinoSnapshot? owner = null)
    {
        Model = model;
        this.geometry = geometry;
        Names = names;
        this.definitions = definitions;
        this.owner = owner;
    }

    public ModelSnapshot Model { get; }

    /// <summary>The runtime serial number of the document this is a live snapshot of; null for a file.</summary>
    public uint? DocumentSerialNumber { get; private set; }

    /// <summary>
    /// For a live snapshot, <see cref="DocumentChanges.Count"/> of its document when it was taken: when the count has
    /// moved since, the document has changed and the snapshot is out of date. Null for a file.
    /// </summary>
    public long? DocumentVersion { get; private set; }

    /// <summary>Owned copies of the top-level geometry, by object id.</summary>
    internal IReadOnlyDictionary<Guid, Entry> Geometry
    {
        get
        {
            ObjectDisposedException.ThrowIf((owner ?? this).disposed, this);
            return geometry;
        }
    }

    /// <summary>Releases the geometry copies. The model data stays readable.</summary>
    public void Dispose()
    {
        if (owner is not null || disposed) return;
        disposed = true;
        foreach (var e in geometry.Values) e.Geometry.Dispose();
        foreach (var g in definitions.Values.SelectMany(d => d)) g.Dispose();
    }

    internal HashNames Names { get; }

    /// <summary>The snapshot's own copy of a top-level object's geometry, or null. Shared: duplicate it before changing it.</summary>
    public GeometryBase? GeometryOf(Guid id) => Geometry.TryGetValue(id, out var e) ? e.Geometry : null;

    /// <summary>
    /// A view of this snapshot with a narrowed model, such as one filtered to some layers. <paramref name="model"/>
    /// must hold a subset of this snapshot's records. The view shares the geometry and owns none of it: disposing
    /// the view does nothing, and it is unusable once this snapshot is disposed.
    /// </summary>
    public RhinoSnapshot WithModel(ModelSnapshot model) =>
        new(model, Geometry, Names, definitions, owner ?? this) { DocumentSerialNumber = DocumentSerialNumber, DocumentVersion = DocumentVersion };

    /// <summary>
    /// A block definition's member geometry, in definition space (empty for a linked or unknown definition). Nested
    /// instances stay <see cref="InstanceReferenceGeometry"/>. Shared: duplicate it before changing it.
    /// </summary>
    public IReadOnlyList<GeometryBase> DefinitionGeometry(Guid definitionId)
    {
        ObjectDisposedException.ThrowIf((owner ?? this).disposed, this);
        return definitions.TryGetValue(definitionId, out var members) ? members : [];
    }

    /// <summary>Reads a .3dm file without opening it in Rhino. The source label is the file name.</summary>
    public static RhinoSnapshot FromFile(string path)
    {
        using var file = File3dm.Read(path) ?? throw new IOException($"Could not read '{path}' as a Rhino file.");
        return FromFile(file, Path.GetFileName(path));
    }

    /// <summary>Snapshots an open <see cref="File3dm"/>. The file can be disposed afterwards.</summary>
    public static RhinoSnapshot FromFile(File3dm file, string label)
    {
        var src = new ModelSource
        {
            Label = label,
            Units = file.Settings.ModelUnitSystem,
            Tolerance = file.Settings.ModelAbsoluteTolerance,
            AngleTolerance = file.Settings.ModelAngleToleranceRadians,
        };
        // File3dm tables: enumerate with foreach; their IList indexers and CopyTo return nulls (RhinoCommon 8.36).
        foreach (var l in file.AllLayers) src.Layers.Add(l);
        foreach (var m in file.AllMaterials) src.Materials.Add(m);
        foreach (var l in file.AllLinetypes) src.Linetypes.Add(l);
        foreach (var g in file.AllGroups) src.Groups.Add(g);
        foreach (var p in file.AllHatchPatterns) src.HatchPatterns[p.Index] = GeometryHasher.PatternKey(p);
        foreach (var d in file.AllDimStyles) src.DimStyles[d.Id] = d.Name ?? "";

        var members = new Dictionary<Guid, SourceObject>();
        foreach (var o in file.Objects)
        {
            var item = new SourceObject(o.Id, o.Geometry, o.Attributes);
            // Block member geometry is in the object table too, flagged as instance definition objects.
            if (o.Attributes.IsInstanceDefinitionObject) members[o.Id] = item;
            else src.Objects.Add(item);
        }
        foreach (var d in file.AllInstanceDefinitions)
        {
            bool linked = !string.IsNullOrEmpty(d.SourceArchive);
            var parts = linked ? [] : d.GetObjectIds().Where(members.ContainsKey).Select(id => members[id]).ToList();
            src.Blocks.Add(new SourceBlock(d.Id, d.Name ?? "", linked, parts));
        }
        return SnapshotReader.Read(src);
    }

    /// <summary>
    /// Snapshots a live document, including hidden and locked objects. Worksession reference objects and
    /// layers are skipped and noted in <see cref="SnapshotInfo.Skipped"/>.
    /// </summary>
    public static RhinoSnapshot FromDocument(RhinoDoc doc, string? label = null)
    {
        DocumentChanges.Start();
        long version = DocumentChanges.Count(doc);
        DocumentChanges.Seen(doc);
        var src = new ModelSource
        {
            Label = label ?? (string.IsNullOrEmpty(doc.Name) ? "Untitled" : doc.Name),
            Units = doc.ModelUnitSystem,
            Tolerance = doc.ModelAbsoluteTolerance,
            AngleTolerance = doc.ModelAngleToleranceRadians,
            CurrentLayer = doc.Layers.CurrentLayer?.Id,
        };
        int referenceLayers = 0, referenceObjects = 0;
        foreach (var l in doc.Layers)
        {
            if (l.IsDeleted) continue;
            if (l.IsReference) referenceLayers++;
            else src.Layers.Add(l);
        }
        foreach (var m in doc.Materials) if (!m.IsDeleted) src.Materials.Add(m);
        foreach (var l in doc.Linetypes) if (!l.IsDeleted) src.Linetypes.Add(l);
        foreach (var g in doc.Groups) if (!g.IsDeleted) src.Groups.Add(g);
        foreach (var p in doc.HatchPatterns) if (!p.IsDeleted) src.HatchPatterns[p.Index] = GeometryHasher.PatternKey(p);
        foreach (var d in doc.DimStyles) if (!d.IsDeleted) src.DimStyles[d.Id] = d.Name ?? "";
        foreach (var d in doc.InstanceDefinitions)
        {
            if (d is null || d.IsDeleted) continue;
            bool linked = d.UpdateType is InstanceDefinitionUpdateType.Linked or InstanceDefinitionUpdateType.LinkedAndEmbedded || d.IsReference;
            var parts = linked ? [] : d.GetObjects().Select(o => new SourceObject(o.Id, o.Geometry, o.Attributes)).ToList();
            src.Blocks.Add(new SourceBlock(d.Id, d.Name ?? "", linked, parts));
        }
        var all = new ObjectEnumeratorSettings
        {
            NormalObjects = true, LockedObjects = true, HiddenObjects = true, IncludeLights = true,
            ReferenceObjects = true, DeletedObjects = false, IncludeGrips = false, IncludePhantoms = false, IdefObjects = false,
        };
        foreach (var o in doc.Objects.GetObjectList(all))
        {
            if (o.IsReference) referenceObjects++;
            else src.Objects.Add(new SourceObject(o.Id, o.Geometry, o.Attributes));
        }
        if (referenceObjects > 0) src.Skipped.Add($"{referenceObjects} worksession reference objects");
        if (referenceLayers > 0) src.Skipped.Add($"{referenceLayers} worksession reference layers");
        var snapshot = SnapshotReader.Read(src);
        snapshot.DocumentSerialNumber = doc.RuntimeSerialNumber;
        snapshot.DocumentVersion = version;
        return snapshot;
    }

    /// <summary>
    /// "What did I change this session?": A is the document's file as last saved, B is the document now.
    /// Throws when the document has never been saved, since there is no last save to compare with. The caller
    /// owns (and disposes) both snapshots.
    /// </summary>
    public static (RhinoSnapshot LastSave, RhinoSnapshot Current) DocumentVsLastSave(RhinoDoc doc)
    {
        string? path = doc.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException(
                "This document has not been saved yet, so there is no last save to compare it with. Save it once, then compare.");
        string name = Path.GetFileName(path);
        using var file = File3dm.Read(path) ?? throw new IOException($"Could not read the last save, '{path}'.");
        var lastSave = FromFile(file, name + " (last save)");
        try
        {
            return (lastSave, FromDocument(doc, name + " (open)"));
        }
        catch
        {
            lastSave.Dispose();
            throw;
        }
    }
}

/// <summary>Diffs two Rhino snapshots with the geometry ladder enabled.</summary>
public static class RhinoDiff
{
    public static DiffResult Diff(RhinoSnapshot a, RhinoSnapshot b, DiffSettings? settings = null) =>
        ModelDiff.Diff(a.Model, b.Model, settings, new RhinoGeometryService(a, b));
}
