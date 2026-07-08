# gaps_analysis.md — CleanDriver Real-Driver Parity Gap Register

| | |
|---|---|
| **Date** | 2026-07-08 |
| **Baseline** | `main` @ `c94de87` (initial CleanDriver clone + CONTRIBUTING.md; all 5 acceptance criteria pass per `specs/nvcleanstall/parity.md`; **no test project exists yet**; no CI). |
| **Authority** | Authoritative, closed task list for closing the 9 `simplified` features in `specs/nvcleanstall/parity.md` §2 toward real-driver parity. Items not in §3 are out of scope (§4), without exception. |
| **Gate** | **Every feature this register closes is proven against the real thing** — real NVIDIA driver metadata and a real downloaded installer file on disk — while the safety boundary (never execute an installer, never install a driver, never write the live registry) stays intact except where a gap here explicitly lifts a named part of it with the AC that proves the lift is opt-in and reversible. |

> **Naming caution:**
> - **"Simplified" (parity.md status) vs. "GAP" (this register):** each of the 9 `simplified` rows in `specs/nvcleanstall/parity.md` §2 maps to exactly one GAP below; a `parity` row is already done and is never a GAP.
> - **"Catalog" is overloaded:** the *mock catalog* is `nvcleanstall/data/catalog.json` (5 hard-coded releases). The *live catalog* is NVIDIA's lookup service. GAP-01 makes the catalog **provider-backed** so both are the same code path with different providers — do not conflate "the JSON file" with "the catalog concept."
> - **"Download" is overloaded:** the current `StartDownload` (`nvcleanstall/Lib/Jobs.cs:68`) is a *simulated progress animation* that copies nothing. GAP-02's *real download* fetches actual bytes to disk. They are different jobs; GAP-02 replaces the simulation on the live path only.
> - **"Install" vs. "download":** downloading a real installer (GAP-02, permitted) is NOT installing it (executing it — forbidden, §4). The distinction is load-bearing for the safety boundary.
> - **DCH vs. Standard, Studio (SD) vs. Game Ready (GRD), WHQL vs. Beta:** NVIDIA driver *types*. GAP-01 must model them explicitly; "latest" is ambiguous without them.

## 1. Pre-flight (gating measurements)

