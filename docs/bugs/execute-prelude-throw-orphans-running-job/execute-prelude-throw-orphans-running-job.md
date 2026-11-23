# StartExecute registers its job before a throwable prelude: a bad request returns HTTP 500 and leaves a job stuck in "running" forever

| Field | Value |
|---|---|
| **Slug** | `execute-prelude-throw-orphans-running-job` |
| **Severity** | Low (bounded robustness: direct-API inputs only; leaks a never-terminal job per request; no file is written) |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness); also confirmed live (headless `--mock-catalog`) |
| **Area** | `nvcleanstall/Lib/Jobs.cs` (`StartExecute`); contributing: `nvcleanstall/Api.cs` (`/api/execute` catch filter) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | **Sibling doc `docs/bugs/execute-malformed-body-500`** (Api.cs, by the Api-area hunter) shows the same HTTP symptom. That doc fixes the **route**: it validates the token, null collections and `outputPath` before calling `StartExecute`. It explicitly files "move `Create()` after validation inside `Jobs.StartExecute`" as an out-of-area lead for Jobs.cs, and this doc is that Jobs.cs half. The two are complementary, not duplicates: the route fix stops HTTP callers, and this fix makes `StartExecute` total, so no caller can publish a never-terminal job. That covers in-process callers, tests and future routes, and failure types the route probe does not anticipate. Recommend **one register row with two ACs**, with the route fix as primary and this as the backstop. Also adjacent to **BUG-01 / GAP-C01**, which should reject null lists before `Create`. Interacts with **BUG-05 / GAP-S02**: AC-4's "eviction never removes a running job" makes orphans unevictable. Follows the convention HARD-02 set ("fails the job by name rather than throwing"). |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R05** — API input contract: malformed requests get a JSON 400, never a bodiless 500 or a zombie job (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`StartExecute` calls `Create(action)` first, which stores a job with `Status = "running"`. Only after that does it run a synchronous prelude that can throw: `Path.GetFullPath(outputPath)`, the `selection.Components` partition, and synchronous `selection.TweakOn(...)` calls. When the prelude throws, the exception escapes `StartExecute`. `/api/execute` only catches `IOException` and `InvalidDataException`, so an `ArgumentException` or `NullReferenceException` becomes an empty **HTTP 500**. The job already in the store is never given a worker (`Completion == null`), so it stays `running` forever and never logs, fails or finishes.

## Evidence
- `nvcleanstall/Lib/Jobs.cs:375-388`. The job is registered, then the prelude runs:
  ```csharp
  public static Job StartExecute(string action, Manifest manifest, PackageSource source,
      Selection selection, string? outputPath, GpuInfo gpu, DownloadArtifact? artifact = null)
  {
      var job = Create(action);                                   // stored, Status "running"
      …
      var selected = manifest.Components.Where(c => selection.Components.Contains(c.Id)).ToList();  // NRE if null
      …
      var outDir = string.IsNullOrWhiteSpace(outputPath)
          ? Path.Combine(OutputDir, $"{action}-{manifest.Version}")
          : Path.GetFullPath(outputPath);                          // ArgumentException on NUL
  ```
  Line 414 (`selection.TweakOn("unattended")`) and line 522 (`selection.TweakOn("driver-telemetry")`) also run synchronously, and throw `NullReferenceException` when `Tweaks` is null.
- `nvcleanstall/Lib/Jobs.cs:390-403`. The HARD-02 block shows the intended contract for a bad `outputPath`, which is to fail the job by name and return it:
  ```csharp
  // Fails the job by name rather than throwing: the caller polls a job, and Api.cs would
  // turn an exception into a 400 with no job to poll.
  ```
  That block only runs if `GetFullPath` already succeeded.
- `nvcleanstall/Lib/Jobs.cs:73-78`. `Create` stores the job immediately:
  ```csharp
  var job = new Job { Id = Interlocked.Increment(ref _nextId).ToString(), Type = type };
  Store[job.Id] = job;
  ```
- `nvcleanstall/Api.cs:112`. The catch filter does not cover these exceptions:
  ```csharp
  catch (Exception ex) when (ex is IOException or InvalidDataException)
  ```
- Deserialization makes the null lists reachable. `Selection.Components`/`Tweaks` default to empty collections, but System.Text.Json assigns an explicit JSON `null` as-is, because nullable annotations are not enforced.

