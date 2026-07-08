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
}
