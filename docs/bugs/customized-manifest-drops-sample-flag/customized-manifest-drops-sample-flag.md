# Customized manifest drops the `sampleComponents` honesty flag, so a sample build passes as the real driver package

| Field | Value |
|---|---|
| **Slug** | `customized-manifest-drops-sample-flag` |
| **Severity** | Medium. The output misstates what it is: a mock component list is labeled as a specific real driver version. The UI then repeats that misstatement on reload, contradicting GAP-02's labeling AC. This is the same class as BUG-07, the untruthful-claims finding. |
| **Confidence** | Confirmed: the repro test fails on HEAD (Linux harness), and it was reproduced live. |
| **Area** | `nvcleanstall/Lib/Packages.cs` (`WriteCustomized` manifest rewrite) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | GAP-02 (`docs/gaps_analysis.md`, closed register): "the UI labels components 'sample component list — real package parsing not yet implemented.'" GAP-05's honesty-marker pattern (`signatureSimulated`, `driverTelemetrySimulated`) is additive and null-omitted. Not in `docs/security_register.md` §3. GAP-F03/F04 will eventually replace the sample, but until then the label must survive. **Sibling doc:** `docs/bugs/receipt-drops-sample-components-flag` covers the same lost flag in the artifacts `Jobs.cs` writes (install receipt and package `config.json`). This doc covers only the `Packages.cs` manifest rewrite and its reload. They are complementary, not duplicates, and may share one register row. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R06** — Artifact identity and honesty (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
For a live-downloaded driver version (no bundled package), `LoadSampleTemplate` serves the newest **mock** package. It relabels it to the requested version and sets `SampleComponents = true`, and the UI shows "sample component list" because of that flag.

`WriteCustomized` rebuilds the output `manifest.json` from `version`, `channel`, `components` and so on, but it never copies `SampleComponents`. The customized directory and the redistributable `<version>-cleandriver-package.zip` therefore claim to *be* driver 610.74 with that component list.

