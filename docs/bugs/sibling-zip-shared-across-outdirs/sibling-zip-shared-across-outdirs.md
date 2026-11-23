# Package archives of different build folders under one parent overwrite each other

| Field | Value |
|---|---|
| **Slug** | `sibling-zip-shared-across-outdirs` |
| **Severity** | Medium. The archive beside one build folder is silently replaced by another build's contents, so the earlier redistributable is lost and the remaining one is mislabeled. The trigger is ordinary use: two custom output folders side by side, building the same version. |
| **Confidence** | Confirmed: the repro test fails on HEAD (Linux harness), and it was reproduced live. |
| **Area** | `nvcleanstall/Lib/Packages.cs` (`ZipPathFor`, `WriteZip`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | GAP-06 Resolution (2) (`docs/gaps_analysis.md`) and parity FEAT-027 fix the name as `<version>-cleandriver-package.zip`, a sibling of `outDir`. This finding is a consequence of that ruling that the ruling did not address. BUG-04 / GAP-C02 cover *concurrent* replacement of the same sibling ZIP. This is the *sequential, cross-folder* case, and GAP-C02's atomic replace would not fix it (it would just atomically replace the wrong archive). |
| **Found** | 2026-11-23 |
| **Owner ruling** | **D-ZIPNAME** (2026-11-23, `docs/security_register.md`): **Option A adopted.** GAP-06 Resolution (2) is amended: the default `package-<version>` folder keeps `<version>-cleandriver-package.zip`; any other folder gets `<version>-<leaf>-cleandriver-package.zip`, still a sibling of `outDir`. Option B is not pursued. Pinned as **GAP-R06**. |
| **Register slice** | **GAP-R06** — Artifact identity and honesty (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`ZipPathFor` names the archive `<parent-of-outDir>/<version>-cleandriver-package.zip`. The name depends only on the version, so every output folder with the same parent shares one archive path.

Say a user packages driver 572.16 into `D:/builds/minimal`, then packages 572.16 again into `D:/builds/full`. The second build `File.Delete`s the first build's archive and writes its own in its place. Both jobs report `done`. Afterwards `D:/builds` holds `minimal/`, `full/` and one zip, which contains the `full` selection. The minimal redistributable is gone.

## Evidence
`Packages.cs:246-252`. The archive path depends only on the parent and the version:
```csharp
public static string ZipPathFor(string outDir, string version)
{
    var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
    var parent = Directory.GetParent(baseDir) ?? throw ...;
    return Path.Combine(parent.FullName, $"{version}-cleandriver-package.zip");   // :251 — outDir's own name is discarded
}
```
`Packages.cs:266-269`. Whatever is at that path gets deleted, whichever build made it:
```csharp
if (File.Exists(zipPath)) File.Delete(zipPath);   // re-runs overwrite cleanly
using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
```
The "re-runs overwrite cleanly" intent (pinned by `PackageZipTests.PackageAction_RunTwiceIntoSameOutDir_OverwritesTheArchive`) assumes that a given archive path belongs to a single `outDir`. That holds only for the default `output/package-<version>` layout.

## Impact
- **Lost redistributable.** The first build's archive is deleted with no warning. Its directory output survives, but the one-file deliverable (FEAT-027) for it no longer exists.
- **Mislabeled deliverable.** The surviving archive sits next to both folders with a version-only name. A user who ships "the 572.16 package next to `minimal/`" ships the `full` component set: components and tweaks they deliberately removed. That defeats the customizer's purpose.
- This deletes a file outside `outDir` that the current build's prior manifest never declared, which runs against the spirit of the D12/D1 rule that "deletion is only of artifacts a CleanDriver manifest declares".

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/PackageZipTests.cs`. It's the same as the harness `repro/sibling-zip-shared-across-outdirs.cs`.
```csharp
// GAP-06 sibling archive: two different build folders under one parent must not share (and
// clobber) one archive. Literal folder names; the archive is found by listing, not re-derived.
[Fact]
public async Task SecondBuildIntoASiblingFolder_DoesNotReplaceTheFirstBuildsArchive()
{
    var (manifest, source) = Packages.LoadCatalog("572.16");
    var root = TempDir.Create();                       // stands in for e.g. D:/builds
    var minimal = Path.Combine(root, "minimal");
    var full = Path.Combine(root, "full");

    var first = Jobs.StartExecute("package", manifest, source, Sel(new[] { "display-driver" }), minimal, G());
    await first.Completion!;
    Assert.Equal("done", first.Status);
    var archive = Assert.Single(Directory.GetFiles(root, "*.zip"));
    string[] Entries() { using var z = ZipFile.OpenRead(archive);
        return z.Entries.Select(e => e.FullName).OrderBy(n => n, System.StringComparer.Ordinal).ToArray(); }
    var minimalEntries = Entries();
    Assert.Equal(new[] { "config.json", "install.cmd", "manifest.json", "payload/display-driver.bin" }, minimalEntries);

    var second = Jobs.StartExecute("package", manifest, source,
        Sel(new[] { "display-driver", "physx", "hd-audio" }), full, G());
    await second.Completion!;

    Assert.True(File.Exists(archive), "the minimal build's archive was deleted");
    Assert.Equal(minimalEntries, Entries());
}
```
Run:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~SecondBuildIntoASiblingFolder_DoesNotReplaceTheFirstBuildsArchive"
```
RED output, trimmed, from the Linux `net10.0` harness at `54b3c31` (class `SiblingZipSharedAcrossOutDirsRepro`):
```
Failed ...SecondBuildIntoASiblingFolder_DoesNotReplaceTheFirstBuildsArchive [5 s]
  Assert.Equal() Failure: Collections differ
  Expected: [···, "install.cmd", "manifest.json", "payload/display-driver.bin"]
  Actual:   [···, "install.cmd", "manifest.json", "payload/display-driver.bin", "payload/hd-audio.bin", "payload/physx.bin"]
Failed!  - Failed: 1, Passed: 0
```

### B. Live — running app
Captured with the Linux harness on port 4802. On Windows use `C:/tmp/l4/builds/...`.
1. `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"catalog","version":"572.16"}'` gives the token.
2. `curl -s -X POST localhost:4780/api/execute -H "Content-Type: application/json" -d '{"token":"<t>","action":"package","selection":{"components":["display-driver"],"tweaks":{}},"outputPath":"C:/tmp/l4/builds/minimal"}'`, then poll until `done`.
3. The same with `"components":["display-driver","physx","hd-audio"]` and `"outputPath":"C:/tmp/l4/builds/full"`.
4. List `C:/tmp/l4/builds` and the zip's entries after each step.

**Expected:** two archives, one per build folder. At least, the minimal one must not be destroyed.
**Actual (captured):**
```
minimal: done
  builds/: 572.16-cleandriver-package.zip  minimal
  zip: ['config.json', 'install.cmd', 'manifest.json', 'payload/display-driver.bin']
full: done
  > Writing 572.16-cleandriver-package.zip… done
  builds/: 572.16-cleandriver-package.zip  full  minimal
  zip: ['config.json', 'install.cmd', 'manifest.json', 'payload/display-driver.bin', 'payload/hd-audio.bin', 'payload/physx.bin']
```

## Root cause
The archive identity is `(parent, version)`, but the thing being archived has identity `outDir`. With the default layout, `output/package-<version>`, the two coincide one-to-one. With user-chosen output folders they don't, and `WriteZip`'s unconditional delete-then-create turns that many-to-one mapping into silent replacement.

## Remedy
**Ruled (D-ZIPNAME, 2026-11-23): Option A.** GAP-06 Resolution (2) is amended as below. Option B is kept for the record only and is **not pursued**.

- **Option A (adopted under D-ZIPNAME).** Make the archive name include the folder, while keeping today's exact name for the default folder so every existing pin and literal stays valid:
```csharp
public static string ZipPathFor(string outDir, string version)
{
    var baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));
    var parent = Directory.GetParent(baseDir) ?? throw new InvalidOperationException(...);
    var leaf = Path.GetFileName(baseDir);
    var name = leaf == $"package-{version}"
        ? $"{version}-cleandriver-package.zip"                    // default layout: unchanged (pins)
        : $"{version}-{leaf}-cleandriver-package.zip";            // custom folder: its own archive
    return Path.Combine(parent.FullName, name);
}
```
  Every distinct `outDir` under one parent then gets a distinct archive. No non-default folder can produce the legacy name, because it always has the extra `-<leaf>` segment.
- *Not pursued (D-ZIPNAME):* **Option B (no ruling change).** Keep the name, but refuse to replace an archive this `outDir` did not produce. Add `builtFrom: "<outDir leaf>"` (additive) to the archived `config.json`. In `WriteZip`, if an archive exists and its `config.json.builtFrom` names a different folder, fail the job by name, for example "a package archive for another build folder already exists beside this folder". That never deletes another build's archive. The cost is that the user must pick another parent, and an existing untrusted zip gets parsed.

Alternative rejected: timestamped names. They would break the "re-runs overwrite cleanly" pin, and stale archives would pile up.

Fences respected:
- Option A deletes nothing new.
- No recursive deletion.
- Honesty markers unchanged, since the archived manifest and config are untouched.
- Pins: `PackageZipTests` uses `package-<version>` folders and literal `572.16-cleandriver-package.zip` paths, and `PackageValidationTests.ExportedZipEntries_AreNormalizedRelativePaths` likewise, so they stay green under Option A. `OutDirHygieneTests.Extract_IntoPreviousPackageBuild_CleansTheStalePackageArtifacts` packages into `extract-572.16` and only *counts* `*.zip` files in the parent, so it stays green: the count is unchanged, only the name differs. Under Option A the UI log line `> Writing <name>… done` shows the new name for custom folders, with no layout change.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** Settled: ruling **D-ZIPNAME** (Option A) and slice **GAP-R06**, which joins GAP-C02's artifact-identity work by cross-reference.
   > *Ruling recorded 2026-11-23: **D-ZIPNAME**, Option A (see the header). The register entry is **GAP-R06**.*
   > *Pinned 2026-11-23 as **GAP-R06** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-siblingzip -b feat/sibling-zip-per-outdir origin/main`
2. **Red.** Add the §A test to `PackageZipTests.cs` and run its filter. It fails with the archive's entries replaced. Commit `"GAP-R06 (red): sibling-zip-shared-across-outdirs — a sibling build folder must not replace another folder's archive"`.
3. **Green.** Implement the ruled option and run the full suite. Commit `"GAP-R06: sibling-zip-shared-across-outdirs — one sibling archive per build folder"`.
4. **Refactor.** Green only.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run §B steps 1-4. Under Option A, `builds/` holds `572.16-minimal-cleandriver-package.zip` (1 payload) and `572.16-full-cleandriver-package.zip` (3 payloads). Building the default `output/package-572.16` still writes `output/572.16-cleandriver-package.zip`.
7. **PR.** Template TDD row. Cite the owner ruling under "Deviations". No existing test edited. Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-siblingzip origin/feat/sibling-zip-per-outdir`; range `origin/main...HEAD`.
3. **Re-run gates**, then §B live in both the custom and default layouts.
4. **Finder pass.** Check:
   - The name derivation is pure and normalizes like today's (trailing separator, drive root refused).
   - The leaf can't inject a separator. It comes from `GetFileName` of a full path, so a separator is impossible, but check for `..` on Windows.
   - The default-layout name is byte-identical.
   - `Jobs.cs` still derives the path only through `ZipPathFor`.
   - The deletion in `WriteZip` is still limited to the computed path.
5. **Adversarial verify.** Try:
   - A folder literally named `package-572.16` under a different parent (which should keep the legacy name).
   - Folder names with spaces, dots and Unicode.
   - Two folders whose leaves differ only in case on Windows (`Full` vs `full`): same directory or not?
   - Rebuilding the same custom folder twice, which must still overwrite cleanly.
   - `outputPath` with a trailing slash.
6. **Verdict document.** APPROVE / REQUEST CHANGES, with CONFIRMED/PLAUSIBLE findings.
7. **Feedback to builder.** Fix loop C0 orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 `F<n>:` commits → C6 closing report → C7 STOP.
8. **Report to owner.** Verdict line, plus the ruling reference. The owner merges.

## Acceptance criteria
1. Packaging the same version into two different folders under one parent leaves two intact archives, `<version>-<leafA>-cleandriver-package.zip` and `<version>-<leafB>-cleandriver-package.zip`, and the first folder's archive keeps exactly its entries.
2. Packaging into the default `output/package-<version>` still writes `output/<version>-cleandriver-package.zip`, and re-running into the same folder still overwrites cleanly (`PackageAction_RunTwiceIntoSameOutDir_OverwritesTheArchive` green, unedited).
3. All existing `PackageZipTests`, `OutDirHygieneTests` and `PackageValidationTests` pins pass unedited.
4. The PR cites ruling **D-ZIPNAME** (recorded 2026-11-23) and its register entry.
