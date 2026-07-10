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

    // GAP-05: is a boolean tweak set true in the raw selection dict? (WriteCustomized
    // receives tweaks as JsonElements, not a Selection.)
    private static bool TweakTrue(Dictionary<string, JsonElement>? tweaks, string id) =>
        tweaks != null && tweaks.TryGetValue(id, out var v) && v.ValueKind == JsonValueKind.True;

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
            // GAP-05 honesty markers: additive + null-omitted (WhenWritingNull), so stock
            // manifests stay byte-identical and `signature` consumers are unaffected — the
            // "rebuilt" signature and the driver-telemetry patch are simulated, never real.
            signatureSimulated = modified ? (bool?)true : null,
            driverTelemetrySimulated = TweakTrue(tweaks, "driver-telemetry") ? (bool?)true : null,
            components = selected,
            tweaks,
        };
        var manifestPath = Path.Combine(outDir, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(outManifest, Json.Web));
        return (written, modified, manifestPath);
    }

    // HARD-01: remove exactly the artifacts a PRIOR CleanDriver build declared it wrote, then
    // let the caller write fresh. "Undo what that manifest says was written", never "empty the
    // directory": outDir is user-supplied, so foreign files must always survive.
    //
    // HARD BOUNDARY: deletion is enumerable-by-name only — File.Delete on a named file, nothing
    // else. No recursive whole-tree removal, ever: outDir is user-supplied and a recursive
    // directory delete could destroy a user's files (owner ruling, D12 round, reaffirmed in pin
    // D1). OutDirHygieneTests scans this file and asserts the primitive is absent — including
    // from comments, which is why this one names no API.
    //
    // Returns the archive-relative names removed, in a stable order; empty when nothing was
    // cleaned. A directory with no manifest.json, a manifest that is not ours, or a manifest
    // that is malformed JSON, is left entirely alone — and never throws.
    public static List<string> CleanPreviousBuild(string outDir)
    {
        var removed = new List<string>();
        var manifestPath = Path.Combine(outDir, "manifest.json");
        if (!File.Exists(manifestPath)) return removed;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(File.ReadAllText(manifestPath)); }
        catch (JsonException) { return removed; }   // not parseable → not ours → touch nothing
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return removed;
            // The stamp WriteCustomized leaves. Absent or different → someone else's directory.
            if (!root.TryGetProperty("customizedBy", out var by)
                || by.ValueKind != JsonValueKind.String
                || by.GetString() != "CleanDriver") return removed;

            // Its own component payloads, by the payload filename the manifest records.
            if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                foreach (var c in comps.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Object
                        && c.TryGetProperty("payload", out var pay)
                        && pay.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(pay.GetString()))
                        Remove(Path.Combine(outDir, "payload", pay.GetString()!), $"payload/{pay.GetString()}");

            // The .reg set is exactly recoverable: the manifest records the same tweak dict the
            // writer consulted, and a snippet exists iff the tweak is reg-capable and was true.
            if (root.TryGetProperty("tweaks", out var tweaks) && tweaks.ValueKind == JsonValueKind.Object)
                foreach (var t in Tweaks.All.Where(t => t.Reg))
                    if (tweaks.TryGetProperty(t.Id, out var v) && v.ValueKind == JsonValueKind.True)
                        Remove(Path.Combine(outDir, $"tweak-{t.Id}.reg"), $"tweak-{t.Id}.reg");
        }

        // The build's own fixed-name artifacts. install.cmd/config.json exist only for the
        // `package` action, but an `extract` into a prior package build must clear them too.
        foreach (var name in new[] { "install.cmd", "config.json", "manifest.json" })
            Remove(Path.Combine(outDir, name), name);

        return removed;

        void Remove(string abs, string display)
        {
            if (!File.Exists(abs)) return;
            File.Delete(abs);          // a single named file — never a directory, never recursive
            removed.Add(display);
        }
    }

    // GAP-06 / D12-F2: where the archive goes — a SIBLING of outDir (owner Ruling 2).
    //
    // Directory.GetParent on a path with a trailing separator returns that path itself, and
    // outputPath arrives unnormalized from the request body. The archive was therefore
    // written *inside* the directory it archives, failing mid-walk and leaving a 22-byte
    // orphan behind. Trim the separator before taking the parent, and refuse a drive root
    // by name rather than dereferencing null (which threw NullReferenceException).
    //
    // A pure function, so the drive-root case is assertable without ever running a build
    // into `C:\` — AC-7 offers "the path-computation level" precisely for that reason.
    public static string ZipPathFor(string outDir, string version)
    {
        var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
        var parent = Directory.GetParent(baseDir)
            ?? throw new InvalidOperationException($"outputPath has no parent directory: {outDir}");
        return Path.Combine(parent.FullName, $"{version}-cleandriver-package.zip");
    }

    // GAP-06: packs one redistributable archive from THIS build's explicit output list.
    //
    // D12-F1: it used to walk outDir with CreateFromDirectory. Nothing ever cleans that
    // directory, so rebuilding a version with a smaller selection archived the previous
    // build's leftovers — payloads the archive's own manifest disowned — and reported
    // `done`. The caller now names every entry, so the archive can only contain what this
    // run produced. Nothing on disk is deleted: outDir is user-supplied, and a recursive
    // clean there could destroy a user's files.
    //
    // Lives here rather than in Jobs.cs (the register's original seam) because the
    // no-execution guard forbids the compression type's name in that file; zipping
    // CleanDriver's own output belongs to the package writer anyway.
    public static string WriteZip(string zipPath, IEnumerable<(string abs, string entry)> files)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);   // re-runs overwrite cleanly
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (abs, entry) in files)
            zip.CreateEntryFromFile(abs, entry);
        return zipPath;
    }
}
