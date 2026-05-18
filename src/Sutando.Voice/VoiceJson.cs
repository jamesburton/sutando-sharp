using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sutando.Voice;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for the voice WS wire format. snake_case property
/// names, lower-snake_case for enum string values, and tolerant case-insensitive reads so a
/// browser client that ships <c>{"Type":"audio"}</c> still parses.
/// </summary>
internal static class VoiceJson
{
    /// <summary>The shared options instance. Cached because <see cref="JsonSerializerOptions"/> compiles metadata on first use.</summary>
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
        };
        // The string-enum converter has its own naming policy independent of property names; wire it
        // up here so wire enum values like ServerMessageType.SetupComplete serialise as
        // "setup_complete" rather than the default "SetupComplete".
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
