# hardening_register.md — CleanDriver Post-GAP Hardening Register

| | |
|---|---|
| **STATUS** | **OPEN** — wave A unbuilt. Successor to `docs/gaps_analysis.md` (CLOSED 2026-07-09), seeded from the review candidates ledger per that register's closure rule: every candidate below either lands here, is deferred with a destination, or is dropped with a reason. |
| **Date** | 2026-07-09 |
| **Baseline** | `main` @ `b94e3fe` (GAP-01…06 merged; 75/75 tests green; CI honest since PR #7; CHANGELOG records merged work only — CL-1 rule). |
| **Authority** | Authoritative, closed task list for the defect/hardening items below. Items not in §2 are out of scope (§4), without exception. The predecessor's §4 fence (GAP-OUT-1…4) is carried forward verbatim and remains absolute. |
| **Pins** | Maintainer-pinned 2026-07-09: **D1** outDir staleness → manifest-scoped clean (rejected: unique-default-dir — leaves user paths defective; refuse-non-empty — no deletion but every rebuild needs manual cleanup; document-only). **D2** `outputPath` → reject filesystem roots up front (rejected: system-dir denylist — maintenance burden/false positives; leave-as-is). **Wave scope**: candidates #8, #5, #4, #1 all join wave B. |

## 1. Pre-flight

None. The test harness exists and is green at baseline (PF-1 of the predecessor is long satisfied). Every slice below inherits `CONTRIBUTING.md`'s gates verbatim: build, test, `git diff --check`, strict TDD with red-first evidence.

## 2. Register

### Wave A — one slice, one PR: `feat/hard-01-outdir-hygiene`

**HARD-01 — Manifest-scoped clean of a previous CleanDriver build in `outDir` (pin D1; candidate #28).**
`extract`/`package` write into `output/{action}-{version}` (no timestamp) or any user-supplied path, and nothing ever cleans it — a rebuild with a smaller selection leaves the previous run's payloads and `.reg` snippets on disk while `manifest.json` disowns them (verified pre-existing at `310c83b`; the archive is already immune since PR #12).
- **Behavior (the pin, exactly):** before writing, iff `outDir` contains a `manifest.json` whose `customizedBy == "CleanDriver"`, delete exactly the artifacts that prior manifest declares — its listed component payloads under `payload/`, the `tweak-<id>.reg` set derivable from its recorded tweaks, and `install.cmd` / `config.json` / `manifest.json` themselves — then write fresh. **Foreign files always survive** (a `leftover.txt` is never touched). A directory without a CleanDriver manifest is written into exactly as today: no cleaning, no refusal. Applies to both `extract` and `package`.
- **Hard boundary:** no recursive delete, ever. `Directory.Delete(outDir, true)` and any equivalent whole-tree removal are forbidden — `outputPath` is user-supplied and a recursive clean can destroy a user's files (owner ruling, D12 round, reaffirmed in pin D1). Deletion is enumerable-by-name only.
- **Seams:** `nvcleanstall/Lib/Packages.cs` (owns `WriteCustomized` and manifest reading) for the clean; `nvcleanstall/Lib/Jobs.cs` `StartExecute` extract/package branch for orchestration + a log line naming what was cleaned. Verify both against real code at prompt time.
- **AC:**
  1. A test builds into a dir, rebuilds with a smaller selection + different tweaks into the same dir, and asserts the **directory** (not just the archive) contains only the new build's payloads/`.reg` — and that a planted foreign file survives both builds.
  2. A test plants a *non-CleanDriver* `manifest.json` (no `customizedBy` or wrong value) plus stray files and asserts nothing is deleted.
  3. A grep/shape guard asserts no `Directory.Delete` with `recursive: true` is reachable from `StartExecute` (same style as `NoExecutionGuardTests`).
  4. The job log carries one line naming the cleaned files when a clean ran, and no such line otherwise.
  5. Test-quality riders (test-only, no production surface): rebuild-scenario assertions use **literal expected paths**, never production's own path arithmetic (candidate #29); the archive-content test additionally asserts the GAP-05 honesty markers (`signatureSimulated`, `driverTelemetrySimulated` when applicable) are present **inside** the archived `manifest.json`/`config.json`, not just the staging dir (candidate #12).

**HARD-02 — Reject a filesystem-root `outputPath` before any write (pin D2; candidate #31).**
`/api/execute` accepts any `outputPath`; a drive root (`C:/`) would have `WriteCustomized` writing `payload/`, `manifest.json`, `install.cmd` directly into `C:\` before PR #12's zip-step guard ever runs.
- **Behavior:** at the top of `StartExecute`, before any filesystem write: if the normalized `outputPath` is a filesystem root (its parent is null), fail the job with a named error (`outputPath is a filesystem root; choose a folder`) and write nothing. All other paths remain allowed — it is the user's disk. `Packages.ZipPathFor`'s existing refusal stays as defense in depth.
- **AC:**
  1. A test drives `StartExecute` for `extract` and `package` with a root `outputPath` (assert at the path-computation/early-failure level per the D12 AC-7 precedent — never run a real build against `C:\`) and asserts `job.Status == "failed"`, the named error, and that **no file was created**.
  2. The existing happy-path and trailing-separator tests still pass unchanged (back-compat pins).

Wave A is **one PR** (owner: "the D1+D2 slice stays tight"); HARD-01 and HARD-02 share the `StartExecute` seam region and their tests share fixtures.

### Wave B — after wave A merges; each item its own branch/PR, sequential

**HARD-03 — `Gpu.QueryWmi` hang guard (candidate #8).** `StandardOutput.ReadToEnd()` runs before the 5s `WaitForExit`; a wedged powershell holding stdout open hangs the first `/api/system` call indefinitely. Move to an async/timed read so the existing 5s budget bounds the whole call; on timeout, fall back to simulated exactly as the documented `WMI → simulated` order already promises. `GpuInfo` shape unchanged (back-compat pin). AC: a fake long-running process (or injected stream that never closes) yields the simulated fallback within the budget; the single-NVIDIA happy path is byte-identical.

**HARD-04 — `pollDownload` handles server-initiated `cancelled` (candidate #5).** The frontend stops polling only on its own cancel flag; a server-side `Status=="cancelled"` spins forever. `nvcleanstall/wwwroot/app.js` `pollDownload`: treat `cancelled` from `/api/jobs/{id}` as terminal → return to Screen 1 per ruling R3's existing cancel behavior. No new UI elements (design contract intact). AC: headless + curl transcript — start a download, cancel the job server-side via the existing cancel route, assert the poll loop terminates (observable via server logs/network quiescence) and a subsequent wizard run works.

**HARD-05 — Download redirect validation (candidate #4).** The GAP-02 client checks the `.nvidia.com` host on the initial URL only, then follows redirects anywhere. Either disable `AllowAutoRedirect` and follow manually with a per-hop host check, or validate each hop's host against the same allowlist. Failure mid-chain cleans up the `.part` file exactly as other failures do (existing pins). AC: fake-handler tests — a redirect chain ending off-allowlist fails with a named error and no residue; an on-allowlist chain succeeds; the no-redirect happy path is byte-identical.

**HARD-06 — Catalog-source marker wording (candidate #1).** The mock-catalog marker always says "(GPU not matched)" even when mock mode was chosen deliberately (`--mock-catalog` / `CLEANDRIVER_MOCK_CATALOG=1`). Carry the fallback *reason* through the provider seam and render it: deliberate mock → "sample catalog (mock mode)"; GPU unmatched → today's text; live failure → "sample catalog (live lookup failed)". Text-only change inside the existing marker element — no new screens/dialogs/controls, so no new design ruling required (rulings R1–R10 untouched). AC: `/api/catalog` carries the reason; three provider states render the three texts (curl assertions); `"source":"mock"` consumers unchanged (back-compat pin).

### DOCS-01 — workflow-docs polish (architect-authored; one docs PR, no builder)

Closes candidates **#13, #14, #18, #19, #20, #23, #24, #25** across the three `docs/ai-*-workflow.html` pages: extend the case ledger past PR #6 (+#7–#13 and the HALTs); add the enforcement-layer-vs-judgement section; mark the two gitignored paths session-local (×2 pages); source-or-soften the §7 disclosure claim; fix the §1 numbering, the TOC/heading mismatch, and the `<code>` paraphrase-as-quotation. Docs-only; TDD exemption stated per the recorded amendment (see §3).

## 3. Recorded amendments (inherited rulings, restated once)

- **A1 (from GAP-01 review, D1):** `/api/sample-package` legitimately keeps a direct `Catalog.Releases()` read (`Api.cs:35`) — sample packages are mock-only; the predecessor's "no second path to catalog.json" wording carries this carve-out.
- **A2 (from PR #10 review, D1):** docs-only PRs (`docs/<slug>` touching only `.md`/`.html`) satisfy the strict-TDD gate **vacuously** — TDD binds behavior changes and a docs page has no unit-test surface. The exemption must be *stated* in the PR body; build/test/`diff --check` still run.

## 4. Explicitly OUT OF SCOPE

Everything in the predecessor's §4, verbatim and still absolute: **GAP-OUT-1** (real NVIDIA package parsing — owns FEAT-004), **GAP-OUT-2** (executing/installing any driver — permanent), **GAP-OUT-3** (applying tweaks to the live registry), **GAP-OUT-4** (native OS file pickers). Plus: no new UI screens/dialogs/controls without an owner ruling (HARD-04/06 are behavior/text inside existing elements).

## 5. Deferred / dropped candidates (the ledger, closed out)

| Candidate | Disposition |
|---|---|
| #3 provider sync-over-async | **Deferred** — acceptable for a single-user localhost app; trigger: the provider gains concurrent callers. |
| #6 `Jobs.StallTimeout` static seam | **Deferred** — trigger: test parallelism. |
| #9 artifact correlation keys on version alone | **Deferred (wave-B candidate, unpinned)** — real but narrow (`src.source.Kind == "catalog"` gate); offer at the next pin round rather than smuggle in now. |
| #10 `ci.yml` root-commit fallback | **Deferred** — unreachable on `main` today; trigger: history rewrite or repo template reuse. |
| #11 qualifier literals ×4 → `const` | **Deferred (wave-B candidate, unpinned)** — cosmetic dedup in `Jobs.cs`; natural rider on whichever slice next touches those branches. |
| #15 guard substring-matching bug | **Dropped — fixed outside the repo** (2026-07-09): both hooks now match by token position, with a 17-case regression suite; lives in the skill tree, not this repo. |
| #27 verbatim-quote verification method | **Dropped — process lesson**, codified in reviewer practice; not repo work. |
| #30 same-version concurrent zip race | **Deferred** — PLAUSIBLE, unreproduced, single-user app; trigger: any multi-session deployment. |
| #32 prompt-prescribed unforced production line | **Dropped — process lesson**, codified in the prompt-author's template (build prompts may sketch shapes, never prescribe production lines no AC forces). |

Candidates #1, #4, #5, #8, #12, #28, #29, #31 land above (§2). #2, #7, #16, #17, #22, #26 were closed by earlier PRs. #13, #14, #18–#21, #23–#25 land in DOCS-01 (#21 as amendment A2).

---
**Provenance.** Produced by the standing architect session (Claude Code), 2026-07-09, from the review candidates ledger and maintainer pins D1/D2/wave-scope of the same date. Source state: `main` @ `b94e3fe`. Consumers: cold builder sessions via `/implement-workflow`, one wave-A prompt first; reviews via `/review-workflow`; the owner merges every PR.
---
