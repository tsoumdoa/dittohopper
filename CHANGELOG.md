# Changelog

## Unreleased

- Project scaffold: solution, build settings, local pre-commit hook and design spec.
- Diff core (M1): snapshot and result model, layer/block/material/linetype/group table diff with
  inferred layers and splits, object matching passes 1–3, property compare through the table maps,
  change sets linked to root-cause events, units/tolerance header, canonical hashing and stable JSON.
- Rhino snapshot (M2): snapshots of .3dm files, live documents and "active document vs last save";
  canonical geometry hash per type; geometry ladder steps 2–3 (Data-only by sampled deviation,
  Moved/rotated/mirrored by a rigid fit) behind a geometry service; `ShapeTolerance`; instance
  transforms compared as a delta; gated Rhino fixture suite with golden JSON and an opt-in noise report.
- Matching and detectors (M3): matching pass 4 (shape signature + nearest, unambiguous pairs only) and
  pass 5 (fuzzy score ≥ `FuzzyThreshold`, marked as needing review); Extrusion, Surface and Brep matched as
  one kind family; geometry ladder steps 4–5 (Edited with moved CV/vertex indices, "moved + edited", polyline
  vertex sequence diff, Reshaped with deviation, Topology changed with what broke), with the topology summary
  measured once into the snapshot signature; accidental-change detectors in `ChangeSet.Flags`
  (`ScopeGlob`, `NudgeFactor`) and `NeedsLook`; per-layer rows and the Level 1 summary sentence (`DiffSummary`).
- Grasshopper components (M4): Snapshot (files cached per version; the live document; the last
  save), Diff Settings, Model Diff, Diff Filter, Diff Preview, Assert Unchanged and the opt-in
  Stamp IDs, on a DittoHopper tab with icons drawn in code. Yak packaging (`tools/Pack-Yak.ps1`)
  and headless Grasshopper tests. Diff Settings' Scope, the Level 1 summary sentence, detector flags and
  fuzzy pairings to review come from the M3 core.
- Rhino plug-in (v1a), version 0.2.0-beta.1: `DittoHopper.rhp` in the same Yak package as the `.gha`. The
  `DittoReview` command compares the open document with its last save, or A and/or B from a file, and opens a
  dockable review panel: A → B, Scope (re-diffs), Gate (Assert Unchanged's check), the summary and Refresh; the
  Changes tab (Needs a look, Table events, In scope or Changes) and the Unchanged tab (identical objects per
  layer, and those that look different only through a table entry); the object card (Changed with from → to and
  why, Unchanged); Zoom to, Select in Rhino (when B is the document), J/K/F, hover outlines. A display conduit
  draws the status colours and A's ghost (the live document's objects when B is the document; snapshots in the
  document's units for two files), dims all but the selected row, and draws the HUD: card, tags, nudge rings,
  strip, or off. Open Review (GH) hands a result to the panel through `ReviewHub`, a static in the shared
  `DittoHopper.Rhino.dll`.
- One presentation model for every shell: `Review` in Core (groups, rows, chips, card, unchanged summary, gate);
  `Describe`, `LayerPatterns`, `Vocabulary` and the gate moved from the `.gha` to Core, `DiffRun` and Diff
  Preview's drawing to `DittoHopper.Rhino`. Diff Filter's "properties unchanged" now counts the properties the
  object card lists.
