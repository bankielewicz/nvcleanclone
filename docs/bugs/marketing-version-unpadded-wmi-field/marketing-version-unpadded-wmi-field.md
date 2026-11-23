# MarketingVersion misreads WMI driver versions whose last field has fewer than 4 digits

| Field | Value |
|---|---|
| **Slug** | `marketing-version-unpadded-wmi-field` |
| **Severity** | Medium (material incorrectness, with a precondition: an installed driver whose marketing version is `x0y.zz`) |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness); also confirmed through `/api/system` on the Linux headless harness with a `powershell` stand-in that emits the WMI line |
| **Area** | `nvcleanstall/Lib/Gpu.cs` (`MarketingVersion`, reached from `ParseVideoControllers`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both: the defect is in a pure, platform-neutral seam. Real input only comes from Windows WMI. |
| **Related register entries** | none. Distinct from GAP-F01 (adapter coverage, multi-GPU) and from HARD-03/GAP-C04 (how the query runs). This is the arithmetic on a correctly read value. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R08** — Catalog and GPU detection correctness (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`Gpu.MarketingVersion` takes "the last five digits of the dot-stripped string" and splits them 3.2. That works only if the fourth WMI field always has four digits. Windows prints each `DriverVersion` field as a plain integer with no zero padding, so a fourth field below 1000 loses its leading zero. Some examples of what HEAD returns:

| WMI string | Real marketing version | HEAD returns |
|---|---|---|
| `9.18.13.697` | 306.97 | `"136.97"` |
| `8.17.13.142` | 301.42 | `"131.42"` |
| `32.0.16.74` (form for a 600.74 driver) | 600.74 | `"016.74"` |

`/api/system` serves the wrong value. Screen 1 shows it, and the "Installed" badge can then never match the right release.

## Evidence
- `nvcleanstall/Lib/Gpu.cs:26-36`:
  ```csharp
  // NVIDIA reports e.g. 32.0.15.7086 through WMI; the marketing version is
  // the last five digits of the dot-stripped string, split 3.2 -> "570.86".
  public static string? MarketingVersion(string? wmiVersion)
  {
      var digits = Regex.Replace(wmiVersion ?? "", @"\.", "");
      ...
      if (digits.Length < 5 || !digits.All(char.IsAsciiDigit)) return null;
      return $"{digits[^5..^2]}.{digits[^2..]}";
  }
  ```
  - The rule is really "last digit of field 3, then the four digits of field 4". Stripping the dots throws away the field boundary.
  - So when field 4 is `697` (written without padding, where it means `0697`), the "last five digits" reach one digit further into field 3 (`13697`).
- What Windows actually prints: a driver version is stored as four 16-bit integers and printed without padding. Well-known readings for older NVIDIA releases follow this pattern, for example 306.97 shown as `9.18.13.697` and 301.42 as `8.17.13.142` (GPU-Z and Device Manager readings). Today's 5xx drivers (`32.0.15.7086`) and the fixture-era 610.xx drivers (`32.0.16.10xx`) happen to have a four-digit fourth field. Every release numbered `N0x.yy` (for example 600.00–609.99) does not.
- `Gpu.cs:68` feeds the result straight into the served record:
  ```csharp
  InstalledDriverVersion = MarketingVersion(parts.ElementAtOrDefault(1)) ?? parts.ElementAtOrDefault(1) ?? "unknown",
  ```
- Consumers:
  - `Api.cs:22` serves `/api/system`.
  - `wwwroot/app.js:546` renders `gpu-driver` text on Screen 1.
  - `app.js:121` shows the "Installed" badge on a release only when `r.version === state.gpu.installedDriverVersion`.
  - `Lib/Jobs.cs:419` logs `"> Uninstalling driver {gpu.InstalledDriverVersion}… done"` on the clean-install path.
- None of the existing pins cover a fourth field with fewer than four digits (`GpuDetectionTests.MarketingVersion_*`: `1.23`, `132.0.15.7086`, `320157086`, non-numeric).

## Impact
- A user whose installed NVIDIA driver has marketing version `x0y.zz` sees a wrong installed version on Screen 1. Examples: `016.74` for 600.74, `161.23` for 601.23.
- The "Installed" badge never appears on their release.
- The simulated install log names a driver that does not exist.
- These are the facts a user relies on to decide whether to update, and CleanDriver presents them confidently with `detectedVia: "system query (WMI Win32_VideoController)"`.
- No unsafe action follows: the version is display and log data only.
- Likelihood: deterministic for every driver in an `N0x.yy` range. The first such range after today's numbering is 600–609, and the pattern comes back every hundred versions.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/GpuDetectionTests.cs`, next to the other `MarketingVersion` boundary pins:

```csharp
// MarketingVersion boundary — unpadded fourth field: Windows prints each DriverVersion
// field as an unpadded integer, so a fourth field below 1000 has lost its leading zero.
// The 3.2 split must still read "last digit of field 3 + field 4 as four digits".
[Theory]
[InlineData("9.18.13.697", "306.97")]
[InlineData("8.17.13.142", "301.42")]
[InlineData("32.0.16.74", "600.74")]
[InlineData("32.0.16.123", "601.23")]
public void MarketingVersion_FourthFieldBelow1000_KeepsItsImpliedLeadingZero(string wmi, string expected)
{
    Assert.Equal(expected, Gpu.MarketingVersion(wmi));
}

// End to end through the WMI-line parser /api/system serves.
[Fact]
public void ParseVideoControllers_UnpaddedFourthField_ReportsTheRealInstalledVersion()
{
    var gpu = Gpu.ParseVideoControllers("NVIDIA GeForce RTX 4070|32.0.16.74");

    Assert.NotNull(gpu);
    Assert.Equal("600.74", gpu!.InstalledDriverVersion);
}
```
(The first two fields for a future 6xx build are not known. Neither HEAD's rule nor the fix reads them; both depend only on fields 3 and 4.)

Command (real repo):
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~GpuDetectionTests"
```

Captured RED output from the Linux harness (`repro/marketing-version-unpadded-wmi-field.cs`, class `MarketingVersionUnpaddedWmiFieldRepro`), HEAD `54b3c31`, trimmed:
```
  Failed ...MarketingVersion_FourthFieldBelow1000_KeepsItsImpliedLeadingZero(wmi: "9.18.13.697", expected: "306.97")
   Assert.Equal() Failure: Strings differ
Expected: "306.97"
Actual:   "136.97"
  Failed ...(wmi: "8.17.13.142", expected: "301.42")
Expected: "301.42"
Actual:   "131.42"
  Failed ...(wmi: "32.0.16.74", expected: "600.74")
Expected: "600.74"
Actual:   "016.74"
  Failed ...(wmi: "32.0.16.123", expected: "601.23")
Expected: "601.23"
Actual:   "161.23"
  Failed ...ParseVideoControllers_UnpaddedFourthField_ReportsTheRealInstalledVersion
Expected: "600.74"
Actual:   "016.74"
Failed!  - Failed:     5, Passed:     0, Skipped:     0, Total:     5
```

### B. Live — running app
On Windows this reproduces only on a machine with an `N0x.yy` driver installed. I reproduced it through the real HTTP route with the Linux headless harness: a copy of `Program.cs --headless` serving the real `Api.cs`/`Lib/*.cs`, plus a `powershell` stand-in placed first on `PATH` that prints the two WMI lines a desktop with an iGPU produces:

1. Stand-in `powershell` script:
   ```sh
   #!/bin/sh
   echo 'Intel(R) UHD Graphics 770|31.0.101.4502'
   echo 'NVIDIA GeForce RTX 4070|32.0.16.74'
   ```
2. `PATH=<shim-dir>:$PATH CLEANDRIVER_URLS=http://127.0.0.1:4804 dotnet out/Server.dll --mock-catalog`. This is the equivalent of `dotnet run --project nvcleanstall -- --headless --mock-catalog`, which serves on port 4780.
3. `curl -s http://127.0.0.1:4804/api/system`
   - Expected `"installedDriverVersion": "600.74"`.
   - Actual:
   ```json
   { "name": "GeForce RTX 4070", "installedDriverVersion": "016.74",
     "isSimulated": false, "detectedVia": "system query (WMI Win32_VideoController)" }
   ```

## Root cause
The code encodes the derivation as a string operation on the dot-stripped digits ("last five"). The real rule is defined per field: hundreds digit = last digit of field 3; tens, units and the two decimals = field 4 read as four digits. "Last five digits" equals that rule only when field 4 is printed with all four digits. Windows drops leading zeros, so for field 4 < 1000 the string rule takes a digit from the wrong field.

## Remedy
Minimal fix: when the value has the four-field shape, compute from the fields, padding field 4 to four digits. Every other shape keeps the existing last-five-digits rule, so all current pins are unchanged.

```diff
     public static string? MarketingVersion(string? wmiVersion)
     {
+        // Windows prints each of the four DriverVersion fields as an unpadded integer, so a
+        // fourth field below 1000 has lost its leading zero ("9.18.13.697" is 306.97). In the
+        // four-field form, take the third field's last digit + the fourth field padded to 4.
+        var fields = (wmiVersion ?? "").Split('.');
+        if (fields.Length == 4 && fields.All(f => f.Length > 0 && f.All(char.IsAsciiDigit))
+            && fields[3].Length <= 4)
+        {
+            var five = fields[2][^1] + fields[3].PadLeft(4, '0');
+            return $"{five[..3]}.{five[3..]}";
+        }
         var digits = Regex.Replace(wmiVersion ?? "", @"\.", "");
```
- **Why minimal:** one guarded branch. The fallback path is byte-identical, so `MarketingVersion_Dotless_DerivesFromLastFiveDigits` (`320157086`), `_Long_` (`132.0.15.7086` → `570.86` through the new branch, same answer), `_Short_ReturnsNull` (`1.23` is two fields, so the old rule applies and returns null) and `_NonNumeric_ReturnsNull` all still hold. So do `ParseVideoControllers_*` and `GpuTimeoutTests` (`32.0.15.7086` → `570.86`).
- **Alternatives rejected:**
  - (a) Always pad every field and strip. This changes the dotless and short pins' meaning.
  - (b) `System.Version.TryParse` with `(Build % 10) * 10000 + Revision`. Equivalent, but it needs an extra `Revision <= 9999` guard and a format string; either form is fine.
  - (c) Read the INF/registry `DriverVersion` instead of WMI. Larger change, and GAP-C04/GAP-F01 territory.
- **Fences:** no change to the WMI query, the 5 s budget, or the `Simulated` fallback (HARD-03 pins); `GpuInfo` shape unchanged.
- **Verified:** I applied the diff to a scratch copy. Full suite plus all repros: 161/161 green.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** Settled: slice **GAP-R08**. GAP-C04 touches `Gpu.cs` but is scoped to the PowerShell path, so this was never part of it.
   > *Pinned 2026-11-23 as **GAP-R08** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-wmi-version -b feat/marketing-version-unpadded-wmi-field origin/main`
2. **Red.** Add the §A theory and fact to `nvcleanstall/CleanDriver.Tests/GpuDetectionTests.cs`. Run `--filter "FullyQualifiedName~GpuDetectionTests"` and confirm the five failures above (`136.97`, `131.42`, `016.74`, `161.23`, `016.74`). Commit `"GAP-R08 (red): marketing-version-unpadded-wmi-field — unpadded fourth WMI field keeps its implied leading zero"`.
3. **Green.** Apply the guarded branch. Run the FULL suite. Commit `"GAP-R08: marketing-version-unpadded-wmi-field — derive the marketing version per field, not from the last five digits"`.
4. **Refactor.** Optionally fold the 3.2 formatting into one local helper used by both branches, with the suite green. Update the `Gpu.cs:26-27` comment so it states the per-field rule.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** `dotnet run --project nvcleanstall -- --headless --mock-catalog` then `curl -s localhost:4780/api/system`.
   - On today's drivers, show the value is unchanged (e.g. `570.86`/`591.86`). This is the back-compat check PR #5's review used.
   - If an `N0x.yy` driver is available on a test machine, show it reads correctly. Otherwise state that the live path is covered by the `ParseVideoControllers` test, which is the exact function `/api/system` serves through `DetectFrom`.
7. **PR.** Fill `.github/pull_request_template.md`: one red/green row per theory case; "No existing test was edited". Push. Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green first. The smoke step prints `GPU detection path`, which confirms the runner still resolves.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-wmi origin/feat/marketing-version-unpadded-wmi-field`. Range `origin/main...HEAD`.
3. **Re-run gates.** Run `/api/system` on the reviewer's real NVIDIA host if available and compare it with Device Manager's driver version.
4. **Finder pass.**
   - Is the four-field branch guarded on all-digit fields, so `31.0.15.abcd` still returns null?
   - Is field 4 longer than 4 digits refused (the old rule applies)?
   - Are the dotless, short and long pins untouched and green?
   - Is `ParseVideoControllers` still using `MarketingVersion` and not a fork?
   - Are no culture-sensitive APIs introduced (string slicing only)?
   - Optionally run `/code-review` on the PR diff as the finder.
5. **Adversarial verify** with throwaway tests:
   - `32.0.16.0074` (zero-padded input). Expect `600.74`, idempotent.
   - `32.0.15.7086`. Expect `570.86`.
   - `32.0.16.5`. Expect `600.05`.
   - `32.0..7086` (empty field) and `32.0.15.70860` (five-digit field 4). Both must take the unchanged old rule, so the result is byte-identical to HEAD (`070.86` and `708.60`). Flag it if the builder silently changes these; tightening them to null is a separate decision.
   - `1.2.3`. Three fields, expect the old rule, which returns null.
   - A trailing `\r` through `ParseVideoControllers` (CRLF output). Expect it trimmed.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Implementer handoff. Fix loop C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relay. Only the owner merges.

## Acceptance criteria
1. `Gpu.MarketingVersion("9.18.13.697") == "306.97"`, `("8.17.13.142") == "301.42"`, `("32.0.16.74") == "600.74"`, `("32.0.16.123") == "601.23"`.
2. `Gpu.ParseVideoControllers("NVIDIA GeForce RTX 4070|32.0.16.74")!.InstalledDriverVersion == "600.74"`.
3. Every existing `GpuDetectionTests`/`GpuTimeoutTests` pin passes unedited, including `320157086 → 570.86`, `132.0.15.7086 → 570.86`, `1.23 → null`, and the non-numeric cases returning null.
4. `/api/system` on a current-driver host returns the same `installedDriverVersion` as before the change.
5. Build, the full test suite, and `git diff --check` are green.
