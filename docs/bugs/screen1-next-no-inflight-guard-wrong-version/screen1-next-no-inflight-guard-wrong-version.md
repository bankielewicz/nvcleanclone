# Screen 1 "Next" has no in-flight guard: a second press during the live lookup starts a second download and adopts the wrong version's job

| Field | Value |
|---|---|
| **Slug** | `screen1-next-no-inflight-guard-wrong-version` |
| **Severity** | Low. In live mode this produces duplicate real downloads (one is orphaned and keeps running with no Cancel), and the progress shown belongs to a different version than the label. Nothing is executed or corrupted. |
| **Confidence** | Confirmed by a live browser repro (Playwright/Chromium) with **injected latency**: a `page.route` delay on `POST /api/download` stands in for the uncached live NVIDIA lookup. App code and backend are unmodified. The candidate fix was checked green. |
| **Area** | `nvcleanstall/wwwroot/app.js` (`leaveSource`, `pollDownload`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. Reachable in **live** mode with a matched GPU. With `--mock-catalog`, `/api/download` answers in about 20 ms, faster than a human double-press (measured in the harness). |
| **Related register entries** | BUG-04 (server `ActiveDownloads` check-then-set). This doc is distinct: it covers *different-version* downloads, which the server idempotency does not cover by design, and a client that adopts the wrong job. Also related to BUG-02 / GAP-C02 (artifacts correlated by version). |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R09a** — Wizard frontend, pass 1: robustness (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`leaveSource` awaits `POST /api/download` with no in-flight guard and no feedback. During that wait **Next** stays enabled and the version combo stays usable. In live mode the wait is long, because `/api/download` runs the full NVIDIA lookup on every call (no cache, 5 s timeout).

If the user picks another version and presses **Next** again, two downloads start. Each response starts its own `pollDownload`. `pollDownload` reads `state.version` instead of the version its job is downloading, so both pollers label the screen with the *last* selected version. Whichever job finishes first posts `/api/package` for that last version and moves the wizard to Screen 3. The other poller stops when it sees the screen change. Its job is never adopted and never cancelled. In live mode that is a second ~800 MB real download still running in the background.

## Evidence
`app.js:129-134`: there is no guard, and the version is read once for the request, then again later by the poller:
```js
async function leaveSource() {
  if (state.sourceKind === 'catalog') {
    const dl = await api.post('/api/download', { version: state.version });   // :131  slow in live mode; Next still clickable
    if (dl.error) return alert(dl.error);
    show('download');
    pollDownload(dl.jobId);                                                    // :134  one poller per response
```
`app.js:146-149` and `app.js:167`: the poller trusts `state.version`, not its job:
```js
  const rel = state.releases.find(r => r.version === state.version);   // :147  label/total from the *current* selection
  ...
      const r = await api.post('/api/package', { kind: 'catalog', version: state.version });   // :167  adopts the current selection
```
`app.js:153`: each poller overwrites the single Cancel handler, so only the last job is cancellable. `app.js:156`: a poller stops when the screen changes, so the losing job is abandoned silently.

The latency comes from the backend. `Api.cs:81` calls `catalog.GetReleases(Gpu.Detect())` on every `/api/download`. `NvidiaCatalogProvider.cs:34-56,66-74` performs a synchronous HTTP lookup per call with no cache, under the 5 s timeout set at `:18`/`:28`. `Jobs.cs:209-213` coalesces only a *same-version* live download (`ActiveDownloads[version]`), so two versions give two real streams.

## Impact
- **Live mode:** two real driver downloads run in parallel. The one that finishes first (job A, version X) triggers adoption of version Y's package before Y's bytes exist. Y keeps downloading in the background with no UI and no Cancel. The GAP-04 receipt enrichment (`Jobs.DownloadedFile(Y)`) then depends on whether Y happens to finish before **Start**.
- **Display:** Screen 2 shows "Y — Y's size" while the bar tracks X's job. The transcript shows the 566.36 label while polling 572.16.
- **Same-version double press** is coalesced server-side for live releases (`Jobs.cs:209-213`), but the client still runs two pollers on one job. Both can reach `done` together and each POSTs `/api/package`, creating two server sessions. That last point is reasoned, not reproduced.
- **Likelihood:** low to moderate. It needs live mode plus a slow lookup with no visible feedback, which is exactly when users press again.

## How to reproduce
### A. Automated — browser transcript (amendment A6: no JS test runner)
`agents/frontend/repro/screen1-next-no-inflight-guard-wrong-version.js`. `LOOKUP_MS = 1500` is the injected stand-in for the live lookup:
```js
// RED transcript: Screen 1 "Next" has no in-flight guard. While POST /api/download is
// pending (in live mode it performs the uncached NVIDIA lookup, 5 s timeout) the user can
// change the version and press Next again: two downloads start, the first job to finish
// adopts the OTHER version's package (pollDownload reads state.version at completion), and
// the second download is orphaned (never cancelled, never adopted).
// The route delay below stands in for the live lookup latency; the app code is unmodified.
// Usage: CLEANDRIVER_BASE=http://localhost:4780 node screen1-next-no-inflight-guard-wrong-version.js
//   (server: dotnet run --project nvcleanstall -- --headless --mock-catalog)
const { chromium } = require('playwright');
const BASE = process.env.CLEANDRIVER_BASE || 'http://127.0.0.1:4805';
const LOOKUP_MS = 1500;

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
  if (process.env.PATCHED_APPJS) await page.route('**/app.js', r => r.fulfill({ path: process.env.PATCHED_APPJS, contentType: 'application/javascript' })); // candidate-fix check only
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  page.on('dialog', d => d.dismiss());
  const downloads = [];            // [{version, jobId}]
  const packages = [];             // versions POSTed to /api/package
  const lastSnap = {};             // jobId -> last snapshot seen
  page.on('response', async r => {
    const u = r.url();
    try {
      if (u.endsWith('/api/download')) downloads.push({ version: JSON.parse(r.request().postData()).version, jobId: (await r.json()).jobId });
      else if (u.endsWith('/api/package')) packages.push(JSON.parse(r.request().postData()).version);
      else { const m = u.match(/\/api\/jobs\/(\w+)$/); if (m) lastSnap[m[1]] = await r.json(); }
    } catch {}
  });
  await page.route('**/api/download', async r => { await new Promise(s => setTimeout(s, LOOKUP_MS)); await r.continue(); });

  await page.goto(BASE + '/');
  await page.waitForFunction(() => document.getElementById('combo-ver').textContent !== '');
  await page.click('#btn-next');                                    // Next on 572.16 ...
  await page.waitForTimeout(400);
  const still = await page.evaluate(() => ({ screen: document.querySelector('.screen.active').id,
    nextDisabled: document.getElementById('btn-next').classList.contains('disabled') }));
  console.log(`400 ms after Next: screen=${still.screen} nextDisabled=${still.nextDisabled} (no feedback, still clickable)`);
  await page.click('#combo-field');
  await page.click('#combo-menu .row:nth-child(5)');                // ... user switches to 566.36
  await page.click('#btn-next');                                    // ... and presses Next again
  await page.waitForSelector('#screen-download.active', { timeout: 10000 });
  await page.waitForTimeout(300);
  const label = await page.evaluate(() => document.getElementById('dl-sub').textContent);
  await page.waitForSelector('#screen-components.active', { timeout: 20000 });
  const firstDone = Object.values(lastSnap).filter(s => s.status === 'done').map(s => s.version);
  await page.waitForTimeout(1500);

  console.log('downloads started:', JSON.stringify(downloads));
  console.log('Screen 2 label while polling the FIRST job:', JSON.stringify(label));
  console.log('job(s) done when Screen 3 appeared:', JSON.stringify(firstDone), '| POST /api/package versions:', JSON.stringify(packages));
  const orphan = downloads.find(d => !firstDone.includes(d.version));
  if (orphan) {
    const s = await (await fetch(`${BASE}/api/jobs/${orphan.jobId}`)).json();
    console.log(`orphaned job ${orphan.jobId} (${orphan.version}): status=${s.status} progress=${Math.round(s.progress * 100)}% — never cancelled, never adopted`);
  }
  const adoptedMismatch = packages.length > 0 && firstDone.length === 1 && packages[0] !== firstDone[0];
  const ok = downloads.length === 1 && !adoptedMismatch;
  console.log(`${ok ? 'GREEN' : 'RED'}: downloads started=${downloads.length} (expected 1); adopted package version != finished download version: ${adoptedMismatch} (expected false); pageerrors=${errors.length}`);
  await browser.close();
  console.log(ok ? 'RESULT: GREEN' : 'RESULT: RED');
})();
```
Run it against the real app:
```bash
dotnet run --project nvcleanstall -- --headless --mock-catalog          # :4780 (the delay is injected client-side)
CLEANDRIVER_BASE=http://localhost:4780 node screen1-next-no-inflight-guard-wrong-version.js
```
The harness used `:4805`. `PATCHED_APPJS` is a harness-only candidate-fix hook; it is unset for red runs.

Captured **RED** output (unmodified `app.js`):
```
400 ms after Next: screen=screen-source nextDisabled=false (no feedback, still clickable)
downloads started: [{"version":"572.16","jobId":"87"},{"version":"566.36","jobId":"88"}]
Screen 2 label while polling the FIRST job: "566.36 WHQL — 774 MB"
job(s) done when Screen 3 appeared: ["572.16"] | POST /api/package versions: ["566.36"]
orphaned job 88 (566.36): status=done progress=100% — never cancelled, never adopted
RED: downloads started=2 (expected 1); adopted package version != finished download version: true (expected false); pageerrors=0
RESULT: RED
```

### B. Manual — running app (live mode)
1. On a machine whose GPU is in `data/nvidia-pfid-map.json`, run `dotnet run --project nvcleanstall` (live catalog, so no `--mock-catalog`). Screen 1 shows live releases and no "Sample catalog" note.
2. Click **Next**. While nothing visible has happened yet (the live lookup takes hundreds of ms to seconds), open the version list, pick a different version, and click **Next** again.
3. **Expected:** one download, of the version you ended on, and its progress is what's shown.
   **Actual:** `output\drivers\` receives two `.part` files (reasoned from `Jobs.cs:209-216`; no live download was run for this report). Screen 2 labels the second version while showing the first one's progress. When the first finishes, Screen 3 opens, and the second download continues in the background with no way to cancel it.
4. In mock mode the window (about 20 ms) is too short to hit by hand. Use §A, or turn on DevTools network throttling ("Slow 3G" adds about 2 s per request), which delays `/api/download` enough to repeat step 2 by hand.

## Root cause
The code treats a user action that starts an asynchronous, server-side effect as if it were instantaneous. Nothing records that a request is pending, so it can be issued twice. The poller derives "which version is this job" from mutable UI state (`state.version`) rather than from the request that created the job.

## Remedy
Allow one Screen 1 request at a time, and bind the download to the version it was requested for:
```diff
@@ app.js:129 @@
-async function leaveSource() {
+let leaving = false;   // one Screen 1 request in flight at a time
+async function leaveSource() {
+  if (leaving) return;
+  leaving = true;
+  try { await leaveSourceOnce(); } finally { leaving = false; }
+}
+
+async function leaveSourceOnce() {
   if (state.sourceKind === 'catalog') {
-    const dl = await api.post('/api/download', { version: state.version });
+    const version = state.version;             // the version this download is for
+    const dl = await api.post('/api/download', { version });
     if (dl.error) return alert(dl.error);
+    state.version = version;                   // a combo change while pending must not re-target it
+    renderCombo();
     show('download');
     pollDownload(dl.jobId);
```
- **Why this is minimal:** a flag and a captured value. `pollDownload` is unchanged, because on Screen 2 `state.version` can no longer drift: the combo is hidden, and the only writer before `show('download')` is the restore above. The local-package branch gets the same guard against a double `/api/package`.
- **Alternatives rejected:**
  - *Add the `disabled` class to Next while pending.* It is visual only. `.btn.disabled` is `pointer-events:none` (`styles.css:107`), so a keyboard Enter would still fire. It is fine as an *addition*, but not as the fix.
  - *Pass `version` into `pollDownload`.* It works, but it touches more lines, and `state.version` would still disagree with the job for anything that reads it later (Back, Finish/`renderCombo`).
  - *Cache the lookup server-side.* That shortens the window but does not close it, and it is out of area (`NvidiaCatalogProvider`). Listed as a lead.
- **Fences:** no new control, screen, or dialog. R3 (Back hidden during download, Cancel the sole return path) is unchanged. No JS toolchain.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active. GAP-C02 ("Create same-version downloads atomically …") is the server half, and this client half is not in it. Fold it into GAP-C02 as an explicit client line item, or pin a new owner entry. Declare the no-test-surface slice: `slice_start --no-surface '^nvcleanstall/wwwroot/'` (sanctioned by that pattern appearing verbatim in the build plan).
   > *Pinned 2026-11-23 as **GAP-R09a** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-next-guard -b feat/screen1-next-no-inflight-guard-wrong-version origin/main`.
2. **Red.** Run the §A script against the unmodified app and save the RED transcript (PR body; HARD-04/HARD-06 precedent: no JS test location, no toolchain).
3. **Green.** Apply the diff, rerun the script, and expect the GREEN transcript below. Run the full C# suite. Commit `screen1-next-no-inflight-guard-wrong-version: one Screen 1 request in flight; bind the download to its version`.
4. **Refactor.** None.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` (unchanged count), `git diff --check`.
6. **Live exercise.** The green transcript, plus manual §B in live mode on a matched GPU in the WebView2 window: one `.part`, and the label matches the progress. This is a **real download** (GAP-02 permits bytes to disk, never executed). Cancel it after the check.
7. **PR.** Use the template. TDD evidence row: "one download per Next; adopted version == downloaded version | browser transcript with injected lookup latency | RED | GREEN". Name **amendment A6** and say plainly that the latency is injected. Push; never merge.

GREEN transcript (same script, with the candidate diff served through the harness `PATCHED_APPJS` hook):
```
400 ms after Next: screen=screen-source nextDisabled=false (no feedback, still clickable)
downloads started: [{"version":"572.16","jobId":"89"}]
Screen 2 label while polling the FIRST job: "572.16 WHQL — 812 MB"
job(s) done when Screen 3 appeared: ["572.16"] | POST /api/package versions: ["572.16"]
GREEN: downloads started=1 (expected 1); adopted package version != finished download version: false (expected false); pageerrors=0
RESULT: GREEN
```

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green. It does not exercise JS.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-next-guard origin/feat/screen1-next-no-inflight-guard-wrong-version`. Range: `origin/main...HEAD`. The diff should touch only `nvcleanstall/wwwroot/app.js`.
3. **Re-run gates and the §A script.** Expect RED on main and GREEN on the head.
4. **Finder pass:**
   - `leaving` is reset on every exit path: the `alert` returns, the throws inside `api.post`, and the local branch. The `finally` covers all of these.
   - Nothing else can start a download.
   - `renderCombo` after the restore doesn't reopen the menu (`state.comboOpen` is unchanged).
   - A failed `/api/download` (`dl.error`) leaves Screen 1 usable and unlocked.
   - R3 is intact.
5. **Adversarial verify:**
   - Triple-press Next with a 3 s injected delay.
   - Switch the source radio to local mid-flight, then press Next.
   - Press Enter on the focused Next button repeatedly.
   - Make the `/api/download` route abort, then confirm the guard releases and Next works again.
   - Same-version double press. Expect one poller (count `/api/jobs/` polls per 150 ms).
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review it before relaying. Only the owner merges.

## Acceptance criteria
1. The §A script prints `GREEN`: `downloads started=1`, and the adopted package version equals the finished download's version.
2. While a Screen 1 request is pending, further Next presses (mouse or keyboard) issue no request.
3. If the version is changed while the request is pending, the wizard proceeds with the requested version, and the combo shows it again on return to Screen 1.
4. After a failed `/api/download` or `/api/package` (alert), Next works again immediately.
5. The diff is confined to `nvcleanstall/wwwroot/app.js`. `dotnet build`, `dotnet test` (unchanged count), and `git diff --check` are green.
