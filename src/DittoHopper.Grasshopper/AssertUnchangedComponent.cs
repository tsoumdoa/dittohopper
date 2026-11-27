using DittoHopper.Core;
using Grasshopper.Kernel;

namespace DittoHopper.Grasshopper;

/// <summary>A QA gate: passes when nothing on the given layers changed.</summary>
public sealed class AssertUnchangedComponent : DittoComponent
{
    public AssertUnchangedComponent()
        : base("Assert Unchanged", "Assert",
            "A QA gate: Pass is True when nothing on the given layers changed between A and B. Why lists every change that failed it. " +
            "Rebuilt geometry with the same shape (Data-only) never fails it.",
            ReviewPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a06");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.AssertUnchanged;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Result", "Result", "Result of Model Diff.", GH_ParamAccess.item);
        p.AddTextParameter("Layers", "Layers",
            "Layers that must not change, e.g. Structure::* (the whole Structure tree). * matches any text. Empty: the whole model.",
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
        var glob = new LayerGlob(Texts(da, 1));

        if (!glob.MatchesAll && !run.LayerPathsA.Values.Concat(run.LayerPathsB.Values).Any(glob.Matches))
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"No layer in A or B matches {glob}. Check the spelling: the gate checks nothing.");

        var why = Check(run, glob);
        bool pass = why.Count == 0;
        if (pass)
        {
            int checkedObjects = run.A.Model.Objects.Count(o => glob.Matches(run.LayerPathsA.GetValueOrDefault(o.LayerId)));
            why.Add($"Nothing on {glob} changed: {Describe.Plural(checkedObjects, "object")} checked.");
        }
        da.SetData(0, pass);
        da.SetDataList(1, why);
        Message = pass ? "Pass" : "Fail";
    }

    /// <summary>Every change set and table event that touches a matching layer, one line each.</summary>
    public static List<string> Check(DiffRun run, LayerGlob glob)
    {
        var why = new List<string>();
        foreach (var e in run.Result.TableEvents.Where(e => Touches(run, e, glob)))
            why.Add(Describe.Event(e));
        foreach (var set in run.Result.ChangeSets)
        {
            var layers = set.Objects
                .SelectMany(p => new[] { p.A is Guid a ? run.LayerOfA(a) : null, p.B is Guid b ? run.LayerOfB(b) : null })
                .OfType<string>().Where(glob.Matches).ToList();
            if (layers.Count > 0) why.Add(Describe.Set(set, layers));
        }
        return why;
    }

    private static bool Touches(DiffRun run, TableEvent e, LayerGlob glob)
    {
        if (glob.MatchesAll) return true;
        var (a, b) = (run.A.Model, run.B.Model);
        bool OnA(Func<ObjectRecord, bool> uses) => a.Objects.Any(o => uses(o) && glob.Matches(run.LayerPathsA.GetValueOrDefault(o.LayerId)));
        bool OnB(Func<ObjectRecord, bool> uses) => b.Objects.Any(o => uses(o) && glob.Matches(run.LayerPathsB.GetValueOrDefault(o.LayerId)));
        Guid? ea = e.A, eb = e.B;
        switch (e.Table)
        {
            case TableKind.Layer:
                return glob.Matches(e.Label)
                    || (ea is Guid la && glob.Matches(run.LayerPathsA.GetValueOrDefault(la)))
                    || (eb is Guid lb && glob.Matches(run.LayerPathsB.GetValueOrDefault(lb)))
                    || e.Shares.Any(s => glob.Matches(s.Label));
            case TableKind.Block:
                return OnA(o => ea is not null && o.BlockDefinitionId == ea) || OnB(o => eb is not null && o.BlockDefinitionId == eb);
            case TableKind.Group:
                return OnA(o => ea is Guid ga && o.GroupIds.Contains(ga)) || OnB(o => eb is Guid gb && o.GroupIds.Contains(gb));
            case TableKind.Material:
                return OnA(o => ea is Guid ma && Uses(a, o, o.Material, l => l.MaterialId, ma))
                    || OnB(o => eb is Guid mb && Uses(b, o, o.Material, l => l.MaterialId, mb));
            case TableKind.Linetype:
                return OnA(o => ea is Guid ta && Uses(a, o, o.Linetype, l => l.LinetypeId, ta))
                    || OnB(o => eb is Guid tb && Uses(b, o, o.Linetype, l => l.LinetypeId, tb));
            default:
                return true;
        }
    }

    /// <summary>The object's own value, or its layer's when it takes it from the layer.</summary>
    private static bool Uses(ModelSnapshot model, ObjectRecord o, Sourced<Guid> value, Func<LayerRecord, Guid> fromLayer, Guid id) =>
        value.Source switch
        {
            ValueSource.ByObject => value.Value == id,
            ValueSource.ByLayer => model.Layers.FirstOrDefault(l => l.Id == o.LayerId) is { } layer && fromLayer(layer) == id,
            _ => false,
        };
}
