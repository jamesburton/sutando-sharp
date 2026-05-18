using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sutando.Bridge;

namespace Sutando.Core.Executors;

/// <summary>
/// Direct Anthropic Messages-API executor. Requires an API key; uses no third-party SDK
/// so the dependency footprint stays minimal.
/// </summary>
/// <remarks>
/// <para>
/// This is the "pure-.NET" executor for users who provide an API key rather than relying
/// on the Claude Code subscription. Single-turn completion only — full tool-use orchestration
/// lives in a follow-up phase. Use <see cref="ClaudeCliAgentExecutor"/> when you need the
/// tool-using agent surface today.
/// </para>
/// <para>
/// Non-owner tiers receive an explicit short-circuit error: the upstream sandboxing policy
/// is "run team/other via codex --sandbox read-only," which is the CLI executor's job, not
/// this one.
/// </para>
/// </remarks>
public sealed class AnthropicHttpAgentExecutor : IAgentExecutor
{
    private const string DefaultEndpoint = "https://api.anthropic.com/v1/messages";
    private const string DefaultModel = "claude-opus-4-7";
    private const int DefaultMaxTokens = 4096;
    private const string ApiVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly AnthropicHttpAgentExecutorOptions _options;
    private readonly ILogger<AnthropicHttpAgentExecutor> _logger;

    /// <inheritdoc/>
    public string Id => "anthropic-http";

    /// <param name="http">An <see cref="HttpClient"/> — typically injected via <see cref="IHttpClientFactory"/>.</param>
    /// <param name="options">Endpoint, model, API key.</param>
    /// <param name="logger">Optional logger.</param>
    public AnthropicHttpAgentExecutor(
        HttpClient http,
        AnthropicHttpAgentExecutorOptions options,
        ILogger<AnthropicHttpAgentExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new ArgumentException("ApiKey is required.", nameof(options));
        }
        _http = http;
        _options = options;
        _logger = logger ?? NullLogger<AnthropicHttpAgentExecutor>.Instance;
    }

    /// <inheritdoc/>
    public async Task<AgentResult> ExecuteAsync(TaskEnvelope task, CancellationToken ct)
    {
        if (task.IsCancelInstruction)
        {
            return AgentResult.Ok($"cancelled task {task.CancelTargetId}", TimeSpan.Zero);
        }
        if (task.AccessTier != AccessTier.Owner)
        {
            // Sandboxing is the CLI executor's domain. The HTTP path is owner-only.
            return AgentResult.Error(
                $"anthropic-http: non-owner tier '{task.AccessTier}' must route through claude-cli for sandboxing.",
                TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();
        var budget = task.Timeout ?? TimeSpan.FromMinutes(2);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(budget);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint ?? DefaultEndpoint)
        {
            Content = JsonContent.Create(BuildRequestBody(task), options: JsonOptions),
        };
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", ApiVersion);

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var raw = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                _logger.LogWarning("Anthropic API {Status} for task {Id}: {Body}", (int)response.StatusCode, task.Id, Truncate(raw, 400));
                stopwatch.Stop();
                return AgentResult.Error($"anthropic api {(int)response.StatusCode}: {Truncate(raw, 400)}", stopwatch.Elapsed);
            }

            var payload = await response.Content.ReadFromJsonAsync<MessagesResponse>(JsonOptions, timeoutCts.Token).ConfigureAwait(false);
            stopwatch.Stop();

            var text = ExtractText(payload);
            return AgentResult.Ok(text, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return AgentResult.Timeout(budget);
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Anthropic HTTP request failed for task {Id}", task.Id);
            return AgentResult.Error($"anthropic http: {ex.Message}", stopwatch.Elapsed);
        }
    }

    private MessagesRequest BuildRequestBody(TaskEnvelope task) => new()
    {
        Model = _options.Model ?? DefaultModel,
        MaxTokens = _options.MaxTokens ?? DefaultMaxTokens,
        System = _options.SystemPrompt,
        Messages =
        [
            new MessagesRequest.Message
            {
                Role = "user",
                Content = task.Body,
            },
        ],
    };

    private static string ExtractText(MessagesResponse? payload)
    {
        if (payload?.Content is null)
        {
            return string.Empty;
        }
        return string.Concat(payload.Content
            .Where(c => string.Equals(c.Type, "text", StringComparison.Ordinal) && c.Text is not null)
            .Select(c => c.Text));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private sealed record MessagesRequest
    {
        [JsonPropertyName("model")] public required string Model { get; init; }
        [JsonPropertyName("max_tokens")] public required int MaxTokens { get; init; }
        [JsonPropertyName("system")] public string? System { get; init; }
        [JsonPropertyName("messages")] public required IReadOnlyList<Message> Messages { get; init; }

        public sealed record Message
        {
            [JsonPropertyName("role")] public required string Role { get; init; }
            [JsonPropertyName("content")] public required string Content { get; init; }
        }
    }

    private sealed record MessagesResponse
    {
        [JsonPropertyName("content")] public IReadOnlyList<ContentBlock>? Content { get; init; }

        public sealed record ContentBlock
        {
            [JsonPropertyName("type")] public string? Type { get; init; }
            [JsonPropertyName("text")] public string? Text { get; init; }
        }
    }
}

/// <summary>Configuration for <see cref="AnthropicHttpAgentExecutor"/>.</summary>
public sealed record AnthropicHttpAgentExecutorOptions
{
    /// <summary>API key (required). Source from <c>ANTHROPIC_API_KEY</c> at composition time.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Override the Messages endpoint. Defaults to the public Anthropic API.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Model id (e.g. <c>claude-opus-4-7</c>).</summary>
    public string? Model { get; init; }

    /// <summary>Max response tokens. Defaults to 4096.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Optional system prompt prepended to every conversation.</summary>
    public string? SystemPrompt { get; init; }
}
