# DittoHopper — Design

Nov 27, 2026. DittoHopper is the plugin that implements this design (working title: Rhino Model Diff).

## Handover summary

DittoHopper compares two Rhino models and shows what changed, and just as clearly what did not. Status: the design is agreed in outline and no code exists yet.

- **Start here:** build the UI-free diff core first (Architecture through Geometry below), tested against fixture file pairs. Then build the Grasshopper components on top.
- **Settled:** items under Decisions were agreed with the owner. Don't reopen them without asking.
- **Unsettled:** nothing is open right now. Items marked proposed are suggestions and can still change.
- **Mocks:** seven interactive HTML mocks from the design session are listed under Sources. They show the intended look; this doc is the spec.
- **Terms:** A is the baseline model, B the later one. A *pair* is one A object matched to one B object. *Added* exists only in B, *removed* only in A.

## Purpose and goals

The tool exists to give confidence: you made only the changes you meant to, or you know exactly what someone else changed. Showing what did *not* change matters as much as listing what did.

Two jobs it must do well:

- **Self-check:** "I meant to change only `Facade::*`. Did I touch anything else?" This is usually the active document compared with its last save.
- **Incoming file (main use, so the default view):** "What did the consultant actually change between rev B and rev C?"

Goals:

1. Report each change once, at its root cause. One layer rename is one change, not 212 object rows.
2. Show untouched things explicitly: untouched layers, and unchanged properties on a changed object.
3. Put likely-accidental changes first (see Accidental-change detectors).
4. Make matching explainable. Every pair records how it was matched and how confident that is.
5. Give the same inputs the same result every time, so a diff can be signed off and re-checked.

Non-goals for v0 and v1:

- Merging two models. A per-change "Restore from A" action is in scope for v1.
- Writing to either model unless the user explicitly asks (Stamp IDs, Restore from A). The diff itself is read-only.

## Architecture

Build one UI-free diff core (a plain .NET library on RhinoCommon) and put three thin shells on it. Grasshopper is the right first shell, but it is a poor place to review 300 changes, so it can't be the only one.

*Architecture: inputs (two .3dm files / active doc vs last save / snapshot history) -> Diff core [Snapshot -> Match tables -> Match objects -> Compare -> Group + flag] -> DiffResult -> shells: Grasshopper components (v0), Rhino panel + conduit (v1), Headless on Rhino.Compute (later).*

Any two inputs can be A and B. The core's result is plain data, so every shell shows the same answer.

**Shells, in build order:**

| Shell | Phase | Who it serves | What it does |
| --- | --- | --- | --- |
| Grasshopper components | v0 | Computational designers | Automate diffs, filter results, run QA gates, bake, export reports |
| Rhino panel and display conduit | v1 | Modelers, PMs, reviewers | Browse, zoom to, review and sign off. No GH needed |
| Headless (Rhino.Compute) | Later | Teams, CI | Diff on every save, or in CI on each commit |

**Why GH fits:** computational designers already work there. The diff is data, so added and moved objects feed filters, tags, bakes and reports. Rule-based QA ("fail if anything on `Structure::*` changed") falls out naturally. GH is also the fastest way to iterate on the matching logic.

**Why GH alone is not enough:**

- Review means browsing a list and zooming to objects. The GH canvas is built for data flow, not that.
- Every upstream tweak re-solves the diff, so snapshots must be cached per file version.
- Preview belongs to each component. You can't click a row to zoom to it, and selection doesn't sync.
- Modelers and PMs who never open GH can't use it.

**Where A and B come from.** Each side can be any of:

- **A file:** read with `File3dm`. The file doesn't have to be open in Rhino (`v12.3dm` against `v13.3dm`).
- **The active document against its last save:** "What did I change this session?" This is the second use, after incoming files.
- **A stored snapshot:** save a small fingerprint file on each save, so any two versions diff in seconds (later phase).

## Object matching

Match objects with a cascade of signals, cheapest and most certain first. Use the Rhino GUID as an early signal, never the only one. GUIDs survive in-place edits but not the things GH users do all day.

| GUID survives | GUID is lost (a new object) |
| --- | --- |
| Move, Rotate, Scale, Gumball | Re-baking from Grasshopper (delete, then bake again) |
| Control-point and sub-object edits | Join, Explode, Booleans |
| Changing layer, name, color or user text | Copy or Mirror, then deleting the original |
| Save, Save As, reopening the file | Round-trips through DWG, IFC or other tools; a consultant rebuilding the model |

