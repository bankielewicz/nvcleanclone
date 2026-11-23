# Receipt file name is dated in the user's culture calendar (Thai Buddhist, Hijri, Persian), not ISO-Gregorian; it contradicts the receipt's own installedAt

| Field | Value |
|---|---|
| **Slug** | `receipt-name-date-uses-culture-calendar` |
| **Severity** | Low (wrong artifact naming on affected locales; no data loss beyond BUG-03's) |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness); also confirmed live (headless mirror under `LC_ALL=th_TH.UTF-8`). Windows: reasoned. `th-TH`'s default calendar is `ThaiBuddhistCalendar` in .NET on every platform, but the run was not repeated on Windows. |
| **Area** | `nvcleanstall/Lib/Jobs.cs` (`StartExecute`, `stamp`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | Same line as **BUG-03** (GAP-C02), but a distinct defect. BUG-03 is that same-day receipts *collide*. This one is that the date in the name uses the *wrong calendar*. GAP-C02's planned rename ("UTC timestamp plus random job ID") will carry this defect over unless its format call is made culture-invariant, so this belongs in GAP-C02's AC list. |
| **Found** | 2026-11-23 |

## Summary
The install/silent receipt is named `receipt-{stamp}.json`, where `stamp = DateTime.Now.ToString("yyyy-MM-dd")`. A custom date format without an explicit provider is rendered in the current culture's **default calendar**, which is not Gregorian on several common Windows regional formats. On a Thai system today's receipt is `receipt-2569-11-23.json`, on Saudi Arabic it is `receipt-1448-06-13.json`, and on Persian it is `receipt-1405-09-02.json`. The receipt body records `installedAt` with the culture-invariant round-trip format (`2026-11-23T…Z`). The same artifact therefore carries two dates that disagree by centuries, and the name is not the ISO date the mockups, tests and tooling assume.

## Evidence
- `nvcleanstall/Lib/Jobs.cs:385`. The stamp uses a culture-sensitive custom format:
  ```csharp
  var stamp = DateTime.Now.ToString("yyyy-MM-dd");
  ```
- `nvcleanstall/Lib/Jobs.cs:441` names the file with it. `Jobs.cs:448` writes the body's date culture-invariantly:
  ```csharp
  var receiptPath = Path.Combine(OutputDir, $"receipt-{stamp}.json");
  …
  installedAt = DateTime.UtcNow.ToString("o"),
  ```
  `"o"` is invariant by definition, while `"yyyy-MM-dd"` follows `CultureInfo.CurrentCulture.Calendar`.
- Contrast with the codebase's own convention: `NvidiaCatalogProvider.NormalizeDate` formats with `CultureInfo.InvariantCulture`, `dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)`. The receipt stamp is the one date in `Lib/` that omits it.
- `ExecuteReceiptTests` documents the name as `Jobs.OutputDir/receipt-<date>.json`, and the mockup's run view shows `output\receipt-….json`. Both assume an ISO date. No test sets a non-Gregorian culture.

## Impact
- On Windows machines whose Regional format is Thai, Saudi Arabic or Persian, among others, receipts are named in that calendar. Anyone or any script looking for today's receipt by ISO date (`receipt-2026-11-23.json`) will not find it.
- Receipts from one machine split across two "eras" if the user ever changes regional format, so sorting by name interleaves them wrongly.
- The artifact is internally inconsistent: its name says 2569, its `installedAt` says 2026.
- How likely: every install/silent run on an affected locale. The body content and the UI are otherwise unaffected. The run banner shows whatever name was written.

## How to reproduce
### A. Automated — failing xunit test
Add to `nvcleanstall/CleanDriver.Tests/ExecuteReceiptTests.cs`. That class owns the shared receipt path and is serialized for it. Once fixed, this test writes the same `receipt-<today>.json` the other facts there read. It reuses `M()`, `Src()`, `Sel()` and `G()`.

```csharp
[Theory]
[InlineData("th-TH")]
[InlineData("ar-SA")]
[InlineData("fa-IR")]
public async Task StartExecute_Install_ReceiptNameUsesGregorianIsoDate_InAnyCulture(string culture)
{
    var prior = System.Globalization.CultureInfo.CurrentCulture;
    var before = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    Job job;
    try
    {
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
        job = Jobs.StartExecute("install", M(), Src(), Sel(new[] { "display" }), null, G());
    }
    finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
    var after = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
    await job.Completion!;

    // before/after bracket the call so a midnight rollover cannot flake the test.
    Assert.Contains(Path.GetFileName(job.Receipt!), new[] { $"receipt-{before}.json", $"receipt-{after}.json" });
}
```

Run (real repo):
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~ExecuteReceiptTests.StartExecute_Install_ReceiptNameUsesGregorianIsoDate_InAnyCulture"
```

Captured RED on unmodified HEAD (Linux net10.0 harness with ICU; class `ReceiptNameDateUsesCultureCalendarRepro`, same body):
```
Failed ...ReceiptNameUsesGregorianIsoDate_InAnyCulture(culture: "th-TH") [1 s]
   Assert.Contains() Failure: Item not found in collection
Collection: ["receipt-2026-11-23.json", "receipt-2026-11-23.json"]
Not found:  "receipt-2569-11-23.json"
Failed ...ReceiptNameUsesGregorianIsoDate_InAnyCulture(culture: "fa-IR") [1 s]
   Assert.Contains() Failure: Item not found in collection
Collection: ["receipt-2026-11-23.json", "receipt-2026-11-23.json"]
Not found:  "receipt-1405-09-02.json"
Failed ...ReceiptNameUsesGregorianIsoDate_InAnyCulture(culture: "ar-SA") [1 s]
   Assert.Contains() Failure: Item not found in collection
Collection: ["receipt-2026-11-23.json", "receipt-2026-11-23.json"]
Not found:  "receipt-1448-06-13.json"
Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3
```

### B. Live — running app (if applicable)
1. **Linux mirror (run).** Start the headless mirror under a Thai locale, which .NET maps to `CurrentCulture`, then install a mock package:
   ```
   LC_ALL=th_TH.UTF-8 CLEANDRIVER_URLS=http://127.0.0.1:4801 dotnet out/Server.dll --mock-catalog &
   curl -s -X POST :4801/api/package  -d '{"kind":"catalog","version":"572.16"}' -H 'Content-Type: application/json'
   curl -s -X POST :4801/api/execute  -d '{"token":"1","action":"install","selection":{"components":["display-driver"],"tweaks":{}}}' -H 'Content-Type: application/json'
   curl -s :4801/api/jobs/<id>
   ```
   Observed: `"receipt": ".../output/receipt-2569-11-23.json"`, while that file contains `"installedAt": "2026-11-23T12:54:29.0752229Z"`.
2. **Windows (owner/reviewer).** In Settings → Time & language → Language & region, set **Regional format** to *Thai*. Run `dotnet run --project nvcleanstall -- --headless --mock-catalog`, repeat the curl calls against port 4780, and `dir nvcleanstall\bin\Debug\net10.0-windows\output`. **Expected:** `receipt-2026-11-23.json`. **Actual (reasoned):** `receipt-2569-11-23.json`. Restore the regional format afterwards.

## Root cause
`DateTime.ToString(string format)` uses `DateTimeFormatInfo.CurrentInfo`, including that culture's **calendar**, for custom specifiers like `yyyy`. A machine-readable artifact name needs the invariant (Gregorian) calendar, and the call omits the `IFormatProvider`.

## Remedy
```diff
-        var stamp = DateTime.Now.ToString("yyyy-MM-dd");
+        var stamp = DateTime.Now.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
```
- **Why minimal:** a one-token change with the same local-date semantics and the same name on Gregorian locales. The existing pins are unaffected, since they never assert the name's date. Fully qualifying the type avoids a new `using` in a file the guard tests scan. `System.Globalization` is not a forbidden token, but a single qualified reference keeps the diff to one line.
- **Validated:** on a scratch copy of `Jobs.cs`, all three culture cases turn green and the full suite stays green (163/163, Linux harness).
- **If GAP-C02 lands first:** it renames receipts to a UTC timestamp plus a random job ID. Its formatting call must use `CultureInfo.InvariantCulture` (or `"o"`/`"s"`-style standard formats), and this test is re-pointed at the new name scheme, asserting a Gregorian ISO date under `th-TH`.
- **Alternatives rejected:** forcing `CultureInfo.DefaultThreadCurrentCulture = InvariantCulture` at startup would also change user-visible formatting, such as `Speed` decimals, which should follow the user's locale. Using `DateTime.UtcNow` alone does not fix the calendar.
- **Fences:** safety, deletion and UI are untouched. Honesty markers are unchanged.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** The active register is `docs/security_register.md`, and no item covers this. The owner pins a new finding ID first. Destination: **fold into GAP-C02**, which owns receipt naming (BUG-03) and will rewrite this exact line. Add AC "receipt names are culture-invariant (Gregorian ISO) under any `CurrentCulture`". Per §7 it gets its own ID.
1. **Slice setup.** `git worktree add ../nvcleanclone-receipt-culture -b feat/receipt-name-invariant-date origin/main` (or the GAP-C02 branch).
2. **Red.** Add the theory from §A to `ExecuteReceiptTests.cs`. Run the filter and confirm three `Not found: "receipt-2569…"`-style failures. Commit `"<ID> (red): receipt name date is ISO-Gregorian in any culture"`.
3. **Green.** Make the one-line change and run the full suite. Commit `"<ID>: format the receipt stamp with InvariantCulture"`.
4. **Refactor.** None.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run the §B.2 Windows procedure under the Thai regional format. Show `receipt-2026-…json` and an `installedAt` with the same year.
7. **PR.** Fill `.github/pull_request_template.md`. TDD row: culture theory (3 cases). No existing test is edited. Deviations: none. Paste the live transcript, push, and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green before reading the diff.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-receipt-culture origin/feat/receipt-name-invariant-date`. Review range `origin/main...HEAD`.
3. **Re-run gates** on the reviewer's checkout.
4. **Finder pass.** Check each of these:
   - **Every** date or number that ends up in a file name or machine-read field in `Jobs.cs` is invariant. Today that is only `stamp`: `installedAt`/`builtAt` use `"o"`. User-visible strings (`Speed`, MB figures in log lines) may stay culture-formatted, so do not over-correct.
   - The test restores `CurrentCulture` in `finally`.
   - The test lives in `ExecuteReceiptTests`, because the shared receipt path requires it.
5. **Adversarial verify.** Throw these at the fix:
   - Run the theory under `ja-JP` with the Japanese calendar set as current calendar (era-based `yyyy`).
   - Run it under `he-IL` with the Hebrew calendar.
   - Run the whole `ExecuteReceiptTests` class twice in a row under `th-TH`, to prove no residual culture leak into other facts.
   - Set the process culture via the Windows regional format, not just the thread culture.
6. **Verdict document.** APPROVE or REQUEST CHANGES, each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** On REQUEST CHANGES, write an implementer handoff. The fix loop runs: C0 Orient → C1 reproduce the red → C2 fix → C3 verify the ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relay. The owner merges.

## Acceptance criteria
1. Under `CurrentCulture` = `th-TH`, `ar-SA` or `fa-IR`, an install/silent receipt is named with the Gregorian ISO local date (`receipt-YYYY-MM-DD…`, where `YYYY` equals the Gregorian year).
2. The year in the receipt name matches the year of the receipt's `installedAt`, ±1 day for the local/UTC offset.
3. Gregorian-locale behavior and all existing `ExecuteReceiptTests` pins are unchanged.
4. If GAP-C02's rename lands, the same invariance holds for the new name scheme.
