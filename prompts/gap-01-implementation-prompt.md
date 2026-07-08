# GAP-01 Implementation Handoff — Live NVIDIA Version Lookup

You are an implementation agent starting cold in `/mnt/c/Projects/nvcleanclone` (git repo,
origin = `https://github.com/bankielewicz/nvcleanclone.git`; on Windows the same checkout is
`C:\Projects\nvcleanclone`). You have no prior context; everything you need is in the
repository. Execute **GAP-01 only**, exactly as specified by **`docs/gaps_analysis.md`** — the
authoritative, closed task list. Read it in full before writing any code, then read
`CONTRIBUTING.md` (mandatory process: TDD, gates, workflow, invariants), then the specific
sections the register cites for GAP-01: `specs/nvcleanstall/parity.md` §2 (FEAT-002/003 rows),
`specs/nvcleanstall/spec.md` §1 + §6, and the code seams named in the GAP-01 entry
(`nvcleanstall/Lib/Catalog.cs`, `nvcleanstall/Lib/Gpu.cs`, `nvcleanstall/Api.cs`,
`nvcleanstall/Lib/Jobs.cs`, `nvcleanstall/Lib/Models.cs`).

## Scope discipline

- `docs/gaps_analysis.md` §3 GAP-01 is your complete task: the `ICatalogProvider` seam, the
  `MockCatalogProvider` and `NvidiaCatalogProvider` implementations, GPU→pfid resolution with
  mock fallback, provider selection wiring (`--mock-catalog` / `CLEANDRIVER_MOCK_CATALOG=1`),
  the `source` field on `/api/catalog`, the injectable HTTP handler, and the PF-1 test-harness
  scaffold (`nvcleanstall/CleanDriver.Tests/`, xunit, `net10.0-windows`) as your FIRST commit.
- **Do NOT implement GAP-02 through GAP-06** even though you will read them in the register.
  Do NOT touch anything in register §4 (out of scope, without exception): no real download of
  driver bytes, no execution/extraction of anything, no registry writes, no new UI screens or
  controls. The only UI-visible change GAP-01 permits is the "sample catalog (GPU not
  matched)" marker in the mock-fallback state, per its AC-5.
- Nothing aspirational: implement what the register says, to its ACs, and stop. New ideas go
  in the PR body as notes for the owner, never into code.
- The five wizard screens are a frozen design contract (`docs/design/` + rulings R1–R10 in
  `docs/design/nvcleanstall-mockup-prompt.md`). Screen 1's dropdown must render live rows with
  the exact existing row structure (version / WHQL-or-Beta tag / date / annotations).

## Workflow (mandatory, per CONTRIBUTING.md)

1. Preconditions — verify each; if any fails, HALT (see Halt rules):
   - `git fetch origin` succeeds and `docs/gaps_analysis.md` exists on `origin/main` (if it
     does not, the register PR is unmerged — HALT).
   - `origin/main` contains commit `c94de87` (`CONTRIBUTING.md` present).
   - The .NET 10 SDK builds the app: `"/mnt/c/Program Files/dotnet/dotnet.exe" build
     nvcleanstall/CleanDriver.csproj` succeeds from WSL (or `dotnet build` from a Windows
     terminal). Note the environment quirk in register §5: inline WSL env vars do not reach
     launched Windows `.exe` processes.
2. Create the worktree: `git worktree add -b feat/gap-01-live-catalog
   ../nvcleanclone-gap01 origin/main` and work only inside it.
3. **TDD without exception** (CONTRIBUTING.md): first commit scaffolds the test project
   (PF-1) with at least one passing placeholder test; every behavior after that is
   red → green → refactor. Acceptance tests run against production wiring and default code
   paths. Existing behavior you must not change is pinned by tests you write FIRST (e.g.
   mock-catalog byte-identity, GAP-01 AC-3).
4. Network code: all HTTP through one injectable `HttpClient`/`HttpMessageHandler`; unit
   tests use a recorded fixture of NVIDIA's `DriverManualLookup` JSON (capture it once via
   the PF-2 probe below and commit it under `nvcleanstall/CleanDriver.Tests/fixtures/`);
   5-second hard timeout; every failure path falls back to the mock provider.
