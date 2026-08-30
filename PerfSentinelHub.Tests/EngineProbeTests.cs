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

    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        $"perf-sentinel-hub-probe-{Guid.NewGuid():N}");

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

    [Theory]
    // The engine declares --daemon-url inside #[cfg(feature = "daemon")], so
    // two binaries of the same version disagree here. The binary is asked
    // rather than the version consulted.
    [InlineData("      --daemon-url <URL>\n          Daemon URL for the HTML live mode.", true)]
    [InlineData("      --output <FILE>\n          Where to write the report.", false)]
    public async Task Whether_the_binary_takes_a_daemon_url_is_read_from_its_own_help(
        string help,
        bool expected)
    {
        var probe = Probe(WriteScript(
            $"if [ \"$1\" = \"report\" ]; then printf '%s' \"{help}\"; exit 0; fi\n"
            + "printf 'perf-sentinel 0.16.0'"));

        await probe.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("0.16.0", probe.Version);
        Assert.Equal(expected, probe.SupportsDaemonUrl);
    }

    [Fact]
    public async Task A_binary_whose_help_cannot_be_read_renders_static_rather_than_failing()
    {
        // Refusing the help is not proof the flag is missing, but guessing the
        // other way costs every daemon run its report.
        var probe = Probe(WriteScript(
            "if [ \"$1\" = \"report\" ]; then exit 2; fi\nprintf 'perf-sentinel 0.16.0'"));

        await probe.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal("0.16.0", probe.Version);
        Assert.False(probe.SupportsDaemonUrl);
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
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private EngineProbe Probe(string? binaryPath) =>
        new(
            Options.Create(new HubOptions
            {
                Analysis = new AnalysisOptions
                {
                    EngineBinaryPath = binaryPath,
                    // The probe runs from the report directory, so the test must
                    // name one it owns rather than inherit the /data default.
                    ReportDirectory = _workspace
                }
            }),
            NullLogger<EngineProbe>.Instance);

    private string WriteScript(string body)
    {
        Directory.CreateDirectory(_workspace);
        var path = Path.Combine(_workspace, $"engine-probe-{Guid.NewGuid():N}.sh");
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _scripts.Add(path);
        return path;
    }
}
