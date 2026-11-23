# `POST /api/presets` persists a preset with no `selection`, which the wizard's Load handler then crashes on

| Field | Value |
|---|---|
| **Slug** | `preset-save-accepts-null-selection` |
| **Severity** | Low |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness); also confirmed by live repro (the frontend crash was reproduced by evaluating the real `applySelection` from `app.js` in Node) |
| **Area** | `nvcleanstall/Api.cs` (`POST /api/presets`, `PresetSaveRequest`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | Not covered by **BUG-01 / GAP-C01**, which validates selections only on `/api/execute`; its "valid preset selections remain green" clause assumes a selection exists. Not covered by **BUG-06 / GAP-C03**, which covers names, collisions, atomic writes and malformed-file tolerance, not a missing `selection` member. Could fold into GAP-C03 as an added AC. |
| **Found** | 2026-11-23 |

## Summary
`PresetSaveRequest` declares `Selection` as non-nullable, but System.Text.Json binds a missing or `null` `selection` to `null`, and the route passes it straight to `Presets.Save`. Save writes the preset with `WhenWritingNull`, so the `selection` key is simply omitted. It returns `200` and the preset shows up in the Load list. Loading that preset in the wizard calls `applySelection(undefined)`, which throws `TypeError` on `sel.components` or `sel.tweaks`. The popover closes and nothing is applied.

## Evidence
- `nvcleanstall/Api.cs:17`. The non-nullable annotation is not enforced at runtime:
  ```csharp
  private record PresetSaveRequest(string Name, Selection Selection);
  ```
- `nvcleanstall/Api.cs:128-132`. The only validation is the name check inside `Presets.Save`:
  ```csharp
  app.MapPost("/api/presets", (PresetSaveRequest req) =>
  {
      try { return Results.Json(Presets.Save(req.Name, req.Selection), Json.Web); }
      catch (InvalidDataException ex) { return Results.Json(new { error = ex.Message }, Json.Web, statusCode: 400); }
  });
  ```
- `nvcleanstall/Lib/Presets.cs:30-36`. Save serializes whatever it is given, and `Json.Web` (`Models.cs:64-68`, `DefaultIgnoreCondition = WhenWritingNull`) drops the null member:
  ```csharp
  var preset = new { name = n, savedAt = DateTime.UtcNow.ToString("o"), selection };
  File.WriteAllText(Path.Combine(PresetDir, $"{n}.json"), JsonSerializer.Serialize(preset, Json.Web));
  ```
- `nvcleanstall/wwwroot/app.js:515-519` hands `preset.selection` (here `undefined`) to `applySelection`. `app.js:462-468` dereferences it unconditionally:
  ```js
  function applySelection(sel) {
    if (state.manifest && Array.isArray(sel.components)) {   // TypeError when a package is loaded
    ...
    state.tweaks = { ...(sel.tweaks || {}) };                  // TypeError when none is loaded
  ```

The route assumes the request record's types are already validated. Nothing checks that the one field a preset exists to carry is actually there.

## Impact
- Any non-wizard client can create a preset that the wizard cannot load: a script, an older or newer client, or a hand-crafted `curl`. The wizard always sends a selection object. The broken preset is persisted to `presets/` and appears in the "Load preset" list, so the damage outlives the request. Clicking it does nothing visible; the console shows `TypeError: Cannot read properties of undefined (reading 'components')`.
- No data loss, and the shipped UI cannot produce such a preset. That makes this **Low**.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/PresetRouteTests.cs` (the same in-process host helper as the sibling preset-route test). The test deletes only the single named file it may have written.

```csharp
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CleanDriver.Tests;

public class PresetSaveValidationTests
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

    [Theory]
    [InlineData("{\"name\":\"repro-nosel-a\"}")]
    [InlineData("{\"name\":\"repro-nosel-b\",\"selection\":null}")]
    public async Task SavePreset_WithoutSelection_Returns400_AndWritesNothing(string body)
    {
        var name = body.Contains("-a") ? "repro-nosel-a" : "repro-nosel-b";
        var file = Path.Combine(Presets.PresetDir, $"{name}.json");
        try
        {
            if (File.Exists(file)) File.Delete(file);   // a single named file, never recursive
            var (app, http) = await StartApiAsync();
            await using var host = app;

            var resp = await http.PostAsync("/api/presets", new StringContent(body, Encoding.UTF8, "application/json"));

            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
            Assert.False(File.Exists(file), "a preset with no selection was persisted");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
```

Command:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~PresetSaveValidationTests"
```

RED, Linux net10.0 harness output (class named `PresetSaveAcceptsNullSelectionRepro` there):
```
  Failed CleanDriver.Tests.PresetSaveAcceptsNullSelectionRepro.SavePreset_WithoutSelection_Returns400_AndWritesNothing(body: "{\"name\":\"repro-nosel-a\"}") [326 ms]
  Error Message:
   Assert.Equal() Failure: Values differ
Expected: BadRequest
Actual:   OK
  Failed ...SavePreset_WithoutSelection_Returns400_AndWritesNothing(body: "{\"name\":\"repro-nosel-b\",\"selection\":null}") [50 ms]
Expected: BadRequest
Actual:   OK
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 368 ms - Harness.Tests.dll (net10.0)
```

### B. Live — running app
Start `dotnet run --project nvcleanstall -- --headless --mock-catalog` (port 4780). The output below was captured on the Linux headless harness, port 4803.

1. Save without a selection. Expected `400`. Actual:
   ```
   $ curl -s -i -X POST localhost:4780/api/presets -H "Content-Type: application/json" -d '{"name":"gaming-lite"}'
   HTTP/1.1 200 OK
   { "name": "gaming-lite", "savedAt": "2026-11-23T12:52:24.0323663Z" }
   ```
2. The preset is persisted and listed, with no `selection` key:
   ```
   $ cat <exe dir>/presets/gaming-lite.json
   { "name": "gaming-lite", "savedAt": "2026-11-23T12:52:24.0323663Z" }
   $ curl -s localhost:4780/api/presets
   { "presets": [ ..., { "name": "gaming-lite", "savedAt": "2026-11-23T12:52:24.0323663Z" } ] }
   $ curl -s localhost:4780/api/presets/gaming-lite
   { "name": "gaming-lite", "savedAt": "2026-11-23T12:52:24.0323663Z" }
   ```
3. The wizard's Load handler on that response. This evaluates the real `applySelection` source extracted from `nvcleanstall/wwwroot/app.js`, with the response above as `preset`:
   ```
   $ node -e '…eval(applySelection from app.js); applySelection(preset.selection) …'
   TypeError: Cannot read properties of undefined (reading 'components')   # state.manifest set
   TypeError: Cannot read properties of undefined (reading 'tweaks')       # state.manifest null
   ```
   In the browser: open Load preset, click "gaming-lite", and the popover closes with nothing applied. The DevTools console shows the TypeError above.

## Root cause
The route relies on C# nullability (`Selection Selection`) as if it were runtime validation. System.Text.Json does not honor reference-type nullability by default, so a missing or `null` member binds to `null`. `Presets.Save` stores whatever it receives, and `WhenWritingNull` hides the problem until the frontend consumes the preset.

## Remedy
Reject a missing selection at the route. This was validated in a scratch copy of `Api.cs`: the full suite plus repros went green.

```diff
         app.MapPost("/api/presets", (PresetSaveRequest req) =>
         {
+            if (req.Selection is null)
+                return Results.Json(new { error = "selection required" }, Json.Web, statusCode: 400);
             try { return Results.Json(Presets.Save(req.Name, req.Selection), Json.Web); }
```

- **Why this is minimal.** It is one guard in the route that owns request validation. The 400 body flows through the wizard's existing `if (r.error) alert(r.error)` (`app.js:503`), so there is no new UI. A selection whose members are `null` (`{"components":null}`) is serialized without those members, and `applySelection` already tolerates that (`Array.isArray` / `|| {}`), so it is left alone.
- **Alternatives rejected.**
  - Defaulting to `new Selection()`: this would silently save an empty preset the user never asked for.
  - A frontend guard in `applySelection`: that is the frontend's area, and it would hide bad data rather than refuse it. Optional defense in depth under A6 (no JS runner).
  - A global `RespectNullableAnnotations`: changes every route's 400 semantics.
- **Fences.** No delete or path changes. No new dialog (R1–R10). No existing test touches this route.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** `docs/security_register.md` is active. Fold into **GAP-C03** (presets) as an added AC ("a save without a selection is rejected and writes nothing"). The owner must pin it; otherwise it needs a new owner-pinned ID.
1. **Slice setup:** `git worktree add ../nvcleanclone-presetsel -b feat/preset-save-requires-selection origin/main`.
2. **Red.** Add §A at `nvcleanstall/CleanDriver.Tests/PresetSaveValidationTests.cs` and run the filter. Both cases fail with `Expected: BadRequest / Actual: OK`. Commit `"preset-save-accepts-null-selection (red): POST /api/presets requires a selection"`.
3. **Green.** Apply the guard and run the FULL suite. Commit `"preset-save-accepts-null-selection: reject a preset save without a selection"`.
4. **Refactor**, with the suite green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** §B step 1 must now print `HTTP/1.1 400` with `{"error":"selection required"}`, and `ls <exe dir>/presets/` must not contain `gaming-lite.json`. A wizard-shaped save (`{"name":"Gaming","selection":{"components":["display-driver"],"tweaks":{}}}`) still returns 200 and loads back.
7. **PR.** Fill the template:
   - TDD row.
   - Edited-test disclosure: none.
   - Deviations: none.
   - The live transcript.

   Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green.
2. **Isolated worktree:** `git worktree add ../nvcleanclone-review-presetsel origin/feat/preset-save-requires-selection`. The range is `origin/main...HEAD`.
3. **Re-run the gates**, then run §B.
4. **Finder pass:**
   - Does the guard run before `Presets.Save`, so no file and no directory creation happen?
   - Is the error string fixed?
   - Is the name validation order unchanged? `{}` still yields `"preset name required"` or `"selection required"`; pick one and pin it in the test.
5. **Adversarial verify:**
   - `{"name":"x","selection":{}}` must be accepted, since an empty selection is a legitimate "nothing" preset.
   - `{"name":"x","selection":[]}` must give the framework's 400 (JSON type mismatch).
   - `{"selection":{"components":[]}}` must still give `"preset name required"`.
   - Run 20 concurrent saves of a null selection: no file appears.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with findings marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Fix loop C0–C7: Orient, then reproduce red, fix, verify ACs, gates, one `F<n>:` commit per finding, closing report, STOP.
8. **Report to owner.** Review comes before relay. Only the owner merges.

## Acceptance criteria
1. `POST /api/presets` with a missing or `null` `selection` returns `400 {"error":"selection required"}`.
2. Such a request creates no file under `presets/`.
3. A save with a selection object (including `{}`) behaves exactly as before, and the saved preset loads back via `GET /api/presets/{name}` with its `selection`.
4. The full suite is green.
