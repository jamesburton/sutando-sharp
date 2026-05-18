using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using OpenAI;

namespace Sutando.LocalInference.OpenAI;

/// <summary>
/// Internal helper — builds an <see cref="OpenAIClient"/> pointed at an
/// <see cref="OpenAiEndpointOptions.Endpoint"/>. Centralised so every stage adapter uses
/// the same construction conventions (sentinel API key, custom base URL).
/// </summary>
[Experimental("SUTANDO001")]
internal static class OpenAiClientFactory
{
    /// <summary>Build an <see cref="OpenAIClient"/> for the given local-or-remote endpoint.</summary>
    /// <param name="options">Endpoint + key + model.</param>
    /// <returns>A configured client; consumers obtain stage-specific subclients via the MEAI extensions.</returns>
    public static OpenAIClient Build(OpenAiEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Endpoint);

        var credential = new ApiKeyCredential(options.ResolvedApiKey);
        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = options.Endpoint,
        };
        return new OpenAIClient(credential, clientOptions);
    }
}
