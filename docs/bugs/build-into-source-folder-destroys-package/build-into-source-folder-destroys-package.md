# Building into the package's own folder destroys the source package

| Field | Value |
|---|---|
| **Slug** | `build-into-source-folder-destroys-package` |
| **Severity** | High: it destroys the user's input package, or the app's own bundled sample package, and then fails the job anyway |
| **Confidence** | Confirmed: the repro test fails on HEAD (Linux harness), and the bug was reproduced live. Mechanism (1) was also run against the pre-PR #23 tree `a8c4f70`, where it passes, so PR #23 introduced it. |
| **Area** | `nvcleanstall/Lib/Packages.cs` (`CopyPayload`, `WriteCustomized`, `CleanPreviousBuild`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | GAP-S01 (PR #23) introduced mechanism (1) when it replaced `ReadPayload` + `WriteAllBytes` with streaming `CopyPayload`. HARD-01 (pin D1) is where mechanism (2) comes from. No register row covers either one. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R01** — Building into the source folder must not destroy the package (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
If `outputPath` is the folder the package was loaded from, an `extract` or `package` run destroys that package. It also fails, so the user gets nothing in return.
- **Mechanism (1), a PR #23 regression.** `CopyPayload` truncates the destination file before it opens the source. When both are the same file, the source payload drops to 0 bytes and the job fails with a sharing violation. This happens with any ordinary local package folder. It also happens with the bundled sample package, whose path `/api/sample-package` shows in the UI.
- **Mechanism (2), from HARD-01.** CleanDriver's own `extract` output (`manifest.json` + `payload/`) is a valid local package. If the user loads it back and rebuilds into the same folder, `CleanPreviousBuild` deletes the input's manifest and payloads before `WriteCustomized` reads them.

- **Mechanism (3), a zip source** (added after the PR #25 review). The `package` step ends with `WriteZip(ZipPathFor(outDir, version))`, which deletes whatever already sits at that path (`Packages.cs:268`). Build a package into `output/package-572.16`, load the resulting `output/572.16-cleandriver-package.zip`, and build again into the same folder: the source archive is deleted and rewritten with the narrower selection.

A sibling symptom of mechanism (1): when a selected payload is missing from the source, a zero-byte `payload/<name>` is left behind.

## Evidence
`Packages.cs:96-114`. The destination is created (`FileMode.Create` truncates it) before the source is located or opened:
```csharp
var destPath = PackageValidation.EnsureConfinedRealPath(outDir, Path.Combine("payload", payloadFile)); // :98
Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
using var dest = File.Create(destPath);                                                               // :100  <- truncates
...
var srcPath = PackageValidation.EnsureConfinedRealPath(source.Dir!, Path.Combine("payload", payloadFile)); // :110
if (!File.Exists(srcPath)) throw new InvalidDataException(PackageValidation.PayloadMissing);              // :111
using var fsrc = File.OpenRead(srcPath);                                                                  // :112  <- same file: sharing violation
```
At `a8c4f70` (before PR #23) the order was safe: read the whole source, then write.
```csharp
var data = ReadPayload(source, c.Payload) ?? throw ...;          // File.ReadAllBytes(src) first
File.WriteAllBytes(Path.Combine(outDir, "payload", c.Payload), data);
```
`Packages.cs:181-234` / `Jobs.cs:487-492`. The clean step runs first. It deletes whatever a `customizedBy == "CleanDriver"` manifest in `outDir` declares, with no idea that `outDir` is also the package being read:
```csharp
steps.Add((200, () => { var cleaned = Packages.CleanPreviousBuild(outDir); ... }));   // Jobs.cs:487-489
```
Nothing anywhere compares `outDir` with `source.Dir`. The only pre-write guard in `StartExecute` is `IsFilesystemRoot` (`Jobs.cs:396`).

## Impact
- **Data loss of the user's input.** "Customize this package in place" is a natural thing to try. It leaves `payload/display-driver.bin` at 0 bytes, and the job reports `failed`.
- Re-customizing a previous CleanDriver extract in place deletes the whole input package. Live, all that remained was a single 0-byte file.
- **Corrupts the app's own data.** Loading `/api/sample-package`'s path and building into it truncates the bundled `data/packages/<ver>/payload/display-driver.bin`. Every later build of that version then copies a 0-byte "driver" and reports `done`, until the app is reinstalled.
- **A zip source is replaced, silently.** Under mechanism (3) the job ends `done`: the user's full archive becomes the narrower one, with no error at all.
- The failure message then leaks the absolute path through the job error. That leak is pre-existing behavior of job errors, noted here only for completeness.

## How to reproduce
### A. Automated — failing xunit test
Add as `nvcleanstall/CleanDriver.Tests/BuildIntoSourceFolderTests.cs`. The body is identical to the harness `repro/build-into-source-folder-destroys-package.cs`, class `BuildIntoSourceFolderDestroysPackageRepro`.
```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

public class BuildIntoSourceFolderTests
{
    private static readonly byte[] Core = Enumerable.Range(0, 4096).Select(i => (byte)(i * 7)).ToArray();

    private static string MakeLocalPackage(string root, string manifestJson)
    {
        var pkg = Path.Combine(root, "my-package");
        Directory.CreateDirectory(Path.Combine(pkg, "payload"));
        File.WriteAllText(Path.Combine(pkg, "manifest.json"), manifestJson);
        File.WriteAllBytes(Path.Combine(pkg, "payload", "display-driver.bin"), Core);
        File.WriteAllBytes(Path.Combine(pkg, "payload", "physx.bin"), new byte[] { 1, 2, 3 });
        return pkg;
    }

    private const string ThirdPartyManifest = """
        { "version":"572.16","channel":"WHQL","components":[
          {"id":"display-driver","name":"Display Driver","required":true,"payload":"display-driver.bin"},
          {"id":"physx","name":"PhysX","payload":"physx.bin"} ] }
        """;

    // Mechanism (1): an ordinary (non-CleanDriver) local package folder, built in place.
    [Theory]
    [InlineData("extract")]
    [InlineData("package")]
    public async Task BuildIntoTheSourceFolder_NeverTruncatesTheSourcePayload(string action)
    {
        var pkg = MakeLocalPackage(TempDir.Create(), ThirdPartyManifest);
        var (manifest, source) = Packages.LoadLocal(pkg);
        var sel = new Selection { Components = new() { "display-driver" } };

        var job = Jobs.StartExecute(action, manifest, source, sel, pkg, Gpu.Simulated);
        await job.Completion!;

        Assert.Equal(Core, File.ReadAllBytes(Path.Combine(pkg, "payload", "display-driver.bin")));
    }

    // Mechanism (2): re-customizing a previous CleanDriver extract in place.
    [Fact]
    public async Task ReCustomizingACleanDriverOutputInPlace_NeverDeletesTheSourcePackage()
    {
        var root = TempDir.Create();
        var first = Path.Combine(root, "extract-572.16");
        var (m0, s0) = Packages.LoadCatalog("572.16");
        var j0 = Jobs.StartExecute("extract", m0, s0,
            new Selection { Components = new() { "display-driver", "physx", "hd-audio" } }, first, Gpu.Simulated);
        await j0.Completion!;
        Assert.Equal("done", j0.Status);

        var (m1, s1) = Packages.LoadLocal(first);   // CleanDriver's output is a valid local package
        var j1 = Jobs.StartExecute("extract", m1, s1,
            new Selection { Components = new() { "display-driver", "physx" } }, first, Gpu.Simulated);
        await j1.Completion!;

        Assert.True(File.Exists(Path.Combine(first, "manifest.json")),
            $"the source package's manifest.json was deleted (job {j1.Status}: {j1.Error})");
        Assert.True(File.Exists(Path.Combine(first, "payload", "display-driver.bin")),
            $"the source package's display-driver payload was deleted (job {j1.Status}: {j1.Error})");
        Assert.True(new FileInfo(Path.Combine(first, "payload", "display-driver.bin")).Length > 0,
            "the source package's display-driver payload was truncated");
    }

    // Same ordering defect, unrelated folders: a missing source payload leaves a zero-byte file.
    [Fact]
    public void MissingSourcePayload_LeavesNoZeroBytePayloadBehind()
    {
        var root = TempDir.Create();
        var pkg = MakeLocalPackage(root, ThirdPartyManifest);
        File.Delete(Path.Combine(pkg, "payload", "physx.bin"));          // declared but absent
        var (manifest, source) = Packages.LoadLocal(pkg);
        var outDir = Path.Combine(root, "out");

        var ex = Assert.Throws<InvalidDataException>(() => Packages.WriteCustomized(source, manifest,
            new List<string> { "display-driver", "physx" }, outDir));
        Assert.Equal(PackageValidation.PayloadMissing, ex.Message);
        Assert.False(File.Exists(Path.Combine(outDir, "payload", "physx.bin")),
            "a zero-byte payload was left behind for a payload that does not exist in the source");
    }

    // Mechanism (3): `package` writes Packages.ZipPathFor(outDir, version). When that is the zip
    // the package was loaded from, the build must be refused by name, never replace the source.
    [Fact]
    public async Task PackageFromAZip_IntoTheFolderThatProducedIt_NeverReplacesTheSourceZip()
    {
        var root = TempDir.Create();
        var outDir = Path.Combine(root, "package-572.16");

        var (m0, s0) = Packages.LoadCatalog("572.16");
        var all = new Selection { Components = m0.Components.Select(c => c.Id).ToList() };
        var j0 = Jobs.StartExecute("package", m0, s0, all, outDir, Gpu.Simulated);
        await j0.Completion!;
        Assert.Equal("done", j0.Status);

        var zip = Packages.ZipPathFor(outDir, "572.16");          // the archive that build produced
        var before = Entries(zip);

        var (m1, s1) = Packages.LoadLocal(zip);                    // load that archive as the source
        var j1 = Jobs.StartExecute("package", m1, s1,
            new Selection { Components = new() { "display-driver" } }, outDir, Gpu.Simulated);
        await j1.Completion!;

        Assert.Equal(before, Entries(zip));                        // the source archive is untouched
        Assert.Equal("failed", j1.Status);                         // …because the build was refused
    }

    private static List<string> Entries(string zip)
    {
        using var z = System.IO.Compression.ZipFile.OpenRead(zip);
        return z.Entries.Select(e => $"{e.FullName}:{e.Length}").OrderBy(x => x, StringComparer.Ordinal).ToList();
    }
}
```
Run:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~BuildIntoSourceFolderTests"
```
RED output, trimmed, from the Linux `net10.0` harness at `54b3c31`:
```
Failed ...BuildIntoTheSourceFolder_NeverTruncatesTheSourcePayload(action: "extract")
  Assert.Equal() Failure: Collections differ
  Expected: [0, 7, 14, 21, 28, ···]
  Actual:   []
Failed ...BuildIntoTheSourceFolder_NeverTruncatesTheSourcePayload(action: "package")
  Expected: [0, 7, 14, 21, 28, ···]
  Actual:   []
Failed ...ReCustomizingACleanDriverOutputInPlace_NeverDeletesTheSourcePackage
  the source package's manifest.json was deleted (job failed: The process cannot access the file
  '/tmp/cleandriver-dltest-…/extract-572.16/payload/display-driver.bin' because it is being used by another process.)
Failed ...MissingSourcePayload_LeavesNoZeroBytePayloadBehind
  a zero-byte payload was left behind for a payload that does not exist in the source
Failed!  - Failed: 4, Passed: 0
```
Mechanism (3), added after the PR #25 review. RED from the Linux `net10.0` harness at `54b3c31` (separate run, class `BuildFromZipIntoItsOwnBuildFolderTests`, identical method body):
```
Failed ...PackageFromAZip_IntoTheFolderThatProducedIt_NeverReplacesTheSourceZip [4 s]
   Assert.Equal() Failure: Collections differ
           ↓ (pos 0)
Expected: ["config.json:290", "install.cmd:179", "manifest.json:3422", "payload/container.bin:2111", "payload/display-driver.bin:2116", ···]
Actual:   ["config.json:158", "install.cmd:179", "manifest.json:534", "payload/display-driver.bin:2116"]
           ↑ (pos 0)
```

Regression check: the same file was compiled against `git archive a8c4f70` (pre-PR #23). Both `BuildIntoTheSourceFolder_*` cases **pass** there. `ReCustomizing…` fails there too ("payload missing for component display-driver"), which confirms mechanism (2) predates PR #23.

### B. Live — running app
Server: `dotnet run --project nvcleanstall -- --headless --mock-catalog`. This transcript is from the Linux harness on port 4802; use 4780 for the real app and forward-slash Windows paths.
1. Make `C:/tmp/l1/my-package/{manifest.json,payload/display-driver.bin (4096 B),payload/physx.bin}` with the two-component manifest above.
2. `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"local","path":"C:/tmp/l1/my-package"}'` returns `{"token":"1",...}`.
3. `curl -s -X POST localhost:4780/api/execute -H "Content-Type: application/json" -d '{"token":"1","action":"extract","selection":{"components":["display-driver"],"tweaks":{}},"outputPath":"C:/tmp/l1/my-package"}'`
4. `curl -s localhost:4780/api/jobs/1`

**Expected:** either the payload bytes are preserved, or the request is refused by name before anything is written.
**Actual (captured):**
```
failed | The process cannot access the file '…/my-package/payload/display-driver.bin' because it is being used by another process.
-rw-r--r-- 1 root root 0 … display-driver.bin      <- was 4096 bytes
```
Same run with `path` = `outputPath` = the value of `GET /api/sample-package` (`…/data/packages/571.96`): the job `failed` and the bundled `571.96/payload/display-driver.bin` went from 2116 B to **0 B**.

Mechanism (2), live: extract catalog 572.16 to `…/l1b/extract-572.16`, which ends `done`. Then `POST /api/package {"kind":"local","path":"…/l1b/extract-572.16"}` and extract into the same path. The log shows `> Cleaning previous CleanDriver build: payload/display-driver.bin, payload/physx.bin, payload/hd-audio.bin, manifest.json`, then `> ERROR: The process cannot access the file …`. Files left: `0  …/payload/display-driver.bin`.


Mechanism (3), live (added after the PR #25 review). Leave **Output folder empty** both times, so the default `outDir` (`output/package-<version>`) is the folder that produced the archive:
1. `POST /api/package {"kind":"catalog","version":"572.16"}`, then `POST /api/execute` with `"action":"package"`, every component selected and `"outputPath":""`. The job ends `done` and writes `output/572.16-cleandriver-package.zip`. Record its size, sha256 and entry count.
2. `POST /api/package {"kind":"local","path":"<app dir>/output/572.16-cleandriver-package.zip"}`.
3. `POST /api/execute` with that token, `"action":"package"`, `"components":["display-driver"]` and `"outputPath":""`, then record the same three values again.

**Expected:** step 3 fails by name and the archive is byte-identical. **Actual (captured on the Linux harness at `54b3c31`, port 4812):**
```
1. job 1: done      3554 bytes output/572.16-cleandriver-package.zip   sha256 ee2bf050dc12fe14…  entries: 13
3. job 2: done      1085 bytes output/572.16-cleandriver-package.zip   sha256 4772ccab16a33322…  entries: 4
```
The user's full archive is silently replaced by the narrower one, and the job reports success. After the fix, `extract` from that same zip (Output folder empty, so `output/extract-572.16`, or explicitly `output/package-572.16`) must still succeed (AC 6).
## Root cause
Nothing treats "the output directory is the input package" as a hazard. Two writers act on that alias:
1. `CopyPayload` opens the destination with truncate semantics before the source is opened, so destination and source being the same file is destructive. PR #23 swapped "read everything, then write" for "open destination, then stream from source".
2. `CleanPreviousBuild` treats a CleanDriver-stamped `manifest.json` in `outDir` as "a previous build to undo", even when that manifest is the package currently being read.

## Remedy
Minimal, two parts:
1. **Refuse the alias by name before any write.** This follows the HARD-02 pattern. Add a pure predicate in `Packages.cs`, and call it at the top of `StartExecute` right after `IsFilesystemRoot` (`Jobs.cs:396`). Failing the job by name has the same shape as `RootOutputPathError`. Call it as `OutputAliasesSource(source, outDir, action, manifest.Version)`; `extract` from a zip is never refused, since it writes no archive.
```csharp
public const string OutputIsSourceError = "outputPath would overwrite the package being customized; choose a different folder";
public static bool OutputAliasesSource(PackageSource source, string outDir, string action, string version)
{
    var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
    // Only `extract` and `package` write to outDir. `install`/`silent` never use outputPath,
    // so they are never refused here (unlike HARD-02's action-uniform root check, amendment A3:
    // aliasing can only damage the source through a write).
    if (action is not ("extract" or "package")) return false;
    // Folder source: both writing actions write into outDir.
    if (source.Dir is not null && string.Equals(Norm(source.Dir), Norm(outDir), cmp)) return true;
    // Zip source: only `package` writes an archive, at ZipPathFor(outDir, version). Call ZipPathFor
    // itself, never a copy of its naming rule (GAP-R06 / D-ZIPNAME changes that name later).
    return action == "package" && source.ZipPath is not null
        && string.Equals(Norm(source.ZipPath), Norm(ZipPathFor(outDir, version)), cmp);
}
```
2. **Make `CopyPayload` source-first.** Resolve and open the source (zip entry, or the confined file plus its `File.Exists` check) before `File.Create(destPath)`. With the alias refused this is defense in depth. It also stops a missing or invalid source from leaving a zero-byte destination.

Why this is minimal: one name-based predicate (the same "by name" stance as `IsFilesystemRoot`) and a reordering of existing lines. No new deletion, and no change to the HARD-01 delete list.

Alternatives rejected:
- Teaching `CleanPreviousBuild` to skip when `outDir == source`. That would still allow the mechanism (1) truncation, and building in place would still overwrite the input's manifest.
- Copying to a temp file and renaming. That's more code, and it doesn't stop the clean step's deletion.

Fences respected:
- Enumerable-by-name deletion only. No new delete. `OutDirHygieneTests.CleanPath_ContainsNoRecursiveDeletePrimitives` stays green, so don't write the recursive-delete API name, even in comments.
- Safety boundary and honesty markers untouched. No UI change; the job fails by name like HARD-02.

Pins checked: `OutDirHygieneTests`, `PackageZipTests` and `PackageValidationTests.ValidPackage_StreamsSelectedBytes_Unchanged` all build into fresh temp folders, so none are affected.

Known limit: being name-based, the predicate does not catch an alias created through a junction. Treat that as a documented limit, as with `IsFilesystemRoot`.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** Settled: slice **GAP-R01**, pinned to land first. Mechanism (1) is a GAP-S01 regression; do not fold it into GAP-C02.
   > *Pinned 2026-11-23 as **GAP-R01** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-srcalias -b feat/build-into-source-folder origin/main`
2. **Red.** Add `nvcleanstall/CleanDriver.Tests/BuildIntoSourceFolderTests.cs` (§A). Run `--filter "FullyQualifiedName~BuildIntoSourceFolderTests"`. It should show 5 failures for the stated reasons (four mechanism-(1)/(2) cases plus the mechanism-(3) zip case). Commit `"GAP-R01 (red): build-into-source-folder-destroys-package — building into the package folder must not destroy the source"`.
3. **Green.** Add `OutputAliasesSource` + `OutputIsSourceError` and call them from `StartExecute`. Reorder `CopyPayload` to open the source first. Run the full suite. Commit `"GAP-R01: build-into-source-folder-destroys-package — refuse outputPath == package folder; CopyPayload opens source before destination"`.
4. **Refactor.** Only with the suite green, for example sharing `Norm` with `IsFilesystemRoot`.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Repeat §B steps 1-4. The job must be `failed` with `OutputIsSourceError` (`outputPath would overwrite the package being customized; choose a different folder`), and `display-driver.bin` must still be 4096 B. Repeat with the `/api/sample-package` path, and confirm the bundled payload size is unchanged.
7. **PR.** Fill `.github/pull_request_template.md` with one TDD-evidence row per behavior: in-place extract, in-place package, re-customize of a CleanDriver output, missing payload leaves no file, and package from a zip into the folder that produced it. No existing test is edited. State the deviation: the new named job error is a new user-visible string with no new control, so R1-R10 are intact. Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `gh pr checks <n>`: `build-test-smoke` must be green before reading the diff.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-srcalias origin/feat/build-into-source-folder`; range `origin/main...HEAD`.
3. **Re-run gates** on the reviewer's checkout, then the §B live exercise.
4. **Finder pass.** Check:
   - The predicate runs before any write: before the clean step and before `WriteCustomized`, in the same early-return block as `IsFilesystemRoot`.
   - It works for the `catalog` source (`source.Dir` under `data/packages`) as well as `local-dir`.
   - Comparison is case-insensitive on Windows and tolerates a trailing separator.
   - `CopyPayload` now opens the source before `File.Create`, on both the zip and folder branches.
   - No new deletion, and no forbidden primitive names in `Packages.cs`/`Jobs.cs`.
   - The error string is path-free.
   - The tests are red at the parent commit.
5. **Adversarial verify.** Try to break it:
   - `outputPath` = `source.Dir + "/"`, a different case of the same path (Windows), and a `..`-laden equivalent (`…/my-package/payload/..`).
   - `outputPath` = the source's `payload/` folder, and its parent.
   - A local-zip source whose zip sits inside `outDir`.
   - A CleanDriver output re-loaded and built into a *different* folder, which must still work.
6. **Verdict document.** APPROVE only if every §AC passes. Any alias spelling that still truncates is a REQUEST CHANGES finding, marked CONFIRMED with a repro.
7. **Feedback to builder.** Three-tier punch list as a PR comment. Fix loop: C0 orient (read the comments, the fence and this doc) → C1 reproduce each finding red → C2 fix → C3 verify each AC with its command output → C4 gates → C5 one `F<n>:` commit per finding, then push → C6 closing comment mapping finding → SHA → AC output → C7 STOP.
8. **Report to owner.** "merge it" or "hold: fixes requested", with the fix prompt attached. Only the owner merges.

## Acceptance criteria
1. `extract` and `package` (the two actions that write to `outputPath`) with `outputPath` equal to the loaded local package folder fail by name (`OutputIsSourceError`) before any write. `install` and `silent`, which never write to `outputPath`, are not refused. Every source file keeps its bytes, and no file is created, deleted or truncated in that folder.
2. The same holds when the source is the bundled catalog package (`/api/sample-package` path) and when `outputPath` differs only by a trailing separator or, on Windows, by case.
3. Re-loading a CleanDriver extract and building into the same folder fails by name. The folder's `manifest.json` and payloads survive unchanged.
4. A selected payload missing from the source raises `PayloadMissing` and leaves no `payload/<name>` file in `outDir`.
5. Building into any other folder is unchanged: `OutDirHygieneTests`, `PackageZipTests` and `PackageValidationTests` are green and unedited.
6. `package` from a local zip into the folder whose archive path (`Packages.ZipPathFor(outDir, version)`) is that same zip fails by name before any write, and the zip keeps every entry byte-for-byte (`PackageFromAZip_IntoTheFolderThatProducedIt_NeverReplacesTheSourceZip`). `extract` from that zip into that folder still succeeds.
