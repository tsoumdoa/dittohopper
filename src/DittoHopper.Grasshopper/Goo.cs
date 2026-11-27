using System.Drawing;
using DittoHopper.Core;
using Core = DittoHopper.Core;
using DittoHopper.Rhino;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace DittoHopper.Grasshopper;

/// <summary>Base for the DittoHopper components: one tab, an icon drawn in code.</summary>
public abstract class DittoComponent(string name, string nickname, string description, string subcategory)
    : GH_Component(name, nickname, description, Tab, subcategory)
{
    public const string Tab = "DittoHopper";
    public const string DiffPanel = "Diff", ReviewPanel = "Review", WritePanel = "Write";

    private Bitmap? icon;

    protected abstract ComponentIcon IconKind { get; }

    protected override Bitmap Icon => icon ??= Icons.Create(IconKind);

    /// <summary>The value inside a goo, a wrapper or a script variable.</summary>
    protected static T? Unwrap<T>(IGH_Goo? goo) where T : class => goo switch
    {
        null => null,
        GH_Goo<T> typed => typed.Value,
        GH_ObjectWrapper wrapper => wrapper.Value as T,
        _ => goo.ScriptVariable() as T,
    };

    protected T? Input<T>(IGH_DataAccess da, int index, string what) where T : class
    {
        IGH_Goo? goo = null;
        if (!da.GetData(index, ref goo) || goo is null) return null;
        var value = Unwrap<T>(goo);
        if (value is null)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"{Params.Input[index].NickName} needs {what}, not {goo.TypeName}.");
        return value;
    }

    protected List<string> Texts(IGH_DataAccess da, int index)
    {
        var list = new List<string>();
        da.GetDataList(index, list);
        return list;
    }
}

public sealed class SnapshotGoo : GH_Goo<RhinoSnapshot>
{
    public SnapshotGoo() { }
    public SnapshotGoo(RhinoSnapshot value) : base(value) { }

    public override bool IsValid => Value is not null;
    public override string TypeName => "Model Snapshot";
    public override string TypeDescription => "One Rhino model, captured for diffing";
    public override IGH_Goo Duplicate() => new SnapshotGoo(Value); // immutable

    public override string ToString()
    {
        if (Value is null) return "No snapshot";
        var m = Value.Model;
        return $"{m.Info.Source} · {Describe.Plural(m.Objects.Count, "object")} · {Describe.Plural(m.Layers.Count, "layer")} · {m.Info.Units}";
    }
}

public sealed class SettingsGoo : GH_Goo<DiffSettings>
{
    public SettingsGoo() { }
    public SettingsGoo(DiffSettings value) : base(value) { }

    public override bool IsValid => Value is not null;
    public override string TypeName => "Diff Settings";
    public override string TypeDescription => "What a diff compares and how";
    public override IGH_Goo Duplicate() => new SettingsGoo(Value);
    public override string ToString() => Value is null ? "No settings" : Vocabulary.Describe(Value);
}

public sealed class ResultGoo : GH_Goo<DiffRun>
{
    public ResultGoo() { }
    public ResultGoo(DiffRun value) : base(value) { }

    public override bool IsValid => Value is not null;
    public override string TypeName => "Diff Result";
    public override string TypeDescription => "What changed between two models, and what did not";
    public override IGH_Goo Duplicate() => new ResultGoo(Value);
    public override string ToString() => Value?.ToString() ?? "No result";
}

/// <summary>Turns snapshot geometry into Grasshopper geometry, and converts between units.</summary>
internal static class Geometry
{
    /// <summary>Multiplies lengths in <paramref name="from"/> into <paramref name="to"/>; 1 when either can't be converted.</summary>
    public static double Scale(Core.UnitSystem from, Core.UnitSystem to) =>
        Units.MetersPer(from) is double f && Units.MetersPer(to) is double t ? f / t : 1;

    /// <summary>Multiplies lengths in <paramref name="from"/> into the units of a Rhino document.</summary>
    public static double Scale(Core.UnitSystem from, global::Rhino.RhinoDoc? doc) =>
        doc is not null && Units.MetersPer(from) is double f && f > 0
            ? f / global::Rhino.RhinoMath.UnitScale(doc.ModelUnitSystem, global::Rhino.UnitSystem.Meters)
            : 1;

    /// <summary>
    /// The one unit rule for every geometry output and the preview: the Rhino document's units, or A's when there is
    /// no document. Returns the factors for A's and B's lengths, and a remark naming the units when A, B and the
    /// document don't all agree.
    /// </summary>
    public static (double A, double B, string? Remark) OutputUnits(DiffRun run, global::Rhino.RhinoDoc? doc)
    {
        var (ua, ub) = (run.A.Model.Info.Units, run.B.Model.Info.Units);
        if (doc is null)
            return (1, run.BToA, ua == ub ? null : $"Geometry is in A's units ({ua}): B ({ub}) is converted.");
        string units = doc.ModelUnitSystem.ToString();
        bool same = ua == ub && ua.ToString() == units;
        return (Scale(ua, doc), Scale(ub, doc),
            same ? null : $"Geometry is in the Rhino document's units ({units}): A is in {ua}, B in {ub}.");
    }

    /// <summary>
    /// Plain GH geometry, a copy the caller owns, scaled by <paramref name="scale"/> about the origin (a unit
    /// conversion). A block instance becomes its insertion point. Null for types Grasshopper has no geometry for.
    /// </summary>
    public static IGH_GeometricGoo? ToGoo(GeometryBase? g, double scale = 1)
    {
        GeometryBase? copy = g switch
        {
            null => null,
            InstanceReferenceGeometry i => new global::Rhino.Geometry.Point(InsertionPoint(i)),
            Extrusion e => e.ToBrep(true),
            _ => g.Duplicate(),
        };
        if (copy is null) return null;
        if (scale != 1) copy.Transform(Transform.Scale(Point3d.Origin, scale));
        return GH_Convert.ToGeometricGoo(copy);
    }

    public static Point3d InsertionPoint(InstanceReferenceGeometry i)
    {
        var p = Point3d.Origin;
        p.Transform(i.Xform);
        return p;
    }
}
