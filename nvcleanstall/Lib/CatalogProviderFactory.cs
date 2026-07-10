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
            ? new MockCatalogProvider()
            : new NvidiaCatalogProvider(new HttpClientHandler(), log: log);
}

/// <summary>Shapes the /api/catalog response: provider releases plus a derived source.</summary>
public static class CatalogEndpoint
{
    public sealed record Response(IReadOnlyList<Release> Releases, string Source, string? SourceDetail = null);

    public static Response Build(ICatalogProvider provider, GpuInfo gpu)
    {
        var releases = provider.GetReleases(gpu);
        var source = releases.Count > 0
            ? releases[0].Source ?? MockCatalogProvider.SourceName
            : MockCatalogProvider.SourceName;
        return new Response(releases, source);
    }
}
