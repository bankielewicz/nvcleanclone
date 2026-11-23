# Bug reports — index

| Field | Value |
|---|---|
| **Baseline** | `main` @ `54b3c31` (after PR #23, GAP-S01) |
| **Date** | 2026-11-23 |
| **Status** | **Findings only — nothing here is scheduled.** The active register is `docs/security_register.md`; each report says which GAP it folds into or that it needs a new owner-pinned entry. A report is not a register entry, and this index is not implementation. |
| **Scope** | Code-level bug hunt across `Lib/Jobs.cs`, `Lib/Packages.cs` + `Lib/PackageValidation.cs` + `Lib/Presets.cs`, `Api.cs` + `Program.cs` + `ShellForm.cs` + `Lib/Models.cs`, the catalog/GPU/tweak sources, and `wwwroot/`, plus an end-to-end functional UI pass ([`ui-functional-test-report.md`](ui-functional-test-report.md)). Already-registered findings (SEC-01…07, BUG-01…09) were excluded on purpose. |
| **Process** | Docs-only; exemption **A2** applies to any PR carrying these files. |

Each report lives at `docs/bugs/<slug>/<slug>.md` and has the same sections:
Summary → Evidence (`file:line` at `54b3c31`) → Impact → **How to reproduce** (A: failing xunit test,
or a Playwright transcript for `wwwroot/` per amendment A6; B: live steps against
`--headless --mock-catalog`) → Root cause → **Remedy** → **TDD fix workflow** (register gate, worktree,
red → green → refactor, gates, live exercise, PR template) → **Code-review workflow** (the eight
stages of `docs/ai-review-workflow.html` and the C0–C7 fix loop of `docs/ai-fix-loop-workflow.html`,
tailored per fix) → Acceptance criteria.

## Summary

| Severity | Code-level hunt | Functional UI pass (new) | Total |
|---|---|---|---|
| High | 1 | 0 | 1 |
| Medium | 15 | 1 | 16 |
| Low | 16 | 6 | 22 |
| **Total** | **32** | **7** | **39** |

38 are **Confirmed** (the repro fails on unmodified HEAD). One is **Plausible**:
`shell-url-from-raw-cleandriver-urls` is a WinForms path that cannot run on Linux, though both of
its halves are reproduced.

The functional UI pass ran **81 browser tests: 57 PASS / 23 FAIL / 0 BLOCKED / 1 N-A**. Its 23
failures map onto 7 new reports, 5 reports from the code-level hunt, and the registered BUG-06. See
[Functional UI pass](#functional-ui-pass). **AC-005 fails as written.** That contradicts
`specs/nvcleanstall/parity.md`, which records it as passing (it only passes if the preset is
loaded after Components is reached).

## Findings

### Package I/O and validation — `Lib/Packages.cs`, `Lib/PackageValidation.cs`

| Slug | Sev. | Conf. | Root cause | Register relation |
|---|---|---|---|---|
| [`build-into-source-folder-destroys-package`](build-into-source-folder-destroys-package/build-into-source-folder-destroys-package.md) | **High** | Confirmed | `CopyPayload` truncates the destination (`File.Create`) before opening the source. When `outputPath` is the package's own folder, the payload goes to 0 bytes. This hits user folders and the bundled sample. HARD-01's clean also deletes the input when re-customizing in place. | **Regression from PR #23 / GAP-S01** (the same test passes on `a8c4f70`) |
| [`clean-previous-build-follows-reparse-point`](clean-previous-build-follows-reparse-point/clean-previous-build-follows-reparse-point.md) | Medium | Confirmed | `CleanPreviousBuild` deletes through a junction/symlink at `outDir/payload` into files outside `outDir`, before the write side rejects the link. | GAP-S01 confines reads and writes but not the HARD-01 delete (deletion ruling D12/D1) |
| [`folder-manifest-read-before-size-cap`](folder-manifest-read-before-size-cap/folder-manifest-read-before-size-cap.md) | Medium | Confirmed | The 1 MiB manifest cap is applied only after `File.ReadAllText`, on the folder path and in the clean. Live: a 1.5 GiB manifest gave an OOM and HTTP 500. | Gap in GAP-S01 (SEC-04) |
| [`clean-deletes-foreign-config-after-extract`](clean-deletes-foreign-config-after-extract/clean-deletes-foreign-config-after-extract.md) | Medium | Confirmed | Any CleanDriver-stamped manifest makes the clean delete `install.cmd`/`config.json`, which only `package` writes, so a user's own files next to an `extract` are lost. | Needs an **owner amendment of pin D1** |
| [`sibling-zip-shared-across-outdirs`](sibling-zip-shared-across-outdirs/sibling-zip-shared-across-outdirs.md) | Medium | Confirmed | The archive name depends only on the version, so two build folders under one parent share one zip, and the second build silently replaces the first's. | Needs an **owner ruling** (GAP-06 Resolution 2 fixed the name) |
| [`customized-manifest-drops-sample-flag`](customized-manifest-drops-sample-flag/customized-manifest-drops-sample-flag.md) | Medium | Confirmed | The rewritten `manifest.json` drops `sampleComponents`, so a sample build passes as the real driver package on reload. | Honesty-marker family (BUG-07 class). Pairs with `receipt-drops-sample-components-flag` |
| [`manifest-null-collections-crash-validator`](manifest-null-collections-crash-validator/manifest-null-collections-crash-validator.md) | Low | Confirmed | `components:null`, `[null]` or `dependsOn:null` throws `NullReferenceException`, giving a bodiless 500 instead of a 400. | GAP-S01 AC-2 not met for these shapes |
| [`validation-regex-allows-newline-and-unicode`](validation-regex-allows-newline-and-unicode/validation-regex-allows-newline-and-unicode.md) | Low | Confirmed | The allowlists end in `$` (which also matches before a trailing `\n`) and use `\d` (which matches Unicode digits). No traversal is possible. | GAP-S01 allowlists wider than documented |
| [`dependency-target-check-ignores-case`](dependency-target-check-ignores-case/dependency-target-check-ignores-case.md) | Low | Confirmed | Dependency targets are checked case-insensitively but consumed exactly. A dangling edge returns 200, then Screen 3 throws a `TypeError`. | Server half; the client half is `component-dependency-closure-incomplete` |

### Job / download engine — `Lib/Jobs.cs`

| Slug | Sev. | Conf. | Root cause | Register relation |
|---|---|---|---|---|
| [`download-headers-phase-has-no-stall-timeout`](download-headers-phase-has-no-stall-timeout/download-headers-phase-has-no-stall-timeout.md) | Medium | Confirmed | The stall timeout covers body reads only. A peer (or redirect hop) that never sends headers leaves the job "running" at 0% forever, and retries return the same wedged job. | New (HARD-05 hop handling is otherwise intact) |
| [`receipt-drops-sample-components-flag`](receipt-drops-sample-components-flag/receipt-drops-sample-components-flag.md) | Medium | Confirmed | The receipt and `config.json` pair the real downloaded installer with the mock template's component list, with no "sample" marker. | Pairs with `customized-manifest-drops-sample-flag` |
| [`execute-prelude-throw-orphans-running-job`](execute-prelude-throw-orphans-running-job/execute-prelude-throw-orphans-running-job.md) | Low | Confirmed | The job is stored before code that can throw (NUL in path, null lists), so the request gets a 500 and the job never ends. | `Jobs.cs` half of `execute-malformed-body-500`; suggest one register row, two ACs |
| [`shutdown-mid-download-leaves-part-file`](shutdown-mid-download-leaves-part-file/shutdown-mid-download-leaves-part-file.md) | Low | Confirmed | Download workers are not tied to the host lifetime, so closing the app mid-download leaves `.part` on disk. | New. Note: `NoRebootGuardTests` forbids the word "shutdown" in `Jobs.cs`, so the fix must avoid it |
| [`receipt-name-date-uses-culture-calendar`](receipt-name-date-uses-culture-calendar/receipt-name-date-uses-culture-calendar.md) | Low | Confirmed | The receipt file name uses the user's culture calendar (`receipt-2569-11-23.json` under th-TH) while `installedAt` says 2026. | Distinct from BUG-03 (same-day overwrite) |

### HTTP API and host — `Api.cs`, `Program.cs`, `ShellForm.cs`, `Lib/Models.cs`

| Slug | Sev. | Conf. | Root cause | Register relation |
|---|---|---|---|---|
| [`host-ambient-config-overrides-bind`](host-ambient-config-overrides-bind/host-ambient-config-overrides-bind.md) | Medium | Confirmed | `appsettings.json` beside the exe (live-reloaded) or `Kestrel__*` env vars override the loopback bind, and `ASPNETCORE_ENVIRONMENT=Development` serves stack traces. | Bypasses GAP-S02's planned `CLEANDRIVER_URLS` check |
| [`open-folder-explorer-search-path`](open-folder-explorer-search-path/open-folder-explorer-search-path.md) | Medium | Confirmed (mechanism); Windows search order reasoned | Explorer is started by bare name, so an `explorer.exe` next to CleanDriver.exe (typically Downloads) runs instead. | Same class as SEC-06, but GAP-C04 only fixes powershell |
| [`execute-malformed-body-500`](execute-malformed-body-500/execute-malformed-body-500.md) | Low | Confirmed | A missing token makes `TryGetValue(null)` throw; null lists or a bad `outputPath` give a bodiless 500 and a zombie job. | Route half of `execute-prelude-throw-orphans-running-job`; GAP-S02 eviction would skip these jobs |
| [`preset-load-invalid-name-500`](preset-load-invalid-name-500/preset-load-invalid-name-500.md) | Low | Confirmed | `GET /api/presets/{name}` doesn't catch `InvalidDataException`, so names like `!`, `...` or non-ASCII return 500, not 404. | Distinct from BUG-06 |
| [`preset-save-accepts-null-selection`](preset-save-accepts-null-selection/preset-save-accepts-null-selection.md) | Low | Confirmed | `POST /api/presets` without `selection` returns 200 and persists it; the wizard's Load handler then crashes. | GAP-C03-adjacent |
| [`download-reresolve-unknown-version`](download-reresolve-unknown-version/download-reresolve-unknown-version.md) | Low | Confirmed | `/api/download` re-queries the provider, so one transient live-lookup failure turns a version the UI listed into 404 "unknown version". | Widens the window in `screen1-next-no-inflight-guard-wrong-version` |
| [`shell-url-from-raw-cleandriver-urls`](shell-url-from-raw-cleandriver-urls/shell-url-from-raw-cleandriver-urls.md) | Low | **Plausible** (both halves reproduced) | The shell URL is built from raw `CLEANDRIVER_URLS`: a `;` list crashes after startup, and port `0` opens a dead page. Windows-only. | GAP-S02's loopback check accepts both values |

### Catalog, GPU detection, tweaks — `Lib/NvidiaCatalogProvider.cs`, `Lib/Gpu.cs`

| Slug | Sev. | Conf. | Root cause | Register relation |
|---|---|---|---|---|
| [`studio-only-release-listed-as-game-ready`](studio-only-release-listed-as-game-ready/studio-only-release-listed-as-game-ready.md) | Medium | Confirmed | The Game Ready/Studio collapse only prefers within a version group. A Studio-only release survives as `WHQL` "Latest" and becomes the default download, saved as `<ver>-whql.exe`. | Distinct from GAP-F01's recorded same-version collapse |
| [`marketing-version-unpadded-wmi-field`](marketing-version-unpadded-wmi-field/marketing-version-unpadded-wmi-field.md) | Medium | Confirmed | The "last 5 digits" rule misreads WMI versions whose 4th field is below 1000 (`9.18.13.697` gives `136.97`, not `306.97`). | New |
| [`gpu-detect-concurrent-first-call-race`](gpu-detect-concurrent-first-call-race/gpu-detect-concurrent-first-call-race.md) | Low | Confirmed | The check-then-set cache is hit concurrently by the wizard's boot `Promise.all`, so every launch runs two WMI queries. | New (distinct from SEC-06) |
| [`live-size-parse-ignores-unit`](live-size-parse-ignores-unit/live-size-parse-ignores-unit.md) | Low | Confirmed (parser); trigger input plausible | `ParseSizeMB` never reads the unit, so `"1.02 GB"` becomes 1 MB. | New |

The tweak `.reg` emitters and the conflict graph were checked and found correct, apart from what
BUG-07 already records.

### Wizard frontend — `wwwroot/app.js` (evidence form: amendment A6 browser transcripts)

| Slug | Sev. | Conf. | Root cause | Register relation |
|---|---|---|---|---|
| [`pollrun-failed-job-dead-ends-run-screen`](pollrun-failed-job-dead-ends-run-screen/pollrun-failed-job-dead-ends-run-screen.md) | Medium | Confirmed | `pollRun` has no `failed` branch. With R4 hiding the footer, a failed run has no banner, no Finish and no Back; only a page reload escapes. | Land before `job-pollers-no-exit-on-404-or-network-error` |
| [`preset-on-screen1-freezes-download-at-100`](preset-on-screen1-freezes-download-at-100/preset-on-screen1-freezes-download-at-100.md) | Medium | Confirmed (4/4 variants) | A preset loaded before the first package makes `adoptManifest` apply it before `hiComp` is set, so a `TypeError` freezes Screen 2 at 100%. Breaks FR-015/AC-005. | Land before `preset-components-lost-after-back-to-source` |
| [`preset-components-lost-after-back-to-source`](preset-components-lost-after-back-to-source/preset-components-lost-after-back-to-source.md) | Medium | Confirmed | After Back, a preset is applied to the stale package and adoption resets components to defaults. Tweaks apply but components are silently lost. | Depends on the previous fix |
| [`component-dependency-closure-incomplete`](component-dependency-closure-incomplete/component-dependency-closure-incomplete.md) | Medium | Confirmed | Dependencies are resolved one level deep, so the required Display Driver can be deselected and Extract writes a package without it. | Client half of BUG-01 (distinct from it) |
| [`auto-reboot-submitted-with-unattended-off`](auto-reboot-submitted-with-unattended-off/auto-reboot-submitted-with-unattended-off.md) | Low | Confirmed | The greyed "Allow automatic reboot" is still submitted after its parent is unticked; the receipt says `unattended=false autoReboot=true`. | Becomes Medium once GAP-C01 rejects the orphan server-side |
| [`job-pollers-no-exit-on-404-or-network-error`](job-pollers-no-exit-on-404-or-network-error/job-pollers-no-exit-on-404-or-network-error.md) | Low | Confirmed (4/4 cases) | A transport error freezes Screen 2 or the run view; a 404 makes `pollDownload` spin and `pollRun` throw. | Builds on `pollrun-failed-job-dead-ends-run-screen` |
| [`screen1-next-no-inflight-guard-wrong-version`](screen1-next-no-inflight-guard-wrong-version/screen1-next-no-inflight-guard-wrong-version.md) | Low | Confirmed (injected latency) | A second Next during the live lookup starts a second download, adopts the wrong version's package, and orphans the other job. | Widened by `download-reresolve-unknown-version` |

## Fix ordering and pairings

- **Fix first:** `build-into-source-folder-destroys-package`. It is a High regression on `main` that destroys user input and the bundled sample.
- **GAP-S01 follow-ups (one reopen, or a batch of `F<n>` follow-ups):** `build-into-source-folder-destroys-package`, `clean-previous-build-follows-reparse-point`, `folder-manifest-read-before-size-cap`, `manifest-null-collections-crash-validator`, `validation-regex-allows-newline-and-unicode`, `dependency-target-check-ignores-case`.
- **One row, two ACs:** `execute-malformed-body-500` (route) + `execute-prelude-throw-orphans-running-job` (engine).
- **Honesty-marker pair:** `customized-manifest-drops-sample-flag` (`manifest.json`) + `receipt-drops-sample-components-flag` (receipt, `config.json`).
- **Order-dependent frontend fixes:** `preset-on-screen1-freezes-download-at-100` → `preset-components-lost-after-back-to-source`; `pollrun-failed-job-dead-ends-run-screen` → `job-pollers-no-exit-on-404-or-network-error`.
- **Owner rulings required before any build:** `clean-deletes-foreign-config-after-extract` (amends pin D1), `sibling-zip-shared-across-outdirs` (amends the GAP-06 archive name), `run-log-lines-have-no-timestamps` (code or spec wording), `component-order-differs-from-mockup` (data or mockup).
- **Spec record to correct:** `specs/nvcleanstall/parity.md` records AC-005 as passing; it fails as written until `preset-on-screen1-freezes-download-at-100` lands.
- **In-flight guards (one pattern, two handlers):** `screen1-next-no-inflight-guard-wrong-version` (Screen 1 Next) and `double-click-start-runs-two-jobs` (Screen 5 Start).
- **Silent client failures:** `api-helper-silent-on-non-json-errors` is the client half of every bodiless-500 report (`execute-malformed-body-500`, `preset-load-invalid-name-500`, BUG-06).
- **Server/client halves:** `dependency-target-check-ignores-case` ↔ `component-dependency-closure-incomplete`; `preset-save-accepts-null-selection` (server) ↔ its Load-handler crash; `auto-reboot-submitted-with-unattended-off` ↔ GAP-C01's server-side orphan check.

## Functional UI pass

Full results: [`ui-functional-test-report.md`](ui-functional-test-report.md). It has 81 rows, each
traced to an FR, AC, ruling R1–R10, parity row or the mockups, plus the suite source and re-run
steps for port 4780. The pass drove the real `Lib/`, `Api.cs` and `wwwroot/` through
Playwright/Chromium against `--mock-catalog`.

- **Passing:** AC-001…AC-004; rulings R2–R10 (R1 is N-A, doc-level only); all four actions write the artifacts FR-011…FR-016 require, including well-formed `.reg` snippets that are never applied; local folder/zip loading with all invalid-input errors; Screen 2 cancel/retry; Recommended; dependency notices; the `566.36` component switch.
- **Failing (23 rows):**

| Report | Sev. | Failing test ids | Root cause | Note |
|---|---|---|---|---|
| [`keyboard-cannot-reach-wizard-controls`](keyboard-cannot-reach-wizard-controls/keyboard-cannot-reach-wizard-controls.md) | Medium | N-07 | The Screen 1 source options, version combo, expert switch, action cards and preset rows are not focusable; toggling a checkbox drops focus to `<body>`. A keyboard-only user can only Install the latest version. | New |
| [`api-helper-silent-on-non-json-errors`](api-helper-silent-on-non-json-errors/api-helper-silent-on-non-json-errors.md) | Low | R-01, R-09, P-05 (client half) | The `api` helper never checks `r.ok`, so a bodiless 500 makes Start / Load preset… do nothing and leaves start-up on "Detecting…". | Client half of `execute-malformed-body-500`, `preset-load-invalid-name-500` and BUG-06 |
| [`double-click-start-runs-two-jobs`](double-click-start-runs-two-jobs/double-click-start-runs-two-jobs.md) | Low | N-06 | Start has no in-flight guard. Presses ≤5 ms apart start two jobs; with Build package one fails while the banner says "Package built". | Machine-speed only: presses ≥15 ms apart start one job. The code-level frontend hunter rejected it as humanly unreachable; it is kept at Low as the first dynamic reproduction of **BUG-04** (two writers on one folder) |
| [`long-output-path-pushes-finish-offscreen`](long-output-path-pushes-finish-offscreen/long-output-path-pushes-finish-offscreen.md) | Low | L-03 | A ~90+ character unbroken output path in the result banner pushes Finish outside the 960 px shell window. | Removes one of R4's two exits |
| [`source-note-clips-local-option`](source-note-clips-local-option/source-note-clips-local-option.md) | Low | L-01, L-02 | With the version dropdown open, the "Sample catalog" note clips "Use driver files on disk" by 14 px. | Cosmetic |
| [`run-log-lines-have-no-timestamps`](run-log-lines-have-no-timestamps/run-log-lines-have-no-timestamps.md) | Low | S5-10 | FR-010 and CLAUDE.md say the log is timestamped; log entries are only `{text, cls}` (`Jobs.cs`). | Needs an **owner ruling**: fix the code or the wording |
| [`component-order-differs-from-mockup`](component-order-differs-from-mockup/component-order-differs-from-mockup.md) | Low | M-01 | Container Runtime Services is row 7, not last as in the frozen Screen 3 mockup (data order in the sample manifests). | Design-contract deviation; owner decides data vs. mockup |
| `preset-on-screen1-freezes-download-at-100` | Medium | **AC-005**, P-02 | (code-level hunt, above) | Also fails after a real process restart, not only on a fresh page |
| `pollrun-failed-job-dead-ends-run-screen` | Medium | R-07, R-08 | (above) | A trigger that writes nothing: Output folder = the sample's `manifest.json` file |
| `job-pollers-no-exit-on-404-or-network-error` | Low | N-04, R-02…R-05 | (above) | Reproduced by a real server restart mid-run |
| `screen1-next-no-inflight-guard-wrong-version` | Low | N-05, R-06 | (above) | The local-package branch has the same gap |
| `auto-reboot-submitted-with-unattended-off` | Low | S4-08 | (above) | — |
| *known:* BUG-06 | — | P-03, P-05 | Preset names silently stripped, truncated or overwritten; one malformed preset file breaks the list. | Registered (GAP-C03) |

- **Not testable here** (listed in the report header): the live NVIDIA catalog and its `sourceDetail` buckets, real driver download, the WebView2 shell, and real WMI detection.

## Unfiled leads (reported by the hunters, not reproduced or written up)

These are recorded so they are not lost; none is a confirmed finding.

- `Packages.WriteCustomized` writes payloads before `manifest.json`. A build that fails partway, after the previous build was cleaned, leaves payloads no manifest names, so later cleans can never remove them (probe observed one).
- Job and session ids restart at 1 when the server restarts, so a page left open could poll an unrelated new job. Reasoned only; GAP-S02's random ids would cover it.
- `Jobs` `TypeTag` distinguishes only beta and WHQL, so Studio and Game Ready builds of one version would share `<ver>-whql.exe` once GAP-F01 keeps both.
- Job error lines expose raw `ex.Message`, absolute paths included, through `/api/jobs/{id}`.
- `NvidiaCatalogProvider` has no cache, so every `/api/catalog` and `/api/download` call does a synchronous lookup of up to 5 s on a Kestrel thread.

## How these were verified

Every "Confirmed" report was reproduced by its author, then **re-run independently** against
unmodified HEAD `54b3c31`. Totals: catalog 9 red cases / 4 reports, jobs 12 / 5, packages 23 / 9,
API 19 / 7, and frontend 7 / 7 transcript scripts RED. The full 81-test functional UI suite was
re-run on a fresh server and gave the identical 57 / 23 / 0 / 1 result, covering every failing row
behind the seven UI-pass reports. The authors also applied each proposed
remedy to a scratch copy and saw the full suite plus the repros go green. That check is advisory;
the real fix PR must produce its own red/green evidence.

The real projects target `net10.0-windows` and cannot run on Linux, so verification used a
throwaway **Linux harness**. It compiles the real platform-neutral sources and the whole real
suite on `net10.0` (151 cases, all green at HEAD), plus the repro tests. `Program.cs` and
`ShellForm.cs` need WinForms and are not compiled; their reports are reasoned or tested through a
verbatim-copy seam. Recreate it outside the repo and drop a report's §A test into `repro/`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <RootNamespace>CleanDriver.Tests</RootNamespace>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <NoWarn>$(NoWarn);CA1416</NoWarn>
    <Src>/path/to/nvcleanclone/nvcleanstall</Src>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <Compile Include="$(Src)/Lib/*.cs" />
    <Compile Include="$(Src)/Api.cs" />
    <Compile Include="$(Src)/GlobalUsings.cs" />
    <Compile Include="$(Src)/CleanDriver.Tests/*.cs" />
    <Compile Include="repro/**/*.cs" />
    <Content Include="$(Src)/data/**" Link="data/%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="$(Src)/CleanDriver.Tests/fixtures/**" Link="fixtures/%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="$(Src)/Lib/Jobs.cs" Link="src/Jobs.cs" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="$(Src)/Lib/Packages.cs" Link="src/Packages.cs" CopyToOutputDirectory="PreserveNewest" />
    <Content Include="$(Src)/Api.cs" Link="src/Api.cs" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

For the frontend and live steps, a headless server harness (a copy of `Program.cs`'s `--headless`
branch over the real `Lib/`, `Api.cs` and `wwwroot/`) served the UI to Playwright/Chromium. Each
report's live steps are written for the real app: `dotnet run --project nvcleanstall -- --headless --mock-catalog` on port 4780.

**Caveats.** Linux is not the target platform. Windows-only semantics (path rules, `CreateProcess`
search order, WebView2) are marked "Windows-only — reasoned" in the affected reports, and Windows CI
has not run any of these repros. A fix PR's red evidence must come from the real
`net10.0-windows` suite.
