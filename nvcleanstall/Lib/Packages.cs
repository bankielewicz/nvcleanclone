using System.IO.Compression;
using System.Text.Json;

namespace CleanDriver.Lib;

public static class Packages
{
    public static (Manifest manifest, PackageSource source) LoadCatalog(string version)
    {
        var dir = Catalog.PackageDir(version);
        var manifest = ReadManifest(File.ReadAllText(Path.Combine(dir, "manifest.json")));
        return (manifest, new PackageSource { Kind = "catalog", Dir = dir });
    }

    public static bool HasCatalogPackage(string version) =>
        Directory.Exists(Catalog.PackageDir(version));

    // GAP-02: a live-downloaded version has no real package (real parsing is
    // GAP-OUT-1). Serve the newest mock package as a representative sample, relabeled
    // to the requested version and flagged SampleComponents. PackageSource stays under
    // data/packages/ so extract/build read sample payloads — never the downloaded .exe.
    public static (Manifest manifest, PackageSource source) LoadSampleTemplate(string version)
    {
        var templateVersion = Catalog.Releases().First().Version; // newest mock package
        var dir = Catalog.PackageDir(templateVersion);
        var manifest = ReadManifest(File.ReadAllText(Path.Combine(dir, "manifest.json")))
            with { Version = version, SampleComponents = true };
        return (manifest, new PackageSource { Kind = "catalog", Dir = dir });
    }

    // A local package is either a directory containing manifest.json + payload/,
    // or a .zip with the same layout at its root.
    public static (Manifest manifest, PackageSource source) LoadLocal(string localPath)
    {
        if (Directory.Exists(localPath))
        {
            var mf = Path.Combine(localPath, "manifest.json");
            if (!File.Exists(mf)) throw new InvalidDataException("manifest.json not found in package folder");
            var manifest = ReadManifest(File.ReadAllText(mf));
            return (manifest, new PackageSource { Kind = "local-dir", Dir = localPath });
        }
        if (File.Exists(localPath) && localPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(localPath);
            var entry = zip.GetEntry("manifest.json")
                ?? throw new InvalidDataException("manifest.json not found in zip root");
            using var reader = new StreamReader(entry.Open());
            var manifest = ReadManifest(reader.ReadToEnd());
            return (manifest, new PackageSource { Kind = "local-zip", ZipPath = localPath });
        }
        throw new InvalidDataException("expected a package folder or .zip file");
    }

    private static Manifest ReadManifest(string json)
    {
        var m = JsonSerializer.Deserialize<Manifest>(json, Json.Web)
            ?? throw new InvalidDataException("invalid manifest");
        if (string.IsNullOrEmpty(m.Version) || m.Components.Count == 0)
            throw new InvalidDataException("invalid manifest: version and non-empty components[] required");
        if (!m.Components.Any(c => c.Required))
            throw new InvalidDataException("invalid manifest: no required component present");
        return m;
    }

    public static byte[]? ReadPayload(PackageSource source, string payloadFile)
    {
        if (source.Kind == "local-zip")
        {
            using var zip = ZipFile.OpenRead(source.ZipPath!);
            var entry = zip.GetEntry($"payload/{payloadFile}");
            if (entry == null) return null;
            using var ms = new MemoryStream();
            entry.Open().CopyTo(ms);
            return ms.ToArray();
        }
        var p = Path.Combine(source.Dir!, "payload", payloadFile);
        return File.Exists(p) ? File.ReadAllBytes(p) : null;
    }

    // Writes the customized package: only selected components' payloads plus a
    // rewritten manifest. Used by both extract-only and build-package.
    public static (List<string> written, bool modified, string manifestPath) WriteCustomized(
        PackageSource source, Manifest manifest, List<string> selectedIds, string outDir,
        Dictionary<string, JsonElement>? tweaks = null)
    {
        var selected = manifest.Components.Where(c => selectedIds.Contains(c.Id)).ToList();
        bool modified = selected.Count != manifest.Components.Count;
        Directory.CreateDirectory(Path.Combine(outDir, "payload"));

        var written = new List<string>();
        foreach (var c in selected)
        {
            var data = ReadPayload(source, c.Payload)
                ?? throw new InvalidDataException($"payload missing for component {c.Id}");
            File.WriteAllBytes(Path.Combine(outDir, "payload", c.Payload), data);
            written.Add(c.Id);
        }

        var outManifest = new
        {
            version = manifest.Version,
            channel = manifest.Channel,
            customizedBy = "CleanDriver",
            customizedAt = DateTime.UtcNow.ToString("o"),
            signature = modified ? "rebuilt" : "stock",
            // GAP-05 honesty marker: additive + null-omitted (WhenWritingNull), so stock
            // manifests stay byte-identical and `signature` consumers are unaffected — the
            // "rebuilt" signature is simulated, never a real re-sign.
            signatureSimulated = modified ? (bool?)true : null,
            components = selected,
            tweaks,
        };
        var manifestPath = Path.Combine(outDir, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(outManifest, Json.Web));
        return (written, modified, manifestPath);
    }
}
