using PerfSentinelHub.Api;
using PerfSentinelHub.Collection;

namespace PerfSentinelHub.Tests;

public sealed class DaemonStateTests
{
    [Fact]
    public void A_daemon_that_did_not_answer_reads_unreachable()
    {
        // Nothing below the status is known, so no other verdict is available.
        Assert.Equal(DaemonView.Unreachable, DaemonView.Classify(null, 3));
    }

    [Theory]
    // The advisor's own line, and the daemon reports the hint at it, not past it.
    [InlineData(900, 1000, DaemonView.NearCapacity)]
    [InlineData(899, 1000, DaemonView.Ok)]
    [InlineData(1000, 1000, DaemonView.NearCapacity)]
    public void A_gauge_is_read_against_the_line_the_daemon_uses(long value, long capacity, string expected)
    {
        Assert.Equal(expected, DaemonView.Classify(Status(activeTraces: value, maxActiveTraces: capacity), 0));
    }

    [Fact]
    public void A_capacity_of_zero_is_an_unknown_and_never_a_full_gauge()
    {
        // Dividing by it would clamp to 100 % and report a quiet daemon as
        // saturated, which is the opposite of what the figure means.
        var status = Status(activeTraces: 0, maxActiveTraces: 0);

        Assert.Null(DaemonView.Read(status).Traces.Pct);
        Assert.False(DaemonView.Read(status).Traces.AtCapacity);
        Assert.Equal(DaemonView.Unknown, DaemonView.Classify(status, 0));
    }

    [Fact]
    public void A_daemon_that_publishes_no_capacity_reads_unknown_rather_than_ok()
    {
        // The Hub has no evidence either way, and "ok" would be a claim.
        Assert.Equal(DaemonView.Unknown, DaemonView.Classify(Status(), 0));
    }

    [Fact]
    public void A_warning_under_the_line_reads_advised()
    {
        var status = Status(activeTraces: 10, maxActiveTraces: 1000);

        Assert.Equal(DaemonView.Advised, DaemonView.Classify(status, 1));
    }

    [Fact]
    public void A_full_gauge_outranks_a_warning()
    {
        // The stronger statement wins: the hint is still rendered underneath.
        var status = Status(activeTraces: 990, maxActiveTraces: 1000);

        Assert.Equal(DaemonView.NearCapacity, DaemonView.Classify(status, 4));
    }

    [Fact]
    public void Any_one_gauge_at_its_cap_is_enough()
    {
        var status = Status(activeTraces: 1, maxActiveTraces: 1000) with
        {
            StoredFindings = 10_000,
            MaxRetainedFindings = 10_000
        };

        Assert.Equal(DaemonView.NearCapacity, DaemonView.Classify(status, 0));
    }

    private static DaemonStatus Status(long? activeTraces = null, long? maxActiveTraces = null) =>
        new("0.16.0", 864_000, activeTraces, maxActiveTraces, null, null, null, null);
}
