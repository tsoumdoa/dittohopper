using Xunit;

namespace DittoHopper.Core.Tests;

/// <summary>
/// Compares a result's JSON with tests/DittoHopper.Core.Tests/golden/&lt;name&gt;.json.
/// Set DITTOHOPPER_UPDATE_GOLDEN=1 to (re)write the files instead, then review the diff before committing.
/// </summary>
internal static class Golden
{
    public static void Verify(string name, DiffResult result)
    {
        // Snapshot capture times are not part of the diff; keep them out of the files.
        result = result with { A = result.A with { CapturedAt = null }, B = result.B with { CapturedAt = null } };
        string json = DiffJson.Serialize(result) + "\n";
        string path = Path.Combine(Directory(), name + ".json");
        if (Environment.GetEnvironmentVariable("DITTOHOPPER_UPDATE_GOLDEN") == "1")
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return;
        }
        Assert.True(File.Exists(path), $"Missing golden/{name}.json. Run the tests with DITTOHOPPER_UPDATE_GOLDEN=1 to create it.");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), json);
    }

    private static string Directory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DittoHopper.sln")))
                return Path.Combine(dir.FullName, "tests", "DittoHopper.Core.Tests", "golden");
        throw new InvalidOperationException("Can't find the repository root (DittoHopper.sln) above the test binaries.");
    }
}
