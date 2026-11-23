# A fast double press of Start runs two jobs into one run view; with Build package one fails while the banner reports success

| Field | Value |
|---|---|
| **Slug** | `double-click-start-runs-two-jobs` |
| **Severity** | Low. Nothing is executed or applied. The user can be shown a contradictory result: an `> ERROR:` line, then "Package built". Each step is also logged twice, and two receipts or two sets of artifacts are written. Reaching it needs the second press to land inside the `/api/execute` round-trip, which was measured at about 5 ms warm and 22 ms on the first cold call. A normal human double-click (60 ms or more apart) does **not** trigger it. A bouncing mouse switch, an automation tool, or a stalled server does. |
| **Confidence** | Confirmed by a live browser repro (Playwright/Chromium, Linux headless harness, `--mock-catalog`). Press-interval sweep: 0 ms and 5 ms start 2 jobs; 15 ms and above start 1 (`probe-click-interval.js`). |
| **Area** | `nvcleanstall/wwwroot/app.js` (`btn-next` handler → `startExecute`, `pollRun`). The server half (one writer per output directory) is GAP-C02 / BUG-04. |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. The logic is engine-independent. |
| **Related register entries** | Same no-in-flight-guard pattern as `screen1-next-no-inflight-guard-wrong-version` (Screen 1 **Next**, `leaveSource`). This doc covers Screen 5 **Start**, which is a different handler with a different effect. BUG-04 / GAP-C02 ("serialize extract/package work by canonical output directory") is the server half: this repro is a dynamic reproduction of the concurrent same-directory writer that BUG-04 lists as "not dynamically reproduced". Not SEC-0x. |
| **Found** | 2026-11-23 (E2E functional pass) |

## Summary
`startExecute` awaits `POST /api/execute` with no in-flight flag. The footer, and with it **Start**, only disappears once the response arrives (`show('run')`). A second press inside that window posts a second job, and both responses call `pollRun`. The two pollers append into the same `#run-log`, so every step appears twice, interleaved. The first poller to see `done` shows the success banner, whatever the other job did.

With **Build package** or **Extract only** both jobs write the same output folder. In the repro, one job failed with `The process cannot access the file '…/payload/display-driver.bin' because it is being used by another process`. Its `> ERROR:` line is printed in the log, and then the banner reads **"Package built — written to output\package-571.96"**.

## Evidence
Failing UI tests in `docs/bugs/ui-functional-test-report.md`: **N-06**. N-05 and R-06 show the same pattern on Screen 1 and are tracked in `screen1-next-no-inflight-guard-wrong-version`.

`app.js:77-82`: **Start** is the footer's `btn-next` on the action screen, with no guard:
```js
$('btn-next').onclick = () => {
  ...
  if (state.screen === 'action') return startExecute();          // :81
};
```
`app.js:383-406`: the request is awaited before the screen changes, so the button stays live for one round-trip:
```js
async function startExecute() {
  const selection = { components: state.components, tweaks: state.tweaks };
  const r = await api.post('/api/execute', { ... });             // :385  second press lands here
  if (r.error) return alert(r.error);
  ...
  show('run');                                                   // :405  footer hidden only now (R4)
  pollRun(r.jobId);                                              // :406  one poller per response
}
```
`app.js:411-439`: each poller keeps its own `shown` cursor but writes the shared log and banner:
```js
function pollRun(jobId) {
  let shown = 0;                                                 // :412  per-poller cursor
  ...
    const log = $('run-log');                                    // :415  shared element
    for (; shown < j.log.length; shown++) { ... log.appendChild(ln); }
  ...
    if (j.status === 'done') { ... $('run-banner').classList.remove('hidden'); }   // :428-438  first done wins
```
`Jobs.cs:386-388`: both jobs resolve the same `outDir` (`output/{action}-{version}`, or the same `outputPath`). `Packages.cs:100`: `File.Create(destPath)` races on the same payload file. `Jobs.cs:586-591` turns the loser's exception into `failed`.

Captured RED (harness, `BASE_URL=http://127.0.0.1:4806`):
```
RED   (a) double-click Next on Screen 1 → POST /api/download ×2, POST /api/package ×2
RED   (b) double-click Start (Build package) → POST /api/execute ×2; jobs [9:done; 10:failed (The process cannot access the file '<scratch>/…)]; 10 log lines, 2 duplicates; error line shown=true; banner "Package built — written to output\package-571.96"
exit=1
```
Press-interval sweep (`probe-click-interval.js`, warm server):
```
0ms→2 job(s) (execute round-trip 8ms)
5ms→2 job(s) (execute round-trip 5ms)
15ms→1 job(s) (execute round-trip 4ms)
30ms→1 job(s) …   60ms→1 …   100ms→1 …   150ms→1 …
cold first /api/execute round-trip 22ms; presses 80ms apart → 1 job(s)
```
An Install double press (N-06, 0 ms) logs all ten steps twice. The two runs write one receipt name (`receipt-<date>.json`, BUG-03), so the second overwrites the first.

