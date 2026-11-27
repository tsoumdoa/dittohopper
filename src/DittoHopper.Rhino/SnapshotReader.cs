using System.Collections.Specialized;
using DittoHopper.Core;
using Rhino.DocObjects;
using Rhino.Geometry;
using UnitSystem = DittoHopper.Core.UnitSystem;

namespace DittoHopper.Rhino;

/// <summary>One object as read from a file or document. Geometry is null when the object has none.</summary>
internal sealed record SourceObject(Guid Id, GeometryBase? Geometry, ObjectAttributes Attributes);

/// <summary>A block definition and its member objects. Linked definitions keep their name but not their content.</summary>
internal sealed record SourceBlock(Guid Id, string Name, bool Linked, IReadOnlyList<SourceObject> Members);

/// <summary>What a File3dm or a RhinoDoc holds, in RhinoCommon types, gathered on the reading thread.</summary>
internal sealed class ModelSource
{
    public required string Label { get; init; }
    public required global::Rhino.UnitSystem Units { get; init; }
    public required double Tolerance { get; init; }
    public Guid? CurrentLayer { get; init; }
    public List<Layer> Layers { get; } = [];
    public List<Material> Materials { get; } = [];
    public List<Linetype> Linetypes { get; } = [];
    public List<Group> Groups { get; } = [];
    public List<SourceBlock> Blocks { get; } = [];
    public List<SourceObject> Objects { get; } = [];
    public Dictionary<int, string> HatchPatterns { get; } = [];
    public Dictionary<Guid, string> DimStyles { get; } = [];
    public List<string> Skipped { get; } = [];
}

/// <summary>
/// Turns a <see cref="ModelSource"/> into Core records. Every object is hashed once, in parallel; top-level
/// geometry is duplicated so the snapshot owns it after the file closes or the document changes.
/// </summary>
internal static class SnapshotReader
{
    public static RhinoSnapshot Read(ModelSource src)
    {
        if (!(src.Tolerance > 0 && double.IsFinite(src.Tolerance)))
            throw new InvalidDataException($"'{src.Label}' has no usable absolute tolerance ({src.Tolerance}).");
        double grid = src.Tolerance;
        var layerIds = IndexToId(src.Layers);
        var materialIds = IndexToId(src.Materials);
        var linetypeIds = IndexToId(src.Linetypes);
        var groupIds = IndexToId(src.Groups);
        var names = new HashNames(src.HatchPatterns, src.DimStyles, src.Blocks.ToDictionary(b => b.Id, b => b.Name));

        var objects = src.Objects.Where(o => o.Geometry is not null).ToList();
        if (objects.Count < src.Objects.Count)
            src.Skipped.Add($"{src.Objects.Count - objects.Count} objects without geometry");
        var owned = objects.Select(o => Owned(o.Geometry!)).ToArray();
        var signatures = new GeometrySignature[owned.Length];
        Parallel.For(0, owned.Length, i => signatures[i] = GeometryHasher.Signature(owned[i], GeometryHasher.Hash(owned[i], grid, names)));

        var records = objects.Select((o, i) => ObjectRecord(o, owned[i], signatures[i], layerIds, materialIds, linetypeIds, groupIds)).ToList();
        var geometry = new Dictionary<Guid, RhinoSnapshot.Entry>(objects.Count);
        for (int i = 0; i < objects.Count; i++)
            geometry[objects[i].Id] = new RhinoSnapshot.Entry(owned[i], signatures[i].Hash);

        var blocks = Blocks(src, grid, names); // may add to Skipped
        var model = new ModelSnapshot
        {
            Info = new SnapshotInfo
            {
                Source = src.Label,
                CapturedAt = DateTimeOffset.UtcNow,
                Units = ToCore(src.Units),
                AbsoluteTolerance = src.Tolerance,
                HashGrid = grid,
                Skipped = [.. src.Skipped],
            },
            Layers = [.. src.Layers.Select(l => Layer(l, materialIds, linetypeIds))],
            Blocks = blocks,
            Materials = [.. src.Materials.Select(m => new TableRecord { Id = m.Id, Name = m.Name ?? "", ContentHash = MaterialHash(m) })],
            Linetypes = [.. src.Linetypes.Select(l => new TableRecord { Id = l.Id, Name = l.Name ?? "", ContentHash = LinetypeHash(l) })],
            Groups = [.. src.Groups.Select(g => new GroupRecord { Id = g.Id, Name = string.IsNullOrEmpty(g.Name) ? null : g.Name })],
            Objects = records,
            CurrentLayerId = src.CurrentLayer,
        };
        return new RhinoSnapshot(model, geometry, names);
    }

