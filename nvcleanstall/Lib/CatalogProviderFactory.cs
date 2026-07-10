namespace CleanDriver.Lib;

/// <summary>
/// Chooses and shapes the catalog provider. Live (NVIDIA) by default; mock when
/// <c>--mock-catalog</c> is passed or <c>CLEANDRIVER_MOCK_CATALOG=1</c> is set, so
/// verification and offline runs are deterministic.
/// </summary>
public static class CatalogProviderFactory
{
    public const string Flag = "--mock-catalog";
    public const string EnvVar = "CLEANDRIVER_MOCK_CATALOG";

    public static bool UseMock(IEnumerable<string> args, Func<string, string?> env) =>
        args.Contains(Flag) || string.Equals(env(EnvVar), "1", StringComparison.Ordinal);

    public static ICatalogProvider Create(IEnumerable<string> args, Func<string, string?> env,
        Action<string>? log = null) =>
        UseMock(args, env)
            ? new DeliberateMockCatalogProvider()
            : new NvidiaCatalogProvider(new HttpClientHandler(), log: log);
}

/// <summary>
/// Composition-level marker (HARD-06): mock mode was chosen deliberately
/// (<c>--mock-catalog</c> / env), so <c>/api/catalog</c> reads "(mock mode)" rather than
/// a fallback reason. Only <see cref="CatalogProviderFactory.Create"/> constructs this;
/// a directly-constructed <see cref="MockCatalogProvider"/> — as tests and the live
/// provider's fallback build it — never claims deliberateness.
/// </summary>
public sealed class DeliberateMockCatalogProvider : ICatalogProvider
{
    private readonly MockCatalogProvider _inner = new();

    public IReadOnlyList<Release> GetReleases(GpuInfo gpu) => _inner.GetReleases(gpu);

    public CatalogResult GetCatalog(GpuInfo gpu) =>
        new(_inner.GetReleases(gpu), CatalogDetail.MockMode);
}

/// <summary>Shapes the /api/catalog response: provider releases plus a derived source.</summary>
public static class CatalogEndpoint
{
    public sealed record Response(IReadOnlyList<Release> Releases, string Source, string? SourceDetail = null);

    public static Response Build(ICatalogProvider provider, GpuInfo gpu)
    {
        var result = provider.GetCatalog(gpu);
        var releases = result.Releases;
        var source = releases.Count > 0
            ? releases[0].Source ?? MockCatalogProvider.SourceName
            : MockCatalogProvider.SourceName;
        // sourceDetail is a mock-only marker; WhenWritingNull omits it for live.
        var sourceDetail = source == MockCatalogProvider.SourceName ? result.SourceDetail : null;
        return new Response(releases, source, sourceDetail);
    }
}
