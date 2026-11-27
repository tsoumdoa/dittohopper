using DittoHopper.Core;
using DittoHopper.Rhino;

namespace DittoHopper.Grasshopper;

/// <summary>
/// One diff with the two snapshots it came from, so downstream components can look up records, layer paths and
/// geometry. Immutable; lookups are built once, on first use.
/// </summary>
public sealed class DiffRun
{
    private readonly Lazy<Index> index;

    public DiffRun(DiffResult result, RhinoSnapshot a, RhinoSnapshot b)
    {
        Result = result;
        A = a;
        B = b;
        index = new Lazy<Index>(() => new Index(this));
    }

    public DiffResult Result { get; }
    public RhinoSnapshot A { get; }
    public RhinoSnapshot B { get; }

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

    /// <summary>Objects in A: matched or removed.</summary>
    public int ObjectsInA => Describe.ObjectsInA(Result);

    public IReadOnlyList<Guid> Added => index.Value.Added;
    public IReadOnlyList<Guid> Removed => index.Value.Removed;
    /// <summary>Modified pairs, each once, in change-set order.</summary>
    public IReadOnlyList<ObjectPair> Modified => index.Value.Modified;

    /// <summary>Multiplies B's lengths into A's units (1 when they agree or can't be converted).</summary>
    public double BToA => Geometry.Scale(B.Model.Info.Units, A.Model.Info.Units);

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
        public readonly Dictionary<Guid, Match> Matches;
        public readonly Dictionary<Guid, GeometryChange> Geometry;
        public readonly List<Guid> Added = [], Removed = [];
        public readonly List<ObjectPair> Modified = [];

        public Index(DiffRun run)
        {
            ObjectsA = ById(run.A.Model.Objects);
            ObjectsB = ById(run.B.Model.Objects);
            LayersA = LayerPaths(run.A.Model);
            LayersB = LayerPaths(run.B.Model);
            Matches = run.Result.Matches.GroupBy(m => m.A).ToDictionary(g => g.Key, g => g.First());
            Geometry = run.Result.Geometry.GroupBy(g => g.A).ToDictionary(g => g.Key, g => g.First());
            var seen = new HashSet<ObjectPair>();
            foreach (var set in run.Result.ChangeSets)
                foreach (var pair in set.Objects)
                {
                    if (set.Kind == ChangeKind.Added && pair.B is Guid b) Added.Add(b);
                    else if (set.Kind == ChangeKind.Removed && pair.A is Guid a) Removed.Add(a);
                    else if (set.Kind == ChangeKind.Modified && seen.Add(pair)) Modified.Add(pair);
                }
        }

        private static Dictionary<Guid, ObjectRecord> ById(IReadOnlyList<ObjectRecord> objects) =>
            objects.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First());
    }
}
