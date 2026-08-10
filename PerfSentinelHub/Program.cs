using System.Net;
using PerfSentinelHub.Api;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;
using PerfSentinelHub.Collection;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, HubJsonContext.Default));
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IValidateOptions<HubOptions>, HubOptionsValidator>());
builder.Services.AddOptions<HubOptions>()
    .BindConfiguration(HubOptions.SectionName)
    .ValidateOnStart();
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HubDatabase>();
builder.Services.AddHttpClient<DaemonClient>().ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AutomaticDecompression = DecompressionMethods.All,
    AllowAutoRedirect = false,
    UseCookies = false
});
builder.Services.AddTransient<SourcePoller>();

var app = builder.Build();

await app.Services.GetRequiredService<HubDatabase>()
    .InitializeAsync(app.Lifetime.ApplicationStopping);

app.MapHubApi();

app.Run();

public partial class Program;
