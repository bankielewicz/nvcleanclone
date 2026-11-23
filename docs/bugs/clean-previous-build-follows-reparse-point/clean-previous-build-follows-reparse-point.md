# CleanPreviousBuild deletes through a junction/symlink at `outDir/payload`, outside `outDir`

| Field | Value |
|---|---|
| **Slug** | `clean-previous-build-follows-reparse-point` |
| **Severity** | Medium. The consequence is High-class: files outside `outDir` are deleted, past the D12/D1 deletion boundary. It is graded down per §1 because it needs a stronger precondition: a reparse point already planted inside the chosen output folder. |
| **Confidence** | Confirmed: the repro test fails on HEAD (Linux harness, using a symlink), and the bug was reproduced live. |
| **Area** | `nvcleanstall/Lib/Packages.cs` (`CleanPreviousBuild`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. Windows uses a directory junction (no privilege needed); Unix uses a directory symlink. |
| **Related register entries** | GAP-S01 is incomplete for the delete sink. The Fix says "reject reparse points in source/output traversal" and "`CleanPreviousBuild` may delete only canonical, previously validated leaf payloads". AC-1 ("a reparse-point escape") and AC-2 ("leaves sentinel files outside source/output untouched") are proven only for the write and read sinks. |
| **Found** | 2026-11-23 |

## Summary
GAP-S01 routes the payload write and the folder-source read through `PackageValidation.EnsureConfinedRealPath`, which rejects any reparse point between the root and the target. The payload delete in `CleanPreviousBuild` still uses a bare `Path.Combine` and has no reparse check.

If `outDir/payload` is a junction or symlink to another directory, and `outDir/manifest.json` is CleanDriver-stamped, the clean step `File.Delete`s the named files in the junction's target, outside `outDir`. Only after that does `WriteCustomized` hit `EnsureConfinedRealPath`, reject the reparse point, and fail the job. The deletion has already happened.

## Evidence
`Packages.cs:204-211`. The delete path is a plain combine, filtered only by the leaf-name check:
```csharp
if (root.TryGetProperty("components", out var comps) && comps.ValueKind == JsonValueKind.Array)
    foreach (var c in comps.EnumerateArray())
        if (... && PackageValidation.IsSafeLeafPayload(pay.GetString()!))
            Remove(Path.Combine(outDir, "payload", pay.GetString()!), $"payload/{pay.GetString()}");   // :211
...
void Remove(string abs, string display) { if (!File.Exists(abs)) return; File.Delete(abs); ... }  // :228-233
```
Compare `Packages.cs:98`, the write sink, which is confined and reparse-checked:
```csharp
var destPath = PackageValidation.EnsureConfinedRealPath(outDir, Path.Combine("payload", payloadFile));
```
And `PackageValidation.cs:126-136`, the check that the delete sink skips:
```csharp
if ((File.Exists(cur) || Directory.Exists(cur))
    && File.GetAttributes(cur).HasFlag(FileAttributes.ReparsePoint))
    throw new InvalidDataException(ReparsePoint);
```
Ordering: `Jobs.cs:487-492` runs the clean step before the `WriteCustomized` step at `:497-506`. So the write side's reparse rejection always comes after the delete.

## Impact
- The delete is name-limited, but the names are attacker-chosen. Any file in the junction target whose name matches `^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$` can be removed with the CleanDriver user's rights. That covers `config.xml`, `settings.json`, `NTUSER.DAT`-style names, and so on.
- Junctions need no privilege on NTFS and may point anywhere. A low-privileged user who can write into a folder that a more privileged CleanDriver user later picks as output gets a junction-redirected delete. For example: a shared `C:\Users\Public\...` folder, or a team share. This is the classic Windows arbitrary-delete primitive.
- The owner ruling D12/D1 says deletion is only of artifacts a CleanDriver manifest declares *inside outDir*. Here deletion leaves `outDir`.
- Benign variant: the user's own `payload` junction (for example to a bigger drive). The clean deletes through it, and then the job fails anyway.

## How to reproduce
### A. Automated — failing xunit test
Add as `nvcleanstall/CleanDriver.Tests/CleanReparsePointTests.cs`. It's the same as the harness `repro/clean-previous-build-follows-reparse-point.cs`, except that the Windows link uses `mklink /J` like `PackageValidationTests.TryMakeJunction`, because `CreateSymbolicLink` needs Developer Mode on Windows.
```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

public class CleanReparsePointTests
{
    // A directory reparse point: a junction on Windows (no privilege), a symlink elsewhere.
    private static bool TryMakeDirLink(string link, string target)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(link, target);
                return Directory.Exists(link);
            }
            var psi = new ProcessStartInfo("cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in new[] { "/c", "mklink", "/J", link, target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        catch { return false; }
    }

    [Fact]
    public void CleanPreviousBuild_PayloadDirIsReparsePoint_DeletesNothingOutsideOutDir()
    {
        var root = TempDir.Create();
        var victim = Path.Combine(root, "victim");
        Directory.CreateDirectory(victim);
        var sentinel = Path.Combine(victim, "important.dat");
        File.WriteAllText(sentinel, "do not touch");

        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(outDir);
        if (!TryMakeDirLink(Path.Combine(outDir, "payload"), victim)) return;   // FS without links
        File.WriteAllText(Path.Combine(outDir, "manifest.json"),
            """{ "customizedBy":"CleanDriver","components":[ {"payload":"important.dat"} ] }""");

        var removed = Packages.CleanPreviousBuild(outDir);   // must not throw (ForeignOrMalformed pin)

        Assert.True(File.Exists(sentinel),
            $"CleanPreviousBuild deleted a file outside outDir through a reparse point (removed: {string.Join(", ", removed)})");
    }

    [Fact]
    public async Task ExtractJob_PayloadDirIsReparsePoint_FailsAndDeletesNothingOutside()
    {
        var root = TempDir.Create();
        var victim = Path.Combine(root, "victim");
        Directory.CreateDirectory(victim);
        var sentinel = Path.Combine(victim, "physx.bin");
        File.WriteAllText(sentinel, "user data that happens to share a payload name");

        var outDir = Path.Combine(root, "out");
        Directory.CreateDirectory(outDir);
        if (!TryMakeDirLink(Path.Combine(outDir, "payload"), victim)) return;
        File.WriteAllText(Path.Combine(outDir, "manifest.json"),
            """{ "customizedBy":"CleanDriver","components":[ {"payload":"physx.bin"} ] }""");

        var (manifest, source) = Packages.LoadCatalog("572.16");
        var job = Jobs.StartExecute("extract", manifest, source,
            new Selection { Components = new() { "display-driver" } }, outDir, Gpu.Simulated);
        await job.Completion!;

        Assert.Equal("failed", job.Status);                       // write side rejects the link (today and after the fix)
        Assert.Equal(PackageValidation.ReparsePoint, job.Error);
        Assert.True(File.Exists(sentinel), "the clean step deleted through the reparse point before the write side rejected it");
    }
}
```
Run:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~CleanReparsePointTests"
```
RED output, trimmed, from the Linux `net10.0` harness at `54b3c31` (class `CleanPreviousBuildFollowsReparsePointRepro`):
```
Failed ...CleanPreviousBuild_PayloadDirIsReparsePoint_DeletesNothingOutsideOutDir
  CleanPreviousBuild deleted a file outside outDir through a reparse point (removed: payload/important.dat, manifest.json)
Failed ...ExtractJob_PayloadDirIsReparsePoint_FailsButHasAlreadyDeletedOutside
  …but the clean step already deleted through it
Failed!  - Failed: 2, Passed: 0
```
The second test's first two assertions pass on HEAD (`failed` / `ReparsePoint`). That proves the order: the write side rejects correctly, but only after the delete.

### B. Live — running app
Captured with the Linux harness on port 4802 using `ln -s`. On Windows use `mklink /J C:\tmp\l2\out\payload C:\tmp\l2\victim`.
1. `victim/physx.bin` ("user data"). `out/payload` is a link to `victim`. `out/manifest.json` = `{"customizedBy":"CleanDriver","components":[{"payload":"physx.bin"}]}`.
2. `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"catalog","version":"572.16"}'` gives the token.
3. `curl -s -X POST localhost:4780/api/execute -H "Content-Type: application/json" -d '{"token":"<t>","action":"extract","selection":{"components":["display-driver"],"tweaks":{}},"outputPath":"C:/tmp/l2/out"}'`
4. `curl -s localhost:4780/api/jobs/<id>`; then `dir C:\tmp\l2\victim`.

