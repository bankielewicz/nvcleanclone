using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// GAP-S01 (SEC-01 path traversal, SEC-04 archive/memory exhaustion): one server-side
// validator confines and bounds every untrusted manifest/package input before any path is
// derived from it. These tests are the register's AC, table-driven at the validator and the
// load endpoints — each malicious shape is rejected with a stable, path-free error, and the
// six filesystem sinks (write, folder read, zip read, delete, zip-entry name, Version-in-path)
// are each exercised, not just the write.
public class PackageValidationTests
{
    // ---- builders ---------------------------------------------------------------
    private static ComponentDef Comp(string id, string payload, bool required = false,
        List<string>? dependsOn = null) =>
        new() { Id = id, Name = id, Required = required, Payload = payload, DependsOn = dependsOn ?? new() };

    private static Manifest ManifestOf(params ComponentDef[] comps) => new()
    {
        Version = "572.16",
        Channel = "WHQL",
        Components = comps.ToList(),
    };

    // A minimal manifest that PASSES validation; each table test mutates exactly one field.
    private static Manifest ValidManifest() =>
        ManifestOf(Comp("display-driver", "display-driver.bin", required: true));

    // ==== AC-1 table: malicious Payload shapes rejected at validation ==============
    // Every row is a single field carrying a traversal / rooted / device / separator /
    // overlong value. The validator must reject each with InvalidDataException.
    [Theory]
    [InlineData("../evil.bin")]                 // parent, forward slash
    [InlineData(@"..\evil.bin")]                // parent, back slash
    [InlineData("sub/evil.bin")]                // separator, forward
    [InlineData(@"sub\evil.bin")]               // separator, back
    [InlineData(@"C:\Windows\evil.bin")]        // rooted, drive
    [InlineData("/etc/passwd")]                 // rooted, posix
    [InlineData(@"\\server\share\evil.bin")]    // UNC
    [InlineData(@"\\?\C:\evil.bin")]            // device path
    [InlineData("..")]                          // bare parent
    [InlineData(".")]                           // bare current
    [InlineData("")]                            // empty
    [InlineData("a b*.bin")]                    // disallowed characters
    public void ValidateManifest_RejectsUnsafePayload(string payload)
    {
        var m = ManifestOf(Comp("display-driver", "display-driver.bin", required: true), Comp("evil", payload));
        var ex = Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateManifest(m));
        AssertPathFree(ex.Message);
    }

    [Fact]
    public void ValidateManifest_RejectsOverlongPayload()
    {
        var m = ManifestOf(Comp("display-driver", "display-driver.bin", required: true),
                           Comp("evil", new string('a', 300) + ".bin"));
        Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateManifest(m));
    }

    // ==== AC-1 table: malicious Version shapes (Sink 6 — Version reaches path names) =
    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("../../evil")]
    [InlineData(@"C:\evil")]
    [InlineData("/evil")]
    [InlineData("572/16")]
    [InlineData(@"572\16")]
    [InlineData("not-a-version")]
    [InlineData("")]
    [InlineData("572..16")]                     // empty numeric segment
    public void ValidateVersion_RejectsUnsafeVersion(string version)
    {
        var ex = Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateVersion(version));
        AssertPathFree(ex.Message);
    }

    [Theory]
    [InlineData("572.16")]
    [InlineData("566.36")]
    [InlineData("610.74")]
    [InlineData("999.10")]
    public void ValidateVersion_AcceptsCurrentFormat(string version)
    {
        PackageValidation.ValidateVersion(version);   // must not throw
    }

    // ==== AC-1: duplicate ids / payloads, case-insensitive ========================
    [Fact]
    public void ValidateManifest_RejectsDuplicateId_CaseInsensitive()
    {
        var m = ManifestOf(Comp("Display-Driver", "a.bin", required: true), Comp("display-driver", "b.bin"));
        Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateManifest(m));
    }

    [Fact]
    public void ValidateManifest_RejectsDuplicatePayload_CaseInsensitive()
    {
        var m = ManifestOf(Comp("display-driver", "Payload.BIN", required: true), Comp("other", "payload.bin"));
        Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateManifest(m));
    }

    // ==== AC-1: missing dependency target =========================================
    [Fact]
    public void ValidateManifest_RejectsMissingDependencyTarget()
    {
        var m = ManifestOf(Comp("display-driver", "display-driver.bin", required: true,
                                dependsOn: new() { "does-not-exist" }));
        Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateManifest(m));
    }

    // ==== AC-1: too many components ===============================================
    [Fact]
    public void ValidateManifest_RejectsMoreThanMaxComponents()
    {
        var comps = new List<ComponentDef> { Comp("display-driver", "display-driver.bin", required: true) };
        for (int i = 0; i < PackageValidation.MaxComponents; i++)   // one over the cap once display-driver is counted
            comps.Add(Comp($"c{i}", $"p{i}.bin"));
        var m = ManifestOf(comps.ToArray());
        Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateManifest(m));
    }

    [Fact]
    public void ValidateManifest_AcceptsTheValidManifest()
    {
        PackageValidation.ValidateManifest(ValidManifest());   // must not throw
    }

    // ==== AC-2: a malicious manifest loaded through the endpoint returns a path-free
    // error and never reaches the filesystem (folder load path). ====================
    [Fact]
    public void LoadLocal_FolderWithTraversalPayload_RejectsWithPathFreeError()
    {
        var root = TempDir.Create();
        var pkg = Path.Combine(root, "pkg");
        Directory.CreateDirectory(Path.Combine(pkg, "payload"));
        var sentinel = Path.Combine(root, "SENTINEL.txt");
        File.WriteAllText(sentinel, "do not touch");
        WriteManifest(Path.Combine(pkg, "manifest.json"), """
            { "version":"572.16","channel":"WHQL","components":[
              {"id":"display-driver","name":"d","required":true,"payload":"display-driver.bin"},
              {"id":"evil","name":"e","payload":"..\\..\\SENTINEL.txt"} ] }
            """);

        var ex = Assert.Throws<InvalidDataException>(() => Packages.LoadLocal(pkg));
        AssertPathFree(ex.Message);
        Assert.True(File.Exists(sentinel), "sentinel outside the package is untouched");
    }

    // The reproduced write-escape (plan's MaliciousPayload_EscapesOutDir), now asserting
    // rejection AT LOAD so it never reaches WriteCustomized.
    [Fact]
    public void ReproducedEscape_IsRejectedAtLoad_NotWritten()
    {
        var root = TempDir.Create();
        var pkg = Path.Combine(root, "src");
        Directory.CreateDirectory(Path.Combine(pkg, "payload"));
        File.WriteAllBytes(Path.Combine(root, "PWNED.bin"), new byte[] { 1, 2, 3 });
        WriteManifest(Path.Combine(pkg, "manifest.json"), """
            { "version":"999.99","channel":"WHQL","components":[
              {"id":"display-driver","name":"d","required":true,"payload":"display-driver.bin"},
              {"id":"evil","name":"evil","required":true,"payload":"..\\..\\PWNED.bin"} ] }
            """);

        Assert.Throws<InvalidDataException>(() => Packages.LoadLocal(pkg));
        // The escape target the original repro proved reachable is never produced.
        Assert.False(File.Exists(Path.Combine(root, "buildzone", "PWNED.bin")));
    }

    // ==== AC-2 / Sink 6: a client version that would escape the default outDir is
    // rejected at the relabel point, before any job/path is derived. ================
    [Theory]
    [InlineData(@"..\..\evil")]
    [InlineData("../../evil")]
    public void LoadSampleTemplate_UnsafeVersion_Rejected(string version)
    {
        Assert.Throws<InvalidDataException>(() => Packages.LoadSampleTemplate(version));
    }

    // ==== AC-1 / SEC-04: archive entry-count bound ================================
    [Fact]
    public void LoadLocal_ZipWithTooManyEntries_Rejected()
    {
        var root = TempDir.Create();
        var zipPath = Path.Combine(root, "pkg.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "manifest.json", ValidManifestJson());
            for (int i = 0; i <= PackageValidation.MaxArchiveEntries; i++)
                WriteZipEntry(zip, $"payload/f{i}.bin", "x");
        }
        var ex = Assert.Throws<InvalidDataException>(() => Packages.LoadLocal(zipPath));
        AssertPathFree(ex.Message);
    }

    // ==== AC-1 / SEC-04: manifest-size bound ======================================
    [Fact]
    public void ReadManifest_OversizedManifest_Rejected()
    {
        // A syntactically valid manifest padded past the size cap via a huge description.
        var big = new string('x', (int)PackageValidation.MaxManifestBytes + 1024);
        var json = "{\"version\":\"572.16\",\"channel\":\"WHQL\",\"components\":[" +
                   "{\"id\":\"display-driver\",\"name\":\"d\",\"required\":true,\"payload\":\"display-driver.bin\"," +
                   "\"description\":\"" + big + "\"}]}";
        var ex = Assert.Throws<InvalidDataException>(() => InvokeReadManifest(json));
        AssertPathFree(ex.Message);
    }

    // ==== AC-1 / SEC-04: per-entry and total uncompressed caps (injected, since 2/4
    // GiB cannot be materialized). Uses the settable-static idiom (Jobs.StallTimeout). =
    [Fact]
    public void LoadLocal_ZipEntryOverPerEntryCap_Rejected()
    {
        WithCaps(entry: 8, total: 1 << 20, () =>
        {
            var zipPath = MakeZip(("manifest.json", ValidManifestJson()),
                                  ("payload/display-driver.bin", new string('A', 64)));   // 64 B > 8 B cap
            var ex = Assert.Throws<InvalidDataException>(() => Packages.LoadLocal(zipPath));
            AssertPathFree(ex.Message);
        });
    }

    [Fact]
    public void LoadLocal_ZipTotalOverTotalCap_Rejected()
    {
        WithCaps(entry: 1 << 20, total: 32, () =>
        {
            var zipPath = MakeZip(("manifest.json", ValidManifestJson()),
                                  ("payload/display-driver.bin", new string('A', 20)),
                                  ("payload/extra.bin", new string('B', 20)));   // 40 B total > 32 B cap
            var ex = Assert.Throws<InvalidDataException>(() => Packages.LoadLocal(zipPath));
            AssertPathFree(ex.Message);
        });
    }

    // ==== Sink 4: CleanPreviousBuild must NOT delete via a malicious prior manifest,
    // and must NOT throw (preserves the ForeignOrMalformed pin — it skips, not rejects). =
    [Fact]
    public void CleanPreviousBuild_MaliciousPriorManifestPayload_DeletesNothingOutside()
    {
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(Path.Combine(outDir, "payload"));
        var sentinel = Path.Combine(root, "SENTINEL.txt");
        File.WriteAllText(sentinel, "do not touch");
        // A CleanDriver-stamped manifest whose component payload traverses out of outDir.
        WriteManifest(Path.Combine(outDir, "manifest.json"), """
            { "customizedBy":"CleanDriver","components":[ {"payload":"..\\..\\SENTINEL.txt"} ] }
            """);

        var removed = Packages.CleanPreviousBuild(outDir);   // must not throw

        Assert.True(File.Exists(sentinel), "the traversal payload name did not delete the outside sentinel");
        Assert.DoesNotContain(removed, r => r.Contains("SENTINEL"));
    }

    // ==== AC-3: a valid folder AND a valid zip stream the SAME selected bytes as the
    // input, for a payload several times the streaming buffer — memory is O(buffer) by
    // construction (chunked copy), not O(payload). Characterization + streaming pin. ==
    [Theory]
    [InlineData(false)]   // folder source
    [InlineData(true)]    // zip source
    public async Task ValidPackage_StreamsSelectedBytes_Unchanged(bool zip)
    {
        var root = TempDir.Create();
        var payload = RandomBytes(5 * 1024 * 1024 + 7);   // ~5 MiB, not a buffer multiple
        var manifestJson = ValidManifestJson();

        PackageSource source;
        if (zip)
        {
            var zipPath = Path.Combine(root, "pkg.zip");
            using (var fs = File.Create(zipPath))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteZipEntry(z, "manifest.json", manifestJson);
                using var e = z.CreateEntry("payload/display-driver.bin").Open();
                e.Write(payload);
            }
            source = Packages.LoadLocal(zipPath).source;
        }
        else
        {
            var pkg = Path.Combine(root, "pkg");
            Directory.CreateDirectory(Path.Combine(pkg, "payload"));
            WriteManifest(Path.Combine(pkg, "manifest.json"), manifestJson);
            File.WriteAllBytes(Path.Combine(pkg, "payload", "display-driver.bin"), payload);
            source = Packages.LoadLocal(pkg).source;
        }

        var (manifest, _) = zip ? Packages.LoadLocal(Path.Combine(root, "pkg.zip"))
                                : Packages.LoadLocal(Path.Combine(root, "pkg"));
        var outDir = Path.Combine(root, "out");
        Packages.WriteCustomized(source, manifest, new List<string> { "display-driver" }, outDir);

        var written = await File.ReadAllBytesAsync(Path.Combine(outDir, "payload", "display-driver.bin"));
        Assert.Equal(payload, written);
    }

    // ==== AC-4: exported ZIP entry names are normalized relative paths — no absolute
    // or parent segment (proven by the leaf-validated payloads flowing to entry names). =
    [Fact]
    public async Task ExportedZipEntries_AreNormalizedRelativePaths()
    {
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");
        var zipPath = Path.Combine(root, "572.16-cleandriver-package.zip");
        var (manifest, src) = Packages.LoadCatalog("572.16");
        var sel = new Selection { Components = new() { "display-driver", "physx" } };

        var job = Jobs.StartExecute("package", manifest, src, sel, outDir, Gpu.Simulated);
        await job.Completion!;
        Assert.Equal("done", job.Status);

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var e in zip.Entries)
        {
            Assert.DoesNotContain("..", e.FullName);
            Assert.False(Path.IsPathRooted(e.FullName), $"entry rooted: {e.FullName}");
            Assert.DoesNotContain('\\', e.FullName);   // archive-relative, forward slash only
        }
    }

    // ==== reparse-point escape: a source whose payload/ directory is a junction to an
    // outside location is rejected in source traversal. Junctions need no privilege on
    // NTFS (unlike symlinks), verified at prompt time. Skips cleanly off NTFS. =========
    [Fact]
    public void CopyPayload_SourcePayloadDirIsReparsePoint_Rejected()
    {
        var root = TempDir.Create();
        var outsideTarget = Path.Combine(root, "outside");
        Directory.CreateDirectory(outsideTarget);
        File.WriteAllBytes(Path.Combine(outsideTarget, "display-driver.bin"), new byte[] { 9, 9, 9 });

        var pkg = Path.Combine(root, "pkg");
        Directory.CreateDirectory(pkg);
        WriteManifest(Path.Combine(pkg, "manifest.json"), ValidManifestJson());
        // Make pkg/payload a junction to the outside directory.
        if (!TryMakeJunction(Path.Combine(pkg, "payload"), outsideTarget))
            return;   // environment can't create a junction (non-NTFS) — nothing to assert

        var (manifest, source) = Packages.LoadLocal(pkg);
        var outDir = Path.Combine(root, "out");
        Assert.Throws<InvalidDataException>(() =>
            Packages.WriteCustomized(source, manifest, new List<string> { "display-driver" }, outDir));
    }

    // ---- helpers ----------------------------------------------------------------
    private static string ValidManifestJson() =>
        "{\"version\":\"572.16\",\"channel\":\"WHQL\",\"components\":[" +
        "{\"id\":\"display-driver\",\"name\":\"d\",\"required\":true,\"payload\":\"display-driver.bin\"}]}";

    private static void WriteManifest(string path, string json) => File.WriteAllText(path, json);

    private static void AssertPathFree(string message)
    {
        // No absolute filesystem path (drive-letter or UNC or posix-root) leaks to the client.
        Assert.DoesNotContain(":\\", message);
        Assert.DoesNotContain(":/", message);
        Assert.DoesNotContain("\\\\", message);
        Assert.False(message.Contains("/home/") || message.Contains("/tmp/") || message.Contains("/Users/"),
            $"message leaked a path: {message}");
    }

    private static byte[] RandomBytes(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)((i * 31 + 7) & 0xFF);   // deterministic, no Random
        return b;
    }

    private static void WriteZipEntry(ZipArchive zip, string name, string content)
    {
        using var s = zip.CreateEntry(name).Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        s.Write(bytes, 0, bytes.Length);
    }

    private static string MakeZip(params (string name, string content)[] entries)
    {
        var zipPath = Path.Combine(TempDir.Create(), "pkg.zip");
        using var fs = File.Create(zipPath);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries) WriteZipEntry(zip, name, content);
        return zipPath;
    }

    // ReadManifest is private; exercise it through the folder-load path.
    private static void InvokeReadManifest(string json)
    {
        var pkg = Path.Combine(TempDir.Create(), "pkg");
        Directory.CreateDirectory(pkg);
        File.WriteAllText(Path.Combine(pkg, "manifest.json"), json);
        Packages.LoadLocal(pkg);
    }

    private static void WithCaps(long entry, long total, Action body)
    {
        var pe = PackageValidation.MaxEntryBytes;
        var pt = PackageValidation.MaxTotalUncompressedBytes;
        PackageValidation.MaxEntryBytes = entry;
        PackageValidation.MaxTotalUncompressedBytes = total;
        try { body(); }
        finally
        {
            PackageValidation.MaxEntryBytes = pe;
            PackageValidation.MaxTotalUncompressedBytes = pt;
        }
    }

    private static bool TryMakeJunction(string link, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("mklink");
            psi.ArgumentList.Add("/J");
            psi.ArgumentList.Add(link);
            psi.ArgumentList.Add(target);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }
}
