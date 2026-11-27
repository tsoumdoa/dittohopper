using System.Diagnostics;
using DittoHopper.Core;

namespace DittoHopper.Rhino.Tests;

/// <summary>
/// One diff of a generated pair, with the two test-plan numbers: reported rows (table events plus change
/// sets; the collapsed Data-only bucket is not a row) and wrong pairings (matches whose truth labels differ).
/// </summary>
internal sealed record FixtureRun(DiffResult Result, int Rows, int WrongPairings, int Objects, TimeSpan SnapshotTime, TimeSpan DiffTime)
{
    public int Noise(int trueChanges) => Rows - trueChanges;
}

internal static class Fixture
{
    public static FixtureRun Run(FixturePair pair, DiffSettings? settings = null)
    {
        var clock = Stopwatch.StartNew();
        var a = RhinoSnapshot.FromFile(pair.A);
        var b = RhinoSnapshot.FromFile(pair.B);
        return Diff(a, b, clock.Elapsed, settings);
    }

    /// <summary>
    /// Diffs with GUIDs renumbered in record order (fresh GUIDs every run would reorder the golden files) and
    /// truth labels stripped from user text, so the label never helps or hurts matching.
    /// </summary>
    public static FixtureRun Diff(RhinoSnapshot a, RhinoSnapshot b, TimeSpan snapshotTime, DiffSettings? settings = null)
    {
        var ids = new Renumbering();
        ids.Claim(a.Model);
        ids.Claim(b.Model);
        var (na, truthA) = ids.Apply(a.Model);
        var (nb, truthB) = ids.Apply(b.Model);

        var clock = Stopwatch.StartNew();
        var result = ModelDiff.Diff(na, nb, settings, new RenumberedGeometry(new RhinoGeometryService(a, b), ids.Original));
        var diffTime = clock.Elapsed;

        int wrong = result.Matches.Count(m =>
            truthA.TryGetValue(m.A, out var x) && truthB.TryGetValue(m.B, out var y) && x != y);
        return new FixtureRun(result, result.TableEvents.Count + result.ChangeSets.Count, wrong,
            a.Model.Objects.Count + b.Model.Objects.Count, snapshotTime, diffTime);
    }

    /// <summary>Maps every record id to 00000000-…-n in the order the records appear, A first.</summary>
    private sealed class Renumbering
    {
        private readonly Dictionary<Guid, Guid> map = [];
        public readonly Dictionary<Guid, Guid> Original = [];

        public void Claim(ModelSnapshot s)
        {
            foreach (var l in s.Layers) Id(l.Id);
            foreach (var x in s.Blocks) Id(x.Id);
            foreach (var x in s.Materials) Id(x.Id);
            foreach (var x in s.Linetypes) Id(x.Id);
            foreach (var x in s.Groups) Id(x.Id);
            foreach (var o in s.Objects) Id(o.Id);
        }

        public (ModelSnapshot Snapshot, Dictionary<Guid, string> Truth) Apply(ModelSnapshot s)
        {
            var truth = new Dictionary<Guid, string>();
            var objects = s.Objects.Select(o =>
            {
                if (o.UserText.TryGetValue(Model.TruthKey, out var label)) truth[Id(o.Id)] = label;
                return o with
                {
                    Id = Id(o.Id),
                    LayerId = Id(o.LayerId),
                    Material = new Sourced<Guid>(Id(o.Material.Value), o.Material.Source),
                    Linetype = new Sourced<Guid>(Id(o.Linetype.Value), o.Linetype.Source),
                    GroupIds = [.. o.GroupIds.Select(Id)],
                    BlockDefinitionId = o.BlockDefinitionId is Guid d ? Id(d) : null,
                    UserText = o.UserText.Where(kv => kv.Key != Model.TruthKey).ToDictionary(kv => kv.Key, kv => kv.Value),
                };
            }).ToList();
            return (s with
            {
                Layers = [.. s.Layers.Select(l => l with
                {
                    Id = Id(l.Id),
                    ParentId = l.ParentId is Guid p ? Id(p) : null,
                    LinetypeId = Id(l.LinetypeId),
                    MaterialId = Id(l.MaterialId),
                })],
                Blocks = [.. s.Blocks.Select(x => x with { Id = Id(x.Id), Nested = [.. x.Nested.Select(n => n with { DefinitionId = Id(n.DefinitionId) })] })],
                Materials = [.. s.Materials.Select(x => x with { Id = Id(x.Id) })],
                Linetypes = [.. s.Linetypes.Select(x => x with { Id = Id(x.Id) })],
                Groups = [.. s.Groups.Select(x => x with { Id = Id(x.Id) })],
                Objects = objects,
                CurrentLayerId = s.CurrentLayerId is Guid c ? Id(c) : null,
            }, truth);
        }

        private Guid Id(Guid g)
        {
            if (g == Guid.Empty) return g;
            if (!map.TryGetValue(g, out var n))
            {
                n = Guid.Parse($"00000000-0000-0000-0000-{map.Count + 1:x12}");
                map[g] = n;
                Original[n] = g;
            }
            return n;
        }
    }

    private sealed class RenumberedGeometry(IGeometryService inner, IReadOnlyDictionary<Guid, Guid> original) : IGeometryService
    {
        public ShapeDeviation? Deviation(Guid a, Guid b, Xform? moveA, double tolerance) =>
            inner.Deviation(original[a], original[b], moveA, tolerance);

        public PointCorrespondence? CorrespondingPoints(Guid a, Guid b) => inner.CorrespondingPoints(original[a], original[b]);

        public string? DataOnlyReason(Guid a, Guid b) => inner.DataOnlyReason(original[a], original[b]);
    }
}