**The cascade.** Each pass sees only objects that no earlier pass claimed.

| Pass | Signal | Catches | Confidence |
| --- | --- | --- | --- |
| 1. Stable key | Own ID in user text (`diff:uid`, or a configured key such as `mark=C-12`), or a name unique in both models | Anything tagged. Survives everything | Certain |
| 2. Rhino GUID | Same object ID, and the object type matches | Objects edited in place | Certain |
| 3. Geometry hash | Exact match on a canonical hash, coordinates snapped to model tolerance | Untouched objects that got a new ID from a re-bake or paste | Very high |
| 4. Shape signature + nearest | Position-free measures (area, volume, edge lengths), then the closest candidate | Objects moved or rotated *and* given a new ID | High |
| 5. Fuzzy score ≥ 0.75 | Weighted score of type, bounding-box overlap, area/volume ratio, layer, name | Objects edited *and* given a new ID | Needs review |
| Leftovers | No match | Reported as added (B only) or removed (A only) | None |

**Rules that keep matching trustworthy:**

- **Identity is not change.** Matching decides which object is which; comparing decides what changed. A pair matched by geometry hash has unchanged geometry by definition, so only its attributes can differ.
- **Every pair carries evidence.** Store `matchedBy` and `confidence` on each pair and show them in the UI as a badge (`GUID`, `hash`, `fuzzy 0.91`). Low-confidence pairs go to a review bucket instead of being shown as certain.
- **Users can unlink and relink pairs.** A wrong pairing is worse than an honest added + removed pair.
- **Stamp IDs is opt-in.** A Stamp IDs command or component writes `diff:uid` into user text on objects that lack one. Bakes that carry it forward let pass 1 catch almost everything. It runs only when the user asks (decided), because it writes to the model.

## Tables: layers, blocks, materials, linetypes, groups

Diff the model's tables before its objects. Build an A→B map for each table, then compare every object through those maps. A renamed or re-parented layer then produces zero object rows.

**Pipeline:** diff tables → build A→B maps → match objects → compare through the maps → collapse to root causes.

**Layer matching order** (decided: stable ID first):

1. `Layer.Id`. Survives rename and re-parent within the same file lineage.
2. Full path, e.g. `Structure::Slabs`.
3. Leaf name, if it is unique in both models.
4. Inferred from where the matched objects went: if at least 60% of an A layer's matched objects now sit on one B layer, treat it as that layer. If no B layer reaches 60% and the objects spread over new B layers, report a split with each share. Mark the result *inferred* with its share.
5. Anything left is an added or removed layer.

Blocks, materials and linetypes use the same order: stable ID, then name. Groups are compared by membership.

**Collapse to root causes.** In one example model, 362 per-object rows collapse into 6 cause rows, each expandable to its objects:

| Cause | Example | Object rows |
| --- | --- | --- |
| Rename | `Walls` → `A-WALL`, 212 objects follow | 0 |
| Layer property | `Doors` color changed; 36 objects look different (ByLayer) | 0 |
| Re-parent | `Furniture` moved under `Interior`; 88 objects follow | 0 |
| New layer | `Interior` added as a new parent | 0 |
| Split | `Slabs` → `Slabs-L01` (7) + `Slabs-L02` (5), inferred, confidence 0.92 | 0 |
| Delete | `Temp` deleted; 14 objects removed with it | 14 removed |

**Rules:**

- **A rename is not an object change.** Objects reference a layer by ID, not by name. Compare `map(A.layer)` with `B.layer`.
- **A ByLayer change belongs to the layer.** If an object's color, material or linetype is ByLayer, the object did not change. Report it once, on the layer.
- **Blocks work the same way.** Editing a definition gives one row: "Window-A changed, 48 instances". An instance gets its own row only when its own transform or attributes change.
- **Nested blocks report to the instance** (decided). A change inside a nested definition appears on every affected top-level instance, with the inner definition shown as the root cause.
- **No shared IDs? Infer them.** For rebuilt files, match by path, then name, then where the objects went. If 95% of `A::Slabs` now sits on `B::Floor`, that is a rename, marked inferred.

**Source tracking.** Every appearance property stores its value *and* its source: ByObject, ByLayer, ByParent (and ByMaterial where Rhino allows it). This tells you whether the object changed or only its layer did. The default view shows *own* changes. An "effective appearance" view shows what the object now looks like (agreed).

