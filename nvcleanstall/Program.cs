using CleanDriver.Lib;

namespace CleanDriver;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool headless = args.Contains("--headless");
        string urls = Environment.GetEnvironmentVariable("CLEANDRIVER_URLS") ?? "http://localhost:4780";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.WebHost.UseUrls(urls);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();

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
            Application.Run(new ShellForm(urls.TrimEnd('/') + "/?shell=1"));
        }
        finally
        {
            app.StopAsync().Wait();
        }
    }
}
