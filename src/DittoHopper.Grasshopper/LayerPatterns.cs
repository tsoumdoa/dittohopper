using DittoHopper.Core;

namespace DittoHopper.Grasshopper;

/// <summary>
/// A Layers input: several layer path patterns, matched with Core's <see cref="LayerGlob"/> (the same rules as Diff
/// Settings' Scope). A path matches when any pattern does; no patterns match everything. Results are memoised per
/// path, since a model has few layer paths and many objects.
/// </summary>
public sealed class LayerPatterns
{
    /// <summary>The one help text for every Layers input.</summary>
    public const string Help =
        "Layer paths such as Structure::* (every layer under Structure, not Structure itself), Structure or *Wall*. * matches " +
        "any text, :: included; ? one character; case is ignored. Several: any of them. Empty: every layer.";

    private readonly Dictionary<string, bool> memo = new(StringComparer.Ordinal);

    public LayerPatterns(IEnumerable<string?> patterns) =>
        Text = [.. patterns.Select(p => p?.Trim()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!)];

    /// <summary>The patterns as given, blanks removed.</summary>
    public IReadOnlyList<string> Text { get; }

    public bool MatchesAll => Text.Count == 0;

    public bool Matches(string? layerPath)
    {
        if (Text.Count == 0) return true;
        if (layerPath is null) return false;
        if (!memo.TryGetValue(layerPath, out bool hit))
            memo[layerPath] = hit = Text.Any(p => LayerGlob.IsMatch(p, layerPath));
        return hit;
    }

    /// <summary>The patterns that match none of <paramref name="layerPaths"/>: typos, or layers that don't exist.</summary>
    public IReadOnlyList<string> Unmatched(IEnumerable<string> layerPaths)
    {
        var paths = layerPaths.ToList();
        return [.. Text.Where(p => !paths.Any(path => LayerGlob.IsMatch(p, path)))];
    }

    public override string ToString() => MatchesAll ? "all layers" : string.Join(", ", Text);
}
