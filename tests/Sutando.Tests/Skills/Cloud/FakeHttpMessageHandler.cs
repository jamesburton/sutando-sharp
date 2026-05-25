using System.Net;
using System.Net.Http;

namespace Sutando.Tests.Skills.Cloud;

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

    /// <summary>Every request that was sent through this handler, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

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
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        if (_responders.Count == 0)
        {
            throw new InvalidOperationException(
                $"FakeHttpMessageHandler: no enqueued response for {request.Method} {request.RequestUri}");
        }
        var responder = _responders.Dequeue();
        return Task.FromResult(responder(request));
    }
}
