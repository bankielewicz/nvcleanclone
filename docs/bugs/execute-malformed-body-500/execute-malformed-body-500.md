# `/api/execute` answers a malformed body with a bodiless HTTP 500 and can leave a zombie "running" job

| Field | Value |
|---|---|
| **Slug** | `execute-malformed-body-500` |
| **Severity** | Low |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness); also confirmed by live repro |
| **Area** | `nvcleanstall/Api.cs` (`POST /api/execute`), `nvcleanstall/Lib/Models.cs` (`Selection`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | Next to **BUG-01 / GAP-C01** ("validate before creating a job"), but GAP-C01 does not cover this. Its ACs are about what a selection means (unknown or duplicate IDs, a missing display driver, tweak types). They do not cover a missing token, `null` collections, or an `outputPath` that `Path.GetFullPath` rejects. A GAP-C01 validator that walks `selection.Components` would hit the same `NullReferenceException` unless it guards for null first. The zombie jobs also make **BUG-05 / GAP-S02** worse: GAP-S02's eviction rule says eviction "never removes a running job", so a job that can never finish would never be evicted. |
| **Found** | 2026-11-23 |

## Summary
`POST /api/execute` treats the bound `ExecuteRequest` as if it were already valid. Three things crash it with an unhandled exception, which Kestrel turns into `500` with `Content-Length: 0`:
- a missing or `null` `token` (`ConcurrentDictionary.TryGetValue(null)` throws);
- a `selection` whose `components` or `tweaks` is JSON `null`;
- an `outputPath` that `Path.GetFullPath` rejects.

In the last two cases `Jobs.StartExecute` has already registered the job before it throws. That job stays `"running"` at progress 0 for the life of the process. The client never receives its id, and nothing will ever complete or evict it.

## Evidence
- `nvcleanstall/Api.cs:16`. `Token` is declared non-nullable, but System.Text.Json does not enforce record nullability. A missing or `null` member binds to `null`:
  ```csharp
  private record ExecuteRequest(string Token, string Action, Selection? Selection, string? OutputPath);
  ```
- `nvcleanstall/Api.cs:96`. The first statement of the route sits outside the `try`. `ConcurrentDictionary<string,…>.TryGetValue(null, …)` throws `ArgumentNullException` (server log: `System.ArgumentNullException: Value cannot be null. (Parameter 'key')`):
  ```csharp
  if (!Sessions.TryGetValue(req.Token, out var src))
  ```
- `nvcleanstall/Api.cs:108-109`. The only guard replaces a missing `Selection` object. It does not check the object's members:
  ```csharp
  var job = Jobs.StartExecute(req.Action, src.manifest, src.source,
      req.Selection ?? new Selection(), req.OutputPath, Gpu.Detect(), artifact);
  ```
- `nvcleanstall/Lib/Models.cs:45-46`. The `= new()` initializers are overwritten when the JSON explicitly sends `null`:
  ```csharp
  public List<string> Components { get; init; } = new();
  public Dictionary<string, JsonElement> Tweaks { get; init; } = new();
  ```
- `nvcleanstall/Lib/Jobs.cs:378` registers the job first (`var job = Create(action);`, which stores it in `Store`). Only after that do these lines throw:
  - `Jobs.cs:382`: `selection.Components.Contains(c.Id)` throws `NullReferenceException` (server log: `at CleanDriver.Lib.Jobs…<StartExecute>b__0(ComponentDef c) in …/Lib/Jobs.cs:line 382`).
  - `Jobs.cs:388`: `Path.GetFullPath(outputPath)` throws `ArgumentException: Null character in path.`
  - `Jobs.cs:414` / `Jobs.cs:522`: `selection.TweakOn(...)` reaches `Models.cs:49` `Tweaks.TryGetValue` on a `null` dictionary and throws `NullReferenceException`.
- `nvcleanstall/Api.cs:112`. The catch filter maps only `IOException` and `InvalidDataException` to 400, so `NullReferenceException` and `ArgumentException` escape as 500:
  ```csharp
  catch (Exception ex) when (ex is IOException or InvalidDataException)
  ```

The route assumes that "the record type says non-null" and "StartExecute validates" together make the body safe. Neither holds. Nullability annotations are not enforced at runtime, and StartExecute's checks run after its side effect (job registration).

## Impact
- Any direct API client gets `500` with an empty body instead of the route's own `{"error": …}` 400 contract. That includes scripts, the Playwright and curl verification flow the repo is built around, and future GAP-S02 token-bearing callers. If the wizard ever sent such a body, `app.js:8-11` (`r.json()`) would reject on the empty body, `startExecute` would never reach `alert(r.error)`, and the click would silently do nothing.
- Each such request that carries a valid token leaks a zombie `Job` (type `install`/`extract`, `status: "running"`, `progress: 0`, `log: []`) into the static `Jobs.Store` for the life of the process. Under GAP-S02's planned cap ("retained completed jobs at 128 … eviction never removes a running job"), zombies are never evicted, so they grow without bound even after that fix.
- Reachability: the shipped wizard always sends a token and arrays, so normal UI use does not trigger the token/null-list cases. That makes it a bounded robustness defect: **Low**.
  - *Addendum (E2E functional pass, 2026-11-23, test R-01/R-09):* the **`outputPath` case is reachable through the wizard**. A NUL character in the Output folder field (entered via Playwright `fill`; hard for a human to type or paste, which keeps the grade at Low) produces this bodiless 500, and Start then silently does nothing, exactly as the first bullet predicts. The client half of that failure is [`api-helper-silent-on-non-json-errors`](../api-helper-silent-on-non-json-errors/api-helper-silent-on-non-json-errors.md). The Remedy's "the wizard already shows `r.error` through `alert`" holds **after** this fix (a JSON 400 body parses), not before it.

## How to reproduce
### A. Automated — failing xunit test
Add as `nvcleanstall/CleanDriver.Tests/ExecuteMalformedBodyTests.cs`. This would be the suite's first in-process API host. It uses only the ASP.NET Core shared framework that `CleanDriver.csproj` already references through `FrameworkReference`, which flows to the test project through the `ProjectReference`; no new package is needed. When a second such test lands, extract `StartApiAsync` into `TestHttp.cs`.

```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CleanDriver.Tests;

public class ExecuteMalformedBodyTests
{
    private static async Task<(WebApplication app, HttpClient http)> StartApiAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        Api.Map(app, new MockCatalogProvider());
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<string> TokenFor(HttpClient http)
    {
        var r = await http.PostAsync("/api/package", Body("{\"kind\":\"catalog\",\"version\":\"572.16\"}"));
        r.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString()!;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"action\":\"install\"}")]
    [InlineData("{\"token\":null,\"action\":\"extract\"}")]
    public async Task Execute_MissingToken_Returns400UnknownToken(string body)
    {
        var (app, http) = await StartApiAsync();
        await using var host = app;

        var resp = await http.PostAsync("/api/execute", Body(body));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Contains("unknown package token", await resp.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("install", "{\"components\":null}", null)]
    [InlineData("install", "{\"components\":[\"display-driver\"],\"tweaks\":null}", null)]
    [InlineData("extract", "{\"components\":null}", null)]
    [InlineData("extract", "{\"components\":[\"display-driver\"]}", "a\\u0000b")]
    public async Task Execute_MalformedSelectionOrPath_Returns400WithError(string action, string selection, string? outputPath)
    {
        var (app, http) = await StartApiAsync();
        await using var host = app;
        var token = await TokenFor(http);
        var path = outputPath == null ? "null" : $"\"{outputPath}\"";

        var resp = await http.PostAsync("/api/execute",
            Body($"{{\"token\":\"{token}\",\"action\":\"{action}\",\"selection\":{selection},\"outputPath\":{path}}}"));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var json = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.TryGetProperty("error", out _));
        Assert.False(json.TryGetProperty("jobId", out _));
    }
}
```

Command:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~ExecuteMalformedBodyTests"
```

RED, Linux net10.0 harness output (class named `ExecuteMalformedBody500Repro` there; trimmed):
```
  Failed CleanDriver.Tests.ExecuteMalformedBody500Repro.Execute_MalformedSelectionOrPath_Returns400WithError(action: "extract", selection: "{\"components\":[\"display-driver\"]}", outputPath: "a\\u0000b") [368 ms]
   Assert.Equal() Failure: Values differ
Expected: BadRequest
Actual:   InternalServerError
  Failed ...Execute_MalformedSelectionOrPath_Returns400WithError(action: "extract", selection: "{\"components\":null}", outputPath: null)
Expected: BadRequest
Actual:   InternalServerError
  Failed ...Execute_MalformedSelectionOrPath_Returns400WithError(action: "install", selection: "{\"components\":[\"display-driver\"],\"tweaks\":nu"···, outputPath: null)
Expected: BadRequest
Actual:   InternalServerError
  Failed ...Execute_MalformedSelectionOrPath_Returns400WithError(action: "install", selection: "{\"components\":null}", outputPath: null)
Expected: BadRequest
Actual:   InternalServerError
  Failed ...Execute_MissingToken_Returns400UnknownToken(body: "{}")
Expected: BadRequest
Actual:   InternalServerError
  Failed ...Execute_MissingToken_Returns400UnknownToken(body: "{\"action\":\"install\"}")
Expected: BadRequest
Actual:   InternalServerError
  Failed ...Execute_MissingToken_Returns400UnknownToken(body: "{\"token\":null,\"action\":\"extract\"}")
Expected: BadRequest
Actual:   InternalServerError
Failed!  - Failed:     7, Passed:     0, Skipped:     0, Total:     7, Duration: 509 ms - Harness.Tests.dll (net10.0)
```

### B. Live — running app
Start `dotnet run --project nvcleanstall -- --headless --mock-catalog` (port 4780). The output below was captured from the Linux headless harness, a verbatim copy of Program.cs's `--headless` branch, on port 4803.

1. No token. Expected `400 {"error":"unknown package token"}`. Actual:
   ```
   $ curl -s -i -X POST localhost:4780/api/execute -H "Content-Type: application/json" -d '{"action":"install"}'
   HTTP/1.1 500 Internal Server Error
   Content-Length: 0
   Server: Kestrel
   ```
2. Get a token: `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"catalog","version":"572.16"}'` returns `"token": "1"`.
3. Null components. Expected 400 and no job. Actual: 500, empty body.
   ```
   $ curl -s -i -X POST localhost:4780/api/execute -H "Content-Type: application/json" \
       -d '{"token":"1","action":"install","selection":{"components":null}}'
   HTTP/1.1 500 Internal Server Error
   Content-Length: 0
   ```