## Impact
- A direct API caller gets `HTTP/1.1 500`, `Content-Length: 0`, with no error text. Examples: scripted headless verification, a future preset/automation client, or a malformed request.
- One phantom `running` job leaks per such request. It is reachable at the next sequential id via `/api/jobs/{id}`, and any poller would spin on it forever. Under GAP-S02's planned cap rule ("eviction never removes a running job"), these become permanently unevictable.
- The UI cannot normally send a NUL or a null list, which is why this is Low. On Windows `Path.GetFullPath` rejects embedded NUL the same way, and the null-list case is platform-independent.

## How to reproduce
### A. Automated — failing xunit test
New file `nvcleanstall/CleanDriver.Tests/ExecutePreludeTests.cs`. It is hermetic: no receipt is written, because every call fails before any step runs.

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

public class ExecutePreludeTests
{
    private static Manifest M() => new()
    {
        Version = "572.16", Channel = "WHQL",
        Components = new List<ComponentDef>
        {
            new() { Id = "display", Name = "Display Driver", SizeMB = 500, Required = true, Payload = "display.bin" },
        },
    };
    private static PackageSource Src() => new() { Kind = "catalog", Dir = "unused" };
    private static GpuInfo G() => new() { Name = "GeForce RTX 5070", InstalledDriverVersion = "591.86" };
    private static Selection Sel() => new()
    {
        Components = new List<string> { "display" }, Tweaks = new Dictionary<string, JsonElement>(),
    };

    // HARD-02 convention: a bad outputPath fails the job by name; it never throws.
    [Fact]
    public async Task StartExecute_UnresolvableOutputPath_FailsJobInsteadOfThrowing()
    {
        Job? job = null;
        var ex = Record.Exception(() => job = Jobs.StartExecute("extract", M(), Src(), Sel(), "out\0dir", G()));

        Assert.Null(ex);
        await job!.Completion!;
        Assert.Equal("failed", job.Status);
        Assert.False(string.IsNullOrEmpty(job.Error));
    }

    // JSON "components": null deserializes to null and throws from the synchronous partition.
    [Fact]
    public void StartExecute_NullComponentList_DoesNotThrow()
    {
        var sel = new Selection { Components = null!, Tweaks = new Dictionary<string, JsonElement>() };
        var ex = Record.Exception(() => Jobs.StartExecute("install", M(), Src(), sel, null, G()));
        Assert.Null(ex);
    }

    // The consequence: no job may be left "running" with no worker.
    [Fact]
    public void StartExecute_PreludeThrow_LeavesNoNeverTerminalJob()
    {
        var before = int.Parse(Jobs.StartDownload(new Release { Version = "000.01", SizeMB = 1 }).Id);
        try { Jobs.StartExecute("extract", M(), Src(), Sel(), "out\0dir", G()); } catch { /* asserted above */ }
        var after = int.Parse(Jobs.StartDownload(new Release { Version = "000.02", SizeMB = 1 }).Id);

        Thread.Sleep(200); // any legitimately-started job has long since been handed its worker
        var orphans = Enumerable.Range(before + 1, after - before - 1)
            .Select(i => Jobs.Get(i.ToString()))
            .Where(j => j is { Type: "extract", Status: "running", Completion: null })
            .Select(j => j!.Id)
            .ToList();

        Assert.Empty(orphans);
    }
}
```

Run (real repo):
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~ExecutePreludeTests"
```

Captured RED on unmodified HEAD (Linux net10.0 harness, class `ExecutePreludeThrowOrphansRunningJobRepro`, same bodies):
```
Failed ...StartExecute_PreludeThrow_LeavesNoNeverTerminalJob [226 ms]
  Error Message:
   Assert.Empty() Failure: Collection was not empty
Collection: ["2"]
Failed ...StartExecute_UnresolvableOutputPath_FailsJobInsteadOfThrowing [2 ms]
  Error Message:
   Assert.Null() Failure: Value is not null
Expected: null
Actual:   System.ArgumentException: Null character in path. (Parameter 'path')
Failed ...StartExecute_NullComponentList_DoesNotThrow [1 ms]
  Error Message:
   Assert.Null() Failure: Value is not null
Expected: null
Actual:   System.NullReferenceException: Object reference not set to an instance of an object.
Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3
```

