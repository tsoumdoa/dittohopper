using System.Text.RegularExpressions;

namespace DittoHopper.Core;

/// <summary>
/// Accidental-change detector ids, as they appear in <see cref="ChangeSet.Flags"/>. A flagged set that isn't
/// marked intended needs a look (<see cref="ChangeSet.NeedsLook"/>).
/// </summary>
public static class Detectors
{
    /// <summary>A change on a layer outside <see cref="DiffSettings.ScopeGlob"/>: the object's A or B layer doesn't match.</summary>
    public const string OutsideScope = "outside-scope";
    /// <summary>Objects moved to the top-level Default layer, or to B's current layer (known for documents only).</summary>
    public const string DefaultLayer = "default-layer";
    /// <summary>Moved more than the shape tolerance but less than the nudge threshold (<see cref="DiffSettings.NudgeFactor"/>).</summary>
    public const string TinyNudge = "tiny-nudge";
    /// <summary>Color, material or linetype source changed from ByLayer to ByObject.</summary>
    public const string ByLayerOverride = "bylayer-override";
    /// <summary>An added object with the same kind and geometry hash as another object in B.</summary>
    public const string Duplicate = "duplicate";
    /// <summary>A change to an object whose layer, or a parent of it, was locked or hidden in A.</summary>
    public const string LockedOrHiddenLayer = "locked-or-hidden-layer";
    /// <summary>At most <see cref="LoneDeletionMax"/> objects removed from a layer where nothing else changed.</summary>
    public const string LoneDeletion = "lone-deletion";
    /// <summary>
    /// The same change on objects across at least <see cref="MassChangeLayers"/> unrelated layers. Layers are related
    /// when one contains the other or they share a parent. Shape changes and changes with a root cause don't count.
    /// </summary>
    public const string MassChange = "mass-change";
    /// <summary>Objects paired by fuzzy score (matching pass 5): the pairing itself needs review.</summary>
    public const string FuzzyMatch = "fuzzy-match";

    /// <summary>Starting value from the design doc.</summary>
    public const int LoneDeletionMax = 3;
    /// <summary>Starting value from the design doc.</summary>
    public const int MassChangeLayers = 3;

    /// <summary>A short reason for a detector id, for a "flag reason" column. Unknown ids come back as given.</summary>
    public static string Describe(string id) => id switch
    {
        OutsideScope => "Outside the declared scope",
        DefaultLayer => "Moved to the Default or current layer",
        TinyNudge => "Tiny nudge: a gumball or arrow-key slip?",
        ByLayerOverride => "ByLayer overridden on the object",
        Duplicate => "Duplicate: copy in place?",
        LockedOrHiddenLayer => "Changed on a layer that was locked or hidden",
        LoneDeletion => "Lone deletion: a stray Delete key?",
        MassChange => "Same change across unrelated layers: select-all?",
        FuzzyMatch => "Fuzzy match: the pairing needs review",
        _ => id,
    };
}

/// <summary>Layer path globs, as in <see cref="DiffSettings.ScopeGlob"/>.</summary>
public static class LayerGlob
{
    /// <summary>
    /// True when the full layer path (e.g. <c>Facade::Panels</c>) matches the glob: <c>*</c> is any text, including
    /// <c>::</c>, <c>?</c> is one character, and case is ignored.
    /// </summary>
    public static bool IsMatch(string glob, string path) => Compile(glob)(path);

    internal static Func<string, bool> Compile(string glob)
    {
        string pattern = "^" + string.Concat(glob.Trim().Select(c => c switch
        {
            '*' => ".*",
            '?' => ".",
            _ => Regex.Escape(c.ToString()),
        })) + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline).IsMatch;
    }
}

/// <summary>
/// Runs the detectors. Per-object flags are part of a change set's signature (<see cref="ChangeSets"/>); lone
/// deletion and mass change are judged on whole sets once the sets and layer rows exist.
/// </summary>
internal sealed class Flagger
{
    private readonly DiffContext ctx;
    private readonly Dictionary<Guid, double> displacement;
    private readonly HashSet<Guid> fuzzy;
    private readonly HashSet<Guid> duplicates;
    private readonly Dictionary<Guid, Guid> layerBToA = [];
    private readonly Func<string, bool>? inScope;

    public Flagger(DiffContext ctx, IReadOnlyList<Pair> pairs, Dictionary<Guid, double> displacement)
    {
        this.ctx = ctx;
        this.displacement = displacement;
        fuzzy = [.. pairs.Where(p => p.By == MatchedBy.Fuzzy).Select(p => p.A.Id)];
        duplicates = [.. ctx.B.Objects.GroupBy(o => (o.Kind, o.Geometry.Hash)).Where(g => g.Count() > 1).SelectMany(g => g.Select(o => o.Id))];
        foreach (var e in ctx.Layers.Entries) layerBToA[e.B] = e.A;
        foreach (var (a, split) in ctx.Splits)
            foreach (var (b, _) in split.Targets) layerBToA.TryAdd(b, a);
        if (!string.IsNullOrWhiteSpace(ctx.Settings.ScopeGlob))
            inScope = LayerGlob.Compile(ctx.Settings.ScopeGlob);
    }

