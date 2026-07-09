# Changelog

All notable changes to this repository. Entries are dated (no semantic versions yet — the
project has no releases). Nothing aspirational: an entry exists only for work that is
merged, or is explicitly marked as an open PR.

## Unreleased (open PRs)

- **GAP-05 — Signature-rebuild and driver-telemetry honesty** (PR #8, open): the
  simulated "rebuild digital signature" step and the `driver-telemetry` tweak stop
  overclaiming. Additive, null-omitted markers ride on the artifacts that record the
  tweak — `signatureSimulated: true` alongside the unchanged `signature: "rebuilt"`
  (back-compat pin) on the install/silent **receipt** and the customized **manifest**;
  `driverTelemetrySimulated: true` on the receipt, manifest, and build-package
  **config.json** — so no output can be mistaken for a really-signed/-patched package.
  Logs gain per-context qualifiers (`… no real signing performed` / `Patching driver
  telemetry endpoints… (simulated — no real patching performed)`). Realizes
  FEAT-017/FEAT-022. Seams: `Jobs.StartExecute` + `Packages.WriteCustomized`. Includes a
  register amendment (`docs/gaps_analysis.md` GAP-05) recording the owner's ruling that
  resolved the "any artifact they write" ambiguity (markers on existing artifacts; no
  standalone note file).
- **ci: whitespace-check fix** (PR #7, open): the `main` CI failed on every merge at the
  whitespace step because `git diff --check HEAD~1 HEAD` ran against a shallow (depth-1)
  clone (`HEAD~1` unknown), and a `|| echo` wrapper masked the git error as a whitespace
  failure. Fetches full history, computes an honest base per event, drops the masking
  wrapper, and also gates `pull_request` (per CLAUDE.md). No real whitespace errors existed.

## 2026-07-08

- **`6ec9594` (PR #6, merged) — GAP-04: install/silent simulation fidelity from the real
  download.** The (still simulated) install/silent run reflects the **real** downloaded
  artifact from GAP-02: the receipt records the installer's on-disk path (`driverFile`)
  and exact size (`driverFileBytes`) when the driver came from the live path, and the log
  carries a real-artifact line. The **auto-reboot** flag is honored in the log (reboot
  *allowed but not performed — simulated*; FEAT-011 "never reboots"), backed by a
  shape-without-function guard that no reboot/OS-restart API is invoked. Correlation is
  server-side (`Jobs.DownloadedFile`); mock-path receipts stay byte-identical. Realizes
  FEAT-011/FEAT-024/FEAT-025. Seam: `Jobs.StartExecute` + `/api/execute`.

- **`65b7fa0` (PR #5, merged) — GAP-03: real GPU detection hardening.** Extracted the
  WMI-line→`GpuInfo` parsing out of the live `powershell` call into pure, unit-testable
  seams (`Gpu.ParseVideoControllers` / `DetectFrom`) with deterministic multi-GPU NVIDIA
  selection and a documented `WMI → simulated` fallback; hardened `MarketingVersion` so a
  non-numeric driver string surfaces the raw value instead of a fabricated version. No
  `GpuInfo` shape change (back-compat pin). Realizes `parity.md` FEAT-001.
- **`4aba9d0` (PR #4, merged) — GAP-02: real installer download to disk, never executed.**
  `Jobs.StartRealDownload` streams a live release's `DownloadURL` to
  `output/drivers/<version>-<type>.exe` with real `Content-Length` progress, atomic
  `.part`→final rename, cancel/failure cleanup, max-size + disk-space checks, and an
  NVIDIA-host guard; a no-execution/extraction guard test pins the safety boundary. Live
  path only — the mock path keeps the simulation (byte-identity pin). Realizes FEAT-005.
- **`e8b5d39` (PR #3, merged) — GAP-01: live NVIDIA version lookup behind a catalog-provider
  seam.** Introduced `ICatalogProvider` with interchangeable `MockCatalogProvider` /
  `NvidiaCatalogProvider`; `GET /api/catalog` now returns releases plus a `source`
  (`"live"` | `"mock"`), selectable via `--mock-catalog` / `CLEANDRIVER_MOCK_CATALOG`. All
  HTTP goes through an injectable handler (5s timeout, mock fallback on any failure);
  metadata only, no driver bytes. Realizes FEAT-002/FEAT-003.
- **CI workflows** (`.github/workflows/`): `ci.yml` — Windows-runner build, xunit tests
  (hard gate once `CleanDriver.Tests` exists, warning until then), and a real headless
  smoke test (boots the server, asserts `/api/catalog` serves); `publish.yml` — manual or
  tag-triggered self-contained single-file publish, artifact upload, GitHub Release on
  `v*` tags.
- **Strict TDD enforcement** (`5e6ae8a`): hardened `CONTRIBUTING.md` rules (no production
  code without a failing test, commit ordering proves discipline, red-then-green evidence
  required), matching directive in `CLAUDE.md`, and a PR template
  (`.github/pull_request_template.md`) that demands the evidence table on every PR.
- **`46d9f63` (PR #1, merged)** — Added `docs/gaps_analysis.md` (authoritative gap register:
  6 gaps / 6 sequential PRs closing the 10 simplified features toward real-driver parity,
  with pre-flight gates and an out-of-scope fence) and
  `prompts/gap-01-implementation-prompt.md` (cold-session kickoff for GAP-01, live NVIDIA
  version lookup).
- **`0e3eef5`** — Versioned `CLAUDE.md` (removed from `.gitignore`) so git worktree sessions
  inherit the Claude Code working notes.
- **`c94de87`** — Added `CONTRIBUTING.md`: mandatory TDD (red→green→refactor, reproducing
  test first for bugs), PR gates (`dotnet build`, `dotnet test`, `git diff --check`),
  one-slice-one-worktree-one-PR workflow with owner-only merges, and the project's safety
  and IP invariants.
- **`c704800`** — Initial commit: **CleanDriver**, a functional clone of NVCleanstall.
  - App (`nvcleanstall/`): .NET 10 Kestrel server + WebView2 shell (`--headless` mode for
    verification), five-screen wizard frontend built from approved high-fidelity mockups,
    mock driver catalog (5 releases) with per-version component manifests, simulated
    download/install jobs with logs and receipts, extract-only and build-package outputs,
    tweak catalog with `.reg` snippet artifacts (never applied), dependency-aware component
    selection, presets that persist across restarts, real GPU detection via WMI with
    simulated fallback. Self-contained single-file publish verified (~139 MB exe).
  - Build record (`specs/nvcleanstall/`): research → approved spec (28-feature scope cut,
    16 FRs, 5 ACs) → milestone progress (M1–M5 all done) → parity report (all 5 ACs pass;
    18/28 features at parity, 10 simplified by the mock-driver safety boundary, 0 omitted).
  - Design contract (`docs/design/`): five mockups (light/dark, Fluent-style tokens) plus
    the generating prompt and mockup-review rulings R1–R10.
  - Stack history, recorded in `specs/nvcleanstall/spec.md`: initial Node.js scaffold was
    re-pinned by the owner to .NET 10 + WebView2 (self-contained single exe) before any
    verified behavior existed; Go single-exe was the evaluated runner-up.
