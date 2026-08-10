using PerfSentinelHub.Api;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Configuration;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, HubJsonContext.Default));
builder.Services.TryAddEnumerable(
    ServiceDescriptor.Singleton<IValidateOptions<HubOptions>, HubOptionsValidator>());
builder.Services.AddOptions<HubOptions>()
    .BindConfiguration(HubOptions.SectionName)
    .ValidateOnStart();

var app = builder.Build();

app.MapHubApi();

app.Run();

public partial class Program;
