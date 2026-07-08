using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
}
