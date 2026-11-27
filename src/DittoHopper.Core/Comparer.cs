using System.Globalization;

namespace DittoHopper.Core;

/// <summary>
/// One property difference on one pair, with the table event that explains it, if any. Identity holds the ids
/// behind a reference-valued change (layers, groups, definitions), so rows group by what changed, not by labels.
/// </summary>
internal readonly record struct ChangeRow(PropertyChange Change, string? Identity, string? Cause, ObjectPair Pair);

/// <summary>
/// Compares each matched pair property by property, through the table maps. Appearance compares value and
/// source; a value that comes from the layer belongs to the layer, so it is never an object change.
/// </summary>
internal sealed class Comparer
{
    /// <param name="Displacement">For each A object with a placement change: the farthest any corner of its box moved.</param>
    public sealed record Output(List<ChangeRow> Rows, List<Match> Matches, List<GeometryChange> Geometry, int UnchangedObjects,
        Dictionary<Guid, double> Displacement);

    private readonly DiffContext ctx;
    private readonly Dictionary<Guid, GeometryChange> ladder;
    private readonly List<ChangeRow> rows = [];
    private readonly List<GeometryChange> geometry = [];
    private readonly Dictionary<Guid, double> displacement = [];

    private Comparer(DiffContext ctx, Dictionary<Guid, GeometryChange> ladder) => (this.ctx, this.ladder) = (ctx, ladder);

    public static Output Run(DiffContext ctx, IReadOnlyList<Pair> pairs, IGeometryService? service)
    {
        var c = new Comparer(ctx, Ladder(ctx, pairs, service));
        var matches = new List<Match>(pairs.Count);
        int unchangedObjects = 0;
        foreach (var p in pairs)
        {
            var (changed, unchanged) = c.Compare(p.A, p.B);
            matches.Add(new Match(p.A.Id, p.B.Id, p.By, p.Confidence, unchanged));
            if (changed == 0) unchangedObjects++;
        }
        return new Output(c.rows, matches, c.geometry, unchangedObjects, c.displacement);
    }

    /// <summary>
    /// Geometry ladder steps 2–3 for every pair whose geometry hash differs (step 1), or whose hash is equal but
    /// its representation differs (Data-only by definition), keyed by A id. Only those
    /// pairs pay for deviation sampling, and they run in parallel: each geometry belongs to one pair.
    /// </summary>
    private static Dictionary<Guid, GeometryChange> Ladder(DiffContext ctx, IReadOnlyList<Pair> pairs, IGeometryService? service)
    {
        var t = ctx.Settings.Toggles;
        // Equal hashes prove "unchanged" only when the hash grid is no coarser than the shape tolerance;
        // otherwise a geometry service checks those pairs too.
        bool recheck = service is not null && !ctx.HashProvesUnchanged;
        var todo = pairs.Where(p => (t.Placement || t.Shape) && !BothInstances(p.A, p.B)
            && (recheck || !ctx.SameGeometryHash(p.A, p.B) || !SameRepresentation(p.A, p.B))).ToArray();
        var results = new GeometryChange[todo.Length];
        Parallel.For(0, todo.Length, i => results[i] = GeometryLadder.Classify(ctx, service, todo[i].A, todo[i].B));
        return todo.Select((p, i) => (p.A.Id, results[i])).ToDictionary(x => x.Id, x => x.Item2);
    }

    private static bool BothInstances(ObjectRecord a, ObjectRecord b) =>
        a.Kind == ObjectKind.InstanceReference && b.Kind == ObjectKind.InstanceReference;

    private static bool SameRepresentation(ObjectRecord a, ObjectRecord b) => a.Geometry.Representation == b.Geometry.Representation;

