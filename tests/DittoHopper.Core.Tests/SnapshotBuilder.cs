namespace DittoHopper.Core.Tests;

/// <summary>
/// Builds in-memory snapshots. Ids are readable (<see cref="Id"/>), hashes and centroids default to unique
/// values per object, and <see cref="Clone"/> starts B from A so a test only states the edit.
/// </summary>
internal sealed class SnapshotBuilder
{
    private int next;

    public SnapshotInfo Info = new() { Source = "A", Units = UnitSystem.Millimeters, AbsoluteTolerance = 0.01, HashGrid = 0.01 };
    public readonly List<LayerRecord> Layers = [];
    public readonly List<BlockRecord> Blocks = [];
    public readonly List<TableRecord> Materials = [];
    public readonly List<TableRecord> Linetypes = [];
    public readonly List<GroupRecord> Groups = [];
    public readonly List<ObjectRecord> Objects = [];
    public Guid? CurrentLayer;

    public SnapshotBuilder(int firstId = 1) => next = firstId;

    public static Guid Id(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:x12}");

    public Guid NewId() => Id(next++);

    /// <summary>B starts as a copy of A, named B, with new ids counting from <paramref name="firstId"/>.</summary>
    public SnapshotBuilder Clone(int firstId = 0x1000)
    {
        var b = new SnapshotBuilder(firstId) { Info = Info with { Source = "B" } };
        b.Layers.AddRange(Layers);
        b.Blocks.AddRange(Blocks);
        b.Materials.AddRange(Materials);
        b.Linetypes.AddRange(Linetypes);
        b.Groups.AddRange(Groups);
        b.Objects.AddRange(Objects);
        return b;
    }

    public ModelSnapshot Build() => new()
    {
        Info = Info,
        Layers = [.. Layers],
        Blocks = [.. Blocks],
        Materials = [.. Materials],
        Linetypes = [.. Linetypes],
        Groups = [.. Groups],
        Objects = [.. Objects],
        CurrentLayerId = CurrentLayer,
    };

    public Guid Layer(string name, Guid? parent = null, int color = unchecked((int)0xFF000000), bool locked = false, bool visible = true)
    {
        var id = NewId();
        Layers.Add(new LayerRecord { Id = id, Name = name, ParentId = parent, Color = color, Locked = locked, Visible = visible });
        return id;
    }

    public Guid Block(string name, ulong content = 1, params Guid[] nested)
    {
        var id = NewId();
        Blocks.Add(new BlockRecord { Id = id, Name = name, ContentHash = content, Nested = [.. nested.Select((d, i) => new NestedBlock(d, (ulong)i))] });
        return id;
    }

    public Guid Group(string? name = null)
    {
        var id = NewId();
        Groups.Add(new GroupRecord { Id = id, Name = name });
        return id;
    }

    /// <summary>A curve with a unique hash and centroid unless given.</summary>
    public Guid Object(Guid layer, Func<ObjectRecord, ObjectRecord>? edit = null, ulong? hash = null, Vec3? at = null)
    {
        var id = NewId();
        int n = next;
        var o = new ObjectRecord
        {
            Id = id,
            Kind = ObjectKind.Curve,
            LayerId = layer,
            Geometry = new GeometrySignature { Hash = hash ?? (ulong)n * 0x9E3779B97F4A7C15UL, Centroid = at ?? new Vec3(n * 100, 0, 0) },
        };
        Objects.Add(edit is null ? o : edit(o));
        return id;
    }

    /// <summary>A box-like solid with a size, so matching passes 4 and 5 have a shape to compare.</summary>
    public Guid Solid(Guid layer, Vec3 at, double size = 100, ObjectKind kind = ObjectKind.Brep, Func<ObjectRecord, ObjectRecord>? edit = null) =>
        Object(layer, o => (edit ?? (x => x))(o with { Kind = kind, Geometry = SolidShape(o.Geometry.Hash, at, size) }));

    public static GeometrySignature SolidShape(ulong hash, Vec3 at, double size)
    {
        var half = new Vec3(size / 2, size / 2, size / 2);
        return new GeometrySignature
        {
            Hash = hash, Area = 6 * size * size, Volume = size * size * size, Centroid = at,
            BBox = new BBox(at - half, at + half), Closed = true, Faces = 6,
        };
    }

    public List<Guid> Many(Guid layer, int count, Func<ObjectRecord, ObjectRecord>? edit = null) =>
        [.. Enumerable.Range(0, count).Select(_ => Object(layer, edit))];

    public Guid Instance(Guid layer, Guid definition, Xform? xform = null) => Object(layer, o => o with
    {
        Kind = ObjectKind.InstanceReference,
        BlockDefinitionId = definition,
        InstanceXform = xform ?? Xform.Identity,
    });

    public void Edit(Guid id, Func<ObjectRecord, ObjectRecord> edit) => Replace(Objects, id, edit, o => o.Id);
    public void EditLayer(Guid id, Func<LayerRecord, LayerRecord> edit) => Replace(Layers, id, edit, l => l.Id);
    public void EditBlock(Guid id, Func<BlockRecord, BlockRecord> edit) => Replace(Blocks, id, edit, b => b.Id);

    public void RemoveLayer(Guid id) => Layers.RemoveAll(l => l.Id == id);
    public void RemoveObject(Guid id) => Objects.RemoveAll(o => o.Id == id);
    public List<Guid> On(Guid layer) => [.. Objects.Where(o => o.LayerId == layer).Select(o => o.Id)];

    public void Move(IEnumerable<Guid> objects, Guid layer)
    {
        foreach (var id in objects.ToList()) Edit(id, o => o with { LayerId = layer });
    }

    /// <summary>Gives the object a new id, as a delete + re-bake does. Returns the new id.</summary>
    public Guid Rebake(Guid id)
    {
        var fresh = NewId();
        Edit(id, o => o with { Id = fresh });
        return fresh;
    }

    private static void Replace<T>(List<T> list, Guid id, Func<T, T> edit, Func<T, Guid> key)
    {
        int i = list.FindIndex(x => key(x) == id);
        if (i < 0) throw new ArgumentException($"No record {id}.");
        list[i] = edit(list[i]);
    }
}
