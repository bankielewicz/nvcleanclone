using System.Collections.Concurrent;
using System.Text.Json;

namespace CleanDriver.Lib;

public class Job
{
    private readonly object _lock = new();
    private readonly List<object> _log = new();

    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Status { get; set; } = "running";
    public double Progress { get; set; }
    public string? Error { get; set; }

    // download-specific
    public string? Version { get; set; }
    public int TotalMB { get; set; }
    public int DoneMB { get; set; }
    public string? Speed { get; set; }

    // execute-specific
    public string? Receipt { get; set; }
    public string? OutDir { get; set; }
    public bool Modified { get; set; }
    public bool RebootRecommended { get; set; }

    public void LogLine(string text, string cls = "")
    {
        lock (_lock) _log.Add(new { text, cls });
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                id = Id, type = Type, status = Status, progress = Progress, error = Error,
                version = Version, totalMB = TotalMB, doneMB = DoneMB, speed = Speed,
                receipt = Receipt, outDir = OutDir, modified = Modified,
                rebootRecommended = RebootRecommended,
                log = _log.ToList(),
            };
        }
    }
}

public static class Jobs
{
    public static string OutputDir => Path.Combine(AppContext.BaseDirectory, "output");

    private static readonly ConcurrentDictionary<string, Job> Store = new();
    private static int _nextId;

    private static Job Create(string type)
    {
        var job = new Job { Id = Interlocked.Increment(ref _nextId).ToString(), Type = type };
        Store[job.Id] = job;
        return job;
    }

    public static Job? Get(string id) => Store.GetValueOrDefault(id);

    // ---- simulated download -------------------------------------------------

    public static Job StartDownload(Release release)
    {
        var job = Create("download");
        job.Version = release.Version;
        job.TotalMB = release.SizeMB;
        double speed = 38 + Random.Shared.NextDouble() * 8; // MB/s, cosmetic
        const int durationMs = 5000;

        _ = Task.Run(async () =>
        {
            var started = DateTime.UtcNow;
            while (true)
            {
                var frac = Math.Min(1.0, (DateTime.UtcNow - started).TotalMilliseconds / durationMs);
                job.Progress = frac;
                job.DoneMB = (int)Math.Round(release.SizeMB * frac);
                job.Speed = $"{speed:F1} MB/s";
                if (frac >= 1) { job.Status = "done"; break; }
                await Task.Delay(150);
            }
        });
        return job;
    }

    // ---- execute (install / silent / extract / package) ---------------------