5. PF-2 probe (register §1): script one `curl` (or `HttpClient` console call) against
   NVIDIA's `AjaxDriverService.php?func=DriverManualLookup` endpoint with a real pfid/osid,
   save the raw JSON as the test fixture, and paste it (or its unreachability report) into
   the PR body. This is evidence, not a unit test; it must not run inside `dotnet test`.
6. Gates before pushing (all green, run from the worktree):
   `dotnet build nvcleanstall/CleanDriver.csproj` ·
   `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` ·
   `git diff --check`.
7. Self-review before the PR: every GAP-01 AC (1–5) demonstrably met with per-AC evidence;
   any scope deviation prominently disclosed in the PR body; the changed path exercised
   live at least once (`--headless` run + `curl /api/catalog` in both live and
   `CLEANDRIVER_MOCK_CATALOG=1` modes — paste both outputs); no stray processes or files
   left (`taskkill.exe /F /IM CleanDriver.exe` if you started it).
8. Push the branch and open ONE PR against `main` titled
   `GAP-01: live NVIDIA version lookup behind catalog-provider seam`, body containing: the
   GAP-01 AC checklist with evidence per item, the PF-2 raw JSON (or unreachability note),
   the live/mock curl outputs, and any deviations. Use `gh pr create`; if `gh` is
   unavailable or unauthenticated, push the branch and give the owner the compare URL.
9. **Do NOT merge. Do NOT start GAP-02.** Report the PR URL and STOP. Expect a punch-list
   review comment (read via `gh pr view <n> --comments`); implement required findings on the
   same branch, one commit per finding, `F<n>:`-prefixed, re-run gates, post a checklist
   comment mapping finding → commit → test, and STOP again.

## Halt rules

- **HALT if you need clarification on any requirement or detect any ambiguity** — including
  any contradiction between `docs/gaps_analysis.md`, the specs, and the code, or an NVIDIA
  response shape that does not match the register's description. Do not guess, do not pick
  an interpretation silently, do not fill gaps with plausible content.
- To HALT: stop working and **use AskUserQuestion**. State the ambiguity precisely
  (file:line for each side), your candidate interpretations, and which you would pick and
  why. Wait for the answer before proceeding.
- Environment blockers (SDK missing, push rejected, endpoint unreachable when an AC needs
  it, worktree path unwritable) are HALT conditions too — report exactly what failed; never
  work around them by weakening or skipping tests.

## Technical anchors (verify in-repo before relying on them)

- Stack: `nvcleanstall/CleanDriver.csproj` — `net10.0-windows`, WinForms + WebView2 +
  `Microsoft.AspNetCore.App` framework reference; zero other packages at baseline.
- Run modes: `dotnet run` (native window) / `dotnet run -- --headless` (server only,
  `http://localhost:4780`) — see `nvcleanstall/README.md`.
- Single-owner seams to reuse, never fork: `Catalog.Releases()`
  (`nvcleanstall/Lib/Catalog.cs:9`) is the only mock-catalog reader; `/api/catalog`
  (`nvcleanstall/Api.cs`) is the only catalog route; `Gpu.Detect()`
  (`nvcleanstall/Lib/Gpu.cs`) is the only GPU source. Your `ICatalogProvider` becomes the
  new single owner of catalog reads — route BOTH implementations through it and leave no
  second path to `catalog.json`.
- `Release` model lives in `nvcleanstall/Lib/Models.cs`; adding fields (e.g. `DownloadUrl`,
  `Source`) must be additive/nullable so mock data deserializes unchanged.
- JSON is camelCase over the wire (`Json.Web` in `nvcleanstall/Lib/Models.cs`); the frontend
  (`nvcleanstall/wwwroot/app.js`) consumes `/api/catalog` at `boot()` — keep the response a
  superset of today's shape.

Begin now. Your first actions: read `docs/gaps_analysis.md` in full, verify the
preconditions, then open the worktree and scaffold the test project.

---
**Provenance.** Produced by **Claude Code** (Claude Fable 5), session
`64712235-4ddd-4f66-86a1-e0d233ba7258`, 2026-07-08 UTC. Source state: `main` @ `c94de87`.
Intended consumer: **a cold Claude Code implementation session**. Governing register:
`docs/gaps_analysis.md` (GAP-01). Return contract: one PR against `main`, never merged;
report PR URL and STOP; HALT via AskUserQuestion on any ambiguity.
---
