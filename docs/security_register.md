# CleanDriver Evidence-Based Gap and Security Register

| Field | Value |
|---|---|
| **Status** | **ACTIVE — adopted as the successor register (owner-pinned 2026-07-10, D-ADOPT).** An item is not complete until its acceptance criteria pass on merged code. Wave S begins with SEC-01. |
| **Baseline** | `main` at `b1e58a8913a2aec8824f4fbf09a3391d2da86557` (PR #21). The predecessor GAP-01…06 and `docs/hardening_register.md` HARD-01…06 remain closed; this document does not reopen them. |
| **Scope** | Current NVCleanstall feature research, comparison with public alternatives, full production-source review, security/correctness findings, and an ordered implementation register. |
| **Safety fence** | CleanDriver may download and inspect a driver package, but it must never execute a downloaded installer, install a driver, reboot Windows, or write the live registry. Extraction is lifted only by GAP-F03 under its exact controls — **owner sign-off for that lift recorded 2026-07-10 (D-WAVEF).** |
| **Delivery law** | Strict red-first TDD; one fresh worktree, branch, and PR per item; owner merges. Security Wave S completes before real-package work. |

**Owner adoption — 2026-07-10 (delegation-loop pins).**

- **D-ADOPT** — this document is the active successor register; it supersedes the architect's candidates ledger (already absorbing deferred items #9 → BUG-02 and #39 → SEC-03). Wave S begins immediately with **SEC-01** (manifest path traversal), whose failing test is already reproduced and in hand.
- **D-WAVEF** — Wave F (real NVIDIA package ingest/rebuild, GAP-F03/F04) is **in scope**. This lifts the extraction safety boundary under GAP-F03's exact controls, with owner sign-off recorded above per the standing safety rule. GAP-F01 still needs its own recorded UI ruling before build; the 7-Zip CLI interface is verified against primary sources at GAP-F03 prompt time.

## 1. Method and verified baseline

The audit read every production C# file, the WebView frontend, project/workflow files, tests, the current spec/parity/register chain, and recent history. It did not execute an NVIDIA installer, mutate the registry, fuzz native parsers, or perform a privileged penetration test.

Verified on 2026-07-10:

- `dotnet build nvcleanstall/CleanDriver.csproj -c Release` succeeds, with one unresolved `WindowsBase` version-conflict warning caused while resolving WebView2 assets.
- `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj -c Release` passes: **102/102**, 36 seconds.
- `dotnet list ... package --vulnerable --include-transitive` reports **no known vulnerable packages** from the configured NuGet sources. This is not proof that the application has no vulnerabilities.
- Current behavior is still a simulator after download: live metadata and installer bytes are real, but live components are replaced by a bundled sample manifest; install/tweak/signature actions are recorded, not performed.

Severity means: **Critical** permits access outside an intended trust boundary; **High** can compromise downloaded artifacts, files, or availability; **Medium** causes material incorrectness or requires a stronger precondition; **Low** is bounded robustness or maintainability risk.

## 2. Research: product and alternative behavior

Research snapshot: 2026-07-10. TechPowerUp's official page exposes NVCleanstall 1.19.0 and describes removal of real package components such as USB-C, notebook optimizations, and telemetry. TechPowerUp release material also documents Studio-driver detection, background update checks, MPO and NVENC options, actual package reconstruction/signature handling, and avoiding update prompts during active games.

Public alternatives provide corroborating, inspectable implementations:

- [TinyNvidiaUpdateChecker](https://github.com/ElPumpo/TinyNvidiaUpdateChecker) extracts a real NVIDIA package with 7-Zip, WinRAR, or NanaZip, discovers component directories through `.nvi` metadata, reads `requires` dependencies, supports notebook/Optimus components, multi-GPU/eGPU identification, Game Ready versus Studio choice, download-only/custom-location flows, and SHA-256 validation. See its [component parser](https://github.com/ElPumpo/TinyNvidiaUpdateChecker/blob/master/TinyNvidiaUpdateChecker/Handlers/ComponentHandler.cs) and [changelog](https://github.com/ElPumpo/TinyNvidiaUpdateChecker/blob/master/CHANGELOG.md).
- [lord-carlos/nvidia-update](https://github.com/lord-carlos/nvidia-update) proves a smaller automation surface: latest-driver lookup, download/extraction, a clean option, custom folder, and Task Scheduler use.
- [NVLite](https://github.com/baldknobber/nv-lite) is an adjacent beta tool, not an NVCleanstall clone. It demonstrates update history, release-note links, download cancellation, rollback, startup checks, and portable distribution. Its monitoring/profile features are outside CleanDriver's purpose.

Primary references: [TechPowerUp NVCleanstall download page](https://www.techpowerup.com/download/techpowerup-nvcleanstall/?wptouch_preview_theme=enabled), [NVCleanstall 1.15 release](https://www.techpowerup.com/forums/threads/techpowerup-nvcleanstall-v1-15-0-released.304001/), and [NVCleanstall 1.14 release archive](https://www.techpowerup.com/news-archive?month=1122).

### Capability comparison and disposition

| Capability demonstrated elsewhere | CleanDriver at baseline | Disposition |
|---|---|---|
| Inspect an actual NVIDIA package and list its real `.nvi` components/dependencies | Uses `LoadSampleTemplate`; displays mock `.bin` payloads for every live driver | GAP-F03, then GAP-F04 |
| Game Ready/Studio and desktop/notebook distinction | Query forces DCH and collapses same-version Studio/Game Ready rows to Game Ready | GAP-F01 |
| Broad GPU, multi-GPU, notebook, and eGPU identification | First NVIDIA WMI name only; static map contains 19 RTX 40/50 desktop names | GAP-F01; UI ruling required |
| Native choice of a driver file and output directory | “Browse” selects a bundled sample; output “Browse” only focuses a text field | GAP-F02 |
| Verify driver transport and publisher before retaining bytes | Host suffix checked; HTTPS and Authenticode are not required | GAP-S03 |
| Produce a usable customized NVIDIA package | ZIP contains mock payloads and a script that explicitly performs no installation | GAP-F04 |
| One portable executable | Publish workflow also ships `wwwroot/**` and `data/**` | GAP-F05 |
| Release history, scheduled checks, rollback, telemetry dashboard | Not present | Not scheduled; these expand the frozen five-screen customizer into an updater/monitoring product |
| Real installation, live registry application, NVENC patch, unsupported-device INF modification | Forbidden or absent | Explicitly excluded in §6 |

## 3. Code-audit findings

The evidence locations below refer to the pinned baseline.

| ID | Severity | Finding and impact | Destination |
|---|---|---|---|
| SEC-01 | **Critical** | Untrusted `Manifest.Version` and `ComponentDef.Payload` values are not constrained. They reach reads, writes, ZIP entry names, default output paths, and prior-build deletion (`Packages.cs:59-100,150-196`; `Jobs.cs:386-388,499-504`). `..`, rooted paths, and reparse points can escape intended directories, enabling arbitrary file read/write/delete and producing traversal entries in exported ZIPs. This is [CWE-22](https://cwe.mitre.org/data/definitions/22.html). | GAP-S01 |
| SEC-02 | **High** | The local Kestrel control plane has no capability token, Origin enforcement, frame protection, or explicit Host allowlist. `CLEANDRIVER_URLS` can bind beyond loopback (`Program.cs:11-18`), while APIs accept local paths, write outputs/presets, expose job paths, cancel jobs, and launch Explorer (`Api.cs`). Predictable session/job IDs increase cross-session impact. Kestrel does not validate Host headers unless filtering is enabled. | GAP-S02 |
| SEC-03 | **High** | Download allowlisting checks only the hostname. Initial and redirect URLs may downgrade to HTTP (`Jobs.cs:243-279`), the closed hardening register's deferred candidate #39. Completed `.exe` files are retained without Authenticode publisher verification. A tampered file is never executed by CleanDriver, but is presented as a completed NVIDIA download that a user may later run. | GAP-S03 |
| SEC-04 | **High** | Local ZIP/manifest processing has no entry-count, manifest-size, per-payload, or total-uncompressed limit and copies payloads through memory (`Packages.cs:44-49,70-82`). A highly compressed or oversized package can exhaust memory/disk ([CWE-409](https://cwe.mitre.org/data/definitions/409.html)). | GAP-S01 |
| SEC-05 | **Medium** | Live release version/channel/date values are interpolated into `innerHTML` without encoding (`app.js:116-122`); there is no CSP. `ShellForm` does not restrict top-level/frame navigation or new windows. Compromised metadata or future untrusted links could run script in a page that can call filesystem-capable APIs. Microsoft's [WebView2 security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security) requires origin and navigation checks. | GAP-S02 |
| SEC-06 | **Medium** | GPU detection launches `powershell` by PATH name (`Gpu.cs:109-119`). A planted or earlier-PATH executable can run with CleanDriver's privileges. | GAP-C04 |
| SEC-07 | **Medium** | `Microsoft.Web.WebView2` uses `Version="*"`; there is no package lock. Restores are non-reproducible and the current Release build emits an unresolved assembly conflict. Release ZIPs have no checksum/provenance file. The live NuGet audit is currently clean. | GAP-S04 |
| BUG-01 | **High** | Required components and dependency edges are enforced only in JavaScript. A direct `/api/execute` request can omit the required display driver, include unknown IDs/tweaks, or submit an inconsistent dependency set (`Api.cs:89-105`; `Jobs.cs:382-384`). | GAP-C01 |
| BUG-02 | **Medium** | Real-download artifacts are correlated only by version. A local package whose version matches an earlier live download receives that unrelated file in its receipt (`Api.cs:97-104`; `Jobs.cs:82-92`). This was deferred candidate #9. | GAP-C02 |
| BUG-03 | **Medium** | All install/silent runs on the same day overwrite `output/receipt-YYYY-MM-DD.json` (`Jobs.cs:385,440-465`), losing audit history. | GAP-C02 |
| BUG-04 | **Medium, plausible** | `ActiveDownloads` uses a check-then-set rather than atomic creation, so simultaneous requests can create two writers for one `.part` path. Package jobs can also replace the same sibling ZIP concurrently. Sequential tests pass; the race was not dynamically reproduced. | GAP-C02 |
| BUG-05 | **Medium** | `Api.Sessions` and `Jobs.Store` never expire. Repeated package/job calls retain manifests, jobs, and logs for process lifetime. | GAP-S02 |
| BUG-06 | **Low** | One malformed preset makes `Presets.List` throw; sanitized names can collide and overwrite; writes are not atomic (`Presets.cs:8-43`). | GAP-C03 |
| BUG-07 | **Medium** | UI descriptions claim tweaks are performed, but most are only recorded. MSI output hard-codes `VEN_10DE&DEV_0000&SUBSYS_00000000`; HD-audio/HDCP target fixed `0000` keys (`Tweaks.cs:98-136`). Manually importing these snippets is not device-specific and may do nothing or affect the wrong instance. | GAP-C05 |
| BUG-08 | **Medium** | “Single EXE” documentation contradicts the publish workflow, which explicitly uploads `CleanDriver.exe`, `wwwroot/**`, and `data/**`. Moving only the EXE breaks file-based content loading through `AppContext.BaseDirectory`. | GAP-F05 |
| BUG-09 | **Low** | Both READMEs still say all driver-touching behavior is mock-only, and the root README reports the old 18/10 parity tally, while live lookup/download and 102 tests are present. | GAP-S04 documentation rider |

## 4. Binding implementation register

Each ID is one PR and must include its own red/green evidence, full build/test output, `git diff --check`, and a live exercise where specified. Later work branches only after dependencies merge.

### Wave S — security gates

#### GAP-S01 — Confine and bound all package input/output

**Fix:** Add one server-side manifest validator used by folder and ZIP loads. Current-format version, component ID, and payload names use allowlists; payload is a leaf filename, never rooted and never containing either separator. Reject duplicate IDs/payloads case-insensitively, missing dependency targets, more than 512 components, manifests over 1 MiB, archives over 4,096 entries, any entry over 2 GiB, or more than 4 GiB total uncompressed data. Replace payload `byte[]` copies with bounded streaming. Canonicalize every path and prove it remains below its intended root; reject reparse points in source/output traversal. `CleanPreviousBuild` may delete only canonical, previously validated leaf payloads.

**Acceptance:**

1. Table-driven red tests cover `../`, `..\`, rooted/UNC/device paths, alternate separators, duplicate case variants, overlong values, a reparse-point escape, a traversal ZIP entry, and an oversized/over-count archive.
2. Each malicious case returns HTTP 400 with a stable non-path-leaking error and leaves sentinel files outside source/output untouched.
3. A valid folder and ZIP stream the same selected bytes as before; memory use is independent of the largest payload size.
4. Exported ZIP entry names are normalized relative paths and contain no absolute or parent segment.

#### GAP-S02 — Make the desktop HTTP/WebView boundary private

**Fix:** Bind only to loopback and reject non-loopback `CLEANDRIVER_URLS`. Enable Host filtering for `localhost`, `127.0.0.1`, and `[::1]`. Generate a 256-bit per-launch token; the native shell passes it in the URL fragment, `app.js` sends it as `X-CleanDriver-Token`, and every `/api/*` route requires it. Headless tests set a deterministic token through `CLEANDRIVER_API_TOKEN`. Mutating requests with a present `Origin` must match the actual loopback origin. Use cryptographically random session/job IDs. Add CSP (`default-src 'self'; frame-ancestors 'none'` with only the minimum style allowance), `X-Frame-Options: DENY`, and `X-Content-Type-Options: nosniff`. Encode release fields with `textContent`; restrict WebView navigation/frames to the loopback origin and send external links to the system browser. Cap active sessions at 32 and retained completed jobs at 128; expire both after 30 minutes.

**Acceptance:**

1. Requests with an attacker Host, wrong Origin, or absent/wrong token cannot read or mutate any API; valid shell/headless requests still complete.
2. A framing test is denied, and malicious release text renders literally without creating elements or executing events.
3. Navigation tests cancel non-loopback top-level/frame navigation; the known Microsoft help link opens externally.
4. Store-cap tests prove eviction never removes a running job or a session referenced by one.

#### GAP-S03 — Require authenticated HTTPS driver artifacts

**Fix:** Require `https` on the initial URL and every redirect before contact. Before `.part` is renamed, verify the PE with Windows `WinVerifyTrust` using the generic Authenticode policy and require a trusted signing chain whose publisher organization is `NVIDIA Corporation`; record SHA-256, signer subject, certificate thumbprint, and verification time in a sidecar JSON. Microsoft documents [WinVerifyTrust for PE verification](https://learn.microsoft.com/en-us/windows/win32/seccrypto/example-c-program--verifying-the-signature-of-a-pe-file). Verification failure deletes `.part`, leaves any prior valid final file unchanged, and sets a named failed status.

**Acceptance:**

1. HTTP initial and redirect targets fail before the fake handler records a request.
2. Injected verifier tests cover unsigned, invalid-chain, wrong-publisher, and valid-NVIDIA outcomes; only the last reaches `done` and creates the sidecar.
3. Replacement uses a temporary final name plus atomic move so failure preserves an earlier verified download.
4. Required Windows live evidence records `Get-AuthenticodeSignature`/WinVerifyTrust success and matching SHA-256 for one current NVIDIA package. No package is executed or extracted in this PR.

#### GAP-S04 — Reproducible dependencies and verifiable releases

**Fix:** Pin an exact WebView2 version, commit `packages.lock.json`, restore locked in CI, and resolve the `WindowsBase` conflict rather than suppressing it. CI runs the vulnerable-package audit. Tag publishing produces the exact release payload plus `SHA256SUMS` and GitHub build-provenance attestation. Correct README/parity claims to the merged baseline; CHANGELOG changes still land only at merge.

**Acceptance:** two clean restores resolve identical versions; Release build has zero warnings; vulnerability audit is green; a published artifact verifies against `SHA256SUMS` and `gh attestation verify`.

### Wave C — correctness and user trust

#### GAP-C01 — Enforce selections on the server

**Fix:** Validate before creating a job: every selected component exists exactly once; all required components and transitive dependencies are selected; tweak IDs and JSON types match `Tweaks.All`; parameter values are from their declared set; `auto-reboot` requires `unattended`. Existing declared conflicts remain warnings, not silent rejection.

**Acceptance:** direct API reds cover missing display driver, missing dependency, unknown/duplicate component, unknown tweak, wrong JSON type, invalid MSI priority, and orphan auto-reboot. Valid UI and preset selections remain green. Invalid requests create no job or files.

#### GAP-C02 — Correct artifact identity, receipts, and concurrent jobs

**Fix:** Store the `DownloadArtifact` on the catalog package session that consumed that download; local sessions always carry null. Receipt names use UTC timestamp plus random job ID and `CreateNew`. Create same-version downloads atomically so concurrent calls return one job. Serialize extract/package work by canonical output directory; a second live writer fails with `output directory is already in use`. Write ZIPs to a temporary sibling and atomically replace only after close.

**Acceptance:** barrier-based concurrency tests prove one HTTP request/one job, unique receipts for 20 same-day runs, no artifact on a same-version local session, one writer per output directory, and preservation of the previous valid ZIP after an injected failure.

#### GAP-C03 — Make presets atomic and fault-isolated

**Fix:** Validate (do not strip) trimmed names against `^[A-Za-z0-9 _-]{1,40}$`; use case-insensitive identity; write temp + atomic replace. List/load parse files independently, skip and log malformed entries, and never fail the endpoint because one file is corrupt.

**Acceptance:** malformed/truncated JSON, invalid names, case collisions, interrupted writes, and valid overwrite each have tests; valid existing presets still restore components/tweaks.

#### GAP-C04 — Remove PATH-based PowerShell execution

**Fix:** Resolve `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe`, require that canonical path, and retain the existing shared five-second budget and simulated fallback. Do not search the app/current/PATH directories.

**Acceptance:** a fake earlier-PATH `powershell.exe` is never invoked; canonical executable, timeout, and no-PowerShell fallback tests pass.

#### GAP-C05 — Make tweak claims and registry snippets truthful

**Fix:** Until GAP-F04 lands, installer telemetry, clean install, Ansel, control panel, driver telemetry, and signature descriptions explicitly say “simulated/recorded only.” Remove all placeholder registry targets. Extend detection with the exact GPU PNP device instance and NVIDIA HD-audio instance; emit MSI/HD-audio/HDCP snippets only when the corresponding canonical registry target is resolved. Each emitted change has a paired rollback `.reg`; unresolved targets produce a named warning and no snippet. MPO remains the documented global key. CleanDriver still never imports either file.

**Acceptance:** no output contains `DEV_0000`, `SUBSYS_00000000`, or an assumed `\0000`; recorded desktop/notebook/multi-GPU fixtures produce exact apply/rollback keys; unresolved devices produce no actionable file; UI/browser evidence shows truthful wording.

### Wave F — end-user parity, after Waves S and C

#### GAP-F01 — Broad GPU/catalog coverage and driver variants

**Blocker:** the frozen UI needs an owner ruling permitting a GPU selector when multiple NVIDIA adapters exist and a Game Ready/Studio selector on Screen 1. Do not implement before that ruling is recorded in this section and the design mockup is updated.

**Fixed behavior after ruling:** detect every NVIDIA adapter with PNP ID, notebook/desktop evidence, and installed version/type; select automatically only when exactly one candidate exists. Resolve NVIDIA lookup IDs from maintained metadata keyed by hardware identity rather than 19 exact display names. Preserve Game Ready and Studio rows as distinct `(version,type,platform)` releases; do not collapse by version. Cache metadata with schema/version/age and retain explicit mock fallback reasons.

**Acceptance:** recorded RTX 20/30/40/50 desktop, notebook Optimus, eGPU, multi-GPU, no-driver, unknown, Game Ready, and Studio fixtures select the correct lookup or named fallback; a live smoke for one desktop and one notebook returns the expected variant without HTML scraping.

#### GAP-F02 — Native file and output-folder pickers

**Fix:** Reuse the existing Browse buttons. In WebView2 shell mode, origin-checked web messages invoke `OpenFileDialog` for `.exe`/`.zip` and `FolderBrowserDialog` for extracted source/output directories, then return the selected canonical path. Cancel changes nothing. Headless mode keeps manual text input and never opens a dialog.

**Acceptance:** mocked dialog tests cover choose/cancel/wrong-origin; manual Windows evidence covers both buttons; existing five-screen layout and headless API are unchanged.

#### GAP-F03 — Read-only ingestion of a real NVIDIA package

**Dependency:** GAP-S01, GAP-S03, GAP-F02.

**Fix:** Add `IDriverPackageReader` for an already-extracted directory and a verified `.exe`. Resolve an installed 7-Zip CLI from explicit configuration, registry install paths, then standard Program Files paths; if absent, fail `7-Zip is required to inspect an NVIDIA installer`. Invoke it with `ProcessStartInfo.ArgumentList`, no shell, a unique `%LOCALAPPDATA%\CleanDriver\work\<GUID>` directory, a five-minute timeout, captured output, and kill-tree cleanup. Parse component directories containing `.nvi` XML; read localized title and `nvi/dependencies/*[@type='requires']`; require `Display.Driver`; preserve unknown components by technical name. Parse `setup.cfg` only as data. Never invoke `setup.exe`.

**Acceptance:** synthetic `.nvi` fixtures prove labels, unknowns, dependency graph, XXE-disabled XML parsing, duplicate/malformed rejection, timeout/nonzero-exit cleanup, and no process target other than the resolved extractor. Required owner-run evidence inspects one current Game Ready and one notebook package and shows the UI list/dependencies match their extracted directories.

#### GAP-F04 — Build a real, internally consistent customized NVIDIA package

**Dependency:** GAP-F03, GAP-C01, GAP-C05.

**Fix:** Copy the verified package's selected component directories plus required NVIDIA installer infrastructure (`setup.exe`, `setup.cfg`, `NVI2`, licenses/device list when present) into unique staging. Rewrite `setup.cfg` from the parsed model so every retained reference exists and every removed component reference is absent. Preserve signed NVIDIA binaries byte-for-byte; do not claim NVIDIA or Windows trust for modified metadata. Produce folder and ZIP outputs with an inventory containing relative path, size, and SHA-256 for every file. The bundled launcher remains manual and clearly states that CleanDriver did not execute/install it.

**Acceptance:** fixture and live-package tests prove selected/required closure, absence of deselected component directories/references, byte identity for retained binaries, inventory/ZIP equality, clean rebuilds, and zero mock `.bin` files. No acceptance step launches `setup.exe`; functional installation validation is out of scope.

#### GAP-F05 — Deliver the promised single executable

**Fix:** Bundle `wwwroot/**` and `data/**` using the .NET single-file content mechanism (`IncludeAllContentForSelfExtract`) and keep `IncludeNativeLibrariesForSelfExtract`. Microsoft documents that this mode extracts bundled content before startup in its [single-file deployment guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview). Store mutable output/presets under `%LOCALAPPDATA%\CleanDriver`, never beside extracted bundle content.

**Acceptance:** a clean `win-x64` publish directory contains only `CleanDriver.exe` plus checksum/provenance artifacts; copying only the EXE to an empty directory starts native and headless modes, serves UI/data, saves presets, and writes output under LocalAppData. The published EXE is inspected for all expected bundle entries.

## 5. Required execution order

1. GAP-S01 → S02 → S03 → S04.
2. GAP-C01 → C02 → C03 → C04 → C05.
3. Record the GAP-F01 UI ruling. GAP-F02 may proceed independently after Wave S.
4. GAP-F01 and F02 → F03 → F04 → F05.
5. After each merge, update the finding row's status and only re-grade `specs/nvcleanstall/parity.md` behavior proven by that merge. Never count this document itself as implementation.

## 6. Explicit exclusions

- **No driver execution or installation.** CleanDriver never launches `setup.exe`, even after GAP-F04.
- **No live registry writes or reboot.** Apply/rollback `.reg` files are inspectable artifacts only.
- **No NVENC session-limit patch or driver-binary telemetry patch.** Both modify third-party binaries, invalidate trust, and exceed the safety/IP scope.
- **No unsupported-hardware INF modification.** Adding device/subsystem IDs can install an incompatible driver; users receive a named unsupported result.
- **No background service, scheduled updater, game-session detector, rollback engine, sensor dashboard, shader-cache cleaner, or NVIDIA profile editor.** These are separate products, not missing behavior in the five-screen package customizer.
- **No Windows 7/8 or 32-bit target.** This repository's target remains Windows 11 `win-x64`.
- **No copied TechPowerUp/NVIDIA code, branding, descriptions, or assets.** Research establishes behavior only; implementation remains original.

## 7. Exit criteria

This register closes only when every non-blocked item is merged, GAP-F01 has either merged or an owner-recorded rejection with a reason, all audit findings have a merged fix or explicit accepted-risk record, build is warning-free, tests and CI are green, the NuGet audit is clean, and README/spec/parity/CHANGELOG accurately describe the shipped artifact. Any new finding receives a new ID; it is never silently folded into an unrelated PR.
