using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CleanDriver.Lib;

namespace CleanDriver.Tests;

// Fake transports + streams for exercising Jobs.StartRealDownload without a network.
internal sealed class DownloadHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public DownloadHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    // Streams a fixed byte body with a real Content-Length.
    public static DownloadHandler Bytes(byte[] body) => new(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });

    // Wraps an arbitrary content stream (used for fault/stall/cancel simulation).
    public static DownloadHandler Stream(Stream content, long? contentLength = null)
    {
        var sc = new StreamContent(content);
        if (contentLength is long len) sc.Headers.ContentLength = len;
        return new DownloadHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = sc });
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Task.FromResult(_respond(request));
}

// A read stream that emits `prefix` bytes, then throws mid-stream (fault) or blocks
// until cancelled (used to drive the AC-2 failure path and the cancel path).
internal sealed class ScriptedStream : Stream
{
    private readonly byte[] _prefix;
    private int _pos;
    private readonly bool _throwAfterPrefix;
    private readonly bool _blockAfterPrefix;

    public ScriptedStream(int prefixLen, bool throwAfterPrefix = false, bool blockAfterPrefix = false)
    {
        _prefix = new byte[prefixLen];
        _throwAfterPrefix = throwAfterPrefix;
        _blockAfterPrefix = blockAfterPrefix;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pos < _prefix.Length)
        {
            int n = Math.Min(buffer.Length, _prefix.Length - _pos);
            _prefix.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        if (_throwAfterPrefix) throw new IOException("simulated mid-stream failure");
        if (_blockAfterPrefix)
        {
            await Task.Delay(Timeout.Infinite, ct); // honors cancellation / stall token
            return 0;
        }
        return 0; // clean EOF
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException();
    public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}

internal static class TempDir
{
    public static string Create()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cleandriver-dltest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

internal static class LiveRelease
{
    // A realistic live release pointing at an nvidia.com host (passes the host guard);
    // the fake handler ignores the URL but Jobs still needs a valid absolute URI.
    public static Release New(string version = "610.74", string channel = "WHQL", int sizeMB = 3) => new()
    {
        Version = version,
        Channel = channel,
        SizeMB = sizeMB,
        Source = "live",
        DownloadUrl = $"https://us.download.nvidia.com/Windows/{version}/{version}-desktop-win10-win11-64bit-international-dch-whql.exe",
    };
}
