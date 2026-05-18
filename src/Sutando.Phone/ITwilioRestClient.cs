using Twilio.Clients;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Sutando.Phone;

/// <summary>
/// Narrow DI seam over the bits of the Twilio REST SDK that the phone bridge actually uses.
/// Tests substitute an in-process fake; production registrations wrap the real
/// <see cref="TwilioRestClient"/>.
/// </summary>
/// <remarks>
/// Twilio's SDK uses a static <c>TwilioClient.Init(...)</c> + static
/// <c>CallResource.CreateAsync(...)</c> shape that's fundamentally untestable without
/// monkey-patching. By taking <see cref="ITwilioRestClient"/> in our handler and forwarding
/// to <see cref="TwilioRestClient"/> in the production adapter, the outbound-call code path
/// can be exercised in unit tests against a recording fake.
/// </remarks>
public interface ITwilioRestClient
{
    /// <summary>
    /// Place an outbound call.
    /// </summary>
    /// <param name="to">E.164 destination number.</param>
    /// <param name="from">E.164 caller-id (must be a Twilio-owned number).</param>
    /// <param name="twimlUrl">URL Twilio fetches for the call's instructions. Either this OR <paramref name="twiml"/>.</param>
    /// <param name="twiml">Inline TwiML for the call. Either this OR <paramref name="twimlUrl"/>.</param>
    /// <param name="statusCallback">Optional status callback URL.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created call's SID.</returns>
    Task<string> CreateCallAsync(
        string to,
        string from,
        Uri? twimlUrl,
        string? twiml,
        Uri? statusCallback,
        CancellationToken ct);
}

/// <summary>
/// Production adapter that funnels into <c>Twilio.Rest.Api.V2010.Account.CallResource</c>.
/// </summary>
/// <remarks>
/// One <see cref="TwilioRestClient"/> instance is kept alive for the host's lifetime — the
/// SDK class is thread-safe and pools its HttpClient internally, so this is the recommended
/// pattern from the Twilio docs.
/// </remarks>
public sealed class TwilioRestClientAdapter : ITwilioRestClient
{
    private readonly TwilioRestClient _inner;

    /// <summary>Creates a new adapter authenticating with <paramref name="accountSid"/> / <paramref name="authToken"/>.</summary>
    /// <param name="accountSid">The Twilio account SID. Must start with <c>AC</c>.</param>
    /// <param name="authToken">The Twilio auth token.</param>
    public TwilioRestClientAdapter(string accountSid, string authToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(authToken);
        _inner = new TwilioRestClient(accountSid, authToken);
    }

    /// <inheritdoc />
    public async Task<string> CreateCallAsync(
        string to,
        string from,
        Uri? twimlUrl,
        string? twiml,
        Uri? statusCallback,
        CancellationToken ct)
    {
        // Twilio's CreateOptions take either url or twiml — never both. The caller is
        // responsible for picking one; we throw early so the SDK's 400 doesn't surface as a
        // mysterious HTTP failure.
        if ((twimlUrl is null) == string.IsNullOrEmpty(twiml))
        {
            throw new ArgumentException("Provide exactly one of twimlUrl or inline twiml.", nameof(twimlUrl));
        }

        var options = new CreateCallOptions(new PhoneNumber(to), new PhoneNumber(from))
        {
            Url = twimlUrl,
            Twiml = twiml,
            StatusCallback = statusCallback,
        };
        var call = await CallResource.CreateAsync(options, _inner).ConfigureAwait(false);
        // The cancellation token is honoured by the underlying HttpClient when the SDK
        // surfaces it. Today the SDK accepts no CancellationToken on CreateAsync; we observe
        // the token for parity with future SDK versions.
        ct.ThrowIfCancellationRequested();
        return call.Sid;
    }
}
