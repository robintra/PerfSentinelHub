using PerfSentinelHub.Api;

var builder = WebApplication.CreateSlimBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, HubJsonContext.Default));

var app = builder.Build();

app.MapHubApi();

app.Run();

public partial class Program;