**PF-1 — Test harness exists and is green.** No test project exists at baseline. The FIRST commit of GAP-01 scaffolds `nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` (xunit, `net10.0-windows`, project-reference to `../CleanDriver.csproj`). From that commit onward, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` MUST pass on every PR. If scaffolding cannot build (SDK/runtime missing), HALT with the exact error — do not proceed without a green harness.

**PF-2 — Live NVIDIA reachability probe (GAP-01 only, informational, non-blocking to merge).** Before implementing GAP-01, run the scripted probe the builder writes (see GAP-01 AC) against NVIDIA's lookup endpoint once and paste the raw JSON response into the PR body as evidence the parsed shape matches reality. If the endpoint is unreachable from the build environment, record that in the PR body and rely on the recorded fixture — do not block; the mock-fallback AC still proves the feature.

## 2. Delivery structure — 6 gaps, 6 PRs

Each gap is a separate branch + PR against `main`, **sequential** (each branches after the prior merges), each independently green per `CONTRIBUTING.md` gates (`dotnet build`, `dotnet test`, `git diff --check`). Branch names: `feat/gap-01-live-catalog`, `feat/gap-02-real-download`, `feat/gap-03-real-detection`, `feat/gap-04-install-simulation-fidelity`, `feat/gap-05-signature-and-telemetry`, `feat/gap-06-single-exe-package`. Standard workflow per `CONTRIBUTING.md`: fresh worktree per gap, TDD, push, open PR, **never merge** — the owner reviews/merges each before the next begins.

**The kickoff prompt builds ONE gap per session** (GAP-01 spans two tightly-coupled concerns — provider abstraction + live lookup — that cannot be a coherent PR apart; it is still one PR). Do not batch gaps.

## 3. Gap register

### Wave A — the two owner-pinned real-driver gaps

**GAP-01 — Live NVIDIA version lookup behind a catalog-provider seam.**
Introduce an `ICatalogProvider` abstraction and route all catalog reads through it, so the mock JSON and the live NVIDIA service are interchangeable implementations. Realizes `specs/nvcleanstall/parity.md` FEAT-002/FEAT-003 (`simplified` → real metadata) and the maintainer pin recorded in this session's chat ("lookup + real download").
- **Seam to create (single-owner, reuse forever):** `nvcleanstall/Lib/ICatalogProvider.cs` with `IReadOnlyList<Release> GetReleases(GpuInfo gpu)`. Two implementations: `MockCatalogProvider` (wraps the existing `Catalog.Releases()` at `nvcleanstall/Lib/Catalog.cs:9`, unchanged behavior) and `NvidiaCatalogProvider`.
- **Live source:** NVIDIA's manual-lookup JSON service (`https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup` with `psid`/`pfid`/`osid`/`dch`/`dtcid` query params; the lookup-value list is at `https://www.nvidia.com/Download/API/lookupValueSearch.aspx`). The response nests driver rows with `Version` and `DownloadURL` under `IDS[].downloadInfo`. **Verify the exact response shape live in PF-2 before parsing; the parser is written against the recorded fixture, not memory.**
- **GPU→pfid resolution:** map the detected GPU name (`GpuInfo.Name`, `nvcleanstall/Lib/Gpu.cs`) to NVIDIA's product-family id. Ship a small static lookup table for at least the RTX 40/50-series desktop parts as a bundled JSON resource; when the GPU is unknown or `GpuInfo.IsSimulated` is true, the provider MUST fall back to `MockCatalogProvider` and the returned `Release` list MUST carry a flag/marker the UI can show as "sample catalog (GPU not matched)". No free-form web scraping of nvidia.com HTML.
- **Selection wiring:** `Program.cs`/`Api.cs` select `NvidiaCatalogProvider` by default and `MockCatalogProvider` when a `--mock-catalog` CLI flag or `CLEANDRIVER_MOCK_CATALOG=1` env var is set (so verification and offline runs are deterministic). `/api/catalog` returns the provider's releases plus a `source` field (`"live"` | `"mock"`).
- **Network hygiene:** all HTTP goes through a single injectable `HttpClient`/`HttpMessageHandler` so tests use a fake handler with the recorded fixture; a hard 5s timeout; any failure (timeout, non-200, parse error) falls back to mock and is logged, never thrown to the UI.
- **Boundary:** metadata only — this gap downloads NO driver bytes and changes NO install/tweak behavior.
- **AC:**
  1. `dotnet test` includes a test that feeds `NvidiaCatalogProvider` a fake `HttpMessageHandler` returning the recorded NVIDIA fixture and asserts the parsed `Release` list (version strings, WHQL/Beta channel, and each `DownloadURL`) matches expected values.
  2. A test asserts that when the handler throws/times out OR the GPU is simulated, `GetReleases` returns the mock catalog and the result is marked `source == "mock"`.
  3. `GET /api/catalog` with `CLEANDRIVER_MOCK_CATALOG=1` returns exactly the 5 mock releases with `"source":"mock"` (curl assertion pasted in PR body).
  4. PF-2 raw live JSON is pasted in the PR body (or its unreachability recorded).
  5. The five-screen mockup contract is unchanged: the version dropdown still renders per `docs/design/CleanDriver GPU driver wizard/mockup-screen-01-driver-source.html`; a "sample catalog" marker appears only in the mock-fallback state.

**GAP-02 — Real installer download to disk, real progress, never executed.**
Replace the simulated download on the **live** path with a real byte-for-byte fetch of the selected release's `DownloadURL` to a file under `output/`, streaming real progress; the file is NEVER executed and NEVER extracted. Realizes `specs/nvcleanstall/parity.md` FEAT-005 (`simplified` → real download) and FEAT-002's download half. Depends on GAP-01 (needs `DownloadURL` from live releases).
- **Seam:** extend `nvcleanstall/Lib/Jobs.cs`. Add `StartRealDownload(Release release, string destDir)` alongside the existing `StartDownload` (keep the simulation for the mock path and for `--mock-catalog`). The `/api/download` route (`nvcleanstall/Api.cs:52`) chooses real vs. simulated by the release's `source`.
- **Behavior:** stream the HTTP response to `output/drivers/<version>-<type>.exe.part`, updating the same `Job` progress/doneMB/speed fields the UI already polls (`pollDownload`, `nvcleanstall/wwwroot/app.js:146`) from real `Content-Length` and bytes-read — the download screen (`mockup-screen-02-download.html`) needs no change. On completion rename `.part`→final. On cancel/failure, delete the partial file. Enforce a configurable max size and a disk-space check before starting.
- **Hard safety AC (boundary lift is explicit and narrow):** downloading is permitted; **executing or extracting the file is forbidden**. A test asserts no code path passes the downloaded file to `Process.Start`, `ZipFile`, or any extractor. After download, the wizard proceeds to the component screen using the **mock manifest** for that version (real package parsing is OUT — §4, GAP-OUT-1); the UI labels components "sample component list — real package parsing not yet implemented."
- **AC:**
  1. A test drives `StartRealDownload` with a fake handler streaming a known N-byte body to a temp dir and asserts: the output file exists with exactly N bytes, `Job.progress` reached 1.0, `doneMB` equals N, and the `.part` file was renamed.
  2. A test asserts a mid-stream failure deletes the partial file and sets `Job.status == "failed"` with a message, leaving no `.part` residue.
  3. A test greps the production code paths reachable from `/api/download` and `/api/execute` and asserts the downloaded path is never handed to `Process.Start`/`ZipFile`/extraction (shape-without-function guard).
  4. With `CLEANDRIVER_MOCK_CATALOG=1`, `/api/download` still uses the original simulation (unchanged behavior — byte-identity of the mock path is a back-compat pin).
  5. Live smoke (scripted, pasted in PR body, not a unit test): a real small driver metadata lookup + a real download of the actual `DownloadURL` to `output/drivers/`, showing the final file size matches NVIDIA's `Content-Length`. If the environment has no network, record that and rely on ACs 1–4.

