# Job pollers have no error exit: a lost server freezes Screen 2 / the run view, and a 404 makes `pollDownload` spin forever

| Field | Value |
|---|---|
| **Slug** | `job-pollers-no-exit-on-404-or-network-error` |
| **Severity** | Low. Bounded robustness: one frozen session, and on Screen 2 **Cancel** still works. In practice this only happens in browser/headless mode. |
| **Confidence** | Confirmed with a live browser repro (Playwright/Chromium). The transport-error case was reproduced with a **real server restart** mid-download (no injection). The 404 and run-view cases use `page.route` to return exactly what `Api.cs:121-122` serves for an unknown job id. The candidate fix was checked green. |
| **Area** | `nvcleanstall/wwwroot/app.js` (`api`, `pollDownload`, `pollRun`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Browser / headless (`--headless`, the verification mode) in practice. In the WebView2 shell the server runs in-process, so a server loss takes the window with it (reasoned). |
| **Related register entries** | Same defect family as **HARD-04**: a status that matches no terminal branch, polled every 150 ms forever. That fix handled `cancelled` only. **Depends on** `pollrun-failed-job-dead-ends-run-screen`: the run-view half routes into the `failed` branch that doc adds, so land that one first. BUG-05 / GAP-S02 (job-store caps and expiry) would make the 404 case reachable while the server is up, unless eviction stays limited to finished jobs as GAP-S02 specifies. |
| **Found** | 2026-11-23 |

## Summary
Both pollers assume every poll returns a 200 job snapshot. There is no error exit for the two other outcomes the server and network can produce:
- **A transport error** (server restarted, crashed, or stopped): `fetch` rejects. The poller's `await` throws, the `tick` promise rejects unhandled, and polling stops silently. Screen 2 freezes at its last percentage with **Next** disabled. The run view freezes with no exits at all, because R4 hides the footer and the banner appears only on `done`.
- **HTTP 404 `{"error":"no such job"}`**: `api.get` parses the error body as if it were a snapshot. `pollDownload` renders `undefined MB`, finds no terminal status, and re-arms its 150 ms timer, polling forever (~6–7 requests/s). `pollRun` throws `TypeError` on `j.log.length` and freezes.

## Evidence
`app.js:7-8`: `api.get` never checks `r.ok`, and has no `catch`:
```js
const api = {
  get: p => fetch(p).then(r => r.json()),
```
`app.js:155-172` (`pollDownload`): the snapshot is rendered before any status check, and anything other than the four known statuses re-arms the timer:
```js
    const j = await api.get(`/api/jobs/${jobId}`);                 // :157  throws on transport error -> unhandled
    $('dl-fill').style.width = `${Math.round(j.progress * 100)}%`; // :158  NaN% on a 404 body
    $('dl-done').textContent = `${j.doneMB} MB`;                    // :159  "undefined MB"
    ...
    if (j.status === 'failed') { ... }                             // :161
    if (j.status === 'cancelled') return show('source');           // :165  HARD-04
    if (j.status === 'done') { ... }                               // :166
    setTimeout(tick, 150);                                         // :172  404 body -> forever
```
`app.js:413-426` (`pollRun`):
```js
    const j = await api.get(`/api/jobs/${jobId}`);                 // :414  transport error -> unhandled
    const log = $('run-log');
    for (; shown < j.log.length; shown++) {                        // :416  404 body -> TypeError
```
`Api.cs:118-124`: an unknown job id is a 404 with an error body, not a snapshot:
```csharp
        app.MapGet("/api/jobs/{id}", (string id) => { var job = Jobs.Get(id);
            return job == null
                ? Results.Json(new { error = "no such job" }, Json.Web, statusCode: 404)
```
`Jobs.cs:70` and `Api.cs:11`: the job store and package sessions are in-memory, so a server restart forgets every job, and the next poll that reaches a live server gets this 404.

## Impact
- **Run view (Screen 5B):** the job is lost, and the user is left with a frozen log, no banner, no Finish and no Back. Only a reload recovers, and it loses every selection. This is the same dead end as `pollrun-failed-job-dead-ends-run-screen`, reached by a different path.
- **Screen 2:** after a transport error, the bar freezes, for example at 15% (real-restart transcript below), and "continues automatically when complete" stays on screen indefinitely. Cancel is the only exit, and nothing tells the user why the download stopped. After a 404, the poller spins against the server forever (19 polls in 3 s), with `undefined MB` on screen.
- **Reachability:** a transport error mid-run is a restart or crash in headless/browser use, which is the documented verification mode (CLAUDE.md "kill a running app instance…"). A 404 needs a poll to reach a *live* server that no longer knows the id. Today that means a restart completing between two polls, which is unlikely. It becomes reachable whenever job eviction lands (BUG-05 / GAP-S02). The real-restart run below hit the transport-error case.

## How to reproduce
### A. Automated: browser transcript (amendment A6: no JS test runner)
`agents/frontend/repro/job-pollers-no-exit-on-404-or-network-error.js`:
```js
// RED transcript: pollDownload / pollRun have no error exit. A 404 job (what Api.cs returns
// for an unknown id, e.g. after a server restart) makes pollDownload spin at ~6-7 req/s
// forever rendering "undefined MB"; pollRun throws and freezes; a transport error leaves an
// unhandled rejection and a frozen screen.
// Usage: CLEANDRIVER_BASE=http://localhost:4780 node job-pollers-no-exit-on-404-or-network-error.js
//   (server: dotnet run --project nvcleanstall -- --headless --mock-catalog)
const { chromium } = require('playwright');
const BASE = process.env.CLEANDRIVER_BASE || 'http://127.0.0.1:4805';
const NOT_FOUND = { status: 404, contentType: 'application/json', body: '{"error":"no such job"}' };

async function boot(browser) {
  const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
  if (process.env.PATCHED_APPJS) await page.route('**/app.js', r => r.fulfill({ path: process.env.PATCHED_APPJS, contentType: 'application/javascript' })); // candidate-fix check only
  const ev = { errors: [], alerts: [], polls: 0 };
  page.on('pageerror', e => ev.errors.push(e.message));
  page.on('dialog', d => { ev.alerts.push(d.message()); d.dismiss(); });
  page.on('request', r => { if (/\/api\/jobs\//.test(r.url())) ev.polls++; });
  await page.addInitScript(() => {
    window.__rejections = [];
    window.addEventListener('unhandledrejection', e => window.__rejections.push(String(e.reason)));
  });
  await page.goto(BASE + '/');
  await page.waitForFunction(() => document.getElementById('combo-ver').textContent !== '');
  return { page, ev };
}
const snap = page => page.evaluate(() => ({
  screen: document.querySelector('.screen.active').id,
  done: document.getElementById('dl-done').textContent,
  rejections: window.__rejections,
}));

(async () => {
  const browser = await chromium.launch();
  let red = 0;

  // A. Screen 2, job vanished (404).
  {
    const { page, ev } = await boot(browser);
    await page.route('**/api/jobs/**', r => r.fulfill(NOT_FOUND));
    await Promise.all([page.waitForResponse(r => r.url().endsWith('/api/download')), page.click('#btn-next')]);
    await page.waitForTimeout(500);
    const p0 = ev.polls; await page.waitForTimeout(3000); const p1 = ev.polls;
    const s = await snap(page);
    console.log(`[A download/404] screen=${s.screen} dl-done="${s.done}" polls in 3 s=${p1 - p0} alerts=${JSON.stringify(ev.alerts)}`);
    const ok = (p1 - p0) <= 1 && s.screen !== 'screen-download';
    console.log(`[A download/404] ${ok ? 'GREEN' : 'RED'}: poller stopped and left Screen 2: ${ok} (expected true)`);
    if (!ok) red++;
    await page.close();
  }

  // B. Screen 2, server unreachable (transport error).
  {
    const { page, ev } = await boot(browser);
    await page.route('**/api/jobs/**', r => r.abort('connectionrefused'));
    await Promise.all([page.waitForResponse(r => r.url().endsWith('/api/download')), page.click('#btn-next')]);
    await page.waitForTimeout(2000);
    const s = await snap(page);
    console.log(`[B download/net] screen=${s.screen} polls=${ev.polls} unhandledrejections=${JSON.stringify(s.rejections)} alerts=${JSON.stringify(ev.alerts)}`);
    const ok = s.rejections.length === 0 && s.screen !== 'screen-download';
    console.log(`[B download/net] ${ok ? 'GREEN' : 'RED'}: error surfaced and Screen 2 left: ${ok} (expected true)`);
    if (!ok) red++;
    await page.close();
  }

  // C/D. Run screen, job vanished (404) / transport error.
  for (const [tag, handler] of [['C run/404', r => r.fulfill(NOT_FOUND)], ['D run/net', r => r.abort('connectionrefused')]]) {
    const { page, ev } = await boot(browser);
    await page.click('#btn-next');
    await page.waitForSelector('#screen-components.active', { timeout: 15000 });
    await page.click('#btn-next'); await page.click('#btn-next');   // -> tweaks -> action
    await page.route('**/api/jobs/**', handler);                    // job lost after Start
    await Promise.all([page.waitForResponse(r => r.url().endsWith('/api/execute')),
      page.click('#btn-next')]);                                     // Start
    await page.waitForTimeout(2000);
    const s = await snap(page);
    const exits = await page.evaluate(() => ['btn-finish', 'btn-open-folder', 'btn-back', 'btn-next']
      .filter(id => { const el = document.getElementById(id); const r = el.getBoundingClientRect();
        return r.width > 0 && getComputedStyle(el).visibility !== 'hidden'; }));
    console.log(`[${tag}] screen=${s.screen} visible exits=${JSON.stringify(exits)} pageerrors=${JSON.stringify(ev.errors)} unhandledrejections=${JSON.stringify(s.rejections)} alerts=${JSON.stringify(ev.alerts)}`);
    const ok = exits.length > 0;
    console.log(`[${tag}] ${ok ? 'GREEN' : 'RED'}: run screen offers an exit after the job is lost: ${ok} (expected true)`);
    if (!ok) red++;
    await page.close();
  }
  await browser.close();
  console.log(red ? `RESULT: RED (${red}/4 cases)` : 'RESULT: GREEN');
})();
```
Run against the real app:
```bash
dotnet run --project nvcleanstall -- --headless --mock-catalog          # :4780
CLEANDRIVER_BASE=http://localhost:4780 node job-pollers-no-exit-on-404-or-network-error.js
```
The harness used `:4805`. `PATCHED_APPJS` is a harness-only candidate-fix hook, unset for red runs.

Captured **RED** output (unmodified `app.js`):
```
[A download/404] screen=screen-download dl-done="undefined MB" polls in 3 s=19 alerts=[]
[A download/404] RED: poller stopped and left Screen 2: false (expected true)
[B download/net] screen=screen-download polls=1 unhandledrejections=["TypeError: Failed to fetch"] alerts=[]
[B download/net] RED: error surfaced and Screen 2 left: false (expected true)
[C run/404] screen=screen-run visible exits=[] pageerrors=["Cannot read properties of undefined (reading 'length')"] unhandledrejections=["TypeError: Cannot read properties of undefined (reading 'length')"] alerts=[]
[C run/404] RED: run screen offers an exit after the job is lost: false (expected true)
[D run/net] screen=screen-run visible exits=[] pageerrors=["Failed to fetch"] unhandledrejections=["TypeError: Failed to fetch"] alerts=[]
[D run/net] RED: run screen offers an exit after the job is lost: false (expected true)
RESULT: RED (4/4 cases)
```
Real server restart, no route injection. The harness killed and restarted its own server about 1 s after Next on Screen 1 (`agents/frontend/dbg-restart.js`):
```
download started; restart the server now
after restart: {"screen":"screen-download","done":"123 MB","fill":"15%"} job poll status counts: {"200":6,"failed":1} polls in last 2 s: 0
```
One poll failed at the transport level, polling stopped, and 11 s later the page still showed Screen 2 at 15%.

### B. Manual — running app (headless mode)
1. Run `dotnet run --project nvcleanstall -- --headless --mock-catalog` and open http://localhost:4780 in a browser.
2. Click **Next** on Screen 1.
3. While the bar is moving, stop the server. Use Ctrl+C, or `taskkill.exe /F /IM CleanDriver.exe` from WSL.
4. Restart the server with the same command.
5. **Expected:** the download view reports the failure (for example "Download failed: lost contact with the CleanDriver server") and returns to Screen 1.
   **Actual:** the bar freezes at its last value with "continues automatically when complete" shown. DevTools shows `Uncaught (in promise) TypeError: Failed to fetch`. No further polls are made.
6. Run-view variant: go through to Screen 5, click **Start**, then repeat steps 3–4 during the run. The log freezes and there is no Finish and no Back.

## Root cause
The pollers have no error model. `api.get` returns `r.json()` whatever the status, so a 404 error body reaches the caller looking like a successful snapshot. Neither `tick` catches a rejected `fetch`. Each poller decides when to stop only by matching known `status` strings, so a response with *no* status falls into "keep polling" (`pollDownload`) or into code that dereferences snapshot fields (`pollRun`).

## Remedy
Add one helper that turns "no job" and "no server" into the terminal `failed` status. Both pollers then use their existing failure branches. The `pollRun` branch is added by `pollrun-failed-job-dead-ends-run-screen`, shown here as context:
```diff
@@ app.js:145 @@
 /* ---------- screen 2: download ---------- */

+// A poll that cannot reach the server, or gets back no job, is a failed job (terminal).
+async function pollJob(jobId) {
+  try {
+    const j = await api.get(`/api/jobs/${jobId}`);
+    return j && j.status ? j : { status: 'failed', error: j?.error || 'unknown job', progress: 0, doneMB: 0, log: [] };
+  } catch {
+    return { status: 'failed', error: 'lost contact with the CleanDriver server', progress: 0, doneMB: 0, log: [] };
+  }
+}
+
@@ app.js:157 (pollDownload) @@
-    const j = await api.get(`/api/jobs/${jobId}`);
+    const j = await pollJob(jobId);
@@ app.js:414 (pollRun) @@
-    const j = await api.get(`/api/jobs/${jobId}`);
+    const j = await pollJob(jobId);
@@ app.js:426 (pollRun — from pollrun-failed-job-dead-ends-run-screen) @@
     if (j.status === 'running') return setTimeout(tick, 200);
+    if (j.status === 'failed') { alert('Run failed: ' + (j.error || 'unknown error')); return show('action'); }
```
- **Why minimal:** one helper plus two call-site swaps. The existing `failed` branches do the rest: an alert, then back to Screen 1 or Screen 5A (HARD-04 precedent: "the `failed` branch keeps its alert"). The synthetic snapshot carries `progress`/`doneMB`/`log`, so the render lines before the status check stay valid and no `undefined MB` is left behind.
- **Alternatives rejected:**
  - Make `api.get` throw on `!r.ok`. Every caller would change, including boot, presets, and Browse, so this is a wider change. The helper keeps the blast radius to the two pollers.
  - Retry or back off on transport errors. That is new behavior with no ruling. After a restart the package session token is gone too (`Api.cs:11`), so a retry cannot resume.
  - Leave Screen 2 to its Cancel button. The 404 spin and the silent freeze would remain.
- **Fences:** no new screen, control, or dialog. Error text reaches only `alert()`. No JS toolchain.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active, and no entry covers client polling. A **new owner-pinned entry** is needed. Suggested: one "run-view and poller terminal states" entry shared with `pollrun-failed-job-dead-ends-run-screen`, with this slice landing second. Declare the no-test-surface slice: `slice_start --no-surface '^nvcleanstall/wwwroot/'` (sanctioned by that pattern appearing verbatim in the build plan).
1. **Slice setup.** After the sibling merges: `git worktree add ../nvcleanclone-poll-errors -b feat/job-pollers-no-exit-on-404-or-network-error origin/main`.
2. **Red.** Run the §A script against the unmodified app and save the RED transcript. Also repeat the manual §B restart once and paste it as live red. Both go in the PR body (HARD-04/HARD-06 precedent: no JS test location, no toolchain).
3. **Green.** Apply the diff. Rerun the script, which should produce the GREEN transcript below, and repeat §B. Run the full C# suite. Commit `job-pollers-no-exit-on-404-or-network-error: treat a lost job or server as a failed job in both pollers`.
4. **Refactor.** None.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` (unchanged count), `git diff --check`.
6. **Live exercise.** The green transcript plus manual §B in headless mode. Also check in the WebView2 window that the normal download, cancel, and run paths are unchanged, including HARD-04's silent server-cancel.
7. **PR.** Use the template, with one evidence row per case (A–D) plus the live-restart row. Name **amendment A6** and the dependency on the sibling. Push, and never merge.

GREEN transcript (same script, with the candidate diff, which already includes the sibling's `failed` branch, served through the harness `PATCHED_APPJS` hook):
```
[A download/404] screen=screen-source dl-done="0 MB" polls in 3 s=0 alerts=["Download failed: no such job"]
[A download/404] GREEN: poller stopped and left Screen 2: true (expected true)
[B download/net] screen=screen-source polls=1 unhandledrejections=[] alerts=["Download failed: lost contact with the CleanDriver server"]
[B download/net] GREEN: error surfaced and Screen 2 left: true (expected true)
[C run/404] screen=screen-action visible exits=["btn-back","btn-next"] pageerrors=[] unhandledrejections=[] alerts=["Run failed: no such job"]
[C run/404] GREEN: run screen offers an exit after the job is lost: true (expected true)
[D run/net] screen=screen-action visible exits=["btn-back","btn-next"] pageerrors=[] unhandledrejections=[] alerts=["Run failed: lost contact with the CleanDriver server"]
[D run/net] GREEN: run screen offers an exit after the job is lost: true (expected true)
RESULT: GREEN
```

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green. It cannot see JS, so green only proves the backend is untouched.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-poll-errors origin/feat/job-pollers-no-exit-on-404-or-network-error`; range `origin/main...HEAD`. The diff must touch only `nvcleanstall/wwwroot/app.js`.
3. **Re-run the gates and the §A script.** Expect RED on the parent, GREEN on the head. Also run a real restart (§B).
4. **Finder pass:**
   - Every `api.get('/api/jobs/…')` goes through `pollJob`. Grep for `/api/jobs/`.
   - Exactly one timer is armed per poller, and none after a terminal status.
   - HARD-04's `cancelled` branch is still silent.
   - A user-cancel on Screen 2 still returns without an alert. Note that `cancelled` is checked only *before* the `await` (`app.js:156`). A poll already in flight when Cancel is clicked can still come back `failed` and alert on Screen 1. The pre-existing `failed` branch has the same race. Decide whether a post-`await` re-check (`if (cancelled || state.screen !== 'download') return;`) belongs in this slice, and disclose it if added.
   - The synthetic snapshot fields keep the renders valid.
   - No unhandled rejections remain. Listen for `unhandledrejection`.
5. **Adversarial verify:**
   - `page.route` answering 500 with an HTML body. `r.json()` rejects, so this must also reach the `catch`.
   - A 200 response with `{}`.
   - An abort only on the 3rd poll, mid-run.
   - Cancel on Screen 2 while polls are failing.
   - A server restart during the `/api/package` POST after `done`. Out of scope here: it remains an unguarded `api.post`. Note it as a finding if it reproduces.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review it before relaying. Only the owner merges.

## Acceptance criteria
1. The §A script prints `GREEN` for A–D. Each case produces zero unhandled rejections, zero page errors, and exactly one alert.
2. A 404 job on Screen 2 stops polling within one tick (≤1 poll over 3 s after it), and the app returns to Screen 1 with "Download failed: no such job".
3. A transport error on Screen 2 or the run view alerts "…lost contact with the CleanDriver server" and returns to Screen 1 or Screen 5A respectively.
4. A real server restart during a headless download (§B) ends on Screen 1 with that alert, not a frozen bar.
5. Normal download, user cancel, HARD-04 server-cancel (silent), and successful runs are unchanged.
6. The diff is confined to `nvcleanstall/wwwroot/app.js` and lands after `pollrun-failed-job-dead-ends-run-screen`. `dotnet build`, `dotnet test` (unchanged count) and `git diff --check` are green.
