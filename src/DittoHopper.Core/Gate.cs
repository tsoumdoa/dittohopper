namespace DittoHopper.Core;

/// <summary>The outcome of a QA gate: whether it passed, and each change that failed it (or what was checked).</summary>
public sealed record GateResult(string Layers, bool Pass, IReadOnlyList<string> Why);

/// <summary>
/// The QA gate behind Assert Unchanged and the review panel's Gate field: it passes when nothing on the given layers
/// changed. Every pattern must match a layer in A or B, so a typo fails it.
/// </summary>
public static class Gate
{
    /// <summary>The patterns that match no layer in A or B.</summary>
    public static IReadOnlyList<string> Unmatched(DiffIndex run, LayerPatterns glob) =>
        glob.Unmatched(run.LayerPathsA.Values.Concat(run.LayerPathsB.Values));

    /// <summary>Every change set and table event that touches a matching layer fails the gate, one line each.</summary>
    public static GateResult Check(DiffIndex run, LayerPatterns glob)
    {
        // Every pattern must match a layer: a typo beside a valid pattern would otherwise check less than asked.
        if (Unmatched(run, glob) is { Count: > 0 } unmatched)
            return new(glob.ToString(), false, [$"No layer in A or B matches {string.Join(", ", unmatched)}, so the gate can't check it. Check the spelling."]);

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
        if (why.Count > 0) return new(glob.ToString(), false, why);

        int checkedObjects = run.ModelA.Objects.Count(o => glob.Matches(run.LayerPathsA.GetValueOrDefault(o.LayerId)));
        return new(glob.ToString(), true, [$"Nothing on {glob} changed: {Describe.Plural(checkedObjects, "object")} checked."]);
    }

    private static bool Touches(DiffIndex run, TableEvent e, LayerPatterns glob)
    {
        if (glob.MatchesAll) return true;
        var (a, b) = (run.ModelA, run.ModelB);
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
                    return run.LayerPathsA.Values.Any(p => DiffIndex.IsUnder(p, pathA) && glob.Matches(p))
                        || run.LayerPathsB.Values.Any(p => DiffIndex.IsUnder(p, pathB) && glob.Matches(p));
                return glob.Matches(e.Label) || glob.Matches(pathA) || glob.Matches(pathB) || e.Shares.Any(s => glob.Matches(s.Label));
            case TableKind.Block:
                return OnA(o => ea is not null && o.BlockDefinitionId == ea) || OnB(o => eb is not null && o.BlockDefinitionId == eb);
            case TableKind.Group:
                return OnA(o => ea is Guid ga && o.GroupIds.Contains(ga)) || OnB(o => eb is Guid gb && o.GroupIds.Contains(gb));
            case TableKind.Material:
                return OnA(o => ea is Guid ma && DiffIndex.Uses(a, o, o.Material, l => l.MaterialId, ma))
                    || OnB(o => eb is Guid mb && DiffIndex.Uses(b, o, o.Material, l => l.MaterialId, mb));
            case TableKind.Linetype:
                return OnA(o => ea is Guid ta && DiffIndex.Uses(a, o, o.Linetype, l => l.LinetypeId, ta))
                    || OnB(o => eb is Guid tb && DiffIndex.Uses(b, o, o.Linetype, l => l.LinetypeId, tb));
            default:
                return true;
        }
    }
}