    private (int Changed, int Unchanged) Compare(ObjectRecord a, ObjectRecord b)
    {
        var (sa, sb, t) = (ctx.A, ctx.B, ctx.Settings.Toggles);
        int changed = 0, unchanged = 0;
        var pair = new ObjectPair(a.Id, b.Id);

        void Emit(PropertyChange change, string? cause = null, string? identity = null)
        {
            rows.Add(new ChangeRow(change, identity, cause, pair));
            changed++;
        }
        void Check(bool enabled, bool same, Func<PropertyChange> change, string? cause = null, string? identity = null)
        {
            if (!enabled) return;
            if (same) unchanged++;
            else Emit(change(), cause, identity);
        }
        void CheckSourced<T>(bool enabled, PropertyKind kind, Sourced<T> x, Sourced<T> y, Func<T, T, bool> same,
            Func<T, string> fromLabel, Func<T, string> toLabel, bool isReference = false)
        {
            string? Own(Sourced<T> v) => v.Source == ValueSource.ByObject ? v.Value?.ToString() : null;
            Check(enabled, Sourcing.Same(x, y, same), () => new PropertyChange(kind, null,
                x.Source == ValueSource.ByObject ? fromLabel(x.Value) : null,
                y.Source == ValueSource.ByObject ? toLabel(y.Value) : null,
                x.Source, y.Source), identity: isReference ? Ids(Own(x), Own(y)) : null);
        }

        // A layer change is the object's own move: even when its old layer was deleted, the delete didn't move it.
        Check(t.Layer, SameLayer(a.LayerId, b.LayerId),
            () => new PropertyChange(PropertyKind.Layer, null, sa.LayerLabel(a.LayerId), sb.LayerLabel(b.LayerId)),
            identity: Ids(a.LayerId.ToString(), b.LayerId.ToString()));
        Check(t.Name, Sourcing.Norm(a.Name) == Sourcing.Norm(b.Name),
            () => new PropertyChange(PropertyKind.Name, null, Sourcing.Norm(a.Name), Sourcing.Norm(b.Name)));
        CheckSourced(t.Color, PropertyKind.Color, a.Color, b.Color, (x, y) => x == y, Format.Color, Format.Color);
        CheckSourced(t.Material, PropertyKind.Material, a.Material, b.Material, ctx.Materials.Same,
            sa.MaterialLabel, sb.MaterialLabel, isReference: true);
        CheckSourced(t.Linetype, PropertyKind.Linetype, a.Linetype, b.Linetype, ctx.Linetypes.Same,
            sa.LinetypeLabel, sb.LinetypeLabel, isReference: true);
        CheckSourced(t.PrintWidth, PropertyKind.PrintWidth, a.PrintWidth, b.PrintWidth, (x, y) => x == y, Format.Number, Format.Number);
        Check(t.DisplayOrder, a.DisplayOrder == b.DisplayOrder, () => new PropertyChange(PropertyKind.DisplayOrder, null,
            Format.Number(a.DisplayOrder), Format.Number(b.DisplayOrder)));

        if (t.UserText)
        {
            // Stable keys are identity, not data: they are never reported as changes.
            var keys = a.UserText.Keys.Union(b.UserText.Keys).Where(k => !ctx.Settings.IsStableKey(k)).Order(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                string? x = a.UserText.GetValueOrDefault(key), y = b.UserText.GetValueOrDefault(key);
                Check(true, x == y, () => new PropertyChange(PropertyKind.UserText, key, x, y));
            }
        }

        if (t.GroupMembership)
        {
            var (same, cause) = CompareGroups(a, b);
            Check(true, same, () => new PropertyChange(PropertyKind.Groups, null, GroupList(sa, a.GroupIds), GroupList(sb, b.GroupIds)),
                cause, Ids(string.Join(",", a.GroupIds.Distinct().Order()), string.Join(",", b.GroupIds.Distinct().Order())));
        }

        if (BothInstances(a, b))
        {
            Guid? da = a.BlockDefinitionId, db = b.BlockDefinitionId;
            Check(t.BlockDefinition, da is Guid x ? db is Guid y && ctx.Blocks.Map(x) == y : db is null,
                () => new PropertyChange(PropertyKind.Block, null, da is Guid x ? sa.BlockLabel(x) : null, db is Guid y ? sb.BlockLabel(y) : null),
                identity: Ids(da?.ToString(), db?.ToString()));
            var (samePlace, placement, delta) = Placement(a.InstanceXform, b.InstanceXform?.ScaleTranslation(ctx.Scale));
            if (t.Placement && !samePlace && delta is not null)
                displacement[a.Id] = Displacement(a.Geometry.BBox, delta);
            Check(t.Placement, samePlace, placement);

            // A definition edit is one table event; only edits nested inside the instance's definition are
            // reported on the instance, each with the inner definition's event as its root cause. The inner
            // definition must be nested on both sides: an instance switched to another definition doesn't
            // inherit edits it never had.
            if (t.BlockDefinition && da is Guid defA && db is Guid defB)
            {
                var nestedInA = sa.Closure(defA).Where(d => d != defA).Select(ctx.Blocks.Map).OfType<Guid>().ToHashSet();
                foreach (var inner in sb.Closure(defB).Where(d => d != defB && nestedInA.Contains(d))
                             .OrderBy(sb.BlockLabel, StringComparer.Ordinal).ThenBy(d => d))
                    if (ctx.ContentEvent.TryGetValue(inner, out var ev))
                        Emit(new PropertyChange(PropertyKind.BlockContent, sb.BlockLabel(inner), null, null), ev, inner.ToString());
            }
        }
        else if (ladder.TryGetValue(a.Id, out var g)
            && !(g.Level == GeometryLevel.DataOnly && ctx.SameGeometryHash(a, b) && SameRepresentation(a, b)))
        {
            geometry.Add(g);
            // Data-only is the same shape: it is listed, but never a change. A rigid move is Placement, so equal
            // moves on many objects (and instances) share one change set; anything else is Shape.
            if (g.Level == GeometryLevel.DataOnly)
                unchanged++;
            else if (g.Level == GeometryLevel.Moved)
            {
                if (t.Placement)
                    displacement[a.Id] = Displacement(a.Geometry.BBox, g.Rigid!);
                Check(t.Placement, false, () => new PropertyChange(PropertyKind.Transform, null, null, g.Detail));
            }
            else
            {
                Check(t.Shape, false, () => new PropertyChange(PropertyKind.Geometry, null, null, ShapeLabel(g)));
            }
        }
        else if (t.Placement || t.Shape)
        {
            unchanged++; // step 1: equal geometry hashes (rechecked by shape above when the grid is coarse)
        }

        return (changed, unchanged);
    }

