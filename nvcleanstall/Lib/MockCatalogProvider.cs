namespace CleanDriver.Lib;

/// <summary>
/// Wraps the existing <see cref="Catalog.Releases"/> reader (data/catalog.json) with
/// no behavior change, stamping every release <c>Source = "mock"</c>. Used for offline
/// / deterministic verification runs and as the fallback for <see cref="NvidiaCatalogProvider"/>.
/// </summary>
public sealed class MockCatalogProvider : ICatalogProvider
{
    public const string SourceName = "mock";

    public IReadOnlyList<Release> GetReleases(GpuInfo gpu) =>
        Catalog.Releases()
            .Select(r => r with { Source = SourceName })
            .ToList();
}
