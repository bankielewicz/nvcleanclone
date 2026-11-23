# A manifest with `null` collections crashes the validator: `NullReferenceException` gives HTTP 500 instead of 400

| Field | Value |
|---|---|
| **Slug** | `manifest-null-collections-crash-validator` |
| **Severity** | Low. Bounded robustness: one request fails with an unstable 500 instead of GAP-S01's stable 400. No state is corrupted, and nothing is written. |
| **Confidence** | Confirmed: the repro test fails on HEAD (Linux harness), and it was reproduced live. |
| **Area** | `nvcleanstall/Lib/Packages.cs` (`ReadManifest`), `nvcleanstall/Lib/PackageValidation.cs` (`ValidateManifest`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | GAP-S01 AC-2 ("Each malicious case returns HTTP 400 with a stable non-path-leaking error") is not met for structurally malformed manifests. **Sibling doc:** `docs/bugs/execute-malformed-body-500` covers the same JSON-`null`-collection pattern on `/api/execute`'s `Selection` (`Api.cs`/`Models.cs`). This doc is the `/api/package` manifest side, rooted in the package validator. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R03** — Package input hardening (GAP-S01 follow-ups) (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
System.Text.Json assigns an explicit JSON `null` to a collection property, which overrides the `= new()` initializer in `Models.cs`. Three manifests show the problem:
- `{"components": null}`
- `{"components": [null]}`
- a component with `"dependsOn": null`

Each deserializes to a `null` list or element. `ReadManifest` (`.Components.Count`, `.Any(c => c.Required)`) and `ValidateManifest` (`foreach (var dep in c.DependsOn)`) then dereference it and throw `NullReferenceException`. `/api/package` catches only `InvalidDataException`, `IOException`, `JsonException` and `UnauthorizedAccessException`, so the client gets a bodiless HTTP 500 rather than the validator's stable 400 rejection.

## Evidence
`Models.cs:22` / `:13`. The initializers don't survive an explicit `null`:
```csharp
public List<ComponentDef> Components { get; init; } = new();
public List<string> DependsOn { get; init; } = new();
```
`Packages.cs:81-87`. These lines dereference without a null check:
```csharp
var m = JsonSerializer.Deserialize<Manifest>(json, Json.Web) ?? throw new InvalidDataException("invalid manifest");
if (string.IsNullOrEmpty(m.Version) || m.Components.Count == 0)        // :83 — NRE when components is null
    throw new InvalidDataException("invalid manifest: version and non-empty components[] required");
if (!m.Components.Any(c => c.Required))                                // :85 — NRE on a null element
    ...
PackageValidation.ValidateManifest(m);
```
`PackageValidation.cs:81-83`:
```csharp
foreach (var c in manifest.Components)
    foreach (var dep in c.DependsOn)                                   // NRE when dependsOn is null
        if (!ids.Contains(dep)) throw new InvalidDataException(MissingDependency);
```
`Api.cs:65-74`, the catch list, which has no `NullReferenceException`/`Exception` fallback:
```csharp
catch (InvalidDataException ex) { ... 400 }
catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { ... 400 }
```
By contrast, null scalar fields are already handled: `IsNullOrEmpty` covers `version`, `id` and `payload`, giving `BadVersion`/`BadComponentId`/`BadPayload`. Only the collections fall through.

## Impact
- A malformed local package (folder or zip) makes `POST /api/package` return HTTP 500 with an empty body. The wizard then shows a generic failure instead of the validator's stable reason. AC-2's "stable … error" contract is broken for this input class.
- If the host ever runs in the Development environment, the developer exception page would render a stack trace with absolute source paths. That would break the "never leak absolute filesystem paths" intent stated at `Api.cs:72`. The default is Production, so this is noted, not claimed.
- No files are touched. Impact is limited to the failed request plus a logged unhandled exception.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/PackageValidationTests.cs`. It's the same as the harness `repro/manifest-null-collections-crash-validator.cs`.
```csharp
// GAP-S01 AC-2: structurally malformed manifests (JSON null where a collection is expected)
// are rejected as InvalidDataException — the type Api.cs maps to a stable HTTP 400 —
// never a NullReferenceException (unhandled → 500).
[Theory]
[InlineData("""{"version":"572.16","channel":"WHQL","components":null}""")]
[InlineData("""{"version":"572.16","channel":"WHQL","components":[null]}""")]
[InlineData("""{"version":"572.16","channel":"WHQL","components":[{"id":"display-driver","name":"d","required":true,"payload":"display-driver.bin","dependsOn":null}]}""")]
public void LoadLocal_ManifestWithNullCollection_RejectsAsInvalidData(string json)
{
    var pkg = Path.Combine(TempDir.Create(), "pkg");
    Directory.CreateDirectory(Path.Combine(pkg, "payload"));
    File.WriteAllText(Path.Combine(pkg, "manifest.json"), json);

    var ex = Record.Exception(() => Packages.LoadLocal(pkg));

    Assert.NotNull(ex);
    Assert.IsType<InvalidDataException>(ex);
    AssertPathFree(ex.Message);
}
```
Run:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~LoadLocal_ManifestWithNullCollection_RejectsAsInvalidData"
```
RED output, trimmed, from the Linux `net10.0` harness at `54b3c31` (class `ManifestNullCollectionsCrashValidatorRepro`):
```
Failed ...LoadLocal_ManifestWithNullCollection_RejectsAsInvalidData(json: "{\"version\":\"572.16\",\"channel\":\"WHQL\",\"com"···)   ×3
  Error Message:
Expected: typeof(System.IO.InvalidDataException)
Actual:   typeof(System.NullReferenceException)
Failed!  - Failed: 3, Passed: 0
```

### B. Live — running app
Captured with the Linux harness on port 4802.
1. Make four folders `C:/tmp/l6/p1..p4`, each with an empty `payload/` and one of these `manifest.json` bodies:
   - p1: `{"version":"572.16","channel":"WHQL","components":null}`
   - p2: `{"version":"572.16","channel":"WHQL","components":[null]}`
   - p3: `{"version":"572.16","channel":"WHQL","components":[{"id":"display-driver","name":"d","required":true,"payload":"display-driver.bin","dependsOn":null}]}`
   - p4 (control): `{"version":"572.16","channel":"WHQL","components":[]}`
2. For each, run `curl -s -w " [HTTP %{http_code}]" -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"local","path":"C:/tmp/l6/pN"}'`.

**Expected:** HTTP 400 with a stable `invalid manifest…` / `manifest rejected…` message for p1-p3.
**Actual (captured):**
```
manifest 1 (components:null)     [HTTP 500]   (empty body)
manifest 2 (components:[null])   [HTTP 500]
manifest 3 (dependsOn:null)      [HTTP 500]
manifest 4 (components:[])       {"error":"invalid manifest: version and non-empty components[] required"} [HTTP 400]   <- control
server log: 3 × System.NullReferenceException
```

## Root cause
The validator assumes deserialization has already enforced shape: lists are non-null, elements are non-null. System.Text.Json does not enforce non-nullable reference types by default. `RespectNullableAnnotations` is off in `Json.Web`, and an explicit `null` replaces the property initializer. The GAP-S01 validator checks *values* (regexes, duplicates, edges) but not the *structure* those checks walk.

## Remedy
Reject null structure up front, with stable path-free messages, in the same validator layer:
```csharp
// Packages.ReadManifest — before the Count/Any checks
if (m.Components is null || m.Components.Any(c => c is null))
    throw new InvalidDataException("invalid manifest: version and non-empty components[] required");

// PackageValidation.ValidateManifest — in the per-component loop
if (c.DependsOn is null || c.DependsOn.Any(d => d is null))
    throw new InvalidDataException(MissingDependency);   // or a new const BadDependsOn, path-free
```
Why this is minimal: two guards at the exact dereference points, reusing existing messages. There is no serializer-option change.

Alternatives rejected:
- Setting `RespectNullableAnnotations = true` on the shared `Json.Web`. That changes deserialization for every model, including `Selection` and catalog data, and turns these cases into `JsonException`, i.e. the generic "could not load package". It's too broad a blast radius.
- A catch-all in `Api.cs`. That hides the gap and is out of this area.

Fences respected: no filesystem change, no UI change, and messages stay path-free (watchpoint 5). All existing `PackageValidationTests` rows stay green, because they never send `null` collections.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** Settled: slice **GAP-R03**, the validator slice shared with `validation-regex-allows-newline-and-unicode` and `dependency-target-check-ignores-case`, one TDD row per behavior (a GAP-S01 AC-2 follow-up).
   > *Pinned 2026-11-23 as **GAP-R03** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-nullmanifest -b feat/manifest-null-collections origin/main`
2. **Red.** Add the §A theory to `PackageValidationTests.cs` and run its filter. Three failures, `NullReferenceException`. Commit `"GAP-R03 (red): manifest-null-collections-crash-validator — null manifest collections must be a 400-class rejection"`.
3. **Green.** Add the two guards and run the full suite. Commit `"GAP-R03: manifest-null-collections-crash-validator — reject null components/dependsOn in the manifest validator"`.
4. **Refactor.** Green only.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run §B. p1-p3 now return HTTP 400 with the stable messages, and p4 is unchanged.
7. **PR.** Template TDD row (×3 inline data). No existing test edited. Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-nullmanifest origin/feat/manifest-null-collections`; range `origin/main...HEAD`.
3. **Re-run gates**, then §B live.
4. **Finder pass.** Check:
   - Every collection the validator walks is null-guarded: `Components`, the elements, `DependsOn` and its elements.
   - Guards come before the first dereference.
   - Messages are constants and path-free.
   - The zip load path reaches the same `ReadManifest`, so it's covered without separate code.
5. **Adversarial verify.** Try:
   - `"components": {}` (an object, which should be `JsonException` → 400).
   - `"dependsOn": [null, "display-driver"]`.
   - `"components": [1]`.
   - Deeply nested JSON (depth > 64 → `JsonException` → 400).
   - A manifest that is the JSON literal `null` (already 400).
   - The same p1-p3 bodies inside a `.zip`.
6. **Verdict document.** APPROVE / REQUEST CHANGES.
7. **Feedback to builder.** Fix loop C0 orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 `F<n>:` commits → C6 closing report → C7 STOP.
8. **Report to owner.** Verdict line; owner merges.

## Acceptance criteria
1. `Packages.LoadLocal` of a folder or zip whose manifest has `components: null`, a `null` component element, or `dependsOn: null` (or a `null` element in it) throws `InvalidDataException` with a stable, path-free message.
2. `POST /api/package` for each of those returns HTTP 400 with that message, never 500.
3. All existing `PackageValidationTests` stay green and unedited.
