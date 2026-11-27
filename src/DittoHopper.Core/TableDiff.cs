namespace DittoHopper.Core;

/// <summary>
/// Matches the tables and reports their changes as root-cause events. Layers: id → full path → unique leaf
/// name → inferred from where matched objects went (after object matching). Blocks, materials and linetypes:
/// id → unique name. Groups: id → membership.
/// </summary>
internal static class TableDiff
{
    public static void MatchTables(DiffContext ctx)
    {
        MatchUnique(ctx, ctx.Layers, s => s.Layers, l => l.Id, (_, l) => l.Id.ToString(), TableMatchedBy.Id);
        MatchUnique(ctx, ctx.Layers, s => s.Layers, l => l.Id, (s, l) => s.LayerPath[l.Id], TableMatchedBy.Path);
        MatchUnique(ctx, ctx.Layers, s => s.Layers, l => l.Id, (_, l) => l.Name, TableMatchedBy.Name);
        MatchByIdThenName(ctx, ctx.Blocks, s => s.Blocks, x => x.Id, x => x.Name);
        MatchByIdThenName(ctx, ctx.Materials, s => s.Materials, x => x.Id, x => x.Name);
        MatchByIdThenName(ctx, ctx.Linetypes, s => s.Linetypes, x => x.Id, x => x.Name);
    }

    /// <summary>
    /// Layer step 4. An unmatched A layer is the new B layer that holds at least LayerInferShare of its matched
    /// objects. Otherwise, if its objects spread over two or more new B layers that together hold that share,
    /// it was split. Strongest shares claim first, so the result doesn't depend on input order.
    /// </summary>
    public static void InferLayers(DiffContext ctx, IReadOnlyList<Pair> pairs)
    {
        double threshold = ctx.Settings.LayerInferShare;
        var flows = new Dictionary<Guid, Dictionary<Guid, int>>();
        foreach (var p in pairs)
        {
            if (ctx.Layers.HasA(p.A.LayerId) || !ctx.A.LayerById.ContainsKey(p.A.LayerId) || !ctx.B.LayerById.ContainsKey(p.B.LayerId))
                continue;
            if (!flows.TryGetValue(p.A.LayerId, out var flow)) flows[p.A.LayerId] = flow = [];
            flow[p.B.LayerId] = flow.GetValueOrDefault(p.B.LayerId) + 1;
        }

        var ordered = flows.OrderBy(f => ctx.A.LayerPath[f.Key], StringComparer.Ordinal).ToList();
        var inferred = ordered
            .Select(f =>
            {
                var best = f.Value.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First();
                return (A: f.Key, B: best.Key, Share: best.Value / (double)f.Value.Values.Sum());
            })
            .Where(c => c.Share >= threshold)
            .OrderByDescending(c => c.Share);   // stable sort: ties stay in A path order
        foreach (var c in inferred)
            if (!ctx.Layers.HasA(c.A) && !ctx.Layers.HasB(c.B))
                ctx.Layers.Add(c.A, c.B, TableMatchedBy.Inferred, c.Share);

        foreach (var (aLayer, flow) in ordered)
        {
            if (ctx.Layers.HasA(aLayer))
                continue;
            var targets = flow
                .Where(kv => !ctx.Layers.HasB(kv.Key) && !ctx.SplitTargetLayers.Contains(kv.Key))
                .OrderBy(kv => ctx.B.LayerPath[kv.Key], StringComparer.Ordinal)
                .Select(kv => (kv.Key, kv.Value)).ToList();
            int total = flow.Values.Sum();
            if (targets.Count < 2 || targets.Sum(t => t.Value) / (double)total < threshold)
                continue;
            ctx.Splits[aLayer] = new LayerSplit(targets, total);
            ctx.SplitTargetLayers.UnionWith(targets.Select(t => t.Key));
        }
    }

