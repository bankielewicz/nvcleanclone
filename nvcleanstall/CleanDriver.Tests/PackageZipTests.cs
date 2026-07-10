using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// GAP-06 — true single-EXE package output. The `package` action keeps writing its output
// directory for inspection and additionally emits ONE redistributable archive beside it:
// <parent-of-outDir>/<version>-cleandriver-package.zip. The bundled install.cmd stays the
// simulated, non-executing installer; nothing is executed, extracted, or installed here.
//
// outDir is nested inside a unique temp dir so the sibling zip lands there too, keeping
// the archive hermetic rather than in shared %TEMP%.
public class PackageZipTests
{
    private static Selection Sel(IEnumerable<string> comps, params string[] onTweaks)
    {
        var tweaks = new Dictionary<string, JsonElement>();
        foreach (var t in onTweaks) tweaks[t] = JsonDocument.Parse("true").RootElement.Clone();
        return new Selection { Components = comps.ToList(), Tweaks = tweaks };
    }

    private static GpuInfo G() => new() { Name = "GeForce RTX 5070", InstalledDriverVersion = "591.86" };

    // A strict subset of components (usbc, gfe, telemetry, … deselected) plus a
    // registry-affecting tweak (msi-mode), so both the "only selected payloads" and the
    // ".reg snippet is archived" assertions are exercised rather than vacuous.
    private static readonly string[] Selected = { "display-driver", "physx", "hd-audio" };

    private static (string outDir, string zipPath) Paths(string version)
    {
        var outDir = Path.Combine(TempDir.Create(), $"package-{version}");
        var zipPath = Path.Combine(
            Directory.GetParent(outDir)!.FullName, $"{version}-cleandriver-package.zip");
        return (outDir, zipPath);
    }

    private static async Task<(string outDir, string zipPath)> RunPackage(string version)
    {
        var (manifest, source) = Packages.LoadCatalog(version);
        var (outDir, zipPath) = Paths(version);
        var job = Jobs.StartExecute("package", manifest, source,
            Sel(Selected, "msi-mode"), outDir, G());
        await job.Completion!;
        Assert.Equal("done", job.Status);
        return (outDir, zipPath);
    }

    // The archive exists, as a sibling of the directory output — never inside it (which
    // would make the archive try to contain itself).
    [Fact]
    public async Task PackageAction_WritesSingleZip_BesideTheOutputDirectory()
    {
        var (outDir, zipPath) = await RunPackage("572.16");

        Assert.True(File.Exists(zipPath), $"expected archive at {zipPath}");
        Assert.True(Directory.Exists(outDir), "the directory output is kept for inspection");
        Assert.False(File.Exists(Path.Combine(outDir, Path.GetFileName(zipPath))),
            "the archive must not be written inside the directory it archives");
    }

    // The register's AC, literally: the entry set is manifest.json + payload/<selected>.bin
    // for each selected component + install.cmd + config.json + the .reg snippet.
    [Fact]
    public async Task PackageZip_ContainsExactlyTheExpectedEntries()
    {
        var (manifest, _) = Packages.LoadCatalog("572.16");
        var (_, zipPath) = await RunPackage("572.16");

        var expected = new HashSet<string>(new[] { "manifest.json", "install.cmd", "config.json", "tweak-msi-mode.reg" });
        foreach (var c in manifest.Components.Where(c => Selected.Contains(c.Id)))
            expected.Add($"payload/{c.Payload}");

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(expected, zip.Entries.Select(e => e.FullName).ToHashSet());
    }

