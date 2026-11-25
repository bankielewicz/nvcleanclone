using CleanDriver.Lib;
using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.DependencyInjection;

namespace CleanDriver;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool headless = args.Contains("--headless");
        string urls = Environment.GetEnvironmentVariable("CLEANDRIVER_URLS") ?? "http://localhost:4780";

        // GAP-S02a (SEC-02 pin 2): refuse to bind anywhere but loopback. A non-loopback
        // CLEANDRIVER_URLS exposes the filesystem-capable API to the network — fail fast.
        if (!HttpSecurity.IsLoopbackBind(urls))
        {
            Console.Error.WriteLine(
                $"CleanDriver refuses to bind to a non-loopback address: '{urls}'. " +
                "Set CLEANDRIVER_URLS to a loopback address (default http://localhost:4780).");
            Environment.Exit(2);
            return;
        }

        // GAP-S02a (SEC-02 pin 1): a 256-bit per-launch token gates every /api/* route. A
        // deterministic token via CLEANDRIVER_API_TOKEN lets headless tests authenticate.
        string apiToken = Environment.GetEnvironmentVariable("CLEANDRIVER_API_TOKEN") ?? Ids.NewToken();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.WebHost.UseUrls(urls);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // GAP-S02a (SEC-02 pin 2): Host filtering — a foreign Host header (DNS-rebind) is refused.
        builder.Services.Configure<HostFilteringOptions>(o =>
            o.AllowedHosts = new List<string> { "localhost", "127.0.0.1", "[::1]" });

        var app = builder.Build();
        app.UseHostFiltering();

        // GAP-S02a (SEC-02/SEC-05 pin 3): security headers on every response.
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'";
            h["X-Frame-Options"] = "DENY";
            h["X-Content-Type-Options"] = "nosniff";
            await next();
        });

        app.UseDefaultFiles();
        app.UseStaticFiles();

        // GAP-S02a (SEC-02 pins 1,7): gate every /api/* route on the token, and refuse a
        // mutating request carrying a foreign Origin. Static files (served above) are NOT gated —
        // the page must load before it can send the token.
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                if (HttpMethods.IsPost(ctx.Request.Method))
                {
                    var origin = ctx.Request.Headers["Origin"].ToString();
                    if (!string.IsNullOrEmpty(origin) && !HttpSecurity.IsAllowedOrigin(origin, urls))
                    {
                        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                        return;   // no body leak
                    }
                }
                var provided = ctx.Request.Headers[HttpSecurity.TokenHeader].ToString();
                if (!HttpSecurity.TokenMatches(provided, apiToken))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;   // no body leak
                }
            }
            await next();
        });

        // Select the catalog provider: live NVIDIA lookup by default, mock when
        // --mock-catalog / CLEANDRIVER_MOCK_CATALOG=1 (deterministic/offline runs).
        var catalog = CatalogProviderFactory.Create(
            args, Environment.GetEnvironmentVariable,
            log: msg => app.Logger.LogWarning("{Message}", msg));
        Api.Map(app, catalog);

        Console.WriteLine($"CleanDriver running at {urls} " +
            $"(catalog: {(CatalogProviderFactory.UseMock(args, Environment.GetEnvironmentVariable) ? "mock" : "live")})" +
            $"{(headless ? " (headless)" : "")}");

        if (headless)
        {
            app.Run();
            return;
        }

        app.Start();
        try
        {
            ApplicationConfiguration.Initialize();
            // GAP-S02a (SEC-02 pin 1): hand the token to the page by URL fragment. Fragments are
            // never sent to the server, so the token stays out of request logs; app.js reads
            // location.hash and sends it as X-CleanDriver-Token on every /api call.
            Application.Run(new ShellForm(urls.TrimEnd('/') + "/?shell=1#token=" + apiToken));
        }
        finally
        {
            app.StopAsync().Wait();
        }
    }
}
