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
/// </summary>
public sealed class RhinoSnapshot
{
    internal readonly record struct Entry(GeometryBase Geometry, ulong Hash);

    internal RhinoSnapshot(ModelSnapshot model, IReadOnlyDictionary<Guid, Entry> geometry, HashNames names)
    {
        Model = model;
        Geometry = geometry;
        Names = names;
    }

    public ModelSnapshot Model { get; }

    /// <summary>Owned copies of the top-level geometry, by object id.</summary>
    internal IReadOnlyDictionary<Guid, Entry> Geometry { get; }

    internal HashNames Names { get; }

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
        };
        // File3dm tables: enumerate with foreach; their IList indexers and CopyTo return nulls (RhinoCommon 8.36).
        foreach (var l in file.AllLayers) src.Layers.Add(l);
        foreach (var m in file.AllMaterials) src.Materials.Add(m);
        foreach (var l in file.AllLinetypes) src.Linetypes.Add(l);
        foreach (var g in file.AllGroups) src.Groups.Add(g);
        foreach (var p in file.AllHatchPatterns) src.HatchPatterns[p.Index] = p.Name ?? "";
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
        var src = new ModelSource
        {
            Label = label ?? (string.IsNullOrEmpty(doc.Name) ? "Untitled" : doc.Name),
            Units = doc.ModelUnitSystem,
            Tolerance = doc.ModelAbsoluteTolerance,
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
        foreach (var p in doc.HatchPatterns) if (!p.IsDeleted) src.HatchPatterns[p.Index] = p.Name ?? "";
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
        return SnapshotReader.Read(src);
    }

    /// <summary>
    /// "What did I change this session?": A is the document's file as last saved, B is the document now.
    /// Throws when the document has never been saved, since there is no last save to compare with.
    /// </summary>
    public static (RhinoSnapshot LastSave, RhinoSnapshot Current) DocumentVsLastSave(RhinoDoc doc)
    {
        string? path = doc.Path;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            throw new InvalidOperationException(
                "This document has not been saved yet, so there is no last save to compare it with. Save it once, then compare.");
        string name = Path.GetFileName(path);
        using var file = File3dm.Read(path) ?? throw new IOException($"Could not read the last save, '{path}'.");
        return (FromFile(file, name + " (last save)"), FromDocument(doc, name + " (open)"));
    }
}

/// <summary>Diffs two Rhino snapshots with the geometry ladder enabled.</summary>
public static class RhinoDiff
{
    public static DiffResult Diff(RhinoSnapshot a, RhinoSnapshot b, DiffSettings? settings = null) =>
        ModelDiff.Diff(a.Model, b.Model, settings, new RhinoGeometryService(a, b));
}
