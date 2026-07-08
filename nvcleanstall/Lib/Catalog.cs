using System.Text.Json;

namespace CleanDriver.Lib;

public static class Catalog
{
    public static string DataDir => Path.Combine(AppContext.BaseDirectory, "data");

    public static List<Release> Releases()
    {
        var raw = File.ReadAllText(Path.Combine(DataDir, "catalog.json"));
        var doc = JsonSerializer.Deserialize<CatalogFile>(raw, Json.Web)!;
        return doc.Releases
            .OrderByDescending(r => Version.Parse(r.Version))
            .ToList();
    }

    public static Release? Find(string version) =>
        Releases().FirstOrDefault(r => r.Version == version);

    public static string PackageDir(string version) =>
        Path.Combine(DataDir, "packages", version);

    private record CatalogFile
    {
        public List<Release> Releases { get; init; } = new();
    }
}
