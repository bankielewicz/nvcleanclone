# Contributing — mandatory process

These rules bind every change to this repository, human or AI. PRs that skip them get
returned.

## TDD (strict — without exception)

The discipline, in order, for every behavior change:

1. **Red first.** Write the failing test that specifies the behavior. Run it. Capture
   the failing output — it is required PR evidence (see PR template).
2. **Green.** Write the minimum implementation that passes it. Run the full suite.
3. **Refactor.** Clean up with the suite green.

Enforcement rules:

- **No production code without a failing test that requires it.** If you cannot name
  the test that forced a line of code, the line is out of scope.
- **Commit ordering proves the discipline:** the test lands in the same commit as (or
  a commit before) the implementation it specifies — never after. A PR whose history
  shows implementation-then-tests gets returned without further review.
- **Red evidence in the PR body:** for each behavior, paste the failing-test output
  from step 1 alongside the passing output from step 2. "Tests exist" is not
  evidence; "tests failed, then passed" is.
- Bug fixes start with a reproducing test that fails on the old code — same evidence
  rule applies.
- Acceptance tests exercise **production wiring and default code paths**, not
  test-only variants. Network-dependent logic is tested against recorded fixtures via
  an injectable `HttpMessageHandler`; live-network checks live in scripted
  verification, not unit tests.
- Existing tests are back-compat pins: editing one to make it pass is a design smell —
  each such edit must be individually disclosed and justified in the PR body.
- Test project: `nvcleanstall/CleanDriver.Tests/` (xunit, `net10.0-windows`). If it
  does not exist yet, scaffolding it is the first commit of the first wave that needs
  it, and that scaffold commit must include at least one passing test.

## Gates (each PR independently green)

```
dotnet build nvcleanstall/CleanDriver.csproj
dotnet test  nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj   # once it exists
git diff --check
```

From WSL, `dotnet` is `"/mnt/c/Program Files/dotnet/dotnet.exe"`. Builds require the
.NET 10 SDK on Windows (`net10.0-windows` target).

## Workflow

- One slice = one fresh git worktree = one branch = one PR against `main`.
  Worktrees live in sibling dirs: `../nvcleanclone-<short-name>`.
- Branch grammar: `feat/<slug>` for code, `docs/<slug>` for documents.
- Push the branch, open the PR (`gh pr create`; if `gh` is unavailable, push and give
  the owner the compare URL). **Never merge** — the owner merges every PR.
- Scope deviations from the governing register/spec are disclosed prominently in the
  PR body, never hidden behind green tests.

## Project invariants

- **Safety boundary** (see `nvcleanstall/README.md` and `specs/nvcleanstall/spec.md`):
  the app must never execute downloaded installers, never install drivers, and never
  write the live registry, unless a gap in `docs/gaps_analysis.md` explicitly and
  individually lifts a named part of this boundary with owner sign-off recorded there.
- **IP boundary:** original branding only ("CleanDriver"); no NVIDIA/TechPowerUp
  logos, trademarks, or copied assets. NVIDIA product names may appear only as
  nominative labels for what real driver packages contain (design ruling R10,
  `docs/design/nvcleanstall-mockup-prompt.md`).
- UI is a design contract: the five wizard screens follow the approved mockups in
  `docs/design/CleanDriver GPU driver wizard/` plus rulings R1–R10. No new screens,
  dialogs, or controls without an owner-approved ruling.