**Expected:** nothing outside `out` is deleted, and the job fails with the reparse-point error.
**Actual (captured):**
```
failed | package rejected: a path crosses a reparse point
  > Cleaning previous CleanDriver build: payload/physx.bin, manifest.json
  > Extracting package 572.16…
  > ERROR: package rejected: a path crosses a reparse point
victim dir now: (empty — physx.bin deleted)
```

## Root cause
GAP-S01 hardened each write/read sink by funneling it through `EnsureConfinedRealPath`. The delete sink only got the leaf-name filter (`IsSafeLeafPayload`). That filter stops `..` and rooted names, but a safe leaf under a redirected directory is still outside the root. The "Sink 4" comment at `Packages.cs:199-203` treats leaf-name validation as sufficient confinement, and it isn't: confinement also requires that the traversal is free of reparse points, which is the very property the write sink checks.

## Remedy
Route the payload delete through the same confinement and keep the never-throws contract. A reparse point or escape makes the entry be skipped, never deleted:
```csharp
// Packages.cs, inside the components loop (replacing the bare Path.Combine at :211)
string abs;
try { abs = PackageValidation.EnsureConfinedRealPath(outDir, Path.Combine("payload", name)); }
catch (InvalidDataException) { continue; }   // escape / reparse point → skip, never delete
Remove(abs, $"payload/{name}");
```
Optionally apply the same to the `tweak-*.reg` and fixed-name removals at `:215-224`. They sit directly under `outDir`, so only a leaf symlink applies there, and `File.Delete` removes the link itself. That is harmless, so it isn't needed for this fix.

