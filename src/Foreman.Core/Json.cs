using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Foreman.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        // Job files and prompts keep non-ASCII text (a Russian brief, a client's answer) readable. They are never
        // embedded in HTML, which is what the default escaping guards against.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>One line, for JSONL logs.</summary>
    public static readonly JsonSerializerOptions Line = new(Options) { WriteIndented = false };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Read<T>(string text) => JsonSerializer.Deserialize<T>(text, Options);
}
