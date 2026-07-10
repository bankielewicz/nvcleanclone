# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**CleanDriver** — a functional clone of TechPowerUp's NVCleanstall (NVIDIA driver customizer), built as a Windows 11 app: a five-screen wizard served by an in-process ASP.NET Core (Kestrel) server, rendered in a WebView2 window, shipped as one self-contained exe. Since the GAP series it queries the **live NVIDIA catalog** (mock fallback) and downloads **real driver bytes** to disk — but everything past download is simulated against a mock package format by design: nothing is ever executed, installed, or written to the live registry. See the safety boundary below before changing anything driver-related.

## Commands

The app targets `net10.0-windows` and builds only with the Windows .NET 10 SDK. From WSL, `dotnet` is:

```
"/mnt/c/Program Files/dotnet/dotnet.exe"
```

```bash
# build
dotnet build nvcleanstall/CleanDriver.csproj

# run: native WebView2 window
dotnet run --project nvcleanstall

# run: server only (verification mode) → http://localhost:4780
dotnet run --project nvcleanstall -- --headless

# deterministic/offline runs: force the bundled mock catalog (or CLEANDRIVER_MOCK_CATALOG=1)
dotnet run --project nvcleanstall -- --headless --mock-catalog

# tests (xunit; the suite IS the quality gate — 102 tests at `0f51fe9`)
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~<TestName>"   # single test

# ship a self-contained single exe (~139 MB, no runtime needed on target)
dotnet publish nvcleanstall/CleanDriver.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o nvcleanstall/publish
```

Every PR must be green on: `dotnet build`, `dotnet test`, `git diff --check`.

### TDD is strict here (CONTRIBUTING.md governs — read it before coding)

Non-negotiable order for every behavior change: failing test first (capture its output),
minimum implementation, refactor. No production code without a failing test that requires
it. Commit the test with or before its implementation — a PR whose history shows
implementation-then-tests gets returned. Every PR body must show red-then-green evidence
per behavior (the PR template asks for it). Bug fixes start with a reproducing test.
Existing tests are back-compat pins: never edit one to make it pass without disclosing and
justifying that edit in the PR body.

Two recorded exemptions (`docs/hardening_register.md` §3): **A2** — docs-only PRs satisfy
the gate vacuously (state the exemption in the PR body by name); **A6** — `wwwroot/` has
no JS test runner, so frontend-only changes capture red/green as live browser/curl
transcripts instead (declared via `slice_start --no-surface '^nvcleanstall/wwwroot/'`,
sanctioned by the pattern appearing verbatim in the build plan). Do not add a JS toolchain.

**CHANGELOG rule (CL-1):** entries land at merge time only, for merged work only —
never add an entry for an open PR (`gh pr list` owns open work).

### Environment quirks (WSL ↔ Windows)

- Inline env vars do **not** cross into launched Windows `.exe` processes (`FOO=1 ./CleanDriver.exe` silently drops `FOO`). Pass CLI flags instead, or set the variable in the Windows environment.
- Kill a running app instance with `taskkill.exe /F /IM CleanDriver.exe` (a `dotnet run` WSL wrapper can exit while the Windows process keeps serving).
- `localhost:4780` is reachable from WSL (mirrored networking). JSON POST bodies with Windows paths: use forward slashes (`C:/Projects/...`) to avoid escaping bugs.

## Architecture

**Kestrel/WebView2 split (the load-bearing decision):** `Program.cs` always starts the HTTP server; the WinForms `ShellForm` (WebView2 → `localhost:4780/?shell=1`) is just chrome. `--headless` skips the window, which is what makes every acceptance criterion terminal-verifiable (curl/Playwright). Don't put logic in the shell; everything testable lives behind the HTTP API.

**Request flow:** `wwwroot/app.js` (wizard state machine, 5 screens + run view) → JSON API in `Api.cs` (all routes; camelCase via `Json.Web` in `Lib/Models.cs`) → static classes in `Lib/`:

