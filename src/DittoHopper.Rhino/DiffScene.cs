using System.Drawing;
using Rhino.Display;
using Rhino.Geometry;
using RhinoDoc = Rhino.RhinoDoc;

namespace DittoHopper.Rhino;

/// <summary>
/// A diff as things to draw in the status colours, both sides converted to the viewport document's units (A's when
/// there is no document): what Diff Preview and the review panel's viewport draw.
/// Ownership: items point at the snapshots' (or the live document's) geometry and are drawn through a model transform
/// (units, block placement); the only geometry created here is a Brep per extrusion. Nothing is disposed: dropping the
/// items lets the GC release it.
/// </summary>
public static class DiffScene
{
    public enum Status { Unchanged, Event, Added, Modified, Removed, Previous }

    /// <summary>
    /// One thing to draw: geometry in its own space, placed by <see cref="Xform"/>. <see cref="Id"/> is the top-level
    /// object it belongs to: the B object, or the A object for removed objects and A's previous state.
    /// </summary>
    public readonly record struct Item(GeometryBase Geometry, Status Status, Transform Xform, Guid Id);

    // The status palette from the design doc (the same colours the Grasshopper icons use).
    public static readonly Color Added = Color.FromArgb(34, 197, 94);
    public static readonly Color Removed = Color.FromArgb(239, 68, 68);
    public static readonly Color Modified = Color.FromArgb(245, 158, 11);
    public static readonly Color Event = Color.FromArgb(59, 130, 246);

    /// <summary>
    /// Unchanged context: a dark slate, not the icons' grey (#9CA3AF), which is almost exactly Rhino's default viewport
    /// background. This keeps a 3:1 luminance contrast on the default grey and on white.
    /// </summary>
    public static readonly Color UnchangedWire = Color.FromArgb(55, 65, 81);

    public static Color ColorOf(Status s) => s switch
    {
        Status.Added => Added,
        Status.Removed => Removed,
        Status.Modified => Modified,
        Status.Previous => Color.FromArgb(150, Modified),
        Status.Event => Event,
        _ => UnchangedWire,
    };

    /// <summary>
    /// Every B object with its status, removed A objects and, with <paramref name="ghost"/>, modified A objects as they
    /// were. <paramref name="liveB"/>, when given, supplies B's geometry from the open document instead of the snapshot,
    /// so edits made since the diff show; an object it returns null for (deleted since) is skipped.
    /// </summary>
    public static List<Item> Build(DiffRun run, bool ghost, RhinoDoc? doc, Func<Guid, GeometryBase?>? liveB = null)
    {
        var units = run.OutputUnits(doc);
        var toDocA = Transform.Scale(Point3d.Origin, units.A);
        var toDocB = Transform.Scale(Point3d.Origin, units.B);
        var added = run.Added.ToHashSet();
        var modified = run.Modified.Where(p => p.B is not null).Select(p => p.B!.Value).ToHashSet();
        var events = run.ChangedThroughTables;

        var list = new List<Item>();
        foreach (var o in run.B.Model.Objects)
        {
            var status = added.Contains(o.Id) ? Status.Added
                : modified.Contains(o.Id) ? Status.Modified
                : events.Contains(o.Id) ? Status.Event
                : Status.Unchanged;
            Add(list, run.B, liveB is null ? run.B.GeometryOf(o.Id) : liveB(o.Id), status, toDocB, o.Id, 0);
        }
        foreach (var id in run.Removed)
            Add(list, run.A, run.A.GeometryOf(id), Status.Removed, toDocA, id, 0);
        if (ghost)
            foreach (var pair in run.Modified)
                if (pair.A is Guid a) Add(list, run.A, run.A.GeometryOf(a), Status.Previous, toDocA, a, 0);
        return list;
    }

    /// <summary>A block instance draws as its definition's members, nested ones included, placed by its transform.</summary>
    private static void Add(List<Item> list, RhinoSnapshot side, GeometryBase? g, Status status, Transform xform, Guid id, int depth)
    {
        switch (g)
        {
            case null:
                return;
            case InstanceReferenceGeometry instance:
                var placed = xform * instance.Xform;
                var members = depth < 16 ? side.DefinitionGeometry(instance.ParentIdefId) : [];
                if (members.Count == 0)
                    list.Add(new Item(new global::Rhino.Geometry.Point(Point3d.Origin), status, placed, id)); // linked or empty: its insertion point
                foreach (var member in members)
                    Add(list, side, member, status, placed, id, depth + 1);
                return;
            case Extrusion e when e.ToBrep(true) is { } brep:
                list.Add(new Item(brep, status, xform, id));
                return;
            default:
                list.Add(new Item(g, status, xform, id));
                return;
        }
    }

    /// <summary>Shades a Brep, mesh or SubD item in <paramref name="color"/>; other geometry has no faces.</summary>
    public static void DrawShaded(DisplayPipeline p, Item item, Color color, double transparency)
    {
        if (item.Geometry is not (Brep or Mesh or SubD)) return;
        using var material = new DisplayMaterial(color, transparency);
        Placed(p, item.Xform, d =>
        {
            switch (item.Geometry)
            {
                case Brep b: d.DrawBrepShaded(b, material); break;
                case Mesh m: d.DrawMeshShaded(m, material); break;
                case SubD s: d.DrawSubDShaded(s, material); break;
            }
        });
    }

    public static void DrawWires(DisplayPipeline p, Item item, Color color, int width) =>
        Placed(p, item.Xform, d => Wires(d, item.Geometry, color, width));

    private static void Placed(DisplayPipeline p, Transform xform, Action<DisplayPipeline> draw)
    {
        if (xform.IsIdentity)
        {
            draw(p);
            return;
        }
        p.PushModelTransform(xform);
        try { draw(p); }
        finally { p.PopModelTransform(); }
    }

    private static void Wires(DisplayPipeline p, GeometryBase g, Color c, int width)
    {
        switch (g)
        {
            case global::Rhino.Geometry.Point pt: p.DrawPoint(pt.Location, PointStyle.RoundSimple, 3 + width, c); break;
            case PointCloud cloud: p.DrawPointCloud(cloud, 1 + width, c); break;
            case Curve curve: p.DrawCurve(curve, c, width); break;
            case Brep brep: p.DrawBrepWires(brep, c, -1); break;
            case Mesh mesh: p.DrawMeshWires(mesh, c); break;
            case SubD subd: p.DrawSubDWires(subd, c, width); break;
            case TextDot dot: p.DrawDot(dot.Point, dot.Text, c, Color.White); break;
            default:
                var bbox = g.GetBoundingBox(false);
                if (bbox.IsValid) p.DrawBox(bbox, c, width);
                break;
        }
    }
}
