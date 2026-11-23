# HARD-01 clean deletes a user's own `config.json` / `install.cmd` that a prior `extract` never wrote

| Field | Value |
|---|---|
| **Slug** | `clean-deletes-foreign-config-after-extract` |
| **Severity** | Medium. It silently deletes a user file inside a folder the user chose, so it is file loss. The precondition is that the folder already received a CleanDriver `extract` and holds a file with one of those two common names. |
| **Confidence** | Confirmed: the repro test fails on HEAD (Linux harness), and it was reproduced live. |
| **Area** | `nvcleanstall/Lib/Packages.cs` (`CleanPreviousBuild`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | This is a gap inside HARD-01 / owner pin D1 (`docs/hardening_register.md` §HARD-01). The pin lists "`install.cmd` / `config.json` / `manifest.json` themselves" as declared artifacts of *any* CleanDriver manifest. It also says "**Foreign files always survive**." Those two clauses conflict whenever the prior build was an `extract`. Not in `docs/security_register.md` §3. |
| **Found** | 2026-11-23 |
| **Owner ruling** | **D-CLEAN** (2026-11-23, `docs/security_register.md`): pin D1 is *clarified*, not reversed — delete only artifacts the prior build actually wrote. The builder picks the mechanism (the Remedy's content check is one option), but any read before a delete must be size-bounded and must not follow links (ACs 5–6). Pair with `clean-previous-build-follows-reparse-point`; both change `CleanPreviousBuild`. Pinned as **GAP-R02**. |
| **Register slice** | **GAP-R02** — `CleanPreviousBuild` deletes only what the prior build wrote, never through a link (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`CleanPreviousBuild` always deletes `install.cmd` and `config.json` when `outDir` holds a CleanDriver-stamped `manifest.json`. Only the `package` action ever writes those two files. An `extract` writes `manifest.json`, `payload/` and `.reg` files and nothing else.

So if a user extracts into a folder that already contains their own `config.json` or `install.cmd`, the first extract leaves them alone. The second extract into the same folder deletes them as "the previous CleanDriver build's artifacts", even though CleanDriver never wrote them. The job reports `done`, and the deletion appears only as a log line.

## Evidence
`Packages.cs:221-224`. The fixed names are deleted unconditionally, without checking that the prior build wrote them:
```csharp
// The build's own fixed-name artifacts. install.cmd/config.json exist only for the
// `package` action, but an `extract` into a prior package build must clear them too.
foreach (var name in new[] { "install.cmd", "config.json", "manifest.json" })
    Remove(Path.Combine(outDir, name), name);
```
The comment states the assumption: "an extract into a prior **package** build". The code cannot tell a prior package build from a prior extract. The rewritten manifest (`Packages.cs:135-149`) does not record the action or the files written:
```csharp
var outManifest = new { version, channel, customizedBy = "CleanDriver", customizedAt, signature,
                        signatureSimulated, driverTelemetrySimulated, components = selected, tweaks };
```
`Jobs.cs:525-551` writes `config.json` and `install.cmd` only when `action == "package"`, and `config.json` carries a `builtBy = "CleanDriver"` stamp. Nothing reads that stamp back before the delete.

## Impact
- Silent loss of a user file in a user-chosen folder, the outcome HARD-01's "foreign files always survive" rule exists to prevent. `config.json` and `install.cmd` are generic names. An output folder such as a tools or project folder, or a folder of the user's own driver scripts, can easily hold one.
- The deletion is not recoverable through CleanDriver, and the only trace is `> Cleaning previous CleanDriver build: …, install.cmd, config.json, …` in the job log.
- It affects every repeat `extract` into the same folder. That includes the default `output/extract-<version>`, which users might reuse to keep their own notes or scripts.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/OutDirHygieneTests.cs`, as a new method. It's the same as the harness `repro/clean-deletes-foreign-config-after-extract.cs`.
```csharp
// HARD-01 "foreign files always survive": extract never writes config.json/install.cmd, so a
// user's own copies beside a prior EXTRACT are foreign and must survive a re-extract.
[Fact]
public async Task ReExtract_IntoFolderWithUsersOwnConfigAndScript_LeavesThemAlone()
{
    var outDir = Path.Combine(TempDir.Create(), "my-tools");
    Directory.CreateDirectory(outDir);
    var userConfig = Path.Combine(outDir, "config.json");
    var userScript = Path.Combine(outDir, "install.cmd");
    File.WriteAllText(userConfig, """{ "theme": "dark", "owner": "me" }""");
    File.WriteAllText(userScript, "@echo off\r\necho my own installer\r\n");

    var first = await Run("extract", outDir, Sel(new[] { "display-driver" }));
    Assert.Equal("done", first.Status);
    Assert.True(File.Exists(userConfig));      // build 1: untouched (extract never writes it)
    Assert.True(File.Exists(userScript));

    var second = await Run("extract", outDir, Sel(new[] { "display-driver" }));
    Assert.Equal("done", second.Status);

    Assert.True(File.Exists(userConfig),
        "the user's config.json — never written by CleanDriver — was deleted by the HARD-01 clean");
    Assert.True(File.Exists(userScript),
        "the user's install.cmd — never written by CleanDriver — was deleted by the HARD-01 clean");
}
```
`Run`, `Sel` and `TempDir` are the existing `OutDirHygieneTests` helpers. Run:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~ReExtract_IntoFolderWithUsersOwnConfigAndScript_LeavesThemAlone"
```
RED output, trimmed, from the Linux `net10.0` harness at `54b3c31` (class `CleanDeletesForeignConfigAfterExtractRepro`):
```
Failed ...ReExtract_IntoFolderWithUsersOwnConfigAndScript_LeavesThemAlone [4 s]
  Error Message:
   the user's config.json — never written by CleanDriver — was deleted by the HARD-01 clean
Failed!  - Failed: 1, Passed: 0
```

### B. Live — running app
Captured with the Linux harness on port 4802. On Windows use `C:/tmp/l9/my-tools`.
1. Create `my-tools/config.json` (`{ "theme": "dark", "owner": "me" }`) and `my-tools/install.cmd`.
2. `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"catalog","version":"572.16"}'` gives the token.
3. Run this twice, polling `GET /api/jobs/<id>` after each:
   `curl -s -X POST localhost:4780/api/execute -H "Content-Type: application/json" -d '{"token":"<t>","action":"extract","selection":{"components":["display-driver"],"tweaks":{}},"outputPath":"C:/tmp/l9/my-tools"}'`

**Expected:** both user files survive both runs.
**Actual (captured):**
```
extract #1: done
  files: ./config.json ./install.cmd ./manifest.json ./payload/display-driver.bin
extract #2: done
  > Cleaning previous CleanDriver build: payload/display-driver.bin, install.cmd, config.json, manifest.json
  files: ./manifest.json ./payload/display-driver.bin
```

## Root cause
The clean infers its delete list from "a CleanDriver manifest exists", not from what that build actually wrote. For payloads and `.reg` files the inference is exact, because the manifest records the components and tweaks. For `install.cmd`/`config.json` it is not, because the manifest records nothing that distinguishes a `package` build from an `extract` build. Deleting them unconditionally breaks the "undo what that manifest says was written" contract stated at `Packages.cs:168-170`.

## Remedy
Delete each of the two package-only files only when it can be shown to be CleanDriver's own. This needs no manifest schema change, and it keeps every existing HARD-01 pin green:
```csharp
// Packages.cs CleanPreviousBuild — replace the fixed-name loop at :223-224
if (IsOurConfig(Path.Combine(outDir, "config.json")))  Remove(Path.Combine(outDir, "config.json"), "config.json");
if (IsOurInstaller(Path.Combine(outDir, "install.cmd"))) Remove(Path.Combine(outDir, "install.cmd"), "install.cmd");
Remove(Path.Combine(outDir, "manifest.json"), "manifest.json");   // the stamp was already verified

// config.json written by Jobs carries builtBy:"CleanDriver"; install.cmd starts with CleanDriver's fixed header.
static bool IsOurConfig(string p) { /* small (<64 KiB), JSON object, builtBy == "CleanDriver"; never throws */ }
static bool IsOurInstaller(string p) { /* small, 2nd line == "rem CleanDriver self-contained package installer (simulated)" */ }
```
Alternative: record `artifacts: [...]`, or the action, in the rewritten manifest and delete exactly that list. That's cleaner long-term, but it changes the manifest shape. Stock/extract manifests would no longer be byte-identical, which the GAP-05 comment at `Packages.cs:142-144` cares about. It also still needs the content check for manifests written before the change. Rejected as not minimal.

Fences respected:
- **D-CLEAN constraints:** any read before a delete is size-bounded (check the length before reading, as `IsOur*` does with its cap), and no check or delete resolves through a symlink or junction (check `FileAttributes.ReparsePoint` first). See ACs 5–6.
- Enumerable-by-name deletion only, nothing recursive. The change only narrows what is deleted.
- Existing pins stay green: `Extract_IntoPreviousPackageBuild_CleansTheStalePackageArtifacts` (CleanDriver's own `config.json`/`install.cmd` still match and are removed), `Rebuild_SmallerSelection_…`, `ForeignOrMalformedManifest_NothingIsDeleted` and `JobLog_NamesTheCleanedFiles_WhenACleanRan`.
- **Ruling D-CLEAN** (2026-11-23) settles pin D1's wording: the clean deletes only artifacts the prior build wrote, so this change is within the ruling.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** Settled: ruling **D-CLEAN** (clarifies pin D1) and slice **GAP-R02**.
   > *Ruling recorded 2026-11-23: **D-CLEAN** (see the header). The register entry is **GAP-R02**.*
   > *Pinned 2026-11-23 as **GAP-R02** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-foreigncfg -b feat/clean-only-own-config origin/main`
2. **Red.** Add the §A method to `OutDirHygieneTests.cs`. Run its single-test filter and confirm it fails on the `config.json` assertion. Commit `"GAP-R02 (red): clean-deletes-foreign-config-after-extract — re-extract must not delete a user's own config.json/install.cmd"`.
3. **Green.** Add `IsOurConfig`/`IsOurInstaller` and gate the two removals. Run the full suite, where all HARD-01 pins must pass unedited. Commit `"GAP-R02: clean-deletes-foreign-config-after-extract — HARD-01 clean removes config.json/install.cmd only when CleanDriver wrote them"`.
4. **Refactor.** Green only. For example, share the installer header string between `Jobs.cs` and `Packages.cs` through a `Packages` constant. Mind the `NoExecutionGuardTests` forbidden-token list in `Jobs.cs`; a plain string constant is fine.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run §B. Extract #2's log line names only `payload/display-driver.bin, manifest.json`, and both user files survive. Then run `package` → `extract` into one folder: CleanDriver's own `install.cmd`/`config.json` are still cleaned.
7. **PR.** Template TDD row. Cite ruling D-CLEAN under "What & why". No existing test edited. Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-foreigncfg origin/feat/clean-only-own-config`; range `origin/main...HEAD`.
3. **Re-run gates**, then §B live plus the package→extract counter-case.
4. **Finder pass.** Check:
   - The ownership checks never throw: a locked, huge, binary or malformed `config.json` means "not ours, skip". This preserves the never-throws contract.
   - They read a bounded amount.
   - The installer check matches what `Jobs.cs` actually writes (CRLF line endings).
   - The change matches ruling D-CLEAN: only files the prior build itself wrote are deleted.
   - No recursive primitive names were added.
5. **Adversarial verify.** Try:
   - A user `config.json` that *contains* `"builtBy":"CleanDriver"` as a nested value (it must not match unless top-level).
   - A 2 GiB `config.json`.
   - `config.json` as a directory.
   - A CleanDriver `install.cmd` edited by the user (skipped: acceptable, document it).
   - The legacy flow package → package, which must still clean.
6. **Verdict document.** APPROVE / REQUEST CHANGES, with CONFIRMED/PLAUSIBLE findings.
7. **Feedback to builder.** Fix loop C0 orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 `F<n>:` commits → C6 closing report → C7 STOP.
8. **Report to owner.** Verdict line, including the D1 amendment for ratification. The owner merges.

## Acceptance criteria
1. A user's `config.json` and `install.cmd` in a folder that received a CleanDriver `extract` survive any number of further `extract` runs into that folder.
2. A CleanDriver `package` build's own `config.json` (`builtBy == "CleanDriver"`) and `install.cmd` are still removed by the next build into that folder (`Extract_IntoPreviousPackageBuild_CleansTheStalePackageArtifacts` green, unedited).
3. `CleanPreviousBuild` still never throws, including when `config.json` is huge, malformed, locked or a directory.
4. The job log's "Cleaning previous CleanDriver build" line names only files that were actually CleanDriver's.
5. **(D-CLEAN)** An oversized `config.json` or `install.cmd` in `outDir` (above the bound the slice sets) is never read whole and is not deleted.
6. **(D-CLEAN)** A `config.json` or `install.cmd` that is a symlink or junction is not followed (its target is neither read nor deleted) and is not deleted. This is the same rule as AC 5 of `clean-previous-build-follows-reparse-point`, which lands in the same slice (GAP-R02).