4. Unparseable outputPath. Actual: 500, empty body.
   ```
   $ curl -s -i -X POST localhost:4780/api/execute -H "Content-Type: application/json" \
       -d '{"token":"1","action":"extract","selection":{"components":["display-driver"]},"outputPath":"a\u0000b"}'
   HTTP/1.1 500 Internal Server Error
   Content-Length: 0
   ```
5. Wait 10 s, then poll the job ids the server allocated and never returned:
   ```
   $ curl -s localhost:4780/api/jobs/1
   { "id": "1", "type": "install", "status": "running", "progress": 0, "totalMB": 0, "doneMB": 0,
     "modified": false, "rebootRecommended": false, "log": [] }
   $ curl -s localhost:4780/api/jobs/2
   { "id": "2", "type": "extract", "status": "running", "progress": 0, ... "log": [] }
   ```
   Both stay `running` for as long as the process lives. The server log shows `System.ArgumentNullException: Value cannot be null. (Parameter 'key')`, then `System.NullReferenceException … Jobs.cs:line 382`, then `System.ArgumentException: Null character in path.`

## Root cause
The route has no request-validation step. It relies on two assumptions that are both false:
1. That the C# non-nullable declarations on `ExecuteRequest.Token` and on `Selection.Components`/`Tweaks` (with their `= new()` defaults) guarantee non-null values at runtime. They do not: System.Text.Json writes an explicit JSON `null` straight over the initializer, and a missing constructor parameter binds to `null`.
2. That anything `Jobs.StartExecute` rejects will surface as one of the two exception types the route catches. It does not. `StartExecute` registers the job before it reads the selection or normalizes the path, and the failures it hits are `NullReferenceException` and `ArgumentException`, neither of which is in the route's catch filter.

