using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Api;

/// <summary>
///     Browser sign-in with the framework's own cookie and OAuth2 handlers, when
///     Hub:Auth:Enabled. Off, nothing here is registered and the pipeline is the one
///     the Hub always had. On, every route needs a session except the ones a machine
///     calls: findings for IDE plugins and CI, the import (its own key), health and
///     metrics.
/// </summary>
public static partial class HubAuthentication
{
    private const string Scheme = "oauth";

    public static bool AddHubAuthentication(this WebApplicationBuilder builder)
    {
        // Read from configuration rather than IOptions: the handlers are
        // registered or not, which is decided before the container exists.
        var auth = builder.Configuration.GetSection($"{HubOptions.SectionName}:Auth").Get<AuthOptions>();
        if (auth is not { Enabled: true }) return false;

        builder.Services.AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = Scheme;
            })
            .AddCookie(options =>
            {
                options.Cookie.Name = "hub_session";
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;
            })
            .AddOAuth(Scheme, options =>
            {
                options.AuthorizationEndpoint = auth.AuthorizationEndpoint!.ToString();
                options.TokenEndpoint = auth.TokenEndpoint!.ToString();
                options.UserInformationEndpoint = auth.UserInformationEndpoint!.ToString();
                options.ClientId = auth.ClientId!;
                options.ClientSecret = auth.ClientSecret!;
                options.CallbackPath = "/auth/callback";
                options.UsePkce = true;
                options.Scope.Clear();
                foreach (var scope in auth.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    options.Scope.Add(scope);
                options.ClaimActions.MapJsonKey(ClaimTypes.Name, auth.IdentityClaim);
                options.Events.OnCreatingTicket = ReadUserInformationAsync;
                options.Events.OnRemoteFailure = RefuseSignInAsync;
                options.Events.OnRedirectToAuthorizationEndpoint = context =>
                {
                    // A fetch cannot follow a redirect to the provider's origin. The
                    // API says 401 and the launcher reloads itself into the sign-in.
                    // A report says 401 only inside the launcher's frame: a shared
                    // report link opened on its own is a page and signs in like one.
                    if (context.Request.Path.StartsWithSegments("/api") ||
                        (context.Request.Path.StartsWithSegments("/reports") &&
                         context.Request.Headers["Sec-Fetch-Dest"] == "iframe"))
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    else
                        context.Response.Redirect(context.RedirectUri);
                    return Task.CompletedTask;
                };
            });

        builder.Services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser().Build());

        // Next to the database, the one writable volume: the container's home is
        // read-only, and keys kept in memory would sign everyone out on restart.
        builder.Services.AddOptions<KeyManagementOptions>()
            .Configure<IOptions<HubOptions>, ILoggerFactory>((options, hub, loggers) =>
                options.XmlRepository = new FileSystemXmlRepository(
                    new DirectoryInfo(Path.Combine(Path.GetDirectoryName(hub.Value.DatabasePath)!, "keys")),
                    loggers));

        // TLS ends at the ingress: without the forwarded scheme the redirect_uri
        // sent to the provider would read http. Only the scheme is trusted, and
        // from any peer, since the ingress address is not known in advance.
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        });
        return true;
    }

    public static void UseHubAuthentication(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseAuthentication();
        app.UseAuthorization();
    }

    /// <summary>
    ///     A cancelled consent screen or a userinfo without the identity field
    ///     would otherwise surface as an unhandled 500. No redirect: it would send
    ///     the user straight back to the screen they just cancelled. Nor "reload":
    ///     reloading this callback replays a state whose correlation cookie is
    ///     already spent, and fails again. The line names no URL built from the
    ///     request, whose Host a client chooses.
    /// </summary>
    private static async Task RefuseSignInAsync(RemoteFailureContext context)
    {
        LogSignInRefused(
            context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(HubAuthentication)),
            context.Failure?.Message ?? "unknown");
        context.HandleResponse();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(
            "Sign-in refused. Open the Hub's home page to try again.",
            context.HttpContext.RequestAborted);
    }

    [LoggerMessage(1900, LogLevel.Warning, "Sign-in refused: {Reason}")]
    private static partial void LogSignInRefused(ILogger logger, string reason);

    private static async Task ReadUserInformationAsync(OAuthCreatingTicketContext context)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, context.Options.UserInformationEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await context.Backchannel.SendAsync(request, context.HttpContext.RequestAborted);
        response.EnsureSuccessStatusCode();
        using var user = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(context.HttpContext.RequestAborted),
            cancellationToken: context.HttpContext.RequestAborted);
        context.RunClaimActions(user.RootElement);
        // Fails the sign-in rather than recording a nameless session: the
        // provider's userinfo does not carry the configured field.
        if (context.Identity?.Name is not { Length: > 0 })
            throw new InvalidOperationException("The provider's userinfo lacks the Hub:Auth:IdentityClaim field.");
    }
}
