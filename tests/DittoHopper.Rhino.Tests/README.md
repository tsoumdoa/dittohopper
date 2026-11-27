# Rhino integration tests

These tests run headless Rhino 8 (RhinoCore, in process) on Windows x64. Plain `dotnet test` and
the pre-commit hook report them as skipped and never load Rhino.

They need an installed, licensed Rhino 8.20 or later. RhinoCommon and its dependencies load from the
Rhino `System` folder; no Rhino binaries are copied, and no Rhino.Inside resolver is needed. The tests
run serially on one STA thread that shares one RhinoCore. They never start, attach to or close an
interactive Rhino.

| Variable | Meaning |
| --- | --- |
| `DITTOHOPPER_RHINO_TESTS` | `1` to run the tests; anything else skips them |
| `DITTOHOPPER_RHINO_SYSTEM` | Rhino `System` folder. Default: `C:/Program Files/Rhino 8/System` |

```powershell
$env:DITTOHOPPER_RHINO_TESTS = '1'
# $env:DITTOHOPPER_RHINO_SYSTEM = 'D:/Rhino 8/System'   # only if Rhino is not in the default place
try {
    dotnet test tests/DittoHopper.Rhino.Tests -c Debug --filter Category=RhinoIntegration
} finally {
    Remove-Item Env:DITTOHOPPER_RHINO_TESTS, Env:DITTOHOPPER_RHINO_SYSTEM -ErrorAction SilentlyContinue
}
```

A cold run takes about 5 s, and about 3.7 s of that is the RhinoCore boot.

## Grasshopper tests

`tests/DittoHopper.Grasshopper.Tests` shares this harness (`RhinoHost`, `FixturePair` and `Model`
are linked in) and the same variable. It boots RhinoCore without `/safemode`, because
Grasshopper is a plug-in, then loads the built `.gha` and solves real definitions: every
component loads with its icon and stable GUID; Snapshot → Model Diff → Diff Filter, Assert
Unchanged and Diff Preview on a generated pair; and Snapshot and Stamp IDs on a headless document.
Its plain tests (layer patterns, settings names, result wording) always run.

```powershell
$env:DITTOHOPPER_RHINO_TESTS = '1'
try { dotnet test tests/DittoHopper.Grasshopper.Tests -c Debug }
finally { Remove-Item Env:DITTOHOPPER_RHINO_TESTS -ErrorAction SilentlyContinue }
```

## Fixture pairs

No `.3dm` file is committed. `FixturePair` writes each A/B pair into a temp folder for the run,
and the folder is deleted when the run ends:

- `FixturePair.Create(name, build, editB)` builds A, then reads A back and edits it to make B.
  GUIDs survive, as they do for an in-place edit.
- `FixturePair.CreateFromScratch(name, build, buildB)` builds B from nothing, so every GUID
  differs, as after a re-bake or a rebuild.

Run anything that touches RhinoCommon through `host.Run(...)`, so it runs on the Rhino thread.
Enumerate `File3dm.Objects` with `foreach`. Its `IList` indexer and `CopyTo` return nulls, so LINQ
`Select`/`ToList` on it breaks.

## Fixture suite and golden files

`FixtureTests` builds every test-plan row from `docs/design.md` with `FixturePair`, snapshots
both sides with `RhinoSnapshot`, diffs them with the geometry ladder and compares the result with
`golden/<fixture>.json`. Each fixture also asserts the plan's two numbers: noise (reported rows,
meaning table events plus change sets, minus true changes) is 0, and no pair is wrong. Every
top-level object carries a truth label in user text (`fixture:truth`), which is stripped before
the diff. GUIDs are renumbered in record order, so the golden files don't change between runs.

To rewrite the golden files after an intended change, set `DITTOHOPPER_UPDATE_GOLDEN=1` for one
run, then review the diff before committing.

## Noise report

`NoiseReportTests` generates building-like models (about 3,000 objects, nested layers and blocks,
groups, user text) and realistic B sides: a consultant revision with a partial rebuild that loses
GUIDs, Grasshopper re-bakes, an in-place conversion, and a save/reopen round trip. For each pair it
prints reported rows against true changes, wrong pairings and timings. It gates nothing and writes
nothing to the repository. It needs its own variable, so neither the hook nor the command above
runs it:

```powershell
$env:DITTOHOPPER_RHINO_TESTS = '1'; $env:DITTOHOPPER_NOISE_REPORT = '1'
try {
    dotnet test tests/DittoHopper.Rhino.Tests -c Debug --filter Category=NoiseReport --logger "console;verbosity=detailed"
} finally {
    Remove-Item Env:DITTOHOPPER_RHINO_TESTS, Env:DITTOHOPPER_NOISE_REPORT -ErrorAction SilentlyContinue
}
```
