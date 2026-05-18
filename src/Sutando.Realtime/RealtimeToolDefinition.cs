using System.Text.Json;

namespace Sutando.Realtime;

/// <summary>
/// Execution kind for a registered tool — surfaced to consumers so they can decide whether
/// to await results inline or hand off to a background runner.
/// </summary>
/// <remarks>
/// In this transport-only slice, both kinds dispatch identically: the consumer registers
/// a handler, the handler runs to completion, the result is sent back to the model. The
/// distinction is wire-shape-preserving so the follow-up phase that adds a subagent runtime
/// can switch on this enum without an API break for existing tools.
/// </remarks>
public enum RealtimeToolExecutionKind
{
    /// <summary>The tool runs inline on the dispatch path. The session waits for the result before continuing.</summary>
    Inline = 0,

    /// <summary>The tool is dispatched to a background runner. The session continues; the result arrives asynchronously.</summary>
    Background = 1,
}

/// <summary>
/// Declarative tool surface registered with the realtime session.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors bodhi's <c>ToolDefinition</c> contract but takes the parameter schema as a raw
/// JSON-Schema <see cref="JsonElement"/> rather than running a Zod-to-JSON-Schema conversion.
/// </para>
/// <para>
/// Authors typically build the schema with <c>JsonDocument.Parse(@"{ ""type"": ""object"", ""properties"": { ... } }").RootElement</c>.
/// </para>
/// </remarks>
/// <param name="Name">Function name. Must match <c>[a-zA-Z0-9_-]</c>, max 63 chars (Gemini constraint).</param>
/// <param name="Description">Short, model-facing description used during function-selection.</param>
/// <param name="ParameterSchema">JSON Schema (object form) describing the parameters the model should pass.</param>
/// <param name="Execution">Whether the handler runs inline or is dispatched to a background runner.</param>
public sealed record RealtimeToolDefinition(
    string Name,
    string Description,
    JsonElement ParameterSchema,
    RealtimeToolExecutionKind Execution = RealtimeToolExecutionKind.Inline);

/// <summary>
/// Realtime tool handler — the user-supplied delegate that produces a result for a tool call.
/// </summary>
/// <param name="arguments">The model-supplied arguments as a JSON object.</param>
/// <param name="ct">Cancellation token, signalled when the tool call is cancelled or the session is torn down.</param>
/// <returns>The function result as a JSON object that will be sent back to the model.</returns>
public delegate Task<JsonElement> RealtimeToolHandler(JsonElement arguments, CancellationToken ct);
