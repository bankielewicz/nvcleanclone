using System.IO;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

public class RealDownloadTests
{
    // AC-1: StartRealDownload streams the body to <version>-<type>.exe, reaching 1.0
    // progress with the .part renamed to the final file.
    [Fact]
    public async Task StartRealDownload_StreamsBodyToFinalFile()
    {
        const int mib = 1024 * 1024;
        var body = new byte[3 * mib];
        for (int i = 0; i < body.Length; i++) body[i] = (byte)(i % 251);
        var dir = TempDir.Create();

        var job = Jobs.StartRealDownload(LiveRelease.New(version: "610.74", channel: "WHQL", sizeMB: 3),
            dir, DownloadHandler.Bytes(body));
        await job.Completion!;

        var final = Path.Combine(dir, "610.74-whql.exe");
        Assert.Equal("done", job.Status);
        Assert.True(File.Exists(final), "final file should exist");
        Assert.False(File.Exists(final + ".part"), ".part should have been renamed away");
        Assert.Equal(body.Length, new FileInfo(final).Length);
        Assert.Equal(body, File.ReadAllBytes(final));
        Assert.Equal(1.0, job.Progress);
        Assert.Equal(3, job.DoneMB);
        Assert.Equal(final, job.FilePath);
    }

    // AC-2: a mid-stream failure deletes the partial file and fails the job, no .part residue.
    [Fact]
    public async Task StartRealDownload_MidStreamFailure_DeletesPartAndFails()
    {
        var dir = TempDir.Create();
        var stream = new ScriptedStream(prefixLen: 512 * 1024, throwAfterPrefix: true);

        var job = Jobs.StartRealDownload(LiveRelease.New(), dir, DownloadHandler.Stream(stream));
        await job.Completion!;

        Assert.Equal("failed", job.Status);
        Assert.False(string.IsNullOrEmpty(job.Error));
        Assert.Empty(Directory.GetFiles(dir)); // neither final nor .part left behind
    }

    // Safety: an empty (zero-byte) body is a failure, not a 0-byte "success".
    [Fact]
    public async Task StartRealDownload_EmptyBody_Fails()
    {
        var dir = TempDir.Create();
        var job = Jobs.StartRealDownload(LiveRelease.New(), dir, DownloadHandler.Bytes(System.Array.Empty<byte>()));
        await job.Completion!;

        Assert.Equal("failed", job.Status);
        Assert.Empty(Directory.GetFiles(dir));
    }

    // Safety: a body exceeding the configured max size fails before/while writing.
    [Fact]
    public async Task StartRealDownload_ExceedsMaxSize_Fails()
    {
        var dir = TempDir.Create();
        Environment.SetEnvironmentVariable("CLEANDRIVER_MAX_DOWNLOAD_MB", "1");
        try
        {
            var body = new byte[2 * 1024 * 1024]; // 2 MiB > 1 MiB cap
            var job = Jobs.StartRealDownload(LiveRelease.New(), dir, DownloadHandler.Bytes(body));
            await job.Completion!;

            Assert.Equal("failed", job.Status);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Environment.SetEnvironmentVariable("CLEANDRIVER_MAX_DOWNLOAD_MB", null); }
    }

    // Defense-in-depth: refuse to fetch from a non-NVIDIA host (no request attempted).
    [Fact]
    public async Task StartRealDownload_NonNvidiaHost_Fails()
    {
        var dir = TempDir.Create();
        var rel = LiveRelease.New() with { DownloadUrl = "https://evil.example.com/610.74/driver.exe" };
        var job = Jobs.StartRealDownload(rel, dir, DownloadHandler.Bytes(new byte[16]));
        await job.Completion!;

        Assert.Equal("failed", job.Status);
        Assert.Empty(Directory.GetFiles(dir));
    }

    // Cancel (register prose): cancelling a real download aborts it, deletes the
    // .part, and ends Status=="cancelled".
    [Fact]
    public async Task Cancel_RealDownload_AbortsAndDeletesPart()
    {
        var dir = TempDir.Create();
        var stream = new ScriptedStream(prefixLen: 64 * 1024, blockAfterPrefix: true);

        var job = Jobs.StartRealDownload(LiveRelease.New(), dir, DownloadHandler.Stream(stream));
        await Task.Delay(80);           // let it get mid-stream
        Assert.True(Jobs.Cancel(job.Id));
        await job.Completion!;

        Assert.Equal("cancelled", job.Status);
        Assert.Empty(Directory.GetFiles(dir));
    }

    // Cancel on a simulated (mock-path) job is a safe no-op — no CTS, simulation
    // finishes harmlessly (preserves the AC-4 byte-identity pin). Unknown id -> false.
    [Fact]
    public void Cancel_SimulatedJob_IsSafeNoOp()
    {
        var sim = Jobs.StartDownload(LiveRelease.New(version: "572.16"));
        Assert.True(Jobs.Cancel(sim.Id));      // job exists -> true, but nothing to abort
        Assert.NotEqual("cancelled", sim.Status);
        Assert.False(Jobs.Cancel("no-such-job"));
    }

    // Liveness: a stream that stalls past the per-read timeout fails (not hangs).
    [Fact]
    public async Task StartRealDownload_Stall_FailsWithStalledMessage()
    {
        var dir = TempDir.Create();
        var prior = Jobs.StallTimeout;
        Jobs.StallTimeout = System.TimeSpan.FromMilliseconds(150);
        try
        {
            var stream = new ScriptedStream(prefixLen: 64 * 1024, blockAfterPrefix: true);
            var job = Jobs.StartRealDownload(LiveRelease.New(), dir, DownloadHandler.Stream(stream));
            await job.Completion!;

            Assert.Equal("failed", job.Status);
            Assert.Contains("stall", job.Error ?? "", System.StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.GetFiles(dir));
        }
        finally { Jobs.StallTimeout = prior; }
    }

    // REQUIRED edge case: a version that isn't NNN.NN is rejected before any
    // filesystem call — no path traversal, no file created.
    [Fact]
    public async Task StartRealDownload_RejectsPathInjectingVersion()
    {
        var dir = TempDir.Create();
        var rel = LiveRelease.New() with { Version = @"..\..\evil" };

        var job = Jobs.StartRealDownload(rel, dir, DownloadHandler.Bytes(new byte[16]));
        await job.Completion!;

        Assert.Equal("failed", job.Status);
        Assert.Empty(Directory.GetFiles(dir));
        Assert.Empty(Directory.GetFiles(Directory.GetParent(dir)!.FullName, "*evil*"));
    }

    // REQUIRED edge case: a second download of a version already in flight returns the
    // existing job (idempotent) — no interleaved writes to the same .part.
    [Fact]
    public async Task StartRealDownload_DuplicateVersion_ReturnsExistingJob()
    {
        var dir = TempDir.Create();
        var first = Jobs.StartRealDownload(LiveRelease.New(version: "599.99"), dir,
            DownloadHandler.Stream(new ScriptedStream(64 * 1024, blockAfterPrefix: true)));
        var second = Jobs.StartRealDownload(LiveRelease.New(version: "599.99"), dir,
            DownloadHandler.Stream(new ScriptedStream(64 * 1024, blockAfterPrefix: true)));

        Assert.Equal(first.Id, second.Id);

        Jobs.Cancel(first.Id);
        await first.Completion!;
    }
}