## Impact
- **Contradictory result:** a failed job's `> ERROR:` line, then a success banner. The user cannot tell which job produced the folder or zip on disk.
- **Duplicated audit trail:** each step appears twice in the log. Install/Silent write two receipts, and the second overwrites the first.
- **Reach is narrow** today: the second press must land inside about 5–22 ms. A failing or "chattering" mouse switch, accessibility or macro tools, or a stalled server (antivirus scanning `output/`, a loaded machine) widen the window. Severity stays Low.

## How to reproduce
### A. Automated — browser transcript (amendment A6)
Script `double-click-starts-duplicate-jobs.js` (Playwright, no npm deps beyond the pre-installed `playwright`):
```js
// Repro: primary buttons have no in-flight guard — a double-click starts two jobs.
// (a) Screen 1 Next (catalog) → two downloads; (b) Screen 5 Start with Build package into the
// default output folder → two concurrent writers on one directory: one fails, the banner says "Package built".
const { chromium } = require('playwright');
const BASE = (process.env.BASE_URL || 'http://localhost:4780').replace(/\/$/, '');
(async () => {
  const b = await chromium.launch(); let red = false;
  const mk = async () => { const ctx = await b.newContext({ viewport: { width: 960, height: 640 } }); await ctx.route(u => !u.href.startsWith(BASE), r => r.abort());
    const p = await ctx.newPage(); p._req = []; p.on('request', r => p._req.push(r.method() + ' ' + r.url().replace(BASE, ''))); p.on('dialog', d => d.accept());
    await p.goto(BASE + '/?shell=1'); await p.waitForFunction(() => document.getElementById('combo-ver').textContent); return p; };
  { const p = await mk(); await p.dblclick('#btn-next'); await p.waitForSelector('#screen-components.active', { timeout: 20000 });
    const n = p._req.filter(r => r === 'POST /api/download').length; red = red || n > 1;
    console.log(`${n > 1 ? 'RED  ' : 'GREEN'} (a) double-click Next on Screen 1 → POST /api/download ×${n}, POST /api/package ×${p._req.filter(r => r === 'POST /api/package').length}`); }
  { const p = await mk(); const ids = [];
    p.on('response', async r => { if (r.url().endsWith('/api/execute')) ids.push((await r.json()).jobId); });
    await p.click('#opt-local .opt-head'); await p.click('#btn-browse'); await p.waitForTimeout(300);
    await p.click('#btn-next'); await p.waitForSelector('#screen-components.active'); await p.click('#btn-next'); await p.click('#btn-next');
    await p.click('.acard[data-action="package"]');               // empty Output folder → default output/package-<ver>
    await p.dblclick('#btn-next'); await p.waitForSelector('#run-banner:not(.hidden)', { timeout: 20000 }); await p.waitForTimeout(2500);
    const jobs = []; for (const id of ids) { const j = await (await fetch(BASE + '/api/jobs/' + id)).json(); jobs.push(`${id}:${j.status}${j.error ? ' (' + j.error.slice(0, 70) + '…)' : ''}`); }
    const lines = await p.$$eval('#run-log .ln', l => l.map(x => x.textContent)); const dup = lines.filter((l, i) => lines.indexOf(l) !== i).length;
    red = red || ids.length > 1;
    console.log(`${ids.length > 1 ? 'RED  ' : 'GREEN'} (b) double-click Start (Build package) → POST /api/execute ×${ids.length}; jobs [${jobs.join('; ')}]; ${lines.length} log lines, ${dup} duplicates; error line shown=${lines.some(l => l.startsWith('> ERROR'))}; banner "${await p.textContent('#banner-text')}"`); }
  await b.close(); process.exit(red ? 1 : 0);
})();
```
Run it against the repo app with `dotnet run --project nvcleanstall -- --headless --mock-catalog` and `BASE_URL=http://localhost:4780 node double-click-starts-duplicate-jobs.js`. The RED output is captured in **Evidence**. Part (a) is the Screen 1 sibling, kept so a shared fix can be verified with one script.

