using DittoHopper.Core;
using DittoHopper.Rhino;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace DittoHopper.Grasshopper;

/// <summary>Diffs snapshot A (before) against snapshot B (after).</summary>
public sealed class ModelDiffComponent : DittoComponent
{
    private DiffRun? last;

    public ModelDiffComponent()
        : base("Model Diff", "Diff",
            "Compares model A (before) with model B (after): what was added, removed and modified, and what did not change. " +
            "Each change is reported once, at its root cause: a layer rename is one table event, not a row per object. " +
            "Its geometry preview is off by default; use Diff Preview to see the changes in colour.",
            DiffPanel)
    {
        Hidden = true;
    }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a03");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.ModelDiff;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("A", "A", "The model before: a Snapshot.", GH_ParamAccess.item);
        p.AddGenericParameter("B", "B", "The model after: a Snapshot.", GH_ParamAccess.item);
        p.AddGenericParameter("Settings", "Settings", "Optional Diff Settings. Empty: the defaults.", GH_ParamAccess.item);
        p[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGeometryParameter("Added", "Added",
            "Objects only in B, in the Rhino document's units (A's without one). A block instance is its insertion point.", GH_ParamAccess.list);
        p.AddGeometryParameter("Removed", "Removed", "Objects only in A, as they were in A, in the document's units. A block instance is its insertion point.", GH_ParamAccess.list);
        p.AddGeometryParameter("Modified", "Modified",
            "Matched objects that changed, as they are in B, in the document's units. Use Diff Filter to see how.", GH_ParamAccess.list);
        p.AddTextParameter("Events", "Events", "Table events: layers, blocks, materials, linetypes and groups added, deleted, renamed or changed.", GH_ParamAccess.list);
        p.AddGenericParameter("Result", "Result", "The whole diff. Connect to Diff Filter, Diff Preview or Assert Unchanged; a Panel shows its summary.", GH_ParamAccess.item);
    }

    protected override void BeforeSolveInstance()
    {
        Message = null;
        base.BeforeSolveInstance();
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        var a = Input<RhinoSnapshot>(da, 0, "a Snapshot");
        var b = Input<RhinoSnapshot>(da, 1, "a Snapshot");
        if (a is null || b is null) return;
        var settings = new DiffSettings();
        IGH_Goo? cfg = null;
        if (da.GetData(2, ref cfg) && cfg is not null)
        {
            if (Unwrap<DiffSettings>(cfg) is not { } given)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Settings needs Diff Settings, not {cfg.TypeName}.");
                return;
            }
            settings = given;
        }

        DiffRun run;
        if (last is not null && ReferenceEquals(last.A, a) && ReferenceEquals(last.B, b) && last.Result.Settings == settings)
        {
            run = last;
        }
        else
        {
            try
            {
                run = new DiffRun(RhinoDiff.Diff(a, b, settings), a, b);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
                return;
            }
            last = run;
        }

        foreach (string note in run.Result.Header.Notes)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, note);
        var units = Geometry.OutputUnits(run, OnPingDocument()?.RhinoDocument ?? global::Rhino.RhinoDoc.ActiveDoc);
        if (units.Remark is { } remark) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, remark);
        da.SetDataList(0, run.Added.Select(id => Geometry.ToGoo(b.GeometryOf(id), units.B)));
        da.SetDataList(1, run.Removed.Select(id => Geometry.ToGoo(a.GeometryOf(id), units.A)));
        da.SetDataList(2, run.Modified.Select(p => p.B is Guid id ? Geometry.ToGoo(b.GeometryOf(id), units.B) : null));
        da.SetDataList(3, run.Result.TableEvents.Select(Describe.Event));
        da.SetData(4, new ResultGoo(run));

        int review = run.Result.Matches.Count(m => m.NeedsReview);
        Message = $"+{run.Added.Count} −{run.Removed.Count} ~{run.Modified.Count} · {Describe.Plural(run.Result.TableEvents.Count, "event")}" +
            (review > 0 ? $" · {review} to review" : "");
    }

    public override void RemovedFromDocument(GH_Document document)
    {
        last = null;
        base.RemovedFromDocument(document);
    }
}