If the user loads that output back (CleanDriver's own output is a valid local package), the manifest deserializes with `SampleComponents = false`. The component screen then shows the mock list as the real 610.74 package, with no sample note.

## Evidence
`Packages.cs:29-41`. The flag is set on the in-memory manifest:
```csharp
var manifest = ReadManifest(File.ReadAllText(Path.Combine(dir, "manifest.json")))
    with { Version = version, SampleComponents = true };                         // :38-39
```
`Packages.cs:135-151`. The rewrite copies some fields and silently drops this one:
```csharp
var outManifest = new
{
    version = manifest.Version,          // "610.74" — the live version label survives
    channel = manifest.Channel,
    customizedBy = "CleanDriver",
    ...
    signatureSimulated = modified ? (bool?)true : null,
    driverTelemetrySimulated = TweakTrue(tweaks, "driver-telemetry") ? (bool?)true : null,
    components = selected,               // the MOCK template's components
    tweaks,
};                                       // no sampleComponents
```
`wwwroot/app.js:184`. The UI label depends entirely on that field:
```js
$('comp-sample-note').classList.toggle('hidden', !state.manifest.sampleComponents);
```

## Impact
- **The redistributable lies about its contents.** Its `manifest.json` reads `"version": "610.74"` plus a component list that is really the 572.16 mock template's. No marker says it's a stand-in. Anyone reading the archive, or re-importing it into CleanDriver, takes it as the 610.74 package.
- **The round trip loses GAP-02's label.** Re-loading the build as a local package gives `version 610.74, sampleComponents false`, and the "sample component list" note is hidden. That's exactly the "mistaken for the real package" outcome the flag exists to prevent (`app.js:182-183` comment).
- The flow is ordinary: download a live driver, extract or package it, then load the result.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/PackageHonestyTests.cs`. It's the same as the harness `repro/customized-manifest-drops-sample-flag.cs`.
```csharp
// GAP-02 honesty: a sample-template build must keep saying it is a sample, in its own
// manifest and after a round trip through LoadLocal (additive, null-omitted like GAP-05).
[Fact]
public void WriteCustomized_SampleTemplate_OutputManifestKeepsTheSampleMarker()
{
    var (manifest, source) = Packages.LoadSampleTemplate("610.74");
    Assert.True(manifest.SampleComponents);                       // precondition (GAP-02 pin)

    var outDir = Path.Combine(TempDir.Create(), "extract-610.74");
    var (_, _, manifestPath) = Packages.WriteCustomized(source, manifest,
        new List<string> { "display-driver", "physx" }, outDir);

    var written = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
    Assert.Equal("610.74", written.GetProperty("version").GetString());
    Assert.True(written.TryGetProperty("sampleComponents", out var flag) && flag.GetBoolean(),
        "the rewritten manifest claims version 610.74 but no longer says its components are a sample");
}

[Fact]
public void ReloadedSampleBuild_IsStillLabeledSample()
{
    var (manifest, source) = Packages.LoadSampleTemplate("610.74");
    var outDir = Path.Combine(TempDir.Create(), "extract-610.74");
    Packages.WriteCustomized(source, manifest, new List<string> { "display-driver" }, outDir);

    var (reloaded, _) = Packages.LoadLocal(outDir);
    Assert.Equal("610.74", reloaded.Version);
    Assert.True(reloaded.SampleComponents,
        "re-loading the build presents the mock component list as the real 610.74 package");
}
```
Run:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~PackageHonestyTests"
```
RED output, trimmed, from the Linux `net10.0` harness at `54b3c31` (class `CustomizedManifestDropsSampleFlagRepro`):
```
Failed ...WriteCustomized_SampleTemplate_OutputManifestKeepsTheSampleMarker
  the rewritten manifest claims version 610.74 but no longer says its components are a sample
Failed ...ReloadedSampleBuild_IsStillLabeledSample
  re-loading the build presents the mock component list as the real 610.74 package
Failed!  - Failed: 2, Passed: 0
```

### B. Live — running app
This reproduces in mock mode too: any version without a bundled package routes to `LoadSampleTemplate`. Captured with the Linux harness on port 4802.
1. `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"catalog","version":"610.74"}'` returns `version 610.74, sampleComponents True`.
2. `curl -s -X POST localhost:4780/api/execute -H "Content-Type: application/json" -d '{"token":"<t>","action":"package","selection":{"components":["display-driver"],"tweaks":{}},"outputPath":"C:/tmp/l5/package-610.74"}'`, then poll until `done`.
3. Inspect `C:/tmp/l5/610.74-cleandriver-package.zip` → `manifest.json`.
4. `curl -s -X POST localhost:4780/api/package -H "Content-Type: application/json" -d '{"kind":"local","path":"C:/tmp/l5/package-610.74"}'`, and the same for the `.zip`.

**Expected:** the archived and reloaded manifests still carry `sampleComponents: true`.
**Actual (captured):**
```
load 610.74 -> version 610.74 sampleComponents True
job done
archived manifest keys: ['channel', 'components', 'customizedAt', 'customizedBy', 'signature', 'signatureSimulated', 'tweaks', 'version'] version 610.74
reload output -> version 610.74 sampleComponents False
reload zip    -> version 610.74 sampleComponents False
```
In the browser, loading that folder through "Use a driver file on disk" shows the component screen with the "sample component list" note hidden.

## Root cause
`WriteCustomized` builds the output manifest as an explicit allowlist of fields, an anonymous object. When GAP-02 added `Manifest.SampleComponents` (`Models.cs:24-27`, "Additive (GAP-02)"), nobody added it to that allowlist. The relabeled `version` therefore survives into persistent output, but the flag that qualifies the relabel does not.

## Remedy
Add the flag to the rewrite in the GAP-05 additive, null-omitted style:
```csharp
// Packages.cs WriteCustomized, in outManifest
sampleComponents = manifest.SampleComponents ? (bool?)true : null,   // GAP-02 honesty: null-omitted, mock builds unchanged
```
Why this is minimal: one line. Mock/stock manifests stay byte-identical, because `WhenWritingNull` omits the field. The field name matches `Manifest.SampleComponents` under `Json.Web` camelCase, so `ReadManifest` restores it on reload with no reader change.

Out of scope here: the package `config.json` and the install receipt in `Jobs.cs` lose the same marker. That is tracked in the sibling doc `receipt-drops-sample-components-flag`. The two fixes can land in one honesty slice if the owner pins them together.

Fences respected:
- Honesty markers are kept honest, and no existing marker changes.
- `PackageHonestyTests.WriteCustomized_Stock_OmitsSignatureSimulated` and its siblings are unaffected, because they read catalog manifests where the flag is false and therefore omitted.
- `DownloadRoutingTests` (the `SampleComponents` pin) and `NoExecutionGuardTests.LiveSession_ExecuteInputIsSampleTemplate_NeverTheDownloadedExe` are unaffected.
- No UI change: app.js already honors the field.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** New finding: request an ID in `docs/security_register.md` §3 (e.g. `BUG-13`, Medium). The natural destination is GAP-C05 ("make tweak claims … truthful", the honesty wave). Do not fold it into an unrelated PR.
   > *Pinned 2026-11-23 as **GAP-R06** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** `git worktree add ../nvcleanclone-sampleflag -b feat/keep-sample-flag-in-output origin/main`
2. **Red.** Add both §A tests to `PackageHonestyTests.cs`. Run `--filter "FullyQualifiedName~PackageHonestyTests"` and confirm the two new tests fail as shown. Commit `"BUG-13 (red): sample-template builds must keep sampleComponents"`.
3. **Green.** Add the one-line field and run the full suite. Commit `"BUG-13: carry sampleComponents into the customized manifest (null-omitted)"`.
4. **Refactor.** None expected.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run §B steps 1-4. The archived manifest has `"sampleComponents": true`, and both reloads report `sampleComponents True`. A catalog 572.16 build's manifest has no `sampleComponents` key, so it stays byte-identical. Browser: the reloaded folder shows the sample note. That is an A6-style transcript.
7. **PR.** Template TDD rows. No existing test edited. Live transcript. Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` green.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-sampleflag origin/feat/keep-sample-flag-in-output`; range `origin/main...HEAD`.
3. **Re-run gates**, then §B live.
4. **Finder pass.** Check:
   - The field is null-omitted for non-sample builds (a byte-identity check against `main` for a 572.16 build, ignoring `customizedAt`).
   - The camelCase name round-trips through `ReadManifest`.
   - No other `Manifest` field is still dropped (`channel`, `version`, components all present).
   - `CleanPreviousBuild` ignores the new key. It only reads `customizedBy`/`components`/`tweaks`.
5. **Adversarial verify.** Try:
   - Hand-editing the output to `"sampleComponents": false` or `"yes"`. The second should be a 400 via `JsonException`, so check it.
   - Re-customizing a reloaded sample build, where the flag must persist through a second `WriteCustomized`.
   - Sample builds via the `install`/`silent` receipt, which has no manifest rewrite. Record PLAUSIBLE if the receipt should carry it too.
6. **Verdict document.** APPROVE / REQUEST CHANGES.
7. **Feedback to builder.** Fix loop C0 orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 `F<n>:` commits → C6 closing report → C7 STOP.
8. **Report to owner.** Verdict line; owner merges.

## Acceptance criteria
1. `WriteCustomized` on a `LoadSampleTemplate` manifest writes `"sampleComponents": true` into `manifest.json`, both in `outDir` and inside the package archive.
2. `Packages.LoadLocal` of that output (folder and zip) yields `SampleComponents == true`, and the UI shows the "sample component list" note.
3. A build from a bundled catalog package writes no `sampleComponents` key, so its manifest is byte-identical to today apart from `customizedAt`.
4. Existing `PackageHonestyTests`, `DownloadRoutingTests` and `NoExecutionGuardTests` stay green and unedited.
