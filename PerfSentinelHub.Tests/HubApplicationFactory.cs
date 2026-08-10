using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

public sealed class HubApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-api-{Guid.NewGuid():N}.db");

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(10_000));

    public HubDatabase Database => Services.GetRequiredService<HubDatabase>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.PostConfigure<HubOptions>(options =>
            {
                options.DatabasePath = _databasePath;
                options.Sources = [new SourceOptions
                {
                    Id = "test",
                    Name = "Test",
                    Environment = "test",
                    BaseUrl = new Uri("http://127.0.0.1:1")
                }];
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        File.Delete(_databasePath);
        File.Delete($"{_databasePath}-shm");
        File.Delete($"{_databasePath}-wal");
    }
}