    /// <summary>
    /// Instance placement compares as a delta, B relative to A, so the same move of many instances is one
    /// change. The change describes the delta; a non-invertible A transform falls back to both matrices.
    /// </summary>
    private (bool Same, Func<PropertyChange> Change, Xform? Delta) Placement(Xform? xa, Xform? xb)
    {
        double tol = ctx.ShapeTolerance;
        if (xa?.Inverse() is { } inverse && xb is not null)
        {
            var delta = xb.After(inverse);
            return (delta.AlmostEquals(Xform.Identity, tol),
                () => new PropertyChange(PropertyKind.Transform, null, null, RigidFit.Describe(delta, ctx.Header.Units, tol)), delta);
        }
        return (xa is null ? xb is null : xb is not null && xa.AlmostEquals(xb, tol),
            () => new PropertyChange(PropertyKind.Transform, null, xa?.ToString(), xb?.ToString()), null);
    }

    /// <summary>The farthest any corner of the box moves under the transform.</summary>
    private static double Displacement(BBox box, Xform move)
    {
        double max = 0;
        foreach (double x in new[] { box.Min.X, box.Max.X })
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
                foreach (double z in new[] { box.Min.Z, box.Max.Z })
                {
                    var corner = new Vec3(x, y, z);
                    max = Math.Max(max, Vec3.Distance(move.Apply(corner), corner));
                }
        return max;
    }

    /// <summary>
    /// The shape change a set groups by: the ladder level, and for a topology change what broke. Index details
    /// and deviations stay on the object (<see cref="GeometryChange"/>), so equal kinds of edit share a set.
    /// </summary>
    private static string? ShapeLabel(GeometryChange g) => g.Level switch
    {
        GeometryLevel.Edited => g.Rigid is null ? "edited" : "moved + edited",
        GeometryLevel.Reshaped => "reshaped",
        GeometryLevel.TopologyChanged => "topology changed: " + g.Detail,
        _ => null,
    };

    /// <summary>
    /// Group membership through the group map. The cause is a group's removal or addition only when exactly
    /// one such event explains the whole difference: every A group still holds the object or is that deleted
    /// group, and every new B group is that added group.
    /// </summary>
    private (bool Same, string? Cause) CompareGroups(ObjectRecord a, ObjectRecord b)
    {
        var inB = b.GroupIds.ToHashSet();
        var mapped = new HashSet<Guid>();
        var events = new List<string>();
        bool same = true, explained = true;
        foreach (var g in a.GroupIds.Distinct())
        {
            if (ctx.Groups.Map(g) is Guid m)
            {
                mapped.Add(m);
                if (!inB.Contains(m)) same = explained = false;
            }
            else
            {
                same = false;
                if (ctx.RemovedEvent.TryGetValue((TableKind.Group, g), out var ev)) events.Add(ev);
                else explained = false;
            }
        }
        foreach (var g in inB.Where(g => !mapped.Contains(g)))
        {
            same = false;
            if (ctx.AddedEvent.TryGetValue((TableKind.Group, g), out var ev)) events.Add(ev);
            else explained = false;
        }
        return (same, explained && events.Count == 1 ? events[0] : null);
    }

    private static string? GroupList(Side side, IReadOnlyList<Guid> groups) =>
        groups.Count == 0 ? null : string.Join(", ", groups.Distinct().Select(side.GroupLabel).Order(StringComparer.Ordinal));

    /// <summary>The same layer through the map, or a split target of the A layer (the split event covers that move).</summary>
    private bool SameLayer(Guid a, Guid b) =>
        ctx.Layers.Map(a) == b || (ctx.Splits.TryGetValue(a, out var split) && split.Targets.Any(t => t.B == b));

    private static string Ids(string? from, string? to) => from + ">" + to;
}