### B. Live — running app (if applicable)
Against `dotnet run --project nvcleanstall -- --headless --mock-catalog` (port 4780). The transcript below was captured on the Linux mirror, port 4801, fresh process.
1. `curl -s -X POST localhost:4780/api/package -H 'Content-Type: application/json' -d '{"kind":"catalog","version":"572.16"}'` → `"token": "1"`.
2. Probe the id counter: `curl -s -X POST localhost:4780/api/download -H 'Content-Type: application/json' -d '{"version":"572.16"}'` → `{"jobId":"1"}`.
3. `curl -s -i -X POST localhost:4780/api/execute -H 'Content-Type: application/json' -d '{"token":"1","action":"extract","selection":{"components":["display"]},"outputPath":"out\u0000dir"}'`
   **Expected:** 200 with a jobId that ends `failed` and carries a named error, or a 400 with an error body. **Actual:** `HTTP/1.1 500 Internal Server Error`, `Content-Length: 0`.
4. `curl -s -i -X POST localhost:4780/api/execute -H 'Content-Type: application/json' -d '{"token":"1","action":"install","selection":{"components":null}}'` → `HTTP/1.1 500`, `Content-Length: 0`.
5. Probe again: `/api/download` → `{"jobId":"4"}`, so ids 2 and 3 were consumed by the failed calls.
6. `curl -s localhost:4780/api/jobs/2` and `/api/jobs/3`, then again 11 s later:
   ```
   {"id":"2","type":"extract","status":"running","progress":0,…,"log":[]}
   {"id":"3","type":"install","status":"running","progress":0,…,"log":[]}
   ```
   Both stay `running`, with an empty log, indefinitely.

## Root cause
`StartExecute` breaks its own ordering invariant: a job may only be published to the store once it is either handed a worker or failed by name. `Create(action)` publishes first, and the synchronous request-derived work that can still throw runs afterwards. The HARD-02 guard closed this for one *value* (a root path) but not for the *class* of prelude failures. Api.cs's narrow filter turns the escape into a bare 500.

## Remedy
Keep the publish-first shape, which HARD-02 relies on to return a failed job, but make the prelude total. Any exception before the worker is assigned fails the published job by name:

```diff
+    public const string InvalidExecuteRequestError = "invalid outputPath or selection";
+
     public static Job StartExecute(string action, Manifest manifest, PackageSource source,
         Selection selection, string? outputPath, GpuInfo gpu, DownloadArtifact? artifact = null)
     {
         var job = Create(action);
+        try { return StartExecuteCore(job, action, manifest, source, selection, outputPath, gpu, artifact); }
+        catch (Exception ex) when (job.Completion is null)   // only prelude failures; the worker owns its own
+        {
+            job.Status = "failed";
+            job.Error = InvalidExecuteRequestError;
+            job.LogLine($"> ERROR: {InvalidExecuteRequestError} ({ex.GetType().Name})", "err");
+            job.Completion = Task.CompletedTask;
+            return job;
+        }
+    }
+
+    private static Job StartExecuteCore(Job job, string action, Manifest manifest, PackageSource source,
+        Selection selection, string? outputPath, GpuInfo gpu, DownloadArtifact? artifact)
+    {
-        var job = Create(action);
         // … existing body unchanged …
```
- **Why minimal:** no behavior change for any request that works today. The HARD-02 root path still fails with `RootOutputPathError`, unchanged, because it returns before any throw. The route needs no change, because a failed job is pollable and the UI's `pollRun` already shows the `> ERROR:` line and stops.
- **Validated:** on a scratch copy of `Jobs.cs`, all three reds turn green and the full suite stays green (163/163, Linux harness).
- **Alternatives rejected:**
  - Widening Api.cs's catch to `ArgumentException`/`NullReferenceException`: it still orphans the job, because it was already published.
  - Moving `Create` after the prelude and throwing `InvalidDataException` (400): this contradicts HARD-02's documented "fail the job by name" contract, and puts a null-list rejection that belongs to GAP-C01 here.
  - Silently treating a null list as empty would install nothing and report success, which is dishonest.

  GAP-C01 should still reject null or invalid selections with a named error *before* `Create`; this fix is the backstop that keeps `StartExecute` total.
