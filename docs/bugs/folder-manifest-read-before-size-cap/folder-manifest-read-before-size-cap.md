# Folder-package (and prior-build) `manifest.json` is read whole into memory before the 1 MiB cap

| Field | Value |
|---|---|
| **Slug** | `folder-manifest-read-before-size-cap` |
| **Severity** | Medium. This is availability, the SEC-04 class: one request drives the process to a multi-GB peak, hits `OutOfMemoryException`, and gets an unhandled HTTP 500. It needs the user to load a hostile or oversized local folder, which is the same precondition SEC-04 has. |
| **Confidence** | Confirmed: the repro test fails on HEAD (Linux harness), and it was reproduced live (1.5 GiB manifest → 3.3 GB peak RSS → OOM → HTTP 500). |
| **Area** | `nvcleanstall/Lib/Packages.cs` (`LoadLocal` folder branch, `ReadManifest`, `CleanPreviousBuild`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | The SEC-04 / GAP-S01 fix ("reject … manifests over 1 MiB") is incomplete. The zip path rejects by declared length *before* reading, but the folder path and the HARD-01 prior-manifest read measure only *after* reading the whole file. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R03** — Package input hardening (GAP-S01 follow-ups) (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
GAP-S01 added a 1 MiB manifest cap. On the zip path the cap is checked against the entry's declared length before anything is read (`Packages.cs:63`). On the folder path, `LoadLocal` calls `File.ReadAllText` on `manifest.json` with no size check. `ReadManifest` then measures the string that has already been materialized (`Packages.cs:80`).

So an oversized folder manifest is fully decoded into a UTF-16 string, plus `StringBuilder` growth, before it is rejected. Past about 1 G characters, .NET throws `OutOfMemoryException`. `Api.cs` doesn't catch that, so the client gets HTTP 500.

`CleanPreviousBuild` reads `outDir/manifest.json` the same unbounded way (`Packages.cs:188`).

## Evidence
`Packages.cs:47-52`, the folder branch, which reads first:
```csharp
var mf = Path.Combine(localPath, "manifest.json");
if (!File.Exists(mf)) throw new InvalidDataException("manifest.json not found in package folder");
var manifest = ReadManifest(File.ReadAllText(mf));          // :51 — whole file, no length check
```
`Packages.cs:62-65`, the zip branch, which checks first:
```csharp
// Reject an oversized manifest by its declared length before reading it into memory.
PackageValidation.ValidateManifestBytes(entry.Length);      // :63
```
`Packages.cs:80`, where the shared check only runs after the read:
```csharp
PackageValidation.ValidateManifestBytes(Encoding.UTF8.GetByteCount(json));
```
`Packages.cs:188`, the HARD-01 clean, unbounded as well:
```csharp
try { doc = JsonDocument.Parse(File.ReadAllText(manifestPath)); }
catch (JsonException) { return removed; }
```
`Api.cs:65-74` catches `InvalidDataException`, `IOException`, `JsonException` and `UnauthorizedAccessException`. It does not catch `OutOfMemoryException`.

## Impact
- **Availability.** A single `POST /api/package {"kind":"local"}` against a folder whose `manifest.json` is ≥ ~1 GiB drives the process to ~3.3 GB peak RSS for several seconds and ends in OOM and HTTP 500. On an 8 GB machine this causes heavy paging. The file can come from extracting a ~1.5 MB zip of a 1.5 GiB zero-filled `manifest.json`, which is the same zip-bomb shape SEC-04 targets.
- Even below the OOM threshold, memory scales with the file. The harness measured **4×** the file size on load and **7×** in the HARD-01 clean.
- The HARD-01 clean's read happens inside the job. An oversized `outDir/manifest.json` causes the same spike there. At OOM size it fails the job with the runtime's message.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/PackageValidationTests.cs`, or as `ManifestSizeCapTests.cs`. It's the same as the harness `repro/folder-manifest-read-before-size-cap.cs`. The sparse file costs no disk. `GC.GetAllocatedBytesForCurrentThread` is deterministic for this synchronous call.
```csharp
using System;
using System.IO;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

public class ManifestSizeCapTests
{
    private const long Oversize = 64L << 20;   // 64 MiB, sparse

    private static string SparseManifest(string dir)
    {
        Directory.CreateDirectory(dir);
        var mf = Path.Combine(dir, "manifest.json");
        using (var fs = File.Create(mf)) fs.SetLength(Oversize);
        return mf;
    }

    [Fact]
    public void LoadLocal_FolderWithOversizedManifest_RejectsWithoutReadingItIntoMemory()
    {
        var pkg = Path.Combine(TempDir.Create(), "pkg");
        SparseManifest(pkg);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var ex = Assert.Throws<InvalidDataException>(() => Packages.LoadLocal(pkg));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(PackageValidation.ManifestTooLarge, ex.Message);
        Assert.True(allocated < 8L << 20,
            $"rejecting a {Oversize >> 20} MiB folder manifest allocated {allocated >> 20} MiB — it was read whole before the 1 MiB cap");
    }

    [Fact]
    public void CleanPreviousBuild_OversizedPriorManifest_IsNotReadWhole()
    {
        var outDir = Path.Combine(TempDir.Create(), "out");
        SparseManifest(outDir);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var removed = Packages.CleanPreviousBuild(outDir);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Empty(removed);
        Assert.True(allocated < 8L << 20,
            $"CleanPreviousBuild allocated {allocated >> 20} MiB reading a {Oversize >> 20} MiB outDir manifest.json");
    }
}
```
Run:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~ManifestSizeCapTests"
```
RED output, trimmed, from the Linux `net10.0` harness at `54b3c31` (class `FolderManifestReadBeforeSizeCapRepro`):
```
Failed ...LoadLocal_FolderWithOversizedManifest_RejectsWithoutReadingItIntoMemory
  rejecting a 64 MiB folder manifest allocated 256 MiB — it was read whole before the 1 MiB cap
Failed ...CleanPreviousBuild_OversizedPriorManifest_IsNotReadWhole
  CleanPreviousBuild allocated 448 MiB reading a 64 MiB outDir manifest.json
Failed!  - Failed: 2, Passed: 0
```
The exception type on the first test is already correct (`ManifestTooLarge`). The red is purely the read-before-check.

### B. Live — running app
Captured with the Linux harness on port 4802. On Windows, `fsutil file createnew C:\tmp\l3\bigpkg\manifest.json 1610612736` makes the same zero-filled file.
1. `truncate -s 1536M /tmp/l3/bigpkg/manifest.json`. Server RSS before: `VmHWM: 90416 kB`.
2. `curl -s -w "HTTP %{http_code}\n" -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"local","path":"C:/tmp/l3/bigpkg"}'`

**Expected:** HTTP 400 `{"error":"manifest rejected: manifest is too large"}`, immediately.
**Actual (captured):**
```
HTTP 500            (empty body)   elapsed 5.78s
VmHWM:   3332344 kB                <- 3.3 GB peak
server log: System.OutOfMemoryException: Insufficient memory to continue the execution of the program.
   at System.Text.StringBuilder.ToString()
   at System.IO.File.ReadAllText(String path, Encoding encoding)
   at CleanDriver.Lib.Packages.LoadLocal(String localPath) in …/Lib/Packages.cs:line 51
```
3. Control: the *same* 1.5 GiB manifest inside a 1.5 MB zip (`POST … {"kind":"local","path":".../pkg.zip"}`) gives `HTTP 400 {"error":"manifest rejected: manifest is too large"}` in **0.016 s**.

## Root cause
The cap was implemented as a property of the *text* (`GetByteCount(json)` in `ReadManifest`) rather than of the *source*. The zip branch added an extra pre-read length check, but the folder branch and `CleanPreviousBuild` have no equivalent. A cap applied after `ReadAllText` bounds what is accepted, not what is read.

## Remedy
Check the on-disk length before reading, on every file-backed manifest read:
```csharp
// Packages.cs — one helper, used by LoadLocal (folder), LoadCatalog/LoadSampleTemplate, CleanPreviousBuild
private static string ReadManifestText(string path)
{
    PackageValidation.ValidateManifestBytes(new FileInfo(path).Length);   // reject before reading
    return File.ReadAllText(path);
}
```
- In `LoadLocal`, use `ReadManifest(ReadManifestText(mf))`.
- In `CleanPreviousBuild`, an oversized prior manifest is "not ours", so return `removed` without reading. Wrap the call in `catch (InvalidDataException) { return removed; }` to keep the never-throws contract.
- `ReadManifest`'s post-read `GetByteCount` check stays as defense in depth.

Why this is minimal: it mirrors the zip branch's existing pre-read check. The behavior change is limited to inputs that are rejected already.

Alternatives considered:
- A bounded streaming reader that stops at `MaxManifestBytes + 1`. This is more robust against a file growing between the stat and the read. It's an acceptable stronger fix, but not required by the AC.
- Catching `OutOfMemoryException` in `Api.cs`. Rejected: it treats the symptom, it's out of this file, and the memory spike still happens.

Fences respected: no deletion change (the clean only *skips* sooner); never-throws is preserved (the `ForeignOrMalformedManifest_NothingIsDeleted` pin); no UI or honesty change. `ReadManifest_OversizedManifest_Rejected` keeps passing, since that manifest is about 1 MiB + 1 KB on disk.

Edge to disclose: a CleanDriver-written manifest over 1 MiB, for example one carrying an enormous request `tweaks` dict, would no longer be cleaned. That's acceptable ("not ours, touch nothing"), and GAP-C01 will bound tweaks.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** SEC-04's fix is incomplete. Request a new owner-pinned ID per §7 (e.g. `SEC-09`, Medium) with destination "GAP-S01 follow-up".
   > *Pinned 2026-11-23 as **GAP-R03** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** `git worktree add ../nvcleanclone-mfcap -b feat/manifest-size-cap-before-read origin/main`
2. **Red.** Add `nvcleanstall/CleanDriver.Tests/ManifestSizeCapTests.cs` (§A). Run `--filter "FullyQualifiedName~ManifestSizeCapTests"` and confirm both fail on the allocation assertion. Commit `"SEC-09 (red): manifest size cap must apply before the file is read"`.
3. **Green.** Add `ReadManifestText` and use it in `LoadLocal` (folder) and `CleanPreviousBuild`. Optionally use it in `LoadCatalog`/`LoadSampleTemplate` for uniformity. Run the full suite. Commit `"SEC-09: check manifest.json length before reading (folder load + HARD-01 clean)"`.
4. **Refactor.** Only with the suite green.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run §B with a 1.5 GiB file: HTTP 400 `manifest rejected: manifest is too large` within well under a second, with the server's working set flat (Task Manager / `Get-Process CleanDriver | select WS`).
7. **PR.** Template rows for both tests. Disclose the >1 MiB own-manifest edge. Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-mfcap origin/feat/manifest-size-cap-before-read`; range `origin/main...HEAD`.
3. **Re-run gates** and §B live, measuring memory.
4. **Finder pass.** Check:
   - Every `File.ReadAllText` of a manifest in `Packages.cs` is preceded by a length check, or replaced with a bounded read.
   - `CleanPreviousBuild` still never throws.
   - The rejection message is the stable `ManifestTooLarge` (path-free).
   - No other reader of untrusted JSON was missed (`Presets.List/Load` are GAP-C03's, so note them, don't fix them here).
5. **Adversarial verify.** Try:
   - A manifest of exactly `MaxManifestBytes` (accepted) and `+1` (rejected).
   - A sparse 3 GiB file.
   - A FIFO/device path named `manifest.json` on Linux CI, if applicable.
   - A file that grows after the stat (PLAUSIBLE TOCTOU; acceptable if documented).
   - A manifest over the cap inside `outDir` (the clean skips; the job proceeds).
6. **Verdict document.** APPROVE / REQUEST CHANGES, with CONFIRMED/PLAUSIBLE findings.
7. **Feedback to builder.** Fix loop C0 orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 `F<n>:` commits → C6 closing report → C7 STOP.
8. **Report to owner.** Verdict line; owner merges.

## Acceptance criteria
1. `Packages.LoadLocal(folder)` with a `manifest.json` larger than `MaxManifestBytes` throws `InvalidDataException(ManifestTooLarge)` while allocating less than 8 MiB, whatever the file size.
2. `POST /api/package {"kind":"local"}` on a 1.5 GiB folder manifest returns HTTP 400 `manifest rejected: manifest is too large` in under 1 s, with no memory spike and no 500.
3. `Packages.CleanPreviousBuild(outDir)` with an oversized `outDir/manifest.json` returns an empty list without reading the file (< 8 MiB allocated) and without throwing.
4. Existing `PackageValidationTests` and `OutDirHygieneTests` pins stay green and unedited.
