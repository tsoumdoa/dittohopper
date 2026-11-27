using DittoHopper.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;

namespace DittoHopper.Grasshopper;

/// <summary>Opt-in: writes a stable id into the user text of referenced document objects that lack one.</summary>
public sealed class StampIdsComponent : DittoComponent
{
    public StampIdsComponent()
        : base("Stamp IDs", "Stamp",
            "WRITES TO THE RHINO MODEL, only while Run is True: gives each referenced object that has no id a new one in its user text " +
            "(key diff:uid by default). Objects that already have one are left alone. Stamped objects are matched by that id in every " +
            "later diff, even after a re-bake, copy or rebuild. One Undo reverts each run. Every other DittoHopper component only reads.",
            WritePanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a07");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.StampIds;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Objects", "Objects", "Objects in the active Rhino document: referenced geometry or object ids.", GH_ParamAccess.list);
        p.AddTextParameter("Key", "Key", "User-text key to write. Use the same Key in Diff Settings if it is not diff:uid.",
            GH_ParamAccess.item, DiffSettings.DefaultStableKey);
        p.AddBooleanParameter("Run", "Run", "True writes the ids. False only reports what would be written.", GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddGenericParameter("Objects", "Objects", "The input objects, passed through.", GH_ParamAccess.list);

    protected override void BeforeSolveInstance()
    {
        Message = null;
        base.BeforeSolveInstance();
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        var objects = new List<IGH_Goo>();
        da.GetDataList(0, objects);
        string key = DiffSettings.DefaultStableKey;
        da.GetData(1, ref key);
        bool run = false;
        da.GetData(2, ref run);
        da.SetDataList(0, objects);

        key = key?.Trim() ?? "";
        if (key.Length == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Key is empty.");
            return;
        }
        var doc = OnPingDocument()?.RhinoDocument ?? RhinoDoc.ActiveDoc;
        if (doc is null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "There is no active Rhino document.");
            return;
        }

        var result = Stamp(doc, objects.Select(Id), key, run);
        if (result.NotInDocument > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"{Describe.Plural(result.NotInDocument, "item")} not referenced from this document (internalised geometry, or deleted): skipped.");
        if (result.Shared > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"{Describe.Plural(result.Shared, "object")} share a {key} with another object, probably copies. Diffs can't tell them apart; " +
                "delete the value from the copies and stamp again.");
        string summary = run
            ? $"Stamped {Describe.Plural(result.Stamped, "object")}; {Describe.Count(result.Had)} already had {key}."
            : $"Run is False, so nothing was written. {Describe.Plural(result.Stamped, "object")} would be stamped; {Describe.Count(result.Had)} already have {key}.";
        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, summary);
        Message = run ? $"Stamped {result.Stamped}" : "Not run";
    }

    public readonly record struct Outcome(int Stamped, int Had, int Shared, int NotInDocument);

    /// <summary>Writes a new id into each object that has none (or counts them, when <paramref name="run"/> is false).</summary>
    public static Outcome Stamp(RhinoDoc doc, IEnumerable<Guid?> ids, string key, bool run)
    {
        int stamped = 0, had = 0, missing = 0;
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        uint undo = run ? doc.BeginUndoRecord("Stamp IDs") : 0;
        try
        {
            foreach (var id in ids.Distinct())
            {
                if (id is not Guid g || doc.Objects.FindId(g) is not { } obj)
                {
                    missing++;
                    continue;
                }
                if (obj.Attributes.GetUserString(key) is { Length: > 0 } existing)
                {
                    had++;
                    values[existing] = values.GetValueOrDefault(existing) + 1;
                    continue;
                }
                stamped++;
                if (!run) continue;
                var attributes = obj.Attributes.Duplicate();
                attributes.SetUserString(key, Guid.NewGuid().ToString());
                doc.Objects.ModifyAttributes(obj, attributes, true);
            }
        }
        finally
        {
            if (undo != 0) doc.EndUndoRecord(undo);
        }
        return new Outcome(stamped, had, values.Values.Where(n => n > 1).Sum(), missing);
    }

    private static Guid? Id(IGH_Goo? goo)
    {
        switch (goo)
        {
            case null: return null;
            case IGH_GeometricGoo { IsReferencedGeometry: true } referenced: return referenced.ReferenceID;
            case GH_Guid guid: return guid.Value;
        }
        return GH_Convert.ToGUID(goo, out Guid id, GH_Conversion.Both) && id != Guid.Empty ? id : null;
    }
}
