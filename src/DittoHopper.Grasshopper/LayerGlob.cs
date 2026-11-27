using System.Text.RegularExpressions;

namespace DittoHopper.Grasshopper;

/// <summary>
/// Layer path patterns such as <c>Structure::*</c>. <c>*</c> matches any text, sublayers included, and <c>?</c> one
/// character; matching ignores case, as Rhino layer names do. A pattern ending in <c>::*</c> also matches the
/// parent layer itself, so <c>Facade::*</c> means the whole Facade tree. Several patterns match when any does;
/// no patterns match everything.
/// </summary>
public sealed class LayerGlob
{
    /// <summary>The one help text for every Layers input.</summary>
    public const string Help =
        "Layer paths such as Structure::* (Structure and everything under it) or *Wall*; * matches any text, ? one character, " +
        "case is ignored. Several: any of them. Empty: every layer.";

    private readonly Regex[] patterns;

    public LayerGlob(IEnumerable<string?> patterns)
    {
        Text = [.. patterns.Select(p => p?.Trim()).Where(p => !string.IsNullOrEmpty(p)).Select(p => p!)];
        this.patterns = [.. Text.Select(Compile)];
    }

    /// <summary>The patterns as given, blanks removed.</summary>
    public IReadOnlyList<string> Text { get; }

    public bool MatchesAll => patterns.Length == 0;

    public bool Matches(string? layerPath) =>
        patterns.Length == 0 || (layerPath is not null && patterns.Any(p => p.IsMatch(layerPath)));

    public override string ToString() => MatchesAll ? "all layers" : string.Join(", ", Text);

    private static Regex Compile(string pattern)
    {
        bool tree = pattern.EndsWith("::*", StringComparison.Ordinal);
        string body = tree ? pattern[..^3] : pattern;
        string regex = Regex.Escape(body).Replace(@"\*", ".*").Replace(@"\?", ".");
        if (tree) regex += "(::.*)?";
        return new Regex("^" + regex + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
