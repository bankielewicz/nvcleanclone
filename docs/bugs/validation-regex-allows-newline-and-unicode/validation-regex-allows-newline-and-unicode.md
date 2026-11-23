# GAP-S01 allowlist regexes accept a trailing newline (`$`) and non-ASCII digits (`\d`)

| Field | Value |
|---|---|
| **Slug** | `validation-regex-allows-newline-and-unicode` |
| **Severity** | Low. The allowlists are wider than documented and admit control characters and non-ASCII digits into version labels, file names and archive entry names. They cannot produce traversal (no separator or `..` gets through), so this is bounded incorrectness, not escape. |
| **Confidence** | Confirmed: the repro test fails on HEAD (Linux harness), and it was reproduced live. These are pure regex semantics, identical on Windows. |
| **Area** | `nvcleanstall/Lib/PackageValidation.cs` (`VersionRe`, `ComponentIdRe`, `PayloadRe`, so `ValidateVersion`, `ValidateManifest`, `IsSafeLeafPayload`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. The Windows consequences below are reasoned from Win32 naming rules, not run. |
| **Related register entries** | GAP-S01 Fix: "Current-format version, component ID, and payload names use allowlists". The `PackageValidation.cs:41-42` comment says "dotted decimals only". The `5447080`/`62b19ed` commit messages say "VersionRe not widened", but `\d` already is wider. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R03** — Package input hardening (GAP-S01 follow-ups) (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
All three GAP-S01 allowlists end in `$`. In .NET, `$` matches at the end of the string *or before a final `\n`*. So `"572.16\n"`, `"display-driver.bin\n"` and `"display-driver\n"` pass validation.

`VersionRe` also uses `\d`. Without `RegexOptions.ECMAScript`, `\d` matches every Unicode decimal digit (`Nd`), so fullwidth `５７２.１６` and Arabic-Indic `٥٧٢.١٦` are accepted as "dotted decimal" driver versions.

These values then reach the places GAP-S01 was meant to protect:
- the relabeled `manifest.Version` (Sink 6),
- the default `outDir` name `extract-<version>`,
- the zip name `<version>-cleandriver-package.zip`,
- payload file names,
- archive entry names (AC-4).

## Evidence
`PackageValidation.cs:41-45`:
```csharp
// Current NVIDIA driver version format: dotted decimals only. Every dot is followed by a
// digit, so a separator or `..` cannot appear — accepts 572.16 / 610.74, rejects the attacks.
private static readonly Regex VersionRe = new(@"^\d{1,5}(\.\d{1,5}){1,3}$", RegexOptions.Compiled);
private static readonly Regex ComponentIdRe = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
private static readonly Regex PayloadRe = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);
```
`IsSafeLeafPayload`'s "belt to the regex's suspenders" checks (`:55-61`) test for separators, `.`/`..` and rootedness. None of them catches `\n`.

`Api.cs:51` → `Packages.LoadSampleTemplate` (`:35-39`). A client version passing `ValidateVersion` becomes `manifest.Version`:
```csharp
with { Version = version, SampleComponents = true };
```
`Jobs.cs:386-388` then derives `Path.Combine(OutputDir, $"{action}-{manifest.Version}")`, and `Packages.ZipPathFor` derives `$"{version}-cleandriver-package.zip"`.

## Impact
- **A control character in on-disk names.** `POST /api/package {"kind":"catalog","version":"572.16\n"}` is accepted (HTTP 200, relabeled sample). A default `extract` then creates the directory `output/extract-572.16\n`. That was observed on Linux.
- On Windows, LF is invalid in NTFS names (reasoned, not run), so the same request passes validation and then fails the job inside `Directory.CreateDirectory`/`File.Create` with an `IOException`. AC-2 requires a 400 at the boundary instead. Its message embeds the absolute path, because job errors surface `ex.Message`.
- A payload `"display-driver.bin\n"` behaves the same way: accepted at load, then written as a newline-suffixed file (Linux) or failing at write time (Windows). On Linux it produces an archive entry `payload/display-driver.bin\n`, which contradicts AC-4's "normalized relative paths".
- **Version identity confusion.** `572.16` and `572.16\n` are distinct accepted versions. `572.16\n` doesn't match the bundled `572.16` directory, so it is served the *sample template* with `SampleComponents=true`. Fullwidth and Arabic-Indic digit versions are accepted labels that render as different-looking but "valid" versions in the UI, receipts and file names.
- No traversal: neither LF nor Unicode digits is a separator, and `..` still can't form. That's why this is Low.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/PackageValidationTests.cs`. It's the same as the harness `repro/validation-regex-allows-newline-and-unicode.cs`.
```csharp
// The allowlists are ASCII and whole-string: `$` must not admit a trailing '\n', and the version
// digit class must not admit non-ASCII Unicode digits.
[Theory]
[InlineData("572.16\n")]                                // trailing LF: `$` matches before it
[InlineData("５７２.１６")]         // fullwidth ５７２.１６
[InlineData("٥٧٢.١٦")]         // Arabic-Indic ٥٧٢.١٦
public void ValidateVersion_RejectsNonAsciiOrControlCharacters(string version)
{
    Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateVersion(version));
}

[Fact]
public void IsSafeLeafPayload_RejectsATrailingNewline()
{
    Assert.False(PackageValidation.IsSafeLeafPayload("display-driver.bin\n"));
}

[Fact]
public void ValidateManifest_RejectsAComponentIdWithATrailingNewline()
{
    var m = ManifestOf(Comp("display-driver\n", "display-driver.bin", required: true));
    Assert.Throws<InvalidDataException>(() => PackageValidation.ValidateManifest(m));
}

[Fact]
public void LoadSampleTemplate_VersionWithTrailingNewline_IsRejected()
{
    Assert.Throws<InvalidDataException>(() => Packages.LoadSampleTemplate("610.74\n"));
}
```
`ManifestOf`/`Comp` are the existing builders in that class. Run:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~PackageValidationTests"
```
RED output, trimmed, from the Linux `net10.0` harness at `54b3c31` (class `ValidationRegexAllowsNewlineAndUnicodeRepro`):
```
Failed ...ValidateVersion_RejectsNonAsciiOrControlCharacters(version: "٥٧٢.١٦")      Assert.Throws() Failure: No exception was thrown
Failed ...ValidateVersion_RejectsNonAsciiOrControlCharacters(version: "572.16\n")    Assert.Throws() Failure: No exception was thrown
Failed ...ValidateVersion_RejectsNonAsciiOrControlCharacters(version: "５７２.１６")   Assert.Throws() Failure: No exception was thrown
Failed ...LoadSampleTemplate_VersionWithTrailingNewline_IsRejected                  Assert.Throws() Failure: No exception was thrown
Failed ...ValidateManifest_RejectsAComponentIdWithATrailingNewline                  Assert.Throws() Failure: No exception was thrown
Failed ...IsSafeLeafPayload_RejectsATrailingNewline                                 Assert.False() Failure  Expected: False
Failed!  - Failed: 6, Passed: 0
```

### B. Live — running app
Captured with the Linux harness on port 4802. `\n` is JSON-escaped in the body.
1. `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"catalog","version":"572.16\n"}'`
2. `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"catalog","version":"５７２.１６"}'`
3. Control: `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"catalog","version":"572/16"}'`
4. With step 1's token: `curl -s -X POST localhost:4780/api/execute -H "Content-Type: application/json" -d '{"token":"<t>","action":"extract","selection":{"components":["display-driver"],"tweaks":{}}}'` (default outDir), then poll the job.

**Expected:** steps 1-2 return HTTP 400 `manifest rejected: version is not a valid driver version`, like step 3.
**Actual (captured):**
```
step 1 → token 10, version '572.16\n', sampleComponents True        (HTTP 200)
step 2 → token 11, version ５７２.１６, sampleComponents True          (HTTP 200)
step 3 → {"error":"manifest rejected: version is not a valid driver version"}   (HTTP 400, control)
step 4 → job done, outDir '…/output/extract-572.16\n'
          ls -b output/ → extract-572.16\n
```

## Root cause
Two .NET-specific regex semantics were missed:
1. `$` is not end-of-string. It also matches before a final `\n`. Whole-string allowlists need `\z`.
2. `\d` is Unicode-aware by default, meaning any `Nd` character. An ASCII allowlist needs `[0-9]`, or `RegexOptions.ECMAScript`.

The comment "dotted decimals only" and the tests (ASCII-only inputs) both assumed the ECMAScript/PCRE-ASCII reading.

## Remedy
Anchor with `\z` and spell the digit class out:
```csharp
private static readonly Regex VersionRe     = new(@"^[0-9]{1,5}(\.[0-9]{1,5}){1,3}\z", RegexOptions.Compiled);
private static readonly Regex ComponentIdRe = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z", RegexOptions.Compiled);
private static readonly Regex PayloadRe     = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z", RegexOptions.Compiled);
```
Why this is minimal: three pattern strings, no behavior change for any value the documented format allows. `ValidateVersion_AcceptsCurrentFormat` (572.16 / 566.36 / 610.74 / 999.10) and every existing reject row stay green.

Alternative: `RegexOptions.ECMAScript`. It fixes `\d`, but whether it also fixes `$` is less obvious to readers, and the explicit classes are self-documenting. Rejected.

Fences respected: validator only; no path, deletion or UI change; error constants unchanged and path-free.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** This is a GAP-S01 allowlist defect. Request a new ID per §7 (e.g. `SEC-10`, Low) with destination "GAP-S01 follow-up". It can share a validator-hardening slice with `manifest-null-collections-crash-validator` and `dependency-target-check-ignores-case`, with one TDD row each.
   > *Pinned 2026-11-23 as **GAP-R03** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** `git worktree add ../nvcleanclone-regexanchor -b feat/validation-regex-anchors origin/main`
2. **Red.** Add the §A tests and run `--filter "FullyQualifiedName~PackageValidationTests"`. The 6 new cases fail with "No exception was thrown". Commit `"SEC-10 (red): allowlists must be whole-string ASCII"`.
3. **Green.** Replace the three patterns and run the full suite. Commit `"SEC-10: anchor allowlists with \z and use [0-9] for version digits"`.
4. **Refactor.** None.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Steps 1-2 of §B now return HTTP 400 `manifest rejected: version is not a valid driver version`, and `572.16` still loads (HTTP 200).
7. **PR.** Template TDD rows. No existing test edited. Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-regexanchor origin/feat/validation-regex-anchors`; range `origin/main...HEAD`.
3. **Re-run gates**, then §B live.
4. **Finder pass.** Check:
   - Every `$` in `PackageValidation.cs` is now `\z`.
   - No other `\d` or `\w` remains in an allowlist.
   - `grep -n 'Regex(' nvcleanstall/Lib/*.cs` for other allowlists with the same flaw. For example, `Presets.SafeName` strips rather than anchors, so it is not affected; note any others found.
5. **Adversarial verify.** Try:
   - `"572.16\r\n"` and `"572.16\r"` (CR is already rejected; confirm).
   - `"572.16\n\n"`.
   - A U+2028 line separator.
   - Every `Nd` digit block (the harness can iterate `char.GetUnicodeCategory`).
   - A payload of exactly 128 chars plus `\n`.
   - `IsSafeLeafPayload` on each.
6. **Verdict document.** APPROVE / REQUEST CHANGES.
7. **Feedback to builder.** Fix loop C0 orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 `F<n>:` commits → C6 closing report → C7 STOP.
8. **Report to owner.** Verdict line; owner merges.

## Acceptance criteria
1. `ValidateVersion` rejects `"572.16\n"` and any version containing a non-ASCII digit, and still accepts `572.16`, `566.36`, `610.74` and `999.10`.
2. `IsSafeLeafPayload("display-driver.bin\n")` is false, and a component id with a trailing `\n` fails `ValidateManifest`.
3. `POST /api/package {"kind":"catalog","version":"572.16\n"}` returns HTTP 400 with `BadVersion`, and no `extract-572.16\n` directory can be created by any flow.
4. All existing `PackageValidationTests` pass unedited.
