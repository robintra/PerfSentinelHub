namespace PerfSentinelHub.Api;

public static class ApiEndpoints
{
    public static void MapHubApi(this WebApplication app)
    {
        var version = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString() ?? "unknown";

        app.MapGet("/api/status", () => new StatusResponse("perf-sentinel-hub", version));
        app.MapGet("/health/live", () => TypedResults.Ok());
        app.MapGet("/health/ready", () => TypedResults.Ok());
    }
}
