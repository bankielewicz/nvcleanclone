# Install receipt and package config drop the sample-components flag, pairing a real installer with a component list that never came from it

| Field | Value |
|---|---|
| **Slug** | `receipt-drops-sample-components-flag` |
| **Severity** | Medium (material incorrectness of a persisted audit artifact; honesty-marker family) |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness); also confirmed live (headless `--mock-catalog`) |
| **Area** | `nvcleanstall/Lib/Jobs.cs` (`StartExecute`: install/silent receipt writer, package `config.json` writer) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | **Sibling doc `docs/bugs/customized-manifest-drops-sample-flag`** (Packages.cs, by the Packages-area hunter) covers the same lost flag in `WriteCustomized`'s `manifest.json` and its reload. This doc covers the two artifacts `Jobs.cs` writes: the install/silent receipt and the package `config.json`. They are complementary, not duplicates, and may share one register row. Distinct from **BUG-02** (GAP-C02). BUG-02 is about the *wrong file* being correlated by version. Here the file is *right*, but the receipt presents sample components as that file's contents. Same honesty-marker pattern as GAP-05's `signatureSimulated`/`driverTelemetrySimulated` (gaps register, closed), applied to the GAP-02 flag that GAP-04's enrichment made misleading. Not covered by GAP-C05, which is about tweak wording and `.reg` targets. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R06** — Artifact identity and honesty (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
A live driver version has no real package to parse (GAP-OUT-1). The session manifest is therefore the newest mock package relabeled to the live version and flagged `SampleComponents = true`, and the UI shows "Sample component list — real package parsing not yet implemented". `StartExecute` never carries that flag into the artifacts it persists. GAP-04 added real-artifact facts to the install/silent receipt, so the receipt now records the **real** downloaded installer (`driverFile`, `driverFileBytes`) next to `version: "610.74"` and a `components` list taken from a mock template. Nothing in the receipt says the components are a stand-in. The build-package `config.json` has the same gap.

## Evidence
- `nvcleanstall/Lib/Packages.cs:39`. The live session is a sample, flagged as such:
  ```csharp
  with { Version = version, SampleComponents = true };
  ```
- `nvcleanstall/Api.cs:53-55`. Every live-version session takes that path, and `Api.cs:104-106` then threads the real download in:
  ```csharp
  loaded = Packages.HasCatalogPackage(version)
      ? Packages.LoadCatalog(version)
      : Packages.LoadSampleTemplate(version);
  …
  var file = Jobs.DownloadedFile(src.manifest.Version);
  DownloadArtifact? artifact = file != null && File.Exists(file)
      ? new DownloadArtifact { FilePath = file, SizeBytes = new FileInfo(file).Length, Source = "live" }
  ```
- `nvcleanstall/Lib/Jobs.cs:442-464`. The receipt writer has no sample marker:
  ```csharp
  version = manifest.Version,
  …
  components = selected.Select(c => c.Id),
  …
  driverFile = liveArtifact ? artifact!.FilePath : null,
  driverFileBytes = liveArtifact ? artifact!.SizeBytes : (long?)null,
  }, Json.Web));
  ```
  `manifest.SampleComponents` is never read anywhere in `Jobs.cs`.
- `nvcleanstall/Lib/Jobs.cs:531-540`. The package `config.json` likewise records `version` and `components` with no sample marker.
- The flag is promised to the user in two places:
  - The UI label `nvcleanstall/wwwroot/index.html:110` / `app.js:184`.
  - `specs/nvcleanstall/parity.md:35` (FEAT-002): "The downloaded installer is never parsed/executed — the component list stays a **labeled** sample (GAP-OUT-1)".

  The label exists only on Screen 3 and is lost from everything written to disk.

The faulty reasoning: GAP-05 established that an artifact recording a simulated fact must carry a marker, so no output can be mistaken for the real thing. GAP-04 then joined real-artifact facts onto the same receipt, and no one revisited the GAP-02 flag that makes the component half of that receipt synthetic.

## Impact
- The persisted receipt states, in effect, "the real `610.74-whql.exe` (N bytes) was installed with components display-driver, physx, hd-audio". Those IDs come from the mock template (`data/packages/572.16`), not from the real installer, whose components are `.nvi` technical names (GAP-F03). `simulated: true` says the *install* was simulated. It does not say the *component list* is fabricated.
- The receipt and `config.json` outlive the UI and are the artifacts a user would share or audit. They overclaim in exactly the way GAP-05's honesty rule ("no output can be mistaken for …") forbids for the signature and telemetry facts.
- How likely: every install/silent/package run on a live version, which is the default live-catalog path. Mock-path runs are unaffected because `SampleComponents` is false there.

## How to reproduce
### A. Automated — failing xunit test
Add the install fact to `nvcleanstall/CleanDriver.Tests/ExecuteReceiptTests.cs`. That class owns the shared `output/receipt-<date>.json` path and is serialized for that reason; its existing `G()` and `LiveArtifact()` helpers are reused. Add the package fact to `nvcleanstall/CleanDriver.Tests/PackageHonestyTests.cs`, which writes to temp dirs and is hermetic.

```csharp
// ExecuteReceiptTests.cs
[Fact]
public async Task StartExecute_InstallFromSampleTemplateWithLiveArtifact_ReceiptMarksSampleComponents()
{
    var (manifest, source) = Packages.LoadSampleTemplate("610.74");
    Assert.True(manifest.SampleComponents); // precondition: the session knows it is a sample
    var required = manifest.Components.Where(c => c.Required).Select(c => c.Id).ToArray();

    var job = Jobs.StartExecute("install", manifest, source, Sel(required), null, G(), LiveArtifact(2048));
    await job.Completion!;
    Assert.Equal("done", job.Status);

    var r = Receipt(job);
    Assert.Equal(2048, r.GetProperty("driverFileBytes").GetInt64()); // the real artifact is recorded…
    Assert.True(r.TryGetProperty("sampleComponents", out var sample) && sample.ValueKind == JsonValueKind.True,
        "receipt records a real driverFile but omits that its components[] is a sample stand-in");
}
```

```csharp
// PackageHonestyTests.cs
[Fact]
public async Task StartExecute_PackageFromSampleTemplate_ConfigMarksSampleComponents()
{
    var (manifest, source) = Packages.LoadSampleTemplate("610.75");
    var all = manifest.Components.Select(c => c.Id).ToArray();
    var outDir = Path.Combine(TempDir.Create(), "build");

    var job = Jobs.StartExecute("package", manifest, source, Sel(all), outDir,
        new GpuInfo { Name = "GeForce RTX 5070", InstalledDriverVersion = "591.86" });
    await job.Completion!;
    Assert.Equal("done", job.Status);

    var cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(outDir, "config.json"))).RootElement;
    Assert.True(cfg.TryGetProperty("sampleComponents", out var sample) && sample.ValueKind == JsonValueKind.True,
        "config.json lists sample components with no sample marker");
}
```

Run (real repo):
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~StartExecute_InstallFromSampleTemplateWithLiveArtifact_ReceiptMarksSampleComponents|FullyQualifiedName~StartExecute_PackageFromSampleTemplate_ConfigMarksSampleComponents"
```

Captured RED on unmodified HEAD (Linux net10.0 harness, class `ReceiptDropsSampleComponentsFlagRepro`, same bodies):
```
Failed ...StartExecute_InstallFromSampleTemplateWithLiveArtifact_ReceiptMarksSampleComponents [2 s]
  Error Message:
   receipt records a real driverFile but omits that its components[] is a sample stand-in
Failed ...StartExecute_PackageFromSampleTemplate_ConfigMarksSampleComponents [2 s]
  Error Message:
   config.json lists sample components with no sample marker
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
```

### B. Live — running app (if applicable)
Against `dotnet run --project nvcleanstall -- --headless --mock-catalog` (port 4780). The transcript below was captured on the Linux mirror, port 4801. With `--mock-catalog` no real download happens, so `driverFile` is absent; the dropped flag is still visible. On the live catalog, after a real download, the same receipt also gains `driverFile`/`driverFileBytes`.
1. Open a live-version session; the sample template is served for any non-catalog version:
   `curl -s -X POST localhost:4780/api/package -H 'Content-Type: application/json' -d '{"kind":"catalog","version":"610.74"}'`
   Observed: `"sampleComponents": true`, components `display-driver, physx, hd-audio, usbc, gfe, …`.
2. Install from it:
   `curl -s -X POST localhost:4780/api/execute -H 'Content-Type: application/json' -d '{"token":"<t>","action":"install","selection":{"components":["display-driver","physx","hd-audio"],"tweaks":{}}}'`. Poll `/api/jobs/<id>` until `done`.
3. `cat output/receipt-<today>.json`. **Expected:** `"sampleComponents": true`. **Actual:**
   ```json
   { "action": "install", "simulated": true, "version": "610.74", "gpu": "GeForce RTX 4070",
     "components": ["display-driver","physx","hd-audio"], "tweaks": {}, "signature": "rebuilt",
     "signatureSimulated": true, "unattended": false, "autoReboot": false, "cleanInstall": false }
   ```
   `grep -c sampleComponents` → `0`.
4. Same session, `"action":"package","outputPath":"<dir>"`. The resulting `<dir>/config.json` is `{"builtBy":"CleanDriver",…,"version":"610.74","components":["display-driver","physx","hd-audio"],"tweaks":{}}`. It has no marker, and `<dir>/manifest.json` has none either.

## Root cause
The receipt and config writers in `StartExecute` serialize `manifest.Version` and the selected component IDs. They do not serialize the one manifest property that qualifies both, `Manifest.SampleComponents`. GAP-04 made the omission material by adding real-artifact facts to the same object, which pairs a real file with synthetic contents.

## Remedy
Add the flag to both `StartExecute` writers, additive and null-omitted exactly like the GAP-05 markers, so that every mock-path artifact stays byte-identical:

```diff
                     driverFileBytes = liveArtifact ? artifact!.SizeBytes : (long?)null,
+                    // GAP-02 honesty: the component list is a relabeled sample stand-in for a
+                    // live version (GAP-OUT-1). Null-omitted, so mock receipts stay byte-identical.
+                    sampleComponents = manifest.SampleComponents ? (bool?)true : null,
                 }, Json.Web));