    private static Dictionary<int, Guid> IndexToId(IEnumerable<ModelComponent> items)
    {
        var map = new Dictionary<int, Guid>();
        foreach (var item in items) map[item.Index] = item.Id;
        return map;
    }

    private static Guid Lookup(Dictionary<int, Guid> ids, int index) => ids.GetValueOrDefault(index);

    /// <summary>
    /// A copy the snapshot owns. A bare surface becomes the one-face Brep a Rhino document stores it as, so a
    /// file written by a script and the same model opened in Rhino snapshot alike.
    /// </summary>
    private static GeometryBase Owned(GeometryBase g) => g is Surface s and not Extrusion ? s.ToBrep() ?? s.Duplicate() : g.Duplicate();

    private static ulong HashOwned(GeometryBase g, double grid, HashNames names)
    {
        using var owned = Owned(g);
        return GeometryHasher.Hash(owned, grid, names);
    }

    private static Dictionary<int, string> Names(IEnumerable<ModelComponent> items)
    {
        var map = new Dictionary<int, string>();
        foreach (var item in items) map[item.Index] = item.Name ?? "";
        return map;
    }

    private static LayerRecord Layer(Layer l, Dictionary<int, Guid> materials, Dictionary<int, Guid> linetypes) => new()
    {
        Id = l.Id,
        Name = l.Name ?? "",
        ParentId = l.ParentLayerId == Guid.Empty ? null : l.ParentLayerId,
        Color = l.Color.ToArgb(),
        LinetypeId = Lookup(linetypes, l.LinetypeIndex),
        MaterialId = Lookup(materials, l.RenderMaterialIndex),
        PrintWidth = l.PlotWeight,
        Visible = l.IsVisible,
        Locked = l.IsLocked,
    };

    private static ObjectRecord ObjectRecord(SourceObject o, GeometryBase g, GeometrySignature signature, Dictionary<int, Guid> layers,
        Dictionary<int, Guid> materials, Dictionary<int, Guid> linetypes, Dictionary<int, Guid> groups)
    {
        var a = o.Attributes;
        var instance = g as InstanceReferenceGeometry;
        return new ObjectRecord
        {
            Id = o.Id,
            Kind = Kind(g),
            LayerId = Lookup(layers, a.LayerIndex),
            Name = string.IsNullOrEmpty(a.Name) ? null : a.Name,
            Color = new Sourced<int>(a.ObjectColor.ToArgb(), Source(a.ColorSource)),
            Material = new Sourced<Guid>(Lookup(materials, a.MaterialIndex), Source(a.MaterialSource)),
            Linetype = new Sourced<Guid>(Lookup(linetypes, a.LinetypeIndex), Source(a.LinetypeSource)),
            PrintWidth = new Sourced<double>(a.PlotWeight, Source(a.PlotWeightSource)),
            DisplayOrder = a.DisplayOrder,
            GroupIds = [.. (a.GetGroupList() ?? []).Select(i => Lookup(groups, i))],
            UserText = UserText(a.GetUserStrings()),
            BlockDefinitionId = instance?.ParentIdefId,
            InstanceXform = instance is null ? null : Xform(instance.Xform),
            Geometry = signature,
        };
    }

    /// <summary>
    /// A definition's content hash covers each member's geometry and attributes, in any order. Tables are
    /// referenced by name and a member's layer is left out: ids differ between files and a layer rename would
    /// otherwise change every definition with members on it. A nested
    /// instance contributes only its placement (transform and attributes), computed once and also recorded in
    /// <see cref="BlockRecord.Nested"/>.
    /// </summary>
    private static List<BlockRecord> Blocks(ModelSource src, double grid, HashNames names)
    {
        var members = src.Blocks.Where(b => !b.Linked).SelectMany(b => b.Members.Where(m => m.Geometry is not null)).ToArray();
        var hashes = new ulong[members.Length];
        var (materials, linetypes) = (Names(src.Materials), Names(src.Linetypes));
        Parallel.For(0, members.Length, i =>
        {
            var m = members[i];
            ulong geometry = m.Geometry is InstanceReferenceGeometry nested
                ? GeometryHasher.NestedPlacement(nested, grid)
                : HashOwned(m.Geometry!, grid, names);
            hashes[i] = MemberHash(geometry, m.Attributes, grid, materials, linetypes);
        });
        var byMember = new Dictionary<SourceObject, ulong>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < members.Length; i++) byMember[members[i]] = hashes[i];

