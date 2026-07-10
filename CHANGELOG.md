# Changelog

All notable changes to this repository. Entries are dated (no semantic versions yet — the
project has no releases). Nothing aspirational: **an entry exists only for merged work**,
added when its PR merges. Open work is not recorded here — `gh pr list` is the source of
truth for in-flight PRs, and is accurate for free. (This file previously kept an
"Unreleased (open PRs)" section; it went stale on every merge because nothing in the merge
flow updated it, and it caused a merge conflict on three consecutive PRs — CL-1. Removed
2026-07-09.)

## 2026-07-10

- **HARD-06 — catalog-source marker wording** (`9516d85`, PR #19, merged): `/api/catalog` gains an
  additive `sourceDetail` field, present only when `source == "mock"`, carrying one of three reason
  buckets — `mock mode` (deliberate `--mock-catalog`/env, decided at the factory via a composition-level
  `DeliberateMockCatalogProvider`), `GPU not matched` (simulated / not in the pfid table), `live lookup
  failed` (no rows / transport error). The wizard's existing `#catalog-source-note` renders the reason in
  the unchanged sentence frame via `textContent`; `index.html` is untouched, so today's static text is the
  byte-identical no-field fallback. The four detailed fallback strings still reach the log verbatim.
  `ICatalogProvider.GetCatalog` is a default interface member, so every existing implementer compiles
  unedited. 102/102 tests (95 + 7). **This closed wave B** — HARD-03…06 landed with zero required review
  findings across four reviews.
- **HARD-05 — download redirect validation** (`a466dc2`, PR #18, merged): the real-download client no
  longer follows redirects invisibly. `AllowAutoRedirect` is off; the worker follows 3xx itself,
  validating **each hop's host** against the same single `.nvidia.com` predicate as the initial URL
  (`IsNvidiaHost`) **before any request reaches the target**, resolving relative `Location` headers
  against the issuing hop, bounded by `MaxRedirectHops = 5` (recorded amendment; exceeding it fails with
  `too many redirects (exceeded 5 hops)`). Mid-chain failures reuse the existing `.part`-cleanup path.
  Streaming half byte-identical (11 pins unedited). Live-verified: 337 MB @ 99 MB/s with the flag off.
  Known scope edge (candidates #39): an on-allowlist hop may still downgrade `https → http`.
- **HARD-04 — `pollDownload` handles server-initiated cancel** (`3188547`, PR #17, merged): a
  server-set `"cancelled"` job status (real downloads cancelled via `POST /api/download/{id}/cancel`)
  previously matched no terminal branch in `pollDownload` — the wizard polled every 150 ms forever with
  the progress bar frozen. Now it returns silently to Screen 1, mirroring the user-cancel path per ruling
  R3 (the `failed` branch keeps its alert; a cancel is not news). Four added lines in `app.js`, nothing
  else. First slice under the declared no-test-surface gate (`wwwroot/` has no JS runner): red and green
  are browser transcripts (+20 polls/3 s before; 1 poll, Screen 1, 0 alerts after), C# suite untouched.
- **HARD-03 — `Gpu.QueryWmi` hang guard** (`69a782f`, PR #16, merged): `QueryWmi` read stdout to EOF
  *before* its 5 s `WaitForExit`, so a wedged powershell holding the pipe open hung the first
  `/api/system` (and `/api/catalog`) call forever. `Gpu.ReadBounded(stdout, waitForExit, kill, budget)`
  bounds the **read** and hands `waitForExit` only the remaining budget — one ~5 s worst case, not two
  stacked; either timeout kills the process and falls back to `Simulated` exactly as the documented
  `WMI → simulated` order promises. Happy-path `GpuInfo` JSON byte-identical; `Detect()` caching intact.
  Recorded amendment: a valid-but-slow (>5 s total) WMI query now yields `Simulated` where it previously
  eventually succeeded (local cold query ~0.47 s, ~10× margin). 92/92 tests (87 + 5).
- **HARD-01 + HARD-02 — outDir hygiene** (`94aac92`, PR #15, merged): rebuilding into the same output
  directory no longer leaves the previous build's payloads and `.reg` snippets on disk.
  `Packages.CleanPreviousBuild` deletes **only** artifacts named by the prior run's own
  `CleanDriver`-stamped manifest (enumerable-by-name, `File.Delete` only — recursive deletion is
  guard-tested absent); foreign files always survive; malformed/foreign manifests are never cleaned and
  never throw. `IsFilesystemRoot` rejects a drive-root `outputPath` at the top of `StartExecute`, before
  any filesystem write, with a named error — verified live against `C:\` (entry count unchanged).
  Recorded amendment: the root check is action-uniform — `install`/`silent` jobs with a root
  `outputPath` now fail by name where they previously ignored the path. 87/87 tests (75 + 12).
- **Hardening register** (`0e974d7`, PR #14, merged): `docs/hardening_register.md` — the successor task
  register to the closed `docs/gaps_analysis.md`. Wave A (HARD-01/02, one slice), wave B (HARD-03…06,
  sequential), DOCS-01 (architect-owned docs polish), §3 amendments A1/A2, and a §5 ledger disposition
  table accounting for every open candidate (landed / deferred-with-trigger / dropped-with-reason).

## 2026-07-09

- **Post-GAP docs refresh** (`b94e3fe`, PR #13, merged, after a two-finding fix round): CHANGELOG's
  stale `Unreleased (open PRs)` section deleted — the CL-1 structural fix; entries land at merge time
  and `gh pr list` owns open work. `specs/nvcleanstall/parity.md` re-graded against the post-GAP tree
  (21 parity / 7 simplified; FEAT-002/005/027 promoted; §3 revised; §4 provenance corrected to "0 → 75
  tests" with the per-PR-vs-post-merge CI distinction). `docs/gaps_analysis.md` STATUS flipped to
  CLOSED with the FEAT-004 → GAP-OUT-1 bookkeeping fixed.
- **GAP-06 — true single-file package output** (`99f53a1`, PR #12, merged): the **Build package** action now
  additionally writes one redistributable archive,
  `output/<version>-cleandriver-package.zip`, containing the customized package tree
  (`manifest.json`, `payload/<selected>.bin`, `install.cmd`, `config.json`, and any
  `.reg` snippets) — containing **exactly the outputs of that build** and nothing else.
  The archive is packed from an explicit file list, never from a walk of the output
  directory: nothing cleans that directory, so a walk archived a previous build's
  leftover payloads while the job reported `done` (D12-F1). The path to the archive is
  normalized before its parent is taken, so an `outputPath` with a trailing separator no
  longer writes the archive inside the directory it archives, and a drive root fails by
  name rather than with a `NullReferenceException` (D12-F2). The directory output stays
  for inspection and `extract` is unchanged. Packing lives in `Packages.WriteZip`, called
  from the package branch of `Jobs.StartExecute`; the archive is written **beside** the
  output directory, never inside it. Two owner rulings and the D12 review resolution are
  recorded in the GAP-06 register entry. The bundled `install.cmd` remains the simulated,
  non-executing installer: nothing is executed, extracted, or installed, nothing on disk
  is ever deleted, and the `NoExecutionGuardTests` safety pin is unedited and green.
- **AI fix-loop workflow** (`docs/ai-fix-loop-workflow.html`; `310c83b`, PR #11, merged): a self-contained HTML
  reference documenting the third side of the AI→AI loop — what a cold *implementer* session
  does when a reviewer hands back a rejected PR. Covers the anatomy of a rejection (verdict +
  implementer handoff comment), the C0–C7 checkpoint spine as the only context-clear-durable
  state, the four-tier punch list (Required / Optional / REFUTED / ACCEPTED) and the fence
  that operationalizes it, the three-tier consultation ladder (`advisor` primary →
  `spec-driven-collaboration` fallback → owner ruling binding), the HALT → ruling → amendment
  path, red-first when the red is a merge conflict, the `F<n>:` commit and force-push
  conventions (handoff-granted, not repo law), and the closing report + delta re-review.
  Worked from the real PR #9 / `D9-F1` round and the earlier `G2-F1` round on PR #4,
  including its two disclosed deviations. Docs only; no production code.
- **`docs/ai-handoff-workflow.html`** (`e0f160f`, PR #10, merged): documents the **builder** side of this
  repo's AI→AI handoff loop — how a cold session ingests a kickoff prompt, orients against
  the governing docs chain, consults before designing (the three-tier ladder: `advisor` →
  `spec-driven-collaboration` when advisor is unavailable → **binding** architect rulings),
  HALTs via AskUserQuestion rather than guessing, then runs strict TDD, the three gates, one
  PR, and STOPs. Illustrated with real evidence from GAP-01…GAP-05 and the CI whitespace
  fix. Descriptive only — `CONTRIBUTING.md` and `docs/gaps_analysis.md` remain normative.
- **Architect PR-validation workflow** (`docs/ai-review-workflow.html`; `e102a7c`, PR #9, merged): a
  self-contained HTML reference documenting how a PR written by a cold AI builder session
  is validated before merge — the eight review stages (CI triage → isolated worktree →
  gate re-run + live exercise → finder pass → adversarial verify → verdict → feedback loop
  → owner report), the feedback mechanisms back to the builder (three-tier punch list,
  paste-able fix prompt, delta re-review, candidates list), a case ledger of the reviews of
  PRs #3–#6, and a copy-paste reviewer's checklist. Docs only; no production code.
- **GAP-05 — Signature-rebuild and driver-telemetry honesty** (`5e47617`, PR #8, merged): the
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
- **ci: whitespace-check fix** (`c9b3ade`, PR #7, merged): the `main` CI failed on every merge at the
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
