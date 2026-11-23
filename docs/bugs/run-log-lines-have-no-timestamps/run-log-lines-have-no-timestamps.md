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
*Under D-LOGTS this test only documents the code/doc mismatch; it is **not** to be added to the suite.* Originally proposed as red test `nvcleanstall/CleanDriver.Tests/JobLogTimestampTests.cs`. It is pure: no receipt, no filesystem, no timing dependency.
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
**Ruled (D-LOGTS, 2026-11-23): option (b), correct the documentation.** The code stands: log entries stay `{text, cls}`, which matches the frozen Screen 5 mockup and R9's reference log shape.
- In `CLAUDE.md` (architecture, `Jobs.cs` bullet), drop "timestamped" from "background step lists appending timestamped log lines the frontend polls via `/api/jobs/{id}`".
- Do **not** edit FR-010 in `specs/nvcleanstall/spec.md`. Under D-LOGTS its "timestamped log and receipt file" is read as met by the receipt (`installedAt` plus its dated file name; the file name's date is itself open under [`receipt-name-date-uses-culture-calendar`](../receipt-name-date-uses-culture-calendar/receipt-name-date-uses-culture-calendar.md)). Rewording FR-010 needs a separate owner sign-off.
- *Not pursued (D-LOGTS):* option (a), recording a UTC `ts` on each log line and showing it on hover or as a visible prefix. It would need a fresh ruling, and a visible prefix would also amend R9.

Fences: docs-only. No production, test or `wwwroot/` change.

## Fix workflow (docs-only — exemption A2)
0. **Register gate.** D-LOGTS rules the outcome. Per CLAUDE.md, the owner still pins the slice: a register entry, or explicit word that D-LOGTS suffices for this one-sentence docs fix.
1. **Slice setup.** `git worktree add ../nvcleanclone-log-wording -b docs/claude-md-log-wording origin/main`.
2. **Edit** the one `CLAUDE.md` sentence. Touch no other file.
3. **No red/green.** Under exemption **A2** a docs-only slice satisfies the TDD gate vacuously; name A2 in the PR body.
4. **Gates still run:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` (count unchanged), `git diff --check`.
5. **PR.** In the template, "What & why" cites D-LOGTS and this report; the TDD table says "A2 — docs-only, no behavior change"; Deviations: none; Live exercise: not applicable (docs). Push; never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage**: green.
2. **Isolated review worktree**, `origin/main...HEAD`. Confirm the diff touches only `CLAUDE.md`.
3. **Re-run the gates.**
4. **Finder pass:**
   - The edited sentence no longer claims timestamped log lines, and nothing else in `CLAUDE.md` changes meaning.
   - FR-010 in `specs/nvcleanstall/spec.md` is untouched (D-LOGTS).
   - The PR body names exemption A2.
5. **Adversarial verify:** `grep -rnI "timestamped log" --include=*.md --include=*.html .` outside `docs/bugs/`. At `54b3c31` the only hits were FR-010 (kept by ruling) and the `CLAUDE.md` sentence. Any new hit is a finding to report to the owner, not to edit silently.
6. **Verdict doc.**
7. **Implementer handoff** if needed → C0–C7, one `F<n>:` commit per finding.
8. **Report to the owner**; the owner merges.

## Acceptance criteria
1. `CLAUDE.md` no longer says log lines are timestamped.
2. FR-010 in `specs/nvcleanstall/spec.md` is unchanged.
3. The diff touches only `CLAUDE.md`, and the PR body names exemption A2.
4. `dotnet build`, `dotnet test` (count unchanged) and `git diff --check` are green.
