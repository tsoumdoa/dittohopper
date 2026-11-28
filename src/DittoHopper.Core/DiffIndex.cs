namespace DittoHopper.Core;

/// <summary>
/// One diff with the two snapshot models it came from, so the shells can look up records, layer paths and how each
/// object fared. Immutable; lookups are built once, on first use. The Rhino adapter's DiffRun adds the geometry.
/// </summary>
public class DiffIndex
{
    private readonly Lazy<Index> index;

    public DiffIndex(DiffResult result, ModelSnapshot a, ModelSnapshot b)
    {
        Result = result;
        ModelA = a;
        ModelB = b;
        index = new Lazy<Index>(() => new Index(this));
    }

    public DiffResult Result { get; }
    public ModelSnapshot ModelA { get; }
    public ModelSnapshot ModelB { get; }

    public ObjectRecord? ObjectA(Guid id) => index.Value.ObjectsA.GetValueOrDefault(id);
    public ObjectRecord? ObjectB(Guid id) => index.Value.ObjectsB.GetValueOrDefault(id);

    /// <summary>Full path of an A object's layer.</summary>
    public string? LayerOfA(Guid id) => ObjectA(id) is { } o ? index.Value.LayersA.GetValueOrDefault(o.LayerId) : null;

    /// <summary>Full path of a B object's layer.</summary>
    public string? LayerOfB(Guid id) => ObjectB(id) is { } o ? index.Value.LayersB.GetValueOrDefault(o.LayerId) : null;

    /// <summary>Full layer paths by layer id.</summary>
    public IReadOnlyDictionary<Guid, string> LayerPathsA => index.Value.LayersA;
    public IReadOnlyDictionary<Guid, string> LayerPathsB => index.Value.LayersB;

    /// <summary>The match an A object is part of.</summary>
    public Match? MatchOfA(Guid a) => index.Value.Matches.GetValueOrDefault(a);

    /// <summary>The geometry ladder's verdict for a pair whose geometry hash differed.</summary>
    public GeometryChange? GeometryOf(ObjectPair pair) =>
        pair.A is Guid a && index.Value.Geometry.TryGetValue(a, out var g) && g.B == pair.B ? g : null;

    /// <summary>Every change set an object pair is in, in result order.</summary>
    public IReadOnlyList<ChangeSet> SetsOf(ObjectPair pair) => index.Value.SetsOf.TryGetValue(pair, out var sets) ? sets : [];

    /// <summary>Objects in A: matched or removed.</summary>
    public int ObjectsInA => Describe.ObjectsInA(Result);

    public IReadOnlyList<Guid> Added => index.Value.Added;
    public IReadOnlyList<Guid> Removed => index.Value.Removed;
    /// <summary>Modified pairs, each once, in change-set order. A pairing to review is not a modification.</summary>
    public IReadOnlyList<ObjectPair> Modified => index.Value.Modified;

    /// <summary>
    /// B objects that look different only because a table entry they take their appearance from changed: a layer's
    /// colour paints the objects whose colour is ByLayer, not those with their own; a rename or reparent paints
    /// everything on the layer and its sublayers; a block, material or linetype edit paints its users.
    /// </summary>
    public IReadOnlySet<Guid> ChangedThroughTables => index.Value.ThroughTables;

    /// <summary>
    /// The objects a table event is about, to highlight, zoom to and select: the users of the entry in B, or in A when it
    /// is gone from B (the objects on a layer, and its sublayers for a rename or reparent; a block's instances; a
    /// group's members; a material's or linetype's users), each with its other side when matched, then those whose
    /// own change it caused.
    /// Membership only: which of them look different is <see cref="ChangedThroughTables"/>, so a group rename, say,
    /// reaches its members without painting them as changed.
    /// </summary>
    public IReadOnlyList<ObjectPair> ObjectsOf(TableEvent e)
    {
        var ix = index.Value;
        var inB = new List<Guid>();
        if (e.B is Guid b) inB.AddRange(Users(e, ModelB, ix.LayersB, b));
        foreach (var share in e.Shares) inB.AddRange(Users(e, ModelB, ix.LayersB, share.B));
        // A's users only when the entry is gone from B (a deleted layer or group): otherwise B's are the ones to see.
        var inA = e.A is Guid a && e.B is null && e.Shares.Count == 0 ? Users(e, ModelA, ix.LayersA, a) : [];
        var caused = Result.ChangeSets.Where(s => s.RootCauseEventId == e.Id).SelectMany(s => s.Objects);
        return [.. inB.Select(id => new ObjectPair(ix.MatchesB.GetValueOrDefault(id)?.A, id))
            .Concat(inA.Select(id => new ObjectPair(id, ix.Matches.GetValueOrDefault(id)?.B)))
            .Concat(caused).Distinct()];
    }

