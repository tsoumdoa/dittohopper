namespace DittoHopper.Core;

/// <summary>
/// The matching cascade, cheapest and most certain first. Each pass sees only objects no earlier pass claimed.
/// Within a pass, ties (several candidates for one key) break by: same mapped layer, then nearest centroid,
/// then GUID order.
/// </summary>
internal sealed class ObjectMatcher
{
    private readonly DiffContext ctx;
    private readonly List<Pair> pairs = [];
    private readonly HashSet<Guid> claimedA = [], claimedB = [];

    private ObjectMatcher(DiffContext ctx) => this.ctx = ctx;

    public static List<Pair> Run(DiffContext ctx)
    {
        var m = new ObjectMatcher(ctx);
        m.ByStableKey();
        m.ByGuid();
        if (ctx.Header.HashGridsMatch)
            m.ByGeometryHash();
        // Passes 4 (shape signature + nearest) and 5 (fuzzy score) go here, on the still-unclaimed objects.
        return [.. m.pairs.OrderBy(p => p.A.Id)];
    }

    private IEnumerable<ObjectRecord> FreeA => ctx.A.Objects.Where(o => !claimedA.Contains(o.Id));
    private IEnumerable<ObjectRecord> FreeB => ctx.B.Objects.Where(o => !claimedB.Contains(o.Id));

    /// <summary>Pass 1: the stable-key user text value; else a name that is unique in both whole models.</summary>
    private void ByStableKey()
    {
        string key = ctx.Settings.StableKey;
        string? Uid(ObjectRecord o) => o.UserText.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        PairByKey(FreeA, FreeB, Uid, MatchedBy.StableKey, 1);

        var uniqueA = UniqueNames(ctx.A.Objects);
        var uniqueB = UniqueNames(ctx.B.Objects);
        foreach (var a in FreeA.ToList())
        {
            if (a.Name is not { Length: > 0 } name || !uniqueA.ContainsKey(name) || !uniqueB.TryGetValue(name, out var b))
                continue;
            // Two different stable keys mean two different objects, whatever their names say.
            if (!claimedB.Contains(b.Id) && (Uid(a) is null || Uid(b) is null))
                Claim(a, b, MatchedBy.StableKey, 1);
        }
    }

    /// <summary>Pass 2: the same Rhino object id, and the same object kind.</summary>
    private void ByGuid()
    {
        foreach (var a in FreeA.ToList())
            if (ctx.B.ObjectById.TryGetValue(a.Id, out var b) && !claimedB.Contains(b.Id) && a.Kind == b.Kind)
                Claim(a, b, MatchedBy.Guid, 1);
    }

    /// <summary>Pass 3: an exact canonical geometry hash and the same kind. Only valid when both sides hashed on the same grid.</summary>
    private void ByGeometryHash() =>
        PairByKey(FreeA, FreeB, o => $"{o.Kind}:{o.Geometry.Hash:x16}", MatchedBy.GeometryHash, 0.99);

    private void PairByKey(IEnumerable<ObjectRecord> freeA, IEnumerable<ObjectRecord> freeB, Func<ObjectRecord, string?> key,
        MatchedBy by, double confidence)
    {
        var groupsB = freeB.Where(o => key(o) is not null).GroupBy(o => key(o)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var groupsA = freeA.Where(o => key(o) is not null).GroupBy(o => key(o)!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (var groupA in groupsA)
            if (groupsB.TryGetValue(groupA.Key, out var candidatesB))
                foreach (var (a, b) in BreakTies([.. groupA], candidatesB))
                    Claim(a, b, by, confidence);
    }

    /// <summary>Pairs candidates that all share one key: same mapped layer first, then nearest centroid, then GUID order.</summary>
    private IEnumerable<(ObjectRecord A, ObjectRecord B)> BreakTies(List<ObjectRecord> a, List<ObjectRecord> b)
    {
        if (a.Count == 1 && b.Count == 1)
            return [(a[0], b[0])];
        var options =
            from x in a
            from y in b
            orderby ctx.Layers.Map(x.LayerId) == y.LayerId ? 0 : 1,
                Vec3.Distance(x.Geometry.Centroid, y.Geometry.Centroid * ctx.Scale),
                x.Id, y.Id
            select (x, y);
        var usedA = new HashSet<Guid>();
        var usedB = new HashSet<Guid>();
        var chosen = new List<(ObjectRecord, ObjectRecord)>();
        foreach (var (x, y) in options)
            if (!usedA.Contains(x.Id) && !usedB.Contains(y.Id))
            {
                usedA.Add(x.Id);
                usedB.Add(y.Id);
                chosen.Add((x, y));
            }
        return chosen;
    }

    private void Claim(ObjectRecord a, ObjectRecord b, MatchedBy by, double confidence)
    {
        claimedA.Add(a.Id);
        claimedB.Add(b.Id);
        pairs.Add(new Pair(a, b, by, confidence));
    }

    private static Dictionary<string, ObjectRecord> UniqueNames(IReadOnlyList<ObjectRecord> objects) =>
        objects.Where(o => o.Name is { Length: > 0 }).GroupBy(o => o.Name!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
}
