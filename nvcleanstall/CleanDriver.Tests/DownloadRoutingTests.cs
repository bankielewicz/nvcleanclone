using System.IO;
using System.Linq;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

public class DownloadRoutingTests
{
    // Route-by-source keys on server-resolved Source=="live" (+ a real URL). Mock and
    // mock-stamped fallback releases always take the simulation (AC-4 back-compat).
    [Fact]
    public void ShouldRealDownload_OnlyForLiveWithUrl()
    {
        var live = new Release { Version = "610.74", Source = "live", DownloadUrl = "https://us.download.nvidia.com/x.exe" };
        var mock = new Release { Version = "572.16", Source = "mock" };
        var fallback = new Release { Version = "572.16", Source = "mock" }; // live provider fell back to mock
        var liveNoUrl = new Release { Version = "610.74", Source = "live" };

        Assert.True(Jobs.ShouldRealDownload(live));
        Assert.False(Jobs.ShouldRealDownload(mock));
        Assert.False(Jobs.ShouldRealDownload(fallback));
        Assert.False(Jobs.ShouldRealDownload(liveNoUrl));
    }

    // A mock catalog version has a real package dir; a live version does not.
    [Fact]
    public void HasCatalogPackage_TrueForMockVersion_FalseForLive()
    {
        Assert.True(Packages.HasCatalogPackage("572.16"));
        Assert.False(Packages.HasCatalogPackage("610.74"));
    }

    // Live versions get a sample template: newest mock package relabeled to the live
    // version, flagged SampleComponents, with the PackageSource still under data/packages
    // (never the downloaded .exe).
    [Fact]
    public void LoadSampleTemplate_RelabelsAndFlagsSample()
    {
        var (manifest, source) = Packages.LoadSampleTemplate("610.74");

        Assert.Equal("610.74", manifest.Version);
        Assert.True(manifest.SampleComponents);
        Assert.Contains(manifest.Components, c => c.Required);
        Assert.Equal("catalog", source.Kind);
        Assert.Contains("packages", source.Dir!.Replace('\\', '/'));
        Assert.Null(source.ZipPath);
    }
}
