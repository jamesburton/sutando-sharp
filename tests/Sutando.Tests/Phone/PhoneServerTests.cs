using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Sutando.Phone;
using Sutando.Tests.Api;

namespace Sutando.Tests.Phone;

/// <summary>
/// End-to-end host tests for the Twilio webhook surface. Each test stands the host up in-process
/// via <see cref="PhoneTestHost"/> and asserts on the HTTP surface + the recording REST
/// client / fake transport.
/// </summary>
/// <remarks>
/// Members of <see cref="WorkspaceCollection"/> — these tests mutate process-global env vars
/// (<c>SUTANDO_PHONE_*</c>, <c>TWILIO_*</c>) during DI bring-up. Without the collection
/// attribute, parallel test classes would race on those mutations and the signature-validation
/// tests would intermittently see the wrong <c>AllowUnsignedWebhooks</c> value.
/// </remarks>
[Collection(WorkspaceCollection.Name)]
public sealed class PhoneServerTests
{
    private static TimeSpan ShortDeadline { get; } = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Healthz_returns_status_ok_and_zero_active_calls_when_idle()
    {
        await using var host = new PhoneTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);
        var client = host.CreateClient();

        var response = await client.GetAsync("/healthz", cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(cts.Token);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("active_calls").GetInt32());
    }

    [Fact]
    public async Task Twilio_incoming_returns_twiml_with_media_streams_url()
    {
        await using var host = new PhoneTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);
        var client = host.CreateClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CallSid"] = "CA1234",
            ["From"] = "+14155550101",
            ["To"] = "+14155550999",
            ["StirVerstat"] = "TN-Validation-Passed-A",
        });
        var response = await client.PostAsync("/twilio/incoming", form, cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var xml = await response.Content.ReadAsStringAsync(cts.Token);
        var doc = XDocument.Parse(xml);
        var stream = doc.Descendants("Stream").FirstOrDefault();
        Assert.NotNull(stream);
        var url = stream!.Attribute("url")?.Value ?? string.Empty;
        // The TwiML response must include the Media Streams WSS URL — Twilio relies on it to
        // open the per-call WebSocket back to us.
        Assert.StartsWith("wss://", url, StringComparison.Ordinal);
        Assert.EndsWith("/twilio/media", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Twilio_incoming_embeds_resolved_tier_as_stream_parameter()
    {
        await using var host = new PhoneTestHost((cfg, _) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Phone:OwnerNumbers"] = "+14155550101",
            });
        });
        using var cts = new CancellationTokenSource(ShortDeadline);
        var client = host.CreateClient();

        // Owner number with A-level STIR — should resolve to Owner tier in the TwiML.
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CallSid"] = "CA5678",
            ["From"] = "+14155550101",
            ["To"] = "+14155550999",
            ["StirVerstat"] = "TN-Validation-Passed-A",
        });
        var response = await client.PostAsync("/twilio/incoming", form, cts.Token);
        var xml = await response.Content.ReadAsStringAsync(cts.Token);
        var doc = XDocument.Parse(xml);
        var parameters = doc.Descendants("Parameter")
            .ToDictionary(p => p.Attribute("name")?.Value ?? string.Empty, p => p.Attribute("value")?.Value ?? string.Empty);

        Assert.Equal("Owner", parameters["tier"]);
        Assert.Equal("false", parameters["tierDowngraded"]);
        Assert.Equal("+14155550101", parameters["from"]);
    }

    [Fact]
    public async Task Twilio_incoming_downgrades_owner_when_stir_is_not_passed_a()
    {
        await using var host = new PhoneTestHost((cfg, _) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Phone:OwnerNumbers"] = "+14155550101",
            });
        });
        using var cts = new CancellationTokenSource(ShortDeadline);
        var client = host.CreateClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CallSid"] = "CAdown",
            ["From"] = "+14155550101",
            ["To"] = "+14155550999",
            ["StirVerstat"] = "TN-Validation-Failed",
        });
        var response = await client.PostAsync("/twilio/incoming", form, cts.Token);
        var xml = await response.Content.ReadAsStringAsync(cts.Token);
        var doc = XDocument.Parse(xml);
        var parameters = doc.Descendants("Parameter")
            .ToDictionary(p => p.Attribute("name")?.Value ?? string.Empty, p => p.Attribute("value")?.Value ?? string.Empty);

        Assert.Equal("Verified", parameters["tier"]);
        Assert.Equal("true", parameters["tierDowngraded"]);
        Assert.Equal("TN-Validation-Failed", parameters["stirVerstat"]);
    }

    [Fact]
    public async Task Twilio_incoming_rejects_unsigned_request_when_signature_validation_enabled()
    {
        Environment.SetEnvironmentVariable(PhoneEnv.TwilioAuthToken, "test-auth-token");
        try
        {
            await using var host = new PhoneTestHost(signatureBypass: false);
            using var cts = new CancellationTokenSource(ShortDeadline);
            var client = host.CreateClient();

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["CallSid"] = "CA-no-sig",
                ["From"] = "+14155550101",
                ["To"] = "+14155550999",
            });
            // No X-Twilio-Signature header — must 403.
            var response = await client.PostAsync("/twilio/incoming", form, cts.Token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PhoneEnv.TwilioAuthToken, null);
        }
    }

    [Fact]
    public async Task Twilio_incoming_accepts_correctly_signed_request()
    {
        const string token = "test-auth-token";
        Environment.SetEnvironmentVariable(PhoneEnv.TwilioAuthToken, token);
        try
        {
            await using var host = new PhoneTestHost(signatureBypass: false);
            using var cts = new CancellationTokenSource(ShortDeadline);
            var client = host.CreateClient();

            var fields = new Dictionary<string, string>
            {
                ["CallSid"] = "CA-signed",
                ["From"] = "+14155550101",
                ["To"] = "+14155550999",
            };
            // Compute Twilio's signature: HMAC-SHA1 over (url + concat-sorted(key + value)).
            var url = "http://localhost/twilio/incoming";
            var concat = new StringBuilder(url);
            foreach (var kv in fields.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                concat.Append(kv.Key).Append(kv.Value);
            }
            using var hmac = new HMACSHA1(Encoding.UTF8.GetBytes(token));
            var sig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(concat.ToString())));

            var form = new FormUrlEncodedContent(fields);
            using var req = new HttpRequestMessage(HttpMethod.Post, "/twilio/incoming") { Content = form };
            req.Headers.TryAddWithoutValidation("X-Twilio-Signature", sig);
            // The in-memory TestServer uses "localhost" as the host header by default — this
            // matches the URL we hashed above so the signature is consistent end-to-end.
            req.Headers.Host = "localhost";
            var response = await client.SendAsync(req, cts.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PhoneEnv.TwilioAuthToken, null);
        }
    }

    [Fact]
    public async Task Twilio_outbound_returns_503_when_bearer_token_is_unset()
    {
        await using var host = new PhoneTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);
        var client = host.CreateClient();

        var body = JsonContent.Create(new { to = "+14155551111" });
        var response = await client.PostAsync("/twilio/outbound", body, cts.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Twilio_outbound_returns_401_when_bearer_token_is_missing_from_request()
    {
        Environment.SetEnvironmentVariable(PhoneEnv.OutboundBearer, "test-bearer");
        try
        {
            await using var host = new PhoneTestHost();
            using var cts = new CancellationTokenSource(ShortDeadline);
            var client = host.CreateClient();

            var body = JsonContent.Create(new { to = "+14155551111" });
            var response = await client.PostAsync("/twilio/outbound", body, cts.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PhoneEnv.OutboundBearer, null);
        }
    }

    [Fact]
    public async Task Twilio_outbound_places_call_via_rest_client_when_authorised()
    {
        Environment.SetEnvironmentVariable(PhoneEnv.OutboundBearer, "test-bearer");
        Environment.SetEnvironmentVariable(PhoneEnv.TwilioPhoneNumber, "+14155550999");
        try
        {
            await using var host = new PhoneTestHost();
            using var cts = new CancellationTokenSource(ShortDeadline);
            var client = host.CreateClient();

            var body = JsonContent.Create(new { to = "+14155551111", message = "hello world" });
            using var req = new HttpRequestMessage(HttpMethod.Post, "/twilio/outbound") { Content = body };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "test-bearer");
            var response = await client.SendAsync(req, cts.Token);

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cts.Token);
            Assert.StartsWith("CA", payload.GetProperty("sid").GetString());
            var record = Assert.Single(host.RestClient.Calls);
            Assert.Equal("+14155551111", record.To);
            Assert.Equal("+14155550999", record.From);
            Assert.NotNull(record.Twiml);
            // The outbound TwiML must still ship a Media Streams Stream — the body is sent to
            // the model through it.
            Assert.Contains("<Stream", record.Twiml, StringComparison.Ordinal);
            Assert.Contains("hello world", record.Twiml, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PhoneEnv.OutboundBearer, null);
            Environment.SetEnvironmentVariable(PhoneEnv.TwilioPhoneNumber, null);
        }
    }

    [Fact]
    public async Task Twilio_status_callback_returns_200()
    {
        await using var host = new PhoneTestHost();
        using var cts = new CancellationTokenSource(ShortDeadline);
        var client = host.CreateClient();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["CallSid"] = "CA-status",
            ["CallStatus"] = "completed",
        });
        var response = await client.PostAsync("/twilio/status", form, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
