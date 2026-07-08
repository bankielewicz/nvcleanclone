# Changelog

All notable changes to this repository. Entries are dated (no semantic versions yet — the
project has no releases). Nothing aspirational: an entry exists only for work that is
merged, or is explicitly marked as an open PR.

## Unreleased (open PRs)

- none

## 2026-07-08

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
