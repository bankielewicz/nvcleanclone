# Studio-only live release is served as an unlabeled WHQL "Latest" Game Ready release

| Field | Value |
|---|---|
| **Slug** | `studio-only-release-listed-as-game-ready` |
| **Severity** | Medium (material incorrectness: the wrong driver type becomes the default download) |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness) |
| **Area** | `nvcleanstall/Lib/NvidiaCatalogProvider.cs` (`Parse`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | Related to GAP-F01, but a different defect. The register (§2 capability table, row "Game Ready/Studio and desktop/notebook distinction") records only the *same-version* collapse: Studio and Game Ready rows that share a version are reduced to the Game Ready row. This bug runs the other way. A Studio row whose version has **no** Game Ready twin is kept and served as an ordinary WHQL release. GAP-F01's model (distinct `(version,type,platform)` releases) would eventually replace this code, but GAP-F01 is blocked on a UI ruling. Until it lands, the provider breaks its own documented "Game-Ready only" contract. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R08** — Catalog and GPU detection correctness (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`NvidiaCatalogProvider.Parse` claims to collapse NVIDIA's rows "down to the Game-Ready build". What it actually does is group rows by version and prefer the Game Ready row *within* each group. That removes a Studio row only when the same version also has a Game Ready row. A Studio-only version (`IsCRD == "1"` with no Game Ready twin) survives. It gets `Channel = "WHQL"`, keeps its Studio (`-nsd-`) installer URL, and is sorted with the Game Ready releases. When it is the newest row, it becomes `releases[0]`, so the UI marks it "Latest", selects it by default, and downloads it as `<version>-whql.exe`.

## Evidence
- `nvcleanstall/Lib/NvidiaCatalogProvider.cs:76-77`. The documented contract:
  ```csharp
  // Parses IDS[].downloadInfo rows, collapses the Game-Ready/Studio pair NVIDIA
  // returns per version down to the Game-Ready build, newest version first.
  ```
- `NvidiaCatalogProvider.cs:92-102`. The Studio flag is computed, then used only as a sort preference. The row is always added, and the channel comes from `IsBeta` alone (`IsWHQL` is never read):
  ```csharp
  bool isBeta = Str(info, "IsBeta") == "1";
  bool isCrd = Str(info, "IsCRD") == "1"; // Creator/Studio Ready Driver
  rows.Add((new Release
  {
      Version = version,
      Channel = isBeta ? "Beta" : "WHQL",
      ...
      DownloadUrl = url,
      Source = "live",
  }, gameReady: !isCrd));
  ```
- `NvidiaCatalogProvider.cs:105-109`. The collapse is per version group, so a group that contains only a Studio row returns that Studio row:
  ```csharp
  return rows
      .GroupBy(x => x.rel.Version)
      .Select(g => g.OrderByDescending(x => x.gameReady).First().rel)
      .OrderByDescending(r => TryVersion(r.Version))
      .ToList();
  ```
- The recorded response `nvcleanstall/CleanDriver.Tests/fixtures/nvidia-drivermanuallookup.json` shows that this exact query returns Studio rows (`"Name":"NVIDIA%20Studio%20Driver"`, `"IsCRD":"1"`, `"IsWHQL":"0"`, `…-nsd-dch-whql.exe`). It also shows that versions do not have to come in pairs: `610.74` has a Game Ready row and no Studio twin, which is the mirror image of the case here. A Studio row is flagged `IsWHQL "0"` by NVIDIA, yet CleanDriver labels it `WHQL`.
- Consumers that make the leaked row the default:
  - `wwwroot/app.js:112,120`: `releases[0]` gets the "Latest" badge.
  - `app.js:549`: `state.version = state.releases[0]?.version` makes it the default selection.
  - `Lib/Jobs.cs:286-287`: the file is saved as `{release.Version}-{TypeTag(Channel)}.exe`, i.e. `611.05-whql.exe` for a Studio installer.
- `docs/gaps_analysis.md:16` already named the risk: "GAP-01 must model them explicitly; 'latest' is ambiguous without them."

## Impact
- If NVIDIA's newest row for the user's GPU is a Studio-only version, CleanDriver's default path does the following:
  - Screen 1 shows the Studio build as the "Latest" WHQL release.
  - Clicking Next downloads the Studio installer, saves it under a Game Ready-style name, and records it in the receipt.
  - The user wanted the Game Ready build the provider promises and never learns a Studio driver was substituted.
- Older Studio-only versions also appear in the version list as ordinary WHQL entries, mixed in with the Game Ready ones.
- Who is affected: every live-catalog user (the default mode) whose 10-row lookup window holds a Studio version with no Game Ready twin. NVIDIA's Studio branch has historically often carried its own version numbers. The fixture shows the response does not pair versions by construction. So this is an ordinary data shape, not an exotic one.
- Safety is not affected: nothing is executed. The harm is the wrong artifact, presented as the right one.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/NvidiaCatalogProviderTests.cs`. It uses the same inline-body style as the existing `GetReleases_MapsBetaFlag_ToBetaChannel`, with row fields shaped like the recorded fixture:

```csharp
// A Studio (IsCRD == "1") row whose version has NO Game Ready twin must not survive the
// "collapse down to the Game-Ready build": it would otherwise become releases[0]
// (the UI's "Latest" + default selection) labeled WHQL.
[Fact]
public void GetReleases_StudioOnlyVersion_IsNotServedAsAGameReadyRelease()
{
    const string body = """
    { "Success":"2", "IDS":[
      { "downloadInfo": {
          "Success":"1", "Name":"NVIDIA%20Studio%20Driver", "Version":"611.05",
          "IsBeta":"0", "IsWHQL":"0", "IsCRD":"1",
          "ReleaseDateTime":"Tue Jul 14, 2026", "DownloadURLFileSize":"978.90 MB",
          "DownloadURL":"https://us.download.nvidia.com/Windows/611.05/611.05-desktop-win10-win11-64bit-international-nsd-dch-whql.exe" } },
      { "downloadInfo": {
          "Success":"1", "Name":"GeForce%20Game%20Ready%20Driver", "Version":"610.74",
          "IsBeta":"0", "IsWHQL":"1", "IsCRD":"0",
          "ReleaseDateTime":"Tue Jul 07, 2026", "DownloadURLFileSize":"979.17 MB",
          "DownloadURL":"https://us.download.nvidia.com/Windows/610.74/610.74-desktop-win10-win11-64bit-international-dch-whql.exe" } }
    ]}
    """;
    var provider = new NvidiaCatalogProvider(StubHandler.Ok(body));

    var resp = CatalogEndpoint.Build(provider, Rtx4070);   // the function /api/catalog serves

    Assert.Equal("live", resp.Source);
    Assert.DoesNotContain(resp.Releases, r => r.DownloadUrl!.Contains("-nsd-"));
    Assert.Equal("610.74", resp.Releases[0].Version);
}
```

Command (real repo):
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~NvidiaCatalogProviderTests.GetReleases_StudioOnlyVersion_IsNotServedAsAGameReadyRelease"
```

Captured RED output from the Linux harness (`repro/studio-only-release-listed-as-game-ready.cs`, class `StudioOnlyReleaseListedAsGameReadyRepro`), HEAD `54b3c31`, trimmed:
```
[xUnit.net 00:00:00.33]     CleanDriver.Tests.StudioOnlyReleaseListedAsGameReadyRepro.GetReleases_StudioOnlyVersion_IsNotServedAsAGameReadyRelease [FAIL]
  Error Message:
   Assert.DoesNotContain() Failure: Filter matched in collection
             ↓ (pos 0)
Collection: [Release { Version = 611.05, Channel = WHQL, ReleaseDate = 2026-07-14, SizeMB = 979, DownloadUrl = https://us.download.nvidia.com/Windows/611.05/611.05-desktop-win10-win11-64bit-international-nsd-dch-whql.exe, Source = live }, Release { Version = 610.74, Channel = WHQL, ... }]
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1
```
The Studio build is at position 0 (newest) and labeled `WHQL`.

### B. Live — running app
Not reproducible on the running app here. The defect is on the live-provider path, and `--mock-catalog` bypasses that path by design. This bug hunt is also barred from live NVIDIA traffic. The fixture-driven test above exercises the same `CatalogEndpoint.Build` that `/api/catalog` serves (`Api.cs:27-28`). On a Windows host with a mapped GPU, when NVIDIA's newest row is Studio-only:
1. `dotnet run --project nvcleanstall -- --headless`
2. `curl -s localhost:4780/api/catalog`. Expected: no `downloadUrl` contains `-nsd-`. Actual (HEAD): `releases[0].downloadUrl` is the `-nsd-` installer with `"channel":"WHQL"`.

## Root cause
Excluding Studio rows was implemented as a preference inside each version group, not as a filter on row type. A preference can only pick between rows that exist in a group. A group that holds a single Studio row has nothing else to pick, so the Studio row is returned. The contract ("down to the Game-Ready build") calls for a filter. A secondary mistake: `Channel` is derived from `IsBeta` alone, so a row that NVIDIA itself flags `IsWHQL "0"` is presented as `WHQL`.

## Remedy
Minimal fix: drop Studio rows at the point the flag is read. The existing `GroupBy` stays and still de-duplicates any repeated Game Ready row.

```diff
             bool isBeta = Str(info, "IsBeta") == "1";
             bool isCrd = Str(info, "IsCRD") == "1"; // Creator/Studio Ready Driver
+            // Game Ready only (GAP-F01 owns a Studio variant): a Studio row is never served,
+            // whether or not its version has a Game Ready twin.
+            if (isCrd) continue;
             rows.Add((new Release
```
(The `gameReady` tuple element and the `OrderByDescending(x => x.gameReady)` become redundant. The Refactor step can remove them.)

- **Why minimal:** a one-line filter where the flag is already computed. No new fields, no UI change.
- **Alternatives rejected:**
  - (a) Emit `Channel = "Studio"`. This adds a value and tag the frozen Screen 1 contract does not define, and it is exactly the GAP-F01 selector that is waiting on an owner ruling.
  - (b) Keep a Studio row only when no Game Ready row exists. That is the bug.
  - (c) Filter in `Api.cs` or `app.js`. Wrong single owner: the provider owns catalog shaping (GAP-01).
- **Edge behavior:** a product whose lookup returns only Studio rows now yields zero rows. That lands in the documented `live lookup failed` bucket and falls back to mock, which is honest. Silently serving Studio would not be.
- **Fences:** safety untouched (nothing executed); IP rule untouched; R1–R10 untouched (no new control).
- **Back-compat pins:** `NvidiaCatalogProviderTests.GetReleases_ParsesRecordedFixture_IntoLiveReleases` (still 610.74/610.62/610.47 with the same `-dch-whql.exe` URLs), `CatalogWiringTests.CatalogResponse_LiveProvider_ReportsLiveSource` (3 releases), `CatalogSourceDetailTests.Build_LiveSuccess_HasNoDetail`, `GetReleases_MapsBetaFlag_ToBetaChannel`.
- **Verified:** I applied this diff to a scratch copy. The full suite (151 tests) plus every repro passed (161/161), with no existing test edited.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** Settled: slice **GAP-R08**, an interim fix ahead of GAP-F01, which stays blocked on its UI ruling; do not fold it into F01.
   > *Pinned 2026-11-23 as **GAP-R08** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-studio-filter -b feat/studio-only-release-listed-as-game-ready origin/main`
2. **Red.** Add the §A test to `nvcleanstall/CleanDriver.Tests/NvidiaCatalogProviderTests.cs`. Run the §A filter and confirm `Assert.DoesNotContain() Failure ... 611.05 ... -nsd-`. Commit `"GAP-R08 (red): studio-only-release-listed-as-game-ready — a Studio-only live row is never served as a Game Ready release"`.
3. **Green.** Add `if (isCrd) continue;`. Run the FULL suite. Commit `"GAP-R08: studio-only-release-listed-as-game-ready — drop Studio rows from the Game-Ready-only live catalog"`.
4. **Refactor.** Remove the now-dead `gameReady` tuple element and its `OrderByDescending`, with the suite green. Update the `Parse` comment so it says "Game Ready rows only".
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.**
   - `dotnet run --project nvcleanstall -- --headless --mock-catalog` then `curl -s localhost:4780/api/catalog`. Expect the 5 mock releases with `sourceDetail: "mock mode"` (unchanged).
   - On a Windows host with a mapped GPU, `dotnet run --project nvcleanstall -- --headless` then `curl -s localhost:4780/api/catalog`. Paste the `downloadUrl` list and show no `-nsd-` entry.
7. **PR.** Fill `.github/pull_request_template.md`: the red/green row for the test above; "No existing test was edited"; under Deviations, "Studio-only products now fall back to mock (`live lookup failed`)". Push. Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `gh pr checks <n>`: `build-test-smoke` must be green before reading the diff.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-studio origin/feat/studio-only-release-listed-as-game-ready`. Review range `origin/main...HEAD`.
3. **Re-run gates** on the reviewer's checkout (build, test, `git diff --check`). Then run `--headless --mock-catalog` and `curl /api/catalog` to confirm the mock path is unchanged.
4. **Finder pass.**
   - Is the filter keyed on `IsCRD` exactly as the fixture spells it (`"1"` string)?
   - Is the test driven through `CatalogEndpoint.Build`, the production function, and not a re-implementation?
   - Does the existing fixture test still assert the same three URLs (regression hunt)?
   - Was any existing test edited?
   - Does the PR add any UI/channel value (fence check against R1–R10 and the GAP-F01 blocker)?
   - Does the `live lookup failed` bucket still fire when every row is filtered (`CatalogSourceDetailTests`)?
   - Optionally run `/code-review` on the PR diff as the finder.
5. **Adversarial verify** with extra throwaway tests in the review worktree:
   - A body containing only Studio rows. Expect mock with `sourceDetail == "live lookup failed"`.
   - A Studio row and a Game Ready row with the same version but swapped order. Expect the Game Ready URL.
   - A Studio row with `IsBeta "1"`. Expect it excluded.
   - A Game Ready row with `IsCRD` missing. Expect it kept.
   - `IsCRD` as a JSON number (`1`). `Str` returns `""`, so the row is treated as Game Ready. Note this as residual risk; it is not this fix's scope.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED (reproduced by a test) or PLAUSIBLE.
7. **Feedback to builder.** If changes are needed, write an implementer handoff (coordinates, findings with reproduce blocks, ACs). The fix loop then runs C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relay. Only the owner merges.

## Acceptance criteria
1. For a live response with a Studio-only version newer than every Game Ready version, `CatalogEndpoint.Build` returns no release whose `DownloadUrl` contains `-nsd-`, and `Releases[0]` is the newest Game Ready version.
2. For the recorded fixture, the result is unchanged: versions `610.74, 610.62, 610.47`, all `WHQL`, all `live`, same `-dch-whql.exe` URLs.
3. A response with only Studio rows yields `source == "mock"` and `sourceDetail == "live lookup failed"`.
4. No existing test is edited. The full suite and `git diff --check` are green.
5. No new channel value, tag, or control reaches `/api/catalog` or `wwwroot/`.
