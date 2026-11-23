# The install/build log has no timestamps, although FR-010 and CLAUDE.md say it is timestamped

| Field | Value |
|---|---|
| **Slug** | `run-log-lines-have-no-timestamps` |
| **Severity** | Low. This is a spec-conformance and audit-quality gap, not a malfunction. The receipt still carries one `installedAt`, but the step-by-step log cannot be correlated in time, and it is lost once the page closes. |
| **Confidence** | Confirmed by a live browser repro (Playwright/Chromium) plus the API snapshot: 0 of 10 run-view lines carry a time, and `/api/jobs/{id}` log entries have only `text` and `cls`. |
| **Area** | `CLAUDE.md` (architecture sentence "appending timestamped log lines") — a documentation defect per **D-LOGTS**. As originally filed, the root cause was placed in `nvcleanstall/Lib/Jobs.cs` (`Job.LogLine`, `Job.Snapshot`), rendered by `nvcleanstall/wwwroot/app.js` (`pollRun`); under the ruling that code is correct. |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | FR-010: "Install/Silent run a simulated install producing a **timestamped log** and receipt file". `CLAUDE.md` architecture: "background step lists appending **timestamped log lines** the frontend polls". Ruling R9 adopts the 9-line mockup log as the reference *shape*, and that mockup shows no times. Parity AC-001 is graded `pass` without checking this. Not in `docs/security_register.md` §3. |
| **Found** | 2026-11-23 (E2E functional pass) |
| **Owner ruling** | **D-LOGTS** (2026-11-23, `docs/security_register.md`): **option (b)** — correct the documentation, not the code. This report is re-graded a **documentation defect** (severity Low): the code matches the frozen mockup and R9. FR-010 is read as satisfied by the receipt and is not reworded under this ruling. The fix is a docs-only (A2) slice correcting the `CLAUDE.md` sentence; option (a) below is not pursued. |

## Summary
FR-010 and the architecture notes both say the job log is timestamped. It is not, anywhere. `Job.LogLine` stores `{ text, cls }`, the snapshot returns those two fields, and the run view renders `text` only. Every action (Install, Silent, Extract, Build) produces lines like `> Removing previous driver traces…` with no time.

Either the implementation or the governing text is wrong. The owner has to rule which one changes. R9's log shape constrains how a time could be *shown*, not whether one is *recorded*.

## Evidence
Failing UI test in `docs/bugs/ui-functional-test-report.md`: **S5-10**.

`Jobs.cs:35-38`: no time is captured:
```csharp
public void LogLine(string text, string cls = "")
{
    lock (_lock) _log.Add(new { text, cls });
}
```
`Jobs.cs:50`: the snapshot forwards those entries as is (`log = _log.ToList()`).

`app.js:416-421`: the client renders `text` only:
```js
    for (; shown < j.log.length; shown++) {
      const ln = document.createElement('div');
      const e = j.log[shown];
      ln.className = 'ln' + (e.cls ? ` ${e.cls}` : '');
      ln.textContent = e.text;
```
Captured RED (harness, `BASE_URL=http://127.0.0.1:4806`):
```
RED   0/10 run-view lines carry a time; /api/jobs/13 log entry keys [text,cls]; first lines ["> Unattended mode: no prompts will be shown.","> Removing previous driver traces…","> Uninstalling driver 570.86… done"]
exit=1
```

## Impact
- FR-010's "timestamped log" is not met. The parity table's AC-001 `pass` therefore over-states conformance for this clause.
- The log is the only step-level record of what a run did (its decision lines, including "Skipping deselected components", "(simulated — no real signing performed)" and "Automatic reboot allowed — not performed"). Without times, two runs that write the same `receipt-<date>.json` (BUG-03) cannot be told apart from screenshots or copied logs.
- `CLAUDE.md` misdescribes the engine to every future builder.