        var blocks = new List<BlockRecord>();
        foreach (var b in src.Blocks)
        {
            if (b.Linked)
            {
                src.Skipped.Add($"linked block definition '{b.Name}': its content is not compared");
                blocks.Add(new BlockRecord { Id = b.Id, Name = b.Name });
                continue;
            }
            var content = b.Members.Where(byMember.ContainsKey).Select(m => byMember[m]).Order().ToList();
            var h = new CanonicalHash("block", grid).Add((long)content.Count);
            foreach (ulong m in content) h.Add(unchecked((long)m));
            blocks.Add(new BlockRecord
            {
                Id = b.Id,
                Name = b.Name,
                ContentHash = h.Finish(),
                Nested = [.. b.Members.Where(m => m.Geometry is InstanceReferenceGeometry)
                    .Select(m => new NestedBlock(((InstanceReferenceGeometry)m.Geometry!).ParentIdefId, byMember[m]))],
            });
        }
        return blocks;
    }

    /// <summary>Member geometry plus the attributes that change how it looks or what it says.</summary>
    private static ulong MemberHash(ulong geometry, ObjectAttributes a, double grid, Dictionary<int, string> materials,
        Dictionary<int, string> linetypes)
    {
        var h = new CanonicalHash("member", grid).Add(unchecked((long)geometry))
            .Add((long)Source(a.ColorSource)).Add(a.ColorSource == ObjectColorSource.ColorFromObject ? a.ObjectColor.ToArgb() : 0L)
            .Add((long)Source(a.MaterialSource)).Add(a.MaterialSource == ObjectMaterialSource.MaterialFromObject ? materials.GetValueOrDefault(a.MaterialIndex) : null)
            .Add((long)Source(a.LinetypeSource)).Add(a.LinetypeSource == ObjectLinetypeSource.LinetypeFromObject ? linetypes.GetValueOrDefault(a.LinetypeIndex) : null)
            .Add((long)Source(a.PlotWeightSource)).Add(a.PlotWeightSource == ObjectPlotWeightSource.PlotWeightFromObject ? GeometryHasher.Exact(a.PlotWeight) : 0)
            .Add(string.IsNullOrEmpty(a.Name) ? null : a.Name);
        foreach (var (k, v) in UserText(a.GetUserStrings()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
            h.Add(k).Add(v);
        return h.Finish();
    }

    private static ulong MaterialHash(Material m)
    {
        var h = new CanonicalHash("material", 1);
        foreach (var c in new[] { m.DiffuseColor, m.AmbientColor, m.EmissionColor, m.SpecularColor, m.ReflectionColor, m.TransparentColor })
            h.Add((long)c.ToArgb());
        foreach (double v in new[] { m.Shine, m.Transparency, m.Reflectivity, m.IndexOfRefraction, m.FresnelIndexOfRefraction,
                     m.ReflectionGlossiness, m.RefractionGlossiness })
            h.Add(GeometryHasher.Exact(v));
        h.Add(m.FresnelReflections ? 1L : 0L).Add(m.DisableLighting ? 1L : 0L).Add(m.AlphaTransparency ? 1L : 0L);
        h.Add(m.IsPhysicallyBased ? 1L : 0L);
        if (m.IsPhysicallyBased && m.PhysicallyBased is { } pbr)
        {
            var c = pbr.BaseColor;
            foreach (double v in new double[] { c.R, c.G, c.B, c.A, pbr.Metallic, pbr.Roughness, pbr.Opacity, pbr.Alpha })
                h.Add(GeometryHasher.Exact(v));
        }
        // Texture file names, not paths: the same texture sits in different folders on different machines.
        foreach (var t in m.GetTextures() ?? [])
            h.Add(Path.GetFileName(t.FileName ?? ""));
        return h.Finish();
    }

    private static ulong LinetypeHash(Linetype l)
    {
        var h = new CanonicalHash("linetype", 1).Add((long)l.SegmentCount);
        for (int i = 0; i < l.SegmentCount; i++)
        {
            l.GetSegment(i, out double length, out bool solid);
            h.Add(GeometryHasher.Exact(length)).Add(solid ? 1L : 0L);
        }
        h.Add(GeometryHasher.Exact(l.Width)).Add((long)l.WidthUnits).Add((long)l.LineCapStyle).Add((long)l.LineJoinStyle);
        foreach (var p in l.GetTaperPoints() ?? [])
            h.Add(GeometryHasher.Exact(p.X)).Add(GeometryHasher.Exact(p.Y));
        return h.Finish();
    }


    private static Dictionary<string, string> UserText(NameValueCollection? strings)
    {
        var text = new Dictionary<string, string>(StringComparer.Ordinal);
        if (strings is null) return text;
        foreach (string? key in strings.AllKeys)
            if (key is not null && strings[key] is string value) text[key] = value;
        return text;
    }

    private static Xform Xform(Transform x) =>
        new([.. Enumerable.Range(0, 16).Select(i => x[i / 4, i % 4])]);

    private static ObjectKind Kind(GeometryBase g) => g switch
    {
        InstanceReferenceGeometry => ObjectKind.InstanceReference,
        Point => ObjectKind.Point,
        PointCloud => ObjectKind.PointCloud,
        Curve => ObjectKind.Curve,
        Brep => ObjectKind.Brep,
        Extrusion => ObjectKind.Extrusion,
        Surface => ObjectKind.Surface,
        Mesh => ObjectKind.Mesh,
        SubD => ObjectKind.SubD,
        AnnotationBase => ObjectKind.Annotation,
        Hatch => ObjectKind.Hatch,
        TextDot => ObjectKind.TextDot,
        global::Rhino.Geometry.Light => ObjectKind.Light,
        _ => ObjectKind.Other,
    };

    private static ValueSource Source(ObjectColorSource s) => s switch
    {
        ObjectColorSource.ColorFromObject => ValueSource.ByObject,
        ObjectColorSource.ColorFromMaterial => ValueSource.ByMaterial,
        ObjectColorSource.ColorFromParent => ValueSource.ByParent,
        _ => ValueSource.ByLayer,
    };

    private static ValueSource Source(ObjectMaterialSource s) => s switch
    {
        ObjectMaterialSource.MaterialFromObject => ValueSource.ByObject,
        ObjectMaterialSource.MaterialFromParent => ValueSource.ByParent,
        _ => ValueSource.ByLayer,
    };

    private static ValueSource Source(ObjectLinetypeSource s) => s switch
    {
        ObjectLinetypeSource.LinetypeFromObject => ValueSource.ByObject,
        ObjectLinetypeSource.LinetypeFromParent => ValueSource.ByParent,
        _ => ValueSource.ByLayer,
    };

    private static ValueSource Source(ObjectPlotWeightSource s) => s switch
    {
        ObjectPlotWeightSource.PlotWeightFromObject => ValueSource.ByObject,
        ObjectPlotWeightSource.PlotWeightFromParent => ValueSource.ByParent,
        _ => ValueSource.ByLayer,
    };

    public static UnitSystem ToCore(global::Rhino.UnitSystem u) => u switch
    {
        global::Rhino.UnitSystem.None => UnitSystem.None,
        global::Rhino.UnitSystem.Microns => UnitSystem.Microns,
        global::Rhino.UnitSystem.Millimeters => UnitSystem.Millimeters,
        global::Rhino.UnitSystem.Centimeters => UnitSystem.Centimeters,
        global::Rhino.UnitSystem.Meters => UnitSystem.Meters,
        global::Rhino.UnitSystem.Kilometers => UnitSystem.Kilometers,
        global::Rhino.UnitSystem.Inches => UnitSystem.Inches,
        global::Rhino.UnitSystem.Feet => UnitSystem.Feet,
        global::Rhino.UnitSystem.Yards => UnitSystem.Yards,
        global::Rhino.UnitSystem.Miles => UnitSystem.Miles,
        _ => UnitSystem.Other,
    };
}
