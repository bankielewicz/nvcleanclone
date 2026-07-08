using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// GAP-05 — signature-rebuild and driver-telemetry honesty. These stay simulated but must
// stop overclaiming: the customized-package manifest (Packages.WriteCustomized) carries a
// `signatureSimulated` / `driverTelemetrySimulated` marker alongside the unchanged
// `signature` value, so no output can be mistaken for a really-signed/-patched package.
// All writes go to temp dirs (hermetic; safe to run in parallel with other classes).
public class PackageHonestyTests
{
    // Back-compat pin: `signature: "rebuilt"` is UNCHANGED; the new `signatureSimulated`
    // rides alongside it when the package is modified (the rebuild step runs).
    [Fact]
    public void WriteCustomized_Modified_ManifestMarksSignatureRebuiltAndSimulated()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var res = Packages.WriteCustomized(source, manifest,
            new List<string> { "display-driver", "physx" }, TempDir.Create());

        Assert.True(res.modified);
        var m = JsonDocument.Parse(File.ReadAllText(res.manifestPath)).RootElement;
        Assert.Equal("rebuilt", m.GetProperty("signature").GetString());
        Assert.True(m.GetProperty("signatureSimulated").GetBoolean());
    }

    // Stock (all components selected): signature stays "stock" and the marker is omitted
    // (null → WhenWritingNull) — byte-identical to before GAP-05.
    [Fact]
    public void WriteCustomized_Stock_OmitsSignatureSimulated()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var all = manifest.Components.Select(c => c.Id).ToList();
        var res = Packages.WriteCustomized(source, manifest, all, TempDir.Create());

        Assert.False(res.modified);
        var text = File.ReadAllText(res.manifestPath);
        Assert.Contains("\"signature\": \"stock\"", text);
        Assert.DoesNotContain("signatureSimulated", text);
    }

    private static Selection Sel(IEnumerable<string> comps, params string[] onTweaks)
    {
        var tweaks = new Dictionary<string, JsonElement>();
        foreach (var t in onTweaks) tweaks[t] = JsonDocument.Parse("true").RootElement.Clone();
        return new Selection { Components = comps.ToList(), Tweaks = tweaks };
    }

    private static Dictionary<string, JsonElement> Tweaks(params string[] on)
    {
        var d = new Dictionary<string, JsonElement>();
        foreach (var t in on) d[t] = JsonDocument.Parse("true").RootElement.Clone();
        return d;
    }

    private static GpuInfo G() => new() { Name = "GeForce RTX 5070", InstalledDriverVersion = "591.86" };

    // GAP-05 telemetry honesty: driver-telemetry on → manifest marks driverTelemetrySimulated.
    [Fact]
    public void WriteCustomized_TelemetryOn_ManifestMarksTelemetrySimulated()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var res = Packages.WriteCustomized(source, manifest,
            new List<string> { "display-driver" }, TempDir.Create(), Tweaks("driver-telemetry"));

        var m = JsonDocument.Parse(File.ReadAllText(res.manifestPath)).RootElement;
        Assert.True(m.GetProperty("driverTelemetrySimulated").GetBoolean());
    }

    // Off → omitted (null → WhenWritingNull).
    [Fact]
    public void WriteCustomized_TelemetryOff_OmitsTelemetrySimulated()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var res = Packages.WriteCustomized(source, manifest, new List<string> { "display-driver" }, TempDir.Create());
        Assert.DoesNotContain("driverTelemetrySimulated", File.ReadAllText(res.manifestPath));
    }

    // build-package config.json marks the telemetry patch simulated, and the log carries
    // the telemetry qualifier. (The package action's manifest is covered above.)
    [Fact]
    public async Task PackageAction_TelemetryOn_ConfigAndLogMarkTelemetrySimulated()
    {
        var (manifest, source) = Packages.LoadCatalog("572.16");
        var outDir = TempDir.Create();
        var job = Jobs.StartExecute("package", manifest, source,
            Sel(new[] { "display-driver" }, "driver-telemetry"), outDir, G());
        await job.Completion!;

        Assert.Equal("done", job.Status);
        var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "config.json"))).RootElement;
        Assert.True(cfg.GetProperty("driverTelemetrySimulated").GetBoolean());

        var log = JsonSerializer.Serialize(job.Snapshot(), Json.Web);
        Assert.Contains("Patching driver telemetry endpoints", log);
        Assert.Contains("no real patching performed", log);
    }
}
