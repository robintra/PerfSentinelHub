using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace PerfSentinelHub.Tests;

/// <summary>
/// A daemon that answers whatever the test says it does. One catch-all route
/// rather than a map per path: the handlers already branch on the request path,
/// and a view of a daemon reads three of them.
/// </summary>
internal sealed class FakeDaemon(WebApplication app, Uri baseUrl) : IAsyncDisposable
{
    public Uri BaseUrl { get; } = baseUrl;

    public static async Task<FakeDaemon> StartAsync(
        RequestDelegate handler,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(handler);
        await app.StartAsync(cancellationToken);
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        return new FakeDaemon(app, new Uri(addresses.Addresses.Single()));
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
