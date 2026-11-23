# `/api/download` re-queries the catalog provider, so one transient live-lookup failure turns the listed driver into "unknown version"

| Field | Value |
|---|---|
| **Slug** | `download-reresolve-unknown-version` |
| **Severity** | Low |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness). Live end-to-end repro not run: it needs live NVIDIA mode on a mapped GPU (see §B). |
| **Area** | `nvcleanstall/Api.cs` (`POST /api/download`, `GET /api/catalog`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | No direct entry. **GAP-F01** plans to "cache metadata with schema/version/age", but it is blocked on an owner UI ruling and does not specify how `/api/download` resolves a pick. This is a route-level defect that holds even with a provider-side cache that can expire between the two calls. Unrelated to **BUG-02** (artifact correlation). |
| **Found** | 2026-11-23 |

## Summary
`GET /api/catalog` shows the user a release list built by the provider. When the user clicks Next, `POST /api/download` does not resolve the pick against that list. It calls `catalog.GetReleases(...)` again, and in live mode that is a fresh, synchronous NVIDIA lookup with a 5-second timeout. `NvidiaCatalogProvider` never throws: on any failure it silently returns the mock catalog. So if the second lookup times out or fails, the live version the user picked is not in the result, and the route answers `404 {"error":"unknown version"}`. The wizard alerts "unknown version" for a driver the app itself listed seconds earlier.

## Evidence
- `nvcleanstall/Api.cs:27-28`. The catalog the user sees is built, returned and forgotten:
  ```csharp
  app.MapGet("/api/catalog", () =>
      Results.Json(CatalogEndpoint.Build(catalog, Gpu.Detect()), Json.Web));
  ```
- `nvcleanstall/Api.cs:81-82`. The download route re-resolves through the provider:
  ```csharp
  var rel = catalog.GetReleases(Gpu.Detect()).FirstOrDefault(r => r.Version == req.Version);
  if (rel == null) return Results.Json(new { error = "unknown version" }, Json.Web, statusCode: 404);
  ```
- `nvcleanstall/Lib/NvidiaCatalogProvider.cs`:
  - `:34`: `GetReleases => GetCatalog(gpu).Releases`, so every call is a full lookup. There is no cache.
  - `:18`: a 5 s `Timeout`.
  - `:71`: a sync-over-async `GetAsync(url).GetAwaiter().GetResult()` on the Kestrel request thread.
  - `:47-57` returns the mock list on any exception or empty result instead of throwing:
    ```csharp
    catch (Exception ex)
    {
        return Fallback(gpu, $"live lookup failed: {ex.GetType().Name}", CatalogDetail.LiveLookupFailed);
    }
    ```
- `nvcleanstall/wwwroot/app.js:131-132`. The error reaches the user verbatim:
  ```js
  const dl = await api.post('/api/download', { version: state.version });
  if (dl.error) return alert(dl.error);
  ```

The comment at `Api.cs:79-80` explains the re-resolution: "never trust a client URL". That goal holds just as well when the server resolves against data it served itself. What breaks is the assumption that a second provider call returns the same catalog as the first. The provider's documented contract (silent fallback to mock) guarantees it will not when the network flakes.

## Impact
- In live mode, a timeout or failure of NVIDIA's lookup service between page load and Next leaves the user unable to download the driver they selected. They see the misleading `unknown version` alert and are returned to Screen 1 for a retry that issues yet another lookup. Causes include a slow or metered network, a VPN reconnect, or brief rate limiting.
- Every Next click costs an extra NVIDIA round trip of up to 5 s, blocking a Kestrel thread.
- A secondary hazard, reasoned and not reproduced. The bundled mock catalog uses real historical NVIDIA version numbers (`572.16`, `571.96`, `566.36`, …). If a live pick shares a version string with a mock row, the fallback result matches. The pick then silently routes to the *simulated* download (`Jobs.ShouldRealDownload` is false for `Source == "mock"`), and the receipt carries no real artifact, with no visible signal.
- Mock mode (`--mock-catalog`) is unaffected, because the mock provider is deterministic. Low likelihood × bounded effect = **Low**.

## How to reproduce
### A. Automated — failing xunit test
Add as `nvcleanstall/CleanDriver.Tests/DownloadResolutionTests.cs`. The provider double implements `NvidiaCatalogProvider`'s documented contract: the first lookup returns live rows, and a later failure falls back to mock with `LiveLookupFailed`. A double is used because `Api.Map` calls `Gpu.Detect()`, which on CI and Linux is `Simulated`, so the real provider would short-circuit to mock on the first call. The release's `DownloadUrl` is deliberately not an NVIDIA host, so even a fixed route never reaches the network from this test: the real-download job fails on the host allowlist before any request.

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CleanDriver.Tests;

public class DownloadResolutionTests
{
    private sealed class LiveThenFallbackProvider : ICatalogProvider
    {
        private int _calls;
        public IReadOnlyList<Release> GetReleases(GpuInfo gpu) => GetCatalog(gpu).Releases;
        public CatalogResult GetCatalog(GpuInfo gpu) =>
            Interlocked.Increment(ref _calls) == 1
                ? new CatalogResult(new[]
                  {
                      new Release { Version = "610.74", Channel = "WHQL", ReleaseDate = "2026-11-01",
                                    SizeMB = 900, Source = "live", DownloadUrl = "https://download.invalid/610.74.exe" },
                  }, null)
                : new CatalogResult(new MockCatalogProvider().GetReleases(gpu), CatalogDetail.LiveLookupFailed);
    }

    private static async Task<(WebApplication app, HttpClient http)> StartApiAsync(ICatalogProvider catalog)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        Api.Map(app, catalog);
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    [Fact]
    public async Task Download_OfVersionTheCatalogJustServed_IsNotUnknown_AfterTransientLookupFailure()
    {
        var (app, http) = await StartApiAsync(new LiveThenFallbackProvider());
        await using var host = app;

        // Screen 1 boot: the server serves a live catalog containing 610.74.
        var cat = JsonDocument.Parse(await http.GetStringAsync("/api/catalog")).RootElement;
        Assert.Equal("live", cat.GetProperty("source").GetString());
        Assert.Equal("610.74", cat.GetProperty("releases")[0].GetProperty("version").GetString());

        // Next: the user downloads exactly the version the server listed.
        var resp = await http.PostAsync("/api/download",
            new StringContent("{\"version\":\"610.74\"}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);   // HEAD: 404 {"error":"unknown version"}
        Assert.True(JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement
            .TryGetProperty("jobId", out _));
    }
}
```

Command:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~DownloadResolutionTests"
```

RED, Linux net10.0 harness output (class named `DownloadReresolveUnknownVersionRepro` there):
```
  Failed CleanDriver.Tests.DownloadReresolveUnknownVersionRepro.Download_OfVersionTheCatalogJustServed_IsNotUnknown_AfterTransientLookupFailure [360 ms]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: OK
Actual:   NotFound
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 360 ms - Harness.Tests.dll (net10.0)
```

### B. Live — running app
This cannot be shown with `--mock-catalog`, because the mock provider never changes between calls. It needs the live path on Windows with a GPU in `GpuPfidMap`. It was not run here: the session must not contact NVIDIA, and the Linux GPU is simulated. The steps are for owner-run evidence:

1. `dotnet run --project nvcleanstall -- --headless` (live mode, port 4780).
2. `curl -s localhost:4780/api/catalog`. Expect `"source": "live"`; note the first `version`, say `V`.
3. Make the next lookup fail for this process only. For example, add an outbound Windows Firewall rule blocking `gfwsl.geforce.com` for `CleanDriver.exe`, or disconnect the network adapter.
4. `curl -s -i -X POST localhost:4780/api/download -H "Content-Type: application/json" -d '{"version":"V"}'`.
   - Expected: `200 {"jobId": …}` (the download job may then fail on the network, which is honest and visible on Screen 2).
   - Actual on HEAD, per the route code and the test above: `HTTP/1.1 404` with `{"error":"unknown version"}`. The server log shows `NvidiaCatalogProvider: using mock catalog (live lookup failed: …)`.
5. Remove the firewall rule.

## Root cause
The route resolves a user's choice against a second, independent provider query instead of the list it served. With a provider whose contract is "never throw; on failure, return a different (mock) list", the two queries can disagree. When they do, the route reports the user's legitimate pick as unknown.

## Remedy
Resolve against the releases this server last served, and fall back to the provider only when the version was never served. That fallback is the current behavior, preserved for direct API callers who never fetched `/api/catalog`. The URL is still server-side data, so "never trust a client URL" holds. This was validated in a scratch copy of `Api.cs`: the full suite (151) plus all repros went green.

```diff
-        app.MapGet("/api/catalog", () =>
-            Results.Json(CatalogEndpoint.Build(catalog, Gpu.Detect()), Json.Web));
+        // The releases this server last served: /api/download resolves the user's pick
+        // against them first (still server-side data, never a client URL).
+        IReadOnlyList<Release> served = Array.Empty<Release>();
+        app.MapGet("/api/catalog", () =>
+        {
+            var resp = CatalogEndpoint.Build(catalog, Gpu.Detect());
+            served = resp.Releases;
+            return Results.Json(resp, Json.Web);
+        });
 ...
-            var rel = catalog.GetReleases(Gpu.Detect()).FirstOrDefault(r => r.Version == req.Version);
+            var rel = served.FirstOrDefault(r => r.Version == req.Version)
+                ?? catalog.GetReleases(Gpu.Detect()).FirstOrDefault(r => r.Version == req.Version);
```

- **Why this is minimal.** It is one captured local per `Map` call: per app instance, not static, so in-process test hosts don't cross-contaminate. Reference assignment is atomic, and the list is immutable once built. No provider change.
- **Alternatives rejected.**
  - Caching inside `NvidiaCatalogProvider`: catalog area, GAP-F01's design space, and still racy against cache expiry.
  - Letting the client send the URL: violates the route's stated invariant and SEC-03's direction.
  - Making the provider throw on failure: breaks HARD-06's `sourceDetail` contract and `/api/catalog`'s never-fail behavior.
- **Fences.** Route-by-source is unchanged: `rel.Source` and `DownloadUrl` come from the provider's own earlier result. The per-hop `.nvidia.com` allowlist in `Jobs.cs` still guards the actual fetch. Safety boundary intact (bytes to disk, never executed). No UI change.
- **Back-compat pins.** `DownloadRoutingTests` and `RealDownloadTests` test `Jobs` directly and are unaffected. `CatalogWiringTests` / `CatalogSourceDetailTests` exercise `CatalogEndpoint.Build`, which is unchanged.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** `docs/security_register.md` is active and no existing GAP owns this. It needs a new owner-pinned entry (for example under Wave C as `BUG-1x` → its own GAP-C item). Do not fold it into GAP-F01, which is blocked on a UI ruling.
1. **Slice setup:** `git worktree add ../nvcleanclone-dlresolve -b feat/download-resolves-served-catalog origin/main`.
2. **Red.** Add §A at `nvcleanstall/CleanDriver.Tests/DownloadResolutionTests.cs` and run the filter. It fails `Expected: OK / Actual: NotFound`. Commit `"download-reresolve-unknown-version (red): /api/download resolves against the catalog the server served"`.
3. **Green.** Apply the diff and run the FULL suite. Commit `"download-reresolve-unknown-version: resolve the download pick against the served catalog first"`.
4. **Refactor**, with the suite green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.**
   - Mock regression: `--headless --mock-catalog`, then `curl localhost:4780/api/catalog`, then `curl -X POST localhost:4780/api/download -d '{"version":"572.16"}' -H "Content-Type: application/json"` returns a `jobId` that reaches `done`.
   - Owner-run live evidence: §B steps 1–5 now return `200 {"jobId":…}`.
7. **PR.** Fill the template:
   - TDD row.
   - Edited-test disclosure: none.
   - Deviations: name the new captured state explicitly.
   - The live transcripts.

   Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green.
2. **Isolated worktree:** `git worktree add ../nvcleanclone-review-dlresolve origin/feat/download-resolves-served-catalog`. The range is `origin/main...HEAD`.
3. **Re-run the gates**, then run the live mock regression.
4. **Finder pass:**
   - Is `served` per-`Map`, not `static`?
   - Is it assigned only from `CatalogEndpoint.Build`'s result, never from request data?
   - Is the provider fallback still present for direct callers?
   - Can `ShouldRealDownload` still only fire for `Source == "live"` with a URL?
   - Does the test's `DownloadUrl` stay a non-NVIDIA host, so a green test can never touch the network?
5. **Adversarial verify:**
   - `POST /api/download {"version":"610.74"}` before any `/api/catalog` call must give today's behavior (provider lookup).
   - Two `/api/catalog` calls (live, then fallback mock) followed by a download of the live version gives `served` = the mock list, so 404. Check the PR documents this "last served wins" semantics.
   - A version string never served (`999.99`) must still give 404.
   - Concurrent `/api/catalog` + `/api/download` under `xargs -P 20` must not throw.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with findings marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Fix loop C0–C7: Orient, then reproduce red, fix, verify ACs, gates, one `F<n>:` commit per finding, closing report, STOP.
8. **Report to owner.** Review comes before relay. Only the owner merges.

## Acceptance criteria
1. After `/api/catalog` served a release, `POST /api/download` for that version returns `200 {"jobId":…}` even if the provider's next lookup falls back to mock.
2. The resolved release (Source, DownloadUrl) is the one the server served. No request field influences the URL.
3. A version never served and absent from a fresh provider lookup still returns `404 {"error":"unknown version"}`.
4. Mock-mode download behavior is byte-identical (simulated job, same fields), and the full suite is green.
