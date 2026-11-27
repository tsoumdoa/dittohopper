using System.Globalization;

namespace DittoHopper.Core;

/// <summary>One property difference on one pair, with the table event that caused it, if any.</summary>
internal readonly record struct ChangeRow(PropertyChange Change, string? Cause, ObjectPair Pair);

/// <summary>
/// Compares each matched pair property by property, through the table maps. Appearance compares value and
/// source; a value that comes from the layer belongs to the layer, so it is never an object change.
/// </summary>
internal sealed class Comparer
{
    public sealed record Output(List<ChangeRow> Rows, List<Match> Matches, List<GeometryChange> Geometry, int UnchangedObjects);

    private readonly DiffContext ctx;
    private readonly List<ChangeRow> rows = [];
    private readonly List<GeometryChange> geometry = [];

    private Comparer(DiffContext ctx) => this.ctx = ctx;

    public static Output Run(DiffContext ctx, IReadOnlyList<Pair> pairs)
    {
        var c = new Comparer(ctx);
        var matches = new List<Match>(pairs.Count);
        int unchangedObjects = 0;
        foreach (var p in pairs)
        {
            var (changed, unchanged) = c.Compare(p.A, p.B);
            matches.Add(new Match(p.A.Id, p.B.Id, p.By, p.Confidence, unchanged));
            if (changed == 0) unchangedObjects++;
        }
        return new Output(c.rows, matches, c.geometry, unchangedObjects);
    }

    private (int Changed, int Unchanged) Compare(ObjectRecord a, ObjectRecord b)
    {
        var (sa, sb, t) = (ctx.A, ctx.B, ctx.Settings.Toggles);
        int changed = 0, unchanged = 0;
        var pair = new ObjectPair(a.Id, b.Id);

        void Emit(PropertyChange change, string? cause = null)
        {
            rows.Add(new ChangeRow(change, cause, pair));
            changed++;
        }
        void Check(bool enabled, bool same, Func<PropertyChange> change, string? cause = null)
        {
            if (!enabled) return;
            if (same) unchanged++;
            else Emit(change(), cause);
        }
        void CheckSourced<T>(bool enabled, PropertyKind kind, Sourced<T> x, Sourced<T> y, Func<T, T, bool> same,
            Func<T, string> fromLabel, Func<T, string> toLabel, TableKind? table = null)
        {
            bool equal = x.Source == y.Source && (x.Source != ValueSource.ByObject || same(x.Value, y.Value));
            string? cause = table is TableKind k && x.Value is Guid xv && y.Value is Guid yv
                ? Cause(k, x.Source == ValueSource.ByObject ? xv : null, y.Source == ValueSource.ByObject ? yv : null)
                : null;
            Check(enabled, equal, () => new PropertyChange(kind, null,
                x.Source == ValueSource.ByObject ? fromLabel(x.Value) : null,
                y.Source == ValueSource.ByObject ? toLabel(y.Value) : null,
                x.Source, y.Source), cause);
        }

        Check(t.Layer, SameLayer(a.LayerId, b.LayerId),
            () => new PropertyChange(PropertyKind.Layer, null, sa.LayerLabel(a.LayerId), sb.LayerLabel(b.LayerId)),
            Cause(TableKind.Layer, a.LayerId, b.LayerId));
        Check(t.Name, Norm(a.Name) == Norm(b.Name), () => new PropertyChange(PropertyKind.Name, null, Norm(a.Name), Norm(b.Name)));
        CheckSourced(t.Color, PropertyKind.Color, a.Color, b.Color, (x, y) => x == y, Format.Color, Format.Color);
        CheckSourced(t.Material, PropertyKind.Material, a.Material, b.Material, ctx.Materials.Same,
            sa.MaterialLabel, sb.MaterialLabel, TableKind.Material);
        CheckSourced(t.Linetype, PropertyKind.Linetype, a.Linetype, b.Linetype, ctx.Linetypes.Same,
            sa.LinetypeLabel, sb.LinetypeLabel, TableKind.Linetype);
        CheckSourced(t.PrintWidth, PropertyKind.PrintWidth, a.PrintWidth, b.PrintWidth, (x, y) => x == y, Format.Number, Format.Number);
        Check(t.DisplayOrder, a.DisplayOrder == b.DisplayOrder, () => new PropertyChange(PropertyKind.DisplayOrder, null,
            Format.Number(a.DisplayOrder), Format.Number(b.DisplayOrder)));

        if (t.UserText)
        {
            // The stable key is identity, not data: it is never reported as a change.
            var keys = a.UserText.Keys.Union(b.UserText.Keys).Where(k => k != ctx.Settings.StableKey).Order(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                string? x = a.UserText.GetValueOrDefault(key), y = b.UserText.GetValueOrDefault(key);
                Check(true, x == y, () => new PropertyChange(PropertyKind.UserText, key, x, y));
            }
        }

        if (t.GroupMembership)
            CompareGroups(a, b, Check);

        if (a.Kind == ObjectKind.InstanceReference && b.Kind == ObjectKind.InstanceReference)
        {
            Guid? da = a.BlockDefinitionId, db = b.BlockDefinitionId;
            Check(t.BlockDefinition, da is Guid x ? db is Guid y && ctx.Blocks.Map(x) == y : db is null,
                () => new PropertyChange(PropertyKind.Block, null, da is Guid x ? sa.BlockLabel(x) : null, db is Guid y ? sb.BlockLabel(y) : null),
                Cause(TableKind.Block, da, db));
            var xb = b.InstanceXform?.ScaleTranslation(ctx.Scale);
            Check(t.Placement, a.InstanceXform is null ? xb is null : xb is not null && a.InstanceXform.AlmostEquals(xb, ctx.Tolerance),
                () => new PropertyChange(PropertyKind.Transform, null, a.InstanceXform?.ToString(), xb?.ToString()));

            // A definition edit is one table event; only edits nested inside the instance's definition are
            // reported on the instance, each with the inner definition's event as its root cause.
            if (t.BlockDefinition && db is Guid def)
                foreach (var inner in sb.Closure(def).Where(d => d != def).OrderBy(sb.BlockLabel, StringComparer.Ordinal).ThenBy(d => d))
                    if (ctx.ContentEvent.TryGetValue(inner, out var ev))
                        Emit(new PropertyChange(PropertyKind.BlockContent, sb.BlockLabel(inner), null, null), ev);
        }
        else if (t.Placement || t.Shape)
        {
            if (ctx.Header.HashGridsMatch && a.Kind == b.Kind && a.Geometry.Hash == b.Geometry.Hash)
            {
                unchanged++;
            }
            else
            {
                // Step 1 of the geometry ladder. Steps 2–5 (M2) classify the pair and fill in the change.
                geometry.Add(new GeometryChange { A = a.Id, B = b.Id, Level = GeometryLevel.Unclassified });
                Emit(new PropertyChange(PropertyKind.Geometry, null, null, null));
            }
        }

        return (changed, unchanged);
    }