    /// <summary>Groups match by id, then by membership: the new B group sharing the most matched members (Jaccard ≥ LayerInferShare).</summary>
    public static void MatchGroups(DiffContext ctx, IReadOnlyList<Pair> pairs)
    {
        MatchUnique(ctx, ctx.Groups, s => s.Groups, g => g.Id, (_, g) => g.Id.ToString(), TableMatchedBy.Id);

        var aToB = pairs.ToDictionary(p => p.A.Id, p => p.B.Id);
        var candidates = new List<(Guid A, Guid B, double Jaccard)>();
        foreach (var ga in ctx.A.Groups)
        {
            if (ctx.Groups.HasA(ga.Id))
                continue;
            var membersA = ctx.A.GroupMembers.GetValueOrDefault(ga.Id) ?? [];
            var overlap = new Dictionary<Guid, int>();
            foreach (var member in membersA)
                if (aToB.TryGetValue(member, out var bId))
                    foreach (var gb in ctx.B.ObjectById[bId].GroupIds.Distinct())
                        if (!ctx.Groups.HasB(gb) && ctx.B.GroupById.ContainsKey(gb))
                            overlap[gb] = overlap.GetValueOrDefault(gb) + 1;
            foreach (var (gb, shared) in overlap)
            {
                int union = membersA.Count + ctx.B.GroupMembers[gb].Count - shared;
                double jaccard = shared / (double)union;
                if (jaccard >= ctx.Settings.LayerInferShare)
                    candidates.Add((ga.Id, gb, jaccard));
            }
        }
        foreach (var c in candidates.OrderByDescending(c => c.Jaccard).ThenBy(c => c.A).ThenBy(c => c.B))
            if (!ctx.Groups.HasA(c.A) && !ctx.Groups.HasB(c.B))
                ctx.Groups.Add(c.A, c.B, TableMatchedBy.Members, c.Jaccard);
    }

    public static void BuildEvents(DiffContext ctx)
    {
        LayerEvents(ctx);
        BlockEvents(ctx);
        var t = ctx.Settings.Toggles;
        SimpleEvents(ctx, ctx.Materials, s => s.Materials, s => s.MaterialById, s => s.MaterialUsers, t.Material);
        SimpleEvents(ctx, ctx.Linetypes, s => s.Linetypes, s => s.LinetypeById, s => s.LinetypeUsers, t.Linetype);
        GroupEvents(ctx);
    }

    private static void LayerEvents(DiffContext ctx)
    {
        var (a, b, t) = (ctx.A, ctx.B, ctx.Settings.Toggles);
        foreach (var la in a.Layers)
        {
            string label = a.LayerPath[la.Id];
            if (ctx.Layers.TryGet(la.Id, out var m))
            {
                var lb = b.LayerById[m.B];
                bool inferred = m.By == TableMatchedBy.Inferred;
                int n = b.OnLayer.GetValueOrDefault(lb.Id);
                void Ev(TableEventKind kind, PropertyKind? property, string? from, string? to) =>
                    Emit(ctx, TableKind.Layer, kind, la.Id, lb.Id, label, n, property, from, to, inferred, m.Confidence);

                if (la.Name != lb.Name)
                    Ev(TableEventKind.Renamed, null, la.Name, lb.Name);
                bool sameParent = la.ParentId is Guid pa
                    ? lb.ParentId is Guid pb && ctx.Layers.Map(pa) == pb
                    : lb.ParentId is null;
                if (!sameParent)
                    Ev(TableEventKind.Reparented, null, a.ParentPath(la), b.ParentPath(lb));
                if (t.Color && la.Color != lb.Color)
                    Ev(TableEventKind.PropertyChanged, PropertyKind.Color, Format.Color(la.Color), Format.Color(lb.Color));
                if (t.Linetype && !ctx.Linetypes.Same(la.LinetypeId, lb.LinetypeId))
                    Ev(TableEventKind.PropertyChanged, PropertyKind.Linetype, a.LinetypeLabel(la.LinetypeId), b.LinetypeLabel(lb.LinetypeId));
                if (t.Material && !ctx.Materials.Same(la.MaterialId, lb.MaterialId))
                    Ev(TableEventKind.PropertyChanged, PropertyKind.Material, a.MaterialLabel(la.MaterialId), b.MaterialLabel(lb.MaterialId));
                if (t.PrintWidth && la.PrintWidth != lb.PrintWidth)
                    Ev(TableEventKind.PropertyChanged, PropertyKind.PrintWidth, Format.Number(la.PrintWidth), Format.Number(lb.PrintWidth));
                if (la.Visible != lb.Visible)
                    Ev(TableEventKind.PropertyChanged, PropertyKind.Visible, Format.Bool(la.Visible), Format.Bool(lb.Visible));
                if (la.Locked != lb.Locked)
                    Ev(TableEventKind.PropertyChanged, PropertyKind.Locked, Format.Bool(la.Locked), Format.Bool(lb.Locked));
            }
            else if (ctx.Splits.TryGetValue(la.Id, out var split))
            {
                var shares = split.Targets
                    .Select(x => new SplitShare(x.B, b.LayerPath[x.B], x.Count, x.Count / (double)split.Total)).ToList();
                Emit(ctx, TableKind.Layer, TableEventKind.Split, la.Id, null, label, a.OnLayer.GetValueOrDefault(la.Id),
                    inferred: true, confidence: split.Targets.Sum(x => x.Count) / (double)split.Total, shares: shares);
            }
            else
            {
                ctx.RemovedEvent[(TableKind.Layer, la.Id)] =
                    Emit(ctx, TableKind.Layer, TableEventKind.Removed, la.Id, null, label, a.OnLayer.GetValueOrDefault(la.Id));
            }
        }
        foreach (var lb in b.Layers)
            if (!ctx.Layers.HasB(lb.Id) && !ctx.SplitTargetLayers.Contains(lb.Id))
                ctx.AddedEvent[(TableKind.Layer, lb.Id)] =
                    Emit(ctx, TableKind.Layer, TableEventKind.Added, null, lb.Id, b.LayerPath[lb.Id], b.OnLayer.GetValueOrDefault(lb.Id));
    }

