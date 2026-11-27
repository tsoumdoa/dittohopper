namespace DittoHopper.Core;

/// <summary>
/// The matching cascade, cheapest and most certain first. Each pass sees only objects no earlier pass claimed.
/// Within passes 1–3, ties (several candidates for one key) break the same way: same GUID and kind, same kind, same
/// mapped layer, nearest centroid (to tolerance), most equal own attributes, then GUID order. Passes 2, 4 and 5
/// compare within a kind family (<see cref="ObjectKinds.Family"/>), and no pass pairs two different stable keys.
/// </summary>
internal sealed class ObjectMatcher
{
    /// <summary>Measures agree to this fraction on top of their tolerance: float noise, never a shape difference.</summary>
    private const double RelativeNoise = 1e-6;

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
        m.ByShapeSignature();
        m.ByFuzzyScore();
        return [.. m.pairs.OrderBy(p => p.A.Id)];
    }

    private IEnumerable<ObjectRecord> FreeA => ctx.A.Objects.Where(o => !claimedA.Contains(o.Id));
    private IEnumerable<ObjectRecord> FreeB => ctx.B.Objects.Where(o => !claimedB.Contains(o.Id));

    /// <summary>Pass 1: the stable-key user text value; else a name that is unique in both whole models.</summary>
    private void ByStableKey()
    {
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

    /// <summary>Pass 2: the same Rhino object id, and the same kind family (an extrusion converted in place keeps its pair).</summary>
    private void ByGuid()
    {
        foreach (var a in FreeA.ToList())
            if (ctx.B.ObjectById.TryGetValue(a.Id, out var b) && !claimedB.Contains(b.Id) && SameFamily(a, b))
                Claim(a, b, MatchedBy.Guid, 1);
    }

    /// <summary>
    /// Pass 3: an exact canonical geometry hash and the same kind. Only valid when both sides hashed on the same grid.
    /// Two objects that carry different stable keys are different objects, so they never pair here.
    /// </summary>
    private void ByGeometryHash() =>
        PairByKey(FreeA, FreeB, o => $"{o.Kind}:{o.Geometry.Hash:x16}", MatchedBy.GeometryHash, 0.99, KeysAllow);

    /// <summary>
    /// Pass 4: the same position-free shape, then the nearest candidate by centroid. The same shape is the same kind
    /// family and topology summary, and length, boundary length, area and volume within tolerance; for instances,
    /// the same definition through the block map. A pair is claimed only when each is the other's nearest within the
    /// move limit (the object's own size), and no other candidate is within twice the distance. When the shape
    /// repeats within reach on either side (another free candidate within the move limit), the move must also be
    /// within the shape tolerance: a re-bake jittered across the hash grid, never a guess between identical
    /// neighbours. Coincident candidates are told apart by their own attributes, then GUID order. Anything less
    /// certain is left to pass 5 (marked for review) or reported as removed + added.
    /// </summary>
    private void ByShapeSignature()
    {
        var groupsB = FreeB.Where(HasShape).GroupBy(b => ShapeKey(b, fromA: false)).ToDictionary(g => g.Key, g => g.ToList());
        var found = new List<(ObjectRecord A, ObjectRecord B)>();
        foreach (var groupA in FreeA.Where(HasShape).GroupBy(a => ShapeKey(a, fromA: true)))
        {
            if (!groupsB.TryGetValue(groupA.Key, out var poolB))
                continue;
            var poolA = groupA.ToList();
            var indexA = new SortedPoints([.. poolA.Select(a => a.Geometry.Centroid)]);
            var indexB = new SortedPoints([.. poolB.Select(b => b.Geometry.Centroid * ctx.Scale)]);
            foreach (var a in poolA)
            {
                double limit = Math.Max(ctx.Tolerance, Diagonal(a.Geometry.BBox));
                if (Unique(a.Geometry.Centroid, limit, poolB, indexB, b => SameShape(a, b), b => Agreement(a, b)) is not { } hit)
                    continue;
                var b = hit.Match;
                if (Unique(b.Geometry.Centroid * ctx.Scale, limit, poolA, indexA, x => SameShape(x, b), x => Agreement(x, b)) is not { } back
                    || back.Match.Id != a.Id || ((hit.Repeats || back.Repeats) && hit.Distance > ctx.ShapeTolerance))
                    continue;
                found.Add((a, b));
            }
        }
        // Mutual nearest pairs never share an object, so the claiming order can't change the outcome.
        foreach (var (a, b) in found)
            Claim(a, b, MatchedBy.ShapeSignature, 0.9);
    }

    /// <summary>
    /// The only candidate within <paramref name="limit"/> of <paramref name="at"/>, or null when there is none or it
    /// is ambiguous: another candidate lies within twice its distance. Candidates within tolerance are coincident;
    /// the most equal own attributes, then GUID order, decide between them. Repeats: more than one candidate lies
    /// within the limit.
    /// </summary>
    private (ObjectRecord Match, double Distance, bool Repeats)? Unique(Vec3 at, double limit, List<ObjectRecord> pool,
        SortedPoints index, Func<ObjectRecord, bool> fits, Func<ObjectRecord, int> agreement)
    {
        var (k, d) = index.Nearest(at, limit, i => fits(pool[i]));
        if (k < 0)
            return null;
        double tol = ctx.Tolerance;
        bool repeats = index.Within(at, limit).Count(i => fits(pool[i])) > 1;
        var close = index.Within(at, d <= tol ? tol : 2 * d).Where(i => fits(pool[i])).ToList();
        if (close.Count == 1)
            return (pool[k], d, repeats);
        if (d > tol)
            return null;
        return (close.Select(i => pool[i]).OrderByDescending(agreement).ThenBy(o => o.Id).First(), d, repeats);
    }

    /// <summary>
    /// Objects with a shape to compare and a place to compare it at: anything with a length, area or volume, and
    /// block instances, but only with a non-empty bounding box. An instance read from a file has no world bounds
    /// (its centroid is the origin), so it could otherwise pair across any distance.
    /// </summary>
    private static bool HasShape(ObjectRecord o) =>
        (o.Kind == ObjectKind.InstanceReference || o.Geometry.Length > 0 || o.Geometry.Area > 0 || o.Geometry.Volume > 0)
        && Diagonal(o.Geometry.BBox) > 0;

    private readonly record struct Shape(ObjectKind Family, bool Closed, int Faces, int BoundaryLoops, int Components, Guid? Definition);

    /// <summary>The exact half of the shape signature. An A instance's definition goes through the block map.</summary>
    private Shape ShapeKey(ObjectRecord o, bool fromA)
    {
        var g = o.Geometry;
        Guid? def = o.BlockDefinitionId is Guid d ? fromA ? ctx.Blocks.Map(d) ?? Guid.Empty : d : null;
        return new Shape(ObjectKinds.Family(o.Kind), g.Closed, g.Faces, g.BoundaryLoops, g.Components, def);
    }

    /// <summary>The measured half: length, boundary length, area and volume equal within the tolerance (scaled by size), and stable keys that allow it.</summary>
    private bool SameShape(ObjectRecord a, ObjectRecord b)
    {
        var (x, y, s) = (a.Geometry, b.Geometry, ctx.Scale);
        double tol = ctx.Tolerance, size = Math.Max(Diagonal(x.BBox), Diagonal(y.BBox) * s);
        return Near(x.Length, y.Length * s, tol) && Near(x.BoundaryLength, y.BoundaryLength * s, tol) && Near(x.Area, y.Area * s * s, tol * size)
            && Near(x.Volume, y.Volume * s * s * s, tol * size * size) && KeysAllow(a, b);
    }

    private static bool Near(double p, double q, double tolerance) =>
        Math.Abs(p - q) <= tolerance + RelativeNoise * Math.Max(Math.Abs(p), Math.Abs(q));

    private static double Diagonal(BBox box) => Vec3.Distance(box.Min, box.Max);

    /// <summary>
    /// Pass 5: a weighted score within a kind family: kind 0.15, bounding-box overlap 0.35, size ratio 0.25, same
    /// layer 0.15, name 0.10. Boxes are padded by the tolerance plus 1% of their size, so flat and thin objects can
    /// overlap. Only objects with a shape (as in pass 4) whose boxes overlap are scored, and the best scores claim
    /// first. A pair needs at least <see cref="DiffSettings.FuzzyThreshold"/>, keeps its score as confidence, and needs review.
    /// </summary>
    private void ByFuzzyScore()
    {
        var poolB = FreeB.Where(HasShape).Select(b => (B: b, Box: Padded(b.Geometry.BBox, ctx.Scale)))
            .OrderBy(x => x.Box.Min.X).ThenBy(x => x.B.Id).ToList();
        // A sweep along X: a B box can overlap A only if it starts no more than the widest B box before A does.
        double widest = poolB.Count == 0 ? 0 : poolB.Max(x => x.Box.Max.X - x.Box.Min.X);
        var candidates = new List<(ObjectRecord A, ObjectRecord B, double Score)>();
        foreach (var a in FreeA.Where(HasShape))
        {
            var boxA = Padded(a.Geometry.BBox, 1);
            int start = 0, end = poolB.Count;
            while (start < end)
            {
                int mid = (start + end) / 2;
                if (poolB[mid].Box.Min.X < boxA.Min.X - widest) start = mid + 1;
                else end = mid;
            }
            for (int i = start; i < poolB.Count; i++)
            {
                var (b, boxB) = poolB[i];
                if (boxB.Min.X > boxA.Max.X)
                    break;
                if (!SameFamily(a, b) || !KeysAllow(a, b) || Overlap(boxA, boxB) is not (> 0 and var overlap))
                    continue;
                double score = FuzzyScore(a, b, overlap);
                if (score >= ctx.Settings.FuzzyThreshold)
                    candidates.Add((a, b, score));
            }
        }
        foreach (var (a, b, score) in candidates.OrderByDescending(c => c.Score).ThenBy(c => c.A.Id).ThenBy(c => c.B.Id))
            if (!claimedA.Contains(a.Id) && !claimedB.Contains(b.Id))
                Claim(a, b, MatchedBy.Fuzzy, score);
    }

    private double FuzzyScore(ObjectRecord a, ObjectRecord b, double overlap)
    {
        double kind = a.Kind == b.Kind ? 1 : 0.5;
        double layer = ctx.Layers.Map(a.LayerId) == b.LayerId ? 1 : 0;
        double name = (Sourcing.Norm(a.Name), Sourcing.Norm(b.Name)) switch
        {
            (null, null) => 0.5,
            var (x, y) when x == y => 1,
            _ => 0,
        };
        return Math.Round(0.15 * kind + 0.35 * overlap + 0.25 * SizeRatio(a, b) + 0.15 * layer + 0.10 * name, 3);
    }

    /// <summary>Smaller over larger of the first measure both have: volume, area, then length. Instances: 1 for the same definition. Nothing to measure: 0.</summary>
    private double SizeRatio(ObjectRecord a, ObjectRecord b)
    {
        if (a.BlockDefinitionId is Guid da)
            return b.BlockDefinitionId is Guid db && ctx.Blocks.Map(da) == db ? 1 : 0;
        var (x, y, s) = (a.Geometry, b.Geometry, ctx.Scale);
        foreach (var (p, q) in new[] { (x.Volume, y.Volume * s * s * s), (x.Area, y.Area * s * s), (x.Length, y.Length * s) })
            if (p > 0 && q > 0)
                return Math.Min(p, q) / Math.Max(p, q);
        return 0;
    }

    private BBox Padded(BBox box, double scale)
    {
        var (min, max) = (box.Min * scale, box.Max * scale);
        double pad = ctx.Tolerance + 0.01 * Vec3.Distance(min, max);
        var p = new Vec3(pad, pad, pad);
        return new BBox(min - p, max + p);
    }

    /// <summary>Intersection over union of two boxes' volumes.</summary>
    private static double Overlap(BBox a, BBox b)
    {
        static double Volume(Vec3 v) => v.X * v.Y * v.Z;
        var lo = new Vec3(Math.Max(a.Min.X, b.Min.X), Math.Max(a.Min.Y, b.Min.Y), Math.Max(a.Min.Z, b.Min.Z));
        var hi = new Vec3(Math.Min(a.Max.X, b.Max.X), Math.Min(a.Max.Y, b.Max.Y), Math.Min(a.Max.Z, b.Max.Z));
        if (hi.X <= lo.X || hi.Y <= lo.Y || hi.Z <= lo.Z)
            return 0;
        double inter = Volume(hi - lo);
        return inter / (Volume(a.Max - a.Min) + Volume(b.Max - b.Min) - inter);
    }

    private static bool SameFamily(ObjectRecord a, ObjectRecord b) => ObjectKinds.Family(a.Kind) == ObjectKinds.Family(b.Kind);

    /// <summary>Two different stable keys mean two different objects, whatever else they share.</summary>
    private bool KeysAllow(ObjectRecord a, ObjectRecord b) => Uid(a) is not string ka || Uid(b) is not string kb || ka == kb;

    private string? Uid(ObjectRecord o) => o.UserText.TryGetValue(ctx.Settings.StableKey, out var v) && v.Length > 0 ? v : null;

    private void PairByKey(IEnumerable<ObjectRecord> freeA, IEnumerable<ObjectRecord> freeB, Func<ObjectRecord, string?> key,
        MatchedBy by, double confidence, Func<ObjectRecord, ObjectRecord, bool>? allowed = null)
    {
        var groupsB = freeB.Where(o => key(o) is not null).GroupBy(o => key(o)!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var groupsA = freeA.Where(o => key(o) is not null).GroupBy(o => key(o)!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (var groupA in groupsA)
            if (groupsB.TryGetValue(groupA.Key, out var candidatesB))
                foreach (var (a, b) in BreakTies([.. groupA], candidatesB, allowed ?? ((_, _) => true)))
                    Claim(a, b, by, confidence);
    }

    /// <summary>
    /// Pairs candidates that all share one key, best-ranked pair first: same GUID and kind (a copy keeps its
    /// original's key but not its GUID), same kind, same mapped layer, nearest centroid, most equal own attributes, GUID
    /// order. Distances are counted in tolerance steps, so coincident candidates tie and attributes decide.
    /// The best pairs are claimed greedily; this never forces a weaker pairing just to pair more objects,
    /// because an honest added + removed is better than a wrong pair.
    /// </summary>
    private IEnumerable<(ObjectRecord A, ObjectRecord B)> BreakTies(List<ObjectRecord> a, List<ObjectRecord> b,
        Func<ObjectRecord, ObjectRecord, bool> allowed)
    {
        if (a.Count == 1 && b.Count == 1)
            return allowed(a[0], b[0]) ? [(a[0], b[0])] : [];
        var options =
            from x in a
            from y in b
            where allowed(x, y)
            orderby x.Id == y.Id && x.Kind == y.Kind ? 0 : 1,
                x.Kind == y.Kind ? 0 : 1,
                ctx.Layers.Map(x.LayerId) == y.LayerId ? 0 : 1,
                DistanceSteps(x, y),
                -Agreement(x, y),
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

    private double DistanceSteps(ObjectRecord x, ObjectRecord y)
    {
        double d = Vec3.Distance(x.Geometry.Centroid, y.Geometry.Centroid * ctx.Scale);
        return ctx.Tolerance > 0 ? Math.Round(d / ctx.Tolerance) : d;
    }

    /// <summary>How many own attributes are equal: name, color, material, linetype, user text (stable keys aside).</summary>
    private int Agreement(ObjectRecord x, ObjectRecord y)
    {
        var s = ctx.Settings;
        bool sameText = x.UserText.Count(kv => !s.IsStableKey(kv.Key)) == y.UserText.Count(kv => !s.IsStableKey(kv.Key))
            && x.UserText.All(kv => s.IsStableKey(kv.Key) || (y.UserText.TryGetValue(kv.Key, out var v) && v == kv.Value));
        return (Sourcing.Norm(x.Name) == Sourcing.Norm(y.Name) ? 1 : 0)
            + (Sourcing.Same(x.Color, y.Color, (p, q) => p == q) ? 1 : 0)
            + (Sourcing.Same(x.Material, y.Material, ctx.Materials.Same) ? 1 : 0)
            + (Sourcing.Same(x.Linetype, y.Linetype, ctx.Linetypes.Same) ? 1 : 0)
            + (sameText ? 1 : 0);
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
