using System.Globalization;

namespace DittoHopper.Core;

/// <summary>
/// Entry point of the diff engine: a pure function from two snapshots and settings to a <see cref="DiffResult"/>.
/// Pipeline: match tables (id, path, name) → match objects (passes 1–3) → infer layers and match groups from
/// where the objects went → table events → compare pairs through the table maps → group into change sets.
/// </summary>
public static class ModelDiff
{
    /// <param name="geometry">Answers deviation and point questions for geometry ladder steps 2–5. Without it,
    /// every pair whose geometry hash differs is <see cref="GeometryLevel.Unclassified"/>.</param>
    public static DiffResult Diff(ModelSnapshot a, ModelSnapshot b, DiffSettings? settings = null, IGeometryService? geometry = null)
    {
        var ctx = new DiffContext(a, b, settings ?? new DiffSettings(), geometry is not null);
        TableDiff.MatchTables(ctx);
        var pairs = ObjectMatcher.Run(ctx);
        TableDiff.InferLayers(ctx, pairs);
        TableDiff.MatchGroups(ctx, pairs);
        TableDiff.BuildEvents(ctx);
        var compared = Comparer.Run(ctx, pairs, geometry);

        return new DiffResult
        {
            A = a.Info with { Skipped = [.. a.Info.Skipped.Order(StringComparer.Ordinal)] },
            B = b.Info with { Skipped = [.. b.Info.Skipped.Order(StringComparer.Ordinal)] },
            Header = ctx.Header,
            Settings = ctx.Settings,
            TableMatches = ctx.AllTableMaps
                .SelectMany(map => map.Entries.Select(e => new TableMatch(map.Kind, e.A, e.B, e.By, e.Confidence)))
                .OrderBy(m => m.Table).ThenBy(m => m.A).ToList(),
            TableEvents = ctx.Events
                .OrderBy(e => e.Table).ThenBy(e => e.Label, StringComparer.Ordinal).ThenBy(e => e.Kind)
                .ThenBy(e => e.Property).ThenBy(e => e.Id, StringComparer.Ordinal).ToList(),
            Matches = compared.Matches,
            Geometry = compared.Geometry,
            ChangeSets = ChangeSets.Build(ctx, compared.Rows, pairs),
            UnchangedObjects = compared.UnchangedObjects,
        };
    }
}

/// <summary>An A layer whose matched objects spread over new B layers: target → count, out of <paramref name="Total"/>.</summary>
internal sealed record LayerSplit(IReadOnlyList<(Guid B, int Count)> Targets, int Total);

/// <summary>A matched object pair, before comparing.</summary>
internal readonly record struct Pair(ObjectRecord A, ObjectRecord B, MatchedBy By, double Confidence);

/// <summary>Shared state of one diff run.</summary>
internal sealed class DiffContext
{
    public readonly Side A, B;
    public readonly DiffSettings Settings;
    public readonly DiffHeader Header;
    /// <summary>Multiplies B lengths into A's units.</summary>
    public readonly double Scale;

    public readonly TableMap Layers = new(TableKind.Layer), Blocks = new(TableKind.Block),
        Materials = new(TableKind.Material), Linetypes = new(TableKind.Linetype), Groups = new(TableKind.Group);

    /// <summary>Split A layers, and every B layer that is a split target.</summary>
    public readonly Dictionary<Guid, LayerSplit> Splits = [];
    public readonly HashSet<Guid> SplitTargetLayers = [];

    public readonly List<TableEvent> Events = [];
    /// <summary>Removed events by table and A id; added events by table and B id; block content events by B id.</summary>
    public readonly Dictionary<(TableKind, Guid), string> RemovedEvent = [], AddedEvent = [];
    public readonly Dictionary<Guid, string> ContentEvent = [];

    public DiffContext(ModelSnapshot a, ModelSnapshot b, DiffSettings settings, bool hasGeometryService = false)
    {
        A = new Side(a);
        B = new Side(b);
        Settings = settings;
        if (settings.ShapeTolerance is double s && !(s > 0 && double.IsFinite(s)))
            throw new ArgumentOutOfRangeException(nameof(settings), "ShapeTolerance must be a positive number, or null.");
        (Header, Scale) = MakeHeader(a.Info, b.Info, hasBlocks: a.Blocks.Count + b.Blocks.Count > 0);
        double grid = Math.Max(a.Info.HashGrid, b.Info.HashGrid * Scale);
        HashProvesUnchanged = ShapeTolerance >= grid * (1 - 1e-9);
        if (!HashProvesUnchanged)
        {
            string consequence = hasGeometryService
                ? "pairs with equal geometry hashes are also compared by shape."
                : "equal geometry hashes count as unchanged, so smaller differences are not reported.";
            Header = Header with { Notes = [.. Header.Notes, Inv($"Shape tolerance {ShapeTolerance} is finer than the hash grid {grid}: {consequence}")] };
        }
    }