- **Fences:** no write happens on the failure path. No deletion. No UI or ruling change, since the error renders in the existing run log, as HARD-02's does. Pins that must stay green unedited: `OutDirHygieneTests` (HARD-02 root cases), `ExecuteReceiptTests.*` and `PackageZipTests.*`. The guard scans (`NoExecutionGuardTests`, `NoRebootGuardTests`) must not see new forbidden tokens.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** The active register is `docs/security_register.md`, and no item covers this. The owner pins a new finding ID first. Destination: **fold into GAP-C01** ("Enforce selections on the server"). That slice already validates the execute request before job creation, and its AC "invalid requests create no job or files" is exactly the orphan property. Per §7 it needs its own ID and AC row, because the `outputPath` trigger is not a selection rule.
   > *Pinned 2026-11-23 as **GAP-R05** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** `git worktree add ../nvcleanclone-exec-prelude -b feat/execute-prelude-total origin/main`
2. **Red.** Add `ExecutePreludeTests.cs` (§A). Run `--filter "FullyQualifiedName~ExecutePreludeTests"` and confirm the `ArgumentException`, `NullReferenceException` and orphan-id reds. Commit `"<ID> (red): StartExecute never throws and never orphans a running job"`.
3. **Green.** Split `StartExecute` into the publish+guard wrapper and `StartExecuteCore`, and run the full suite. Commit `"<ID>: fail prelude errors by name instead of throwing"`.
4. **Refactor.** Optionally lift the HARD-02 root check's `failed`/`Error`/`LogLine`/`Completion` quartet and the new guard into one `FailByName(job, error)` helper, with the suite green.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Repeat §B steps 1–6. Expected: steps 3 and 4 return `200 {"jobId":…}`, `/api/jobs/<id>` shows `"status":"failed","error":"invalid outputPath or selection"` with an `> ERROR:` log line, and no job stays `running`. Then a valid extract into a temp dir still ends `done`.
7. **PR.** Fill `.github/pull_request_template.md`. TDD rows: NUL `outputPath`, null components, no orphan. No existing test is edited. Deviation: "prelude failures are generalized beyond HARD-02's root case". Paste the live transcript, push, and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green before reading the diff.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-exec-prelude origin/feat/execute-prelude-total`. Review range `origin/main...HEAD`.
3. **Re-run gates** on the reviewer's checkout.
4. **Finder pass.** Check each of these:
   - **Every** synchronous throw site before `job.Completion = Task.Run(...)` is inside the guard: `GetFullPath`, `IsFilesystemRoot` (which calls `GetFullPath` again), the `Where/ToList` partitions, and the synchronous `TweakOn` calls in both branches.
   - The `when (job.Completion is null)` filter (or equivalent) means a worker-side failure is never double-handled.
   - The HARD-02 root path still yields exactly `RootOutputPathError`.
   - No file or directory is created on the failure path.
   - The error text does not echo the raw `outputPath`. It may contain control characters, and the route already avoids reflecting paths.
5. **Adversarial verify.** Throw these at the fix:
   - `outputPath` of `"\u0000"` and `"a\u0000"`.
   - On Windows, a path over 32 767 chars. Check whether `GetFullPath` throws `PathTooLongException` there; this was not verified from Linux. That type is an `IOException`, so on HEAD Api.cs would answer 400 *and still* orphan the published job. The guard must cover it too.
   - `"tweaks": null` on install, extract and package.
   - `"selection": null`. This is already defaulted by Api.cs, so confirm it still works.
   - A valid request immediately after a failed one gets a fresh id and ends `done`.
   - Scan `Jobs.Get` over a range of ids after 50 bad requests: zero `running` jobs with `Completion == null`.
6. **Verdict document.** APPROVE or REQUEST CHANGES, each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** On REQUEST CHANGES, write an implementer handoff. The fix loop runs: C0 Orient → C1 reproduce the red → C2 fix → C3 verify the ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relay. The owner merges.

## Acceptance criteria
1. `Jobs.StartExecute` never throws for any `outputPath` string or any `Selection` whose lists are null. It always returns a job.
2. Such a job is terminal on return: `Status == "failed"`, a stable named `Error` with no path echo, an `> ERROR:` log line, and `Completion` completed.
3. After any number of rejected execute requests, no job in the store is `running` with `Completion == null`.
4. `/api/execute` no longer returns an empty 500 for these inputs.
5. HARD-02's root-path behavior (`RootOutputPathError`) and all existing execute, receipt and zip pins are unchanged.