## Property change tracking

Each matched pair is compared property by property. Each difference becomes one `PropertyChange`, and identical changes are grouped into change sets. Accidents usually happen in batches (select-all, then a layer change), so grouping makes them easy to spot.

1. **Record per object.** Store references as IDs, never names: layer `#8c1e`, color `ByLayer`, material `ByLayer`, name `"W-042"`, user text `{mark, fire}`, geometry hash `77c2…`.
2. **Compare through the table maps.** `map(A.layer)` against `B.layer`, value *and* source for each appearance property, user text per key, the geometry hash.
3. **Emit a PropertyChange** for each property that differs: `{object, property, from, to}`. Count the unchanged properties too, so the UI can confirm them.
4. **Group into change sets** by identical signature (property, from, to, plus the same flags). "Layer `Walls` → `Default` on 23 objects" is one row, not 23.

**User text is diffed per key** (decided): each key is added, removed or changed. Example: `panel_type` A → B, `+ supplier = FX-02`, `− temp_note`.

**What counts as a change.** Defaults, editable in Diff Settings:

| Group | On by default | Off by default |
| --- | --- | --- |
| Geometry | Placement (rigid move, rotate, mirror; reported as a transform); Shape (everything else; with Δ area, volume or length) | None |
| Organization | Layer; block definition; group membership | Display order |
| Appearance | Color; material; linetype | Print width (own value vs ByLayer) |
| Data | Name; user text, per key | Plugin user data; render meshes (always noisy) |

Geometry changes have their own ladder, below.

## Geometry changes

"Geometry changed" alone is not useful. Each pair whose geometry differs goes down a ladder of questions, and stops at the first yes. A rebuild that kept the shape is then reported differently from a dragged control point or a solid that became open.

| Step | Question | If yes, label | What the report shows |
| --- | --- | --- | --- |
| 1 | Geometry hash equal (coordinates snapped to tolerance)? | Unchanged | Nothing |
| 2 | Same shape within tolerance? Sampled deviation ≤ shape tolerance, even though the data differs | Data-only | Which kind: rebuilt, knots added, reparameterized, direction flipped, seam moved, NURBS ↔ polyline |
| 3 | Explained by a rigid transform? | Moved / rotated / mirrored | The transform, e.g. "moved 120 mm in X" |
| 4 | Same structure? Type, degree, CV count, knots, vertex count | Edited | Which CVs or vertices moved and by how much, compared index by index |
| 5 | Same topology? Faces, edges, naked edges, closed or solid | Reshaped | Deviation: max, mean, and where (heatmap). Polylines use a vertex sequence diff, like a text diff |
| — | All no | Topology changed | What broke, e.g. "solid became open", "face split" |

Only step 1 runs on every pair, and it is a hash compare. Steps 2–5 run only on pairs whose hash differs, usually a few percent of the model, so deviation sampling stays cheap. Data-only changes are shown in a collapsed bucket, but they never count as changes and never fail a QA gate (decided).

**What the snapshot stores per geometry type:**

| Type | Structure | Arrays for index diff | Measures |
| --- | --- | --- | --- |
| Curves | Type, degree, CV count, knot hash; closed, periodic, planar | CV array | Length, start and end points, direction |
| Surfaces and Breps | Faces, edges, naked edges; solid or not; per face: CV grid size + hash; trim loop hash | Per-face CV grids | Area, volume, centroid |
| Meshes and SubD | Vertex and face count; closed, manifold, welded | Vertex array | Area, volume, bounding box |

**Edge cases to handle so the report stays honest:**

- **Closed curve with a moved seam.** The CV list is the same, rotated to start elsewhere. Rotate it before comparing, or a seam move looks like every CV moved.
- **Flipped direction.** Also compare against the reversed list. A flip alone is Data-only.
- **Two tolerances.** The *shape tolerance* decides whether a deviation is a real change (default: document absolute tolerance). A larger *nudge threshold* flags small moves as possible slips. One number can't do both jobs.
- **Moved and edited at once** (proposed). Fit the best rigid transform first, then diff the residual, and report "Moved + edited" instead of every CV moving.
- **Breps.** v0 reports deviation for the whole object. Matching individual faces ("face 3 changed") is a v2 feature.

## Accidental-change detectors