    // AC-002 discipline: a deselected component's payload is absent from the archive.
    [Fact]
    public async Task PackageZip_OmitsDeselectedComponentPayloads()
    {
        var (manifest, _) = Packages.LoadCatalog("572.16");
        var (_, zipPath) = await RunPackage("572.16");

        var deselected = manifest.Components.Where(c => !Selected.Contains(c.Id)).ToList();
        Assert.NotEmpty(deselected);

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName).ToHashSet();
        foreach (var c in deselected)
            Assert.DoesNotContain($"payload/{c.Payload}", entries);
    }

    // The AC's own wording — "matching the on-disk directory exactly". Compared as sets
    // (GetFiles order is unspecified) after normalizing the on-disk paths to the zip's
    // relative, forward-slash form.
    [Fact]
    public async Task PackageZip_MatchesTheOnDiskDirectoryExactly()
    {
        var (outDir, zipPath) = await RunPackage("572.16");

        var onDisk = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(outDir, p).Replace('\\', '/'))
            .ToHashSet();

        using var zip = ZipFile.OpenRead(zipPath);
        Assert.Equal(onDisk, zip.Entries.Select(e => e.FullName).ToHashSet());
    }

    // Building the same package twice must overwrite the archive cleanly rather than fail:
    // the underlying directory-to-archive call refuses to write over an existing file, so
    // WriteZip deletes first. Without that delete the second run ends in status "error".
    [Fact]
    public async Task PackageAction_RunTwiceIntoSameOutDir_OverwritesTheArchive()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var (outDir, zipPath) = Paths("572.16");

        var first = Jobs.StartExecute("package", manifest, source, Sel(Selected, "msi-mode"), outDir, G());
        await first.Completion!;
        Assert.Equal("done", first.Status);

        var second = Jobs.StartExecute("package", manifest, source, Sel(Selected, "msi-mode"), outDir, G());
        await second.Completion!;

        Assert.Null(second.Error);
        Assert.Equal("done", second.Status);
        using var zip = ZipFile.OpenRead(zipPath);   // a readable archive, not a corrupt/half-written one
        Assert.Contains("manifest.json", zip.Entries.Select(e => e.FullName));
    }

    // ---- D12-F1 -----------------------------------------------------------------
    // The archive is built from THIS build's output list, not from a walk of outDir.
    // These tests assert against LITERAL expected paths: the original suite recomputed
    // production's own `Directory.GetParent(outDir)!` expression, so it could never
    // falsify it, and it built into a fresh temp dir, so outDir was never dirty. Both
    // bugs lived exactly in that gap.

    // AC-1 — rebuilding into a NON-EMPTY outDir with a strictly smaller selection must not
    // archive the previous build's payloads, nor a .reg for a tweak now switched off.
    [Fact]
    public async Task PackageZip_RebuildWithSmallerSelection_ExcludesPreviousBuildsLeftovers()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");
        var zipPath = Path.Combine(root, "572.16-cleandriver-package.zip");   // literal, not re-derived

        var first = Jobs.StartExecute("package", manifest, source,
            Sel(new[] { "display-driver", "physx", "hd-audio" }, "msi-mode"), outDir, G());
        await first.Completion!;
        Assert.Equal("done", first.Status);

        // Same outDir, smaller selection, msi-mode OFF. The leftovers are still on disk.
        var second = Jobs.StartExecute("package", manifest, source,
            Sel(new[] { "display-driver" }), outDir, G());
        await second.Completion!;
        Assert.Equal("done", second.Status);

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.Contains("payload/display-driver.bin", entries);
        Assert.DoesNotContain("payload/physx.bin", entries);      // deselected in run 2
        Assert.DoesNotContain("payload/hd-audio.bin", entries);   // deselected in run 2
        Assert.DoesNotContain("tweak-msi-mode.reg", entries);     // tweak switched off in run 2

        // HARD-01 (register pin D1) — the deferred half of this defect, filed as PR #12's
        // optional cleanup #3 and now implemented: the DIRECTORY is cleaned too, manifest-scoped.
        // This assertion previously read `Assert.True(... "the directory output is never
        // cleaned")`, characterizing the then-deferred behavior. See OutDirHygieneTests.
        Assert.False(File.Exists(Path.Combine(outDir, "payload", "physx.bin")),
            "the deselected payload is removed from the directory by the manifest-scoped clean");
    }

    // AC-2 — unrelated files sitting in outDir (including a stale archive from an earlier
    // failed run) are never swallowed into the redistributable.
    [Fact]
    public async Task PackageZip_StrayFilesInOutDir_AreNotArchived()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");
        var zipPath = Path.Combine(root, "572.16-cleandriver-package.zip");

        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "leftover.txt"), "not mine");
        File.WriteAllText(Path.Combine(outDir, "572.16-cleandriver-package.zip"), "stale orphan");

        var job = Jobs.StartExecute("package", manifest, source, Sel(Selected, "msi-mode"), outDir, G());
        await job.Completion!;
        Assert.Equal("done", job.Status);

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Select(e => e.FullName).ToHashSet();
        Assert.DoesNotContain("leftover.txt", entries);
        Assert.DoesNotContain("572.16-cleandriver-package.zip", entries);   // F1b: no nested corpse
        Assert.Equal(
            new HashSet<string> { "manifest.json", "install.cmd", "config.json", "tweak-msi-mode.reg",
                                  "payload/display-driver.bin", "payload/physx.bin", "payload/hd-audio.bin" },
            entries);
    }

    // ---- D12-F2 -----------------------------------------------------------------
    // Owner Ruling 2: the archive is a sibling of outDir, never inside it. Directory.GetParent
    // on a path with a trailing separator returns that path itself, so an outputPath ending in
    // a separator wrote the archive into the directory it was archiving.

    // AC-5 — an outputPath with a trailing separator still succeeds, and the archive lands
    // BESIDE outDir. Before the fix: status=failed, "used by another process".
    [Fact]
    public async Task PackageAction_OutputPathWithTrailingSeparator_WritesArchiveBesideOutDir()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");
        var zipPath = Path.Combine(root, "572.16-cleandriver-package.zip");   // literal

        var job = Jobs.StartExecute("package", manifest, source,
            Sel(Selected, "msi-mode"), outDir + Path.DirectorySeparatorChar, G());
        await job.Completion!;

        Assert.Null(job.Error);
        Assert.Equal("done", job.Status);
        Assert.True(File.Exists(zipPath), $"expected the archive beside outDir at {zipPath}");
    }

    // AC-6 — for EITHER input shape, no *.zip is ever created inside outDir.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageAction_NeverWritesAnyZipInsideOutDir(bool trailingSeparator)
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");
        var passed = trailingSeparator ? outDir + Path.DirectorySeparatorChar : outDir;

        var job = Jobs.StartExecute("package", manifest, source, Sel(Selected, "msi-mode"), passed, G());
        await job.Completion!;

        Assert.Equal("done", job.Status);
        Assert.Empty(Directory.GetFiles(outDir, "*.zip", SearchOption.AllDirectories));
        Assert.Single(Directory.GetFiles(root, "*.zip"));
    }

    // AC-7 — a drive-root outputPath fails by name, not with a NullReferenceException.
    // Asserted at the path-computation level (AC-7 explicitly permits this) so the full
    // package action never runs into `C:\`: WriteCustomized executes first and would write
    // payload/, manifest.json and install.cmd straight into the drive root.
    // The exception TYPE is the assertion — Assert.Throws<Exception> would pass on the NRE.
    [Fact]
    public void ZipPathFor_DriveRoot_ThrowsNamedError_NotNullReference()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(TempDir.Create()))!;   // e.g. "C:\"

        var ex = Assert.Throws<InvalidOperationException>(() => Packages.ZipPathFor(root, "572.16"));
        Assert.Contains("no parent directory", ex.Message);
    }

    // ZipPathFor is a sibling path for both input shapes (the arithmetic, asserted literally).
    [Fact]
    public void ZipPathFor_TrailingSeparator_ResolvesToTheSameSiblingPath()
    {
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");
        var expected = Path.Combine(root, "572.16-cleandriver-package.zip");

        Assert.Equal(expected, Packages.ZipPathFor(outDir, "572.16"));
        Assert.Equal(expected, Packages.ZipPathFor(outDir + Path.DirectorySeparatorChar, "572.16"));
    }

    // Only `package` produces an archive; `extract` behavior is unchanged.
    [Fact]
    public async Task ExtractAction_WritesNoZip()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var outDir = Path.Combine(TempDir.Create(), "extract-572.16");
        var job = Jobs.StartExecute("extract", manifest, source, Sel(Selected, "msi-mode"), outDir, G());
        await job.Completion!;

        Assert.Equal("done", job.Status);
        Assert.Empty(Directory.GetFiles(Directory.GetParent(outDir)!.FullName, "*.zip"));
    }
}
