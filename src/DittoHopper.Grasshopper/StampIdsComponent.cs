using DittoHopper.Core;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.DocObjects;

namespace DittoHopper.Grasshopper;

/// <summary>Opt-in: writes a stable id into the user text of referenced document objects that lack one.</summary>
public sealed class StampIdsComponent : DittoComponent
{
    /// <summary>
    /// Run's value at the previous solution, recorded after every solution (even one that never reached
    /// SolveInstance); null until the first, so opening a file never writes.
    /// </summary>
    private bool? lastRun;
    private string? lastWrite;

    public StampIdsComponent()
        : base("Stamp IDs", "Stamp",
            "WRITES TO THE RHINO MODEL, once each time Run turns True: wire a Button to Run and press it. Gives each referenced " +
            "object that has no id a new one in its user text (key diff:uid by default) and leaves objects that have one alone. " +
            "Opening the file, recomputing or connecting more objects never writes. One Undo reverts each press. Stamped objects " +
            "are matched by that id in every later diff, as long as re-bakes and copies carry the user text forward. Every " +
            "other DittoHopper component only reads.",
            WritePanel)
    { }

    public override Guid ComponentGuid => new("5b0f7e21-3c4a-4f6e-9a51-2d8c1b7e0a07");
    public override GH_Exposure Exposure => GH_Exposure.primary;
    protected override ComponentIcon IconKind => ComponentIcon.StampIds;

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("Objects", "Objects",
            "Objects in the active Rhino document: referenced geometry or object ids, in any number of branches (one press stamps them all).",
            GH_ParamAccess.tree);
        p[0].Optional = true;
        p.AddTextParameter("Key", "Key", "User-text key to write. Use the same Key in Diff Settings if it is not diff:uid.",
            GH_ParamAccess.item, DiffSettings.DefaultStableKey);
        p.AddBooleanParameter("Run", "Run",
            "Wire a Button: each press (False → True) writes once. Holding True, recomputing or opening the file never writes.",
            GH_ParamAccess.item, false);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p) =>
        p.AddGenericParameter("Objects", "Objects", "The input objects, passed through.", GH_ParamAccess.tree);

    protected override void BeforeSolveInstance()
    {
        Message = null;
        base.BeforeSolveInstance();
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        da.GetDataTree(0, out GH_Structure<IGH_Goo> tree);
        var objects = tree?.AllData(true).ToList() ?? [];
        string key = DiffSettings.DefaultStableKey;
        da.GetData(1, ref key);
        bool run = false;
        da.GetData(2, ref run);
        if (tree is not null) da.SetDataTree(0, tree);
        bool write = run && lastRun == false && da.Iteration == 0; // a press: one write per solution, never the first after opening

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

        var result = Stamp(doc, objects.Select(Id), key, write);
        if (result.NotInDocument > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"{Describe.Plural(result.NotInDocument, "item")} not referenced from this document (internalised geometry, or deleted): skipped.");
        if (result.Failed > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{Describe.Plural(result.Failed, "object")} could not be changed (locked?).");
        if (result.Shared > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                $"{Describe.Plural(result.Shared, "object")} in this document share a {key} value with another object, probably copies. " +
                "Diffs can't tell them apart: delete the value from the copies and stamp again.");
        if (write)
        {
            lastWrite = $"Stamped {result.Stamped}";
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"Stamped {Describe.Plural(result.Stamped, "object")}; {Describe.Count(result.Had)} already had {key}.");
        }
        else
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"Nothing written. {Describe.Plural(result.Stamped, "object")} would be stamped; {Describe.Count(result.Had)} already have {key}. " +
                "Press the Button on Run to write.");
        }
        Message = lastWrite ?? "Not run";
    }

    protected override void AfterSolveInstance()
    {
        lastRun = Params.Input[2].VolatileData.AllData(true).OfType<GH_Boolean>().Any(b => b.Value);
        base.AfterSolveInstance();
    }

    public readonly record struct Outcome(int Stamped, int Had, int Shared, int NotInDocument, int Failed);

    /// <summary>
    /// Writes a new id into each object that has none, in one undo record, counting only successful writes; when
    /// <paramref name="write"/> is false, counts what it would write. Shared counts objects anywhere in the document
    /// whose value another object also has.
    /// </summary>
    public static Outcome Stamp(RhinoDoc doc, IEnumerable<Guid?> ids, string key, bool write)
    {
        int stamped = 0, had = 0, missing = 0, failed = 0;
        var targets = new List<RhinoObject>();
        foreach (var id in ids.Distinct())
        {
            if (id is not Guid g || doc.Objects.FindId(g) is not { } obj) missing++;
            else if (!string.IsNullOrEmpty(obj.Attributes.GetUserString(key))) had++;
            else targets.Add(obj);
        }

        if (!write)
        {
            stamped = targets.Count;
        }
        else if (targets.Count > 0)
        {
            bool recording = doc.UndoRecordingEnabled;
            doc.UndoRecordingEnabled = true;
            uint undo = doc.BeginUndoRecord("Stamp IDs");
            try
            {
                foreach (var obj in targets)
                {
                    var attributes = obj.Attributes.Duplicate();
                    attributes.SetUserString(key, Guid.NewGuid().ToString());
                    if (doc.Objects.ModifyAttributes(obj, attributes, true)) stamped++;
                    else failed++;
                }
            }
            finally
            {
                if (undo != 0) doc.EndUndoRecord(undo);
                doc.UndoRecordingEnabled = recording;
            }
        }
        return new Outcome(stamped, had, SharedValues(doc, key), missing, failed);
    }

    /// <summary>Objects in the whole document whose value under <paramref name="key"/> another object also has.</summary>
    private static int SharedValues(RhinoDoc doc, string key)
    {
        var all = new ObjectEnumeratorSettings { NormalObjects = true, LockedObjects = true, HiddenObjects = true, IncludeLights = true };
        return doc.Objects.GetObjectList(all)
            .Select(o => o.Attributes.GetUserString(key))
            .Where(v => !string.IsNullOrEmpty(v))
            .GroupBy(v => v, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Sum(g => g.Count());
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