Each detector flags a whole change set, and a flagged set goes to "Needs a look". A set is also flagged when it falls outside the declared scope. Thresholds marked *starting value* are agreed defaults, to tune on real files.

| Detector | Fires when | Default threshold | Typical cause |
| --- | --- | --- | --- |
| Outside declared scope | A change on a layer that doesn't match the scope glob | Glob set by the user | Edited the wrong thing |
| Moved to Default or current layer | Objects' B layer is `Default` or the layer that was current | Any | Wrong layer was active |
| Tiny nudge | Moved by more than tolerance but less than the nudge threshold | 10 × absolute tolerance | Gumball or arrow-key slip |
| ByLayer → override | Color, material or linetype source changed from ByLayer to ByObject | Any | Property set directly on the object |
| Duplicate | An added object has the same geometry hash as an existing one | Exact hash | Copy in place |
| Edit on locked or hidden layer | Something changed on a layer that was locked or hidden in A | Any | Should not be possible by hand |
| Lone deletion | A few objects removed from a layer where nothing else changed | ≤ 3 objects (starting value) | Stray Delete key |
| Mass identical change | The same change on many objects across unrelated layers | ≥ 3 layers (starting value) | Select-all, then a change |

Rule: a set needs a look when it is not yet marked intended, and it is either flagged or outside scope.

## UX

The output people want is a sentence like "Outside `Facade::*`, only 28 objects changed, in 3 sets", not 300 rows. The review UI gets there in three levels of detail: matrix → change sets → one object. Both surfaces (GH and the Rhino panel) use the same colors and the same evidence badges.

**Colors:** added = green, removed = red (shown as a ghost), modified = amber, table events = blue, unchanged = grey.

**Level 1: what changed and what didn't.**

- **Scope field:** "I meant to change", an optional layer glob such as `Facade::*`.
- **Summary sentence:** with a scope, "Outside `Facade::*`, N objects changed in M sets. K layers are untouched." Without one, "2,104 of 2,416 objects are identical. 3 change sets look suspicious."
- **Layer × property matrix:** one row per A layer. Columns: Placement, Shape, Layer, Color, Material, Name, User text, Added, Removed. Cells hold counts; empty cells are a dot. A status column says *untouched*, *N to check* or *expected*. Clicking a cell highlights its change set.

**Level 2: change sets,** in two groups: *Needs a look* first, then *Expected / signed off*. Each row shows the count, the layer, each property's from → to, and the flag reason. Actions: Show, Intended (with Undo), and Restore from A (Delete for added objects). If the restored object's layer or block is gone from B, it is recreated from A and listed (decided).

**Level 3: the object card.** The header gives status, name, "1 of 23 in set …" and how it was matched. Then *Changed · N* with from → to, flags and side effects ("color is ByLayer, so it now looks different though its own settings didn't change"). Then *Unchanged · M* as compact chips. Showing the unchanged properties is what gives confidence; leaving them out makes people wonder whether they were checked.

**Layer flow view.** A Sankey with A layers on the left and B layers on the right. Objects that stayed are faint bands; moves stand out; an unexpected move into `Default` is in the warning color. Untouched layers are collapsed. Clicking a band selects those objects in Rhino.

**Viewport (Rhino panel, v1):**

- Color modes: *Status* (default), *By property* (one color per changed property), *Layer from → to* (fill = new layer color, halo = old layer color).
- Layout modes: Overlay, Side by side, Swipe, Changes only. A's previous state can show as a ghost.

**Review panel behavior:**

- Causes first, objects second. Table events sit at the top and expand to their objects.
- Review is a to-do list. Rows can be ticked off, and that state is saved with the DiffResult, so a team can sign off "v12 → v13".
- Confidence is always visible as a badge. Fuzzy pairs get an Unlink button.
- Keyboard: J and K move to the next and previous row; Space marks it reviewed.

**Grasshopper components (v0):**

| Component | Inputs | Outputs | Notes |
| --- | --- | --- | --- |
| Snapshot | Source (file path or active doc), Layers filter | Snap | Cached per file version, so a slider downstream doesn't re-read the 3dm |
| Diff Settings | Key (stable-key name), Tol (default auto), Ignore (property toggles) | Cfg |  |
| Model Diff | A, B, Cfg | Added, Removed, Modified, Events, Result | Outputs plain GH geometry and attributes |
| Diff Filter | Result, Kind (e.g. moved), Layer glob | Objects, Info |  |
| Diff Preview | Result, Ghost A | None (viewport preview) | Status colors |
| Assert Unchanged | Result, Layers glob | Pass, Why | A QA gate; can run headless |
| Stamp IDs | Objects, Key | Objects | Opt-in; writes `diff:uid` |
| Open Review | Result | None | Sends the result to the Rhino panel |

