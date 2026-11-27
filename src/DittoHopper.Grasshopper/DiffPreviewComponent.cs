using System.Drawing;
using DittoHopper.Core;
using Grasshopper.Kernel;
using Rhino.Display;
using Rhino.Geometry;

namespace DittoHopper.Grasshopper;

/// <summary>Draws a diff in the viewport in the status colours.</summary>
public sealed class DiffPreviewComponent : DittoComponent
{
    public enum Status { Unchanged, Event, Added, Modified, Removed, Previous }

    public readonly record struct Item(GeometryBase Geometry, Status Status);

    private readonly List<Item> items = [];
    private BoundingBox box = BoundingBox.Empty;

    public DiffPreviewComponent()
        : base("Diff Preview", "Preview",
            "Shows a diff in the viewport: added green, removed red, modified amber, objects whose layer or block changed blue, " +
            "unchanged grey. Ghost A also draws modified objects as they were in A.",
            ReviewPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a05");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.DiffPreview;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Result", "Result", "Result of Model Diff.", GH_ParamAccess.item);
        p.AddBooleanParameter("Ghost A", "Ghost", "Also draw modified objects as they were in A, faintly.", GH_ParamAccess.item, true);
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
        foreach (var item in Build(run, ghost))
        {
            items.Add(item);
            box.Union(item.Geometry.GetBoundingBox(false));
        }
    }

    /// <summary>Every B object with its status, plus removed A objects and, with ghost, modified A objects.</summary>
    public static IEnumerable<Item> Build(DiffRun run, bool ghost)
    {
        var added = run.Added.ToHashSet();
        var modified = run.Modified.Where(p => p.B is not null).Select(p => p.B!.Value).ToHashSet();
        var (eventLayers, eventBlocks) = (new HashSet<Guid>(), new HashSet<Guid>());
        foreach (var e in run.Result.TableEvents)
        {
            if (e.Table == TableKind.Layer && e.Kind != TableEventKind.Added && e.B is Guid lb) eventLayers.Add(lb);
            if (e.Table == TableKind.Layer) foreach (var s in e.Shares) eventLayers.Add(s.B);
            if (e.Table == TableKind.Block && e.B is Guid bb) eventBlocks.Add(bb);
        }

        foreach (var o in run.B.Model.Objects)
        {
            var status = added.Contains(o.Id) ? Status.Added
                : modified.Contains(o.Id) ? Status.Modified
                : eventLayers.Contains(o.LayerId) || (o.BlockDefinitionId is Guid d && eventBlocks.Contains(d)) ? Status.Event
                : Status.Unchanged;
            if (Display(run.B.GeometryOf(o.Id)) is { } g) yield return new Item(g, status);
        }
        foreach (var id in run.Removed)
            if (Display(run.A.GeometryOf(id)) is { } g) yield return new Item(g, Status.Removed);
        if (ghost)
            foreach (var pair in run.Modified)
                if (pair.A is Guid a && Display(run.A.GeometryOf(a)) is { } g) yield return new Item(g, Status.Previous);
    }

    /// <summary>Geometry the pipeline can draw: an instance becomes its insertion point, an extrusion a Brep.</summary>
    private static GeometryBase? Display(GeometryBase? g) => g switch
    {
        InstanceReferenceGeometry i => new global::Rhino.Geometry.Point(Geometry.InsertionPoint(i)),
        Extrusion e => e.ToBrep(true),
        _ => g,
    };

    public override bool IsPreviewCapable => true;

    public override BoundingBox ClippingBox => box;

    public override void DrawViewportMeshes(IGH_PreviewArgs args)
    {
        foreach (var item in items)
        {
            if (item.Status is not (Status.Added or Status.Modified or Status.Removed)) continue;
            using var material = new DisplayMaterial(ColorOf(item.Status), item.Status == Status.Removed ? 0.75 : 0.45);
            switch (item.Geometry)
            {
                case Brep b: args.Display.DrawBrepShaded(b, material); break;
                case Mesh m: args.Display.DrawMeshShaded(m, material); break;
                case SubD s: args.Display.DrawSubDShaded(s, material); break;
            }
        }
    }

    public override void DrawViewportWires(IGH_PreviewArgs args)
    {
        int bold = Attributes.Selected ? 3 : 2;
        foreach (var item in items)
        {
            var color = ColorOf(item.Status);
            int width = item.Status is Status.Unchanged or Status.Previous ? 1 : bold;
            Wires(args.Display, item.Geometry, color, width);
        }
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