- **Catalog is a provider seam** (`ICatalogProvider` via `CatalogProviderFactory`): live by default — `NvidiaCatalogProvider` resolves the detected GPU through `GpuPfidMap` and queries NVIDIA's lookup, falling back to `MockCatalogProvider` (which reads `data/catalog.json`, 5 sample releases) on any failure. `/api/catalog` carries `source` (`live`/`mock`) and, when mock, a `sourceDetail` reason bucket (`mock mode` / `GPU not matched` / `live lookup failed`). `--mock-catalog` forces mock deliberately. Mock release `566.36` intentionally lacks the USB-C component so version switching visibly changes the component list.
- `Jobs.cs` is the async engine: background step lists appending timestamped log lines the frontend polls via `/api/jobs/{id}`; receipts/artifacts land in `output/` beside the exe. **Live releases download real bytes** (`.part` → rename; deleted on cancel/failure; per-read stall timeout; size caps) with the `.nvidia.com` host allowlist enforced **per redirect hop** (`AllowAutoRedirect` off, manual follow, `MaxRedirectHops = 5`). Mock releases simulate. Install/silent/extract/package are always simulated, and the written `manifest.json`/`config.json` carry honesty markers (`signatureSimulated`, `driverTelemetrySimulated`) — keep those honest.
- `Packages.cs` loads catalog/local packages (folder or zip) and writes customized output (selected payloads + rewritten manifest; `package` also zips an explicit file list to an archive **beside** `outDir`). Before writing it runs `CleanPreviousBuild`: deletes **only** artifacts named by a prior `customizedBy == "CleanDriver"` manifest, `File.Delete` by name — a shape-guard test forbids any recursive delete. `IsFilesystemRoot` rejects a drive-root `outputPath` at the top of `StartExecute`, before any write.
- `Tweaks.cs` is the single source of tweak definitions (descriptions, warnings, dependency conflicts, `.reg` snippet emitters — written to output, **never applied**).
- `Gpu.cs` detects the real GPU via PowerShell/WMI (marketing version from the WMI string's last 5 digits), `Simulated` fallback. The WMI read is bounded by one 5s budget (`ReadBounded` — the read and the exit-wait share it; a wedged powershell is killed). `ParseVideoControllers`/`DetectFrom`/`MarketingVersion` are pure, pinned seams; `Detect()` caches, so the first `/api/system` or `/api/catalog` call pays the query.

**Package-session tokens:** `/api/package` loads a manifest and returns a token; `/api/execute` requires it. Tokens live in server memory — a server restart invalidates the wizard's in-flight state.

**Frontend contract:** the five screens are a frozen design contract — the mockups in `docs/design/CleanDriver GPU driver wizard/` plus numbered rulings R1–R10 in `docs/design/nvcleanstall-mockup-prompt.md` (e.g. R3: Back hidden during download; R4: footer hidden in the result state). No new screens/dialogs/controls without an owner ruling. Component names from local manifests are untrusted — escape anything interpolated into `innerHTML` (see `esc()` in `app.js`).

## Governing documents (read before non-trivial work)

The docs chain is authoritative and ordered: `specs/nvcleanstall/spec.md` (scope cut, FRs, ACs, stack pin) → `specs/nvcleanstall/progress.md` (milestone status) → `specs/nvcleanstall/parity.md` (verified feature-parity table) → the task registers `docs/gaps_analysis.md` (GAP-01…06) and `docs/hardening_register.md` (HARD-01…06; recorded amendments A1–A6 live in its §3) — **both CLOSED 2026-07-10; there is currently no open register**, so new work needs an owner-pinned register entry first, and each register's §4 out-of-scope fence stays absolute. `CONTRIBUTING.md` binds process: one slice = one worktree (`../nvcleanclone-<name>`) = one branch (`feat/<slug>` / `docs/<slug>`) = one PR; **never merge — the owner merges**. The three `docs/ai-*-workflow.html` pages document the AI→AI build/review/fix loop this repo is built with.

## Hard boundaries

- **Safety:** never execute downloaded installers, never install drivers, never write the live registry. Named register entries may lift a *specific* part with owner sign-off (GAP-02 permits downloading bytes to disk, still never executing); nothing else does.
- **Deletion (owner ruling, D12/D1):** never delete recursively — `Directory.Delete(x, true)` is forbidden and guard-tested absent; deletion is enumerable-by-name only, and only of artifacts a CleanDriver manifest declares. `outputPath` is user-supplied: a filesystem root is rejected by name before any write, and tests must never run a real build against a drive root (compile-level or predicate-level reds instead).
- **IP:** original branding only ("CleanDriver"). No NVIDIA/TechPowerUp logos, trademarks, or copied assets; NVIDIA product names appear only as nominative labels in mock data (ruling R10).