```
```diff
                         driverTelemetrySimulated = selection.TweakOn("driver-telemetry") ? (bool?)true : null,
+                        sampleComponents = manifest.SampleComponents ? (bool?)true : null,
                     }, Json.Web));
```
- **Sibling, out of this file:** `Packages.WriteCustomized`'s `manifest.json` (`Packages.cs:135-149`) needs the same one-liner. It is documented, with its reload consequence, in `docs/bugs/customized-manifest-drops-sample-flag`. It shares the slice because it is the same flag on the third artifact GAP-05 named, but the Packages owner should review that line.
- **Why minimal:** two additive fields and no shape change. `sampleComponents` is omitted whenever false, which keeps the GAP-04/GAP-05 byte-identity pins green.
- **Validated:** on a scratch copy of `Jobs.cs`, both reds turn green and the full suite stays green (163/163, Linux harness).
- **Alternatives rejected:**
  - Dropping `components` from live receipts loses the user's actual selection.
  - Renaming `components` to `sampleComponents` breaks the back-compat pin on the receipt shape.
  - A prose note in the log only does not survive into the artifact.
- **Fences:** safety untouched. Honesty markers are strengthened, never weakened. No UI change (the Screen 3 label already exists). Back-compat pins that must stay green unedited:
  - `ExecuteReceiptTests.StartExecute_NoArtifact_OmitsDriverFields`
  - `StartExecute_MockArtifact_OmitsDriverFields_KeepsFlagFields`
  - `StartExecute_StockInstall_OmitsSignatureSimulated`
  - `PackageHonestyTests.*`
  - `NoExecutionGuardTests.LiveSession_ExecuteInputIsSampleTemplate_NeverTheDownloadedExe`

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** The active register is `docs/security_register.md`, and no item covers this. The owner pins a new finding ID first. Destination: **fold into GAP-C02** ("Correct artifact identity, receipts …"). GAP-C02 already moves the `DownloadArtifact` onto the catalog session that consumed it and renames receipts, so it is the slice that touches these exact writers, and the marker is part of making the receipt's artifact identity truthful. Per §7 it needs its own ID and AC row.
   > *Pinned 2026-11-23 as **GAP-R06** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** `git worktree add ../nvcleanclone-sample-flag -b feat/receipt-sample-components-marker origin/main` (or the GAP-C02 branch, if folded).
2. **Red.** Add the two facts from §A. Run the filter and confirm both fail on the missing `sampleComponents` assertion, not on a compile or setup error. Commit `"<ID> (red): live-session receipt/config mark sample components"`.
3. **Green.** Add the two `Jobs.cs` fields, plus the `Packages.cs` manifest sibling if the owner scopes it in, with its own red first. Run the full suite. Commit `"<ID>: carry SampleComponents into receipt and config.json"`.
4. **Refactor.** None expected. If the marker literals are deduplicated (hardening candidate #11), keep the suite green.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Repeat §B steps 1–4 and show `"sampleComponents": true` in the receipt and `config.json`. Then load the mock catalog version (`"version":"572.16"`), install, and show that the receipt has **no** `sampleComponents` key (byte-identity).
7. **PR.** Fill `.github/pull_request_template.md`. TDD rows: receipt marker, config marker, and manifest marker if in scope. No existing test is edited. Deviations: none, or "manifest.json sibling included". Paste both live transcripts, push, and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green before reading the diff.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-sample-flag origin/feat/receipt-sample-components-marker`. Review range `origin/main...HEAD`.
3. **Re-run gates** on the reviewer's checkout.
4. **Finder pass.** Check each of these:
   - The marker is `(bool?)true : null`, so it is omitted on the mock path. A plain `bool` would break byte-identity.
   - Both writers are covered: the install/silent receipt and the package `config.json`. Extract writes no config, so check the manifest sibling if it is in scope.
   - `signature`, `signatureSimulated` and `driverTelemetrySimulated` are untouched.
   - The field name is consistent with the manifest's camelCase JSON (`sampleComponents`, via `Json.Web`).
   - No existing pin was edited.
