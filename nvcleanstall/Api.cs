using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using CleanDriver.Lib;

namespace CleanDriver;

public static class Api
{
    // package sources loaded this session, keyed by token handed to the client
    private static readonly ConcurrentDictionary<string, (Manifest manifest, PackageSource source)> Sessions = new();
    private static int _nextSession;

    private record PackageRequest(string Kind, string? Version, string? Path);
    private record DownloadRequest(string Version);
    private record ExecuteRequest(string Token, string Action, Selection? Selection, string? OutputPath);
    private record PresetSaveRequest(string Name, Selection Selection);
    private record OpenFolderRequest(string? Path);

    public static void Map(WebApplication app, ICatalogProvider catalog)
    {
        app.MapGet("/api/system", () => Results.Json(Gpu.Detect(), Json.Web));

        // Catalog reads route through the provider seam (GAP-01): live NVIDIA lookup or
        // mock, each release carrying a source marker. Response is a superset of the
        // original shape (releases[] + a source field).
        app.MapGet("/api/catalog", () =>
            Results.Json(CatalogEndpoint.Build(catalog, Gpu.Detect()), Json.Web));

        app.MapGet("/api/tweaks", () => Results.Json(new { tweaks = Tweaks.All }, Json.Web));

        // The page cannot open an OS file picker; Browse… offers the bundled sample package.
        app.MapGet("/api/sample-package", () =>
        {
            var sample = Catalog.Releases().Skip(1).FirstOrDefault() ?? Catalog.Releases().First();
            return Results.Json(new { path = Catalog.PackageDir(sample.Version) }, Json.Web);
        });

        app.MapPost("/api/package", (PackageRequest req) =>
        {
            try
            {
                (Manifest manifest, PackageSource source) loaded;
                if (req.Kind == "catalog")
                {
                    var version = req.Version ?? throw new InvalidDataException("version required");
                    // GAP-S01 F1: validate before the HasCatalogPackage fork, so the untrusted
                    // version never reaches Catalog.PackageDir (via HasCatalogPackage's
                    // Directory.Exists probe) unvalidated. LoadCatalog validates too — this is
                    // the earliest point, closing the filesystem-probe side channel.
                    PackageValidation.ValidateVersion(version);
                    // A live-downloaded version has no real package: serve a labeled sample.
                    loaded = Packages.HasCatalogPackage(version)
                        ? Packages.LoadCatalog(version)
                        : Packages.LoadSampleTemplate(version);
                }
                else
                {
                    loaded = Packages.LoadLocal(req.Path ?? throw new InvalidDataException("path required"));
                }
                var token = Interlocked.Increment(ref _nextSession).ToString();
                Sessions[token] = loaded;
                return Results.Json(new { token, manifest = loaded.manifest }, Json.Web);
            }
            catch (InvalidDataException ex)
            {
                // Our own validation messages carry no filesystem paths — safe to surface.
                return Results.Json(new { error = ex.Message }, Json.Web, statusCode: 400);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Never leak absolute filesystem paths to the client.
                return Results.Json(new { error = "could not load package" }, Json.Web, statusCode: 400);
            }
        });

        app.MapPost("/api/download", (DownloadRequest req) =>
        {
            // Resolve the release server-side via the provider (never trust a client URL):
            // this yields the source marker + DownloadUrl and keeps route-by-source honest.
            var rel = catalog.GetReleases(Gpu.Detect()).FirstOrDefault(r => r.Version == req.Version);
            if (rel == null) return Results.Json(new { error = "unknown version" }, Json.Web, statusCode: 404);
            var job = Jobs.ShouldRealDownload(rel)
                ? Jobs.StartRealDownload(rel, Path.Combine(Jobs.OutputDir, "drivers"))
                : Jobs.StartDownload(rel);
            return Results.Json(new { jobId = job.Id }, Json.Web);
        });

        app.MapPost("/api/download/{id}/cancel", (string id) =>
            Jobs.Cancel(id)
                ? Results.Json(new { cancelled = id }, Json.Web)
                : Results.Json(new { error = "no such job" }, Json.Web, statusCode: 404));

        app.MapPost("/api/execute", (ExecuteRequest req) =>
        {
            if (!Sessions.TryGetValue(req.Token, out var src))
                return Results.Json(new { error = "unknown package token" }, Json.Web, statusCode: 400);
            if (req.Action is not ("install" or "silent" or "extract" or "package"))
                return Results.Json(new { error = "unknown action" }, Json.Web, statusCode: 400);
            try
            {
                // GAP-04: if this version was really downloaded (GAP-02, live path) this
                // session, reflect the actual artifact in the (still simulated) receipt/log.
                var file = Jobs.DownloadedFile(src.manifest.Version);
                DownloadArtifact? artifact = file != null && File.Exists(file)
                    ? new DownloadArtifact { FilePath = file, SizeBytes = new FileInfo(file).Length, Source = "live" }
                    : null;
                var job = Jobs.StartExecute(req.Action, src.manifest, src.source,
                    req.Selection ?? new Selection(), req.OutputPath, Gpu.Detect(), artifact);
                return Results.Json(new { jobId = job.Id }, Json.Web);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                return Results.Json(new { error = ex.Message }, Json.Web, statusCode: 400);
            }
        });

        app.MapGet("/api/jobs/{id}", (string id) =>
        {
            var job = Jobs.Get(id);
            return job == null
                ? Results.Json(new { error = "no such job" }, Json.Web, statusCode: 404)
                : Results.Json(job.Snapshot(), Json.Web);
        });

        app.MapGet("/api/presets", () => Results.Json(new { presets = Presets.List() }, Json.Web));

        app.MapPost("/api/presets", (PresetSaveRequest req) =>
        {
            try { return Results.Json(Presets.Save(req.Name, req.Selection), Json.Web); }
            catch (InvalidDataException ex) { return Results.Json(new { error = ex.Message }, Json.Web, statusCode: 400); }
        });

        app.MapGet("/api/presets/{name}", (string name) =>
        {
            var preset = Presets.Load(name);
            return preset == null
                ? Results.Json(new { error = "no such preset" }, Json.Web, statusCode: 404)
                : Results.Json(preset, Json.Web);
        });

        app.MapPost("/api/open-folder", (OpenFolderRequest req) =>
        {
            var dir = req.Path != null && Directory.Exists(req.Path) ? req.Path : Jobs.OutputDir;
            try
            {
                Process.Start(new ProcessStartInfo("explorer", dir.Replace('/', '\\')) { UseShellExecute = false });
            }
            catch { /* best effort — headless/CI environments have no shell */ }
            return Results.Json(new { opened = dir }, Json.Web);
        });
    }
}