/// <summary>Comparison rules shared by matching and comparing.</summary>
internal static class Sourcing
{
    /// <summary>Equal sources, and equal values when the value is the object's own. An inherited value belongs to its source.</summary>
    public static bool Same<T>(Sourced<T> x, Sourced<T> y, Func<T, T, bool> same) =>
        x.Source == y.Source && (x.Source != ValueSource.ByObject || same(x.Value, y.Value));

    public static string? Norm(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>
/// Groups change rows and leftover objects into change sets with stable ids and a stable order. Each object's
/// detector flags are part of its signature, so a flagged object never hides in an unflagged set.
/// </summary>
internal static class ChangeSets
{
    public static List<ChangeSet> Build(DiffContext ctx, List<ChangeRow> rows, IReadOnlyList<Pair> pairs, Flagger flags)
    {
        var sets = new List<ChangeSet>();
        // Labels are for display: rows group by the change, the ids behind it, its cause and its flags.
        foreach (var g in rows.GroupBy(r => (r.Change, r.Identity, r.Cause, Flags: Key(flags.ForRow(r)))))
        {
            var (c, identity, cause, flagKey) = g.Key;
            sets.Add(new ChangeSet
            {
                Id = StableId.From("cs", [nameof(ChangeKind.Modified), c.Property.ToString(), c.Key, c.From, c.To,
                    c.FromSource?.ToString(), c.ToSource?.ToString(), identity, cause, .. FlagPart(flagKey)]),
                Kind = ChangeKind.Modified,
                Change = c,
                Objects = [.. g.Select(r => r.Pair).OrderBy(p => p.A).ThenBy(p => p.B)],
                Flags = Split(flagKey),
                RootCauseEventId = cause,
            });
        }

        var matchedA = pairs.Select(p => p.A.Id).ToHashSet();
        var matchedB = pairs.Select(p => p.B.Id).ToHashSet();
        foreach (var g in ctx.A.Objects.Where(o => !matchedA.Contains(o.Id)).GroupBy(o => (o.LayerId, Flags: Key(flags.ForRemoved(o)))))
            sets.Add(Leftovers(ChangeKind.Removed, g.Key.LayerId, ctx.A.LayerLabel(g.Key.LayerId), g.Select(o => new ObjectPair(o.Id, null)),
                ctx.RemovedEvent.GetValueOrDefault((TableKind.Layer, g.Key.LayerId)), g.Key.Flags));
        foreach (var g in ctx.B.Objects.Where(o => !matchedB.Contains(o.Id)).GroupBy(o => (o.LayerId, Flags: Key(flags.ForAdded(o)))))
            sets.Add(Leftovers(ChangeKind.Added, g.Key.LayerId, ctx.B.LayerLabel(g.Key.LayerId), g.Select(o => new ObjectPair(null, o.Id)), null, g.Key.Flags));

        return [.. sets
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Change?.Property)
            .ThenBy(s => s.Change?.Key, StringComparer.Ordinal)
            .ThenBy(s => s.Change?.From, StringComparer.Ordinal)
            .ThenBy(s => s.Change?.To, StringComparer.Ordinal)
            .ThenBy(s => s.Change?.FromSource)
            .ThenBy(s => s.Change?.ToSource)
            .ThenBy(s => s.Layer, StringComparer.Ordinal)
            .ThenBy(s => s.Id, StringComparer.Ordinal)];
    }

    private static ChangeSet Leftovers(ChangeKind kind, Guid layerId, string layer, IEnumerable<ObjectPair> objects, string? cause, string flagKey) => new()
    {
        Id = StableId.From("cs", [kind.ToString(), layer, layerId.ToString(), cause, .. FlagPart(flagKey)]),
        Kind = kind,
        Layer = layer,
        Objects = [.. objects.OrderBy(p => p.A).ThenBy(p => p.B)],
        Flags = Split(flagKey),
        RootCauseEventId = cause,
    };

    private static string Key(IEnumerable<string> flags) => string.Join(",", flags.Order(StringComparer.Ordinal));
    private static string[] Split(string key) => key.Length == 0 ? [] : key.Split(',');
    /// <summary>Unflagged sets keep the ids they had before detectors existed.</summary>
    private static string?[] FlagPart(string key) => key.Length == 0 ? [] : [key];
}

/// <summary>Culture-invariant labels for change values.</summary>
internal static class Format
{
    public static string Color(int argb)
    {
        uint v = unchecked((uint)argb);
        return (v >> 24) == 0xFF ? "#" + (v & 0xFFFFFF).ToString("X6") : "#" + v.ToString("X8");
    }

    public static string Number(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    public static string Number(int v) => v.ToString(CultureInfo.InvariantCulture);
    public static string Bool(bool v) => v ? "true" : "false";
}
