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

    // Hardening: a non-numeric driver string has no derivable marketing version and
    // must return null (previously the dot-strip yielded a garbage value like
    // "5ab.cd"), so the caller can fall back to the raw string.
    [Theory]
    [InlineData("garbage-version")]
    [InlineData("31.0.15.abcd")]
    [InlineData("N/A")]
    public void MarketingVersion_NonNumeric_ReturnsNull(string wmiVersion)
    {
        Assert.Null(Gpu.MarketingVersion(wmiVersion));
    }

    // AC (malformed driver string): an NVIDIA adapter whose driver version can't be
    // parsed is still detected — the raw driver string is surfaced verbatim rather
    // than a fabricated marketing version, and the GPU is not dropped.
    [Fact]
    public void ParseVideoControllers_MalformedDriverString_FallsBackToRawVersion()
    {
        var gpu = Gpu.ParseVideoControllers("NVIDIA GeForce RTX 5070|garbage-version");

        Assert.NotNull(gpu);
        Assert.Equal("GeForce RTX 5070", gpu!.Name);
        Assert.Equal("garbage-version", gpu.InstalledDriverVersion);
        Assert.False(gpu.IsSimulated);
    }
}
