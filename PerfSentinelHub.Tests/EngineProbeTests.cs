using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PerfSentinelHub.Analysis;
using PerfSentinelHub.Configuration;

namespace PerfSentinelHub.Tests;

// The probe runs a real subprocess, and the fake binary is a shell script.
// The Hub only ever ships in a Linux container.
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class EngineProbeTests : IDisposable
{
    private readonly List<string> _scripts = [];

    [Fact]
    public async Task No_configured_binary_leaves_the_version_unknown()
    {
        var probe = Probe(null);

        await probe.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(probe.Version);
    }

    [Theory]
    // clap prints the name and the version on one line.
    [InlineData("perf-sentinel 0.16.0", "0.16.0")]
    [InlineData("perf-sentinel 0.16.0-rc.1\n", "0.16.0-rc.1")]
    // A binary that answers something else is not one we can compare against.
    // The last token of a sentence is a word, and a version starts with a digit.
    [InlineData("not a version line", null)]
    [InlineData("perf-sentinel version unknown", null)]
    [InlineData("", null)]
    public async Task A_version_line_is_read_and_anything_else_is_refused(string output, string? expected)
    {
        var probe = Probe(WriteScript($"printf '%s' \"{output}\""));

        await probe.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, probe.Version);
    }

    [Fact]
    public async Task A_binary_that_fails_leaves_the_version_unknown()
    {
        var probe = Probe(WriteScript("printf 'perf-sentinel 0.16.0'\nexit 1"));

        await probe.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(probe.Version);
    }

    [Fact]
    public async Task A_missing_binary_leaves_the_version_unknown_without_taking_the_host_down()
    {
        var probe = Probe(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"));

        await probe.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(probe.Version);
    }

    public void Dispose()
    {
        foreach (var script in _scripts)
            File.Delete(script);
    }

    private static EngineProbe Probe(string? binaryPath) =>
        new(
            Options.Create(new HubOptions { Analysis = new AnalysisOptions { EngineBinaryPath = binaryPath } }),
            NullLogger<EngineProbe>.Instance);

    private string WriteScript(string body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"engine-probe-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _scripts.Add(path);
        return path;
    }
}