### B. Manual — running app
1. Start the app and choose **Use driver files on disk** → **Browse…** → **Next** → **Next** → **Next**.
2. On **Action**, select **Build package** and leave Output folder empty.
3. Press **Start** twice as fast as possible. A macro key or a scripted click works; a normal double-click is usually too slow, see **Impact**.
4. **Expected:** one run, each step logged once, one result.
5. **Actual:** every step is logged twice. Often an `> ERROR: The process cannot access the file …` line appears, followed by "Package built — written to output\package-<ver>".

## Root cause
A user action that starts an asynchronous, server-side job is treated as if it were instantaneous. Nothing records "a start is pending", so the handler can run twice before the screen changes. `pollRun` also assumes it owns the run view, so two pollers silently share one log and one banner.

## Remedy
Frontend-only, A6, with no new controls. The fence is the frozen five-screen contract plus R1–R10, and the footer is still hidden by R4 once the run view shows.
- Add a module-level `executing` flag. In `startExecute`, `if (executing) return; executing = true;`. Add `$('btn-next').classList.add('disabled')` so the existing `.btn.disabled` style (`styles.css:107`) applies. Release both in a `finally` when the call did not reach `show('run')`, so an alert path stays retryable.
- Make `pollRun` single-owner: keep the current job id in `state.runJob`, and have a tick return early when `jobId !== state.runJob`.
- Apply the same flag pattern to `leaveSource`, as `screen1-next-no-inflight-guard-wrong-version` proposes. Land both in one slice, or sequence them.
- Keep `textContent` for log lines (`app.js:420`) and `esc()` for the banner path. Leave the safety boundary and honesty markers untouched.
- Server half (separate, GAP-C02): serialize extract/package by canonical output directory and fail the second writer with `output directory is already in use`.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active. The server half is GAP-C02. The client guard is in no register row, so fold it into GAP-C02 as a client line item (as the Screen 1 doc proposes) or get a new owner-pinned entry. Declare `slice_start --no-surface '^nvcleanstall/wwwroot/'`.
1. **Slice setup.** `git worktree add ../nvcleanclone-dblstart -b feat/double-click-start-runs-two-jobs origin/main`.
2. **Red.** Run the §A script against `--headless --mock-catalog` on 4780 and capture `RED … (b) … POST /api/execute ×2`. There is no JS test runner (A6), so the transcript is the red. Commit nothing under wwwroot yet.
3. **Green.** Make the minimum change in `app.js`: the flag, the disabled class, and the single-owner `pollRun`. Re-run the transcript for `GREEN … ×1`, and run the full C# suite, which must stay green (no backend change). Commit `"double-click-start-runs-two-jobs: guard Start while /api/execute is in flight"`.
4. **Refactor**, green only. If the Screen 1 fix lands in the same slice, share a small `once(fn)` helper.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run the green transcript plus a manual check in the WebView2 window: press Start rapidly, and exactly one run should appear. Also check that an `alert` path, such as an invalid token after a server restart, leaves Start usable again.
7. **PR.** Fill `.github/pull_request_template.md`, with TDD evidence rows naming amendment A6 (red and green transcripts). Push and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `gh pr checks <n>`: build-test-smoke must be green first.
2. **Isolated review worktree** at the PR head, diff `origin/main...HEAD`.
3. **Re-run the gates** and the §A transcript on the reviewer's checkout.
4. **Finder pass**, with a checklist specific to this diff:
   - Is the flag released on *every* non-run exit (`r.error` alert, a thrown `api.post`)? A stuck-disabled Start is a new dead end.
   - Does `pollRun` ignore stale job ids after **Finish** → new run?
   - Is the footer still hidden in the run view (R4)?
   - Is there any new control or text? There must be none.
5. **Adversarial verify:**
   - `page.route('**/api/execute')` delayed 2 s, press Start 5 times: expect 1 request.
   - Same delay, answered `{error}`: expect the alert, then Start works again.
   - Finish during a run that was started twice: no stale lines appear in the next run.
6. **Verdict doc:** APPROVE / REQUEST CHANGES, findings CONFIRMED / PLAUSIBLE.
7. **Implementer handoff**, then fix loop C0–C7 with one `F<n>:` commit per finding.
8. **Report to the owner**; only the owner merges.

## Acceptance criteria
1. The §A script prints `GREEN … (b) … POST /api/execute ×1` with 0 duplicate log lines, under a 0 ms double press and under a 2 s injected `/api/execute` delay.
2. After an `r.error` alert from `/api/execute`, **Start** is enabled and a second press posts exactly once.
3. Only the poller of the latest job writes to `#run-log` and the banner. After **Finish** and a new run, no line from the earlier job appears.
4. R4 still holds: the footer is hidden in the run view, and Open output folder and Finish are the exits. No new controls are added.
5. The full C# suite is green, and `git diff --check` is clean.
