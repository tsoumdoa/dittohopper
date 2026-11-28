<img src="assets/icon.svg" width="64" height="64" alt="">

# DittoHopper

DittoHopper is an MIT open-source plug-in for Rhino 8 that compares two Rhino models and shows
what changed, and just as clearly what did not. Review in Rhino with the `DittoReview` command,
its panel and a viewport overlay; automate diffs and QA gates with the Grasshopper components.

- **Incoming file:** "What did the consultant actually change between rev B and rev C?"
- **Self-check:** "I meant to change only `Facade::*`. Did I touch anything else?"

Each change is reported once, at its root cause: one layer rename is one change, not 212
object rows. Objects are matched by a cascade (stable key, GUID, geometry hash, shape, fuzzy),
and every pair records how it was matched and how sure that is. Likely accidents (a stray
nudge, objects dropped on `Default`, a select-all edit) are flagged first.

**Status: early beta.** See the [design](docs/design.md) for the full spec and roadmap.

## Install

Rhino 8.20 or later on Windows, running .NET 8 (use Rhino's `SetDotNetRuntime` command if
needed). macOS, Rhino 7 and .NET Framework are not supported yet.

Drop the `.yak` file onto a Rhino window, accept the installation, and restart Rhino. One package
holds both plug-ins: the `DittoReview` command and its panel in Rhino, and the components on
Grasshopper's **DittoHopper** tab.

## Review in Rhino

Run **`DittoReview`**. By default it compares the open document (B) with its last save (A): "what
did I change this session?". Its options make A another `.3dm` file (`A=File`), and B a file too
(`B=File`) for an incoming revision. The **DittoHopper Review** panel opens with:

- **A → B** with units and tolerance; **Scope**, what you meant to change (a layer pattern such
  as `Facade::*`: changes outside it are flagged, and the summary counts them); **Gate**, layers
  that must not change, checked as Assert Unchanged checks them (empty: no gate); the summary
  sentence; and **Refresh**, which reads A and B again.
- **Changes**: *Needs a look* (flagged, or outside the scope), then *Table events* (layers,
  blocks, materials, linetypes, groups), then *In scope* (or *Changes* without a scope). Each row
  shows its status colour, what changed, its flags and how its objects were matched.
- **Unchanged**: per layer, how many objects were checked and found identical, and which of them
  look different only because a layer, block, material or linetype they use changed.
- The **object card** for the selected row: *Changed* with from → to and why it was flagged, and
  *Unchanged*, the compared properties that stayed the same. **Zoom to** (`F`), and **Select in
  Rhino** when B is the open document. `J` and `K` move between rows.

The viewport draws the review: added green, removed red, modified amber, blue where only a table
entry changed, unchanged slate, and A's previous state as a ghost. When B is the open document,
its objects are drawn in their status colour (selected ones as Rhino draws them) and follow your
edits; two files are drawn from their snapshots, in the document's units. Selecting a row dims
everything else; hovering one outlines its objects. The HUD is display only: a card with the
summary, counts and gate (or a one-line strip, or nothing: the panel's Viewport setting), tags on
the rows that need a look and on the selected one, and a ring around a change too small to see.
Closing the panel stops the drawing.

From Grasshopper, **Open Review** sends a Model Diff result to the same panel.

## Components

| Component | Inputs | Outputs | What it does |
| --- | --- | --- | --- |
| Snapshot | Source, Layers, Saved | Snapshot | Captures a `.3dm` file (cached until the file changes), or the open document when Source is empty: live, or as last saved with Saved = True |
| Diff Settings | Key, Tolerance, Ignore, Scope | Settings | Optional: the user-text key that identifies objects, the shape tolerance (empty = auto), properties to ignore, and Scope: what you meant to change, e.g. `Facade::*` |
| Model Diff | A, B, Settings | Added, Removed, Modified, Events, Result | Diffs A (before) against B (after). Geometry comes out in the Rhino document's units (A's without one), as Diff Preview draws it. A Panel on Result shows the summary sentence and up to 20 change sets, one line each, suspicious ones first |
| Diff Filter | Result, Kind, Layers | Objects, Info | Changed objects by kind (`placement`/`moved`, `layer`, `color`, `added`, `needs look`, `review`, a detector such as `tiny-nudge`…) and layer; Info says what changed, what didn't and how each was matched |
| Diff Preview | Result, Ghost A | | Draws the diff: added green, removed red, modified amber, unchanged grey, blue where only a layer, block, material or linetype the object uses changed |
| Assert Unchanged | Result, Layers | Pass, Why | A QA gate: fails, with reasons, if anything on those layers changed, if a fuzzy pairing there needs review, or if any pattern matches no layer. It sees only what the Snapshots captured, so a Snapshot Layers filter limits it |
| Stamp IDs | Objects, Key, Run | Objects | Writes a `diff:uid` into objects that lack one, once per press of a Button on Run. The only component that changes the model |
| Open Review | Result | | Opens the result in the Rhino review panel, once per new result |

Layer patterns: `Structure::*` is every layer under `Structure` (not `Structure` itself); `*` matches any
text, `::` included; `?` one character; case is ignored.

**Try it:** two Snapshots (one with Source set to the last revision's `.3dm`, one with Source
empty for the open document) into Model Diff A and B, a Panel on Result, and Diff Preview. Add
Diff Settings with Scope set to what you meant to change, and the summary says what changed outside it.

**Limitations:** files are read on Rhino's main thread, so a large model pauses Rhino while it is
read (in Grasshopper, once per file version). Geometry outputs give a block instance as its
insertion point; Diff Preview and the review viewport draw the whole block. Review state (ticks,
intended, sign-off), the Layers matrix and Restore from A come in v1b.

## Build

```powershell
dotnet build DittoHopper.sln -c Debug   # warnings are errors
dotnet test DittoHopper.sln -c Debug    # Rhino-free tests
git config core.hooksPath .githooks     # local pre-commit build + test
```

Rhino integration tests are opt-in; see [tests/DittoHopper.Rhino.Tests](tests/DittoHopper.Rhino.Tests/README.md).

Requires the .NET 8 SDK or later. There is no cloud CI: the pre-commit hook builds and
tests every commit that touches code.

## Layout

| Path | What it is |
| --- | --- |
| `src/DittoHopper.Core` | The diff engine: matching, comparing, change sets, detectors; and the review's words (rows, cards, gate) for every shell. No Rhino types |
| `src/DittoHopper.Rhino` | Snapshots `.3dm` files and documents; geometry deviation and topology; the shared viewport drawing; the hand-off from Grasshopper to the panel |
| `src/DittoHopper.Grasshopper` | The Grasshopper components (`.gha`) |
| `src/DittoHopper.PlugIn` | The Rhino plug-in (`DittoHopper.rhp`): the command, the review panel and the viewport HUD |
| `tests/` | Unit tests, plus fixture tests that generate `.3dm` pairs at run time |
| `docs/design.md` | The design spec |

## License

[MIT](LICENSE)
