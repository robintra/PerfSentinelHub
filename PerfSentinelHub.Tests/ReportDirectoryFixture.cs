[assembly: AssemblyFixture(typeof(PerfSentinelHub.Tests.ReportDirectoryFixture))]

namespace PerfSentinelHub.Tests;

/// <summary>
///     Gives every host the suite builds a report directory it owns. The Hub defaults
///     <c>Hub:Analysis:ReportDirectory</c> to the container's <c>/data/reports</c>, which
///     <c>Path.IsPathFullyQualified</c> rejects on Windows, so each host would refuse to start
///     there. Bound configuration reads the environment, so one variable reaches them all.
/// </summary>
// Created by xUnit through the assembly attribute above.
// ReSharper disable once ClassNeverInstantiated.Global
public sealed class ReportDirectoryFixture : IDisposable
{
    internal static readonly string ReportDirectory = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-reports-{Guid.NewGuid():N}");

    public ReportDirectoryFixture()
    {
        Environment.SetEnvironmentVariable("Hub__Analysis__ReportDirectory", ReportDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(ReportDirectory))
            Directory.Delete(ReportDirectory, true);
    }
}