5. **Adversarial verify.** Throw these at the fix:
   - Mock catalog session plus install gives no key.
   - Local folder/zip package whose `manifest.json` itself contains `"sampleComponents": true`. `ReadManifest` deserializes it, so decide and pin whether a local manifest may self-declare. The marker should then still be truthful.
   - Live session without a completed download (no `driverFile`) still marks the sample.
   - `silent` action, not just `install`.
   - The package zip's `config.json` entry carries the marker too, because the archive packs the same file.
6. **Verdict document.** APPROVE or REQUEST CHANGES, each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** On REQUEST CHANGES, write an implementer handoff. The fix loop runs: C0 Orient → C1 reproduce the red → C2 fix → C3 verify the ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relay. The owner merges.

## Acceptance criteria
1. An install or silent run from a `SampleComponents == true` session writes a receipt containing `"sampleComponents": true`, with or without a live `driverFile`.
2. A package run from such a session writes `config.json` containing `"sampleComponents": true`, and the archived `config.json` matches.
3. Runs from a `SampleComponents == false` session (mock catalog, local package) produce receipts and configs with **no** `sampleComponents` key. All existing receipt and honesty pins pass unedited.
4. (If the Packages sibling is in scope) `WriteCustomized`'s `manifest.json` carries the same null-omitted marker.