    private static IEnumerable<Guid> Users(TableEvent e, ModelSnapshot m, IReadOnlyDictionary<Guid, string> paths, Guid id)
    {
        bool tree = e.Kind is TableEventKind.Renamed or TableEventKind.Reparented;
        string? path = paths.GetValueOrDefault(id);
        return m.Objects.Where(o => e.Table switch
        {
            TableKind.Layer => tree ? IsUnder(paths.GetValueOrDefault(o.LayerId), path) : o.LayerId == id,
            TableKind.Block => o.BlockDefinitionId == id,
            TableKind.Group => o.GroupIds.Contains(id),
            TableKind.Material => Uses(m, o, o.Material, l => l.MaterialId, id),
            TableKind.Linetype => Uses(m, o, o.Linetype, l => l.LinetypeId, id),
            _ => false,
        }).Select(o => o.Id);
    }

    /// <summary>Multiplies B's lengths into A's units (1 when they agree or can't be converted).</summary>
    public double BToA => Units.MetersPer(ModelB.Info.Units) is double f && Units.MetersPer(ModelA.Info.Units) is double t ? f / t : 1;

    /// <summary>The layers a change set's objects sit on: B's, or A's for objects only in A.</summary>
    public IEnumerable<string> LayersOf(ChangeSet set) =>
        set.Objects.Select(p => p.B is Guid b ? LayerOfB(b) : p.A is Guid a ? LayerOfA(a) : null).OfType<string>();

    public override string ToString() => Describe.Report(Result, LayersOf);

    /// <summary>
    /// True when the object takes its material or linetype from <paramref name="id"/>: its own value, or its layer's
    /// when it uses the layer's.
    /// </summary>
    public static bool Uses(ModelSnapshot model, ObjectRecord o, Sourced<Guid> value, Func<LayerRecord, Guid> fromLayer, Guid id) =>
        value.Source switch
        {
            ValueSource.ByObject => value.Value == id,
            ValueSource.ByLayer => model.Layers.FirstOrDefault(l => l.Id == o.LayerId) is { } layer && fromLayer(layer) == id,
            _ => false,
        };

    /// <summary>The path itself or a sublayer of it.</summary>
    public static bool IsUnder(string? path, string? parent) =>
        path is not null && parent is not null &&
        (string.Equals(path, parent, StringComparison.OrdinalIgnoreCase) || path.StartsWith(parent + "::", StringComparison.OrdinalIgnoreCase));

    /// <summary>Full layer paths from a snapshot's layer table.</summary>
    public static Dictionary<Guid, string> LayerPaths(ModelSnapshot model)
    {
        var byId = model.Layers.GroupBy(l => l.Id).ToDictionary(g => g.Key, g => g.First());
        var paths = new Dictionary<Guid, string>(byId.Count);
        foreach (var layer in byId.Values)
        {
            var names = new List<string>();
            var seen = new HashSet<Guid>();
            for (var l = layer; l is not null && seen.Add(l.Id); l = l.ParentId is Guid p ? byId.GetValueOrDefault(p) : null)
                names.Add(l.Name);
            names.Reverse();
            paths[layer.Id] = string.Join("::", names);
        }
        return paths;
    }

    private sealed class Index
    {
        public readonly Dictionary<Guid, ObjectRecord> ObjectsA, ObjectsB;
        public readonly Dictionary<Guid, string> LayersA, LayersB;
        public readonly Dictionary<Guid, Match> Matches, MatchesB;
        public readonly Dictionary<Guid, GeometryChange> Geometry;
        public readonly Dictionary<ObjectPair, List<ChangeSet>> SetsOf = [];
        public readonly List<Guid> Added = [], Removed = [];
        public readonly List<ObjectPair> Modified = [];
        public readonly Dictionary<string, List<Guid>> ByEvent;
        public readonly HashSet<Guid> ThroughTables;

