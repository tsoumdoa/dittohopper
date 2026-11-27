using System.Drawing;
using DittoHopper.Core;
using DittoHopper.Rhino;
using Grasshopper.Kernel;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace DittoHopper.Grasshopper;

/// <summary>
/// Draws a diff in the viewport in the status colours, both sides converted to the viewport document's units
/// (A's units when there is no document).
/// Ownership: items point at the snapshots' geometry and are drawn through a model transform (units, block
/// placement); the only geometry this component creates is a Brep per extrusion. It never disposes anything:
/// clearing the items lets the GC release it.
/// </summary>
public sealed class DiffPreviewComponent : DittoComponent
{
    public enum Status { Unchanged, Event, Added, Modified, Removed, Previous }

    /// <summary>One thing to draw: geometry in its own space, placed by <see cref="Xform"/>.</summary>
    public readonly record struct Item(GeometryBase Geometry, Status Status, Transform Xform);

    private readonly List<Item> items = [];
    private BoundingBox box = BoundingBox.Empty;

    public DiffPreviewComponent()
        : base("Diff Preview", "Preview",
            "Shows a diff in the viewport: added green, removed red, modified amber, unchanged grey, and blue for objects that " +
            "look different only because a layer, block, material or linetype they use changed. Ghost A also draws modified " +
            "objects as they were in A, faintly.",
            ReviewPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a05");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.DiffPreview;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Result", "Result", "Result of Model Diff.", GH_ParamAccess.item);
        p.AddBooleanParameter("Ghost A", "Ghost A", "Also draw modified objects as they were in A, faintly.", GH_ParamAccess.item, true);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p) { }

    public IReadOnlyList<Item> Items => items;

    public override void ClearData()
    {
        items.Clear();
        box = BoundingBox.Empty;
        base.ClearData();
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        var run = Input<DiffRun>(da, 0, "a Model Diff Result");
        if (run is null) return;
        bool ghost = true;
        da.GetData(1, ref ghost);
        var doc = OnPingDocument()?.RhinoDocument ?? RhinoDoc.ActiveDoc;
        foreach (var item in Build(run, ghost, doc))
        {
            items.Add(item);
            box.Union(item.Geometry.GetBoundingBox(item.Xform));
        }
    }

    /// <summary>Every B object with its status, removed A objects and, with ghost, modified A objects as they were.</summary>
    public static IEnumerable<Item> Build(DiffRun run, bool ghost, RhinoDoc? doc)
    {
        // The same unit rule as the geometry outputs: the document's units, or A's without a document.
        var units = Geometry.OutputUnits(run, doc);
        var toDocA = Transform.Scale(Point3d.Origin, units.A);
        var toDocB = Transform.Scale(Point3d.Origin, units.B);
        var added = run.Added.ToHashSet();
        var modified = run.Modified.Where(p => p.B is not null).Select(p => p.B!.Value).ToHashSet();
        var events = EventObjects(run);

        var list = new List<Item>();
        foreach (var o in run.B.Model.Objects)
        {
            var status = added.Contains(o.Id) ? Status.Added
                : modified.Contains(o.Id) ? Status.Modified
                : events.Contains(o.Id) ? Status.Event
                : Status.Unchanged;
            Add(list, run.B, run.B.GeometryOf(o.Id), status, toDocB, 0);
        }
        foreach (var id in run.Removed)
            Add(list, run.A, run.A.GeometryOf(id), Status.Removed, toDocA, 0);
        if (ghost)
            foreach (var pair in run.Modified)
                if (pair.A is Guid a) Add(list, run.A, run.A.GeometryOf(a), Status.Previous, toDocA, 0);
        return list;
    }

    /// <summary>A block instance draws as its definition's members, nested ones included, placed by its transform.</summary>
    private static void Add(List<Item> list, RhinoSnapshot side, GeometryBase? g, Status status, Transform xform, int depth)
    {
        switch (g)
        {
            case null:
                return;
            case InstanceReferenceGeometry instance:
                var placed = xform * instance.Xform;
                var members = depth < 16 ? side.DefinitionGeometry(instance.ParentIdefId) : [];
                if (members.Count == 0)
                    list.Add(new Item(new global::Rhino.Geometry.Point(Point3d.Origin), status, placed)); // linked or empty: its insertion point
                foreach (var member in members)
                    Add(list, side, member, status, placed, depth + 1);
                return;
            case Extrusion e when e.ToBrep(true) is { } brep:
                list.Add(new Item(brep, status, xform));
                return;
            default:
                list.Add(new Item(g, status, xform));
                return;
        }
    }

    /// <summary>
    /// B objects that look different only because a table entry they take their appearance from changed: a layer's
    /// colour paints the objects whose colour is ByLayer, not those with their own; a rename or reparent paints
    /// everything on the layer and its sublayers; a block, material or linetype edit paints its users.
    /// </summary>
    private static HashSet<Guid> EventObjects(DiffRun run)
    {
        var b = run.B.Model;
        var paths = run.LayerPathsB;
        var onLayer = b.Objects.ToLookup(o => o.LayerId);
        var result = new HashSet<Guid>();
        void Paint(IEnumerable<ObjectRecord> objects) { foreach (var o in objects) result.Add(o.Id); }

        foreach (var e in run.Result.TableEvents)
        {
            if (e.Table == TableKind.Layer)
            {
                if (e.Kind == TableEventKind.Split)
                    foreach (var share in e.Shares) Paint(onLayer[share.B]);
                if (e.B is not Guid lb) continue;
                switch (e.Kind)
                {
                    case TableEventKind.Renamed or TableEventKind.Reparented:
                        string? path = paths.GetValueOrDefault(lb);
                        foreach (var (id, p) in paths)
                            if (DiffRun.IsUnder(p, path)) Paint(onLayer[id]);
                        break;
                    case TableEventKind.PropertyChanged:
                        Paint(onLayer[lb].Where(o => e.Property switch
                        {
                            PropertyKind.Color => o.Color.Source == ValueSource.ByLayer,
                            PropertyKind.Material => o.Material.Source == ValueSource.ByLayer,
                            PropertyKind.Linetype => o.Linetype.Source == ValueSource.ByLayer,
                            PropertyKind.PrintWidth => o.PrintWidth.Source == ValueSource.ByLayer,
                            _ => true,
                        }));
                        break;
                }
            }
            else if (e.B is Guid id && e.Kind is TableEventKind.Renamed or TableEventKind.PropertyChanged)
            {
                Paint(e.Table switch
                {
                    TableKind.Block => b.Objects.Where(o => o.BlockDefinitionId == id),
                    TableKind.Material => b.Objects.Where(o => DiffRun.Uses(b, o, o.Material, l => l.MaterialId, id)),
                    TableKind.Linetype => b.Objects.Where(o => DiffRun.Uses(b, o, o.Linetype, l => l.LinetypeId, id)),
                    _ => [],
                });
            }
        }
        return result;
    }

    public override bool IsPreviewCapable => true;

    public override BoundingBox ClippingBox => box;

    public override void DrawViewportMeshes(IGH_PreviewArgs args)
    {
        foreach (var item in items)
        {
            if (item.Status is not (Status.Added or Status.Modified or Status.Removed)) continue;
            using var material = new DisplayMaterial(ColorOf(item.Status), item.Status == Status.Removed ? 0.75 : 0.45);
            Placed(args.Display, item.Xform, p =>
            {
                switch (item.Geometry)
                {
                    case Brep b: p.DrawBrepShaded(b, material); break;
                    case Mesh m: p.DrawMeshShaded(m, material); break;
                    case SubD s: p.DrawSubDShaded(s, material); break;
                }
            });
        }
    }

    public override void DrawViewportWires(IGH_PreviewArgs args)
    {
        int bold = Attributes.Selected ? 3 : 2;
        foreach (var item in items)
        {
            var color = ColorOf(item.Status);
            int width = item.Status is Status.Unchanged or Status.Previous ? 1 : bold;
            Placed(args.Display, item.Xform, p => Wires(p, item.Geometry, color, width));
        }
    }

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

    public static Color ColorOf(Status s) => s switch
    {
        Status.Added => Icons.Added,
        Status.Removed => Icons.Removed,
        Status.Modified => Icons.Modified,
        Status.Previous => Color.FromArgb(150, Icons.Modified),
        Status.Event => Icons.Event,
        _ => Icons.Unchanged,
    };

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