    /// <summary>
    /// True when equal snapped hashes prove a pair unchanged: the hash grid is no coarser than the shape
    /// tolerance (the default, where both are the absolute tolerance).
    /// </summary>
    public readonly bool HashProvesUnchanged;

    /// <summary>Ladder step 1: the same kind and the same canonical hash, on matching grids.</summary>
    public bool SameGeometryHash(ObjectRecord a, ObjectRecord b) =>
        Header.HashGridsMatch && a.Kind == b.Kind && a.Geometry.Hash == b.Geometry.Hash;

    public IEnumerable<TableMap> AllTableMaps => [Layers, Blocks, Materials, Linetypes, Groups];

    public double Tolerance => Header.Tolerance;

    /// <summary>Decides whether a geometry deviation is a real change: the setting, else the tolerance.</summary>
    public double ShapeTolerance => Settings.ShapeTolerance ?? Tolerance;

    private static (DiffHeader, double) MakeHeader(SnapshotInfo a, SnapshotInfo b, bool hasBlocks)
    {
        var notes = new List<string>();
        double scale = 1;
        if (a.Units != b.Units)
        {
            if (Units.MetersPer(a.Units) is double ma && Units.MetersPer(b.Units) is double mb)
            {
                scale = mb / ma;
                notes.Add(Inv($"Units differ: A is {a.Units}, B is {b.Units}. B is converted to {a.Units}."));
            }
            else
            {
                notes.Add(Inv($"Units differ: A is {a.Units}, B is {b.Units}, which can't be converted. B is compared unscaled."));
            }
        }

        double tolB = b.AbsoluteTolerance * scale;
        double tol = Math.Max(a.AbsoluteTolerance, tolB);
        if (Math.Abs(a.AbsoluteTolerance - tolB) > 1e-9 * tol)
            notes.Add(Inv($"Tolerances differ: A {a.AbsoluteTolerance}, B {tolB} (in {a.Units}). Using the larger, {tol}."));

        // Grids compare as lengths: a 1 mm grid and a 0.001 m grid snap the same points to the same integers.
        bool gridsMatch = Units.MetersPer(a.Units) is double ga && Units.MetersPer(b.Units) is double gb
            ? Math.Abs(a.HashGrid * ga - b.HashGrid * gb) <= 1e-12 * Math.Max(a.HashGrid * ga, b.HashGrid * gb)
            : a.Units == b.Units && a.HashGrid == b.HashGrid;
        if (!gridsMatch)
        {
            notes.Add(Inv($"Hash grids differ: A {a.HashGrid} {a.Units}, B {b.HashGrid} {b.Units}. Geometry-hash matching (pass 3) is skipped and every pair's geometry is compared by shape."));
            if (hasBlocks)
                notes.Add("Block definition content could not be compared, because it is hashed on those grids.");
        }

        notes.AddRange(a.Skipped.Order(StringComparer.Ordinal).Select(s => "A skipped: " + s));
        notes.AddRange(b.Skipped.Order(StringComparer.Ordinal).Select(s => "B skipped: " + s));
        return (new DiffHeader { Units = a.Units, Tolerance = tol, HashGridsMatch = gridsMatch, Notes = notes }, scale);
    }

    public static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>One side's snapshot, sorted by id and indexed, with derived lookups.</summary>
internal sealed class Side
{
    public readonly IReadOnlyList<LayerRecord> Layers;
    public readonly IReadOnlyList<BlockRecord> Blocks;
    public readonly IReadOnlyList<TableRecord> Materials, Linetypes;
    public readonly IReadOnlyList<GroupRecord> Groups;
    public readonly IReadOnlyList<ObjectRecord> Objects;

    public readonly Dictionary<Guid, LayerRecord> LayerById;
    public readonly Dictionary<Guid, BlockRecord> BlockById;
    public readonly Dictionary<Guid, TableRecord> MaterialById, LinetypeById;
    public readonly Dictionary<Guid, GroupRecord> GroupById;
    public readonly Dictionary<Guid, ObjectRecord> ObjectById;

    public readonly Dictionary<Guid, string> LayerPath = [];
    /// <summary>Object counts per layer; top-level instances per definition (direct or nested); users per material, linetype, group.</summary>
    public readonly Dictionary<Guid, int> OnLayer = [], InstancesUsing = [], MaterialUsers = [], LinetypeUsers = [];
    public readonly Dictionary<Guid, List<Guid>> GroupMembers = [];
    public readonly ILookup<Guid, ObjectRecord> ObjectsOnLayer;
    private readonly Dictionary<Guid, HashSet<Guid>> closures = [];

