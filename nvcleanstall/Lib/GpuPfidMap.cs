using System.Text.Json;

namespace CleanDriver.Lib;

/// <summary>
/// Static GPU-name -> NVIDIA manual-lookup id table (bundled JSON resource,
/// <c>data/nvidia-pfid-map.json</c>). Covers the RTX 40/50-series desktop parts.
/// No free-form scraping: a name that isn't in the table is simply unresolved,
/// which makes <see cref="NvidiaCatalogProvider"/> fall back to the mock catalog.
/// </summary>
public sealed class GpuPfidMap
{
    public int OsId { get; }
    public int LanguageCode { get; }
    private readonly Dictionary<string, (int Psid, int Pfid)> _byName;

    private GpuPfidMap(int osId, int languageCode, Dictionary<string, (int, int)> byName)
    {
        OsId = osId;
        LanguageCode = languageCode;
        _byName = byName;
    }

    public bool TryResolve(string? gpuName, out int psid, out int pfid)
    {
        psid = pfid = 0;
        if (string.IsNullOrWhiteSpace(gpuName)) return false;
        if (!_byName.TryGetValue(gpuName.Trim(), out var ids)) return false;
        (psid, pfid) = ids;
        return true;
    }

    private static GpuPfidMap? _bundled;

    public static GpuPfidMap Bundled() => _bundled ??=
        Load(Path.Combine(AppContext.BaseDirectory, "data", "nvidia-pfid-map.json"));

    public static GpuPfidMap Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        int osId = root.TryGetProperty("osId", out var o) ? o.GetInt32() : 135;
        int lang = root.TryGetProperty("languageCode", out var l) ? l.GetInt32() : 1033;

        var map = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("products", out var products))
        {
            foreach (var p in products.EnumerateArray())
            {
                var name = p.GetProperty("name").GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;
                map[name.Trim()] = (p.GetProperty("psid").GetInt32(), p.GetProperty("pfid").GetInt32());
            }
        }
        return new GpuPfidMap(osId, lang, map);
    }
}