Why this is minimal: it reuses the existing validator, one call site, and no new API.

Alternative rejected: running `WriteCustomized`'s confinement before the clean step. That reorders the HARD-01 pipeline and still leaves `CleanPreviousBuild` unsafe for any other caller.

Fences respected:
- Enumerable-by-name only, no new deletion, nothing recursive. `CleanPath_ContainsNoRecursiveDeletePrimitives` stays green.
- `ForeignOrMalformedManifest_NothingIsDeleted` and `CleanPreviousBuild_MaliciousPriorManifestPayload_DeletesNothingOutside` are unaffected, because the method still never throws.
- No UI or honesty-marker change.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** This is a GAP-S01 acceptance gap (AC-1/AC-2 for the delete sink). Per §7 it needs a new ID (e.g. `SEC-08`, Medium), with destination "GAP-S01 follow-up". The owner pins it before work starts.
1. **Slice setup.** `git worktree add ../nvcleanclone-cleanreparse -b feat/clean-previous-build-reparse origin/main`
2. **Red.** Add `nvcleanstall/CleanDriver.Tests/CleanReparsePointTests.cs` (§A). Run `--filter "FullyQualifiedName~CleanReparsePointTests"` and confirm both fail because the sentinel was deleted. Commit `"SEC-08 (red): the HARD-01 clean must not delete through a reparse point"`.
3. **Green.** Make the one-site change above and run the full suite. Commit `"SEC-08: confine CleanPreviousBuild payload deletes with EnsureConfinedRealPath"`.
4. **Refactor.** Only green. For example, extract a `TryConfined(outDir, rel, out abs)` helper.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run §B on Windows with `mklink /J`. The log has no `Cleaning previous CleanDriver build: payload/physx.bin`, `victim\physx.bin` survives, and the job is still `failed` with `package rejected: a path crosses a reparse point`.
7. **PR.** Template TDD rows for both tests. No existing test edited. Live transcript. Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green first. Note whether the Windows CI agent actually created the junction: the tests `return` early if `mklink` fails. A vacuous green is a finding.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-cleanreparse origin/feat/clean-previous-build-reparse`; range `origin/main...HEAD`.
3. **Re-run gates**, then run §B live with a real junction.
4. **Finder pass.** Check:
   - Every delete in `CleanPreviousBuild` whose path crosses a subdirectory goes through `EnsureConfinedRealPath`.
   - A rejection *skips*; it never throws and never aborts the rest of the clean.
   - The `removed` list never names a skipped file.
   - No recursive primitive names appear (the guard test scans comments too).
   - The tests are not vacuous on CI.
5. **Adversarial verify.** Try:
   - A junction at `payload/` with a target on another drive.
   - A leaf symlink `payload/x.bin` → outside file. Expect a skip, or deletion of the link only, never the target.
   - The prior manifest naming the same payload twice.
   - A junction created *after* `LoadCatalog` but before the job's first step (a TOCTOU note; record it as PLAUSIBLE if not closed).
6. **Verdict document.** APPROVE / REQUEST CHANGES. Findings marked CONFIRMED / PLAUSIBLE.
7. **Feedback to builder.** Punch list as a PR comment. Fix loop C0 orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Verdict line; only the owner merges.

## Acceptance criteria
1. With `outDir/payload` a directory junction (Windows) or symlink (elsewhere) to an outside directory and a CleanDriver-stamped prior manifest naming a file there, `CleanPreviousBuild` deletes nothing outside `outDir`. It does not throw, and its returned list does not name that payload.
2. The full `extract`/`package` job in that setup still fails with `PackageValidation.ReparsePoint`, and the outside sentinel survives.
3. All existing HARD-01 pins (`OutDirHygieneTests`) and the GAP-S01 Sink 4 test stay green and unedited.
4. No recursive-delete primitive appears in `Packages.cs`/`Jobs.cs`.