    private static void BlockEvents(DiffContext ctx)
    {
        var (a, b) = (ctx.A, ctx.B);
        foreach (var da in a.Blocks)
        {
            if (ctx.Blocks.TryGet(da.Id, out var m))
            {
                var db = b.BlockById[m.B];
                int n = b.InstancesUsing.GetValueOrDefault(db.Id);
                if (da.Name != db.Name)
                    Emit(ctx, TableKind.Block, TableEventKind.Renamed, da.Id, db.Id, da.Name, n, from: da.Name, to: db.Name);
                if (ctx.Settings.Toggles.BlockDefinition && da.ContentHash != db.ContentHash)
                    ctx.ContentEvent[db.Id] =
                        Emit(ctx, TableKind.Block, TableEventKind.PropertyChanged, da.Id, db.Id, da.Name, n, PropertyKind.Content);
            }
            else
            {
                ctx.RemovedEvent[(TableKind.Block, da.Id)] =
                    Emit(ctx, TableKind.Block, TableEventKind.Removed, da.Id, null, da.Name, a.InstancesUsing.GetValueOrDefault(da.Id));
            }
        }
        foreach (var db in b.Blocks)
            if (!ctx.Blocks.HasB(db.Id))
                ctx.AddedEvent[(TableKind.Block, db.Id)] =
                    Emit(ctx, TableKind.Block, TableEventKind.Added, null, db.Id, db.Name, b.InstancesUsing.GetValueOrDefault(db.Id));
    }

    private static void SimpleEvents(DiffContext ctx, TableMap map, Func<Side, IReadOnlyList<TableRecord>> table,
        Func<Side, Dictionary<Guid, TableRecord>> byId, Func<Side, Dictionary<Guid, int>> users, bool compareContent)
    {
        var (a, b) = (ctx.A, ctx.B);
        foreach (var ra in table(a))
        {
            if (map.TryGet(ra.Id, out var m))
            {
                var rb = byId(b)[m.B];
                int n = users(b).GetValueOrDefault(rb.Id);
                if (ra.Name != rb.Name)
                    Emit(ctx, map.Kind, TableEventKind.Renamed, ra.Id, rb.Id, ra.Name, n, from: ra.Name, to: rb.Name);
                if (compareContent && ra.ContentHash != rb.ContentHash)
                    Emit(ctx, map.Kind, TableEventKind.PropertyChanged, ra.Id, rb.Id, ra.Name, n, PropertyKind.Content);
            }
            else
            {
                ctx.RemovedEvent[(map.Kind, ra.Id)] =
                    Emit(ctx, map.Kind, TableEventKind.Removed, ra.Id, null, ra.Name, users(a).GetValueOrDefault(ra.Id));
            }
        }
        foreach (var rb in table(b))
            if (!map.HasB(rb.Id))
                ctx.AddedEvent[(map.Kind, rb.Id)] =
                    Emit(ctx, map.Kind, TableEventKind.Added, null, rb.Id, rb.Name, users(b).GetValueOrDefault(rb.Id));
    }

