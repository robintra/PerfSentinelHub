using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using PerfSentinelHub.Collection;
using PerfSentinelHub.Configuration;
using PerfSentinelHub.Maintenance;
using PerfSentinelHub.Storage;

namespace PerfSentinelHub.Tests;

// Created by xUnit through IClassFixture<T>.
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class HubApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-api-{Guid.NewGuid():N}.db");

    private FakeTimeProvider Clock { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(10_000));

    public HubDatabase Database => Services.GetRequiredService<HubDatabase>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            RemoveBackgroundWorkers(services);

            services.PostConfigure<HubOptions>(options =>
            {
                options.DatabasePath = _databasePath;
                // Off rather than removed: the hosted service is registered by a
                // factory, so it carries no ImplementationType for the filter
                // above to match, and one flag stops it before its first request.
                options.UpdateCheck = new UpdateCheckOptions { Enabled = false };
                options.Sources =
                [
                    new SourceOptions
                    {
                        Id = "test",
                        Name = "Test",
                        Environment = "test",
                        BaseUrl = new Uri("http://127.0.0.1:1"),
                        ImportApiKey = "0123456789abcdef0123456789abcdef" // gitleaks:allow -- synthetic test credential
                    }
                ];
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

    /// <summary>
    ///     Drops the polling and retention workers so a test observes only what it
    ///     writes itself. Both run within milliseconds of startup, so leaving them in
    ///     makes any assertion on <c>source_state</c> a race against the first poll.
    /// </summary>
    internal static void RemoveBackgroundWorkers(IServiceCollection services)
    {
        var workers = services.Where(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType is { } type &&
            (type == typeof(PollWorker) || type == typeof(RetentionWorker))).ToArray();
        foreach (var worker in workers)
            services.Remove(worker);
    }
}
