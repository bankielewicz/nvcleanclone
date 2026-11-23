# `GET /api/presets/{name}` crashes with HTTP 500 for names that sanitize to empty, instead of 404

| Field | Value |
|---|---|
| **Slug** | `preset-load-invalid-name-500` |
| **Severity** | Low |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness); also confirmed by live repro |
| **Area** | `nvcleanstall/Api.cs` (`GET /api/presets/{name}`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | Next to **BUG-06 / GAP-C03**, which hardens `Presets.cs`. GAP-C03 does not fix this, and its planned "validate (do not strip)" names makes `Presets.Load` throw `InvalidDataException` for more names. The route-level gap therefore grows after GAP-C03 unless the route maps that exception. Suggest folding this into GAP-C03 as an explicit route AC. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R05** — API input contract: malformed requests get a JSON 400, never a bodiless 500 or a zombie job (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`Presets.Load(name)` calls `SafeName`, which throws `InvalidDataException("preset name required")` when a name sanitizes to empty. Examples: `!`, any non-ASCII name such as `日本`, a whitespace-only name, or `...`. The `GET /api/presets/{name}` route handles only the `null` (not found) return. The exception escapes and Kestrel returns `500` with an empty body. The sibling `POST /api/presets` route catches the same exception and returns a 400, so the GET route is the inconsistent one.

## Evidence
- `nvcleanstall/Api.cs:134-140`. No exception handling; only the `null` result maps to 404:
  ```csharp
  app.MapGet("/api/presets/{name}", (string name) =>
  {
      var preset = Presets.Load(name);
      return preset == null
          ? Results.Json(new { error = "no such preset" }, Json.Web, statusCode: 404)
          : Results.Json(preset, Json.Web);
  });
  ```
- `nvcleanstall/Lib/Presets.cs:39-41`. Load sanitizes before probing the file:
  ```csharp
  public static JsonElement? Load(string name)
  {
      var p = Path.Combine(PresetDir, $"{SafeName(name)}.json");
  ```
- `nvcleanstall/Lib/Presets.cs:10-15`. SafeName throws for an empty result:
  ```csharp
  var s = Regex.Replace(name?.Trim() ?? "", "[^A-Za-z0-9 _-]", "");
  if (s.Length == 0) throw new InvalidDataException("preset name required");
  ```
- `nvcleanstall/Api.cs:128-132`. The POST route does catch `InvalidDataException`, which shows the route layer is meant to own this mapping:
  ```csharp
  try { return Results.Json(Presets.Save(req.Name, req.Selection), Json.Web); }
  catch (InvalidDataException ex) { return Results.Json(new { error = ex.Message }, Json.Web, statusCode: 400); }
  ```
- `Presets.List` (`Presets.cs:17-28`) returns each file's `name` property from the JSON content, not the sanitized filename. A preset file whose content name is, for example, `日本` (a hand-edited, copied-in or future-version preset in `presets/`) is listed by `/api/presets`. The wizard then requests exactly that name (`app.js:515`, `api.get('/api/presets/' + encodeURIComponent(el.dataset.name))`) and gets the 500.

The route assumes `Presets.Load` either returns a preset or `null`. For names it cannot even turn into a filename, it throws instead.

## Impact
- API contract: a client asking for a preset that cannot exist should get the route's own `404 {"error":"no such preset"}`. It gets a bodiless 500 instead, and the server logs an unhandled-exception error for each request.
- Wizard: if a listed preset's content name sanitizes to empty, clicking it in "Load preset" fails silently. `api.get` (`app.js:8`) rejects on `r.json()` of the empty 500 body, the popover closes, and nothing is applied.
- No data is lost and the shipped save path (which sanitizes) never produces such a file. That makes this **Low**.

## How to reproduce
### A. Automated — failing xunit test
Add as `nvcleanstall/CleanDriver.Tests/PresetRouteTests.cs`. It hosts `Api.Map` in-process on an ephemeral loopback port, using the ASP.NET Core shared framework already referenced by `CleanDriver.csproj`.

```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CleanDriver.Tests;

public class PresetRouteTests
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

    // Every name here is stripped to "" by Presets.SafeName, which throws
    // InvalidDataException("preset name required"); the route catches nothing.
    [Theory]
    [InlineData("%21")]                // "!"
    [InlineData("%E6%97%A5%E6%9C%AC")] // a non-ASCII name ("日本")
    [InlineData("%20%20")]             // whitespace only
    [InlineData("...")]                // dots only (not a dot-segment, so it reaches the route)
    public async Task GetPreset_NameThatSanitizesToEmpty_Returns4xxJson_Not500(string encodedName)
    {
        var (app, http) = await StartApiAsync();
        await using var host = app;

        var resp = await http.GetAsync($"/api/presets/{encodedName}");

        Assert.True(resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest,
            $"expected 404/400, got {(int)resp.StatusCode}");
        Assert.Contains("\"error\"", await resp.Content.ReadAsStringAsync());
    }
}
```

Command:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~PresetRouteTests"
```

RED, Linux net10.0 harness output (class named `PresetLoadInvalidName500Repro` there):
```
  Failed CleanDriver.Tests.PresetLoadInvalidName500Repro.GetPreset_NameThatSanitizesToEmpty_Returns4xxJson_Not500(encodedName: "%20%20") [291 ms]
  Error Message:
   expected 404/400, got 500
  Failed ...GetPreset_NameThatSanitizesToEmpty_Returns4xxJson_Not500(encodedName: "%E6%97%A5%E6%9C%AC") [43 ms]
   expected 404/400, got 500
  Failed ...GetPreset_NameThatSanitizesToEmpty_Returns4xxJson_Not500(encodedName: "...") [21 ms]
   expected 404/400, got 500
  Failed ...GetPreset_NameThatSanitizesToEmpty_Returns4xxJson_Not500(encodedName: "%21") [19 ms]
   expected 404/400, got 500
Failed!  - Failed:     4, Passed:     0, Skipped:     0, Total:     4, Duration: 374 ms - Harness.Tests.dll (net10.0)
```

### B. Live — running app
Start `dotnet run --project nvcleanstall -- --headless --mock-catalog` (port 4780). The output below was captured on the Linux headless harness, port 4803.

1. Expected `404 {"error":"no such preset"}` for each request. Actual:
   ```
   $ curl -s -i --path-as-is localhost:4780/api/presets/%21
   HTTP/1.1 500 Internal Server Error
   Content-Length: 0
   $ curl -s -i localhost:4780/api/presets/%E6%97%A5%E6%9C%AC
   HTTP/1.1 500 Internal Server Error
   Content-Length: 0
   $ curl -s -i --path-as-is localhost:4780/api/presets/...
   HTTP/1.1 500 Internal Server Error
   Content-Length: 0
   ```
   Server log for each: `System.IO.InvalidDataException: preset name required at CleanDriver.Lib.Presets.SafeName(String name) in …/Lib/Presets.cs:line 13`.
2. Control: an ordinary unknown name gets the route's contract, `curl -s -i localhost:4780/api/presets/nope` returns `HTTP/1.1 404` with `{"error":"no such preset"}`.

## Root cause
The route maps only one of `Presets.Load`'s two failure modes, the `null` "file does not exist" result. The other failure mode, "this name cannot denote a preset file", surfaces as `InvalidDataException`. The GET route does not handle it, even though the POST route next to it does.

## Remedy
Map the exception to the existing not-found response in the route. This was validated in a scratch copy of `Api.cs`: the full suite plus all repro tests went green.

```diff
         app.MapGet("/api/presets/{name}", (string name) =>
         {
-            var preset = Presets.Load(name);
+            JsonElement? preset;
+            try { preset = Presets.Load(name); }
+            catch (InvalidDataException) { preset = null; }   // name sanitizes to empty: not a preset
             return preset == null
                 ? Results.Json(new { error = "no such preset" }, Json.Web, statusCode: 404)
                 : Results.Json(preset, Json.Web);
         });
```

- **Why this is minimal and why 404.** A name that cannot denote a file cannot name an existing preset. Reusing the existing 404 body means the wizard's existing `if (preset.error) return alert(preset.error)` (`app.js:517`) handles it with no new UI (R1–R10 intact).
- **Alternatives rejected.**
  - Changing `Presets.Load` to return `null`: that is in `Presets.cs`, owned by GAP-C03, which is changing its name rules anyway. The route-level mapping survives that change.
  - Returning 400: also acceptable, and the test allows either, but 404 needs no new string.
- **Fences.** No filesystem or deletion behavior changes. No new dialogs. No existing test touches this route, so no pin is affected.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** `docs/security_register.md` is active. Fold into **GAP-C03** (presets) as an added AC: the GET route returns a named 404/400 for an invalid name. The owner must pin it; otherwise it needs a new owner-pinned ID.
   > *Pinned 2026-11-23 as **GAP-R05** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup:** `git worktree add ../nvcleanclone-preset404 -b feat/preset-load-invalid-name-404 origin/main`.
2. **Red.** Add §A at `nvcleanstall/CleanDriver.Tests/PresetRouteTests.cs` and run the filter. All 4 cases fail with `expected 404/400, got 500`. Commit `"preset-load-invalid-name-500 (red): GET /api/presets/{name} answers unloadable names with 404"`.
3. **Green.** Apply the diff and run the FULL suite. Commit `"preset-load-invalid-name-500: map Presets.Load's invalid-name InvalidDataException to 404"`.
4. **Refactor**, with the suite green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Rerun §B step 1. All three requests must print `HTTP/1.1 404` with `{"error":"no such preset"}`. Then save a preset with `curl -s -X POST localhost:4780/api/presets -H "Content-Type: application/json" -d '{"name":"Gaming","selection":{"components":["display-driver"],"tweaks":{}}}'` and confirm `curl -s localhost:4780/api/presets/Gaming` still returns it.
7. **PR.** Fill the template:
   - TDD row with red/green output.
   - Edited-test disclosure: none.
   - Deviations: none.
   - The live transcript.

   Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green first.
2. **Isolated worktree:** `git worktree add ../nvcleanclone-review-preset404 origin/feat/preset-load-invalid-name-404`. The range is `origin/main...HEAD`.
3. **Re-run the gates**, then run §B live.
4. **Finder pass** (optionally `/code-review` on the diff):
   - Is the catch limited to `InvalidDataException`? A malformed preset file (`JsonException` from `JsonDocument.Parse`) is BUG-06 / GAP-C03 territory and must not be silently turned into "no such preset" by this PR unless the owner rules so.
   - Is the POST route unchanged?
   - Does the error body contain no path?
5. **Adversarial verify:**
   - `GET /api/presets/%2E%2E%2F..` must not reach outside `presets/`. Sanitization strips it, so expect 404.
   - A very long name (10 kB) must give 404, not 500.
   - Write `presets/jp.json` with content name `日本`, then call `GET /api/presets` followed by the encoded GET. Expect 404, and the list still works.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with findings marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Fix loop: C0 Orient, C1 reproduce red, C2 fix, C3 verify ACs, C4 gates, C5 one `F<n>:` commit per finding, C6 closing report, C7 STOP.
8. **Report to owner.** Review comes before relay. Only the owner merges.

## Acceptance criteria
1. `GET /api/presets/{name}` returns `404 {"error":"no such preset"}` (or a named 400) for any name `Presets.SafeName` rejects: `!`, non-ASCII, whitespace-only and dots-only.
2. An existing preset still loads with `200` and its stored JSON. An ordinary missing name still returns 404.
3. No request to this route produces an unhandled-exception log entry for an invalid name.
4. The full suite is green; `POST /api/presets` behavior is unchanged.
