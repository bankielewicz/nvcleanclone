# Closing the app mid-download leaves the partial installer (.part) on disk: download workers are not tied to the host lifetime

| Field | Value |
|---|---|
| **Slug** | `shutdown-mid-download-leaves-part-file` |
| **Severity** | Low (disk residue up to the 2 GiB cap per abandoned version; never executed; overwritten only if that version is downloaded again) |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness); residue after a real process exit confirmed with a console mirror of `Program.Main`'s stop path |
| **Area** | `nvcleanstall/Lib/Jobs.cs` (`StartRealDownload` / `RunRealDownloadAsync` — fire-and-forget worker, no drain seam); wiring: `nvcleanstall/Api.cs` (`Map`) / `nvcleanstall/Program.cs` (`Main`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | None. GAP-02's contract was "On cancel/failure, delete the partial file", and CLAUDE.md says "`.part` → rename; deleted on cancel/failure". GAP-S03's "temporary final name plus atomic move" protects an earlier verified file, but does not remove an abandoned `.part`. Not BUG-05: that is about in-memory store growth, and this is on-disk residue. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R07** — Download engine robustness (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
A real download runs on a detached `Task.Run` worker. Only that worker can delete the `.part` file, from its own `catch` blocks. When the user closes the shell window during a download (on Screen 2, R3 hides Back, so the only exits are Cancel and closing the window), `Program.Main` runs `app.StopAsync().Wait()` and returns. Pressing Ctrl+C on `--headless` stops the host the same way. Nothing cancels or waits for the download worker, so the process exits mid-stream with the worker's cleanup never run. `output/drivers/<version>-<type>.exe.part`, up to 2 GiB, is left on disk, and nothing in the app ever removes it.

## Evidence
- `nvcleanstall/Lib/Jobs.cs:236-239`. The worker is detached, and its cancel source is reachable only per-job via `Cancel(id)`:
  ```csharp
  var cts = new CancellationTokenSource();
  job.Cancellation = cts;
  ActiveDownloads[version] = job.Id;
  job.Completion = Task.Run(() => RunRealDownloadAsync(job, release, destDir, http, cts));
  ```
- `nvcleanstall/Lib/Jobs.cs:352-364`. The only `.part` cleanup lives inside that worker:
  ```csharp
  catch (OperationCanceledException) when (cancelCts.IsCancellationRequested)
  {
      TryDelete(partPath);
      …
  catch (Exception ex)
  {
      TryDelete(partPath);
  ```
- `nvcleanstall/Program.cs:45-53`. The native-shell exit stops the host and returns, which ends the process:
  ```csharp
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
  ```
  `Program.cs:38-41` (`--headless`) uses `app.Run()`, which stops the same way on Ctrl+C.
- Nothing subscribes to the host lifetime. `grep -n "ApplicationStopping\|Lifetime" nvcleanstall/**/*.cs` finds no matches, and `Jobs` exposes no way to cancel or await all in-flight downloads.

The faulty reasoning is that GAP-02 treated "cancel" as only the Cancel button (`/api/download/{id}/cancel`). Closing the app is a cancel too, and it is the one path the engine never sees.

## Impact
- Each download abandoned by closing the app leaves a partial installer in `output/drivers/`, up to `MaxDownloadBytes()` (2 GiB default, ~1 GB for a real package). The residue is only replaced if the user later downloads **the same version**. Other versions accumulate.
- It contradicts the documented invariant (CLAUDE.md; GAP-02 behavior) that a partial file never survives a download that did not complete.
- It is not a safety violation. The file keeps its `.part` extension, is never executed, and `DownloadedFile` ignores it because no job has a `FilePath` for it. But it is an unlabelled, half-written NVIDIA installer that the user may find and run by hand.
- How likely: high whenever a user abandons a slow download by closing the window, which is the natural gesture when Back is hidden (R3).

## How to reproduce
### A. Automated — failing xunit test
New file `nvcleanstall/CleanDriver.Tests/HostLifetimeTests.cs`. The test drives the real route-table wiring (`Api.Map`) through a real host start and stop, which is exactly what `Program.Main` does. A drain is process-wide, so the class runs in a **non-parallel collection**, or it would cancel other classes' in-flight test downloads once fixed.

```csharp
using System;
using System.IO;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CleanDriver.Tests;

[CollectionDefinition("HostLifetime", DisableParallelization = true)]
public class HostLifetimeCollection { }

[Collection("HostLifetime")]
public class HostLifetimeTests
{
    [Fact]
    public async Task HostStop_MidRealDownload_CancelsJobAndDeletesPart()
    {
        var dir = TempDir.Create();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        Api.Map(app, new MockCatalogProvider()); // the production route table / wiring
        await app.StartAsync();

        var job = Jobs.StartRealDownload(LiveRelease.New(version: "612.01"), dir,
            DownloadHandler.Stream(new ScriptedStream(prefixLen: 256 * 1024, blockAfterPrefix: true)));
        var part = Path.Combine(dir, "612.01-whql.exe.part");
        try
        {
            for (int i = 0; i < 100 && !File.Exists(part); i++) await Task.Delay(20);
            Assert.True(File.Exists(part), "precondition: download is mid-stream");

            await app.StopAsync(); // exactly what Program.Main does on window close
            await app.DisposeAsync();

            // After a graceful stop returns the process is about to exit: the download must
            // already be terminal and its partial file gone — nothing runs after this point.
            Assert.Equal("cancelled", job.Status);
            Assert.False(File.Exists(part), ".part survives a graceful stop");
        }
        finally
        {
            Jobs.Cancel(job.Id);
            await job.Completion!;
        }
    }
}
```

Run (real repo):
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~HostLifetimeTests"
```

Captured RED on unmodified HEAD (Linux net10.0 harness, class `ShutdownMidDownloadLeavesPartFileRepro`, same body):
```
Failed ...HostStop_MidRealDownload_CancelsJobAndDeletesPart [178 ms]
  Error Message:
   Assert.Equal() Failure: Strings differ
           ↓ (pos 0)
Expected: "cancelled"
Actual:   "running"
           ↑ (pos 0)
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
```

### B. Live — running app (if applicable)
Partly applicable. `--mock-catalog` never takes the real-download path, and a live repro needs NVIDIA's CDN, which is outside this audit's localhost-only rule. Two substitutes:
1. **Process-exit mirror (run, Linux).** A console app compiles the real `Lib/*.cs` and `Api.cs` and mirrors `Program.Main`'s shell path: `app.Start()`, a mid-stream `StartRealDownload` with the suite's `ScriptedStream`, then `finally { app.StopAsync().Wait(); }` and return. Output:
   ```
   mid-download: job 1 status=running doneMB=0 part=262144 bytes
   user closes the window -> Application.Run returns
   app.StopAsync() returned; Main returns; process exits
   exit=0
   --- after process exit:
   -rw-r--r-- 1 root root 262144 Nov 23 12:46 612.02-whql.exe.part
   ```
2. **Windows manual procedure (owner/reviewer).** Run `dotnet run --project nvcleanstall` on the live catalog, pick the latest version and press Next. While Screen 2 shows progress, close the window. Then `dir nvcleanstall\bin\Debug\net10.0-windows\output\drivers`. **Expected:** no `*.part`. **Actual on HEAD:** `<version>-whql.exe.part` with the bytes received so far. Headless: `dotnet run --project nvcleanstall -- --headless`, `curl -s -X POST localhost:4780/api/download -H 'Content-Type: application/json' -d '{"version":"<latest>"}'`, Ctrl+C mid-download, and check the same folder.

## Root cause
The engine's invariant "a `.part` never outlives its job" is enforced only from inside the worker, and the worker is detached from the host. A graceful host stop is not a cancel the engine can observe: `Jobs` has no drain seam, and nothing registers on `IHostApplicationLifetime.ApplicationStopping`. The process therefore ends while the worker is still inside its read loop.

## Remedy
Add a drain seam to `Jobs`, and register it once on the host's stopping event in `Api.Map`. That location covers both `Program.Main` paths and is reachable from tests.

```diff
 // Jobs.cs
+    // Host-stop drain: cancel every in-flight real download and wait, bounded, for each
+    // worker's own cleanup (TryDelete of its .part) to run before the process exits.
+    public static void CancelActiveDownloads(TimeSpan grace)
+    {
+        var running = Store.Values
+            .Where(j => j.Cancellation != null && j.Status == "running" && j.Completion != null)
+            .ToList();
+        foreach (var j in running) Cancel(j.Id);
+        try { Task.WaitAll(running.Select(j => j.Completion!).ToArray(), grace); }
+        catch (AggregateException) { /* a worker's failure is recorded on its job */ }
+    }
```
```diff
 // Api.cs
     public static void Map(WebApplication app, ICatalogProvider catalog)
     {
+        app.Lifetime.ApplicationStopping.Register(() => Jobs.CancelActiveDownloads(TimeSpan.FromSeconds(5)));
```
- **Guard-test trap, verified:** the first draft of this patch had the comment "Graceful-shutdown drain", and it **failed** `NoRebootGuardTests.JobsEngine_ContainsNoRebootOrShutdownApi`. That safety pin forbids the token `shutdown` anywhere in `Jobs.cs`, case-insensitive, comments included. Name and comment the seam without that word ("host stop", "drain"). Do not edit the pin.
- **Validated:** with the wording above, on scratch copies of `Jobs.cs`/`Api.cs`, the red turns green and the full suite stays green (163/163, Linux harness).
- **Why minimal:** it reuses `Cancel(id)` and the worker's existing cancel path, which already does `TryDelete(partPath)` → `"cancelled"`. No new deletion code. It is one registration, at the single place both entry points share.
- **Alternatives rejected:**
  - **A startup sweep of `output/drivers/*.part`:** that is a glob-based deletion, which breaks the deletion fence (D12/D1: enumerable-by-name, manifest-declared artifacts only). It would need its own owner ruling.
  - **Wiring only in `Program.cs`'s `finally`:** that misses `--headless` (`app.Run()`), and `Program.Main` is not testable.
  - **Waiting unboundedly:** a wedged download could hang app exit (see `download-headers-phase-has-no-stall-timeout`), so keep the grace bounded.
- **Known limit, documented:** a hard kill, crash or power loss still leaves a `.part`. Only a future, owner-ruled cleanup could address that.
- **Fences:** safety untouched. Deletion is still only the worker's own `TryDelete` of its own named `.part`. No UI change. Simulated jobs have no CTS, so they are untouched and the mock-path byte-identity pin holds. Pins that must stay green unedited: `RealDownloadTests.*` (especially `Cancel_AfterJobCompleted_MustNotThrow`), `NoRebootGuardTests`, `NoExecutionGuardTests`.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** The active register is `docs/security_register.md`, and no item covers this. The owner pins a new finding ID first. Destination: Wave C, most naturally **fold into GAP-C02** ("Correct artifact identity, receipts, and concurrent jobs"), which owns download-job lifecycle correctness (atomic same-version creation). Otherwise it is a standalone new entry. Per §7 it needs its own ID and AC.
   > *Pinned 2026-11-23 as **GAP-R07** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-host-drain -b feat/download-host-stop-drain origin/main`
2. **Red.** Add `HostLifetimeTests.cs` (§A). Run `--filter "FullyQualifiedName~HostLifetimeTests"` and confirm `Expected: "cancelled" / Actual: "running"`, which is not a compile error. Commit `"<ID> (red): host stop cancels in-flight downloads and removes .part"`.
3. **Green.** Add `Jobs.CancelActiveDownloads` and the `Api.Map` registration. Grep `Jobs.cs` for `shutdown`, which must return nothing. Run the full suite. Commit `"<ID>: drain real downloads on host stop"`.
4. **Refactor.** Only with the suite green.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run the §B.2 Windows procedure, both native-window close and headless Ctrl+C. Show the `output\drivers` listing with no `.part` after exit, and timestamps showing the app exited within the grace period.
7. **PR.** Fill `.github/pull_request_template.md`. TDD row: host stop → `cancelled` + no `.part`. No existing test is edited. Deviations: "touches Api.cs for the one-line lifetime registration", and "hard-kill residue out of scope". Paste the live transcript, push, and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green before reading the diff.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-host-drain origin/feat/download-host-stop-drain`. Review range `origin/main...HEAD`.
3. **Re-run gates** on the reviewer's checkout.
4. **Finder pass.** Check each of these:
   - The registration happens once per app, and the wait is **bounded**.
   - Only jobs with a CTS are touched (real downloads), never simulated ones.
   - The drain reuses the worker's own cleanup. No new `File.Delete` or glob delete is added, which would break the deletion fence.
   - `Jobs.cs` contains no `shutdown` / `System.Diagnostics` / `Process.Start` tokens (guard pins).
   - A late `Cancel` on a finished job is still a no-op (`ObjectDisposedException` caught).
   - The test is in a non-parallel collection, so the process-wide drain cannot cancel other classes' downloads.
5. **Adversarial verify.** Throw these at the fix:
   - Stop while a download is inside `MoveIntoPlace`. It must end `done`, with no lost final file.
   - Stop with two concurrent versions downloading. Both end `cancelled` and both `.part` files are gone.
   - Stop while the download is wedged pre-headers on HEAD's unbounded wait. Exit must still happen within the grace period, because cancel reaches `GetAsync`.
   - Stop with only simulated jobs running. No effect, no delay.
   - Repeated `StartAsync`/`StopAsync` in one process does not double-register in a way that throws.
6. **Verdict document.** APPROVE or REQUEST CHANGES, each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** On REQUEST CHANGES, write an implementer handoff. The fix loop runs: C0 Orient → C1 reproduce the red → C2 fix → C3 verify the ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relay. The owner merges.

## Acceptance criteria
1. After a graceful host stop (`app.StopAsync()` returning) during an in-flight real download, the job is `cancelled` and its `.part` no longer exists.
2. The stop completes within a bounded grace period even if a download is unresponsive.
3. Simulated (mock-path) downloads and finished jobs are unaffected by the drain.
4. `Jobs.cs` still passes `NoRebootGuardTests` and `NoExecutionGuardTests` unedited, and no new deletion primitive is introduced.
5. Windows evidence: closing the native window and pressing Ctrl+C on `--headless` mid-download each leave no `*.part` in `output\drivers`.
