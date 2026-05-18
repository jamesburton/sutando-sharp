using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sutando.Phone;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for the phone bridge. Used for both the
/// Twilio Media Streams wire envelope (camelCase from the carrier) and the call-metadata
/// JSON we write to <c>data/phone/&lt;call-sid&gt;.json</c> (snake_case for parity with the
/// rest of the bridge contract).
/// </summary>
/// <remarks>
/// We pick camelCase as the default because Twilio's wire format is camelCase. Properties on
/// the metadata records are explicitly attributed with snake_case names — so the same options
/// instance serialises both shapes correctly. The <c>PropertyNameCaseInsensitive</c> flag
/// keeps tolerance high on inbound parses (Twilio occasionally mixes case across SDK versions).
/// </remarks>
internal static class PhoneJson
{
    /// <summary>The shared options instance. Cached because <see cref="JsonSerializerOptions"/> compiles metadata on first use.</summary>
    public static JsonSerializerOptions Options { get; } = Build();

    private static JsonSerializerOptions Build()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };
        // Enum strings — useful for tier in the metadata file. Lower-case-only ASCII names
        // align with the snake_case enum policy used by Sutando.Voice / Sutando.Api.
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        return options;
    }
}
