using DittoHopper.Core;
using Grasshopper.Kernel;

namespace DittoHopper.Grasshopper;

/// <summary>A QA gate: passes when nothing on the given layers changed.</summary>
public sealed class AssertUnchangedComponent : DittoComponent
{
    public AssertUnchangedComponent()
        : base("Assert Unchanged", "Assert",
            "A QA gate: Pass is True only when nothing on the given layers changed between A and B. Why lists every change that " +
            "failed it. A pattern that matches no layer fails, so a typo can't pass. Rebuilt geometry with the same shape " +
            "(Data-only) never fails it.",
            ReviewPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a06");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.AssertUnchanged;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Result", "Result", "Result of Model Diff.", GH_ParamAccess.item);
        p.AddTextParameter("Layers", "Layers", "Layers that must not change. " + LayerGlob.Help.Replace("Empty: every layer.", "Empty: the whole model."),
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

        var (pass, why) = Check(run, glob);
        if (!glob.MatchesAll && !MatchesAnyLayer(run, glob))
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"No layer in A or B matches {glob}.");
        da.SetData(0, pass);
        da.SetDataList(1, why);
        Message = pass ? "Pass" : "Fail";
    }

    private static bool MatchesAnyLayer(DiffRun run, LayerGlob glob) =>
        run.LayerPathsA.Values.Concat(run.LayerPathsB.Values).Any(glob.Matches);

    /// <summary>The gate: every change set and table event that touches a matching layer fails it, one line each.</summary>
    public static (bool Pass, List<string> Why) Check(DiffRun run, LayerGlob glob)
    {
        if (!glob.MatchesAll && !MatchesAnyLayer(run, glob))
            return (false, [$"No layer in A or B matches {glob}, so nothing was checked. Check the spelling."]);

        var why = new List<string>();
        foreach (var e in run.Result.TableEvents.Where(e => Touches(run, e, glob)))
            why.Add(Describe.Event(e));
        foreach (var set in run.Result.ChangeSets)
        {
            // Count only the objects in scope: a set can span many layers.
            var inScope = set.Objects
                .Select(p => (A: p.A is Guid a ? run.LayerOfA(a) : null, B: p.B is Guid b ? run.LayerOfB(b) : null))
                .Where(l => glob.Matches(l.A) || glob.Matches(l.B))
                .ToList();
            if (inScope.Count > 0)
                why.Add(Describe.Set(set, inScope.Count, inScope.Select(l => (l.B is not null && glob.Matches(l.B) ? l.B : l.A) ?? l.B ?? "?")));
        }
        if (why.Count > 0) return (false, why);

        int checkedObjects = run.A.Model.Objects.Count(o => glob.Matches(run.LayerPathsA.GetValueOrDefault(o.LayerId)));
        return (true, [$"Nothing on {glob} changed: {Describe.Plural(checkedObjects, "object")} checked."]);
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
                string? pathA = ea is Guid la ? run.LayerPathsA.GetValueOrDefault(la) : null;
                string? pathB = eb is Guid lb ? run.LayerPathsB.GetValueOrDefault(lb) : null;
                // A rename or reparent changes the path of every sublayer too, in both trees.
                if (e.Kind is TableEventKind.Renamed or TableEventKind.Reparented)
                    return run.LayerPathsA.Values.Any(p => DiffRun.IsUnder(p, pathA) && glob.Matches(p))
                        || run.LayerPathsB.Values.Any(p => DiffRun.IsUnder(p, pathB) && glob.Matches(p));
                return glob.Matches(e.Label) || glob.Matches(pathA) || glob.Matches(pathB) || e.Shares.Any(s => glob.Matches(s.Label));
            case TableKind.Block:
                return OnA(o => ea is not null && o.BlockDefinitionId == ea) || OnB(o => eb is not null && o.BlockDefinitionId == eb);
            case TableKind.Group:
                return OnA(o => ea is Guid ga && o.GroupIds.Contains(ga)) || OnB(o => eb is Guid gb && o.GroupIds.Contains(gb));
            case TableKind.Material:
                return OnA(o => ea is Guid ma && DiffRun.Uses(a, o, o.Material, l => l.MaterialId, ma))
                    || OnB(o => eb is Guid mb && DiffRun.Uses(b, o, o.Material, l => l.MaterialId, mb));
            case TableKind.Linetype:
                return OnA(o => ea is Guid ta && DiffRun.Uses(a, o, o.Linetype, l => l.LinetypeId, ta))
                    || OnB(o => eb is Guid tb && DiffRun.Uses(b, o, o.Linetype, l => l.LinetypeId, tb));
            default:
                return true;
        }
    }
}
