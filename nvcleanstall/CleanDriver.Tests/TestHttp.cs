using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace CleanDriver.Tests;

// Injectable fake transport so provider tests never touch the network
// (CONTRIBUTING.md: network logic is tested against recorded fixtures).
internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
    public HttpRequestMessage? LastRequest { get; private set; }

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

    public static StubHandler Ok(string body) => new(_ =>
        new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });

    public static StubHandler Throws() => new(_ => throw new HttpRequestException("simulated network failure"));

    public static StubHandler Status(HttpStatusCode code) => new(_ => new HttpResponseMessage(code));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        LastRequest = request;
        return Task.FromResult(_respond(request));
    }
}

internal static class Fixtures
{
    public static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    public const string NvidiaLookup = "nvidia-drivermanuallookup.json";
}