### Wave B — the remaining simplified features (each a self-contained PR)

**GAP-03 — Real GPU detection hardening.**
`specs/nvcleanstall/parity.md` FEAT-001 (`simplified`). Current detection (`nvcleanstall/Lib/Gpu.cs`, `QueryPowershell` + `MarketingVersion`) already reads real WMI but is single-strategy and best-effort. Make it robust and testable without changing the `GpuInfo` shape.
- Extract the WMI-line→`GpuInfo` parsing (name normalization, `MarketingVersion`) into a pure function so it is unit-testable independent of the live `powershell` call. Add multi-GPU handling (pick the NVIDIA adapter deterministically when several are present) and a documented fallback order (WMI → simulated).
- **AC:** tests feed recorded `Win32_VideoController` output lines (single-NVIDIA, NVIDIA+iGPU, no-NVIDIA, malformed driver string) and assert the resulting `GpuInfo` (name, `InstalledDriverVersion`, `IsSimulated`, `DetectedVia`) for each; `MarketingVersion` boundary cases (short/long/dotless) are asserted. No behavior change when a single NVIDIA GPU is present (back-compat pin).

**GAP-04 — Install/silent simulation fidelity from the real download.**
`specs/nvcleanstall/parity.md` FEAT-011, FEAT-024, FEAT-025 (all `simplified`). Still simulated (no real install — that stays forbidden, §4), but the simulation must reflect the **real** downloaded artifact from GAP-02 when present: log the real file path and real size, and honor unattended/auto-reboot/clean-install flags in the emitted receipt and log wording exactly as the existing receipt schema defines. Depends on GAP-02.
- **Seam:** `nvcleanstall/Lib/Jobs.cs` `StartExecute`. Do not add real execution; enrich the log/receipt with real-artifact facts when the job's release `source == "live"`.
- **AC:** a test runs `StartExecute` for `install` and `silent` with a stubbed real-download result and asserts the receipt JSON records the real file path + size and the flag fields; a test asserts `auto-reboot` never triggers any real reboot API (grep guard). Mock-path receipts unchanged (byte-identity pin).

**GAP-05 — Signature-rebuild and driver-telemetry honesty.**
`specs/nvcleanstall/parity.md` FEAT-017, FEAT-022 (both `simplified`). These stay simulated but must stop overclaiming: the "rebuilding digital signature" step and the `signature: rebuilt` manifest marker, and the driver-telemetry tweak, must each emit an explicit "(simulated — no real signing/patching performed)" qualifier in the log and a `simulated: true` field in any artifact they write, so no output can be mistaken for a really-signed package.
- **Seam:** `nvcleanstall/Lib/Jobs.cs` (log lines + manifest writer) and `nvcleanstall/Lib/Packages.cs` `WriteCustomized` (`signature` field). Additive only.
- **AC:** tests assert the build-package manifest carries both `signature: "rebuilt"` AND `signatureSimulated: true`, and that the log contains the qualifier string; driver-telemetry-on output records `simulated: true`. Existing `signature: rebuilt` consumers still see that value (back-compat pin).
- **Resolution (owner ruling, GAP-05 impl session — closes the "any artifact they write" ambiguity):** the honesty markers live on the artifacts that already record the tweak's selection — the install/silent **receipt**, the customized-package **manifest** (`WriteCustomized`), and the build-package **config.json** — **no standalone note file** (a driver-telemetry-only note would be output clutter and a new artifact class for GAP-06's zip to carry). Concrete field names, symmetric and additive/null-omitted (`WhenWritingNull`) like GAP-04's receipt fields: **`signatureSimulated: true`** (present only when the package is modified / the rebuild step runs, alongside the unchanged `signature: "rebuilt"`) and **`driverTelemetrySimulated: true`** (present only when the `driver-telemetry` tweak is on). Log qualifiers are per-context: the rebuild line gains `(simulated — no real signing performed)`; a new `> Patching driver telemetry endpoints… (simulated — no real patching performed)` line is emitted in every flow where the tweak is on.