    private static void GroupEvents(DiffContext ctx)
    {
        var (a, b) = (ctx.A, ctx.B);
        foreach (var ga in a.Groups)
        {
            string label = a.GroupLabel(ga.Id);
            if (ctx.Groups.TryGet(ga.Id, out var m))
            {
                var gb = b.GroupById[m.B];
                if ((ga.Name ?? "") != (gb.Name ?? ""))
                    Emit(ctx, TableKind.Group, TableEventKind.Renamed, ga.Id, gb.Id, label, b.GroupMembers.GetValueOrDefault(gb.Id)?.Count ?? 0,
                        from: ga.Name, to: gb.Name, inferred: m.By == TableMatchedBy.Members, confidence: m.Confidence);
            }
            else
            {
                ctx.RemovedEvent[(TableKind.Group, ga.Id)] =
                    Emit(ctx, TableKind.Group, TableEventKind.Removed, ga.Id, null, label, a.GroupMembers.GetValueOrDefault(ga.Id)?.Count ?? 0);
            }
        }
        foreach (var gb in b.Groups)
            if (!ctx.Groups.HasB(gb.Id))
                ctx.AddedEvent[(TableKind.Group, gb.Id)] =
                    Emit(ctx, TableKind.Group, TableEventKind.Added, null, gb.Id, b.GroupLabel(gb.Id), b.GroupMembers.GetValueOrDefault(gb.Id)?.Count ?? 0);
    }

    private static string Emit(DiffContext ctx, TableKind table, TableEventKind kind, Guid? a, Guid? b, string label, int affected,
        PropertyKind? property = null, string? from = null, string? to = null, bool inferred = false, double confidence = 1,
        IReadOnlyList<SplitShare>? shares = null)
    {
        string id = StableId.From("ev", table.ToString(), kind.ToString(), a?.ToString(), b?.ToString(), property?.ToString());
        ctx.Events.Add(new TableEvent
        {
            Id = id, Table = table, Kind = kind, A = a, B = b, Label = label, Property = property, From = from, To = to,
            Shares = shares ?? [], Inferred = inferred, Confidence = confidence, AffectedObjects = affected,
        });
        return id;
    }

    private static void MatchByIdThenName<T>(DiffContext ctx, TableMap map, Func<Side, IReadOnlyList<T>> items, Func<T, Guid> id, Func<T, string> name)
    {
        MatchUnique(ctx, map, items, id, (_, x) => id(x).ToString(), TableMatchedBy.Id);
        MatchUnique(ctx, map, items, id, (_, x) => name(x), TableMatchedBy.Name);
    }

    /// <summary>Pairs unmatched entries whose key is unique in both whole tables.</summary>
    private static void MatchUnique<T>(DiffContext ctx, TableMap map, Func<Side, IReadOnlyList<T>> items, Func<T, Guid> id,
        Func<Side, T, string?> key, TableMatchedBy by)
    {
        var inA = UniqueKeys(ctx.A, items(ctx.A), key);
        var inB = UniqueKeys(ctx.B, items(ctx.B), key);
        foreach (var (k, x) in inA.OrderBy(kv => id(kv.Value)))
            if (!map.HasA(id(x)) && inB.TryGetValue(k, out var y) && !map.HasB(id(y)))
                map.Add(id(x), id(y), by);
    }

    private static Dictionary<string, T> UniqueKeys<T>(Side side, IReadOnlyList<T> items, Func<Side, T, string?> key)
    {
        var unique = new Dictionary<string, T>(StringComparer.Ordinal);
        var duplicate = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in items)
            if (key(side, x) is { Length: > 0 } k && !unique.TryAdd(k, x))
                duplicate.Add(k);
        foreach (var k in duplicate) unique.Remove(k);
        return unique;
    }
}
