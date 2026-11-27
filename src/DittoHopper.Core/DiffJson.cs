using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DittoHopper.Core;

/// <summary>
/// Reads and writes a <see cref="DiffResult"/> as JSON. Output is byte-stable: camelCase names, enums as
/// strings, LF line endings, and numbers in their shortest round-trip form whatever the machine's culture.
/// </summary>
public static class DiffJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    // Raw newlines only appear as indentation: newlines inside strings are always escaped.
    public static string Serialize(DiffResult result) => JsonSerializer.Serialize(result, Options).Replace("\r\n", "\n");

    public static DiffResult Deserialize(string json) =>
        JsonSerializer.Deserialize<DiffResult>(json, Options) ?? throw new JsonException("The JSON holds no diff result.");
}
