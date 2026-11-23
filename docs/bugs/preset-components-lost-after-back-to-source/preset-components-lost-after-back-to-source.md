# A preset loaded on Screen 1 after Back is applied to the stale package, then its components are silently discarded

| Field | Value |
|---|---|
| **Slug** | `preset-components-lost-after-back-to-source` |
| **Severity** | Medium. The preset is applied only in part, silently: its tweaks take effect but its component choices are replaced by defaults, which violates FR-015 ("restoring every component and tweak choice"). The user is not warned. |
| **Confidence** | Confirmed: reproduced live in a browser (Playwright/Chromium), with a control run in the same script, and the candidate fix checked green. |
| **Area** | `nvcleanstall/wwwroot/app.js` (Load-preset handler, `adoptManifest`, Back handler) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. Reproduced in Chromium; the logic does not depend on `?shell=1`. |
| **Related register entries** | FR-015, FEAT-028. Sibling of `preset-on-screen1-freezes-download-at-100`, which follows the same "load on Screen 1" path but has a different root cause. **This fix must land after that one**: it sends more loads through the `pendingPreset` path, and that path crashes on a stale `hiComp` until the sibling is fixed. Not covered by GAP-C03 (that entry is server-side). |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R09a** — Wizard frontend, pass 1: robustness (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
Back from Screen 3 returns to Screen 1 but keeps `state.manifest` loaded. The Load-preset handler treats "a manifest exists" as "the preset can be applied now", so on Screen 1 it applies the preset to the old package directly and skips the `pendingPreset` deferral. Leaving Screen 1 always re-adopts a package, even for the same version: Next re-downloads it, and `adoptManifest` resets `state.components` to the recommended defaults. With nothing pending, the preset's components are overwritten. The user arrives on Screen 3 with the preset's tweaks but the default components, and nothing tells them the preset was only half-applied.

## Evidence
`app.js:54` and `app.js:72-75`: Back from Screen 3 goes straight to Screen 1 and clears nothing:
```js
  components: { step: 'Step <b>3</b> of 5 — Components', back: 'source', next: 'Next' },
...
$('btn-back').onclick = () => {
  const target = FOOTER[state.screen].back;
  if (target) show(target);
};
```
By contrast, **Finish** does clear it (`app.js:452-458`: `state.manifest = null;`). After Back, `state.manifest` still points at the package the user is about to replace.

`app.js:518-519`: the Load-preset handler decides only on `state.manifest`, not on the screen:
```js
      if (state.manifest) applySelection(preset.selection);                                   // taken after Back
      else { state.pendingPreset = preset.selection; applySelection(preset.selection); }       // deferral skipped
```
`app.js:129-134` and `app.js:166-169`: leaving Screen 1 always fetches and adopts a package. `app.js:186-190`: adoption resets components and only re-applies a *pending* preset:
```js
  state.components = comps.filter(c => c.required || c.recommended).map(c => c.id);   // :186  preset's components gone
  if (state.pendingPreset) { ... }                                                    // :187  null on this path
```
`app.js:468`: tweaks live outside the manifest (`state.tweaks = { ...(sel.tweaks || {}) }`), so they survive the reset. That is why the outcome is a half-applied preset and not a clean no-op.

## Impact
- Silent incorrectness on a core flow. "Go back, pick a different driver version, load my preset" is a natural sequence. The user sees Screen 3 with defaults and may proceed without noticing that the preset's component removals (telemetry, GeForce Experience, and so on) did not take effect. For **Extract** and **Build package** this changes what is written to disk.
- Tweaks and components now come from two different sources: the preset's tweaks and the default components. A preset saved from this state is therefore a mixture the user never chose.
- Likelihood: any Back to Screen 1, then **Load preset…**, then **Next**. The version does not have to change.

## How to reproduce
### A. Automated — browser transcript (amendment A6: no JS test runner)
`agents/frontend/repro/preset-components-lost-after-back-to-source.js`. The control step loads the same preset while on Screen 3, which proves the preset itself is fine:
```js
// RED transcript: a preset loaded on Screen 1 after Back from Screen 3 loses its components.
// Usage: CLEANDRIVER_BASE=http://localhost:4780 node preset-components-lost-after-back-to-source.js
//   (server: dotnet run --project nvcleanstall -- --headless --mock-catalog)
const { chromium } = require('playwright');
const BASE = process.env.CLEANDRIVER_BASE || 'http://127.0.0.1:4805';
const PRESET = 'repro-gfe-' + Date.now().toString(36);

const checked = page => page.evaluate(() =>
  [...document.querySelectorAll('#comp-list .crow')]
    .filter(r => r.querySelector('.cb').classList.contains('on'))
    .map(r => r.querySelector('.cname').firstChild.textContent.trim()));
const rowCb = (page, name) => page.locator('#comp-list .crow', { hasText: name }).locator('.cb');

async function toComponents(page) {
  await page.click('#btn-next');                          // Screen 1 -> download (mock: ~5 s)
  await page.waitForSelector('#screen-components.active', { timeout: 15000 });
}
async function loadPreset(page, name) {
  await page.click('#btn-load-preset');
  await page.click(`#preset-pop .prow[data-name="${name}"]`);
  await page.waitForTimeout(300);
}

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
  if (process.env.PATCHED_APPJS) await page.route('**/app.js', r => r.fulfill({ path: process.env.PATCHED_APPJS, contentType: 'application/javascript' })); // candidate-fix check only
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  page.on('dialog', d => d.dismiss());
  await page.goto(BASE + '/');
  await page.waitForFunction(() => document.getElementById('combo-ver').textContent !== '');

  // 1. Build a distinctive selection on Screen 3 and save it as a preset.
  await toComponents(page);
  await rowCb(page, 'GeForce Experience').click();        // auto-selects Container + Telemetry
  await rowCb(page, 'PhysX System Software').click();     // deselect a recommended one
  const saved = await checked(page);
  await page.click('#btn-save-preset');
  await page.fill('#preset-name', PRESET);
  await page.click('#preset-save-go');
  await page.waitForTimeout(300);
  console.log('saved preset components :', JSON.stringify(saved));

  // 2. Control: reset to Recommended, load the preset while ON Screen 3 -> restored.
  await page.click('#btn-recommended');
  await loadPreset(page, PRESET);
  const control = await checked(page);
  console.log('control (load on Screen 3):', JSON.stringify(control),
    JSON.stringify(control) === JSON.stringify(saved) ? '-> restored' : '-> NOT restored');

  // 3. Defect: reset to Recommended, go Back to Screen 1 (the old manifest stays in state),
  //    load the same preset there, Next (same version, no other change).
  await page.click('#btn-recommended');
  await page.click('#btn-back');                           // Screen 3 -> Screen 1
  await page.waitForSelector('#screen-source.active');
  await loadPreset(page, PRESET);
  await toComponents(page);
  const after = await checked(page);
  console.log('after Back + load + Next :', JSON.stringify(after));
  const ok = JSON.stringify(after) === JSON.stringify(saved);
  console.log(`${ok ? 'GREEN' : 'RED'}: preset components restored after Back->Load->Next: ${ok} (expected true); pageerrors=${errors.length}`);
  await browser.close();
  console.log(ok ? 'RESULT: GREEN' : 'RESULT: RED');
})();
```
To run it against the real app:
```bash
dotnet run --project nvcleanstall -- --headless --mock-catalog          # :4780
CLEANDRIVER_BASE=http://localhost:4780 node preset-components-lost-after-back-to-source.js
```
(The harness used `:4805`. `PATCHED_APPJS` is a hook used only by the harness, for candidate fixes; it is unset for red runs.)

Captured **RED** output from the unmodified `app.js`:
```
saved preset components : ["Display Driver","HD Audio Driver","GeForce Experience","Telemetry Services","Container Runtime Services"]
control (load on Screen 3): ["Display Driver","HD Audio Driver","GeForce Experience","Telemetry Services","Container Runtime Services"] -> restored
after Back + load + Next : ["Display Driver","PhysX System Software","HD Audio Driver"]
RED: preset components restored after Back->Load->Next: false (expected true); pageerrors=0
RESULT: RED
```

### B. Manual — running app
1. Open http://localhost:4780 (or the WebView2 window). Click **Next** and wait for Screen 3.
2. Tick **GeForce Experience** (this auto-selects Container Runtime + Telemetry) and untick **PhysX System Software**. Click **Save preset…**, enter `gfe`, then **Save**.
3. Click **Recommended**. The list returns to Display Driver, PhysX, and HD Audio.
4. Click **Back**. You are now on Screen 1.
5. Click **Load preset…** and choose `gfe`. Then click **Next** and wait for the download.
6. **Expected:** Screen 3 shows `gfe`'s components: Display Driver, HD Audio, GeForce Experience, Telemetry, Container Runtime.
   **Actual:** Screen 3 shows the defaults (Display Driver, PhysX, HD Audio). Screen 4 does show `gfe`'s tweaks.
7. Control: on Screen 3, **Load preset…** with `gfe` restores the components correctly.

## Root cause
The handler uses a stale proxy for "a package is current". `state.manifest !== null` stays true after Back to Screen 1, but on Screens 1 and 2 the loaded package is guaranteed to be replaced: leaving Screen 1 always re-adopts, and adoption resets components. The deferral mechanism, `pendingPreset`, exists for exactly this case, but its guard reads the wrong signal.

## Remedy
Defer whenever the user is on a screen that precedes package adoption, and apply directly only on Screens 3–5:
```diff
@@ app.js:518 @@ $('btn-load-preset').onclick
       if (preset.error) return alert(preset.error);
