using DittoHopper.Core;
using DittoHopper.Rhino;
using Grasshopper.Kernel;

namespace DittoHopper.Grasshopper;

/// <summary>A QA gate: passes when nothing on the given layers changed.</summary>
public sealed class AssertUnchangedComponent : DittoComponent
{
    public AssertUnchangedComponent()
        : base("Assert Unchanged", "Assert",
            "A QA gate: Pass is True only when nothing on the given layers changed between A and B. Why lists every change that " +
            "failed it. Every pattern must match a layer in A or B, so a typo fails. A fuzzy pairing that needs review on " +
            "those layers fails it; rebuilt geometry with the same shape (Data-only) never does. It sees only what the " +
            "Snapshots captured: a Layers filter on Snapshot hides the other layers from the gate.",
            ReviewPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a06");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.AssertUnchanged;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Result", "Result", "Result of Model Diff.", GH_ParamAccess.item);
        p.AddTextParameter("Layers", "Layers", "Layers that must not change. " + LayerPatterns.Help.Replace("Empty: every layer.", "Empty: the whole model."),
            GH_ParamAccess.list);
        p[1].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddBooleanParameter("Pass", "Pass", "True when nothing on those layers changed.", GH_ParamAccess.item);
        p.AddTextParameter("Why", "Why", "Each change that fails the gate, or what was checked when it passes.", GH_ParamAccess.list);
    }

    protected override void BeforeSolveInstance()
    {
        Message = null;
        base.BeforeSolveInstance();
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        var run = Input<DiffRun>(da, 0, "a Model Diff Result");
        if (run is null) return;
        var glob = new LayerPatterns(Texts(da, 1));

        var (_, pass, why) = Gate.Check(run, glob);
        if (Gate.Unmatched(run, glob) is { Count: > 0 } unmatched)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"No layer in A or B matches {string.Join(", ", unmatched)}.");
        da.SetData(0, pass);
        da.SetDataList(1, why);
        Message = pass ? "Pass" : "Fail";
    }
}
