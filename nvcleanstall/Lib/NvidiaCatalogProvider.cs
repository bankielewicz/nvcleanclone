using System.Globalization;
using System.Text.Json;

namespace CleanDriver.Lib;

/// <summary>
/// Live catalog provider backed by NVIDIA's manual-lookup JSON service. All HTTP goes
/// through a single injectable <see cref="HttpMessageHandler"/> (tests supply a fake
/// returning the recorded fixture) with a hard 5-second timeout. The GPU is mapped to
/// a product/series id via <see cref="GpuPfidMap"/>; the response rows nest under
/// <c>IDS[].downloadInfo</c> with <c>Version</c> and <c>DownloadURL</c>.
/// </summary>
public sealed class NvidiaCatalogProvider : ICatalogProvider
{
    // gfwsl.geforce.com manual-lookup endpoint (verified live in PF-2).
    private const string Endpoint =
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _http;
    private readonly GpuPfidMap _map;
    private readonly ICatalogProvider _fallback;
    private readonly Action<string> _log;

    public NvidiaCatalogProvider(HttpMessageHandler handler, GpuPfidMap? map = null,
        ICatalogProvider? fallback = null, Action<string>? log = null)
    {
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout };
        _map = map ?? GpuPfidMap.Bundled();
        _fallback = fallback ?? new MockCatalogProvider();
        _log = log ?? (_ => { });
    }

    // Any failure (unresolved GPU, timeout, non-200, empty/parse error) falls back to
    // the mock catalog and is logged — never thrown to the UI.
    public IReadOnlyList<Release> GetReleases(GpuInfo gpu)
    {
        if (gpu.IsSimulated)
            return Fallback(gpu, "GPU is simulated");
        if (!_map.TryResolve(gpu.Name, out var psid, out var pfid))
            return Fallback(gpu, $"GPU '{gpu.Name}' not in pfid table");

        try
        {
            var releases = Parse(Fetch(psid, pfid));
            return releases.Count > 0 ? releases : Fallback(gpu, "live lookup returned no rows");
        }
        catch (Exception ex)
        {
            return Fallback(gpu, $"live lookup failed: {ex.GetType().Name}");
        }
    }

    private IReadOnlyList<Release> Fallback(GpuInfo gpu, string reason)
    {
        _log($"NvidiaCatalogProvider: using mock catalog ({reason}).");
        return _fallback.GetReleases(gpu);
    }

    private string Fetch(int psid, int pfid)
    {
        var url = $"{Endpoint}?func=DriverManualLookup" +
                  $"&psid={psid}&pfid={pfid}&osID={_map.OsId}" +
                  $"&languageCode={_map.LanguageCode}&dch=1&numberOfResults=10";
        using var resp = _http.GetAsync(url).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        return resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    // Parses IDS[].downloadInfo rows, collapses the Game-Ready/Studio pair NVIDIA
    // returns per version down to the Game-Ready build, newest version first.
    private static IReadOnlyList<Release> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return Array.Empty<Release>();

        var rows = new List<(Release rel, bool gameReady)>();
        foreach (var entry in ids.EnumerateArray())
        {
            if (!entry.TryGetProperty("downloadInfo", out var info)) continue;
            var version = Str(info, "Version");
            var url = Str(info, "DownloadURL");
            if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url)) continue;

            bool isBeta = Str(info, "IsBeta") == "1";
            bool isCrd = Str(info, "IsCRD") == "1"; // Creator/Studio Ready Driver
            rows.Add((new Release
            {
                Version = version,
                Channel = isBeta ? "Beta" : "WHQL",
                ReleaseDate = NormalizeDate(Str(info, "ReleaseDateTime")),
                SizeMB = ParseSizeMB(Str(info, "DownloadURLFileSize")),
                DownloadUrl = url,
                Source = "live",
            }, gameReady: !isCrd));
        }

        return rows
            .GroupBy(x => x.rel.Version)
            .Select(g => g.OrderByDescending(x => x.gameReady).First().rel)
            .OrderByDescending(r => TryVersion(r.Version))
            .ToList();
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // "Tue Jul 07, 2026" -> "2026-07-07"; leaves the raw value if it can't be parsed.
    private static string NormalizeDate(string raw)
    {
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var dt))
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return raw;
    }

    // "979.17 MB" -> 979; 0 when absent/unparseable.
    private static int ParseSizeMB(string raw)
    {
        var token = raw.Split(' ').FirstOrDefault() ?? "";
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb)
            ? (int)Math.Round(mb) : 0;
    }

    private static Version TryVersion(string v) =>
        Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0);
}
