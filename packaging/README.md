# DittoHopper

Compare two Rhino models and see what changed, and what did not.
For Rhino 8.20+ on Windows, running .NET 8.

Drop the `.yak` file onto a Rhino window, accept the installation, then restart
Rhino. The package holds a Rhino plug-in and the Grasshopper components.

In Rhino, run `DittoReview`: it compares the open document with its last save
(options: A or B from a .3dm file) and opens the DittoHopper Review panel, with
the changes grouped, what didn't change, an object card, Zoom to and Select in
Rhino, and a viewport overlay in the status colours.

In Grasshopper, the components are on the **DittoHopper** tab:

- **Diff:** Snapshot, Diff Settings, Model Diff
- **Review:** Diff Filter, Diff Preview, Assert Unchanged, Open Review (sends a result to the Rhino panel)
- **Write:** Stamp IDs, the only component that changes the Rhino model: once per press of a Button wired to Run

Quick start: two Snapshot components (Source = a .3dm path each, or leave one
empty for the open document) into Model Diff A and B. Put a Panel on Result for
the summary, and connect Diff Preview to see the changes in colour.

Use Rhino's `SetDotNetRuntime` command to select .NET 8 if necessary. macOS,
Rhino 7 and .NET Framework are not supported. Keep the package files together:
the .rhp and the .gha share the DLLs beside them.

Source, documentation and issues: https://github.com/tsoumdoa/dittohopper
