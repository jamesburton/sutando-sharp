using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// One recorded request observed by <see cref="FakeHttpMessageHandler"/>. The values are
/// snapshotted at <see cref="HttpMessageHandler.SendAsync"/> time so callers that wrap their
/// <see cref="HttpRequestMessage"/> in <c>using</c> can still assert on body / headers from
/// the test without hitting <see cref="ObjectDisposedException"/>.
/// </summary>
internal sealed record RecordedRequest(
    HttpMethod Method,
    Uri? RequestUri,
    AuthenticationHeaderValue? Authorization,
    IReadOnlyDictionary<string, string[]> RequestHeaders,
    IReadOnlyDictionary<string, string[]> ContentHeaders,
    byte[] Body)
{
    /// <summary>Body decoded as UTF-8 text — convenient for JSON-body assertions.</summary>
    public string BodyAsString() => Encoding.UTF8.GetString(Body);
}

/// <summary>
/// Test-only <see cref="HttpMessageHandler"/> that records every request and replies with a
/// caller-supplied <see cref="HttpResponseMessage"/> (or throws if the queue is exhausted).
/// Each cloud skill's fake-HTTP test stands one of these up, hands the resulting
/// <see cref="HttpClient"/> to the skill via <see cref="Sutando.Skills.SkillContext"/>, and
/// inspects <see cref="Requests"/> afterwards.
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();

    /// <summary>Snapshots of every request that was sent through this handler, in order.</summary>
    public List<RecordedRequest> Requests { get; } = [];

    /// <summary>Enqueue a literal response for the next request.</summary>
    public FakeHttpMessageHandler EnqueueResponse(HttpResponseMessage response)
    {
        _responders.Enqueue(_ => response);
        return this;
    }

    /// <summary>Enqueue a response that's a function of the inbound request.</summary>
    public FakeHttpMessageHandler EnqueueResponse(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Snapshot body + headers before the responder runs, and before the caller's `using`
        // block disposes the request on return.
        var body = request.Content is null
            ? []
            : await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        var requestHeaders = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.Ordinal);
        var contentHeaders = request.Content?.Headers.ToDictionary(h => h.Key, h => h.Value.ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, string[]>(StringComparer.Ordinal);

        Requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            request.Headers.Authorization,
            requestHeaders,
            contentHeaders,
            body));

        if (_responders.Count == 0)
        {
            throw new InvalidOperationException(
                $"FakeHttpMessageHandler: no enqueued response for {request.Method} {request.RequestUri}");
        }
        var responder = _responders.Dequeue();
        return responder(request);
    }
}
