using DittoHopper.Core;
using RhinoDoc = Rhino.RhinoDoc;

namespace DittoHopper.Rhino;

/// <summary>
/// One diff with the two Rhino snapshots it came from: <see cref="DiffIndex"/>'s lookups, plus the geometry the
/// Grasshopper outputs, Diff Preview and the review panel's viewport draw. Immutable.
/// </summary>
public sealed class DiffRun(DiffResult result, RhinoSnapshot a, RhinoSnapshot b) : DiffIndex(result, a.Model, b.Model)
{
    public RhinoSnapshot A { get; } = a;
    public RhinoSnapshot B { get; } = b;

    /// <summary>The open document B is a live snapshot of, or null when B is a file (or that document is closed).</summary>
    public RhinoDoc? BDocument => B.DocumentSerialNumber is uint sn ? RhinoDoc.FromRuntimeSerialNumber(sn) : null;

    /// <summary>Multiplies lengths in <paramref name="from"/> into the units of a Rhino document; 1 without one.</summary>
    public static double Scale(Core.UnitSystem from, RhinoDoc? doc) =>
        doc is not null && Units.MetersPer(from) is double f && f > 0
            ? f / global::Rhino.RhinoMath.UnitScale(doc.ModelUnitSystem, global::Rhino.UnitSystem.Meters)
            : 1;

    /// <summary>
    /// The one unit rule for every geometry output and drawing: the Rhino document's units, or A's when there is no
    /// document. Returns the factors for A's and B's lengths, and a remark naming the units when A, B and the document
    /// don't all agree.
    /// </summary>
    public (double A, double B, string? Remark) OutputUnits(RhinoDoc? doc)
    {
        var (ua, ub) = (ModelA.Info.Units, ModelB.Info.Units);
        if (doc is null)
            return (1, BToA, ua == ub ? null : $"Geometry is in A's units ({ua}): B ({ub}) is converted.");
        string units = doc.ModelUnitSystem.ToString();
        bool same = ua == ub && ua.ToString() == units;
        return (Scale(ua, doc), Scale(ub, doc),
            same ? null : $"Geometry is in the Rhino document's units ({units}): A is in {ua}, B in {ub}.");
    }
}
