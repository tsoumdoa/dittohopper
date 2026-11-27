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