## Remedy
Validate the body at the top of the route, before any job exists. This patch was validated in a scratch copy of `Api.cs`: the full suite (151) plus all repro tests went green.

```diff
-            if (!Sessions.TryGetValue(req.Token, out var src))
+            if (string.IsNullOrEmpty(req.Token) || !Sessions.TryGetValue(req.Token, out var src))
                 return Results.Json(new { error = "unknown package token" }, Json.Web, statusCode: 400);
             if (req.Action is not ("install" or "silent" or "extract" or "package"))
                 return Results.Json(new { error = "unknown action" }, Json.Web, statusCode: 400);
+            var selection = req.Selection ?? new Selection();
+            if (selection.Components is null || selection.Tweaks is null)
+                return Results.Json(new { error = "invalid selection" }, Json.Web, statusCode: 400);
+            if (!string.IsNullOrWhiteSpace(req.OutputPath))
+            {
+                try { Path.GetFullPath(req.OutputPath); }
+                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
+                { return Results.Json(new { error = "invalid outputPath" }, Json.Web, statusCode: 400); }
+            }
             try
             {
 ...
                 var job = Jobs.StartExecute(req.Action, src.manifest, src.source,
-                    req.Selection ?? new Selection(), req.OutputPath, Gpu.Detect(), artifact);
+                    selection, req.OutputPath, Gpu.Detect(), artifact);
```

