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
