using System.Globalization;
using System.Text.Json;
using PerfSentinelHub.Api;

namespace PerfSentinelHub.Tests;

// The same cases as the engine's energy_source_label tests, so the two labels
// cannot drift apart unnoticed.
public sealed class EnergySourceTests
{
    private static string? Label(string green)
    {
        using var document = JsonDocument.Parse(green);
        return EnergySource.Label(document.RootElement);
    }

    private static string Summary(string model, string services, string extra = "")
    {
        return "{\"energy_kwh\":0.1,\"energy_model\":\"" + model + "\"" + extra + "," + services + "}";
    }

    private static string Services(params (string Service, string Tag, double Ratio)[] rows)
    {
        var ratios = string.Join(",", rows.Select(r => $"\"{r.Service}\":{r.Ratio.ToString(CultureInfo.InvariantCulture)}"));
        var tags = string.Join(",", rows.Select(r => $"\"{r.Service}\":{JsonSerializer.Serialize(r.Tag)}"));
        return "\"per_service_measured_ratio\":{" + ratios + "},\"per_service_energy_model\":{" + tags + "}";
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"energy_kwh\":0.0}")]
    [InlineData("{\"energy_kwh\":\"1\"}")]
    public void No_energy_reads_as_no_label(string green)
    {
        Assert.Null(Label(green));
    }

    [Fact]
    public void An_intensity_tag_is_never_read_as_the_energy_source()
    {
        Assert.Equal("modeled from I/O counts",
            Label(Summary("electricity_maps_api", Services(("a", "electricity_maps_api", 0.0)))));
        Assert.Equal("modeled from I/O counts", Label("{\"energy_kwh\":0.1}"));
    }

    [Fact]
    public void Calibration_reads_from_the_suffix_and_from_the_flag()
    {
        const string calibrated = "modeled from I/O counts · calibrated";
        Assert.Equal(calibrated, Label(Summary("io_proxy_v3+cal", Services(("a", "io_proxy_v3", 0.0)))));
        Assert.Equal(calibrated, Label(Summary("io_proxy_v3", Services(("a", "io_proxy_v3+cal", 0.0)))));
        Assert.Equal(
            "source scaphandre_rapl on 1 of 2 services · rest modeled from I/O counts · calibrated",
            Label(Summary("scaphandre_rapl",
                Services(("a", "scaphandre_rapl", 1.0), ("b", "scaphandre_rapl", 0.0)),
                ",\"energy_calibrated\":true")));
        // Fully measured leaves nothing modeled to qualify.
        Assert.Equal("source redfish_bmc",
            Label(Summary("redfish_bmc", Services(("a", "redfish_bmc", 1.0)), ",\"energy_calibrated\":true")));
    }

    [Fact]
    public void Full_coverage_sorts_and_dedupes_the_tags()
    {
        Assert.Equal("source kepler_ebpf, scaphandre_rapl",
            Label(Summary("scaphandre_rapl", Services(
                ("a", "scaphandre_rapl+cal", 1.0), ("b", "kepler_ebpf", 1.0), ("c", "scaphandre_rapl", 1.0)))));
    }

    [Fact]
    public void A_hostile_tag_is_not_displayed()
    {
        var longTag = new string('a', 65);
        Assert.Equal("source redfish_bmc",
            Label(Summary("x", Services(
                ("a", longTag, 1.0), ("b", "kepler‮_ebpf", 1.0), ("c", "redfish_bmc", 1.0)))));
        Assert.Equal("source unknown on 1 of 2 services · rest modeled from I/O counts",
            Label(Summary("x", Services(("a", longTag, 1.0), ("b", "io_proxy_v3", 0.0)))));
    }
}
