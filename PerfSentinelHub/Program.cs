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
        Console.Error.WriteLine("Usage: PerfSentinelHub backup <destination>");
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
builder.Services.AddHostedService(provider => provider.GetRequiredService<EngineProbe>());
builder.Services.AddSingleton<ImportGate>();
builder.Services.AddSingleton<AnalysisRunner>();
builder.Services.AddHostedService<AnalysisWorker>();
builder.Services.AddHttpClient<DaemonClient>().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = false,
    UseCookies = false
});
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
app.UseStaticFiles();

app.MapHubApi();
app.MapAnalysisApi();

await app.RunAsync();
return 0;

// Exposed for WebApplicationFactory<Program> integration tests.
// ReSharper disable once ClassNeverInstantiated.Global
#pragma warning disable ASP0027
public partial class Program;
#pragma warning restore ASP0027