**GAP-06 — True single-EXE package output.**
`specs/nvcleanstall/parity.md` FEAT-027 (`simplified` → parity). "Build package" currently writes a directory (`payload/` + `install.cmd` + `config.json`). Produce, additionally, a single self-contained archive file (a `.zip` written via `System.IO.Compression`, named `<version>-cleandriver-package.zip`) containing that directory tree, so the output is one redistributable file. The bundled `install.cmd` remains the simulated (non-executing) installer.
- **Seam:** `nvcleanstall/Lib/Jobs.cs` package branch; use `System.IO.Compression.ZipFile.CreateFromDirectory`. Additive — the directory output stays for inspection.
- **AC:** a test runs the package action and asserts a single `<version>-cleandriver-package.zip` exists, opens it, and confirms it contains `manifest.json`, `payload/<selected>.bin` (only selected components), `install.cmd`, `config.json`, and any `.reg` snippets — matching the on-disk directory exactly.

## 4. Explicitly OUT OF SCOPE

Honored without exception, however natural it feels while in the code. Each names its real destination.

- **GAP-OUT-1 — Real NVIDIA package parsing (7z/`setup.cfg`).** Extracting a downloaded installer to derive the *real* component list. Deferred: this is `specs/nvcleanstall/parity.md` §3 next-step 1; it requires an extractor and lifts a boundary (extraction) — a future register only. Until then the component screen uses the mock manifest with the "sample component list" label (GAP-02).
- **GAP-OUT-2 — Executing or installing any driver.** Forbidden permanently by the safety boundary (`CONTRIBUTING.md`, `specs/nvcleanstall/spec.md` §1). No gap here lifts it.
- **GAP-OUT-3 — Applying tweaks to the live registry.** `specs/nvcleanstall/parity.md` §3 next-step 4. The app emits `.reg` files it never imports; making them apply for real is a future register with its own opt-in/elevation ADR.
- **GAP-OUT-4 — Native OS file/folder pickers in the WebView2 shell.** `specs/nvcleanstall/parity.md` §3 next-step 5. Useful but independent of real-driver parity; deferred to a follow-up UI register. Browse… keeps the bundled-sample shortcut.
- **New UI screens, dialogs, or controls.** The five-screen mockup + rulings R1–R10 are the design contract; changes need an owner ruling first (`CONTRIBUTING.md`).

## 5. Binding constraints (cite, don't restate)

- **Process & TDD:** `CONTRIBUTING.md` (worktree/branch/PR workflow, gate commands, TDD rules, project invariants).
- **Spec & safety/IP boundaries:** `specs/nvcleanstall/spec.md` (§1 safety boundary, §6 stack pin, §2 FRs), `specs/nvcleanstall/parity.md` (the 9 `simplified` rows this register closes).
- **Design contract:** `docs/design/nvcleanstall-mockup-prompt.md` (rulings R1–R10) and the five files in `docs/design/CleanDriver GPU driver wizard/`.
- **Stack facts (verify live before relying):** `nvcleanstall/CleanDriver.csproj` (`net10.0-windows`, WebView2, ASP.NET framework ref); named code seams cited per-gap above — open each before citing.
- **Environment quirk:** from WSL, the .NET CLI is `"/mnt/c/Program Files/dotnet/dotnet.exe"`; the app targets `net10.0-windows` and builds on the Windows SDK. Inline WSL env vars do NOT cross into launched Windows `.exe` processes — pass configuration via CLI flags or set the env var in the Windows environment, not inline before a `./x.exe` call.

---
**Provenance.** Produced by **Claude Code** (Claude Fable 5), session `64712235-4ddd-4f66-86a1-e0d233ba7258`, 2026-07-08 UTC.
Source state: `main` @ `c94de87`. Intended consumer: **a cold Claude Code implementation session** (one gap per session).
Conventions: the host repo's `CONTRIBUTING.md` + `specs/nvcleanstall/` docs + `docs/design/` mockups.
Return contract: one PR per gap against `main`, never merged; STOP for owner review between gaps; HALT via AskUserQuestion on any ambiguity.
---
