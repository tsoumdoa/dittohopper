using Rhino.DocObjects;
using Rhino.DocObjects.Tables;
using RhinoDoc = Rhino.RhinoDoc;

namespace DittoHopper.Rhino;

/// <summary>
/// Counts the changes to each open document that a snapshot of it would see, so a live snapshot can tell later whether
/// its document has moved on (<see cref="RhinoSnapshot.DocumentVersion"/>). Started by the first live snapshot and
/// shared by both plug-ins, since they load this assembly once. Use on Rhino's UI thread.
/// <para>
/// The events are exactly what <see cref="RhinoSnapshot.FromDocument"/> reads: the object table (lights included) and
/// object attributes; the layer (and current layer), material, linetype, group, hatch pattern, dimension style and
/// block tables; and the units and tolerances. Reading anything new there means subscribing to its event here.
/// RhinoCommon has no catch-all document change event, and headless documents raise none of these.
/// </para>
/// </summary>
public static class DocumentChanges
{
    private static readonly Dictionary<uint, long> counts = [];
    private static readonly Dictionary<uint, (string Units, double Tolerance, double Angle)> settings = [];
    private static bool started;

    /// <summary>Raised after a change to a document is counted.</summary>
    public static event Action<RhinoDoc>? Changed;

    /// <summary>Subscribes once; later calls do nothing.</summary>
    public static void Start()
    {
        if (started) return;
        started = true;
        RhinoDoc.AddRhinoObject += (_, e) => Note(e.TheObject?.Document);
        RhinoDoc.DeleteRhinoObject += (_, e) => Note(e.TheObject?.Document);
        RhinoDoc.UndeleteRhinoObject += (_, e) => Note(e.TheObject?.Document);
        RhinoDoc.ReplaceRhinoObject += (_, e) => Note(e.Document);
        RhinoDoc.ModifyObjectAttributes += (_, e) => Note(e.Document);
        RhinoDoc.LightTableEvent += (_, e) => Note(e.Document);
        RhinoDoc.LayerTableEvent += (_, e) => Note(e.Document);
        RhinoDoc.MaterialTableEvent += (_, e) => Note(e.Document);
        RhinoDoc.LinetypeTableEvent += (_, e) => Note(e.Document);
        RhinoDoc.GroupTableEvent += (_, e) => Note(e.Document);
        RhinoDoc.HatchPatternTableEvent += (_, e) => Note(e.Document);
        RhinoDoc.DimensionStyleTableEvent += (_, e) => Note(e.Document);
        RhinoDoc.InstanceDefinitionTableEvent += (_, e) => Note(e.Document);
        RhinoDoc.DocumentPropertiesChanged += (_, e) =>
        {
            // Only the settings a snapshot reads count: other document properties don't change a diff.
            if (e.Document is { } doc && !Equals(Read(doc), settings.GetValueOrDefault(doc.RuntimeSerialNumber))) Note(doc);
        };
    }

    /// <summary>The number of changes counted for <paramref name="doc"/> so far.</summary>
    public static long Count(RhinoDoc doc) => counts.GetValueOrDefault(doc.RuntimeSerialNumber);

    /// <summary>Records the settings a snapshot read, so a later properties change is compared with them.</summary>
    internal static void Seen(RhinoDoc doc) => settings[doc.RuntimeSerialNumber] = Read(doc);

    /// <summary>Counts a change to <paramref name="doc"/>. The events above call it; tests on headless documents call it themselves.</summary>
    public static void Note(RhinoDoc? doc)
    {
        if (doc is null) return;
        uint sn = doc.RuntimeSerialNumber;
        counts[sn] = counts.GetValueOrDefault(sn) + 1;
        settings[sn] = Read(doc);
        Changed?.Invoke(doc);
    }

    private static (string, double, double) Read(RhinoDoc doc) =>
        (doc.ModelUnitSystem.ToString(), doc.ModelAbsoluteTolerance, doc.ModelAngleToleranceRadians);
}
