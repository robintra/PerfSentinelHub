using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace PerfSentinelHub.Tests;

/// <summary>
///     The Hub signing browser users in against an OAuth2 provider of its own,
///     played here by a Kestrel that answers the token and userinfo calls.
/// </summary>
public sealed class AuthenticationTests : IAsyncLifetime
{
    private readonly HubApplicationFactory _hub = new();
    private WebApplicationFactory<Program> _factory = null!;
    private FakeDaemon _provider = null!;

    public async ValueTask InitializeAsync()
    {
        // The code becomes the access token, so a test picks the userinfo it gets
        // back by the code it hands the callback: "anon" names nobody.
        _provider = await FakeDaemon.StartAsync(async context =>
        {
            switch (context.Request.Path.Value)
            {
                case "/token":
                    var form = await context.Request.ReadFormAsync();
                    await context.Response.WriteAsJsonAsync(new Dictionary<string, string>
                        { ["access_token"] = form["code"].ToString(), ["token_type"] = "Bearer" });
                    break;
                case "/userinfo" when context.Request.Headers.Authorization == "Bearer anon":
                    await context.Response.WriteAsJsonAsync(new Dictionary<string, string> { ["sub"] = "2" });
                    break;
                case "/userinfo":
                    await context.Response.WriteAsJsonAsync(new Dictionary<string, string>
                        { ["sub"] = "1", ["email"] = "alice@example.internal" });
                    break;
                default:
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    break;
            }
        }, TestContext.Current.CancellationToken);

        _factory = _hub.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Hub:Auth:Enabled", "true");
            builder.UseSetting("Hub:Auth:AuthorizationEndpoint", "https://idp.example.internal/authorize");
            builder.UseSetting("Hub:Auth:TokenEndpoint", new Uri(_provider.BaseUrl, "token").ToString());
            builder.UseSetting("Hub:Auth:UserInformationEndpoint", new Uri(_provider.BaseUrl, "userinfo").ToString());
            builder.UseSetting("Hub:Auth:ClientId", "hub");
            builder.UseSetting("Hub:Auth:ClientSecret", "secret"); // gitleaks:allow -- synthetic test credential
            // The factory's clock sits in 1970, where every cookie the handlers
            // write has already expired before the browser can send it back.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(TimeProvider.System);
            });
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _hub.DisposeAsync();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task A_page_sends_the_browser_to_the_provider_with_pkce()
    {
        using var client = Client();

        using var response = await client.GetAsync("/", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith("https://idp.example.internal/authorize?", location.ToString(), StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("hub", query["client_id"]);
        Assert.Equal("https://localhost/auth/callback", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.True(query.ContainsKey("state"));
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/sources")]
    [InlineData("/api/analyses")]
    [InlineData("/app.js")]
    public async Task What_the_launcher_reads_answers_401_or_redirects_without_a_session(string path)
    {
        using var client = Client();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        // A fetch cannot follow a redirect to another origin, so the API says
        // 401 and the launcher reloads itself into the sign-in.
        Assert.Equal(
            path == "/app.js" ? HttpStatusCode.Redirect : HttpStatusCode.Unauthorized,
            response.StatusCode);
    }

    [Theory]
    [InlineData("iframe", HttpStatusCode.Unauthorized)]
    [InlineData("document", HttpStatusCode.Redirect)]
    [InlineData(null, HttpStatusCode.Redirect)]
    public async Task A_report_answers_401_in_the_launcher_frame_and_signs_in_as_a_page(
        string? destination, HttpStatusCode expected)
    {
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/reports/0123456789abcdef.html");
        if (destination is not null) request.Headers.Add("Sec-Fetch-Dest", destination);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        // A shared report link opened on its own is a page: it has to reach the
        // sign-in, not stop at a bare 401.
        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/findings")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/metrics")]
    public async Task Machine_routes_stay_open(string path)
    {
        using var client = Client();

        using var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_import_still_answers_to_its_key_alone()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        using var refused = await client.PostAsync("/api/import/findings?source_id=test",
            new StringContent("[]", Encoding.UTF8, "application/json"), cancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Null(refused.Headers.Location);
    }

    [Fact]
    public async Task A_signed_in_user_is_the_identity_and_the_proxy_header_is_ignored()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        using var challenge = await client.GetAsync("/", cancellationToken);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        using var callback = await client.GetAsync(
            $"/auth/callback?code=abc&state={Uri.EscapeDataString(state!)}", cancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.Add("X-Forwarded-User", "mallory@example.internal");
        using var status = await client.SendAsync(request, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        var payload = await status.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Assert.Equal("alice@example.internal", payload.GetProperty("identity").GetString());
    }

    [Theory]
    [InlineData("error=access_denied")]
    [InlineData("code=anon")]
    public async Task A_refused_or_nameless_sign_in_answers_403_rather_than_failing(string outcome)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        using var challenge = await client.GetAsync("/", cancellationToken);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        using var callback = await client.GetAsync(
            $"/auth/callback?{outcome}&state={Uri.EscapeDataString(state!)}", cancellationToken);

        // Cancelling on the provider's consent screen, or a userinfo without the
        // configured field, is not a Hub fault. No redirect either: it would send
        // the user straight back to the screen they just cancelled.
        Assert.Equal(HttpStatusCode.Forbidden, callback.StatusCode);
        Assert.Null(callback.Headers.Location);
        Assert.Contains("Sign-in refused", await callback.Content.ReadAsStringAsync(cancellationToken),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replayed_callback_points_at_the_hub_rather_than_at_a_reload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var client = Client();

        using var challenge = await client.GetAsync("/", cancellationToken);
        var state = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query)["state"];
        var callbackPath = $"/auth/callback?error=access_denied&state={Uri.EscapeDataString(state!)}";
        using var first = await client.GetAsync(callbackPath, cancellationToken);
        using var replay = await client.GetAsync(callbackPath, cancellationToken);

        // The correlation cookie is spent by the first answer, so reloading the
        // callback can never succeed: the line has to send the user home.
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        var line = await replay.Content.ReadAsStringAsync(cancellationToken);
        Assert.Contains("home page", line, StringComparison.Ordinal);
        Assert.DoesNotContain("Reload", line, StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient Client()
    {
        // https: the session and correlation cookies are Secure, and the cookie
        // container would not send them back over http.
        return _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
    }
}
