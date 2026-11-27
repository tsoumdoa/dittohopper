namespace DittoHopper.Core.Tests;

/// <summary>The design doc's test-plan cases that need no real geometry, as in-memory A/B pairs.</summary>
internal static class Fixtures
{
    public const int Red = unchecked((int)0xFFFF0000);

    public static (SnapshotBuilder A, SnapshotBuilder B) LayerRename()
    {
        var a = new SnapshotBuilder();
        var walls = a.Layer("Walls");
        a.Many(walls, 4);
        var b = a.Clone();
        b.EditLayer(walls, l => l with { Name = "A-WALL" });
        return (a, b);
    }

    public static (SnapshotBuilder A, SnapshotBuilder B) LayerReparent()
    {
        var a = new SnapshotBuilder();
        var furniture = a.Layer("Furniture");
        a.Many(furniture, 3);
        var b = a.Clone();
        var interior = b.Layer("Interior");
        b.EditLayer(furniture, l => l with { ParentId = interior });
        return (a, b);
    }

    /// <summary>Slabs → Slabs-L01 (7) + Slabs-L02 (5); the Slabs layer is gone.</summary>
    public static (SnapshotBuilder A, SnapshotBuilder B) LayerSplit()
    {
        var a = new SnapshotBuilder();
        var slabs = a.Layer("Slabs");
        var objects = a.Many(slabs, 12);
        var b = a.Clone();
        b.Move(objects.Take(7), b.Layer("Slabs-L01"));
        b.Move(objects.Skip(7), b.Layer("Slabs-L02"));
        b.RemoveLayer(slabs);
        return (a, b);
    }

    /// <summary>Doors turns red. Its ByLayer objects look different but did not change; the ByObject one is untouched.</summary>
    public static (SnapshotBuilder A, SnapshotBuilder B) ByLayerColor()
    {
        var a = new SnapshotBuilder();
        var doors = a.Layer("Doors");
        a.Many(doors, 3);
        a.Object(doors, o => o with { Color = new Sourced<int>(Red, ValueSource.ByObject) });
        var b = a.Clone();
        b.EditLayer(doors, l => l with { Color = Red });
        return (a, b);
    }

    /// <summary>Every object deleted and baked again from Grasshopper: new ids, same geometry.</summary>
    public static (SnapshotBuilder A, SnapshotBuilder B) GhRebake()
    {
        var a = new SnapshotBuilder();
        var gh = a.Layer("GH");
        var objects = a.Many(gh, 4);
        var b = a.Clone();
        foreach (var id in objects) b.Rebake(id);
        return (a, b);
    }

    public static (SnapshotBuilder A, SnapshotBuilder B) DefaultLayer()
    {
        var a = new SnapshotBuilder();
        var dflt = a.Layer("Default");
        var walls = a.Layer("Walls");
        var objects = a.Many(walls, 25);
        var b = a.Clone();
        b.Move(objects.Take(23), dflt);
        return (a, b);
    }

    public static (SnapshotBuilder A, SnapshotBuilder B) UserText()
    {
        var a = new SnapshotBuilder();
        var panels = a.Layer("Panels", a.Layer("Facade"));
        var panel = a.Object(panels, o => o with
        {
            UserText = new Dictionary<string, string> { ["diff:uid"] = "P-1", ["panel_type"] = "A", ["temp_note"] = "check" },
        });
        var b = a.Clone();
        b.Edit(panel, o => o with
        {
            UserText = new Dictionary<string, string> { ["diff:uid"] = "P-1", ["panel_type"] = "B", ["supplier"] = "FX-02" },
        });
        return (a, b);
    }

    /// <summary>Inner is nested in Outer. Inner is edited: 3 Outer instances and 2 Inner instances are affected.</summary>
    public static (SnapshotBuilder A, SnapshotBuilder B, Guid Inner) NestedBlock()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("Blocks");
        var inner = a.Block("Inner", content: 1);
        var outer = a.Block("Outer", content: 2, inner);
        for (int i = 0; i < 3; i++) a.Instance(layer, outer, Xform.Translation(i * 1000, 0, 0));
        for (int i = 0; i < 2; i++) a.Instance(layer, inner, Xform.Translation(0, i * 1000, 0));
        var b = a.Clone();
        b.EditBlock(inner, d => d with { ContentHash = 11 });
        return (a, b, inner);
    }

    public static (SnapshotBuilder A, SnapshotBuilder B) CopyInPlace()
    {
        var a = new SnapshotBuilder();
        var layer = a.Layer("Columns");
        var objects = a.Many(layer, 3);
        var b = a.Clone();
        var original = b.Objects.Single(o => o.Id == objects[1]);
        b.Objects.Add(original with { Id = b.NewId() });
        return (a, b);
    }

    public static (SnapshotBuilder A, SnapshotBuilder B) DeleteLayer()
    {
        var a = new SnapshotBuilder();
        var walls = a.Layer("Walls");
        var temp = a.Layer("Temp");
        a.Many(walls, 2);
        var gone = a.Many(temp, 4);
        var b = a.Clone();
        b.RemoveLayer(temp);
        foreach (var id in gone) b.RemoveObject(id);
        return (a, b);
    }

    /// <summary>
    /// The design doc's example: 362 per-object rows collapse into 6 causes. Walls renamed (212 objects),
    /// Doors recolored (36, ByLayer), Furniture moved under a new Interior (88), Slabs split 7 + 5, Temp deleted (14).
    /// </summary>
    public static (SnapshotBuilder A, SnapshotBuilder B) RootCauses()
    {
        var a = new SnapshotBuilder();
        var walls = a.Layer("Walls");
        var doors = a.Layer("Doors");
        var furniture = a.Layer("Furniture");
        var slabs = a.Layer("Slabs");
        var temp = a.Layer("Temp");
        a.Many(walls, 212);
        a.Many(doors, 36);
        a.Many(furniture, 88);
        var slabObjects = a.Many(slabs, 12);
        var tempObjects = a.Many(temp, 14);

        var b = a.Clone();
        b.EditLayer(walls, l => l with { Name = "A-WALL" });
        b.EditLayer(doors, l => l with { Color = Red });
        var interior = b.Layer("Interior");
        b.EditLayer(furniture, l => l with { ParentId = interior });
        b.Move(slabObjects.Take(7), b.Layer("Slabs-L01"));
        b.Move(slabObjects.Skip(7), b.Layer("Slabs-L02"));
        b.RemoveLayer(slabs);
        b.RemoveLayer(temp);
        foreach (var id in tempObjects) b.RemoveObject(id);
        return (a, b);
    }

    public static DiffResult Diff((SnapshotBuilder A, SnapshotBuilder B) pair, DiffSettings? settings = null) =>
        ModelDiff.Diff(pair.A.Build(), pair.B.Build(), settings);
}
