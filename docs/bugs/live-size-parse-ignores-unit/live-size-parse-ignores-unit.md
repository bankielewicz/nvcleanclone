# Live release size parser ignores the unit: a size given in GB is reported as that many MB

| Field | Value |
|---|---|
| **Slug** | `live-size-parse-ignores-unit` |
| **Severity** | Low (wrong size and progress display; no safety or data-integrity effect) |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness). Caveat: the triggering input, a `DownloadURLFileSize` stated in GB, is **not** in the recorded fixture. It is plausible (see Impact), not observed. |
| **Area** | `nvcleanstall/Lib/NvidiaCatalogProvider.cs` (`ParseSizeMB`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | none |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R08** — Catalog and GPU detection correctness (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`ParseSizeMB` keeps whatever number comes before the first space and never reads the unit after it. NVIDIA's `DownloadURLFileSize` is a human-readable string (`"979.17 MB"` in the recorded response). Any size stated in another unit is therefore taken as megabytes: `"1.02 GB"` becomes `SizeMB = 1`. That contradicts the method's own contract, which is the value in MB, or 0 when absent or unparseable. The recorded sizes are already ~979 MB, and Jobs.cs notes that "real packages are ~1 GB". So the first driver NVIDIA reports in GB would show as a 1 MB download.

## Evidence
- `nvcleanstall/Lib/NvidiaCatalogProvider.cs:124-130`:
  ```csharp
  // "979.17 MB" -> 979; 0 when absent/unparseable.
  private static int ParseSizeMB(string raw)
  {
      var token = raw.Split(' ').FirstOrDefault() ?? "";
      return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb)
          ? (int)Math.Round(mb) : 0;
  }
  ```
  The unit, `raw.Split(' ')[1]`, is discarded unread, so `"1.02 GB"`, `"1 GB"` and `"812 KB"` all "parse" successfully as MB.
- Recorded data: `fixtures/nvidia-drivermanuallookup.json` sizes are `"979.17 MB"`, `"978.43 MB"`, `"978.41 MB"`, `"978.48 MB"`, `"978.47 MB"`, about 2% under 1000 MB. `Lib/Jobs.cs:162`: `// Configurable cap (register): env override, else 2 GiB (real packages are ~1 GB).`
- Consumers of `Release.SizeMB`:
  - `wwwroot/app.js:148-149`: Screen 2 shows `${rel.version} ${rel.channel} — ${rel.sizeMB} MB` and `dl-total`.
  - `Lib/Jobs.cs:218`: `job.TotalMB = release.SizeMB;` until a Content-Length arrives.
  - `Lib/Jobs.cs:310`: `long denom = total ?? (long)release.SizeMB * 1024 * 1024;` is the progress denominator when the server sends no Content-Length.

## Impact
- If NVIDIA reports a package size in GB, CleanDriver shows "1 MB" on Screen 2's heading and total for a ~1 GB download.
- If that response also comes without a Content-Length (chunked transfer), the progress denominator is 1 MiB. Progress then pins at 0.99 after the first megabyte (`Math.Min(0.99, prog)`) and the bar sits at 99% for the whole real download.
- When Content-Length is present, `TotalMB` is corrected from the header (`Jobs.cs:308`), but the Screen 2 heading still shows the catalog's 1 MB.
- No unsafe effect: the 2 GiB byte cap and disk-space check use real byte counts, not `SizeMB`.
- Likelihood: unknown, because it depends on NVIDIA's formatting threshold. The parser fails for *any* non-MB unit, and real packages are within ~2% of the 1000 MB line.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/NvidiaCatalogProviderTests.cs`:

```csharp
private static string SizedBody(string size) => $$"""
{ "Success":"1", "IDS":[
  { "downloadInfo": {
      "Success":"1", "Version":"611.20", "IsBeta":"0", "IsWHQL":"1", "IsCRD":"0",
      "ReleaseDateTime":"Tue Aug 04, 2026", "DownloadURLFileSize":"{{size}}",
      "DownloadURL":"https://us.download.nvidia.com/Windows/611.20/611.20-desktop-win10-win11-64bit-international-dch-whql.exe" } }
]}
""";

// A >1 GB installer must not be reported as a 1 MB one. (MiB-vs-MB conversion is the
// fix's call; either lands well above 1000 — the bound asserted here.)
[Theory]
[InlineData("1.02 GB")]
[InlineData("1 GB")]
public void GetReleases_SizeStatedInGB_IsConvertedToMegabytes(string size)
{
    var rel = Assert.Single(new NvidiaCatalogProvider(StubHandler.Ok(SizedBody(size))).GetReleases(Rtx4070));
    Assert.InRange(rel.SizeMB, 1000, 1100);
}
```
Command (real repo):
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~NvidiaCatalogProviderTests.GetReleases_SizeStatedInGB_IsConvertedToMegabytes"
```
Captured RED output from the Linux harness (`repro/live-size-parse-ignores-unit.cs`, class `LiveSizeParseIgnoresUnitRepro`), HEAD `54b3c31`:
```
  Failed CleanDriver.Tests.LiveSizeParseIgnoresUnitRepro.GetReleases_SizeStatedInGB_IsConvertedToMegabytes(size: "1 GB") [44 ms]
   Assert.InRange() Failure: Value not in range
Range:  (1000 - 1100)
Actual: 1
  Failed CleanDriver.Tests.LiveSizeParseIgnoresUnitRepro.GetReleases_SizeStatedInGB_IsConvertedToMegabytes(size: "1.02 GB") [< 1 ms]
   Assert.InRange() Failure: Value not in range
Range:  (1000 - 1100)
Actual: 1
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
```
(Also checked: the parser is culture-safe. de-DE, tr-TR, ar-SA, th-TH, fa-IR and ja-JP give identical results for `"979.17 MB"` with ICU data present. This bug is about the unit, not the culture.)

### B. Live — running app
Not applicable here. The value comes only from the live NVIDIA provider, `--mock-catalog` serves the integer sizes in `data/catalog.json`, and this bug hunt is barred from live NVIDIA traffic. The test drives the same `NvidiaCatalogProvider.GetReleases` that `/api/catalog` and `/api/download` use.

## Root cause
The method was written for the one unit seen in the PF-2 recording. It reads the number token and assumes the unit, so the assumption is never checked. Its own contract ("0 when absent/unparseable") requires it to recognize when it does not understand a value. Here it misreads the value instead.

## Remedy
Minimal fix: read the unit, convert the known ones, and return 0 for anything else.

```diff
-    // "979.17 MB" -> 979; 0 when absent/unparseable.
+    // "979.17 MB" -> 979, "1.02 GB" -> 1044; 0 when absent/unparseable or the unit is unknown.
     private static int ParseSizeMB(string raw)
     {
-        var token = raw.Split(' ').FirstOrDefault() ?? "";
-        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb)
-            ? (int)Math.Round(mb) : 0;
+        var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
+        if (parts.Length != 2 ||
+            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
+            return 0;
+        double? mb = parts[1].ToUpperInvariant() switch
+        {
+            "MB" => n,
+            "GB" => n * 1024,
+            _ => null,
+        };
+        return mb is double v ? (int)Math.Round(v) : 0;
     }
```
- **Why minimal:** one private method; same culture-invariant parsing; `"979.17 MB"` → 979 unchanged.
- **Multiplier:** `× 1024` matches how Jobs.cs converts bytes to "MB" (`/ (1024.0 * 1024.0)`). The test's 1000–1100 bound accepts either convention, so the builder can choose and disclose.
- **Alternatives rejected:**
  - (a) Derive the size from a HEAD request's Content-Length. Extra network round-trips per release on Screen 1, inside a 5 s budget.
  - (b) Drop `SizeMB` for live rows. Breaks the Screen 2 contract.
- **Fences:** none touched (display metadata only; honesty markers, safety and IP unaffected).
- **Back-compat pins:** `NvidiaCatalogProviderTests.GetReleases_ParsesRecordedFixture_IntoLiveReleases` (`Assert.Equal(979, releases[0].SizeMB)`) is still green. Verified in a scratch copy: full suite plus repros 161/161.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** Settled: slice **GAP-R08**, with the other catalog and GPU fixes; GAP-F01 stays separate.
   > *Pinned 2026-11-23 as **GAP-R08** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-size-unit -b feat/live-size-parse-ignores-unit origin/main`
2. **Red.** Add the §A theory to `nvcleanstall/CleanDriver.Tests/NvidiaCatalogProviderTests.cs`. Run the filter and confirm `Actual: 1` for both cases. Commit `"GAP-R08 (red): live-size-parse-ignores-unit — a live size stated in GB is converted to MB"`.
3. **Green.** Unit-aware `ParseSizeMB`. Run the FULL suite. Commit `"GAP-R08: live-size-parse-ignores-unit — read the unit in DownloadURLFileSize"`.
4. **Refactor.** Suite green only.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.**
   - `dotnet run --project nvcleanstall -- --headless --mock-catalog` then `curl -s localhost:4780/api/catalog`. Mock sizes are unchanged (812/809/806/801/774).
   - On a live host, `curl -s localhost:4780/api/catalog` and paste `sizeMB` next to NVIDIA's published size for the newest release.
7. **PR.** Fill `.github/pull_request_template.md`. Disclose the chosen MB/MiB convention under Deviations. Push. Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green first.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-size origin/feat/live-size-parse-ignores-unit`. Range `origin/main...HEAD`.
3. **Re-run gates** on the reviewer's checkout, then do the `--mock-catalog` curl.
4. **Finder pass.**
   - Is `InvariantCulture` still used?
   - Is the unit match case-insensitive?
   - Are unknown units → 0, as the contract says?
   - Do extra spaces or tabs not break `"979.17 MB"`?
   - Is no change made outside `ParseSizeMB`?
   - Optionally run `/code-review` on the PR diff as the finder.
5. **Adversarial verify:**
   - `"979.17 MB"` → 979.
   - `"1.02 gb"` → about 1044.
   - `"0.98 GB"` → about 1004.
   - `"812 KB"` → 0 (or 1 if the builder adds KB; either way it must not be 812).
   - `"979.17MB"` (no space) → 0, the same as HEAD; note it.
   - `""` → 0.
   - Under `CultureInfo.CurrentCulture = de-DE`, `"1.02 GB"` → about 1044.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Implementer handoff. Fix loop C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relay. Only the owner merges.

## Acceptance criteria
1. A live row with `DownloadURLFileSize` `"1.02 GB"` or `"1 GB"` yields `SizeMB` in [1000, 1100].
2. `"979.17 MB"` still yields 979 (the recorded-fixture pin is unedited and green).
3. A size string with an unrecognized unit yields 0, not its bare number.
4. Parsing is culture-invariant (the same result under de-DE).
5. Build, the full suite, and `git diff --check` are green.
