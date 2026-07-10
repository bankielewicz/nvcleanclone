using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// HARD-06: /api/catalog gains an additive `sourceDetail` that explains WHY a mock
// catalog is in play — so the marker can read "(mock mode)" when mock was chosen
// deliberately instead of always claiming "(GPU not matched)". The three reason
// buckets ("mock mode" / "GPU not matched" / "live lookup failed") are asserted here
// through CatalogEndpoint.Build; the render text is verified in the PR transcript
// (wwwroot rendering exemption).
public class CatalogSourceDetailTests
{
    private static readonly GpuInfo Rtx4070 =
        new() { Name = "GeForce RTX 4070", IsSimulated = false, DetectedVia = "test" };

    // Deliberateness is decided at the factory: mock chosen via --mock-catalog reports
    // "mock mode". Routed through Create (the real fix locus), not the wrapper directly,
    // so the factory line is what this red forces.
    [Fact]
    public void Build_DeliberateMock_ReportsMockModeDetail()
    {
        var provider = CatalogProviderFactory.Create(new[] { "--mock-catalog" }, _ => null);

        var resp = CatalogEndpoint.Build(provider, Gpu.Simulated);

        Assert.Equal("mock", resp.Source);
        Assert.Equal("mock mode", resp.SourceDetail);
    }

    // Pin-4 negative: a directly-constructed MockCatalogProvider (as tests and the live
    // provider's fallback build it) must NOT claim deliberateness on its own.
    [Fact]
    public void Build_DirectMockProvider_HasNoMockModeClaim()
    {
        var resp = CatalogEndpoint.Build(new MockCatalogProvider(), Gpu.Simulated);

        Assert.Equal("mock", resp.Source);
        Assert.Null(resp.SourceDetail);
    }

    // NvidiaCatalogProvider fallback site: a simulated GPU never touches the network and
    // buckets as GPU-unmatched.
    [Fact]
    public void Build_NvidiaFallback_GpuSimulated_ReportsGpuNotMatched()
    {
        var provider = new NvidiaCatalogProvider(StubHandler.Ok(Fixtures.Load(Fixtures.NvidiaLookup)));

        var resp = CatalogEndpoint.Build(provider, Gpu.Simulated);

        Assert.Equal("mock", resp.Source);
        Assert.Equal("GPU not matched", resp.SourceDetail);
    }

    // NvidiaCatalogProvider fallback site: a GPU absent from the pfid table buckets as
    // GPU-unmatched.
    [Fact]
    public void Build_NvidiaFallback_GpuUnknown_ReportsGpuNotMatched()
    {
        var provider = new NvidiaCatalogProvider(StubHandler.Ok(Fixtures.Load(Fixtures.NvidiaLookup)));
        var unknown = new GpuInfo { Name = "GeForce GTX 1080 Ti", IsSimulated = false };

        var resp = CatalogEndpoint.Build(provider, unknown);

        Assert.Equal("mock", resp.Source);
        Assert.Equal("GPU not matched", resp.SourceDetail);
    }

    // NvidiaCatalogProvider fallback site: a live lookup that returns zero rows buckets as
    // live-lookup-failed.
    [Fact]
    public void Build_NvidiaFallback_NoRows_ReportsLiveLookupFailed()
    {
        var provider = new NvidiaCatalogProvider(StubHandler.Ok("""{ "Success":"1", "IDS":[] }"""));

        var resp = CatalogEndpoint.Build(provider, Rtx4070);

        Assert.Equal("mock", resp.Source);
        Assert.Equal("live lookup failed", resp.SourceDetail);
    }

    // NvidiaCatalogProvider fallback site: a transport failure (the catch) buckets as
    // live-lookup-failed.
    [Fact]
    public void Build_NvidiaFallback_HandlerThrows_ReportsLiveLookupFailed()
    {
        var provider = new NvidiaCatalogProvider(StubHandler.Throws());

        var resp = CatalogEndpoint.Build(provider, Rtx4070);

        Assert.Equal("mock", resp.Source);
        Assert.Equal("live lookup failed", resp.SourceDetail);
    }

    // Live success carries no detail; WhenWritingNull then omits the field over the wire.
    [Fact]
    public void Build_LiveSuccess_HasNoDetail()
    {
        var provider = new NvidiaCatalogProvider(StubHandler.Ok(Fixtures.Load(Fixtures.NvidiaLookup)));

        var resp = CatalogEndpoint.Build(provider, Rtx4070);

        Assert.Equal("live", resp.Source);
        Assert.Null(resp.SourceDetail);
    }
}
