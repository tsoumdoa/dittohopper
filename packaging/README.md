# DittoHopper

Compare two Rhino models in Grasshopper and see what changed, and what did not.
For Rhino 8.20+ on Windows, running .NET 8.

Drop the `.yak` file onto a Rhino window, accept the installation, then restart
Rhino. In Grasshopper, the components are on the **DittoHopper** tab:

- **Diff:** Snapshot, Diff Settings, Model Diff
- **Review:** Diff Filter, Diff Preview, Assert Unchanged
- **Write:** Stamp IDs, the only component that changes the Rhino model: once per press of a Button wired to Run

Quick start: two Snapshot components (Source = a .3dm path each, or leave one
empty for the open document) into Model Diff A and B. Put a Panel on Result for
the summary, and connect Diff Preview to see the changes in colour.

Use Rhino's `SetDotNetRuntime` command to select .NET 8 if necessary. macOS,
Rhino 7 and .NET Framework are not supported. Keep the package files together:
the .gha needs the DLLs beside it.

Source, documentation and issues: https://github.com/tsoumdoa/dittohopper
