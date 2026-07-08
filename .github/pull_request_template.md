# PR

## What & why

<!-- Which register gap / spec item this delivers. Cite GAP/FEAT/AC ids. -->

## TDD evidence (required — CONTRIBUTING.md "TDD (strict)")

<!-- One row per behavior. "Red output" = the failing-test output captured BEFORE the
     implementation existed; "Green output" = the same test passing after. A PR without
     red-then-green evidence, or whose commit history shows implementation before its
     tests, gets returned without further review. -->

| Behavior | Test (class::method) | Red output (before) | Green output (after) |
|---|---|---|---|
| | | | |

- [ ] Every production change is forced by a test committed with or before it
- [ ] No existing test was edited — or every edit is disclosed and justified below
- [ ] Bug fixes include a reproducing test that failed on the old code

## Gates

- [ ] `dotnet build nvcleanstall/CleanDriver.csproj`
- [ ] `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`
- [ ] `git diff --check`

## Deviations from the register/spec

<!-- Disclose every deviation prominently; "none" if none. Hidden deviations behind green
     tests are a returnable offense. -->

## Live exercise

<!-- Paste the command(s) + output proving the changed path was exercised against the
     running app (e.g. --headless + curl), not only via unit tests. -->