- **Why this is minimal.** It touches one route. The error strings are fixed and contain no paths. A missing `selection` still defaults to `new Selection()`, so that existing behavior is preserved.
- **Alternatives rejected.**
  - Widening the catch to `Exception`: zombie jobs would remain, and arbitrary exception text (which can contain paths) would reach the client.
  - Global `RespectNullableAnnotations = true` on the HTTP `JsonOptions`: this changes the 400 semantics of every route. For example `/api/package {}` would stop returning the `{"error":"path required"}` JSON body that the wizard alerts on. If wanted, it is a separate decision.
  - Moving `Create()` after validation inside `Jobs.StartExecute`: good defense in depth, but it is out of area for this slice (Jobs.cs) and does not fix the null-token crash at `Api.cs:96`. It is filed as a lead.
- **Fences respected.**
  - No new UI. The wizard already shows `r.error` through `alert` (`app.js:391`).
  - The HARD-02 root-path rejection is unchanged: a drive-root `outputPath` still parses, so it still becomes a named failed job.
  - No honesty-marker or receipt change.
  - No deletion code is involved.
- **Back-compat pins.** `ExecuteReceiptTests`, `OutDirHygieneTests` and `PackageZipTests` call `Jobs.StartExecute` directly and are unaffected.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** `docs/security_register.md` is active. This fits **GAP-C01** ("Validate before creating a job") as three extra direct-API reds: missing token, null collections, and unparseable `outputPath`. The owner must pin those added ACs into GAP-C01. Otherwise it needs a new owner-pinned entry (for example `BUG-10`); per §7, "any new finding receives a new ID".
1. **Slice setup:** `git worktree add ../nvcleanclone-exec400 -b feat/execute-malformed-body-400 origin/main`.
2. **Red.** Add §A's test at `nvcleanstall/CleanDriver.Tests/ExecuteMalformedBodyTests.cs` and run the filter. All 7 cases fail with `Actual: InternalServerError`, which is the stated reason (the unhandled ArgumentNullException, NullReferenceException or ArgumentException). Commit `"execute-malformed-body-500 (red): /api/execute answers malformed bodies with a named 400"`.
3. **Green.** Apply the route-level validation above and run the FULL suite. Commit `"execute-malformed-body-500: validate token/selection/outputPath before any job is created"`.
4. **Refactor**, with the suite green only. If a second API-host test exists by then, move `StartApiAsync` to `TestHttp.cs`.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Rerun §B steps 1, 3 and 4. Each must print `HTTP/1.1 400` with an `"error"` body. Then confirm `curl -s localhost:4780/api/jobs/1` returns `404 {"error":"no such job"}` when no valid execute ran first, which proves no zombie was registered. Also run one valid `{"token":"1","action":"extract","selection":{"components":["display-driver"]}}` request and confirm it returns a `jobId` that reaches `done`.
7. **PR.** Fill `.github/pull_request_template.md`:
   - TDD rows: Missing token returns 400; null collections return 400; bad outputPath returns 400.
   - Red/green outputs.
   - Edited-test disclosure: none.
   - Deviations: the error strings are new (fixed, path-free).
   - The live transcript from step 6.

   Push, and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `gh pr checks <n>`: `build-test-smoke` must be green before you read the diff.
