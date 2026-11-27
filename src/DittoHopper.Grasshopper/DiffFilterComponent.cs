using DittoHopper.Core;
using Grasshopper.Kernel;

namespace DittoHopper.Grasshopper;

/// <summary>Picks changed objects out of a diff by kind of change and by layer, with a line of evidence for each.</summary>
public sealed class DiffFilterComponent : DittoComponent
{
    /// <summary>Kinds besides the property names (the same as Diff Settings' Ignore) and the detector ids.</summary>
    private static readonly string[] ObjectKinds = ["added", "removed", "modified", "flagged", "needs look", "review"];

    /// <summary>Core's detector ids, as they appear in a change set's flags.</summary>
    public static readonly string[] DetectorIds =
    [
        Detectors.OutsideScope, Detectors.DefaultLayer, Detectors.TinyNudge, Detectors.ByLayerOverride, Detectors.Duplicate,
        Detectors.LockedOrHiddenLayer, Detectors.LoneDeletion, Detectors.MassChange, Detectors.FuzzyMatch,
    ];

    public static IEnumerable<string> Kinds => ObjectKinds.Concat(Vocabulary.Names).Concat(DetectorIds);

    public DiffFilterComponent()
        : base("Diff Filter", "Filter",
            "The changed objects of a diff, filtered by kind of change and layer. Info says what changed, what didn't, and how " +
            "each object was matched.",
            ReviewPanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a04");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.DiffFilter;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Result", "Result", "Result of Model Diff.", GH_ParamAccess.item);
        p.AddTextParameter("Kind", "Kind",
            $"Kinds of change to keep: added, removed, modified; a property ({string.Join(", ", Vocabulary.Names)}; moved is placement); " +
            $"flagged (any detector), needs look (flagged and not yet intended) or a detector ({string.Join(", ", DetectorIds)}); " +
            "review (fuzzy pairings to check; a pairing alone is not a change). Several: any of them. Empty: every change and pairing to review. " +
            "Table events, such as a block definition edited directly or a layer renamed, are not objects: they are listed in " +
            "Model Diff's Events.",
            GH_ParamAccess.list);
        p[1].Optional = true;
        p.AddTextParameter("Layers", "Layers",
            "Only objects on these layers, before or after the change. " + LayerPatterns.Help, GH_ParamAccess.list);
        p[2].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGeometryParameter("Objects", "Objects",
            "The matching objects in the Rhino document's units (A's without one): removed ones as they were in A, the rest as they are in B. A block instance is its insertion point.",
            GH_ParamAccess.list);
        p.AddTextParameter("Info", "Info",
            "One line per object: what changed, its layer, how many properties did not change, and how it was matched (GUID, key, hash…).",
            GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        var run = Input<DiffRun>(da, 0, "a Model Diff Result");
        if (run is null) return;
        var layers = new LayerPatterns(Texts(da, 2));

        var other = ObjectKinds.Concat(DetectorIds).Select(Vocabulary.Normalize).ToHashSet();
        var kinds = new List<string>();
        var unknown = new List<string>();
        foreach (var raw in Texts(da, 1).Where(k => !string.IsNullOrWhiteSpace(k)))
        {
            string k = Vocabulary.Canonical(raw) ?? Vocabulary.Normalize(raw);
            if (Vocabulary.Canonical(raw) is null && !other.Contains(k)) unknown.Add(raw.Trim());
            else kinds.Add(k);
        }
        if (unknown.Count > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Unknown kind {string.Join(", ", unknown)}. Use: {string.Join(", ", Kinds)}.");

        // Only invalid kinds given: nothing matches, rather than everything.
        var rows = kinds.Count == 0 && unknown.Count > 0 ? [] : Select(run, kinds, layers);
        var units = Geometry.OutputUnits(run, OnPingDocument()?.RhinoDocument ?? global::Rhino.RhinoDoc.ActiveDoc);
        if (units.Remark is { } remark) AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, remark);
        da.SetDataList(0, rows.Select(r => r.Pair.B is Guid b
            ? Geometry.ToGoo(run.B.GeometryOf(b), units.B)
            : Geometry.ToGoo(run.A.GeometryOf(r.Pair.A!.Value), units.A)));
        da.SetDataList(1, rows.Select(r => r.Info));
        Message = Describe.Plural(rows.Count, "object");
    }

    public sealed record Row(ObjectPair Pair, string Info);

    /// <summary>One row per object, in change-set order, combining every matching change on it.</summary>
    public static List<Row> Select(DiffRun run, IReadOnlyCollection<string> kinds, LayerPatterns layers)
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
                if (set.Flags.Count > 0 && !Describe.IsPairing(set)) text += $" [flagged: {Describe.Flags(set.Flags)}]";
                list.Add(text);
            }
        return [.. order.Select(p => new Row(p, Info(run, p, changes[p])))];
    }

    private static string Info(DiffRun run, ObjectPair pair, List<string> changes)
    {
        var record = pair.B is Guid b ? run.ObjectB(b) : run.ObjectA(pair.A!.Value);
        string layer = (pair.B is Guid lb ? run.LayerOfB(lb) : run.LayerOfA(pair.A!.Value)) ?? "?";
        string text = $"{Describe.Object(record, (pair.B ?? pair.A)!.Value)} · {string.Join("; ", changes)} · {layer}";
        if (pair.A is Guid a && run.MatchOfA(a) is { } m)
            text += $" · {Describe.Plural(m.UnchangedProperties, "property", "properties")} unchanged · matched by {Describe.Evidence(m)}";
        return text;
    }

    private static bool Matches(ChangeSet s, string kind) => kind switch
    {
        "added" => s.Kind == ChangeKind.Added,
        "removed" => s.Kind == ChangeKind.Removed,
        "modified" => s.Kind == ChangeKind.Modified && !Describe.IsPairing(s),
        "flagged" => s.Flags.Count > 0,
        "needslook" => s.NeedsLook,
        "review" => Describe.IsPairing(s) || s.Flags.Contains(Detectors.FuzzyMatch), // a fuzzy pair, changed or not
        _ => (s.Kind == ChangeKind.Modified && s.Change is { } c && Vocabulary.KindsOf(kind).Contains(c.Property))
            || s.Flags.Any(f => Vocabulary.Normalize(f) == kind),
    };
}