## How to reproduce
### A. Automated — failing xunit test (backend root cause) + browser transcript
Proposed red test `nvcleanstall/CleanDriver.Tests/JobLogTimestampTests.cs`. It is pure: no receipt, no filesystem, no timing dependency.
```csharp
using System;
using System.Globalization;
using System.Text.Json;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// FR-010 "timestamped log": every job log entry carries a UTC ISO-8601 time, additively
// (existing `text`/`cls` unchanged, so mock-path byte-identity of receipts is unaffected).
public class JobLogTimestampTests
{
    [Fact]
    public void EveryLogEntryCarriesAnIsoUtcTimestamp()
    {
        var job = new Job { Id = "t1", Type = "install" };
        var before = DateTime.UtcNow;
        job.LogLine("> step one");
        job.LogLine("> step two", "mut");

        var snap = JsonSerializer.SerializeToElement(job.Snapshot(), Json.Web);
        foreach (var e in snap.GetProperty("log").EnumerateArray())
        {
            Assert.True(e.TryGetProperty("ts", out var ts), "log entry has no ts");
            var t = DateTime.Parse(ts.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            Assert.Equal(DateTimeKind.Utc, t.Kind);
            Assert.InRange(t, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
            Assert.True(e.TryGetProperty("text", out _));   // back-compat pin: text/cls unchanged
        }
    }
}
```
Expected red today: `Assert.True() Failure — log entry has no ts`. The xunit file itself was not run here, because the test project targets `net10.0-windows`. Its exact assertion was compiled against the real `nvcleanstall/Lib/*.cs` in a Linux console harness and printed:
```
RED: Assert.True() Failure — log entry has no ts (entry = {       "text": "> step one",       "cls": ""     })
```
Capture the xunit red output at slice time.

Browser transcript `run-log-lines-have-no-timestamps.js`:
```js
// Repro: FR-010 "timestamped log" — neither the run-view lines nor the /api/jobs/{id} log entries carry a time.
const { chromium } = require('playwright');
const BASE = (process.env.BASE_URL || 'http://localhost:4780').replace(/\/$/, '');
(async () => {
  const b = await chromium.launch(); const ctx = await b.newContext({ viewport: { width: 960, height: 640 } });
  await ctx.route(u => !u.href.startsWith(BASE), r => r.abort());
  const p = await ctx.newPage(); await p.goto(BASE + '/?shell=1'); await p.waitForFunction(() => document.getElementById('combo-ver').textContent);
  await p.click('#opt-local .opt-head'); await p.click('#btn-browse'); await p.waitForTimeout(300);
  await p.click('#btn-next'); await p.waitForSelector('#screen-components.active'); await p.click('#btn-next'); await p.click('#btn-next');
  const [resp] = await Promise.all([p.waitForResponse(r => r.url().endsWith('/api/execute')), p.click('#btn-next')]);
  const { jobId } = await resp.json(); await p.waitForSelector('#run-banner:not(.hidden)', { timeout: 20000 });
  const lines = await p.$$eval('#run-log .ln', l => l.map(x => x.textContent));
  const job = await (await fetch(BASE + '/api/jobs/' + jobId)).json();
  const ts = /\b\d{1,2}:\d{2}(:\d{2})?\b|\d{4}-\d{2}-\d{2}T/;
  const n = lines.filter(l => ts.test(l)).length; const keys = Object.keys(job.log[0]);
  const red = n < lines.length && !keys.some(k => /ts|time|at/i.test(k));
  console.log(`${red ? 'RED  ' : 'GREEN'} ${n}/${lines.length} run-view lines carry a time; /api/jobs/${jobId} log entry keys [${keys}]; first lines ${JSON.stringify(lines.slice(0, 3))}`);
  await b.close(); process.exit(red ? 1 : 0);
})();
```
Run: `dotnet run --project nvcleanstall -- --headless --mock-catalog`, then `BASE_URL=http://localhost:4780 node run-log-lines-have-no-timestamps.js`. The RED output is captured in **Evidence**.

### B. Manual — running app
1. Run any action (for example: Use driver files on disk → Browse… → Next → Next → Next → Start).
2. Read the log panel. Also `curl -s localhost:4780/api/jobs/<id>` with the job id from the network panel.
3. **Expected (FR-010):** each line is timestamped, or the API exposes a time per entry. **Actual:** neither.

