using System.IO;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// GAP-04: the execute route reflects the real artifact by correlating the manifest
// version to a completed real download, server-side (never trusting a client path).
// Jobs.DownloadedFile is that correlation; the ~3-line endpoint glue that turns its
// result into a DownloadArtifact is live-smoke-covered (per GAP-02's precedent, where
// DownloadRoutingTests covered the decision logic and the endpoint itself was smoke).
public class DownloadCorrelationTests
{
    [Fact]
    public async Task DownloadedFile_ReturnsCompletedRealDownloadPath()
    {
        var dir = TempDir.Create();
        var job = Jobs.StartRealDownload(LiveRelease.New(version: "601.11", sizeMB: 1),
            dir, DownloadHandler.Bytes(new byte[64 * 1024]));
        await job.Completion!;

        Assert.Equal("done", job.Status);
        Assert.Equal(job.FilePath, Jobs.DownloadedFile("601.11"));
    }

    [Fact]
    public void DownloadedFile_UnknownVersion_ReturnsNull()
    {
        Assert.Null(Jobs.DownloadedFile("000.00"));
    }

    [Fact]
    public void DownloadedFile_SimulatedDownload_ReturnsNull()
    {
        // A simulated (mock-path) download carries no FilePath, so it is never mistaken
        // for a real artifact — the execute route stays on the byte-identical mock path.
        var sim = Jobs.StartDownload(LiveRelease.New(version: "603.33"));
        Assert.Null(Jobs.DownloadedFile("603.33"));
        Assert.Null(sim.FilePath);
    }
}