    public Side(ModelSnapshot s)
    {
        Layers = Sorted(s.Layers, x => x.Id, out LayerById, "layer");
        Blocks = Sorted(s.Blocks, x => x.Id, out BlockById, "block");
        Materials = Sorted(s.Materials, x => x.Id, out MaterialById, "material");
        Linetypes = Sorted(s.Linetypes, x => x.Id, out LinetypeById, "linetype");
        Groups = Sorted(s.Groups, x => x.Id, out GroupById, "group");
        Objects = Sorted(s.Objects, x => x.Id, out ObjectById, "object");

        foreach (var layer in Layers)
            LayerPath[layer.Id] = BuildPath(layer);
        ObjectsOnLayer = Objects.ToLookup(o => o.LayerId);

        foreach (var o in Objects)
        {
            Increment(OnLayer, o.LayerId);
            if (o.BlockDefinitionId is Guid def)
                foreach (var d in Closure(def)) Increment(InstancesUsing, d);
            if (Effective(o.Material, l => l.MaterialId, o.LayerId) is Guid m) Increment(MaterialUsers, m);
            if (Effective(o.Linetype, l => l.LinetypeId, o.LayerId) is Guid lt) Increment(LinetypeUsers, lt);
            foreach (var g in o.GroupIds.Distinct())
            {
                if (!GroupMembers.TryGetValue(g, out var list)) GroupMembers[g] = list = [];
                list.Add(o.Id);
            }
        }
    }

    /// <summary>The definition and every definition nested in it, at any depth.</summary>
    public HashSet<Guid> Closure(Guid def)
    {
        if (closures.TryGetValue(def, out var set)) return set;
        set = [];
        var stack = new Stack<Guid>([def]);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (set.Add(d) && BlockById.TryGetValue(d, out var block))
                foreach (var n in block.Nested) stack.Push(n.DefinitionId);
        }
        return closures[def] = set;
    }

    public string LayerLabel(Guid id) => LayerPath.TryGetValue(id, out var p) ? p : id.ToString();
    public string? ParentPath(LayerRecord l) => l.ParentId is Guid p ? LayerLabel(p) : null;
    public string BlockLabel(Guid id) => BlockById.TryGetValue(id, out var b) ? b.Name : id.ToString();
    public string MaterialLabel(Guid id) => RefLabel(MaterialById, id);
    public string LinetypeLabel(Guid id) => RefLabel(LinetypeById, id);
    public string GroupLabel(Guid id) =>
        GroupById.TryGetValue(id, out var g) && !string.IsNullOrEmpty(g.Name) ? g.Name : id.ToString();

    private static string RefLabel(Dictionary<Guid, TableRecord> table, Guid id) =>
        id == Guid.Empty ? "(default)" : table.TryGetValue(id, out var r) ? r.Name : id.ToString();

    private Guid? Effective(Sourced<Guid> v, Func<LayerRecord, Guid> fromLayer, Guid layerId) => v.Source switch
    {
        ValueSource.ByObject => v.Value,
        ValueSource.ByLayer when LayerById.TryGetValue(layerId, out var l) => fromLayer(l),
        _ => null,
    };

    private string BuildPath(LayerRecord layer)
    {
        var names = new List<string>();
        var seen = new HashSet<Guid>();
        for (var l = layer; l is not null; l = l.ParentId is Guid p ? LayerById.GetValueOrDefault(p) : null)
        {
            if (!seen.Add(l.Id))
                throw new InvalidDataException($"Layer '{layer.Name}' ({layer.Id}) has a parent cycle.");
            names.Add(l.Name);
        }
        names.Reverse();
        return string.Join("::", names);
    }

    private static void Increment(Dictionary<Guid, int> counts, Guid id) => counts[id] = counts.GetValueOrDefault(id) + 1;

    private static List<T> Sorted<T>(IReadOnlyList<T> items, Func<T, Guid> id, out Dictionary<Guid, T> byId, string what)
    {
        byId = new Dictionary<Guid, T>(items.Count);
        foreach (var item in items)
            if (!byId.TryAdd(id(item), item))
                throw new InvalidDataException($"The snapshot has two {what} records with id {id(item)}.");
        return [.. items.OrderBy(id)];
    }
}

/// <summary>One table's A → B map.</summary>
internal sealed class TableMap(TableKind kind)
{
    public readonly record struct Entry(Guid A, Guid B, TableMatchedBy By, double Confidence);

    private readonly Dictionary<Guid, Entry> byA = [];
    private readonly HashSet<Guid> claimedB = [];

    public TableKind Kind => kind;
    public IEnumerable<Entry> Entries => byA.Values;

    public void Add(Guid a, Guid b, TableMatchedBy by, double confidence = 1)
    {
        byA.Add(a, new Entry(a, b, by, confidence));
        claimedB.Add(b);
    }

    public bool HasA(Guid a) => byA.ContainsKey(a);
    public bool HasB(Guid b) => claimedB.Contains(b);
    public bool TryGet(Guid a, out Entry e) => byA.TryGetValue(a, out e);
    public Guid? Map(Guid a) => byA.TryGetValue(a, out var e) ? e.B : null;

    /// <summary>True when an A reference and a B reference point at the same entry. <see cref="Guid.Empty"/> is the default entry.</summary>
    public bool Same(Guid a, Guid b) => a == Guid.Empty ? b == Guid.Empty : Map(a) == b;
}
