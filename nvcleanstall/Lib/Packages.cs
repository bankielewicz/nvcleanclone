using System.IO.Compression;
using System.Text;
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
        // GAP-S01 Sink 6: this client-supplied version becomes manifest.Version and is later
        // interpolated into path names (Jobs.cs default outDir, ZipPathFor). Validate it here,
        // at the relabel, before any path is derived — ReadManifest below only sees the (safe)
        // template version, so the relabel is the sole place this untrusted value enters.
        PackageValidation.ValidateVersion(version);
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
            // SEC-04: bound the archive (entry count, per-entry + total uncompressed size) before
            // reading anything out of it.
            PackageValidation.ValidateArchiveBounds(zip);
            var entry = zip.GetEntry("manifest.json")
                ?? throw new InvalidDataException("manifest.json not found in zip root");
            // Reject an oversized manifest by its declared length before reading it into memory.
            PackageValidation.ValidateManifestBytes(entry.Length);
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
        // SEC-04: bound the manifest text; SEC-01: the content validator neutralizes every
        // untrusted Version/Id/Payload before any path is derived from them.
        PackageValidation.ValidateManifestBytes(Encoding.UTF8.GetByteCount(json));
        var m = JsonSerializer.Deserialize<Manifest>(json, Json.Web)
            ?? throw new InvalidDataException("invalid manifest");
        if (string.IsNullOrEmpty(m.Version) || m.Components.Count == 0)
            throw new InvalidDataException("invalid manifest: version and non-empty components[] required");
        if (!m.Components.Any(c => c.Required))
            throw new InvalidDataException("invalid manifest: no required component present");
        PackageValidation.ValidateManifest(m);
        return m;
    }

    // GAP-S01 (SEC-04 + SEC-01): copy one selected payload from `source` into
    // `outDir/payload/<payloadFile>` with bounded, O(buffer) streaming — no whole-payload
    // byte[] — and confine both the destination and (folder source) the read under their
    // intended roots, rejecting reparse-point escapes. `payloadFile` is already a validated
    // leaf (ReadManifest); the confinement is defense in depth. Returns bytes copied.
    private static long CopyPayload(PackageSource source, string payloadFile, string outDir)
    {
        var destPath = PackageValidation.EnsureConfinedRealPath(outDir, Path.Combine("payload", payloadFile));
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var dest = File.Create(destPath);

        if (source.Kind == "local-zip")
        {
            using var zip = ZipFile.OpenRead(source.ZipPath!);
            var entry = zip.GetEntry($"payload/{payloadFile}")
                ?? throw new InvalidDataException(PackageValidation.PayloadMissing);
            using var src = entry.Open();
            return PackageValidation.BoundedCopy(src, dest, PackageValidation.MaxEntryBytes);
        }
        var srcPath = PackageValidation.EnsureConfinedRealPath(source.Dir!, Path.Combine("payload", payloadFile));
        if (!File.Exists(srcPath)) throw new InvalidDataException(PackageValidation.PayloadMissing);
        using var fsrc = File.OpenRead(srcPath);
        return PackageValidation.BoundedCopy(fsrc, dest, PackageValidation.MaxEntryBytes);
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
            // GAP-S01: bounded streaming copy + path confinement, replacing the unbounded
            // ReadPayload byte[] + WriteAllBytes (SEC-04 memory, SEC-01 write sink).
            CopyPayload(source, c.Payload, outDir);
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

    // HARD-02: the error a root outputPath fails with. Named, so tests and the UI can match it.
    public const string RootOutputPathError = "outputPath is a filesystem root; choose a folder";

    // HARD-02: is this path a filesystem root (its parent is null)? `outputPath` is user-supplied
    // and reaches WriteCustomized before PR #12's zip-step guard ever runs, so `C:/` would spray
    // payload/, manifest.json and install.cmd straight into the drive root.
    //
    // A pure predicate: the drive-root case is assertable without running a build against `C:\`
    // (the D12 AC-7 precedent). Trims a trailing separator first — Directory.GetParent returns
    // the path itself for "C:\dir\", and null for a root either way.
    public static bool IsFilesystemRoot(string path) =>
        Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))) is null;

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

            // Its own component payloads, by the payload filename the manifest records. GAP-S01
            // Sink 4: this prior on-disk manifest is untrusted (read raw, not through the
            // validator), so a payload name that is not a safe leaf — `..\..\x`, a rooted path —
            // is SKIPPED, never deleted. Skip, not throw: a malformed/hostile prior manifest must
            // still leave the directory untouched (the ForeignOrMalformed pin).
            if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
                foreach (var c in comps.EnumerateArray())
                    if (c.ValueKind == JsonValueKind.Object
                        && c.TryGetProperty("payload", out var pay)
                        && pay.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(pay.GetString())
                        && PackageValidation.IsSafeLeafPayload(pay.GetString()!))
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
