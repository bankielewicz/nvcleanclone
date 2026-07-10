using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// HARD-01 — manifest-scoped clean of a previous CleanDriver build in outDir.
// HARD-02 — reject a filesystem-root outputPath before any write.
//
// The clean derives its delete-list from the PRIOR manifest's own declarations: "undo what
// that manifest says was written", never "empty the directory". Foreign files always survive,
// and a directory without a CleanDriver manifest is never touched.
//
// Every expected path here is a LITERAL (register AC-5 rider, candidate #29): a test that
// recomputes production's path arithmetic cannot falsify that arithmetic — the PR #12 lesson.
public class OutDirHygieneTests
{
    private static Selection Sel(IEnumerable<string> comps, params string[] onTweaks)
    {
        var tweaks = new Dictionary<string, JsonElement>();
        foreach (var t in onTweaks) tweaks[t] = JsonDocument.Parse("true").RootElement.Clone();
        return new Selection { Components = comps.ToList(), Tweaks = tweaks };
    }

    private static GpuInfo G() => new() { Name = "GeForce RTX 5070", InstalledDriverVersion = "591.86" };

    private static IEnumerable<string> FilesUnder(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(dir, p).Replace('\\', '/'))
            .OrderBy(p => p, StringComparer.Ordinal);

    private static async Task<Job> Run(string action, string outDir, Selection sel)
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var job = Jobs.StartExecute(action, manifest, source, sel, outDir, G());
        await job.Completion!;
        return job;
    }

    // ---- HARD-01 AC-1 -------------------------------------------------------------
    // Rebuild with a SMALLER selection and DIFFERENT tweaks into the same directory: the
    // directory (not merely the archive) holds only the new build's payloads and .reg, and a
    // planted foreign file survives both builds.
    [Fact]
    public async Task Rebuild_SmallerSelection_LeavesOnlyNewBuildsArtifacts_ForeignFileSurvives()
    {
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");
        Directory.CreateDirectory(outDir);
        var foreign = Path.Combine(outDir, "leftover.txt");
        File.WriteAllText(foreign, "not mine");

        var first = await Run("package", outDir, Sel(new[] { "display-driver", "physx", "hd-audio" }, "msi-mode"));
        Assert.Equal("done", first.Status);
        Assert.True(File.Exists(Path.Combine(outDir, "payload", "physx.bin")));
        Assert.True(File.Exists(Path.Combine(outDir, "tweak-msi-mode.reg")));
        Assert.True(File.Exists(foreign), "the foreign file survives the first build");

        var second = await Run("package", outDir, Sel(new[] { "display-driver" }, "disable-hdcp"));
        Assert.Equal("done", second.Status);

        Assert.Equal(
            new[]
            {
                "config.json",
                "install.cmd",
                "leftover.txt",
                "manifest.json",
                "payload/display-driver.bin",
                "tweak-disable-hdcp.reg",
            },
            FilesUnder(outDir).ToArray());

        Assert.True(File.Exists(foreign), "the foreign file survives the rebuild — deletion is manifest-scoped");
    }

    // The clean applies to `extract` too (the register says both actions). An extract into a
    // directory holding a prior *package* build removes that build's install.cmd/config.json,
    // which extract itself never writes.
    [Fact]
    public async Task Extract_IntoPreviousPackageBuild_CleansTheStalePackageArtifacts()
    {
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "extract-572.16");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "leftover.txt"), "not mine");

        var built = await Run("package", outDir, Sel(new[] { "display-driver", "physx" }, "msi-mode"));
        Assert.Equal("done", built.Status);
        Assert.True(File.Exists(Path.Combine(outDir, "install.cmd")));
        Assert.True(File.Exists(Path.Combine(outDir, "config.json")));

        var extracted = await Run("extract", outDir, Sel(new[] { "display-driver" }));
        Assert.Equal("done", extracted.Status);

        Assert.Equal(
            new[] { "leftover.txt", "manifest.json", "payload/display-driver.bin" },
            FilesUnder(outDir).ToArray());

        // extract still writes no archive (existing behavior, unchanged).
        Assert.Empty(Directory.GetFiles(root, "*.zip"));
    }

    // ---- HARD-01 AC-2 -------------------------------------------------------------
    // A directory whose manifest.json is not a CleanDriver manifest is written into exactly as
    // today: no cleaning, no refusal. Nothing planted is deleted.
    [Theory]
    [InlineData("{\"customizedBy\":\"SomeoneElse\",\"components\":[{\"payload\":\"stale.bin\"}]}")]
    [InlineData("{\"version\":\"572.16\",\"components\":[{\"payload\":\"stale.bin\"}]}")]   // no customizedBy
    [InlineData("{ this is not json")]                                                       // malformed: must not throw, must not delete
    [InlineData("")]                                                                          // empty
    public async Task ForeignOrMalformedManifest_NothingIsDeleted(string manifestJson)
    {
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");
        Directory.CreateDirectory(Path.Combine(outDir, "payload"));
        File.WriteAllText(Path.Combine(outDir, "manifest.json"), manifestJson);
        File.WriteAllText(Path.Combine(outDir, "payload", "stale.bin"), "stale payload");
        File.WriteAllText(Path.Combine(outDir, "leftover.txt"), "not mine");
        File.WriteAllText(Path.Combine(outDir, "tweak-msi-mode.reg"), "planted reg");

        var job = await Run("package", outDir, Sel(new[] { "display-driver" }));
        Assert.Equal("done", job.Status);

        // Every planted file survives. manifest.json is overwritten by the new build (a write,
        // not a delete), so it is not asserted here.
        Assert.True(File.Exists(Path.Combine(outDir, "payload", "stale.bin")), "foreign payload survives");
        Assert.True(File.Exists(Path.Combine(outDir, "leftover.txt")), "foreign file survives");
        Assert.True(File.Exists(Path.Combine(outDir, "tweak-msi-mode.reg")), "planted .reg survives — not our manifest");
    }

    // ---- HARD-01 AC-3 -------------------------------------------------------------
    // Shape-without-function guard, in the NoExecutionGuardTests style: deletion is
    // enumerable-by-name only. No recursive whole-tree removal is reachable from StartExecute.
    // outputPath is user-supplied; a recursive clean there could destroy a user's files.
    [Fact]
    public void CleanPath_ContainsNoRecursiveDeletePrimitives()
    {
        foreach (var file in new[] { "Jobs.cs", "Packages.cs" })
        {
            var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "src", file));
            Assert.DoesNotContain("Directory.Delete", src, StringComparison.Ordinal);
            Assert.DoesNotContain("recursive: true", src, StringComparison.Ordinal);
        }
    }

    // ---- HARD-01 AC-4 -------------------------------------------------------------
    [Fact]
    public async Task JobLog_NamesTheCleanedFiles_WhenACleanRan()
    {
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");

        var first = await Run("package", outDir, Sel(new[] { "display-driver", "physx" }, "msi-mode"));
        var firstLog = JsonSerializer.Serialize(first.Snapshot(), Json.Web);
        Assert.DoesNotContain("Cleaning previous CleanDriver build", firstLog);   // nothing to clean

        var second = await Run("package", outDir, Sel(new[] { "display-driver" }));
        var secondLog = JsonSerializer.Serialize(second.Snapshot(), Json.Web);
        Assert.Contains("Cleaning previous CleanDriver build", secondLog);
        Assert.Contains("payload/physx.bin", secondLog);        // names what it removed
        Assert.Contains("tweak-msi-mode.reg", secondLog);
    }

    // ---- HARD-01 AC-5 rider (candidate #12) ---------------------------------------
    // The GAP-05 honesty markers must be present INSIDE the archive, not only in the staging
    // directory: a consumer reads the redistributable, never the build folder.
    [Fact]
    public async Task ArchivedManifestAndConfig_CarryTheHonestyMarkers()
    {
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");
        var zipPath = Path.Combine(root, "572.16-cleandriver-package.zip");   // literal

        var job = await Run("package", outDir, Sel(new[] { "display-driver" }, "driver-telemetry"));
        Assert.Equal("done", job.Status);

        using var zip = ZipFile.OpenRead(zipPath);
        using var mfs = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        var mf = JsonDocument.Parse(mfs.ReadToEnd()).RootElement;
        Assert.Equal("rebuilt", mf.GetProperty("signature").GetString());
        Assert.True(mf.GetProperty("signatureSimulated").GetBoolean());
        Assert.True(mf.GetProperty("driverTelemetrySimulated").GetBoolean());

        using var cfs = new StreamReader(zip.GetEntry("config.json")!.Open());
        var cfg = JsonDocument.Parse(cfs.ReadToEnd()).RootElement;
        Assert.True(cfg.GetProperty("driverTelemetrySimulated").GetBoolean());
    }
}
