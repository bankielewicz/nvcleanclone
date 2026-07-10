using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    // real-download-specific (GAP-02, additive)
    public string? FilePath { get; set; }
    public Task? Completion { get; internal set; }          // not serialized; tests await it
    internal CancellationTokenSource? Cancellation { get; set; } // null for simulated jobs

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
                rebootRecommended = RebootRecommended, filePath = FilePath,
                log = _log.ToList(),
            };
        }
    }
}

// GAP-04: facts about the real installer fetched by GAP-02, threaded into the (still
// simulated) install/silent run so its receipt/log reflect the actual artifact. Present
// only for live-path downloads; null on the mock path (keeping mock receipts identical).
public record DownloadArtifact
{
    public string FilePath { get; init; } = "";
    public long SizeBytes { get; init; }
    public string? Source { get; init; } // mirrors Release.Source: "live" | "mock"
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

    // GAP-04: the final path of the most recent completed real download for a version, or
    // null if none this session. Simulated/mock jobs carry no FilePath and are excluded,
    // so the execute route reflects a real artifact only when one was actually downloaded —
    // without trusting any client-supplied path. Same version -> same finalPath, so the
    // unordered Store scan cannot return a wrong file.
    public static string? DownloadedFile(string version) =>
        Store.Values
            .Where(j => j.Type == "download" && j.Version == version
                        && j.Status == "done" && !string.IsNullOrEmpty(j.FilePath))
            .Select(j => j.FilePath)
            .LastOrDefault();

    // Per-read stall window (public seam so tests can shorten it; no UI knob — §4).
    public static TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // Cancels a running real download (aborts the stream; the worker deletes the
    // .part and marks the job "cancelled"). A no-op for simulated jobs, which carry
    // no CancellationTokenSource — the simulation finishes harmlessly, preserving
    // the mock-path byte-identity pin. Returns false only when the job is unknown.
    public static bool Cancel(string id)
    {
        var job = Get(id);
        if (job == null) return false;
        // A cancel racing the worker's finally (or any late/duplicate cancel) can hit
        // an already-disposed CTS; a late cancel on a finished job is a harmless no-op.
        try { job.Cancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* download already completed */ }
        return true;
    }

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

    // ---- real download (GAP-02): fetch bytes to disk, never executed --------

    // Route-by-source: only a server-resolved live release with a real URL streams
    // for real; mock and mock-stamped fallback releases keep the simulation.
    public static bool ShouldRealDownload(Release release) =>
        release.Source == "live" && !string.IsNullOrWhiteSpace(release.DownloadUrl);

    // Untrusted live version strings are used to build a file path — accept only NNN.NN.
    private static readonly Regex DriverVersionRe = new(@"^\d{3}\.\d{2}$", RegexOptions.Compiled);

    // In-flight real downloads keyed by version, for idempotent double-click handling.
    private static readonly ConcurrentDictionary<string, string> ActiveDownloads = new();

    // A separate client from GAP-01's 5s metadata client: a real installer legitimately
    // takes minutes, so the client has no overall timeout (ResponseHeadersRead + a
    // per-read stall timeout enforce liveness instead).
    private static readonly TimeSpan SpeedSampleWindow = TimeSpan.FromSeconds(1);

    // Configurable cap (register): env override, else 2 GiB (real packages are ~1 GB).
    public static long MaxDownloadBytes()
    {
        var env = Environment.GetEnvironmentVariable("CLEANDRIVER_MAX_DOWNLOAD_MB");
        return long.TryParse(env, out var mb) && mb > 0 ? mb * 1024L * 1024L : 2L * 1024 * 1024 * 1024;
    }