    public static Job StartExecute(string action, Manifest manifest, PackageSource source,
        Selection selection, string? outputPath, GpuInfo gpu)
    {
        var job = Create(action);
        var selected = manifest.Components.Where(c => selection.Components.Contains(c.Id)).ToList();
        var deselected = manifest.Components.Where(c => !selection.Components.Contains(c.Id)).ToList();
        bool modified = deselected.Count > 0;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var outDir = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(OutputDir, $"{action}-{manifest.Version}")
            : Path.GetFullPath(outputPath);

        var steps = new List<(int delayMs, Action fn)>();
        var display = selected.FirstOrDefault(c => c.Required);
        var extras = selected.Where(c => !c.Required).Select(c => c.Name).ToList();

        if (action is "install" or "silent")
        {
            if (selection.TweakOn("unattended"))
                job.LogLine("> Unattended mode: no prompts will be shown.", "mut");
            if (selection.TweakOn("clean-install"))
            {
                steps.Add((400, () => job.LogLine("> Removing previous driver traces…")));
                steps.Add((500, () => job.LogLine($"> Uninstalling driver {gpu.InstalledDriverVersion}… done", "mut")));
            }
            steps.Add((600, () => job.LogLine($"> Copying {display?.Name ?? "core"} payload ({display?.SizeMB.ToString() ?? "?"} MB)…")));
            if (deselected.Count > 0)
                steps.Add((250, () => job.LogLine($"> Skipping deselected components ({deselected.Count})", "mut")));
            if (extras.Count > 0)
                steps.Add((500, () => job.LogLine($"> Installing {string.Join(", ", extras)}…")));
            steps.Add((300, () =>
            {
                var applied = Tweaks.All.Where(t => selection.TweakOn(t.Id))
                    .Select(t => ShortTweakName(t, selection)).ToList();
                if (applied.Count > 0) job.LogLine($"> Applying tweaks: {string.Join(", ", applied)}…");
            }));
            if (modified)
                steps.Add((450, () => job.LogLine("> Rebuilding digital signature… done")));
            steps.Add((350, () =>
            {
                Directory.CreateDirectory(OutputDir);
                var receiptPath = Path.Combine(OutputDir, $"receipt-{stamp}.json");
                File.WriteAllText(receiptPath, JsonSerializer.Serialize(new
                {
                    action,
                    simulated = true,
                    version = manifest.Version,
                    gpu = gpu.Name,
                    installedAt = DateTime.UtcNow.ToString("o"),
                    components = selected.Select(c => c.Id),
                    tweaks = selection.Tweaks,
                    signature = modified ? "rebuilt" : "stock",
                    unattended = selection.TweakOn("unattended"),
                    autoReboot = selection.TweakOn("auto-reboot"),
                    cleanInstall = selection.TweakOn("clean-install"),
                }, Json.Web));
                job.Receipt = receiptPath;
                job.LogLine("> Writing install receipt… done");
            }));
            steps.Add((200, () => job.LogLine("> Installation finished.", "ok")));
        }
        else // extract | package
        {
            job.OutDir = outDir;
            steps.Add((300, () => job.LogLine($"> Extracting package {manifest.Version}…")));
            if (deselected.Count > 0)
                steps.Add((250, () => job.LogLine(
                    $"> Dropping deselected components ({deselected.Count}): {string.Join(", ", deselected.Select(c => c.Name))}", "mut")));
            steps.Add((500, () =>
            {
                var res = Packages.WriteCustomized(source, manifest, selection.Components, outDir, selection.Tweaks);
                job.Modified = res.modified;
                job.LogLine($"> Writing {res.written.Count} component payload(s) to {outDir}");
            }));
            steps.Add((250, () =>
            {
                // .reg artifacts for registry-affecting tweaks (never applied)
                var written = new List<string>();
                foreach (var t in Tweaks.All.Where(t => t.Reg && selection.TweakOn(t.Id)))
                {
                    var snip = Tweaks.RegSnippet(t.Id, selection)!;
                    var f = Path.Combine(outDir, $"tweak-{t.Id}.reg");
                    File.WriteAllText(f, snip);
                    written.Add(Path.GetFileName(f));
                }
                if (written.Count > 0)
                    job.LogLine($"> Writing tweak snippets: {string.Join(", ", written)} (not applied)");
            }));
            if (action == "package")
            {
                steps.Add((300, () =>
                {
                    File.WriteAllText(Path.Combine(outDir, "config.json"), JsonSerializer.Serialize(new
                    {
                        builtBy = "CleanDriver",
                        builtAt = DateTime.UtcNow.ToString("o"),
                        version = manifest.Version,
                        components = selection.Components,
                        tweaks = selection.Tweaks,
                    }, Json.Web));
                    File.WriteAllText(Path.Combine(outDir, "install.cmd"), string.Join("\r\n", new[]
                    {
                        "@echo off",
                        "rem CleanDriver self-contained package installer (simulated)",
                        "echo Installing customized driver package %~dp0 ...",
                        "echo (simulation only — no drivers are installed)",
                    }) + "\r\n");
                    job.LogLine("> Writing install script and config… done");
                }));
            }
            if (modified)
                steps.Add((400, () => job.LogLine("> Rebuilding digital signature… done")));
            steps.Add((200, () => job.LogLine(
                $"> {(action == "package" ? "Package build" : "Extraction")} finished.", "ok")));
        }

        _ = Task.Run(async () =>
        {
            int i = 0;
            foreach (var (delayMs, fn) in steps)
            {
                await Task.Delay(delayMs);
                try
                {
                    fn();
                    job.Progress = (double)++i / steps.Count;
                }
                catch (Exception ex)
                {
                    job.Status = "failed";
                    job.Error = ex.Message;
                    job.LogLine($"> ERROR: {ex.Message}", "err");
                    return;
                }
            }
            job.Progress = 1;
            job.RebootRecommended = action is "install" or "silent";
            job.Status = "done";
        });
        return job;
    }

    private static string ShortTweakName(TweakDef t, Selection selection) => t.Id switch
    {
        "clean-install" => "clean install",
        "msi-mode" => $"MSI (priority {selection.TweakString("msi-priority") ?? "Default"})",
        _ => t.Name.Replace("Disable ", "no ").Replace("Enable ", "").ToLowerInvariant(),
    };
}
