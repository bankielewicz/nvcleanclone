using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// GAP-03: real GPU detection hardening. The WMI-line -> GpuInfo parsing is extracted
// into pure functions (ParseVideoControllers / DetectFrom) so it is unit-testable
// without shelling out to powershell. GpuInfo shape is unchanged.
public class GpuDetectionTests
{
    // AC (single NVIDIA) + back-compat pin: one NVIDIA adapter parses to the same
    // GpuInfo Detect() has always produced — name normalized (leading "NVIDIA "
    // stripped), marketing version derived, not simulated, WMI provenance.
    [Fact]
    public void ParseVideoControllers_SingleNvidia_ReturnsNormalizedGpu()
    {
        var gpu = Gpu.ParseVideoControllers("NVIDIA GeForce RTX 5070|32.0.15.7086");

        Assert.NotNull(gpu);
        Assert.Equal("GeForce RTX 5070", gpu!.Name);
        Assert.Equal("570.86", gpu.InstalledDriverVersion);
        Assert.False(gpu.IsSimulated);
        Assert.Equal("system query (WMI Win32_VideoController)", gpu.DetectedVia);
    }

    // AC (NVIDIA + iGPU): with several adapters present the NVIDIA one is picked
    // deterministically, regardless of enumeration order (iGPU listed first here).
    [Fact]
    public void ParseVideoControllers_NvidiaPlusIgpu_PicksNvidiaDeterministically()
    {
        var output = "Intel(R) UHD Graphics 770|31.0.101.4502\n"
                   + "NVIDIA GeForce RTX 5070|32.0.15.7086";

        var gpu = Gpu.ParseVideoControllers(output);

        Assert.NotNull(gpu);
        Assert.Equal("GeForce RTX 5070", gpu!.Name);
        Assert.Equal("570.86", gpu.InstalledDriverVersion);
        Assert.False(gpu.IsSimulated);
    }

    // AC (no NVIDIA): no adapter matches -> null, which is the documented trigger
    // for the WMI -> simulated fallback (see DetectFrom below).
    [Fact]
    public void ParseVideoControllers_NoNvidia_ReturnsNull()
    {
        Assert.Null(Gpu.ParseVideoControllers("Intel(R) UHD Graphics 770|31.0.101.4502"));
    }

    // Fallback order (documented: WMI -> simulated): when parsing yields no NVIDIA
    // adapter, DetectFrom returns the Simulated GpuInfo (IsSimulated == true).
    [Fact]
    public void DetectFrom_NoNvidia_FallsBackToSimulated()
    {
        var gpu = Gpu.DetectFrom("Intel(R) UHD Graphics 770|31.0.101.4502");

        Assert.Equal(Gpu.Simulated, gpu);
        Assert.True(gpu.IsSimulated);
        Assert.Equal("GeForce RTX 4070", gpu.Name);
        Assert.Equal("simulated (no GPU query available)", gpu.DetectedVia);
    }

    // Fallback order: a failed/empty WMI query (null output) also lands on Simulated,
    // so a powershell failure never throws to the caller.
    [Fact]
    public void DetectFrom_NullOutput_FallsBackToSimulated()
    {
        Assert.Equal(Gpu.Simulated, Gpu.DetectFrom(null));
        Assert.Equal(Gpu.Simulated, Gpu.DetectFrom(""));
    }

    // MarketingVersion boundary — short: fewer than 5 digits has no derivable
    // marketing version.
    [Fact]
    public void MarketingVersion_Short_ReturnsNull()
    {
        Assert.Null(Gpu.MarketingVersion("1.23"));
    }

    // MarketingVersion boundary — long: a longer WMI string still resolves to the
    // last five digits (matching the canonical 32.0.15.7086 -> 570.86 rule).
    [Fact]
    public void MarketingVersion_Long_TakesLastFiveDigits()
    {
        Assert.Equal("570.86", Gpu.MarketingVersion("132.0.15.7086"));
    }

    // MarketingVersion boundary — dotless: a version with no dots still derives from
    // its last five digits.
    [Fact]
    public void MarketingVersion_Dotless_DerivesFromLastFiveDigits()
    {
        Assert.Equal("570.86", Gpu.MarketingVersion("320157086"));
    }
}
