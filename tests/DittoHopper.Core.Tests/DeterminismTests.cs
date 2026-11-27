using Xunit;

namespace DittoHopper.Core.Tests;

public class DeterminismTests
{
    public static TheoryData<string> Cases => new()
    {
        "layer-rename", "layer-reparent", "layer-split", "bylayer-color", "gh-rebake", "default-layer",
        "user-text", "nested-block", "copy-in-place", "delete-layer", "root-causes",
    };

    private static (SnapshotBuilder A, SnapshotBuilder B) Case(string name) => name switch
    {
        "layer-rename" => Fixtures.LayerRename(),
        "layer-reparent" => Fixtures.LayerReparent(),
        "layer-split" => Fixtures.LayerSplit(),
        "bylayer-color" => Fixtures.ByLayerColor(),
        "gh-rebake" => Fixtures.GhRebake(),
        "default-layer" => Fixtures.DefaultLayer(),
        "user-text" => Fixtures.UserText(),
        "nested-block" => Fixtures.NestedBlock() is var (a, b, _, _) ? (a, b) : default,
        "copy-in-place" => Fixtures.CopyInPlace(),
        "delete-layer" => Fixtures.DeleteLayer(),
        "root-causes" => Fixtures.RootCauses(),
        _ => throw new ArgumentException(name),
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ShuffledInputsGiveByteIdenticalJson(string name)
    {
        var (a, b) = Case(name);
        string expected = DiffJson.Serialize(ModelDiff.Diff(a.Build(), b.Build()));
        for (int seed = 1; seed <= 3; seed++)
        {
            var rng = new Random(seed);
            string actual = DiffJson.Serialize(ModelDiff.Diff(Shuffle(a.Build(), rng), Shuffle(b.Build(), rng)));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void JsonRoundTripsWithReviewState()
    {
        var r = Fixtures.Diff(Fixtures.DeleteLayer());
        var signedOff = r.WithReview(r.ChangeSets[0].Id, ReviewState.Intended).WithReview(r.TableEvents[0].Id, ReviewState.Intended);
        string json = DiffJson.Serialize(signedOff);

        var back = DiffJson.Deserialize(json);
        Assert.Equal(json, DiffJson.Serialize(back));
        Assert.Equal(ReviewState.Intended, back.ChangeSets[0].Review);
        Assert.Equal(ReviewState.Intended, back.TableEvents[0].Review);
        Assert.Throws<KeyNotFoundException>(() => r.WithReview("cs-missing", ReviewState.Intended));
    }

    [Fact]
    public void ChangeSetIdsDependOnlyOnTheChange()
    {
        // The same edit in two unrelated models gives the same set id, so sign-off survives a re-snapshot.
        var first = Fixtures.Diff(Fixtures.DefaultLayer()).ChangeSets.Single();
        var (a, b) = Fixtures.DefaultLayer();
        a.Many(a.Layer("Extra"), 5);
        var second = ModelDiff.Diff(a.Build(), b.Build()).ChangeSets.Single(s => s.Kind == ChangeKind.Modified);
        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void SkippedNotesAreSortedInTheResult()
    {
        var (a, b) = Fixtures.LayerRename();
        a.Info = a.Info with { Skipped = ["worksession 'x.rws'", "linked block 'Site'"] };
        var first = DiffJson.Serialize(ModelDiff.Diff(a.Build(), b.Build()));
        a.Info = a.Info with { Skipped = ["linked block 'Site'", "worksession 'x.rws'"] };
        Assert.Equal(first, DiffJson.Serialize(ModelDiff.Diff(a.Build(), b.Build())));
    }

    private static ModelSnapshot Shuffle(ModelSnapshot s, Random rng) => s with
    {
        Layers = Mix(s.Layers, rng),
        Blocks = Mix(s.Blocks, rng).Select(d => d with { Nested = Mix(d.Nested, rng) }).ToList(),
        Materials = Mix(s.Materials, rng),
        Linetypes = Mix(s.Linetypes, rng),
        Groups = Mix(s.Groups, rng),
        Objects = Mix(s.Objects, rng).Select(o => o with
        {
            GroupIds = Mix(o.GroupIds, rng),
            UserText = Mix(o.UserText.ToList(), rng).ToDictionary(kv => kv.Key, kv => kv.Value),
        }).ToList(),
    };

    private static List<T> Mix<T>(IReadOnlyList<T> items, Random rng)
    {
        var list = items.ToList();
        rng.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list));
        return list;
    }
}
