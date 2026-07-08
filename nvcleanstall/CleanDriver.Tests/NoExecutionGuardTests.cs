using System;
using System.IO;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// GAP-02 AC-3 — "shape-without-function" guard: the downloaded installer is fetched
// to disk and NEVER executed or extracted. This is a SAFETY PIN (green from the start),
// same category as a back-compat pin — it locks an invariant, it does not drive new code.
public class NoExecutionGuardTests
{
    // (a) Textual: the download+execute engine (Jobs.cs) has no execution/extraction
    // capability at all. Legitimate uses elsewhere are intentionally out of scope:
    //   - Api.cs uses Process.Start only to open a *directory* in Explorer (open-folder);
    //   - Packages.cs uses ZipFile only to read a user-supplied *local* package zip.
    // Neither ever receives the downloaded .exe path, which lives only inside Jobs.cs.
    [Fact]
    public void JobsEngine_ContainsNoExecutionOrExtractionPrimitives()
    {
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "src", "Jobs.cs"));
        foreach (var forbidden in new[] { "Process.Start", "ProcessStartInfo", "ZipFile", ".Extract(", "System.Diagnostics" })
            Assert.DoesNotContain(forbidden, src, StringComparison.Ordinal);
    }

    // (b) Behavioral: the execute/extract input for a live-downloaded version is the
    // mock sample template under data/packages/ — never the downloaded .exe in output/drivers.
    [Fact]
    public void LiveSession_ExecuteInputIsSampleTemplate_NeverTheDownloadedExe()
    {
        var (_, source) = Packages.LoadSampleTemplate("610.74");

        var dir = source.Dir!.Replace('\\', '/');
        Assert.Contains("data/packages/", dir);
        Assert.DoesNotContain("output/drivers", dir);
        Assert.DoesNotContain(".exe", dir);
        Assert.Null(source.ZipPath);
    }
}
