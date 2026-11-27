using Rhino.FileIO;

namespace DittoHopper.Rhino.Tests;

/// <summary>
/// A generated A/B pair of .3dm files in a per-run temp folder. No .3dm file is ever committed.
/// Call only from inside <see cref="RhinoHost.Run"/>.
/// </summary>
public sealed record FixturePair(string A, string B)
{
    private static readonly Lazy<string> Run = new(() => Directory.CreateTempSubdirectory("DittoHopper.Rhino.Tests-").FullName);

    /// <summary>The per-run folder that holds every generated pair.</summary>
    public static string RunDirectory => Run.Value;

    /// <summary>
    /// Builds A, writes it, then reads A back and applies <paramref name="editB"/> to make B.
    /// GUIDs survive, as they do when someone edits a model in place.
    /// </summary>
    public static FixturePair Create(string name, Action<File3dm> build, Action<File3dm> editB) =>
        Write(name, build, a =>
        {
            var b = File3dm.Read(a) ?? throw new InvalidOperationException($"Could not read back '{a}'.");
            editB(b);
            return b;
        });

    /// <summary>
    /// Builds A and B independently, so every GUID differs, as after a re-bake or a rebuild.
    /// </summary>
    public static FixturePair CreateFromScratch(string name, Action<File3dm> build, Action<File3dm> buildB) =>
        Write(name, build, _ =>
        {
            var b = new File3dm();
            buildB(b);
            return b;
        });

    internal static void DeleteRunDirectory()
    {
        if (Run.IsValueCreated && Directory.Exists(Run.Value)) Directory.Delete(Run.Value, recursive: true);
    }

    private static FixturePair Write(string name, Action<File3dm> build, Func<string, File3dm> makeB)
    {
        var folder = Directory.CreateDirectory(Path.Combine(RunDirectory, name)).FullName;
        var pair = new FixturePair(Path.Combine(folder, "A.3dm"), Path.Combine(folder, "B.3dm"));
        using (var a = new File3dm())
        {
            build(a);
            Save(a, pair.A);
        }
        using (var b = makeB(pair.A)) Save(b, pair.B);
        return pair;
    }

    private static void Save(File3dm file, string path)
    {
        if (!file.WriteWithLog(path, new File3dmWriteOptions(), out var log))
            throw new InvalidOperationException($"Could not write '{path}': {log}");
    }
}