## Data model

The core is a pure function from two snapshots and settings to a serializable `DiffResult`. This is a sketch to start from, not a frozen API. Names are proposals.

```csharp
// Entry point: no UI, no RhinoDoc dependency after snapshotting.
public static class ModelDiff
{
    public static ModelSnapshot Snapshot(File3dm file, SnapshotOptions opt);
    public static ModelSnapshot Snapshot(RhinoDoc doc, SnapshotOptions opt);
    public static DiffResult Diff(ModelSnapshot a, ModelSnapshot b, DiffSettings s);
    // Diff = TableDiff -> ObjectMatcher -> Comparer -> GeometryClassifier
    //        -> ChangeSetGrouper -> Detectors
}

public sealed record ModelSnapshot(
    string Source, DateTime CapturedAt, UnitSystem Units,
    double AbsTolerance, double AngleTolerance,
    IReadOnlyList<LayerRecord> Layers,
    IReadOnlyList<BlockRecord> Blocks,
    IReadOnlyList<TableRecord> Materials,
    IReadOnlyList<TableRecord> Linetypes,
    IReadOnlyList<GroupRecord> Groups,
    IReadOnlyList<ObjectRecord> Objects,
    Guid CurrentLayerId);

public enum ValueSource { ByObject, ByLayer, ByParent, ByMaterial }
public sealed record Sourced<T>(T Value, ValueSource Source);

public sealed record LayerRecord(
    Guid Id, string FullPath, Guid ParentId,
    int Color, Guid LinetypeId, Guid MaterialId, double PrintWidth,
    bool Visible, bool Locked);

public sealed record ObjectRecord(
    Guid Id, ObjectType Type, Guid LayerId, string? Name,
    Sourced<int> Color, Sourced<Guid> Material, Sourced<Guid> Linetype,
    Sourced<double> PrintWidth, bool Visible, bool Locked,
    IReadOnlyList<Guid> GroupIds,
    IReadOnlyDictionary<string, string> UserText,
    Guid? BlockDefinitionId, Transform? InstanceXform,
    GeometrySignature Geometry);

public sealed record GeometrySignature(
    ulong Hash,                    // canonical, coordinates snapped to tolerance
    string Kind, int Degree, int CvCount, ulong KnotHash,
    bool Closed, bool Periodic, bool Planar, bool Solid,
    int Faces, int Edges, int NakedEdges, int Vertices,
    double Length, double Area, double Volume,
    Point3d Centroid, BoundingBox BBox);
    // CV / vertex arrays are loaded lazily, only for pairs that reach steps 2-5.

public enum MatchedBy { StableKey, Guid, GeometryHash, ShapeSignature, Fuzzy, Manual }
public sealed record Match(Guid A, Guid B, MatchedBy By, double Confidence);

public enum TableEventKind { Added, Removed, Renamed, Reparented, Split, Merged, PropertyChanged }
public sealed record TableEvent(
    string Id, TableKind Table, TableEventKind Kind,
    Guid? AId, IReadOnlyList<Guid> BIds, string Detail,
    bool Inferred, double Confidence, int AffectedObjects);

public sealed record PropertyChange(
    Guid A, Guid B, string Property, string? From, string? To,
    ValueSource? FromSource, ValueSource? ToSource);

public enum GeometryLevel { Unchanged, DataOnly, Moved, Edited, Reshaped, TopologyChanged }
public sealed record GeometryChange(
    Guid A, Guid B, GeometryLevel Level, string Detail,
    Transform? Rigid, double MaxDeviation, double MeanDeviation,
    IReadOnlyList<int> MovedIndices);

public sealed record ChangeSet(
    string Id, string Signature,          // e.g. "layer:8c1e->0000"
    IReadOnlyList<Guid> Objects,
    IReadOnlyList<PropertyChange> Changes,
    IReadOnlyList<string> Flags,           // detector ids
    string? RootCauseEventId,              // links to a TableEvent
    ReviewState Review);                   // Unreviewed | Intended | Restored

public sealed record DiffSettings(
    string StableKey = "diff:uid",
    double? ShapeTolerance = null,         // null = document absolute tolerance
    double NudgeFactor = 10,               // nudge threshold = factor x tolerance
    double FuzzyThreshold = 0.75,
    double LayerInferShare = 0.60,
    string? ScopeGlob = null,
    PropertyToggles Toggles = default);

public sealed record DiffResult(
    SnapshotInfo A, SnapshotInfo B, DiffSettings Settings,
    IReadOnlyList<Match> Matches,
    IReadOnlyList<Guid> Added, IReadOnlyList<Guid> Removed,
    IReadOnlyList<TableEvent> TableEvents,
    IReadOnlyList<GeometryChange> Geometry,
    IReadOnlyList<ChangeSet> ChangeSets,
    int UnchangedCount);                   // serialized to JSON for sign-off
```

