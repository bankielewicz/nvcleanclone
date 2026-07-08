# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

**CleanDriver** — a functional clone of TechPowerUp's NVCleanstall (NVIDIA driver customizer), built as a Windows 11 app: a five-screen wizard served by an in-process ASP.NET Core (Kestrel) server, rendered in a WebView2 window, shipped as one self-contained exe. It operates on a **mock** driver catalog/package format by design — see the safety boundary below before changing anything driver-related.

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

# tests (xunit; scaffolded as CleanDriver.Tests per docs/gaps_analysis.md PF-1)
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~<TestName>"   # single test

# ship a self-contained single exe (~139 MB, no runtime needed on target)
dotnet publish nvcleanstall/CleanDriver.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o nvcleanstall/publish
```

Every PR must be green on: `dotnet build`, `dotnet test`, `git diff --check` (see `CONTRIBUTING.md` — TDD is mandatory, red→green→refactor, bug fixes start with a reproducing test).

### Environment quirks (WSL ↔ Windows)

- Inline env vars do **not** cross into launched Windows `.exe` processes (`FOO=1 ./CleanDriver.exe` silently drops `FOO`). Pass CLI flags instead, or set the variable in the Windows environment.
- Kill a running app instance with `taskkill.exe /F /IM CleanDriver.exe` (a `dotnet run` WSL wrapper can exit while the Windows process keeps serving).
- `localhost:4780` is reachable from WSL (mirrored networking). JSON POST bodies with Windows paths: use forward slashes (`C:/Projects/...`) to avoid escaping bugs.

## Architecture

**Kestrel/WebView2 split (the load-bearing decision):** `Program.cs` always starts the HTTP server; the WinForms `ShellForm` (WebView2 → `localhost:4780/?shell=1`) is just chrome. `--headless` skips the window, which is what makes every acceptance criterion terminal-verifiable (curl/Playwright). Don't put logic in the shell; everything testable lives behind the HTTP API.

**Request flow:** `wwwroot/app.js` (wizard state machine, 5 screens + run view) → JSON API in `Api.cs` (all routes; camelCase via `Json.Web` in `Lib/Models.cs`) → static classes in `Lib/`:

- `Catalog.cs` reads `data/catalog.json` (5 mock releases); each release has a package at `data/packages/<version>/` (manifest.json + payload files). `566.36` intentionally lacks the USB-C component so version switching visibly changes the component list.
- `Packages.cs` loads catalog/local packages (folder or zip) and writes customized output (selected payloads + rewritten manifest, `signature: rebuilt` when the set differs from stock).
- `Jobs.cs` is the async engine: simulated download + install/silent/extract/package actions run as background step lists appending timestamped log lines the frontend polls via `/api/jobs/{id}`. Receipts/artifacts land in `output/` beside the exe.
- `Tweaks.cs` is the single source of tweak definitions (descriptions, warnings, dependency conflicts, `.reg` snippet emitters — written to output, **never applied**).
- `Gpu.cs` detects the real GPU via PowerShell/WMI (marketing version derived from the WMI string's last 5 digits), simulated fallback.

**Package-session tokens:** `/api/package` loads a manifest and returns a token; `/api/execute` requires it. Tokens live in server memory — a server restart invalidates the wizard's in-flight state.

**Frontend contract:** the five screens are a frozen design contract — the mockups in `docs/design/CleanDriver GPU driver wizard/` plus numbered rulings R1–R10 in `docs/design/nvcleanstall-mockup-prompt.md` (e.g. R3: Back hidden during download; R4: footer hidden in the result state). No new screens/dialogs/controls without an owner ruling. Component names from local manifests are untrusted — escape anything interpolated into `innerHTML` (see `esc()` in `app.js`).

## Governing documents (read before non-trivial work)

The docs chain is authoritative and ordered: `specs/nvcleanstall/spec.md` (scope cut, FRs, ACs, stack pin) → `specs/nvcleanstall/progress.md` (milestone status) → `specs/nvcleanstall/parity.md` (verified feature-parity table) → `docs/gaps_analysis.md` (**the closed task register for current work** — its §4 out-of-scope fence is absolute) → `prompts/` (cold-session kickoff prompts). `CONTRIBUTING.md` binds process: one slice = one worktree (`../nvcleanclone-<name>`) = one branch (`feat/<slug>` / `docs/<slug>`) = one PR; **never merge — the owner merges**.

## Hard boundaries

- **Safety:** never execute downloaded installers, never install drivers, never write the live registry. Individual gaps in `docs/gaps_analysis.md` may lift a *named* part of this (e.g. GAP-02 permits downloading bytes to disk, still never executing); nothing else does.
- **IP:** original branding only ("CleanDriver"). No NVIDIA/TechPowerUp logos, trademarks, or copied assets; NVIDIA product names appear only as nominative labels in mock data (ruling R10).
