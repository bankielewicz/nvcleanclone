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
        Api.Map(app);

        Console.WriteLine($"CleanDriver running at {urls}{(headless ? " (headless)" : "")}");

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
