using System.Globalization;

namespace DittoHopper.Core;

public enum LayerState { Untouched, Expected, ToCheck }

/// <summary>A layer row with its status: untouched, expected, or how many of its change sets need a look.</summary>
public sealed record LayerStatus(LayerRow Row, LayerState State, int ToCheck);

/// <summary>
/// Level 1 of the review UI, from a result alone: the summary sentence and each layer's status. Both follow the
/// review state, so marking a set intended (<see cref="DiffResult.WithReview"/>) updates them.
/// </summary>
public static class DiffSummary
{
    /// <summary>
    /// With a scope: "Outside Facade::*, 28 objects changed in 3 sets. 412 layers are untouched." (or "Nothing
    /// outside Facade::* changed."), plus "1 change set inside it looks suspicious." when one does. Without a
    /// scope: "2,104 of 2,416 objects are identical. 3 change sets look suspicious." Sets marked intended don't count.
    /// </summary>
    public static string Sentence(DiffResult r)
    {
        var look = r.ChangeSets.Where(s => s.NeedsLook).ToList();
        if (r.Settings.ScopeGlob is { } scope && !string.IsNullOrWhiteSpace(scope))
        {
            scope = scope.Trim();
            var outside = look.Where(s => s.Flags.Contains(Detectors.OutsideScope)).ToList();
            int objects = outside.SelectMany(s => s.Objects).Distinct().Count();
            int untouched = Layers(r).Count(l => l.State == LayerState.Untouched);
            string text = outside.Count == 0
                ? $"Nothing outside {scope} changed. "
                : $"Outside {scope}, {Count(objects, "object")} changed in {Count(outside.Count, "set")}. ";
            text += untouched == 1 ? "1 layer is untouched." : $"{Number(untouched)} layers are untouched.";
            int inside = look.Count - outside.Count;
            if (inside > 0)
                text += inside == 1 ? " 1 change set inside it looks suspicious." : $" {Number(inside)} change sets inside it look suspicious.";
            return text;
        }
        // Every A object is either matched or removed.
        int total = r.Matches.Count + r.ChangeSets.Where(s => s.Kind == ChangeKind.Removed).Sum(s => s.Objects.Count);
        string same = $"{Number(r.UnchangedObjects)} of {Count(total, "object")} {(r.UnchangedObjects == 1 ? "is" : "are")} identical.";
        return same + (look.Count == 1 ? " 1 change set looks suspicious." : $" {Number(look.Count)} change sets look suspicious.");
    }

    /// <summary>
    /// Every layer row with its state: <see cref="LayerState.Untouched"/> when no change set or table event touches
    /// it, <see cref="LayerState.ToCheck"/> when some of its sets need a look, else <see cref="LayerState.Expected"/>.
    /// </summary>
    public static IReadOnlyList<LayerStatus> Layers(DiffResult r)
    {
        var look = r.ChangeSets.Where(s => s.NeedsLook).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        return [.. r.Layers.Select(row =>
        {
            int toCheck = row.ChangeSets.Count(look.Contains);
            var state = row.ChangeSets.Count + row.Events.Count == 0 ? LayerState.Untouched
                : toCheck > 0 ? LayerState.ToCheck : LayerState.Expected;
            return new LayerStatus(row, state, toCheck);
        })];
    }

    private static string Count(int n, string noun) => $"{Number(n)} {noun}{(n == 1 ? "" : "s")}";

    private static string Number(int n) => n.ToString("N0", CultureInfo.InvariantCulture);
}

/// <summary>Builds the layer matrix rows: which change sets and table events touch each layer, on either side.</summary>
internal static class LayerRows
{
    public static List<LayerRow> Build(DiffContext ctx, IReadOnlyList<ChangeSet> sets, IReadOnlyList<TableEvent> events)
    {
        var rows = new List<(Guid? A, Guid? B, string Path, int Objects, SortedSet<string> Sets, SortedSet<string> Events)>();
        var rowOfA = new Dictionary<Guid, int>();
        var rowOfB = new Dictionary<Guid, int>();
        foreach (var la in ctx.A.Layers)
        {
            var b = ctx.Layers.Map(la.Id);
            rowOfA[la.Id] = rows.Count;
            if (b is Guid id) rowOfB[id] = rows.Count;
            rows.Add((la.Id, b, ctx.A.LayerPath[la.Id], ctx.A.OnLayer.GetValueOrDefault(la.Id), new(StringComparer.Ordinal), new(StringComparer.Ordinal)));
        }
        foreach (var lb in ctx.B.Layers.Where(l => !rowOfB.ContainsKey(l.Id)))
        {
            rowOfB[lb.Id] = rows.Count;
            rows.Add((null, lb.Id, ctx.B.LayerPath[lb.Id], ctx.B.OnLayer.GetValueOrDefault(lb.Id), new(StringComparer.Ordinal), new(StringComparer.Ordinal)));
        }

        void Touch(Dictionary<Guid, int> index, Guid? layer, Func<int, SortedSet<string>> list, string id)
        {
            if (layer is Guid l && index.TryGetValue(l, out int row)) list(row).Add(id);
        }
        foreach (var s in sets)
            foreach (var p in s.Objects)
            {
                if (p.A is Guid a) Touch(rowOfA, ctx.A.ObjectById[a].LayerId, i => rows[i].Sets, s.Id);
                if (p.B is Guid b) Touch(rowOfB, ctx.B.ObjectById[b].LayerId, i => rows[i].Sets, s.Id);
            }
        foreach (var e in events.Where(e => e.Table == TableKind.Layer))
        {
            Touch(rowOfA, e.A, i => rows[i].Events, e.Id);
            Touch(rowOfB, e.B, i => rows[i].Events, e.Id);
            foreach (var share in e.Shares) Touch(rowOfB, share.B, i => rows[i].Events, e.Id);
        }

        return [.. rows
            .Select(r => new LayerRow { A = r.A, B = r.B, Path = r.Path, Objects = r.Objects, ChangeSets = [.. r.Sets], Events = [.. r.Events] })
            .OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.A).ThenBy(r => r.B)];
    }
}
