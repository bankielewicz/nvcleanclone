namespace CleanDriver.Lib;

/// <summary>
/// Single owner of catalog reads (GAP-01). Both the bundled mock catalog and the
/// live NVIDIA lookup service are interchangeable implementations behind this seam.
/// Each returned <see cref="Release"/> carries a <c>Source</c> marker
/// (<c>"live"</c> | <c>"mock"</c>) so callers/UI can tell which path produced it.
/// </summary>
public interface ICatalogProvider
{
    IReadOnlyList<Release> GetReleases(GpuInfo gpu);

    /// <summary>
    /// Releases plus an optional <see cref="CatalogDetail"/> explaining a mock/fallback
    /// state (HARD-06). The default reports no detail, so every existing implementer is
    /// unaffected; a provider that knows WHY it is serving mock overrides this.
    /// </summary>
    CatalogResult GetCatalog(GpuInfo gpu) => new(GetReleases(gpu), null);
}

/// <summary>Releases from a provider, with an optional reason a mock catalog is in play.</summary>
public sealed record CatalogResult(IReadOnlyList<Release> Releases, string? SourceDetail);

/// <summary>
/// The three reason buckets rendered by the mock-catalog marker (HARD-06). Exactly one
/// reaches <c>/api/catalog</c>'s <c>sourceDetail</c> when <c>source == "mock"</c>; the
/// detailed fallback strings keep flowing to the log unchanged.
/// </summary>
public static class CatalogDetail
{
    public const string MockMode = "mock mode";          // --mock-catalog / env chose mock deliberately
    public const string GpuNotMatched = "GPU not matched"; // live provider fell back: simulated / unresolved GPU
    public const string LiveLookupFailed = "live lookup failed"; // live provider fell back: no rows / transport error
}