## Implementation notes

The building blocks are in RhinoCommon. Rigid-transform fitting, sequence diffs and hashing need custom code. Target Rhino 8.20 and later on .NET 8 (decided). Verify each API name against Rhino 8 before relying on it.

| Need | RhinoCommon | Note |
| --- | --- | --- |
| Read a file without opening it | `File3dm.Read(path)`; `Objects`, `AllLayers`, `AllInstanceDefinitions`, `AllMaterials`, `AllLinetypes`, `AllGroups`, `Settings.ModelAbsoluteTolerance` | Each `File3dmObject` has `Geometry` and `Attributes` |
| Active doc vs last save | `RhinoDoc.Objects` and tables for B; `File3dm.Read(doc.Path)` for A | An unsaved doc has no last save; a snapshot taken on open covers it later |
| Value sources | `ObjectAttributes.ColorSource`, `MaterialSource`, `LinetypeSource`, `PlotWeightSource` | Maps to `ValueSource` |
| User text | `ObjectAttributes.GetUserStrings()` | Diff per key |
| Curve deviation (step 2, 5) | `Curve.GetDistancesBetweenCurves` | Returns max and min distance and where they occur |
| Surface and Brep deviation | Sample points on B, `Brep.ClosestPoint` or `Mesh.ClosestPoint` on A | Same approach as Rhino's `PointDeviation` command |
| Structure (step 4) | `NurbsCurve.Points`, `Knots`; `NurbsSurface.Points`; `Mesh.Vertices`; `SubD.Vertices` | Use `ToNurbsCurve()` to compare mixed curve types |
| Topology (step 5) | `Brep.Faces`, `Brep.Edges`, `BrepEdge.Valence == EdgeAdjacency.Naked`, `Brep.IsSolid`; `Mesh.IsClosed`, `Mesh.IsManifold` |  |
| Polyline vertex diff | `Curve.TryGetPolyline`, then an LCS or Myers diff with tolerance equality | Reports inserted, removed, moved and untouched vertices |
| Rigid fit (step 3) | Custom Kabsch (SVD) on CVs or sample points | Accept when residual ≤ shape tolerance; determinant −1 means mirrored |
| Nearest candidate (pass 4) | `RTree` on bounding-box centers |  |
| Viewport overlay | `DisplayConduit` | Status colors, ghosts, halos |
| Review panel | Eto.Forms dockable panel registered with `Panels.RegisterPanel` |  |

**Hashing.** Write a canonical serialization (type tag + coordinates snapped to the tolerance grid) and hash it, for example with xxHash64. Don't hash Rhino's binary serialization, which can carry non-geometric data. A value near a grid boundary can snap differently. That only sends the pair to step 2, so it costs time, never correctness.

**Performance.** Hash every object once per snapshot. Run steps 2–5 only on hash-differing pairs, in parallel, since geometry is read-only. Read files off the UI thread. In GH, cache snapshots by path, file size and last-write time.

**Tolerances.** Shape tolerance defaults to the document's absolute tolerance. The nudge threshold defaults to 10 × that. If A and B use different units or tolerances, convert to A's units and use the larger tolerance, and say so in the result header.

## Decisions

