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

**Status: in development.** Nothing is released yet. See the [design](docs/design.md) for
the full spec and roadmap.

## Requirements

Rhino 8.20 or later on Windows, running .NET 8 (use Rhino's `SetDotNetRuntime` command if
needed). macOS, Rhino 7 and .NET Framework are not supported yet.

## Build

```powershell
dotnet build DittoHopper.sln -c Debug   # warnings are errors
dotnet test DittoHopper.sln -c Debug    # Rhino-free tests
git config core.hooksPath .githooks     # local pre-commit build + test
```

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
