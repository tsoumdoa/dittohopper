<img src="assets/icon.svg" width="64" height="64" alt="">

# DittoHopper

DittoHopper is an MIT open-source Grasshopper plugin for Rhino 8 that compares two Rhino
models and shows what changed, and just as clearly what did not.

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

Drop the `.yak` file onto a Rhino window, accept the installation, and restart Rhino. The
components are on Grasshopper's **DittoHopper** tab.

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

Layer patterns: `Structure::*` is every layer under `Structure` (not `Structure` itself); `*` matches any
text, `::` included; `?` one character; case is ignored.

**Try it:** two Snapshots (one with Source set to the last revision's `.3dm`, one with Source
empty for the open document) into Model Diff A and B, a Panel on Result, and Diff Preview. Add
Diff Settings with Scope set to what you meant to change, and the summary says what changed outside it.

**Limitations (v0):** files are read on Rhino's main thread, so a large model pauses Grasshopper
while it is read (once per file version). Geometry outputs give a block instance as its
insertion point; Diff Preview draws the whole block.

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
| `src/DittoHopper.Core` | The diff engine: matching, comparing, change sets, detectors. No Rhino types |
| `src/DittoHopper.Rhino` | Snapshots `.3dm` files and documents; geometry deviation and topology |
| `src/DittoHopper.Grasshopper` | The Grasshopper components (`.gha`) |
| `tests/` | Unit tests, plus fixture tests that generate `.3dm` pairs at run time |
| `docs/design.md` | The design spec |

## License

[MIT](LICENSE)