| Topic | Choice | Status |
| --- | --- | --- |
| Main use | Incoming files (two .3dm files) first, so the tool opens on two files by default. Self-check is second | Decided |
| Table identity (layers, blocks, materials, linetypes) | Stable ID first, then path or name, then inferred from object membership | Decided |
| User text | Diffed per key: added, removed, changed | Decided |
| Nested block changes | Reported on each affected instance, with the inner definition as root cause | Decided |
| Appearance | Own changes by default; effective appearance as a separate view | Decided |
| Data-only geometry (rebuilds, knot changes) | Shown in a collapsed bucket; never counted as a change; never fails a QA gate | Decided |
| Stamp IDs | Manual only, when the user runs the command or component | Decided |
| Rhino target | Rhino 8.20+ on .NET 8 only (replaces the earlier net48 + net7 plan) | Decided |
| Worksessions, linked blocks, referenced files | Not in v1; skipped and noted in the result header | Decided |
| Object join and split | v0 and v1 report removed + added; v2 links them ("split into 3", "joined from 4") | Decided |
| Restore from A with a missing layer or block | Recreate the layer or block from A, and list what was recreated | Decided |
| Detector thresholds | Keep the starting values (lone deletion ≤ 3 objects, mass change ≥ 3 layers); tune on real files | Decided |
| Platform | UI-free core; GH components v0; Rhino panel v1; headless later | Proposed |
| Object matching | Five-pass cascade, each pair with `matchedBy` and confidence | Proposed |
| Geometry classification | Six-level ladder, steps 2–5 only on hash-differing pairs | Proposed |

## Open questions

None right now. The owner answered the seven earlier questions on 2026-11-27; the answers are in Decisions. New questions go here as unticked tasks.

## Roadmap

1. **v0a, core and fixtures.** Snapshot, table diff, matching passes 1–3, property compare, ladder steps 1–3, change sets, JSON output. Gate: the fixture suite passes, and noise is measured on two or three real project revisions.
2. **v0, Grasshopper.** The components in the UX table, matching passes 4–5, ladder steps 4–5, detectors. Gate: the owner uses it on a real revision and trusts the result.
3. **v1, Rhino panel and conduit.** Review list, matrix, object card, layer flow view, viewport modes, sign-off saved with the result, Restore from A.
4. **Later.** Snapshot history sidecar, headless runs on Rhino.Compute or CI, per-face Brep matching, object join and split detection (v2), then worksessions, linked blocks and referenced files.

## Test plan

Build a fixture pair of .3dm files for each case, with the expected `DiffResult` stored as golden JSON. Track two numbers per fixture: reported rows against true changes (noise), and wrong pairings (must be zero for certain passes).

| Fixture | Edit made in B | Expected result |
| --- | --- | --- |
| layer-rename | Rename `Walls` to `A-WALL` | 1 Renamed event, 0 object rows |
| layer-reparent | Move `Furniture` under a new `Interior` | 1 Reparented + 1 Added layer event |
| layer-split | Split `Slabs` across two new layers | 1 Split event, inferred, with shares |
| bylayer-color | Change a layer color | 1 layer event, 0 own object changes |
| gh-rebake | Delete and re-bake everything from GH | All matched by hash, 0 changes |
| paste-between-files | Copy objects into a new file | Matched by hash or shape signature |
| tiny-nudge | Move one column 0.8 mm | Moved, flagged Tiny nudge |
| default-layer | Move 23 objects to `Default` | 1 change set, flagged |
| curve-rebuild | Rebuild a curve, keeping its shape | Data-only |
| cv-edit | Drag one control point | Edited, CV index and distance |
| seam-and-flip | Move a closed curve's seam; flip another | Both Data-only |
| brep-open | Delete one face of a solid | Topology changed: solid became open |
| nested-block | Edit a nested block definition | Each instance reports it; definition is root cause |
| user-text | Change, add and remove keys | One change per key |
| copy-in-place | Duplicate an object in place | Added, flagged Duplicate |

## Sources

Interactive HTML mocks from the design session. Open them in a browser:

- [p1-gh-fit.html](mocks/p1-gh-fit.html): GH fit and architecture
- [p2-matching.html](mocks/p2-matching.html): GUID vs cascade demo
- [p3-layers.html](mocks/p3-layers.html): layer mapping and root causes
- [p4-ui.html](mocks/p4-ui.html): GH definition and Rhino review panel
- [p5-props-a.html](mocks/p5-props-a.html): scope, matrix and change sets
- [p5-props-b.html](mocks/p5-props-b.html): object cards, layer flow, viewport modes, detectors
- [p6-geometry.html](mocks/p6-geometry.html): geometry ladder and examples
