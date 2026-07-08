using System.Linq;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

public class NvidiaCatalogProviderTests
{
    private static readonly GpuInfo Rtx4070 =
        new() { Name = "GeForce RTX 4070", IsSimulated = false, DetectedVia = "test" };

    // AC-1: the recorded NVIDIA fixture parses into the expected live release list.
    [Fact]
    public void GetReleases_ParsesRecordedFixture_IntoLiveReleases()
    {
        var handler = StubHandler.Ok(Fixtures.Load(Fixtures.NvidiaLookup));
        var provider = new NvidiaCatalogProvider(handler);

        var releases = provider.GetReleases(Rtx4070);

        // Fixture has 5 rows (Game Ready + Studio per version); collapsed to one
        // Game Ready row per version, newest-first.
        Assert.Equal(new[] { "610.74", "610.62", "610.47" },
            releases.Select(r => r.Version).ToArray());

        // Every row is WHQL (IsBeta == 0) and live-sourced.
        Assert.All(releases, r => Assert.Equal("WHQL", r.Channel));
        Assert.All(releases, r => Assert.Equal("live", r.Source));

        // Each release keeps its real DownloadURL (the Game Ready dch-whql build).
        Assert.Equal(
            "https://us.download.nvidia.com/Windows/610.74/610.74-desktop-win10-win11-64bit-international-dch-whql.exe",
            releases[0].DownloadUrl);
        Assert.Equal(
            "https://us.download.nvidia.com/Windows/610.62/610.62-desktop-win10-win11-64bit-international-dch-whql.exe",
            releases[1].DownloadUrl);
        Assert.All(releases, r => Assert.EndsWith("-dch-whql.exe", r.DownloadUrl));

        // Dates normalized to ISO; sizes parsed from Content-Length string.
        Assert.Equal("2026-07-07", releases[0].ReleaseDate);
        Assert.Equal(979, releases[0].SizeMB);

        // GPU -> pfid resolution drove the request (RTX 4070 => psid 127 / pfid 1015).
        var url = handler.LastRequest!.RequestUri!.ToString();
        Assert.Contains("pfid=1015", url);
        Assert.Contains("psid=127", url);
    }

    // AC-2: a transport failure falls back to the mock catalog, marked source == "mock".
    [Fact]
    public void GetReleases_WhenHandlerThrows_FallsBackToMock()
    {
        var provider = new NvidiaCatalogProvider(StubHandler.Throws());

        var releases = provider.GetReleases(Rtx4070);

        Assert.Equal(5, releases.Count); // the five mock releases
        Assert.All(releases, r => Assert.Equal("mock", r.Source));
        Assert.Contains(releases, r => r.Version == "572.16");
    }

    // AC-2: a non-200 response also falls back to the mock catalog.
    [Fact]
    public void GetReleases_WhenServerErrors_FallsBackToMock()
    {
        var provider = new NvidiaCatalogProvider(StubHandler.Status(System.Net.HttpStatusCode.ServiceUnavailable));

        var releases = provider.GetReleases(Rtx4070);

        Assert.Equal(5, releases.Count);
        Assert.All(releases, r => Assert.Equal("mock", r.Source));
    }

    // AC-2: a simulated GPU never touches the network and returns the mock catalog.
    [Fact]
    public void GetReleases_WhenGpuSimulated_ReturnsMockWithoutHttp()
    {
        var handler = StubHandler.Ok(Fixtures.Load(Fixtures.NvidiaLookup));
        var provider = new NvidiaCatalogProvider(handler);

        var releases = provider.GetReleases(Gpu.Simulated);

        Assert.Null(handler.LastRequest); // no HTTP attempted
        Assert.Equal(5, releases.Count);
        Assert.All(releases, r => Assert.Equal("mock", r.Source));
    }

    // AC-2: an unrecognized GPU name (not in the pfid table) falls back without HTTP.
    [Fact]
    public void GetReleases_WhenGpuUnknown_ReturnsMockWithoutHttp()
    {
        var handler = StubHandler.Ok(Fixtures.Load(Fixtures.NvidiaLookup));
        var provider = new NvidiaCatalogProvider(handler);
        var unknown = new GpuInfo { Name = "GeForce GTX 1080 Ti", IsSimulated = false };

        var releases = provider.GetReleases(unknown);

        Assert.Null(handler.LastRequest);
        Assert.All(releases, r => Assert.Equal("mock", r.Source));
    }

    // Channel-mapping branch: a beta row (IsBeta == 1) maps to the Beta channel.
    [Fact]
    public void GetReleases_MapsBetaFlag_ToBetaChannel()
    {
        const string body = """
        { "Success":"1", "IDS":[
          { "downloadInfo": {
              "Success":"1", "Version":"999.10", "IsWHQL":"0", "IsBeta":"1", "IsCRD":"0",
              "ReleaseDateTime":"Mon Jan 05, 2026", "DownloadURLFileSize":"512.00 MB",
              "DownloadURL":"https://us.download.nvidia.com/Windows/999.10/999.10-beta.exe" } }
        ]}
        """;
        var provider = new NvidiaCatalogProvider(StubHandler.Ok(body));

        var rel = Assert.Single(provider.GetReleases(Rtx4070));
        Assert.Equal("999.10", rel.Version);
        Assert.Equal("Beta", rel.Channel);
        Assert.Equal("live", rel.Source);
    }
}
