# A failed install/extract/build job leaves the run view with no way out

| Field | Value |
|---|---|
| **Slug** | `pollrun-failed-job-dead-ends-run-screen` |
| **Severity** | Medium: the wizard dead-ends on a routine user error, such as a bad output folder or an incomplete local package. Nothing is corrupted, but the only escape is an undiscoverable page reload. |
| **Confidence** | Confirmed: live browser repro (Playwright/Chromium), in both plain and `?shell=1` mode, against the real backend failing the job by itself (no injection). The candidate fix was checked green against the same script. |
| **Area** | `nvcleanstall/wwwroot/app.js` (`pollRun`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. Reproduced in Chromium with and without `?shell=1`. The WebView2 shell uses the same engine (reasoned). |
| **Related register entries** | Ruling R4: its exits ("Open output folder" and "Finish") never appear on failure. HARD-02 (`Jobs.cs:396-403` fails a drive-root run *by job status*, "the caller polls a job"). Sibling of HARD-04, which fixed the same "status with no terminal branch" defect in `pollDownload`. Also sibling of `job-pollers-no-exit-on-404-or-network-error` (transport and 404 errors), which builds on this fix. Distinct from `build-into-source-folder-destroys-package`: that doc is about what the backend *does* to files, and this one is about what the UI does once the job fails. |
| **Found** | 2026-11-23 |

## Summary
`pollRun` handles only two job statuses, `running` and `done`. The backend's third terminal status, `failed`, matches no branch. When it arrives, the poll loop stops silently. The run view keeps its "Extracting customized package…" title, a partial progress bar, and an `> ERROR:` line in the log. It shows no result banner, so there is no **Finish** and no **Open output folder**. The footer is also hidden by ruling R4, so there is no **Back**. The user has no control that leaves the screen. The only escape is a page reload (F5), which nothing on screen suggests and which discards the whole wizard state.

## Evidence
`app.js:426-440`: the terminal branches cover `done` only. `failed` falls through and the function returns with no timer and no UI change:
```js
    if (j.status === 'running') return setTimeout(tick, 200);   // :426

    if (j.status === 'done') {                                  // :428
      $('run-status').classList.remove('hidden');
      ...
      $('run-banner').classList.remove('hidden');               // :438  the only exits (Finish / Open output folder)
```
`app.js:57`: the run view's footer is hidden unconditionally (R4):
```js
  run:        { step: 'Step <b>5</b> of 5 — Action', hidden: true }, // R4: footer hidden while running / after completion
```
`styles.css:287`: `.foot-actions.hidden{visibility:hidden; ...}`. Hidden Back/Start are neither clickable nor focusable.

The backend fails jobs **by status** on purpose and expects the UI to poll for it:
- `Jobs.cs:586-591`: any exception in a step sets `job.Status = "failed"`, `job.Error`, and logs `> ERROR: …`.
- `Jobs.cs:396-403`, HARD-02: "Fails the job by name rather than throwing: the caller polls a job".

The download poller already has the matching branch (`app.js:161`: `if (j.status === 'failed') { alert('Download failed: ' + …); return show('source'); }`). HARD-04's CHANGELOG entry notes that "the `failed` branch keeps its alert". `pollRun` never got one.

Ruling R4: "Screen 5 Frame B hides footer Back/Start after completion; 'Open output folder' and 'Finish' are the exits." R4 hides the footer *because* the banner provides the exits. On failure the banner never appears, so the run view has zero exits. That breaks the design contract's premise: no state of the frozen five-screen wizard is meant to be a dead end.

Reachable by ordinary input (each confirmed with the real backend unless marked):
1. A local package whose manifest lists a payload missing from `payload/`. `Packages.cs:111` (folder) or `:106` (zip) throws `PayloadMissing` during **Extract only** or **Build package** (this repro).
2. An **Output folder** that is an existing file, an unwritable folder, or a path on a missing drive. `Directory.CreateDirectory` throws (confirmed by curl: `failed | Could not find a part of the path '…/not-a-folder.txt/payload'`).
3. A drive root such as `C:\` (HARD-02, `Jobs.cs:396-403`). Reasoned only, and not exercised, per CLAUDE.md's drive-root rule. The disabled Output-folder field is still submitted for **Install** and **Silent** (`app.js:389`; `Jobs.cs:386-388` computes `outDir` for every action before the root check). So a root typed for Extract and then left in place makes an Install fail here too.

## Impact
- The wizard is unusable for the rest of the session. In the WebView2 window nothing hints at the escape: the F5 accelerator is on by default because `ShellForm.cs` sets no `CoreWebView2Settings` (reasoned). Reloading loses every selection.
- The screen looks like it is still working: the title still reads "…ing…" and the bar stays at the last progress value. Users are likely to wait rather than retry.
- Likelihood is high for Extract and Build package. A mistyped output path or an incomplete local package is a normal user error, and each one triggers this.

## How to reproduce
### A. Automated: browser transcript (amendment A6: no JS test runner)
Script: `agents/frontend/repro/pollrun-failed-job-dead-ends-run-screen.js`, with fixture `fixtures/missing-payload/` (a valid manifest whose `physx.bin` payload is absent):
```js
// RED transcript: a failed execute job leaves the run screen with no exit.
// Usage: CLEANDRIVER_BASE=http://localhost:4780 node pollrun-failed-job-dead-ends-run-screen.js
//   (server: dotnet run --project nvcleanstall -- --headless --mock-catalog)
//   FIXTURE_ROOT / OUT_DIR override the paths as the SERVER sees them (Windows: C:/...).
const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');

const BASE = process.env.CLEANDRIVER_BASE || 'http://127.0.0.1:4805';
const FIX = process.env.FIXTURE_ROOT || path.join(__dirname, 'fixtures');
const OUT = process.env.OUT_DIR || path.join(__dirname, 'out', 'pollrun-failed');

(async () => {
  if (!process.env.OUT_DIR) fs.mkdirSync(OUT, { recursive: true });
  const browser = await chromium.launch();
  let red = 0;
  for (const mode of ['browser', 'shell']) {
    const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
    if (process.env.PATCHED_APPJS) await page.route('**/app.js', r => r.fulfill({ path: process.env.PATCHED_APPJS, contentType: 'application/javascript' })); // candidate-fix check only
    const errors = [], alerts = [];
    let pollsAfterFail = 0, failedSeen = false;
    page.on('pageerror', e => errors.push(e.message));
    page.on('dialog', d => { alerts.push(d.message()); d.dismiss(); });
    page.on('response', async r => {
      if (!/\/api\/jobs\//.test(r.url())) return;
      if (failedSeen) pollsAfterFail++;
      try { if ((await r.json()).status === 'failed') failedSeen = true; } catch {}
    });
    await page.goto(BASE + (mode === 'shell' ? '/?shell=1' : '/'));
    await page.waitForFunction(() => document.getElementById('combo-ver').textContent !== '');

    // Screen 1: local package whose manifest names a payload the folder does not contain.
    await page.click('#opt-local .opt-head');
    await page.fill('#local-path', path.join(FIX, 'missing-payload').replace(/\\/g, '/'));
    await page.click('#btn-next');                                  // -> components
    await page.waitForSelector('#screen-components.active');
    await page.click('#btn-next');                                  // -> tweaks
    await page.click('#btn-next');                                  // -> action
    await page.click('.acard[data-action="extract"]');
    await page.fill('#out-path', OUT.replace(/\\/g, '/'));
    await page.click('#btn-next');                                  // Start
    await page.waitForSelector('#screen-run.active');
    await page.waitForTimeout(3000);                                // job fails at the write step

    const st = await page.evaluate(() => {
      const vis = el => { const r = el.getBoundingClientRect(); const cs = getComputedStyle(el);
        return r.width > 0 && r.height > 0 && cs.visibility !== 'hidden' && cs.display !== 'none'; };
      const exits = ['btn-finish', 'btn-open-folder', 'btn-back', 'btn-next']
        .filter(id => vis(document.getElementById(id)));
      return {
        screen: document.querySelector('.screen.active').id,
        lastLog: [...document.querySelectorAll('#run-log .ln')].map(l => l.textContent).pop(),
        title: document.getElementById('run-title').textContent,
        statusShown: vis(document.getElementById('run-status')),
        bannerShown: vis(document.getElementById('run-banner')),
        footerVisibility: getComputedStyle(document.getElementById('foot-actions')).visibility,
        exits,
      };
    });
    console.log(`[${mode}] screen=${st.screen} title="${st.title}"`);
    console.log(`[${mode}] last log line: ${st.lastLog}`);
    console.log(`[${mode}] job failed=${failedSeen}; polls after failure=${pollsAfterFail}; alerts=${JSON.stringify(alerts)}; pageerrors=${errors.length}`);
    console.log(`[${mode}] banner shown=${st.bannerShown} status shown=${st.statusShown} footer visibility=${st.footerVisibility}`);
    const ok = st.exits.length > 0;
    console.log(`[${mode}] ${ok ? 'GREEN' : 'RED'}: visible exits on run screen after a failed job: ${st.exits.length} ${JSON.stringify(st.exits)} (expected >= 1)`);
    if (!ok) red++;
    await page.close();
  }
  await browser.close();
  console.log(red ? `RESULT: RED (${red}/2 modes dead-ended)` : 'RESULT: GREEN');
  process.exit(0);
})();
```
The fixture `fixtures/missing-payload/manifest.json` declares `display-driver` (required, payload present) and `physx` (recommended, payload file **absent** from `payload/`).

Run it against the real app:
```bash
dotnet run --project nvcleanstall -- --headless --mock-catalog          # serves :4780
CLEANDRIVER_BASE=http://localhost:4780 FIXTURE_ROOT=C:/path/to/fixtures OUT_DIR=C:/Temp/cd-out \
  node pollrun-failed-job-dead-ends-run-screen.js
```
The harness ran it on port 4805: `CLEANDRIVER_URLS=http://127.0.0.1:4805 dotnet out/Server.dll --mock-catalog`. Paths below are shortened to `<harness>`. (`PATCHED_APPJS` is a harness-only hook that serves a candidate `app.js` via `page.route`. It is unset for red runs.)

Captured **RED** output (unmodified `app.js`):
```
[browser] screen=screen-run title="Extracting customized package…"
[browser] last log line: > ERROR: package rejected: a selected payload is missing
[browser] job failed=true; polls after failure=0; alerts=[]; pageerrors=0
[browser] banner shown=false status shown=false footer visibility=hidden
[browser] RED: visible exits on run screen after a failed job: 0 [] (expected >= 1)
[shell] screen=screen-run title="Extracting customized package…"
[shell] last log line: > ERROR: package rejected: a selected payload is missing
[shell] job failed=true; polls after failure=0; alerts=[]; pageerrors=0
[shell] banner shown=false status shown=false footer visibility=hidden
[shell] RED: visible exits on run screen after a failed job: 0 [] (expected >= 1)
RESULT: RED (2/2 modes dead-ended)
```

### B. Manual: running app
1. `dotnet run --project nvcleanstall` (WebView2 window) or `-- --headless`, then open http://localhost:4780.
2. Screen 1: leave the catalog selected and click **Next**. Wait for the download to finish.
3. Screen 3: **Next**. Screen 4: **Next**.
4. Screen 5: choose **Extract only**. In **Output folder**, type the path of an existing *file* (for example, create `C:/Temp/not-a-folder.txt` first). Click **Start**.
5. **Expected:** the run ends with a visible failure, and you can get back to Screen 5 to fix the folder and retry.
   **Actual:** the log ends in `> ERROR: Could not find a part of the path '…\not-a-folder.txt\payload'.`. The title still says "Extracting customized package…". There is no banner, no Finish, no Back. Nothing on the page is actionable. Only F5 recovers, and it loses all selections.

## Root cause
`pollRun` treats "not running" as "done" by omission. It was written against the two statuses the happy path produces, so it misses the backend's third terminal status, which the backend deliberately uses for user-input failures (HARD-02). Ruling R4 then removes the footer because the success banner provides the exits. The failure path shows neither, so it has no exit at all.

## Remedy
Add the missing terminal branch, mirroring `pollDownload`'s existing `failed` handling (alert, then return to the screen the user came from). Screen 5A still holds the token, action, and output path, so the user can correct the folder and press **Start** again.
```diff
@@ app.js:424 @@ function pollRun(jobId) {
     $('run-fill').style.width = `${Math.round(j.progress * 100)}%`;

     if (j.status === 'running') return setTimeout(tick, 200);
+    if (j.status === 'failed') { alert('Run failed: ' + (j.error || 'unknown error')); return show('action'); }

     if (j.status === 'done') {
```
- **Why minimal:** one line, no new control, screen, or dialog. `alert()` is already the app's error surface (`app.js:132,137,139,161,168,391,503,517`), and HARD-04 kept exactly this alert for download failure. `show('action')` restores the footer through the existing `FOOTER.action` row.
- **Alternatives rejected:**
  - *Reuse the success banner with error text.* It carries a green check, a "Completed" status, and an "Open output folder" button for a folder that may not exist, so it would misrepresent the outcome.
  - *Un-hide the footer on the run view.* That creates a run-view state not in Frame B, so it needs an owner ruling.
  - *Return silently.* The error text lives only in the run log, which `show('action')` hides.
- **Fences:** frozen five-screen contract intact. R4 is unchanged for the success path. `j.error` goes through `alert()` (plain text), never `innerHTML`. No JS toolchain.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is the active register. No existing GAP covers UI failure handling (GAP-C01…C05 are server-side), so this needs a **new owner-pinned register entry** first. Suggest one "run-view terminal states" entry covering this doc and `job-pollers-no-exit-on-404-or-network-error`, whose `pollRun` part reuses this branch. This slice lands first. Declare the no-test-surface slice: `slice_start --no-surface '^nvcleanstall/wwwroot/'`, sanctioned by that pattern appearing verbatim in the build plan.
1. **Slice setup.** `git worktree add ../nvcleanclone-pollrun-failed -b feat/pollrun-failed-job-dead-ends-run-screen origin/main`.
2. **Red.** Start `dotnet run --project nvcleanstall -- --headless --mock-catalog`. Run the §A script against the unmodified app and save the RED transcript above verbatim as the PR's red evidence. The repo has no place for JS scripts and must not gain a toolchain, so the script and transcript live in the PR body. That is the HARD-04/HARD-06 precedent.
3. **Green.** Apply the one-line diff. Rerun the same script and expect the GREEN transcript below. Run the full C# suite to prove no backend regression. Commit `pollrun-failed-job-dead-ends-run-screen: treat a failed run job as terminal (alert + back to Screen 5)`.
4. **Refactor.** None needed. Touch nothing else unless the green transcript still passes.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, then `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` (count unchanged vs `origin/main`), then `git diff --check`.
6. **Live exercise.** Green transcript, plus manual §B in the WebView2 window (`dotnet run --project nvcleanstall`): the alert appears, Screen 5 returns, a corrected folder succeeds, and a normal Install still ends on the banner with the footer hidden (R4).
7. **PR.** Fill `.github/pull_request_template.md`. TDD evidence row: "failed run job is terminal | browser transcript `pollrun-failed-job-dead-ends-run-screen.js` | RED (2/2 modes dead-ended) | GREEN". State **amendment A6** by name. Push. Never merge.

GREEN transcript, the same script with the candidate diff served through the harness's `PATCHED_APPJS` hook (the PR's green run is against the edited file itself):
```
[browser] screen=screen-action title="Extracting customized package…"
[browser] last log line: > ERROR: package rejected: a selected payload is missing
[browser] job failed=true; polls after failure=0; alerts=["Run failed: package rejected: a selected payload is missing"]; pageerrors=0
[browser] banner shown=false status shown=false footer visibility=visible
[browser] GREEN: visible exits on run screen after a failed job: 2 ["btn-back","btn-next"] (expected >= 1)
[shell] screen=screen-action title="Extracting customized package…"
[shell] last log line: > ERROR: package rejected: a selected payload is missing
[shell] job failed=true; polls after failure=0; alerts=["Run failed: package rejected: a selected payload is missing"]; pageerrors=0
[shell] banner shown=false status shown=false footer visibility=visible
[shell] GREEN: visible exits on run screen after a failed job: 2 ["btn-back","btn-next"] (expected >= 1)
RESULT: GREEN
```

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green first. CI cannot see JS behavior: green CI proves only that the backend is untouched.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-pollrun origin/feat/pollrun-failed-job-dead-ends-run-screen`. Review range `origin/main...HEAD`. Confirm the diff touches only `nvcleanstall/wwwroot/app.js`.
3. **Re-run gates,** then re-run the §A script on the reviewer's checkout: RED on `origin/main`, GREEN on the head.
4. **Finder pass** (optionally `/code-review` on the PR diff as the finder). Check:
   - The `failed` branch is placed **after** the log/progress rendering, so the `> ERROR:` line is still appended before leaving.
   - No timer is re-armed on `failed`.
   - The R4 success path is byte-identical (footer hidden, banner shown, Finish works).
   - `j.error` reaches only `alert()`, never `innerHTML`.
   - No new element, screen, or dialog.
   - `state.token` is kept, so a retry from Screen 5A works without re-download.
5. **Adversarial verify.**
   - Fail via a missing payload, via an output path that is a file, and via `page.route` fulfilling `/api/jobs/*` with `{"status":"failed","error":"<img src=x onerror=alert(1)>","log":[]}`. The last must alert the literal text.
   - Run a failing Extract, then press Start again without changing anything. It must fail cleanly again, with no duplicated pollers.
   - Run a failing Build package, then a successful Install, in one session.
   - Repeat in `?shell=1`.
6. **Verdict document.** APPROVE or REQUEST CHANGES. Mark each finding CONFIRMED (reproduced) or PLAUSIBLE (reasoned).
7. **Feedback to builder.** Implementer handoff, then the fix loop: C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review it before relaying. Only the owner merges.

## Acceptance criteria
1. With the §A script against the fixed app, both `browser` and `shell` modes print `GREEN`. The alert text is `Run failed: <job.error>`, and Screen 5A is active with Back and Start visible.
2. From that state, changing the Output folder to a writable folder and pressing **Start** completes with the success banner, reusing the same package token with no re-download.
3. A successful Install, Silent, Extract, or Build run is unchanged: banner with Finish and Open output folder, footer hidden (R4).
4. An injected `failed` snapshot whose `error` contains markup is shown literally (alert), with no DOM injection.
5. The diff is confined to `nvcleanstall/wwwroot/app.js`. `dotnet build`, `dotnet test` (unchanged count), and `git diff --check` are green.
