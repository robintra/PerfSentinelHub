using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Maintenance;
using PerfSentinelHub.Storage;

if (args is ["backup", ..])
{
    // Any wrong arity must refuse loudly: the fallthrough would silently
    // discard the bare tokens and boot the full server instead.
    if (args is not ["backup", var backupDestination])
    {
        await Console.Error.WriteLineAsync("Usage: PerfSentinelHub backup <destination>");
        return 2;
    }

    // Reads the same appsettings + environment sources as the server, so the
    // configured Hub:DatabasePath applies without booting listeners or workers.
    var backupConfiguration = WebApplication.CreateSlimBuilder().Configuration;
    return await HubBackup.RunAsync(
        backupConfiguration[$"{HubOptions.SectionName}:{nameof(HubOptions.DatabasePath)}"]
        ?? new HubOptions().DatabasePath,
        backupDestination);
}

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, HubJsonContext.Default);
    // Matches the envelope perf-sentinel itself emits, so one contract reads
    // the same on both sides. Every pre-existing field is a single word and
    // serialises identically under either policy.
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
});
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IValidateOptions<HubOptions>, HubOptionsValidator>());
builder.Services.AddOptions<HubOptions>()
    .BindConfiguration(HubOptions.SectionName)
    .ValidateOnStart();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HubDatabase>();
builder.Services.AddSingleton<EngineProbe>();
// Registered here, on builder.Services, so it starts before the listener does:
// Build() adds the web host's own hosted service after every registration made
// on the builder, and hosted services start one after another. The probe has
// therefore answered before the first request arrives, which is what lets a run
// trust SupportsDaemonUrl instead of racing it. Moving this after Build(), or
// into the web host, would silently hand the first runs a static report.
builder.Services.AddHostedService(provider => provider.GetRequiredService<EngineProbe>());
builder.Services.AddSingleton<ImportGate>();
builder.Services.AddSingleton<ImportMetrics>();
builder.Services.AddSingleton<ImportAdmission>();
builder.Services.AddSingleton<DaemonViewGate>();
builder.Services.AddSingleton<IncidentRefreshGate>();
builder.Services.AddSingleton<AnalysisRunner>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHttpClient<DaemonClient>().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = false,
    UseCookies = false
});
// Its own client, not the one the sources share: this destination is not a
// source, and keeping them apart is what lets the handler above stay described
// as "how the Hub talks to a daemon".
builder.Services.AddHttpClient(UpdateChecker.ClientName).ConfigurePrimaryHttpMessageHandler(() =>
    // Redirects off for the same reason as the sources above: a 302 would send
    // this request to a host that appears nowhere in the configuration.
    new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = false,
        UseCookies = false
    });
builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<UpdateChecker>());
builder.Services.AddTransient<IncidentReader>();
builder.Services.AddTransient<SourcePoller>();
builder.Services.AddHostedService<PollWorker>();
builder.Services.AddHostedService<RetentionWorker>();

var app = builder.Build();

await app.Services.GetRequiredService<HubDatabase>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

// The launcher and the reports it opens are served from the same origin: the
// theme handoff between them goes through sessionStorage, with no URL
// parameter and no postMessage.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Nothing here carries a fingerprint in its name, so a cached app.js can
    // outlive the API it talks to across a Hub upgrade. Without a Cache-Control
    // the browser picks its own freshness from the last-modified date and holds
    // the old file for minutes. no-cache still revalidates against the ETag, so
    // an unchanged file costs a 304 and not a download.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache"
});

app.MapHubApi();
app.MapAnalysisApi();
app.MapMetrics();

await app.RunAsync();
return 0;
