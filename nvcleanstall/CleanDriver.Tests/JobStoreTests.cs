using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// GAP-S02a — the Jobs-store integration of crypto ids (AC-5) and the BUG-05 bounded/expiring
// store (AC-6): a running job is never evicted (pinned), and a session referenced by a running
// job is recognized so Api.Sessions can pin it too.
public class JobStoreTests
{
    private static GpuInfo G() => new() { Name = "GeForce RTX 5070", InstalledDriverVersion = "591.86" };

    private static Selection Sel(params string[] comps) =>
        new() { Components = comps.ToList() };

    // Synchronously-terminal job: a filesystem-root outputPath fails at the top of StartExecute
    // (HARD-02), so Completion is already complete — a clean way to mint terminal jobs.
    private static async Task<Job> TerminalJob()
    {
        var (m, s) = Packages.LoadCatalog("572.16");
        var root = Path.GetPathRoot(Path.GetFullPath(TempDir.Create()))!;
        var j = Jobs.StartExecute("package", m, s, Sel("display-driver"), root, G());
        await j.Completion!;
        return j;
    }

    // A deterministically-running job: the SIMULATED download runs a ~5 s progress timer, so the
    // job stays "running" for this test's sub-second synchronous asserts. Deliberately NOT the
    // real stalling download — that depends on the shared Jobs.StallTimeout static, which
    // RealDownloadTests mutates in parallel (candidate #6); a timer-based sim avoids that race.
    private static Job RunningDownload() =>
        Jobs.StartDownload(new Release { Version = "572.16", Channel = "WHQL", SizeMB = 100 });

    // ---- AC-5: crypto job ids ----------------------------------------------------
    [Fact]
    public async Task JobIds_AreCryptoRandom_NotSequential()
    {
        var j1 = await TerminalJob();
        var j2 = await TerminalJob();
        Assert.NotEqual(j1.Id, j2.Id);
        Assert.Matches("^[0-9A-Fa-f]{32}$", j1.Id);
        Assert.NotEqual("1", j1.Id);
        Assert.NotEqual("2", j2.Id);
    }

    // ---- AC-6: the BUG-05 landmine at the Jobs level -----------------------------
    // A running job is never evicted, even as the oldest, while the cap sheds terminal jobs.
    [Fact]
    public async Task JobStore_Eviction_NeverRemovesRunningJob()
    {
        var priorMax = Jobs.MaxRetainedJobs;
        Jobs.MaxRetainedJobs = 3;
        try
        {
            var running = RunningDownload();            // oldest, and pinned (running)
            Assert.Equal("running", running.Status);

            var firstTerminal = await TerminalJob();    // oldest terminal — first to be shed
            for (int i = 0; i < 6; i++) await TerminalJob();   // far exceed the cap of 3

            // Eviction actually ran: the oldest terminal job is gone (discriminates against a
            // no-eviction baseline, where it would still be present).
            Assert.Null(Jobs.Get(firstTerminal.Id));

            // …but the running job survived every eviction, though it is the oldest entry.
            var still = Jobs.Get(running.Id);
            Assert.NotNull(still);
            Assert.Equal("running", still!.Status);
        }
        finally { Jobs.MaxRetainedJobs = priorMax; }
    }

    // Expiry seam: with an overridable clock and retention, a stale terminal job is evicted on
    // the next store write; a fresh one and a still-running one are not.
    [Fact]
    public async Task JobStore_Expiry_RemovesStaleTerminalJobs()
    {
        var priorClock = Jobs.Clock;
        var priorRet = Jobs.RetentionPeriod;
        var now = DateTimeOffset.UnixEpoch;
        Jobs.Clock = () => now;
        Jobs.RetentionPeriod = TimeSpan.FromMinutes(30);
        try
        {
            var stale = await TerminalJob();
            Assert.NotNull(Jobs.Get(stale.Id));
            now = now.AddMinutes(31);          // stale by age
            var fresh = await TerminalJob();   // this write triggers eviction at the new time
            Assert.Null(Jobs.Get(stale.Id));   // stale terminal job expired
            Assert.NotNull(Jobs.Get(fresh.Id));
        }
        finally { Jobs.Clock = priorClock; Jobs.RetentionPeriod = priorRet; }
    }

    // ---- AC-6: a session referenced by a running execute job is recognized ---------
    [Fact]
    public async Task HasRunningJobForSession_TrueWhileExecuteRuns_FalseWhenTerminal()
    {
        var (m, s) = Packages.LoadCatalog("572.16");
        var outDir = Path.Combine(TempDir.Create(), "extract-572.16");
        // An execute job carries the session token it was started from; right after StartExecute
        // the worker is still running (its steps have real delays).
        var job = Jobs.StartExecute("extract", m, s, Sel("display-driver"), outDir, G(),
                                    sessionToken: "sess-abc");

        Assert.True(Jobs.HasRunningJobForSession("sess-abc"));   // referenced by a running job
        Assert.False(Jobs.HasRunningJobForSession("other"));

        await job.Completion!;
        Assert.False(Jobs.HasRunningJobForSession("sess-abc"));  // job terminal -> no longer pinned
    }

    // Back-compat: the /api/jobs/{id} snapshot shape is unchanged — SessionToken is internal
    // plumbing, never serialized.
    [Fact]
    public async Task JobSnapshot_DoesNotExposeSessionToken()
    {
        var (m, s) = Packages.LoadCatalog("572.16");
        var outDir = Path.Combine(TempDir.Create(), "extract-572.16");
        var job = Jobs.StartExecute("extract", m, s, Sel("display-driver"), outDir, G(),
                                    sessionToken: "sess-secret");
        await job.Completion!;
        var snap = JsonSerializer.Serialize(job.Snapshot(), Json.Web);
        Assert.DoesNotContain("sess-secret", snap);
        Assert.DoesNotContain("sessionToken", snap, StringComparison.OrdinalIgnoreCase);
    }
}