## Root cause
The log entry model was written as `{text, cls}` for rendering only. The "timestamped" wording in FR-010 and CLAUDE.md was never backed by a field, and AC-001's verification checked step *content*, not times.

## Remedy
The owner rules first, choosing (a) or (b):
- **(a) Implement, preferred and additive.** `LogLine` records `ts = DateTime.UtcNow.ToString("o")` alongside `text`/`cls`. The snapshot passes it through, and mock receipts are unaffected because the receipt does not embed the log. In the UI, to keep R9's line shape, render the time unobtrusively. Either use a `title` attribute (hover shows local time) or a muted `HH:mm:ss` prefix via `textContent`; the latter needs an owner ruling against R9, since it changes the reference line text.
- **(b) Correct the text.** Amend FR-010 ("a log and a timestamped receipt") and the CLAUDE.md architecture sentence. That is docs-only, under amendment A2.

Fences: no new controls, `textContent` only, and receipts' honesty markers untouched.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active, and no entry covers FR-010 conformance. Get a new owner-pinned entry first, or record the owner's choice of (b) as a docs-only fix (A2). If (a) touches `wwwroot/` for the display, declare `slice_start --no-surface '^nvcleanstall/wwwroot/'` for that part.
   > *Ruling recorded 2026-11-23: **D-LOGTS**, option (b). The fix is the docs-only (A2) `CLAUDE.md` correction; no code slice and no red/green test apply.*
1. **Slice setup.** `git worktree add ../nvcleanclone-logts -b feat/run-log-lines-have-no-timestamps origin/main`.
2. **Red.** Add `JobLogTimestampTests.cs` and run `dotnet test … --filter "FullyQualifiedName~JobLogTimestampTests"`. It must fail on the assertion (`log entry has no ts`), not compile-fail. Commit `"run-log-lines-have-no-timestamps (red): job log entries carry a UTC ts"`.
3. **Green.** Make the one-line `LogLine` change, then run the full suite. Existing receipt and log pins stay green because `text`/`cls` are unchanged. If the UI renders the time, add the red and green browser transcript (§A script). Commit `"run-log-lines-have-no-timestamps: record a UTC timestamp per log line"`.
4. **Refactor**, green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run `curl /api/jobs/<id>` (entries show `ts`), plus the WebView2 run view, which matches R9 unless the owner ruled otherwise.
7. **PR.** Use the template's TDD rows (xunit red/green; plus A6 transcripts if the UI changed). Push and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.**
2. **Isolated review worktree**, `origin/main...HEAD`.
3. **Re-run the gates** and the transcript.
4. **Finder pass:**
   - `ts` is UTC with round-trip `"o"`, not culture-formatted. Compare `receipt-name-date-uses-culture-calendar`.
   - Every `LogLine` caller is covered, including the early root-path failure (`Jobs.cs:400`) and the download jobs.
   - Receipts and manifests are byte-identical on the mock path.
   - Any UI rendering uses `textContent`.
5. **Adversarial verify:**
   - Snapshot a running job twice: earlier entries keep their original `ts`.
   - A failed job's `> ERROR:` line has a `ts`.
   - Silent install (bar hidden) still shows times if the UI renders them.
6. **Verdict doc.**
7. **Implementer handoff** → C0–C7, one `F<n>:` commit per finding.
8. **Report to the owner**; the owner merges.

## Acceptance criteria
1. Either (a) every `/api/jobs/{id}` log entry has an ISO-8601 UTC `ts` and `JobLogTimestampTests` passes, or (b) FR-010 and CLAUDE.md are amended by an owner-approved docs PR, so that no document claims a timestamped log that does not exist.
2. Existing log/receipt tests are unchanged and green, and mock receipts are byte-identical.
3. If times are displayed, R9's line text is unchanged unless the owner ruled otherwise, and rendering uses `textContent`.
4. `dotnet build`, `dotnet test` and `git diff --check` are green.