-      if (state.manifest) applySelection(preset.selection);
+      if (state.manifest && state.screen !== 'source' && state.screen !== 'download') applySelection(preset.selection);
       else { state.pendingPreset = preset.selection; applySelection(preset.selection); }
```
- **Why minimal:** one condition changes. The existing `pendingPreset` path does all the work. Tweaks are still applied immediately, as they are today on a fresh Screen 1, and the components are applied on adoption.
- **Ordering dependency (verified in the harness):** with this change alone, a deferred preset is applied in `adoptManifest` while `hiComp` still names the *previous* package's row. Back, load preset, switch to a local package whose ids differ, Next then crashes (`TypeError … reading 'name'`, the app stays on Screen 1), and it would not crash without the change. The sibling's one-line fix (`preset-on-screen1-freezes-download-at-100`) removes that crash, so land it first. Harness check of the same sequence:
  ```
  unmodified app.js                     -> screen-components  selected ["Display Driver"]                 (preset lost)
  this fix only                         -> screen-source      pageerrors ["Cannot read properties of undefined (reading 'name')"]
  sibling fix + this fix                -> screen-components  selected ["Display Driver","Companion App"]  pageerrors []
  ```
  With both fixes, the §A script is also GREEN.
- **Alternatives rejected:**
  - Clear `state.manifest`/`state.token` on Back to Screen 1. This is a wider behavior change: Save preset on Screen 1 after Back would then record `[]` components, and other code paths read `state.manifest`.
  - Skip re-adoption when the version is unchanged. That is a larger, separate change and does not cover a version switch.
- **Fences:** no new screen, control, or dialog. Ruling R3 (Back hidden during download) is untouched. No JS toolchain.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active, and no existing GAP covers client-side preset application, so a **new owner-pinned entry** is needed (it can be shared with `preset-on-screen1-freezes-download-at-100` as separate, ordered slices). Declare the slice with `slice_start --no-surface '^nvcleanstall/wwwroot/'`, sanctioned by that pattern appearing verbatim in the build plan.
   > *Pinned 2026-11-23 as **GAP-R09a** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** After the sibling merges, run `git worktree add ../nvcleanclone-preset-back -b feat/preset-components-lost-after-back-to-source origin/main`.
2. **Red.** Run the §A script against the unmodified app and save the RED transcript. It goes in the PR body (HARD-04/HARD-06 precedent: no JS test location, no toolchain).
3. **Green.** Apply the diff and rerun the script, which should produce the GREEN transcript below. Run the full C# suite. Commit as `preset-components-lost-after-back-to-source: defer presets loaded before package adoption`.
4. **Refactor.** None.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` (test count unchanged), `git diff --check`.
6. **Live exercise.** The green transcript, plus manual §B in the WebView2 window, repeated with a version switch on step 5 (choose 566.36: the USB-C row is absent, and the preset's other components are restored).
7. **PR.** Use the template. The evidence row is "preset loaded on Screen 1 after Back restores its components | browser transcript | RED | GREEN". Name **amendment A6** and state the ordering dependency. Push; never merge.

GREEN transcript (the same script with the candidate diff, served through the harness `PATCHED_APPJS` hook):
```
saved preset components : ["Display Driver","HD Audio Driver","GeForce Experience","Telemetry Services","Container Runtime Services"]
control (load on Screen 3): ["Display Driver","HD Audio Driver","GeForce Experience","Telemetry Services","Container Runtime Services"] -> restored
after Back + load + Next : ["Display Driver","HD Audio Driver","GeForce Experience","Telemetry Services","Container Runtime Services"]
GREEN: preset components restored after Back->Load->Next: true (expected true); pageerrors=0
RESULT: GREEN
```

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green. It does not exercise JS.
2. **Isolated worktree.** Run `git worktree add ../nvcleanclone-review-preset-back origin/feat/preset-components-lost-after-back-to-source`, with range `origin/main...HEAD`. The diff must touch only `nvcleanstall/wwwroot/app.js`, and `origin/main` must already contain the sibling `hiComp` fix.
3. **Re-run gates and the §A script.** Expect RED on the parent commit and GREEN on the head.
4. **Finder pass.** Check each of the following:
   - Every screen is classified correctly: `source` and `download` defer; `components`, `tweaks`, `action`, and `run` apply now.
   - `pendingPreset` is consumed exactly once.
   - A preset loaded on Screen 3 still applies immediately (the control line).
   - Tweaks are applied in both branches.
   - No interaction with Cancel on Screen 2: a deferred preset survives Cancel, then Next.
5. **Adversarial verify.** Try each of these:
   - Back, load preset A, load preset B, Next. B must win.
   - Back, load, Cancel during download, Next.
   - Back, load, switch to a local package.
   - Load during Screen 2 on a second pass.
   - Load on the run view, then Finish.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review it before relaying. Only the owner merges.

## Acceptance criteria
1. The §A script prints `GREEN: preset components restored after Back->Load->Next: true`. The control line still prints `-> restored`.
2. Back, Load preset, Next with a *different* version restores the preset's components that exist in that version's manifest, plus the required ones.
3. A preset loaded on Screens 3, 4, or 5 still applies immediately, with no re-download.
4. Tweaks from the preset are applied in every case; Screen 4 shows them.
5. The diff is confined to `nvcleanstall/wwwroot/app.js`, and lands after `preset-on-screen1-freezes-download-at-100`. `dotnet build`, `dotnet test` (test count unchanged), and `git diff --check` are green.