    // <type> is derived from our own channel enum, never from response text.
    private static string TypeTag(string channel) =>
        channel.Equals("Beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "whql";

    private static void EnsureDiskSpace(string destDir, long needed)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destDir));
        if (string.IsNullOrEmpty(root)) return;
        try
        {
            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed + 64L * 1024 * 1024)
                throw new IOException("insufficient disk space for download");
        }
        catch (ArgumentException) { /* non-fixed / UNC path — skip the check */ }
    }

    // Delete swallows-and-ignores: a cleanup failure (e.g. an AV lock on the .part)
    // must never mask the original error that triggered cleanup.
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    // Atomic same-volume rename (MoveFileEx REPLACE_EXISTING). The write stream is
    // already disposed by the caller. Retries a few times to ride out a transient
    // antivirus lock on the freshly written file.
    private static void MoveIntoPlace(string part, string final)
    {
        for (int attempt = 1; ; attempt++)
        {
            try { File.Move(part, final, overwrite: true); return; }
            catch (IOException) when (attempt < 3) { Thread.Sleep(200); }
        }
    }

    public static Job StartRealDownload(Release release, string destDir, HttpMessageHandler? handler = null)
    {
        var version = release.Version ?? "";

        // Idempotent: a second request for a version already downloading returns the
        // in-flight job rather than starting a second interleaved stream.
        if (ActiveDownloads.TryGetValue(version, out var existingId)
            && Get(existingId) is { Status: "running" } existing)
            return existing;

        var job = Create("download");
        job.Version = version;
        job.TotalMB = release.SizeMB;

        // Reject path-injecting / malformed versions before any filesystem call.
        if (!DriverVersionRe.IsMatch(version))
        {
            job.Status = "failed";
            job.Error = "invalid driver version";
            job.Completion = Task.CompletedTask;
            return job;
        }

        bool ownsHandler = handler == null;
        var http = new HttpClient(handler ?? new SocketsHttpHandler(), disposeHandler: ownsHandler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        var cts = new CancellationTokenSource();
        job.Cancellation = cts;
        ActiveDownloads[version] = job.Id;
        job.Completion = Task.Run(() => RunRealDownloadAsync(job, release, destDir, http, cts));
        return job;
    }

    private static async Task RunRealDownloadAsync(Job job, Release release, string destDir,
        HttpClient http, CancellationTokenSource cancelCts)
    {
        var cancelToken = cancelCts.Token;
        var type = TypeTag(release.Channel);
        var finalPath = Path.Combine(destDir, $"{release.Version}-{type}.exe");
        var partPath = finalPath + ".part";
        try
        {
            // Defense-in-depth: only ever fetch from NVIDIA (on top of server-side
            // provider re-resolution). Removable for mirrors — see PR body.
            var host = new Uri(release.DownloadUrl ?? throw new IOException("release has no download URL")).Host;
            if (!(host.Equals("nvidia.com", StringComparison.OrdinalIgnoreCase) ||
                  host.EndsWith(".nvidia.com", StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"refusing download from non-NVIDIA host: {host}");

            Directory.CreateDirectory(destDir);
            long maxBytes = MaxDownloadBytes();
            using var resp = await http.GetAsync(release.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancelToken);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            if (total is 0) throw new IOException("download is empty (Content-Length 0)");
            if (total is long t)
            {
                if (t > maxBytes) throw new IOException($"download too large ({t} bytes exceeds {maxBytes})");
                EnsureDiskSpace(destDir, t);
                job.TotalMB = (int)Math.Round(t / (1024.0 * 1024.0));
            }
            long denom = total ?? (long)release.SizeMB * 1024 * 1024;

            long read = 0;
            var lastSample = DateTime.UtcNow;
            long lastBytes = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buf = new byte[128 * 1024];
                while (true)
                {
                    // Each read gets a fresh stall window; the job's cancel token is
                    // linked in so a cancel aborts an in-flight read immediately.
                    using var stall = new CancellationTokenSource(StallTimeout);
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelToken, stall.Token);
                    int n = await src.ReadAsync(buf, linked.Token);
                    if (n == 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), cancelToken);
                    read += n;
                    if (read > maxBytes) throw new IOException($"download exceeded max size ({maxBytes} bytes)");
                    job.DoneMB = (int)Math.Round(read / (1024.0 * 1024.0));
                    double prog = denom > 0 ? (double)read / denom : 0;
                    job.Progress = total.HasValue ? Math.Min(1.0, prog) : Math.Min(0.99, prog);
                    var now = DateTime.UtcNow;
                    var dt = (now - lastSample).TotalSeconds;
                    if (dt >= SpeedSampleWindow.TotalSeconds)
                    {
                        job.Speed = $"{((read - lastBytes) / (1024.0 * 1024.0)) / dt:F1} MB/s";
                        lastSample = now;
                        lastBytes = read;
                    }
                }
            } // streams disposed here, before the move

            if (read == 0) throw new IOException("download produced no bytes");
            if (File.Exists(finalPath)) job.LogLine("> Overwriting previous download.", "mut");
            MoveIntoPlace(partPath, finalPath); // streams above are disposed before this
            job.FilePath = finalPath;
            job.DoneMB = (int)Math.Round(read / (1024.0 * 1024.0));
            job.Progress = 1.0;
            job.Status = "done";
        }
        catch (OperationCanceledException) when (cancelCts.IsCancellationRequested)
        {
            TryDelete(partPath);
            job.Status = "cancelled";
            job.Error = "download cancelled";
        }
        catch (Exception ex)
        {
            TryDelete(partPath);
            job.Status = "failed";
            // A cancellation not tied to the job's token is a per-read stall.
            job.Error = ex is OperationCanceledException ? "download stalled" : ex.Message;
        }
        finally
        {
            ActiveDownloads.TryRemove(new KeyValuePair<string, string>(release.Version ?? "", job.Id));
            http.Dispose();
            cancelCts.Dispose();
        }
    }

    // ---- execute (install / silent / extract / package) ---------------------

    public static Job StartExecute(string action, Manifest manifest, PackageSource source,
        Selection selection, string? outputPath, GpuInfo gpu, DownloadArtifact? artifact = null)
    {
        var job = Create(action);
        // Enrich only when the driver came from the live path (GAP-02 real download).
        // A mock/absent artifact leaves the receipt byte-identical to before GAP-04.
        bool liveArtifact = artifact is { Source: "live" } && !string.IsNullOrEmpty(artifact.FilePath);
        var selected = manifest.Components.Where(c => selection.Components.Contains(c.Id)).ToList();
        var deselected = manifest.Components.Where(c => !selection.Components.Contains(c.Id)).ToList();
        bool modified = deselected.Count > 0;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
        var outDir = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine(OutputDir, $"{action}-{manifest.Version}")
            : Path.GetFullPath(outputPath);

        // HARD-02: refuse a filesystem-root outputPath before ANY filesystem write. Otherwise
        // WriteCustomized sprays payload/, manifest.json and install.cmd into the drive root,
        // long before Packages.ZipPathFor's refusal (which stays, as defense in depth) is reached.
        // Fails the job by name rather than throwing: the caller polls a job, and Api.cs would
        // turn an exception into a 400 with no job to poll. All other paths remain allowed — it
        // is the user's disk. The default outDir is never a root.
        if (Packages.IsFilesystemRoot(outDir))
        {
            job.Status = "failed";
            job.Error = Packages.RootOutputPathError;
            job.LogLine($"> ERROR: {Packages.RootOutputPathError}", "err");
            job.Completion = Task.CompletedTask;   // nothing runs; nothing is written
            return job;
        }

        var steps = new List<(int delayMs, Action fn)>();
        var display = selected.FirstOrDefault(c => c.Required);
        var extras = selected.Where(c => !c.Required).Select(c => c.Name).ToList();

        if (action is "install" or "silent")
        {
            if (liveArtifact)
                job.LogLine($"> Using downloaded installer {artifact!.FilePath} " +
                            $"({Math.Round(artifact.SizeBytes / (1024.0 * 1024.0))} MB · {artifact.SizeBytes} bytes).", "mut");
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
            if (selection.TweakOn("driver-telemetry"))
                steps.Add((300, () => job.LogLine(
                    "> Patching driver telemetry endpoints… (simulated — no real patching performed)")));
            if (modified)
                steps.Add((450, () => job.LogLine(
                    "> Rebuilding digital signature… done (simulated — no real signing performed)")));
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
                    // GAP-05: additive + null-omitted honesty markers; `signature` value is
                    // unchanged (back-compat pin) — the rebuild and the driver-telemetry
                    // patch are simulated, never real.
                    signatureSimulated = modified ? (bool?)true : null,
                    driverTelemetrySimulated = selection.TweakOn("driver-telemetry") ? (bool?)true : null,
                    unattended = selection.TweakOn("unattended"),
                    autoReboot = selection.TweakOn("auto-reboot"),
                    cleanInstall = selection.TweakOn("clean-install"),
                    // GAP-04: real-artifact facts, live path only. Null on the mock path,
                    // so WhenWritingNull omits them and mock receipts stay byte-identical.
                    driverFile = liveArtifact ? artifact!.FilePath : null,
                    driverFileBytes = liveArtifact ? artifact!.SizeBytes : (long?)null,
                }, Json.Web));
                job.Receipt = receiptPath;
                job.LogLine("> Writing install receipt… done");
            }));
            steps.Add((200, () => job.LogLine("> Installation finished.", "ok")));
            // FEAT-011 "never reboots": honor the auto-reboot flag in the log only — the
            // reboot is declared and explicitly NOT performed (no OS restart API is called).
            if (selection.TweakOn("auto-reboot"))
                steps.Add((150, () => job.LogLine(
                    "> Automatic reboot allowed — not performed (simulated; no system reboot).", "mut")));
        }
        else // extract | package
        {
            job.OutDir = outDir;
            // D12-F1: the archive is built from this build's explicit output list, never a
            // walk of outDir (which nothing cleans, so it may hold a previous build's
            // leftovers). Each step records the entries it writes; the zip step packs
            // exactly those. Entry names are archive-relative and use '/'.
            var archiveEntries = new List<(string abs, string entry)>();
            // HARD-01: undo a prior CleanDriver build in this directory before writing. Must run
            // before WriteCustomized, which recreates payload/ and overwrites manifest.json —
            // the old manifest IS the delete-list. Manifest-scoped and enumerable-by-name only:
            // foreign files always survive, and a directory that is not ours is never touched.
            steps.Add((200, () =>
            {
                var cleaned = Packages.CleanPreviousBuild(outDir);
                if (cleaned.Count > 0)
                    job.LogLine($"> Cleaning previous CleanDriver build: {string.Join(", ", cleaned)}", "mut");
            }));
            steps.Add((300, () => job.LogLine($"> Extracting package {manifest.Version}…")));
            if (deselected.Count > 0)
                steps.Add((250, () => job.LogLine(
                    $"> Dropping deselected components ({deselected.Count}): {string.Join(", ", deselected.Select(c => c.Name))}", "mut")));
            steps.Add((500, () =>
            {
                var res = Packages.WriteCustomized(source, manifest, selection.Components, outDir, selection.Tweaks);
                job.Modified = res.modified;
                archiveEntries.Add((res.manifestPath, "manifest.json"));
                // res.written holds component ids; the archive entry is the payload filename.
                foreach (var c in selected)
                    archiveEntries.Add((Path.Combine(outDir, "payload", c.Payload), $"payload/{c.Payload}"));
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
                    archiveEntries.Add((f, Path.GetFileName(f)));
                }
                if (written.Count > 0)
                    job.LogLine($"> Writing tweak snippets: {string.Join(", ", written)} (not applied)");
            }));
            if (selection.TweakOn("driver-telemetry"))
                steps.Add((250, () => job.LogLine(
                    "> Patching driver telemetry endpoints… (simulated — no real patching performed)")));
            if (action == "package")
            {
                steps.Add((300, () =>
                {
                    var configPath = Path.Combine(outDir, "config.json");
                    var installPath = Path.Combine(outDir, "install.cmd");
                    File.WriteAllText(configPath, JsonSerializer.Serialize(new
                    {
                        builtBy = "CleanDriver",
                        builtAt = DateTime.UtcNow.ToString("o"),
                        version = manifest.Version,
                        components = selection.Components,
                        tweaks = selection.Tweaks,
                        // GAP-05: additive + null-omitted honesty marker (build-package config).
                        driverTelemetrySimulated = selection.TweakOn("driver-telemetry") ? (bool?)true : null,
                    }, Json.Web));
                    File.WriteAllText(installPath, string.Join("\r\n", new[]
                    {
                        "@echo off",
                        "rem CleanDriver self-contained package installer (simulated)",
                        "echo Installing customized driver package %~dp0 ...",
                        "echo (simulation only — no drivers are installed)",
                    }) + "\r\n");
                    archiveEntries.Add((installPath, "install.cmd"));
                    archiveEntries.Add((configPath, "config.json"));
                    job.LogLine("> Writing install script and config… done");
                }));
                // GAP-06: fold this build's outputs into one redistributable archive,
                // written beside the directory. Must run after config.json/install.cmd and
                // the .reg snippets exist, or the archive would be incomplete. The packing
                // lives in Packages (see WriteZip) — this branch only orchestrates.
                steps.Add((350, () =>
                {
                    // D12-F2: outputPath arrives unnormalized; a trailing separator made
                    // Directory.GetParent return outDir itself, writing the archive inside
                    // the directory it archives. ZipPathFor normalizes and refuses a root.
                    var zipPath = Packages.ZipPathFor(outDir, manifest.Version);
                    Packages.WriteZip(zipPath, archiveEntries);
                    job.LogLine($"> Writing {Path.GetFileName(zipPath)}… done");
                }));
            }
            if (modified)
                steps.Add((400, () => job.LogLine(
                    "> Rebuilding digital signature… done (simulated — no real signing performed)")));
            steps.Add((200, () => job.LogLine(
                $"> {(action == "package" ? "Package build" : "Extraction")} finished.", "ok")));
        }

        // Expose the worker as Completion so callers/tests can await the run deterministically
        // rather than poll (avoids depending on shared static timing state — PR#5 note).
        job.Completion = Task.Run(async () =>
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
