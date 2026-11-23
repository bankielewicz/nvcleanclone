# "Allow automatic reboot" stays in effect after its parent "Unattended express installation" is unchecked

| Field | Value |
|---|---|
| **Slug** | `auto-reboot-submitted-with-unattended-off` |
| **Severity** | Low today: the run is simulated and nothing reboots, but the receipt (the app's only audit artifact) and the log assert a reboot permission that the user's visible state disowns. Once GAP-C01 lands it becomes Medium, because the server is specified to *reject* an orphan `auto-reboot`, so an ordinary UI flow would then fail with a 400. |
| **Confidence** | Confirmed with a live browser repro (Playwright/Chromium). The submitted body, the run log, and the written receipt were all captured. The candidate fix was checked green. |
| **Area** | `nvcleanstall/wwwroot/app.js` (`renderTweaks` sub-row, `startExecute`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. Reproduced in Chromium; the logic does not depend on `?shell=1`. |
| **Related register entries** | FEAT-011 ("Toggle with auto-reboot sub-option"), FR-010. GAP-C01 lists "`auto-reboot` requires `unattended`" as server-side validation still to come, with the AC "orphan auto-reboot" red and "Valid UI and preset selections remain green". This bug means the UI itself *produces* the orphan, so GAP-C01's two ACs conflict until this is fixed. BUG-01 is the server half and is not restated here. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R09a** — Wizard frontend, pass 1: robustness (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
On Screen 4 the sub-option **Allow automatic reboot** is drawn as a dependent of **Unattended express installation**. When the parent is unchecked, the sub-row is greyed out and becomes unclickable (`disabled-block`: opacity .4, `pointer-events:none`), which tells the user the option is not in effect. Its value in `state.tweaks` does not change, though. If it was ticked before the parent was unticked, `startExecute` still submits `"auto-reboot": true` alongside `"unattended": false`. The backend then logs `> Automatic reboot allowed — not performed (simulated; no system reboot).` and writes `"autoReboot": true` into the receipt, for a run the user configured as interactive.

## Evidence
In `app.js:298-303`, unticking the parent flips only the parent:
```js
    row.querySelector('.cb').onclick = (e) => {
      e.stopPropagation();
      state.hiTweak = t.id;
      state.tweaks[t.id] = !on;          // the sub's value (state.tweaks['auto-reboot']) is left as is
      renderTweaks();
    };
```
In `app.js:306-314`, the sub-row is disabled under an unticked parent but still renders its stored value:
```js
    if (t.sub) {
      const parentOn = tweakOn(t.id);
      const subOn = state.tweaks[t.sub.id] === true;
      ...
      sub.className = 'trow sub' + (parentOn ? '' : ' disabled-block');    // styles.css:155 opacity:.4; pointer-events:none
```
In `app.js:383-384`, the raw map is submitted:
```js
async function startExecute() {
  const selection = { components: state.components, tweaks: state.tweaks };
```
The backend honors the flag independently of its parent. `Jobs.cs:471-473` appends the "Automatic reboot allowed" log line on `selection.TweakOn("auto-reboot")`, and `Jobs.cs:457-458` writes `unattended = …`, `autoReboot = selection.TweakOn("auto-reboot")` into the receipt.

The spec frames it as a *sub*-option: `Tweaks.cs:34-37` defines `Sub = new TweakSub("auto-reboot", …)`, and the description says to "Combine with 'Allow automatic reboot' to finish without interaction". GAP-C01 states the invariant outright: "`auto-reboot` requires `unattended`".

## Impact
- The receipt and the log misstate what the user configured: `unattended=false autoReboot=true`, plus a log line announcing that an automatic reboot was allowed. The receipt exists as the record of what a run would have done (FR-010), and here it is wrong.
- Presets saved in this state carry the orphan as well (`Save preset…` posts the same `state.tweaks`), so the error persists across runs.
- When GAP-C01 lands, the same clicks give `400` on **Start**. The only fix available to the user is to re-tick the parent, untick the greyed sub, and untick the parent again, a sequence nothing in the UI suggests.
- Likelihood: low to moderate. It takes the sequence "allow reboot, then decide against unattended", which is a plausible change of mind.

## How to reproduce
### A. Automated: browser transcript (amendment A6: no JS test runner)
Script: `agents/frontend/repro/auto-reboot-submitted-with-unattended-off.js`
```js
// RED transcript: "Allow automatic reboot" stays true (and is submitted) after its parent
// "Unattended express installation" is unchecked, even though the sub row is disabled.
// Usage: CLEANDRIVER_BASE=http://localhost:4780 node auto-reboot-submitted-with-unattended-off.js
//   (server: dotnet run --project nvcleanstall -- --headless --mock-catalog)
const { chromium } = require('playwright');
const fs = require('fs');
const BASE = process.env.CLEANDRIVER_BASE || 'http://127.0.0.1:4805';

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
  if (process.env.PATCHED_APPJS) await page.route('**/app.js', r => r.fulfill({ path: process.env.PATCHED_APPJS, contentType: 'application/javascript' })); // candidate-fix check only
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  page.on('dialog', d => d.dismiss());
  let execBody = null, lastJob = null;
  page.on('request', r => { if (r.url().endsWith('/api/execute')) execBody = JSON.parse(r.postData()); });
  page.on('response', async r => { if (/\/api\/jobs\//.test(r.url())) { try { lastJob = await r.json(); } catch {} } });

  await page.goto(BASE + '/');
  await page.waitForFunction(() => document.getElementById('combo-ver').textContent !== '');
  await page.click('#btn-next');                                   // -> download (mock ~5 s)
  await page.waitForSelector('#screen-components.active', { timeout: 15000 });
  await page.click('#btn-next');                                   // -> tweaks

  const sub = page.locator('#tweak-list .trow.sub');
  const parentCb = page.locator('#tweak-list .trow', { hasText: 'Unattended express installation' }).first().locator('.cb');
  await sub.locator('.cb').click();                                // 1. allow automatic reboot
  await parentCb.click();                                          // 2. untick unattended
  const ui = await page.evaluate(() => {
    const s = document.querySelector('#tweak-list .trow.sub');
    return { subChecked: s.querySelector('.cb').classList.contains('on'),
             subDisabled: s.classList.contains('disabled-block'),
             subOpacity: getComputedStyle(s).opacity, subPointer: getComputedStyle(s).pointerEvents };
  });
  console.log('sub row after unticking parent:', JSON.stringify(ui));

  await page.click('#btn-next');                                   // -> action (Install default)
  await page.click('#btn-next');                                   // Start
  await page.waitForSelector('#run-banner:not(.hidden)', { timeout: 15000 });

  const t = execBody.selection.tweaks;
  console.log('POST /api/execute tweaks: unattended =', t['unattended'], ', auto-reboot =', t['auto-reboot']);
  const rebootLines = lastJob.log.map(l => l.text).filter(x => /reboot/i.test(x));
  console.log('run log reboot lines:', JSON.stringify(rebootLines));
  if (lastJob.receipt && fs.existsSync(lastJob.receipt)) {
    const rc = JSON.parse(fs.readFileSync(lastJob.receipt, 'utf8'));
    console.log(`receipt ${lastJob.receipt}: unattended=${rc.unattended} autoReboot=${rc.autoReboot}`);
  }
  const ok = t['auto-reboot'] !== true;
  console.log(`${ok ? 'GREEN' : 'RED'}: auto-reboot submitted as true while its parent is off: ${!ok} (expected false); pageerrors=${errors.length}`);
  await browser.close();
  console.log(ok ? 'RESULT: GREEN' : 'RESULT: RED');
})();
```
Run it against the real app:
```bash
dotnet run --project nvcleanstall -- --headless --mock-catalog          # :4780
CLEANDRIVER_BASE=http://localhost:4780 node auto-reboot-submitted-with-unattended-off.js
```
The receipt line prints only when the receipt path is readable from where node runs. The harness ran on `:4805`. `PATCHED_APPJS` is a harness-only candidate-fix hook and is unset for red runs.

Captured **RED** output from the unmodified `app.js`:
```
sub row after unticking parent: {"subChecked":true,"subDisabled":true,"subOpacity":"0.4","subPointer":"none"}
POST /api/execute tweaks: unattended = false , auto-reboot = true
run log reboot lines: ["> Automatic reboot allowed — not performed (simulated; no system reboot)."]
receipt <harness>/server/out/output/receipt-2026-11-23.json: unattended=false autoReboot=true
RED: auto-reboot submitted as true while its parent is off: true (expected false); pageerrors=0
RESULT: RED
```

### B. Manual: running app
1. Open http://localhost:4780. Click **Next**, wait for the download, then **Next** to Screen 4.
2. Tick **Allow automatic reboot** (the indented sub-row under **Unattended express installation**).
3. Untick **Unattended express installation**. The sub-row greys out but still shows a tick.
4. Click **Next**, keep **Install**, then click **Start**.
5. **Expected:** the run is interactive with no reboot permission. The log has no "Automatic reboot allowed" line, and the receipt says `"autoReboot": false`.
   **Actual:** the log's last line is `> Automatic reboot allowed — not performed (simulated; no system reboot).`, and `output\receipt-<date>.json` contains `"unattended": false` together with `"autoReboot": true`.

## Root cause
The UI encodes the parent–sub dependency only in presentation (the disabled styling) and never in the selection it submits. `state.tweaks` is treated as the selection itself, when it is really the user's *remembered* choices. The effective value of a sub-option is "sub AND parent", and no code computes it.

## Remedy
At the point of submission, derive the effective tweaks: a sub-option whose parent is off is not in effect. The UI's remembered state stays as it is, so re-ticking the parent restores the user's earlier sub choice. That matches what the greyed-but-ticked row implies.
```diff
@@ app.js:381 @@
 $('btn-out-browse').onclick = () => $('out-path').focus();

+// A sub-option only takes effect under its parent (FEAT-011): never submit an orphan.
+function effectiveTweaks() {
+  const t = { ...state.tweaks };
+  for (const d of state.tweakDefs) if (d.sub && t[d.id] !== true) t[d.sub.id] = false;
+  return t;
+}
+
 async function startExecute() {
-  const selection = { components: state.components, tweaks: state.tweaks };
+  const selection = { components: state.components, tweaks: effectiveTweaks() };
```
- **Why it is minimal:** it touches one call site and adds one pure helper. It is generic over `TweakDef.Sub`, so a future sub-option is covered without new special cases. Render and preset behavior are unchanged.
- **Alternatives rejected:**
  - Clear the sub when the parent is unticked (`state.tweaks[t.sub.id] = false` in the parent's onclick). This forgets the user's choice, and it does not cover a loaded preset or a hand-edited one that already holds the orphan.
  - Also normalize on **Save preset…**. A preset should restore the screen exactly as saved (FR-015 "restoring every … tweak choice"), greyed tick included. The orphan only matters when the run executes.
  - A backend-only fix (GAP-C01). The server would then reject the UI's own request, which is the conflict described above.
- **Fences:** no new control, screen, or dialog. Screen 4 looks the same as in the mockup. No JS toolchain.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active. This belongs **with GAP-C01**, which names the invariant. Record it there as the client-side counterpart so that its "Valid UI … selections remain green" AC is achievable, or pin it as a new owner-approved entry ordered **before** GAP-C01. Declare the no-test-surface slice: `slice_start --no-surface '^nvcleanstall/wwwroot/'` (sanctioned by that pattern appearing verbatim in the build plan).
   > *Pinned 2026-11-23 as **GAP-R09a** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** `git worktree add ../nvcleanclone-auto-reboot -b feat/auto-reboot-submitted-with-unattended-off origin/main`.
2. **Red.** Run the §A script against the unmodified app and save the RED transcript. It goes in the PR body, since the repo has no JS test location and no toolchain (HARD-04/HARD-06 precedent).
3. **Green.** Apply the diff and rerun the script, expecting the GREEN transcript below. Run the full C# suite; mock receipts for selections *without* an orphan must stay byte-identical (the existing receipt pins). Commit `auto-reboot-submitted-with-unattended-off: submit a sub-tweak only when its parent is on`.
4. **Refactor.** None.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` (unchanged count), `git diff --check`.
6. **Live exercise.** The green transcript, plus manual §B in the WebView2 window. Also check that unattended + auto-reboot both ticked still logs "Automatic reboot allowed" and writes `autoReboot: true`.
7. **PR.** Use the template. TDD evidence row: "orphan auto-reboot not submitted | browser transcript | RED | GREEN". Name **amendment A6** and the GAP-C01 relationship. Push; never merge.

GREEN transcript (same script, candidate diff served through the harness `PATCHED_APPJS` hook):
```
sub row after unticking parent: {"subChecked":true,"subDisabled":true,"subOpacity":"0.4","subPointer":"none"}
POST /api/execute tweaks: unattended = false , auto-reboot = false
run log reboot lines: []
receipt <harness>/server/out/output/receipt-2026-11-23.json: unattended=false autoReboot=false
GREEN: auto-reboot submitted as true while its parent is off: false (expected false); pageerrors=0
RESULT: GREEN
```

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green. It cannot see JS, so it only proves the backend is untouched.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-auto-reboot origin/feat/auto-reboot-submitted-with-unattended-off`; range `origin/main...HEAD`. Confirm the diff touches only `nvcleanstall/wwwroot/app.js`.
3. **Re-run gates** and the §A script: RED on main, GREEN on the head.
4. **Finder pass:**
   - `effectiveTweaks` copies rather than mutates `state.tweaks`, so the screen still remembers the sub.
   - It is applied to `/api/execute` only. Presets stay faithful.
   - The parent check uses strict `=== true`, the same as `tweakOn`.
   - The sub is set to `false`, not deleted. The server's `TweakOn` treats both the same; `false` is explicit in the receipt's `tweaks` echo.
   - Non-sub tweaks, and `msi-priority` params, pass through unchanged.
5. **Adversarial verify:**
   - Load a crafted preset with `{"unattended":false,"auto-reboot":true}` and Start; the receipt must read `autoReboot=false`.
   - Toggle parent off, on, off and Start.
   - Tick both and Start; the log line must be present.
   - Silent install path.
   - `?shell=1`.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relaying. Only the owner merges.

## Acceptance criteria
1. The §A script prints `GREEN`: `POST /api/execute tweaks: unattended = false , auto-reboot = false`, no reboot line in the run log, and receipt `autoReboot=false`.
2. With both **Unattended** and **Allow automatic reboot** ticked, the run log still contains "Automatic reboot allowed — not performed…" and the receipt has `autoReboot=true`.
3. After unticking and re-ticking the parent, the sub-row shows the user's earlier choice. The UI state is not cleared.
4. A preset containing an orphan `auto-reboot: true` loads as saved, and a run started from it submits `auto-reboot: false`.
5. The diff is confined to `nvcleanstall/wwwroot/app.js`. `dotnet build`, `dotnet test` (unchanged count) and `git diff --check` are green.
