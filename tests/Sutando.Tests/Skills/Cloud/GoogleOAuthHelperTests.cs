using System.Net;
using System.Net.Http;
using System.Text;
using Sutando.Skills.Cloud.Common;

namespace Sutando.Tests.Skills.Cloud;

/// <summary>
/// Unit tests for <see cref="GoogleOAuthHelper"/>. Uses <see cref="FakeHttpMessageHandler"/> to
/// drive the token endpoint without hitting the network. The clock is injected via constructor to
/// control cache-expiry behaviour deterministically.
/// </summary>
public sealed class GoogleOAuthHelperTests
{
    private const string ClientId = "test-client-id";
    private const string ClientSecret = "test-client-secret";
    private const string RefreshToken = "test-refresh-token";
    private const string AccessToken = "ya29.first-access-token";
    private const string SecondAccessToken = "ya29.second-access-token";

    private static HttpResponseMessage TokenResponse(string token, int expiresIn = 3600) =>
        new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"{{\"access_token\":\"{token}\",\"expires_in\":{expiresIn},\"token_type\":\"Bearer\"}}",
                Encoding.UTF8, "application/json"),
        };

    [Fact]
    public async Task GetAccessTokenAsync_FirstCall_FetchesFromEndpointAndReturnsToken()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(TokenResponse(AccessToken));
        var http = new HttpClient(handler);
        var helper = new GoogleOAuthHelper();

        var token = await helper.GetAccessTokenAsync(http, ClientId, ClientSecret, RefreshToken, CancellationToken.None);

        Assert.Equal(AccessToken, token);
        Assert.Single(handler.Requests);

        // Verify the token request was a POST to the token endpoint with form-urlencoded body.
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri?.AbsoluteUri);

        // Body should contain the OAuth2 form fields.
        var body = request.BodyAsString();
        Assert.Contains("grant_type=refresh_token", body);
        Assert.Contains($"client_id={Uri.EscapeDataString(ClientId)}", body);
        Assert.Contains($"client_secret={Uri.EscapeDataString(ClientSecret)}", body);
        Assert.Contains($"refresh_token={Uri.EscapeDataString(RefreshToken)}", body);

        // No Authorization header — credentials go in the form body only.
        Assert.Null(request.Authorization);
    }

    [Fact]
    public async Task GetAccessTokenAsync_SecondCallWithinExpiry_ReturnsCachedTokenWithoutHttp()
    {
        var handler = new FakeHttpMessageHandler().EnqueueResponse(TokenResponse(AccessToken, expiresIn: 3600));
        var http = new HttpClient(handler);
        var helper = new GoogleOAuthHelper();

        var first = await helper.GetAccessTokenAsync(http, ClientId, ClientSecret, RefreshToken, CancellationToken.None);
        var second = await helper.GetAccessTokenAsync(http, ClientId, ClientSecret, RefreshToken, CancellationToken.None);

        // Both should be the same token, but only one HTTP request should have been made.
        Assert.Equal(AccessToken, first);
        Assert.Equal(AccessToken, second);
        Assert.Single(handler.Requests); // second call was served from cache
    }

    [Fact]
    public async Task GetAccessTokenAsync_CallAfterExpiry_RefetchesToken()
    {
        // Use an injected clock so we can simulate time passing.
        var fakeNow = DateTimeOffset.UtcNow;
        var helper = new GoogleOAuthHelper(() => fakeNow);

        // First fetch: token expires in 120 seconds. With 60s margin, effective expiry = +60s.
        var handler = new FakeHttpMessageHandler()
            .EnqueueResponse(TokenResponse(AccessToken, expiresIn: 120))
            .EnqueueResponse(TokenResponse(SecondAccessToken, expiresIn: 3600));
        var http = new HttpClient(handler);

        var first = await helper.GetAccessTokenAsync(http, ClientId, ClientSecret, RefreshToken, CancellationToken.None);
        Assert.Equal(AccessToken, first);
        Assert.Single(handler.Requests);

        // Advance clock past the effective expiry (60s after original fetch).
        fakeNow = fakeNow.AddSeconds(61);

        var second = await helper.GetAccessTokenAsync(http, ClientId, ClientSecret, RefreshToken, CancellationToken.None);
        Assert.Equal(SecondAccessToken, second);
        Assert.Equal(2, handler.Requests.Count); // re-fetch happened
    }

    [Fact]
    public async Task GetAccessTokenAsync_Non2xxResponse_ThrowsInvalidOperationExceptionWithBody()
    {
        var errorBody = "{\"error\":\"invalid_client\",\"error_description\":\"The OAuth client was not found.\"}";
        var handler = new FakeHttpMessageHandler().EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(errorBody, Encoding.UTF8, "application/json"),
        });
        var http = new HttpClient(handler);
        var helper = new GoogleOAuthHelper();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            helper.GetAccessTokenAsync(http, ClientId, ClientSecret, RefreshToken, CancellationToken.None));

        Assert.Contains("401", ex.Message);
        Assert.Contains("invalid_client", ex.Message);
    }

    [Fact]
    public void RequiredEnvVars_ListsAllThreeOAuthFields()
    {
        Assert.Equal(3, GoogleOAuthHelper.RequiredEnvVars.Count);
        Assert.Contains(GoogleOAuthHelper.ClientIdEnvVar, GoogleOAuthHelper.RequiredEnvVars);
        Assert.Contains(GoogleOAuthHelper.ClientSecretEnvVar, GoogleOAuthHelper.RequiredEnvVars);
        Assert.Contains(GoogleOAuthHelper.RefreshTokenEnvVar, GoogleOAuthHelper.RequiredEnvVars);
    }
}