2. **Isolated worktree:** `git worktree add ../nvcleanclone-review-exec400 origin/feat/execute-malformed-body-400`. The range is `origin/main...HEAD`.
3. **Re-run the gates** on the reviewer's checkout (build, test, `git diff --check`), then run §B against `--headless --mock-catalog`.
4. **Finder pass** (optionally run `/code-review` on the diff as the finder):
   - Does every new early return happen before `Jobs.StartExecute`? That is the zombie-job property.
   - Is `req.Selection ?? new Selection()` still the default for a missing selection? The wizard always sends one, but presets and other old clients may not.
   - Do the error strings interpolate nothing (no path, no exception message)?
   - Is the `Path.GetFullPath` probe used only for validation, with `StartExecute` still deriving `outDir` itself, so HARD-02's `IsFilesystemRoot` stays the authority?
   - Does the test use `127.0.0.1:0`, never a fixed port and never 4780?
5. **Adversarial verify:**
   - `{"token":""}` must give 400 unknown token.
   - `{"token":"1","action":"install","selection":{"components":["display-driver"],"tweaks":{"unattended":null}}}` (a null tweak value, not a null map) must still succeed, because `TweakOn` checks `ValueKind`.
   - `"outputPath":"C:/"` must still produce HARD-02's named failed job, not a 400.
   - 50 parallel malformed requests via `xargs -P 20` must leave `/api/jobs/<n>` empty.
   - A body of `[]` must give the framework's 400.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED (reproduced) or PLAUSIBLE.
7. **Feedback to builder.** Use the implementer handoff and fix loop: C0 Orient (read the fence; branch and head SHA), C1 reproduce red, C2 fix, C3 verify each AC below, C4 gates, C5 one `F<n>:` commit per finding, C6 closing report (finding, then SHA, then AC output), C7 STOP.
8. **Report to owner.** Review comes before relay. Only the owner merges.

## Acceptance criteria
1. `POST /api/execute` with a missing, `null` or empty `token` returns `400 {"error":"unknown package token"}`.
2. With a valid token, `selection.components: null` or `selection.tweaks: null` returns `400` with an `"error"` body and no `jobId`, for every action.
3. An `outputPath` that `Path.GetFullPath` rejects (for example one containing NUL) returns `400 {"error":"invalid outputPath"}`. A drive-root `outputPath` still yields HARD-02's named failed job.
4. None of the requests in AC 1–3 registers a job: no new id becomes visible via `/api/jobs/{id}`.
5. A valid wizard request (token, action, arrays, null or valid outputPath) behaves exactly as before, and the full suite is green.
