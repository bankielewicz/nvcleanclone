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
}
