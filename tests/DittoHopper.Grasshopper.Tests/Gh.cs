using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Xunit;

namespace DittoHopper.Grasshopper.Tests;

/// <summary>
/// The .gha under test sits beside the tests but is not in deps.json (see the csproj), so resolve it here. Kept
/// free of Grasshopper types, so plain tests run without Rhino.
/// </summary>
internal static class GhaResolver
{
    [ModuleInitializer]
    internal static void Register() => AssemblyLoadContext.Default.Resolving += (context, name) =>
    {
        var gha = Path.Combine(AppContext.BaseDirectory, name.Name + ".gha");
        return File.Exists(gha) ? context.LoadFromAssemblyPath(gha) : null;
    };
}

/// <summary>Builds and solves Grasshopper definitions in the headless Rhino. Call only inside RhinoHost.Run.</summary>
internal static class Gh
{
    public static readonly Guid PlugInId = new("b45a29b1-4343-4035-989e-044e8580d9cf");

    public static void Load()
    {
        Assert.True(global::Rhino.PlugIns.PlugIn.LoadPlugIn(PlugInId), "Grasshopper did not load.");
        GH_Document.EnableSolutions = true;
    }

    public static T Add<T>(GH_Document doc) where T : GH_Component, new()
    {
        var c = new T();
        c.CreateAttributes();
        doc.AddObject(c, false);
        return c;
    }

    public static void Set<T>(IGH_Param param, params T[] values) where T : class, IGH_Goo
    {
        var p = (GH_PersistentParam<T>)param;
        p.PersistentData.Clear();
        foreach (var v in values) p.PersistentData.Append(v);
        p.ExpireSolution(false);
    }

    public static void Wire(IGH_Param from, IGH_Param to) => to.AddSource(from);

    public static void Solve(GH_Document doc) => doc.NewSolution(true, GH_SolutionMode.Silent);

    public static List<IGH_Goo> Out(GH_Component c, int index) => [.. c.Params.Output[index].VolatileData.AllData(true)];

    public static List<string> Texts(GH_Component c, int index) => [.. Out(c, index).Select(g => ((GH_String)g).Value)];

    public static string Errors(GH_Component c) =>
        string.Join("; ", c.RuntimeMessages(GH_RuntimeMessageLevel.Error).Concat(c.RuntimeMessages(GH_RuntimeMessageLevel.Warning)));
}
