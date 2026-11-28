using DittoHopper.Rhino;
using Grasshopper.Kernel;
using Rhino;
using Rhino.Geometry;
using Status = DittoHopper.Rhino.DiffScene.Status;

namespace DittoHopper.Grasshopper;

/// <summary>
/// Draws a diff in the viewport in the status colours, both sides converted to the viewport document's units
/// (A's units when there is no document). The items and the drawing are <see cref="DiffScene"/>'s, shared with the
/// Rhino plug-in's review viewport.
/// </summary>
public sealed class DiffPreviewComponent : DittoComponent
{
    private readonly List<DiffScene.Item> items = [];
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

    public IReadOnlyList<DiffScene.Item> Items => items;

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
        foreach (var item in DiffScene.Build(run, ghost, doc))
        {
            items.Add(item);
            box.Union(item.Geometry.GetBoundingBox(item.Xform));
        }
    }

    public override bool IsPreviewCapable => true;

    public override BoundingBox ClippingBox => box;

    public override void DrawViewportMeshes(IGH_PreviewArgs args)
    {
        foreach (var item in items)
            if (item.Status is Status.Added or Status.Modified or Status.Removed)
                DiffScene.DrawShaded(args.Display, item, DiffScene.ColorOf(item.Status), item.Status == Status.Removed ? 0.75 : 0.45);
    }

    public override void DrawViewportWires(IGH_PreviewArgs args)
    {
        int bold = Attributes.Selected ? 3 : 2;
        foreach (var item in items)
            DiffScene.DrawWires(args.Display, item, DiffScene.ColorOf(item.Status), item.Status is Status.Unchanged or Status.Previous ? 1 : bold);
    }
}
