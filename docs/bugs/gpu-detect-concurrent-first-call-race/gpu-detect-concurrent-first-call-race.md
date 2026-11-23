# Gpu.Detect() cache races on the wizard's concurrent boot calls, running two WMI queries

| Field | Value |
|---|---|
| **Slug** | `gpu-detect-concurrent-first-call-race` |
| **Severity** | Low (bounded: duplicate process launch on every boot, plus a timing-dependent inconsistent GPU record) |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness, deterministic 5/5). Also confirmed live on the Linux headless harness: the wizard's three boot requests launch `powershell` twice. The timeout-ordering consequence (§Impact 2) is reasoned, not dynamically reproduced. |
| **Area** | `nvcleanstall/Lib/Gpu.cs` (`Detect`, `_cached`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both (a plain check-then-act race; on Windows the duplicated process is real `powershell.exe`) |
| **Related register entries** | Distinct from SEC-06/GAP-C04, which fix *which* executable is launched; this is *how many times* it is launched and *which result wins*. Distinct from HARD-03, which bounded a single query's time. The fix keeps both of those intact. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R08** — Catalog and GPU detection correctness (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`Gpu.Detect()` caches its result with an unsynchronized check-then-set. The wizard's boot (`app.js:527-529`) fires `/api/system` and `/api/catalog` together with `Promise.all`, and both routes call `Gpu.Detect()`. Each first call therefore sees `_cached == null` and launches its own PowerShell/WMI query. Each route then answers from its own query, and `_cached` keeps whichever finished last. The docs say "`Detect()` caches, so the first `/api/system` or `/api/catalog` call pays the query". In practice, both calls pay.

## Evidence
- `nvcleanstall/Lib/Gpu.cs:16` and `:39-44`:
  ```csharp
  private static GpuInfo? _cached;
  ...
  public static GpuInfo Detect()
  {
      if (_cached != null) return _cached;
      _cached = DetectFrom(QueryWmi());   // ~1–5 s: powershell start + CIM query
      return _cached;
  }
  ```
  There is no lock, no `Lazy`, and no `Interlocked`. The window between the check and the set is the whole PowerShell query, up to `WmiBudget` (5 s).
- Both boot routes call it (`nvcleanstall/Api.cs`):
  - `:22` `app.MapGet("/api/system", () => Results.Json(Gpu.Detect(), Json.Web));`
  - `:28` `Results.Json(CatalogEndpoint.Build(catalog, Gpu.Detect()), Json.Web));`
- The boot fires them together (`nvcleanstall/wwwroot/app.js:527-529`):
  ```js
  const [sys, cat, tw] = await Promise.all([
    api.get('/api/system'), api.get('/api/catalog'), api.get('/api/tweaks'),
  ]);
  ```
  Kestrel serves the two requests on separate thread-pool threads.
- Later requests trust the cache: `/api/download` at `Api.cs:81` (`catalog.GetReleases(Gpu.Detect())`) and `/api/execute` at `Api.cs:109`.

## Impact
1. **Every cold launch runs the WMI query twice.**
   - Two `powershell -NoProfile -Command Get-CimInstance …` processes start at the same moment, doubling first-screen CPU and disk work.
   - On a slow or cold machine the two compete for the same resources. That makes it more likely that the shared 5 s `WmiBudget` is exceeded, which is the failure HARD-03 exists to bound.
   - This was observed live (§B).
2. **Inconsistent GPU record (reasoned).** Each route returns its own query's result. Suppose one query times out: `ReadBounded` returns null and `DetectFrom` maps that to `Simulated`, but only after the full 5 s. The other query succeeds faster. Then:
   - Screen 1 and the catalog disagree. For example, `/api/catalog` built a live list for the real GPU while `/api/system` shows "simulated device".
   - The timed-out call finishes **last** (at the budget), so `_cached` is permanently overwritten with `Simulated`.
   - Every later `/api/download` re-resolves the catalog with the simulated GPU (`Api.cs:81` → `NvidiaCatalogProvider.GetCatalog` → `GpuNotMatched` → mock). The live version the user picked then fails with `404 "unknown version"`.
   - Not dynamically reproduced: it needs a query that times out next to one that does not.
3. **Scope.** All users, on every launch (the double launch is deterministic). The inconsistency needs a near-budget WMI query. Nothing unsafe is executed.

## How to reproduce
### A. Automated — failing xunit test
**Linux harness red (HEAD code, unmodified).** On Unix, `Process.Start("powershell")` resolves through `PATH`, so a PATH-first stand-in can count launches. The code under test is HEAD's real `Detect()` → `QueryWmi()` → `ReadBounded()`; nothing is stubbed in-process. This file is `repro/gpu-detect-concurrent-first-call-race.cs`, class `GpuDetectConcurrentFirstCallRaceRepro`:

```csharp
public class GpuDetectConcurrentFirstCallRaceRepro
{
    private static readonly FieldInfo Cache =
        typeof(Gpu).GetField("_cached", BindingFlags.NonPublic | BindingFlags.Static)!;

    [Fact]
    public void Detect_TwoConcurrentFirstCalls_RunOneWmiQuery_AndAgree()
    {
        if (OperatingSystem.IsWindows()) return; // CreateProcess searches System32 before PATH
        var dir = TempDir.Create();
        var log = Path.Combine(dir, "launches.log");
        var shim = Path.Combine(dir, "powershell");
        File.WriteAllText(shim,
            "#!/bin/sh\n" +
            $"echo launched >> '{log}'\n" +
            "sleep 1\n" +
            "echo 'NVIDIA GeForce RTX 4070|32.0.15.7086'\n");
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldCache = Cache.GetValue(null);
        Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + oldPath);
        Cache.SetValue(null, null); // a fresh process: nothing detected yet
        try
        {
            using var gate = new Barrier(2);
            GpuInfo Call() { gate.SignalAndWait(); return Gpu.Detect(); }
            var a = Task.Factory.StartNew(Call, TaskCreationOptions.LongRunning); // ~ /api/system
            var b = Task.Factory.StartNew(Call, TaskCreationOptions.LongRunning); // ~ /api/catalog
            Task.WaitAll(a, b);

            var launches = File.Exists(log) ? File.ReadAllLines(log).Length : 0;
            Assert.Equal("GeForce RTX 4070", a.Result.Name);   // the stand-in really answered
            Assert.Equal(1, launches);                           // one query per process lifetime
            Assert.Same(a.Result, b.Result);                     // both routes see one cached GpuInfo
        }
        finally
        {
            Cache.SetValue(null, oldCache);
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }
}
```

Captured RED output (Linux harness, HEAD `54b3c31`; same result on 5 of 5 runs):
```
[xUnit.net 00:00:01.34]     CleanDriver.Tests.GpuDetectConcurrentFirstCallRaceRepro.Detect_TwoConcurrentFirstCalls_RunOneWmiQuery_AndAgree [FAIL]
   Assert.Equal() Failure: Values differ
Expected: 1
Actual:   2
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 1 s
```

**Repo-suite test (what lands in `nvcleanstall/CleanDriver.Tests/GpuDetectCacheTests.cs`).** The repo's CI is `windows-latest`, where a PATH stand-in is never reached: CreateProcess searches System32 before `PATH`, which is the same reason GAP-C04 exists. So the committed test drives the cache through a delegate seam, following the `ReadBounded` idiom where the process is reached only through delegates:

```csharp
using System.Threading;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// Gpu.Detect() is reached by /api/system AND /api/catalog, which app.js's boot fires
// together (Promise.all). The cache must collapse concurrent first calls into ONE WMI
// query, and every caller must see the same GpuInfo. Driven through the Memoize seam so no
// real powershell runs (the GpuTimeoutTests idiom: the process is reached only via delegates).
public class GpuDetectCacheTests
{
    private const string Wmi = "NVIDIA GeForce RTX 4070|32.0.15.7086";

    [Fact]
    public void Memoize_ConcurrentFirstCalls_RunOneQuery_AndShareOneResult()
    {
        int queries = 0;
        using var bothInside = new Barrier(2);
        var detect = Gpu.Memoize(() =>
        {
            Interlocked.Increment(ref queries);
            Thread.Sleep(500); // a WMI query takes real time; widen the race window
            return Wmi;
        });

        GpuInfo Call() { bothInside.SignalAndWait(); return detect(); }
        var a = Task.Factory.StartNew(Call, TaskCreationOptions.LongRunning); // ~ /api/system
        var b = Task.Factory.StartNew(Call, TaskCreationOptions.LongRunning); // ~ /api/catalog
        Task.WaitAll(a, b);

        Assert.Equal(1, queries);
        Assert.Same(a.Result, b.Result);
        Assert.Equal("GeForce RTX 4070", a.Result.Name);
    }

    [Fact]
    public void Memoize_LaterCalls_NeverRequery()
    {
        int queries = 0;
        var detect = Gpu.Memoize(() => { queries++; return Wmi; });

        var first = detect();
        var second = detect();

        Assert.Equal(1, queries);
        Assert.Same(first, second);
    }
}
```
Command (real repo):
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~GpuDetectCacheTests"
```
On HEAD this is a **compile-level red** (`Gpu.Memoize` does not exist). CLAUDE.md names compile-level reds as an accepted form where a behavioral red cannot safely run (its drive-root rule); the PR body should disclose that this case is analogous. The behavioral red is captured by extracting HEAD's check-then-set into the seam *unchanged* (see TDD step 2). I ran exactly that in a scratch copy:
```
  Failed CleanDriver.Tests.GpuDetectCacheTests.Memoize_ConcurrentFirstCalls_RunOneQuery_AndShareOneResult [523 ms]
Expected: 1
Actual:   2
Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2
```

### B. Live — running app
Linux headless harness: a copy of `Program.cs --headless` serving the real `Api.cs`/`Lib/*.cs`, with a counting `powershell` stand-in first on `PATH`. The Windows equivalent is `dotnet run --project nvcleanstall -- --headless --mock-catalog` on port 4780, while watching `powershell.exe` launches with Process Monitor or `Get-CimInstance Win32_Process`.

1. Stand-in script:
   ```sh
   #!/bin/sh
   echo "launched $$" >> <shim>/launches.log
   sleep 1
   echo 'NVIDIA GeForce RTX 4070|32.0.15.7086'
   ```
2. `PATH=<shim>:$PATH CLEANDRIVER_URLS=http://127.0.0.1:4804 dotnet out/Server.dll --mock-catalog`
3. Fire what `app.js` boot fires, concurrently:
   ```sh
   curl -s http://127.0.0.1:4804/api/system & curl -s http://127.0.0.1:4804/api/catalog & curl -s http://127.0.0.1:4804/api/tweaks & wait
   ```
4. Result:
   - Expected: `launches.log` has 1 line.
   - Actual:
   ```
   launched 7172
   launched 7173
   2
   ```
   A fourth, later `/api/system` call adds no line: the cache works once it has been written, and the race is only on the first calls.

## Root cause
A memoized value whose computation takes seconds was cached with a non-atomic "if null, compute, store". That is safe only if the first caller is alone. The wizard guarantees the first caller is *not* alone: `Promise.all` makes two routes that both need the GPU start at the same moment. The HARD-03 commentary assumed a single "first `/api/system` call" paying the query and did not consider concurrent first calls.

## Remedy
Minimal fix: make the cache single-flight with `Lazy<T>` (`ExecutionAndPublication`), behind a public delegate seam. The seam is public because the project has no `InternalsVisibleTo`; existing seams such as `ReadBounded` are public for the same reason.

```diff
 public static class Gpu
 {
-    private static GpuInfo? _cached;
+    // One WMI query per process lifetime, even when /api/system and /api/catalog make
+    // their first calls concurrently (app.js boots with Promise.all).
+    private static readonly Func<GpuInfo> _detect = Memoize(QueryWmi);
+
+    // Testable seam: the query is reached only through `query`.
+    public static Func<GpuInfo> Memoize(Func<string?> query)
+    {
+        var once = new Lazy<GpuInfo>(() => DetectFrom(query()), LazyThreadSafetyMode.ExecutionAndPublication);
+        return () => once.Value;
+    }
 ...
-    public static GpuInfo Detect()
-    {
-        if (_cached != null) return _cached;
-        _cached = DetectFrom(QueryWmi());
-        return _cached;
-    }
+    public static GpuInfo Detect() => _detect();
```
- **Why minimal:** the cache semantics are unchanged (compute once, keep forever). The only change is that concurrent first callers wait for the one in-flight query.
- **Exceptions:** `QueryWmi` already converts every failure to `null` and therefore to `Simulated`, so `Lazy`'s exception caching is never reached.
- **Alternatives rejected:**
  - (a) `lock` plus double-checked `_cached`. Equivalent, but more code and easier to get wrong (it needs `Volatile.Read`).
  - (b) Have `app.js` call `/api/system` before `/api/catalog`. That fixes one caller, not the race, puts logic in the frontend, and adds latency.
  - (c) Re-query when the cached value is `Simulated`. A behavior change outside this bug: it would re-spawn PowerShell on every request on machines without an NVIDIA GPU.
- **Related observation (not fixed here):** a single transient timeout is cached as `Simulated` for the process lifetime. That matches the documented "Detect() caches" design, so changing it is an owner decision.
- **Fences:**
  - HARD-03's `ReadBounded`/5 s budget is untouched. `GpuTimeoutTests` and `GpuDetectionTests` are untouched (they never call `Detect()`).
  - GAP-C04's canonical-path change slots into `QueryWmi` unchanged.
  - `GpuInfo` shape unchanged; no new `DetectedVia` text.
- **Verified:** I applied the diff to a scratch copy. Full suite plus all repros: 161/161 green, including `GpuDetectCacheTests` (2/2). HEAD's PATH-stand-in test run against the fixed `Detect()` records **1** launch.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** `docs/security_register.md` is active. This is a `Gpu.cs` correctness defect next to GAP-C04 but not covered by it. Ask the owner to pin a new ID (for example BUG-12, "Wave C — Gpu.cs single-flight cache") per §7, or to rule that it rides with GAP-C04 as a disclosed second finding.
   > *Pinned 2026-11-23 as **GAP-R08** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** `git worktree add ../nvcleanclone-gpu-once -b feat/gpu-detect-concurrent-first-call-race origin/main`
2. **Red.** Add `nvcleanstall/CleanDriver.Tests/GpuDetectCacheTests.cs` (§A). In the same commit, add the seam `Gpu.Memoize(Func<string?>)` containing HEAD's check-then-set logic **moved verbatim** (`GpuInfo? cached = null; return () => { if (cached != null) return cached; cached = DetectFrom(query()); return cached; };`), with `Detect()` routed through it. That is a behavior-preserving extraction the test needs in order to compile.
   - Run `--filter "FullyQualifiedName~GpuDetectCacheTests"` and capture `Expected: 1 / Actual: 2`.
   - Commit `"BUG-12 (red): concurrent first Detect() calls run one WMI query"`.
3. **Green.** Replace the verbatim body with `Lazy<GpuInfo>(…, ExecutionAndPublication)`. Run the FULL suite. Commit `"BUG-12: single-flight the GPU detection cache"`.
4. **Refactor.** Fix the CLAUDE.md wording "the first `/api/system` or `/api/catalog` call pays the query" only if the owner wants docs touched in this PR (otherwise note it). Suite green.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** On Windows, run `dotnet run --project nvcleanstall -- --headless --mock-catalog`. In PowerShell, record `Get-CimInstance Win32_Process -Filter "Name='powershell.exe'"` before and after. Fire the three boot requests concurrently (`Start-Job`/`curl.exe … &`), then `curl -s localhost:4780/api/system`. Paste evidence of exactly one new `powershell.exe` (Process Monitor "Process Create" filter, or the CommandLine containing `Win32_VideoController`).
7. **PR.** Fill `.github/pull_request_template.md`. Under Deviations, disclose the compile-level-then-extracted red and the new public seam. Push. Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green; the smoke step's `GPU detection path` line still prints.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-gpu-once origin/feat/gpu-detect-concurrent-first-call-race`. Range `origin/main...HEAD`.
3. **Re-run gates.** Then do the live exercise (step 6) on the reviewer's own Windows host. A builder's self-report is a claim, not a fact.
4. **Finder pass.**
   - Is `LazyThreadSafetyMode.ExecutionAndPublication` (not `PublicationOnly`, which would still run two queries)?
   - Is `Detect()` the only production caller of `QueryWmi`?
   - Does the red commit's seam really contain HEAD's logic verbatim? Diff the red commit against HEAD.
   - Does the test exercise the seam production uses (`_detect = Memoize(QueryWmi)`), not a test-only copy? This is the "shape without function" trap.
   - Is the `ReadBounded`/`WmiBudget` path untouched?
   - Is no `DetectedVia`/`GpuInfo` change made?
   - Optionally run `/code-review` on the PR diff as the finder.
5. **Adversarial verify:**
   - 8 concurrent callers. Expect `queries == 1`.
   - A query delegate that returns `null`. Expect all callers to get the `Simulated` instance and `queries == 1`.
   - A query delegate that throws. Decide and pin the behavior; `QueryWmi` never throws, but the seam can.
   - Re-run the Linux PATH-stand-in repro against the branch. Expect 1 launch.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Implementer handoff. Fix loop C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relay. Only the owner merges.

## Acceptance criteria
1. Two (and eight) concurrent first calls through `Gpu.Memoize(query)` invoke `query` exactly once and receive the same `GpuInfo` instance.
2. Later calls never re-invoke `query`.
3. `Gpu.Detect()` routes through that seam (`Memoize(QueryWmi)`), so production uses the single-flight path.
4. On a running app, the wizard's concurrent boot requests (`/api/system`, `/api/catalog`, `/api/tweaks`) start exactly one PowerShell/WMI process.
5. `GpuTimeoutTests` and `GpuDetectionTests` pass unedited. Build, the full suite, and `git diff --check` are green.
