using System.Linq;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// Back-compat pin (GAP-01 AC-3 base): MockCatalogProvider wraps the existing
// Catalog.Releases() with no behavior change, stamping source == "mock".
public class MockCatalogProviderTests
{
    [Fact]
    public void GetReleases_ReturnsTheFiveMockReleases_MarkedMock()
    {
        var releases = new MockCatalogProvider().GetReleases(Gpu.Simulated);

        // Exactly the five catalog.json releases, newest-first ordering preserved.
        Assert.Equal(
            new[] { "572.16", "571.96", "571.59", "570.86", "566.36" },
            releases.Select(r => r.Version).ToArray());

        // Core fields unchanged from stock data.
        Assert.Single(releases, r => r.Channel == "Beta");
        Assert.Contains(releases, r => r.Channel == "Beta" && r.Version == "571.59");
        Assert.Equal("2026-06-24", releases[0].ReleaseDate);
        Assert.Equal(812, releases[0].SizeMB);

        // Additive markers: every mock release carries source == "mock" and no live URL.
        Assert.All(releases, r => Assert.Equal("mock", r.Source));
        Assert.All(releases, r => Assert.Null(r.DownloadUrl));
    }

    [Fact]
    public void GetReleases_IgnoresGpu_AlwaysMock()
    {
        var realGpu = new GpuInfo { Name = "GeForce RTX 4070", IsSimulated = false };
        var releases = new MockCatalogProvider().GetReleases(realGpu);
        Assert.Equal(5, releases.Count);
        Assert.All(releases, r => Assert.Equal("mock", r.Source));
    }
}
