using DittoHopper.Core;
using Grasshopper.Kernel;

namespace DittoHopper.Grasshopper;

/// <summary>Picks changed objects out of a diff by kind of change and by layer, with a line of evidence for each.</summary>
public sealed class DiffFilterComponent : DittoComponent
{
    /// <summary>Kinds of change Diff Filter understands, besides detector flags.</summary>
    public static readonly string[] Kinds =
        ["added", "removed", "modified", "moved", "shape", "layer", "name", "color", "material", "linetype",
         "print width", "display order", "user text", "groups", "block", "flagged"];

    public DiffFilterComponent()
        : base("Diff Filter", "Filter",
            "The changed objects of a diff, filtered by kind of change and layer. Info says what changed and how each object was matched.",
            ReviewPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a04");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.DiffFilter;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Result", "Result", "Result of Model Diff.", GH_ParamAccess.item);
        p.AddTextParameter("Kind", "Kind",
            $"Kinds of change to keep: {string.Join(", ", Kinds)}, or a detector flag. Several: any of them. Empty: every change.",
            GH_ParamAccess.list);
        p[1].Optional = true;
        p.AddTextParameter("Layer", "Layer",
            "Only objects on these layers, before or after the change, e.g. Structure::* (the whole Structure tree). Empty: every layer.",
            GH_ParamAccess.list);
        p[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGeometryParameter("Objects", "Objects", "The matching objects: removed ones as they were in A, the rest as they are in B.", GH_ParamAccess.list);
        p.AddTextParameter("Info", "Info", "One line per object: what changed, its layer, and how it was matched (GUID, key, hash…).", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        var run = Input<DiffRun>(da, 0, "a Model Diff Result");
        if (run is null) return;
        var kinds = Texts(da, 1).Where(k => !string.IsNullOrWhiteSpace(k)).Select(Settings.Normalize).Distinct().ToList();
        var layers = new LayerGlob(Texts(da, 2));

        var flags = run.Result.ChangeSets.SelectMany(s => s.Flags).Select(Settings.Normalize).ToHashSet();
        var known = Kinds.Select(Settings.Normalize).ToHashSet();
        var unknown = kinds.Where(k => !known.Contains(k) && !flags.Contains(k)).ToList();
        if (unknown.Count > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"Unknown kind {string.Join(", ", unknown)}, and no change is flagged that way. Use: {string.Join(", ", Kinds)}.");

        var rows = Select(run, kinds, layers);
        da.SetDataList(0, rows.Select(r => Geometry.ToGoo(r.Pair.B is Guid b ? run.B.GeometryOf(b) : run.A.GeometryOf(r.Pair.A!.Value))));
        da.SetDataList(1, rows.Select(r => r.Info));
        Message = Describe.Plural(rows.Count, "object");
    }

    public sealed record Row(ObjectPair Pair, string Info);

    /// <summary>One row per object, in change-set order, combining every matching change on it.</summary>
    public static List<Row> Select(DiffRun run, IReadOnlyCollection<string> kinds, LayerGlob layers)
    {
        var changes = new Dictionary<ObjectPair, List<string>>();
        var order = new List<ObjectPair>();
        var events = run.Result.TableEvents.ToDictionary(e => e.Id);
        foreach (var set in run.Result.ChangeSets.Where(s => kinds.Count == 0 || kinds.Any(k => Matches(s, k))))
            foreach (var pair in set.Objects)
            {
                if (!layers.MatchesAll && !(pair.A is Guid a && layers.Matches(run.LayerOfA(a))) && !(pair.B is Guid b && layers.Matches(run.LayerOfB(b))))
                    continue;
                if (!changes.TryGetValue(pair, out var list))
                {
                    changes[pair] = list = [];
                    order.Add(pair);
                }
                string text = set.Kind switch
                {
                    ChangeKind.Added => "added",
                    ChangeKind.Removed => "removed",
                    _ => set.Change is { } c ? Describe.Change(c, run.GeometryOf(pair)) : "changed",
                };
                if (set.RootCauseEventId is { } cause && events.TryGetValue(cause, out var e)) text += $" ({Describe.Cause(e)})";
                if (set.Flags.Count > 0) text += $" [flagged: {string.Join(", ", set.Flags)}]";
                list.Add(text);
            }
        return [.. order.Select(p => new Row(p, Info(run, p, changes[p])))];
    }

    private static string Info(DiffRun run, ObjectPair pair, List<string> changes)
    {
        var record = pair.B is Guid b ? run.ObjectB(b) : run.ObjectA(pair.A!.Value);
        string layer = (pair.B is Guid lb ? run.LayerOfB(lb) : run.LayerOfA(pair.A!.Value)) ?? "?";
        string text = $"{Describe.Object(record, (pair.B ?? pair.A)!.Value)} · {string.Join("; ", changes)} · {layer}";
        if (pair.A is Guid a && run.MatchOfA(a) is { } m) text += " · matched by " + Describe.Evidence(m);
        return text;
    }

    private static bool Matches(ChangeSet s, string kind) => kind switch
    {
        "added" => s.Kind == ChangeKind.Added,
        "removed" => s.Kind == ChangeKind.Removed,
        "modified" => s.Kind == ChangeKind.Modified,
        "flagged" => s.Flags.Count > 0,
        _ => (s.Kind == ChangeKind.Modified && s.Change is { } c && Settings.Normalize(Keyword(c.Property)) == kind)
            || s.Flags.Any(f => Settings.Normalize(f) == kind),
    };

    private static string Keyword(PropertyKind p) => p switch
    {
        PropertyKind.Transform => "moved",
        PropertyKind.BlockContent => "block",
        _ => Describe.Name(p),
    };
}