        public Index(DiffIndex run)
        {
            ObjectsA = ById(run.ModelA.Objects);
            ObjectsB = ById(run.ModelB.Objects);
            LayersA = LayerPaths(run.ModelA);
            LayersB = LayerPaths(run.ModelB);
            Matches = run.Result.Matches.GroupBy(m => m.A).ToDictionary(g => g.Key, g => g.First());
            MatchesB = run.Result.Matches.GroupBy(m => m.B).ToDictionary(g => g.Key, g => g.First());
            Geometry = run.Result.Geometry.GroupBy(g => g.A).ToDictionary(g => g.Key, g => g.First());
            var seen = new HashSet<ObjectPair>();
            foreach (var set in run.Result.ChangeSets)
                foreach (var pair in set.Objects)
                {
                    if (!SetsOf.TryGetValue(pair, out var sets)) SetsOf[pair] = sets = [];
                    sets.Add(set);
                    if (set.Kind == ChangeKind.Added && pair.B is Guid b) Added.Add(b);
                    else if (set.Kind == ChangeKind.Removed && pair.A is Guid a) Removed.Add(a);
                    // A fuzzy pairing on its own is review, not a change.
                    else if (set.Kind == ChangeKind.Modified && !Describe.IsPairing(set) && seen.Add(pair)) Modified.Add(pair);
                }
            ByEvent = Painted(run.Result, run.ModelB, LayersB);
            ThroughTables = [.. ByEvent.Values.SelectMany(v => v)];
        }

        private static Dictionary<Guid, ObjectRecord> ById(IReadOnlyList<ObjectRecord> objects) =>
            objects.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());

        /// <summary>The B objects each table event changed through the table entry they use, by event id.</summary>
        private static Dictionary<string, List<Guid>> Painted(DiffResult r, ModelSnapshot b, Dictionary<Guid, string> paths)
        {
            var onLayer = b.Objects.ToLookup(o => o.LayerId);
            var result = new Dictionary<string, List<Guid>>();
            foreach (var e in r.TableEvents)
            {
                var seen = new HashSet<Guid>();
                var list = result[e.Id] = [];
                void Paint(IEnumerable<ObjectRecord> objects) { foreach (var o in objects) if (seen.Add(o.Id)) list.Add(o.Id); }
                if (e.Table == TableKind.Layer)
                {
                    if (e.Kind == TableEventKind.Split)
                        foreach (var share in e.Shares) Paint(onLayer[share.B]);
                    if (e.B is not Guid lb) continue;
                    switch (e.Kind)
                    {
                        case TableEventKind.Renamed or TableEventKind.Reparented:
                            string? path = paths.GetValueOrDefault(lb);
                            foreach (var (id, p) in paths)
                                if (IsUnder(p, path)) Paint(onLayer[id]);
                            break;
                        case TableEventKind.PropertyChanged:
                            Paint(onLayer[lb].Where(o => e.Property switch
                            {
                                PropertyKind.Color => o.Color.Source == ValueSource.ByLayer,
                                PropertyKind.Material => o.Material.Source == ValueSource.ByLayer,
                                PropertyKind.Linetype => o.Linetype.Source == ValueSource.ByLayer,
                                PropertyKind.PrintWidth => o.PrintWidth.Source == ValueSource.ByLayer,
                                _ => true,
                            }));
                            break;
                    }
                }
                else if (e.B is Guid id && e.Kind is TableEventKind.Renamed or TableEventKind.PropertyChanged)
                {
                    Paint(e.Table switch
                    {
                        TableKind.Block => b.Objects.Where(o => o.BlockDefinitionId == id),
                        TableKind.Material => b.Objects.Where(o => Uses(b, o, o.Material, l => l.MaterialId, id)),
                        TableKind.Linetype => b.Objects.Where(o => Uses(b, o, o.Linetype, l => l.LinetypeId, id)),
                        _ => [],
                    });
                }
            }
            return result;
        }
    }
}
