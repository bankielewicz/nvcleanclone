# Dependency-target check is case-insensitive, so a dangling `dependsOn` edge passes and crashes the component screen

| Field | Value |
|---|---|
| **Slug** | `dependency-target-check-ignores-case` |
| **Severity** | Low. A malformed local package passes validation (HTTP 200) and then breaks the wizard's component screen with a JavaScript `TypeError`. It is bounded to that session; no files are affected. |
| **Confidence** | Confirmed: the repro test fails on HEAD (Linux harness), and it was reproduced live. The `app.js` consequence was demonstrated by running `app.js:196-219`/`:259` verbatim under node in scratch. No JS toolchain was added to the repo (A6). |
| **Area** | `nvcleanstall/Lib/PackageValidation.cs` (`ValidateManifest`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | GAP-S01 AC-1 lists "missing dependency targets" as a case the validator must reject. Case-variant targets are missing for every consumer, yet they pass. GAP-C01 (server-side selection enforcement, not built yet) will consume these edges too. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R03** — Package input hardening (GAP-S01 follow-ups) (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`ValidateManifest` resolves `dependsOn` targets against `ids`, a `HashSet<string>` built with `StringComparer.OrdinalIgnoreCase`. That comparer is correct for *duplicate* detection, but it also makes `"dependsOn": ["Display-Driver"]` count as resolved when the only component is `display-driver`.

Every consumer of a dependency edge matches ids **ordinally**:
- `app.js` `compById` uses `c.id === id`;
- the selection filters in `WriteCustomized` and `StartExecute` use `List.Contains`.

To all of them the target does not exist. The manifest loads with HTTP 200. Then `renderComponents` dereferences `compById("Display-Driver").name`, gets `undefined.name`, throws, and the component screen never appears.

## Evidence
`PackageValidation.cs:69-83`:
```csharp
var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);          // :69 — right for duplicates…
...
// Every dependency edge must resolve to a declared component.
foreach (var c in manifest.Components)
    foreach (var dep in c.DependsOn)
        if (!ids.Contains(dep)) throw new InvalidDataException(MissingDependency);   // :83 — …wrong for resolution
```
`wwwroot/app.js:196`, the consumers, which are ordinal and assume resolution:
```js
function compById(id) { return state.manifest.components.find(c => c.id === id); }
```
`wwwroot/app.js:259` (renderComponents) and `:207` (setComponent):
```js
const names = esc(hi.dependsOn.map(d => compById(d).name).join(', '));       // :259
panelMsgs.push(`<b>Requires:</b> ${esc(missing.map(d => compById(d).name).join(', '))} …`); // :207
```
`app.js:191-193` (adoptManifest) highlights the first non-required component, then calls `renderComponents()` *before* `show('components')`. So a throw there leaves the wizard stuck on the previous screen.

`Packages.cs:122` / `Jobs.cs:382`, the server-side consumers, which are ordinal:
```csharp
var selected = manifest.Components.Where(c => selectedIds.Contains(c.Id)).ToList();
```

## Impact
- A package whose manifest has a case typo in a dependency is accepted as valid, though GAP-S01 says such edges must be rejected. The user then hits an unexplained dead end: clicking Next does nothing visible, and the console shows `TypeError: Cannot read properties of undefined (reading 'name')`.
- The intended dependency is silently not enforced. `setComponent` adds the bogus id `"Display-Driver"` to the selection set and then throws. After GAP-C01, server-side closure checks keyed ordinally would see a dependency that can never be satisfied. That is a latent inconsistency the validator was supposed to exclude.
- Exact-missing targets are already rejected (HTTP 400), so only case variants slip through.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/PackageValidationTests.cs`. It's the same as the harness `repro/dependency-target-check-ignores-case.cs`.
```csharp
// AC-1 "missing dependency target": resolution is ORDINAL (every consumer — app.js compById,
// Jobs/WriteCustomized selection filters — matches ids exactly), even though duplicate
// detection stays case-insensitive.
[Fact]
public void ValidateManifest_RejectsADependencyWhoseTargetDiffersOnlyInCase()
{
    var m = ManifestOf(
        Comp("display-driver", "display-driver.bin", required: true),
        Comp("physx", "physx.bin", dependsOn: new() { "Display-Driver" }));

    var ex = Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateManifest(m));
    Assert.Equal(PackageValidation.MissingDependency, ex.Message);
}

[Fact]
public void LoadLocal_FolderWithCaseVariantDependency_IsRejected()
{
    var pkg = Path.Combine(TempDir.Create(), "pkg");
    Directory.CreateDirectory(Path.Combine(pkg, "payload"));
    WriteManifest(Path.Combine(pkg, "manifest.json"), """
        { "version":"572.16","channel":"WHQL","components":[
          {"id":"display-driver","name":"Display Driver","required":true,"payload":"display-driver.bin"},
          {"id":"physx","name":"PhysX","payload":"physx.bin","dependsOn":["Display-Driver"]} ] }
        """);
    Assert.Throws<InvalidDataException>(() => Packages.LoadLocal(pkg));
}
```
Run:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~CaseVariantDependency|FullyQualifiedName~DiffersOnlyInCase"
```
RED output, trimmed, from the Linux `net10.0` harness at `54b3c31` (class `DependencyTargetCheckIgnoresCaseRepro`):
```
Failed ...ValidateManifest_RejectsADependencyWhoseTargetDiffersOnlyInCase
  Assert.Throws() Failure: No exception was thrown
  Expected: typeof(System.IO.InvalidDataException)
Failed ...LoadLocal_FolderWithCaseVariantDependency_IsRejected
  Assert.Throws() Failure: No exception was thrown
Failed!  - Failed: 2, Passed: 0
```

### B. Live — running app
Captured with the Linux harness on port 4802.
1. Make `C:/tmp/l8/pkg/manifest.json` with the two-component manifest above (`"dependsOn":["Display-Driver"]`) and an empty `payload/`.
2. `curl -s -o NUL -w "HTTP %{http_code}\n" -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"local","path":"C:/tmp/l8/pkg"}'`
3. Control: change the edge to `"dependsOn":["no-such-id"]` and repeat.
4. Browser (A6-style transcript): open `http://localhost:4780`, choose "Use a driver file on disk", enter `C:/tmp/l8/pkg` and click Next.

**Expected:** step 2 returns HTTP 400 `manifest rejected: dependency target not found`, like step 3.
**Actual (captured):**
```
step 2 → load -> HTTP 200
step 3 → {"error":"manifest rejected: dependency target not found"}  [HTTP 400]   <- control
```
Step 4 consequence: `app.js:196-219` and the `:259` expression were run verbatim under node with this manifest:
```
renderComponents (app.js:259) -> TypeError: Cannot read properties of undefined (reading 'name')
setComponent (app.js:207) -> TypeError: Cannot read properties of undefined (reading 'name')
```
The browser shows the same `TypeError` in DevTools, and the wizard does not advance to the component screen.

## Root cause
One comparer is used for two jobs. Case-insensitive identity is right for rejecting `Display-Driver` + `display-driver` as duplicates: those ids collide in the case-insensitive places, and the stricter rule costs nothing. But dependency resolution has to use the same equality as the consumers that follow the edge, and they all use ordinal equality.

## Remedy
Resolve edges against an ordinal set; keep the case-insensitive set for duplicates:
```csharp
var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);       // duplicate detection (unchanged)
var exactIds = new HashSet<string>(StringComparer.Ordinal);            // resolution, as consumers see it
foreach (var c in manifest.Components) { ...; if (!ids.Add(c.Id)) throw ...; exactIds.Add(c.Id); ... }
foreach (var c in manifest.Components)
    foreach (var dep in c.DependsOn)
        if (!exactIds.Contains(dep)) throw new InvalidDataException(MissingDependency);
```
Why this is minimal: one extra set and one changed lookup in the validator. No consumer changes.

Alternative rejected: making `app.js`/`Jobs` case-insensitive. That spreads a looser identity into several files, the frontend contract and GAP-C01, for no user benefit.

Fences respected: no UI change (R1-R10 intact); messages unchanged and path-free; `ValidateManifest_RejectsMissingDependencyTarget` and `ValidateManifest_RejectsDuplicateId_CaseInsensitive` stay green.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** This is a GAP-S01 AC-1 gap. Request a new ID per §7 (e.g. `BUG-15`, Low) with destination "GAP-S01 follow-up". It's a natural companion of `manifest-null-collections-crash-validator` and `validation-regex-allows-newline-and-unicode` in one validator slice, if the owner pins it that way.
   > *Pinned 2026-11-23 as **GAP-R03** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** `git worktree add ../nvcleanclone-depcase -b feat/dependency-resolution-ordinal origin/main`
2. **Red.** Add both §A tests and run the filter. Both fail with "No exception was thrown". Commit `"BUG-15 (red): dependency targets resolve ordinally"`.
3. **Green.** Add the ordinal set and run the full suite. Commit `"BUG-15: resolve dependsOn against exact component ids"`.
4. **Refactor.** None expected.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** §B step 2 now returns HTTP 400 `manifest rejected: dependency target not found`. The bundled catalog packages still load (their edges are exact), e.g. `POST /api/package {"kind":"catalog","version":"572.16"}` → 200.
7. **PR.** Template TDD rows. No existing test edited. Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-depcase origin/feat/dependency-resolution-ordinal`; range `origin/main...HEAD`.
3. **Re-run gates**, then §B live. Also load every bundled `data/packages/*/manifest.json` to prove none relied on case-folding.
4. **Finder pass.** Check:
   - Duplicate detection is still case-insensitive.
   - Resolution is ordinal.
   - No consumer was changed.
   - `CleanPreviousBuild` is unaffected (it never reads `dependsOn`).
5. **Adversarial verify.** Try:
   - A self-edge (`physx` → `physx`), which is allowed today. Should it be? Record as a PLAUSIBLE follow-up, not this fix.
   - Edges with a trailing space.
   - Edges differing by Unicode case-folding only (`ǅ` vs `ǆ`).
   - A dependency on a *required* component with different case.
6. **Verdict document.** APPROVE / REQUEST CHANGES.
7. **Feedback to builder.** Fix loop C0 orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 `F<n>:` commits → C6 closing report → C7 STOP.
8. **Report to owner.** Verdict line; owner merges.

## Acceptance criteria
1. `ValidateManifest` throws `InvalidDataException(MissingDependency)` for any `dependsOn` entry that does not exactly (ordinally) equal a declared component id.
2. `POST /api/package` with such a local package returns HTTP 400 `manifest rejected: dependency target not found`.
3. Duplicate ids differing only in case are still rejected (`ValidateManifest_RejectsDuplicateId_CaseInsensitive` green, unedited).
4. All bundled catalog packages still load.