    public IEnumerable<string> ForRow(ChangeRow row)
    {
        var a = ctx.A.ObjectById[row.Pair.A!.Value];
        var b = ctx.B.ObjectById[row.Pair.B!.Value];
        var c = row.Change;
        if (OutsideA(a.LayerId) || OutsideB(b.LayerId))
            yield return Detectors.OutsideScope;
        if (c.Property == PropertyKind.Layer && IsDefaultOrCurrent(b.LayerId))
            yield return Detectors.DefaultLayer;
        double tol = ctx.ShapeTolerance;
        if (c.Property == PropertyKind.Transform && displacement.TryGetValue(a.Id, out double d)
            && d > tol && d < ctx.Settings.NudgeFactor * tol)
            yield return Detectors.TinyNudge;
        if (c.Property is PropertyKind.Color or PropertyKind.Material or PropertyKind.Linetype
            && c.FromSource == ValueSource.ByLayer && c.ToSource == ValueSource.ByObject)
            yield return Detectors.ByLayerOverride;
        if (LockedOrHidden(a.LayerId))
            yield return Detectors.LockedOrHiddenLayer;
        if (fuzzy.Contains(a.Id))
            yield return Detectors.FuzzyMatch;
    }

    public IEnumerable<string> ForRemoved(ObjectRecord a)
    {
        if (OutsideA(a.LayerId))
            yield return Detectors.OutsideScope;
        if (LockedOrHidden(a.LayerId))
            yield return Detectors.LockedOrHiddenLayer;
    }

    public IEnumerable<string> ForAdded(ObjectRecord b)
    {
        if (OutsideB(b.LayerId))
            yield return Detectors.OutsideScope;
        if (duplicates.Contains(b.Id))
            yield return Detectors.Duplicate;
        if (layerBToA.TryGetValue(b.LayerId, out var a) && LockedOrHidden(a))
            yield return Detectors.LockedOrHiddenLayer;
    }

    /// <summary>Adds the set-level flags: lone deletion and mass change.</summary>
    public List<ChangeSet> ForSets(List<ChangeSet> sets, IReadOnlyList<LayerRow> layers)
    {
        var byId = sets.ToDictionary(s => s.Id);
        var rowOfA = layers.Where(r => r.A is not null).ToDictionary(r => r.A!.Value);
        return [.. sets.Select(s =>
        {
            var extra = new List<string>();
            if (s.Kind == ChangeKind.Removed && s.RootCauseEventId is null
                && rowOfA.TryGetValue(ctx.A.ObjectById[s.Objects[0].A!.Value].LayerId, out var row)
                && row.Events.Count == 0 && row.ChangeSets.All(id => byId[id].Kind == ChangeKind.Removed)
                && row.ChangeSets.Sum(id => byId[id].Objects.Count) <= Detectors.LoneDeletionMax)
                extra.Add(Detectors.LoneDeletion);
            if (s.Kind == ChangeKind.Modified && s.RootCauseEventId is null
                && s.Change!.Property is not (PropertyKind.Geometry or PropertyKind.BlockContent)
                && UnrelatedLayers(s.Objects.Select(p => ctx.A.ObjectById[p.A!.Value].LayerId)) >= Detectors.MassChangeLayers)
                extra.Add(Detectors.MassChange);
            return extra.Count == 0 ? s : s with { Flags = [.. s.Flags.Concat(extra).Order(StringComparer.Ordinal)] };
        })];
    }

    private bool OutsideA(Guid layer) => inScope is not null && !inScope(ctx.A.LayerLabel(layer));
    private bool OutsideB(Guid layer) => inScope is not null && !inScope(ctx.B.LayerLabel(layer));

    private bool IsDefaultOrCurrent(Guid layer) =>
        layer == ctx.B.CurrentLayerId
        || (ctx.B.LayerById.TryGetValue(layer, out var l) && l.ParentId is null && string.Equals(l.Name, "Default", StringComparison.OrdinalIgnoreCase));

    /// <summary>The A layer or one of its parents was locked or hidden in A.</summary>
    private bool LockedOrHidden(Guid layer)
    {
        for (var l = ctx.A.LayerById.GetValueOrDefault(layer); l is not null; l = l.ParentId is Guid p ? ctx.A.LayerById.GetValueOrDefault(p) : null)
            if (l.Locked || !l.Visible)
                return true;
        return false;
    }

    /// <summary>How many groups the A layers fall into when layers that contain one another or share a parent are joined.</summary>
    private int UnrelatedLayers(IEnumerable<Guid> layers)
    {
        var ids = layers.Distinct().Where(ctx.A.LayerById.ContainsKey).Order().ToList();
        var root = ids.ToDictionary(x => x, x => x);
        Guid Find(Guid x)
        {
            while (root[x] != x) x = root[x];
            return x;
        }
        for (int i = 0; i < ids.Count; i++)
            for (int j = i + 1; j < ids.Count; j++)
                if (Related(ids[i], ids[j]))
                    root[Find(ids[j])] = Find(ids[i]);
        return ids.Select(Find).Distinct().Count();
    }

    private bool Related(Guid x, Guid y)
    {
        var (lx, ly) = (ctx.A.LayerById[x], ctx.A.LayerById[y]);
        return (lx.ParentId is not null && lx.ParentId == ly.ParentId) || Contains(x, y) || Contains(y, x);
    }

    private bool Contains(Guid ancestor, Guid layer)
    {
        for (var l = ctx.A.LayerById[layer]; l.ParentId is Guid p && ctx.A.LayerById.TryGetValue(p, out var parent); l = parent)
            if (p == ancestor)
                return true;
        return false;
    }
}
