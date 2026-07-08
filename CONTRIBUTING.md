# Contributing — mandatory process

These rules bind every change to this repository, human or AI. PRs that skip them get
returned.

## TDD (without exception)

- Red → green → refactor: every behavior change starts with a failing test that
  specifies it; then the minimum implementation; then cleanup.
- Bug fixes start with a reproducing test that fails on the old code.
- Acceptance tests exercise **production wiring and default code paths**, not
  test-only variants. Network-dependent logic is tested against recorded fixtures via
  an injectable `HttpMessageHandler`; live-network checks live in scripted
  verification, not unit tests.
- Existing tests are back-compat pins: editing one to make it pass is a design smell —
  each such edit must be individually disclosed and justified in the PR body.
- Test project: `nvcleanstall/CleanDriver.Tests/` (xunit, `net10.0-windows`). If it
  does not exist yet, scaffolding it is the first commit of the first wave that needs it.

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