    private void CompareGroups(ObjectRecord a, ObjectRecord b, Action<bool, bool, Func<PropertyChange>, string?> check)
    {
        var mapped = a.GroupIds.Select(ctx.Groups.Map).ToList();
        var inB = b.GroupIds.ToHashSet();
        bool same = mapped.All(g => g is not null) && mapped.Select(g => g!.Value).ToHashSet().SetEquals(inB);
        string? cause = a.GroupIds.Where(g => !ctx.Groups.HasA(g))
            .Select(g => ctx.RemovedEvent.GetValueOrDefault((TableKind.Group, g)))
            .Concat(b.GroupIds.Where(g => !mapped.Contains(g)).Select(g => ctx.AddedEvent.GetValueOrDefault((TableKind.Group, g))))
            .Where(e => e is not null).Order(StringComparer.Ordinal).FirstOrDefault();
        check(true, same, () => new PropertyChange(PropertyKind.Groups, null, GroupList(ctx.A, a.GroupIds), GroupList(ctx.B, b.GroupIds)), cause);
    }

    private static string? GroupList(Side side, IReadOnlyList<Guid> groups) =>
        groups.Count == 0 ? null : string.Join(", ", groups.Distinct().Select(side.GroupLabel).Order(StringComparer.Ordinal));

    /// <summary>The same layer through the map, or a split target of the A layer (the split event covers that move).</summary>
    private bool SameLayer(Guid a, Guid b) =>
        ctx.Layers.Map(a) == b || (ctx.Splits.TryGetValue(a, out var split) && split.Targets.Any(t => t.B == b));

    /// <summary>The event behind a reference change: the A entry was removed, or else the B entry is new.</summary>
    private string? Cause(TableKind table, Guid? a, Guid? b) =>
        (a is Guid x ? ctx.RemovedEvent.GetValueOrDefault((table, x)) : null)
        ?? (b is Guid y ? ctx.AddedEvent.GetValueOrDefault((table, y)) : null);

    private static string? Norm(string? s) => string.IsNullOrEmpty(s) ? null : s;
}

/// <summary>Groups change rows and leftover objects into change sets with stable ids and a stable order.</summary>
internal static class ChangeSets
{
    public static List<ChangeSet> Build(DiffContext ctx, List<ChangeRow> rows, IReadOnlyList<Pair> pairs)
    {
        var sets = new List<ChangeSet>();
        foreach (var g in rows.GroupBy(r => r.Change))
        {
            var c = g.Key;
            sets.Add(new ChangeSet
            {
                Id = StableId.From("cs", nameof(ChangeKind.Modified), c.Property.ToString(), c.Key, c.From, c.To,
                    c.FromSource?.ToString(), c.ToSource?.ToString()),
                Kind = ChangeKind.Modified,
                Change = c,
                Objects = [.. g.Select(r => r.Pair).OrderBy(p => p.A).ThenBy(p => p.B)],
                RootCauseEventId = g.Select(r => r.Cause).Where(e => e is not null).Order(StringComparer.Ordinal).FirstOrDefault(),
            });
        }

        var matchedA = pairs.Select(p => p.A.Id).ToHashSet();
        var matchedB = pairs.Select(p => p.B.Id).ToHashSet();
        foreach (var g in ctx.A.Objects.Where(o => !matchedA.Contains(o.Id)).GroupBy(o => o.LayerId))
            sets.Add(Leftovers(ChangeKind.Removed, ctx.A.LayerLabel(g.Key), g.Select(o => new ObjectPair(o.Id, null)),
                ctx.RemovedEvent.GetValueOrDefault((TableKind.Layer, g.Key))));
        foreach (var g in ctx.B.Objects.Where(o => !matchedB.Contains(o.Id)).GroupBy(o => o.LayerId))
            sets.Add(Leftovers(ChangeKind.Added, ctx.B.LayerLabel(g.Key), g.Select(o => new ObjectPair(null, o.Id)), null));

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

    private static ChangeSet Leftovers(ChangeKind kind, string layer, IEnumerable<ObjectPair> objects, string? cause) => new()
    {
        Id = StableId.From("cs", kind.ToString(), layer),
        Kind = kind,
        Layer = layer,
        Objects = [.. objects.OrderBy(p => p.A).ThenBy(p => p.B)],
        RootCauseEventId = cause,
    };
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
