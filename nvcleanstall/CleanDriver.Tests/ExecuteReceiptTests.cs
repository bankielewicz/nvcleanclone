using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// GAP-04 — install/silent simulation fidelity from the real download.
// The simulation stays simulated (no real install — §4), but when the driver came
// from the live path (a GAP-02 real download) the emitted receipt/log must reflect the
// REAL artifact (its on-disk path and exact byte size) and honor the install flags.
//
// All tests here share the fixed receipt path (Jobs.OutputDir/receipt-<date>.json), so
// they live in ONE class (xunit serializes tests within a class) and read job.Receipt
// immediately after awaiting completion. No other test writes that path.
public class ExecuteReceiptTests
{
    private static Manifest M() => new()
    {
        Version = "610.74",
        Channel = "WHQL",
        Components = new List<ComponentDef>
        {
            new() { Id = "display", Name = "Display Driver", SizeMB = 500, Required = true, Payload = "display.bin" },
            new() { Id = "hd-audio", Name = "HD Audio", SizeMB = 20, Recommended = true, Payload = "audio.bin" },
        },
    };

    private static PackageSource Src() => new() { Kind = "catalog", Dir = "unused-for-install" };

    private static GpuInfo G() => new()
    {
        Name = "GeForce RTX 5070",
        InstalledDriverVersion = "591.86",
        IsSimulated = false,
        DetectedVia = "system query (WMI Win32_VideoController)",
    };

    private static Selection Sel(IEnumerable<string> components, params string[] onTweaks)
    {
        var tweaks = new Dictionary<string, JsonElement>();
        foreach (var t in onTweaks) tweaks[t] = JsonDocument.Parse("true").RootElement.Clone();
        return new Selection { Components = components.ToList(), Tweaks = tweaks };
    }

    private static DownloadArtifact LiveArtifact(long bytes)
    {
        var file = Path.Combine(TempDir.Create(), "610.74-whql.exe");
        File.WriteAllBytes(file, new byte[bytes]);
        return new DownloadArtifact { FilePath = file, SizeBytes = bytes, Source = "live" };
    }

    private static JsonElement Receipt(Job job) =>
        JsonDocument.Parse(File.ReadAllText(job.Receipt!)).RootElement;

    // AC-1 (install): a live-download artifact enriches the receipt with the real file
    // path + exact byte size, and the flag fields carry their true values; the real path
    // also appears in the log.
    [Fact]
    public async Task StartExecute_InstallWithLiveArtifact_ReceiptRecordsRealFileAndFlags()
    {
        var artifact = LiveArtifact(1234);
        var job = Jobs.StartExecute("install", M(), Src(),
            Sel(new[] { "display", "hd-audio" }, "unattended", "auto-reboot", "clean-install"),
            null, G(), artifact);
        await job.Completion!;

        Assert.Equal("done", job.Status);
        var r = Receipt(job);
        Assert.Equal(artifact.FilePath, r.GetProperty("driverFile").GetString());
        Assert.Equal(1234, r.GetProperty("driverFileBytes").GetInt64());
        Assert.True(r.GetProperty("unattended").GetBoolean());
        Assert.True(r.GetProperty("autoReboot").GetBoolean());
        Assert.True(r.GetProperty("cleanInstall").GetBoolean());

        // "log the real file path and real size" (register): filename + byte count in the
        // log. Assert on escaping-safe substrings (the JSON-serialized snapshot escapes the
        // path's backslashes); the exact path is pinned via the receipt above.
        var log = JsonSerializer.Serialize(job.Snapshot(), Json.Web);
        Assert.Contains("610.74-whql.exe", log);
        Assert.Contains("1234 bytes", log);
    }

    // AC-1 (silent): same enrichment on the silent action.
    [Fact]
    public async Task StartExecute_SilentWithLiveArtifact_ReceiptRecordsRealFile()
    {
        var artifact = LiveArtifact(4096);
        var job = Jobs.StartExecute("silent", M(), Src(), Sel(new[] { "display" }), null, G(), artifact);
        await job.Completion!;

        var r = Receipt(job);
        Assert.Equal("silent", r.GetProperty("action").GetString());
        Assert.Equal(artifact.FilePath, r.GetProperty("driverFile").GetString());
        Assert.Equal(4096, r.GetProperty("driverFileBytes").GetInt64());
    }

    // AC-3 + source gate: a mock-source artifact must be IGNORED (register: enrich only
    // when source == "live"); the receipt then omits the driver fields but keeps the
    // pre-existing flag fields — the additive-only / byte-identity pin.
    [Fact]
    public async Task StartExecute_MockArtifact_OmitsDriverFields_KeepsFlagFields()
    {
        var mock = new DownloadArtifact { FilePath = "C:/nope/x.exe", SizeBytes = 999, Source = "mock" };
        var job = Jobs.StartExecute("install", M(), Src(), Sel(new[] { "display" }, "unattended"), null, G(), mock);
        await job.Completion!;

        var text = File.ReadAllText(job.Receipt!);
        Assert.DoesNotContain("driverFile", text);
        Assert.DoesNotContain("driverFileBytes", text);
        var r = JsonDocument.Parse(text).RootElement;
        Assert.True(r.GetProperty("unattended").GetBoolean());
        Assert.True(r.TryGetProperty("autoReboot", out _));
        Assert.True(r.TryGetProperty("cleanInstall", out _));
        Assert.True(r.GetProperty("simulated").GetBoolean());
    }

    // Back-compat pin: the pre-GAP-04 6-arg call (no artifact) still compiles and behaves
    // as before — receipt carries no driver fields (mock path unchanged).
    [Fact]
    public async Task StartExecute_NoArtifact_OmitsDriverFields()
    {
        var job = Jobs.StartExecute("install", M(), Src(), Sel(new[] { "display" }), null, G());
        await job.Completion!;

        Assert.DoesNotContain("driverFile", File.ReadAllText(job.Receipt!));
    }
}
