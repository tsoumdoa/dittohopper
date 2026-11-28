using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true, MaxParallelThreads = 1)]

namespace DittoHopper.Rhino.Tests;

/// <summary>Skipped at discovery unless DITTOHOPPER_RHINO_TESTS=1, so plain dotnet test never loads Rhino.</summary>
public sealed class RhinoFactAttribute : FactAttribute
{
    public RhinoFactAttribute()
    {
        if (!RhinoHost.Enabled) Skip = "Headless Rhino integration test: set DITTOHOPPER_RHINO_TESTS=1 (see README.md).";
    }
}

[CollectionDefinition("Rhino")]
public sealed class RhinoCollection : ICollectionFixture<RhinoHost>;

/// <summary>
/// One headless RhinoCore on one STA thread for the whole serial test run. RhinoCommon is resolved
/// from the installed Rhino System folder, so no Rhino binaries are copied or redistributed.
/// </summary>
public sealed class RhinoHost : IDisposable
{
    public static bool Enabled => Environment.GetEnvironmentVariable("DITTOHOPPER_RHINO_TESTS") == "1";

    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread? _thread;
    private Exception? _startupError;

    /// <summary>Time from host construction to a ready RhinoCore.</summary>
    public TimeSpan BootTime { get; }

    public RhinoHost()
    {
        if (!Enabled) return;
        var system = Environment.GetEnvironmentVariable("DITTOHOPPER_RHINO_SYSTEM")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Rhino 8", "System");
        if (!File.Exists(Path.Combine(system, "RhinoCommon.dll")))
            throw new InvalidOperationException($"RhinoCommon.dll not found in '{system}'. Set DITTOHOPPER_RHINO_SYSTEM.");
        SetDllDirectory(system);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var folder in new[] { Path.Combine(system, "netcore"), system, Path.Combine(system, "..", "Plug-ins", "Grasshopper") })
            {
                var path = Path.Combine(folder, name.Name + ".dll");
                if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
            }
            return null;
        };

        var clock = Stopwatch.StartNew();
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            try { HostLoop(ready); }
            catch (Exception ex) { _startupError = ex; ready.Set(); }
        }) { IsBackground = true, Name = "RhinoHost" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        if (_startupError is not null) ExceptionDispatchInfo.Capture(_startupError).Throw();
        BootTime = clock.Elapsed;
    }

#if DITTOHOPPER_GRASSHOPPER_TESTS
    // Grasshopper is a Rhino plug-in, and /safemode skips plug-ins.
    private static readonly string[] Arguments = ["/nosplash", "/notemplate"];
#else
    private static readonly string[] Arguments = ["/nosplash", "/notemplate", "/safemode"];
#endif

    /// <summary>
    /// Set to a folder to write screenshots (the Grasshopper tests' ScreenshotTests). Rhino then boots with a hidden
    /// window instead of none, since view capture needs real views.
    /// </summary>
    public static string? ScreenshotFolder => Environment.GetEnvironmentVariable("DITTOHOPPER_SCREENSHOTS") is { Length: > 0 } s ? s : null;

    private static readonly List<Action> AfterShutdown = [];

    /// <summary>Runs <paramref name="cleanup"/> once RhinoCore has shut down, when Rhino has written its settings.</summary>
    public static void OnShutdown(Action cleanup)
    {
        lock (AfterShutdown) AfterShutdown.Add(cleanup);
    }

    private void HostLoop(ManualResetEventSlim ready)
    {
        var window = ScreenshotFolder is null ? global::Rhino.Runtime.InProcess.WindowStyle.NoWindow : global::Rhino.Runtime.InProcess.WindowStyle.Hidden;
        using var core = new global::Rhino.Runtime.InProcess.RhinoCore(Arguments, window);
        ready.Set();
        foreach (var action in _queue.GetConsumingEnumerable()) action();
    }

    /// <summary>Runs <paramref name="action"/> on the Rhino thread and rethrows its failure.</summary>
    public void Run(Action action)
    {
        Exception? failure = null;
        using var complete = new ManualResetEventSlim();
        _queue.Add(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { complete.Set(); }
        });
        Assert.True(complete.Wait(TimeSpan.FromMinutes(2)), "Rhino test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread?.Join();
        _queue.Dispose();
        lock (AfterShutdown) foreach (var cleanup in AfterShutdown) cleanup();
        FixturePair.DeleteRunDirectory();
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectory(string path);
}
