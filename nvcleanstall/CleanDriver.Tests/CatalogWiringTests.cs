using System.Collections.Generic;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

public class CatalogWiringTests
{
    private static string? Env(Dictionary<string, string?> d, string k) =>
        d.TryGetValue(k, out var v) ? v : null;

    // Selection: NvidiaCatalogProvider by default; MockCatalogProvider when the
    // --mock-catalog flag or CLEANDRIVER_MOCK_CATALOG=1 is set.
    [Fact]
    public void UseMock_DefaultsToLive()
    {
        var env = new Dictionary<string, string?>();
        Assert.False(CatalogProviderFactory.UseMock(new[] { "--headless" }, k => Env(env, k)));
    }

    [Fact]
    public void UseMock_TrueForFlag()
    {
        var env = new Dictionary<string, string?>();
        Assert.True(CatalogProviderFactory.UseMock(new[] { "--mock-catalog" }, k => Env(env, k)));
    }

    [Fact]
    public void UseMock_TrueForEnvVarOne()
    {
        var env = new Dictionary<string, string?> { ["CLEANDRIVER_MOCK_CATALOG"] = "1" };
        Assert.True(CatalogProviderFactory.UseMock(System.Array.Empty<string>(), k => Env(env, k)));
    }

    [Fact]
    public void UseMock_FalseForEnvVarOtherThanOne()
    {
        var env = new Dictionary<string, string?> { ["CLEANDRIVER_MOCK_CATALOG"] = "0" };
        Assert.False(CatalogProviderFactory.UseMock(System.Array.Empty<string>(), k => Env(env, k)));
    }

    // /api/catalog envelope: releases plus a derived source field.
    [Fact]
    public void CatalogResponse_MockProvider_ReportsMockSource()
    {
        var resp = CatalogEndpoint.Build(new MockCatalogProvider(), Gpu.Simulated);
        Assert.Equal("mock", resp.Source);
        Assert.Equal(5, resp.Releases.Count);
    }

    [Fact]
    public void CatalogResponse_LiveProvider_ReportsLiveSource()
    {
        var provider = new NvidiaCatalogProvider(StubHandler.Ok(Fixtures.Load(Fixtures.NvidiaLookup)));
        var gpu = new GpuInfo { Name = "GeForce RTX 4070", IsSimulated = false };

        var resp = CatalogEndpoint.Build(provider, gpu);

        Assert.Equal("live", resp.Source);
        Assert.Equal(3, resp.Releases.Count);
    }
}
