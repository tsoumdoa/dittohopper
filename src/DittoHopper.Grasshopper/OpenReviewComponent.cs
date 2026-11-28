using DittoHopper.Rhino;
using Grasshopper.Kernel;

namespace DittoHopper.Grasshopper;

/// <summary>Sends a result to the Rhino plug-in's review panel and opens it, once per new result.</summary>
public sealed class OpenReviewComponent : DittoComponent
{
    private DiffRun? sent;

    public OpenReviewComponent()
        : base("Open Review", "Review",
            "Opens a result in DittoHopper's Rhino review panel, the same panel as the DittoReview command: browse the changes, " +
            "zoom to them and see them in the viewport. It sends each new result once; recomputing with the same result " +
            "doesn't reopen a panel you closed.",
            ReviewPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a08");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.OpenReview;

    protected override void RegisterInputParams(GH_InputParamManager p) =>
        p.AddGenericParameter("Result", "Result", "Result of Model Diff.", GH_ParamAccess.item);

    protected override void RegisterOutputParams(GH_OutputParamManager p) { }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        var run = Input<DiffRun>(da, 0, "a Model Diff Result");
        if (run is null) return;
        // Model Diff hands out the same result until its inputs change, so this is once per new result.
        if (ReferenceEquals(run, sent))
        {
            Message = "Sent";
            return;
        }
        if (ReviewHub.Show(run) is { } problem)
        {
            Message = null;
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, problem);
            return;
        }
        sent = run;
        Message = "Sent";
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        sent = null;
        base.RemovedFromDocument(document);
    }
}
