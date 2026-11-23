# Loading a preset before the first package is loaded crashes `adoptManifest` and freezes the wizard on Screen 2

| Field | Value |
|---|---|
| **Slug** | `preset-on-screen1-freezes-download-at-100` |
| **Severity** | Medium. The documented "load it on a later run" preset flow (FR-015, AC-005) dead-ends the wizard for the whole session: retrying crashes the same way, and only a page reload recovers. No data is corrupted. |
| **Confidence** | Confirmed by a live browser repro (Playwright/Chromium) in 4/4 variants: catalog plain, catalog `?shell=1`, local package, and a load during the download. The stack trace was captured, and the candidate fix was checked green. |
| **Area** | `nvcleanstall/wwwroot/app.js` (`adoptManifest`, `applySelection`, `renderComponents`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. Reproduced in Chromium with and without `?shell=1`. |
| **Related register entries** | FR-015 and AC-005 ("restart app → load 'lean' → every selection restored exactly"). Not covered by GAP-C03 (server-side preset atomicity) or BUG-06. Sibling of `preset-components-lost-after-back-to-source`, which is a different root cause on the same "load on Screen 1" path. That doc's fix should land after this one. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R09a** — Wizard frontend, pass 1: robustness (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
On a fresh run, no manifest is loaded yet. If the user clicks **Load preset…** on Screen 1 (or during the Screen 2 download), the preset is parked in `state.pendingPreset` so it can be applied when the package arrives. When the package does arrive, `adoptManifest` applies the pending preset before it chooses the highlighted component row. `applySelection` re-renders the component list, and `renderComponents` dereferences the highlighted row, `compById(null)`, which is `undefined`. The resulting `TypeError` aborts `adoptManifest` before `show('components')`. The wizard stays on Screen 2 at 100% with Next disabled and "continues automatically when complete" showing, forever. For a local package it stays on Screen 1 with no message. `pendingPreset` is never cleared, so every retry (Cancel, then Next) crashes the same way.

## Evidence
`app.js:179-194` applies the preset (line 188) before `hiComp` is assigned (line 191):
```js
function adoptManifest(r) {
  state.token = r.token;
  state.manifest = r.manifest;
  ...
  state.components = comps.filter(c => c.required || c.recommended).map(c => c.id);   // :186
  if (state.pendingPreset) {
    applySelection(state.pendingPreset);                                              // :188  renders now
    state.pendingPreset = null;                                                       // :189  never reached
  }
  state.hiComp = comps.find(c => !c.required)?.id || comps[0].id;                     // :191  too late
  renderComponents();
  show('components');                                                                 // :193  never reached
}
```
`app.js:472`: `applySelection` renders whenever a manifest exists (`if (state.manifest) renderComponents();`).
`app.js:252-253`: `renderComponents` assumes a valid highlighted row:
```js
  const hi = compById(state.hiComp);          // state.hiComp === null on a fresh run (app.js:28)
  $('comp-rp-title').textContent = hi.name;   // TypeError
```
`app.js:518-519` is where the pending path is taken (`state.manifest` is null on a fresh run):
```js
      if (state.manifest) applySelection(preset.selection);
      else { state.pendingPreset = preset.selection; applySelection(preset.selection); }
```
Captured stack (from the harness):
```
TypeError: Cannot read properties of undefined (reading 'name')
    at renderComponents (http://127.0.0.1:4805/app.js:253:39)
    at applySelection (http://127.0.0.1:4805/app.js:472:23)
    at adoptManifest (http://127.0.0.1:4805/app.js:188:5)
    at leaveSource (http://127.0.0.1:4805/app.js:140:5)
```
The same crash also hits a *stale* `hiComp`. Finish (`app.js:452-458`) clears the manifest but not `hiComp`. If the user then loads a preset and picks a local package whose ids differ, `compById(oldId)` is `undefined`. This was confirmed in the harness: completing a run, then **Finish**, then **Load preset…**, then a local package with different ids, then **Next** gives `unmodified -> screen screen-source pageerrors ["Cannot read properties of undefined (reading 'name')"]`, and with the fix below gives `patched -> screen screen-components pageerrors []`.

## Impact
- FR-015 says a preset is "loadable on a later run". The natural place to load it is Screen 1, before choosing a driver, and the toolbar offers **Load preset…** there. Doing so breaks the run for good. AC-005 passes only if the tester happens to load on Screen 3/4.
- The freeze looks like a slow finish: 100% and "continues automatically when complete". In the WebView2 window there is no visible recovery. F5 works only because `ShellForm.cs` leaves default accelerators on, which is reasoned, not shown in the UI.
- Likelihood: every user who loads a preset before reaching Screen 3 on a fresh run hits it.

## How to reproduce
### A. Automated — browser transcript (amendment A6: no JS test runner)
`agents/frontend/repro/preset-on-screen1-freezes-download-at-100.js`. It seeds the preset through `POST /api/presets` (the same request **Save preset…** sends), so it is self-contained:
```js
// RED transcript: loading a preset on Screen 1 of a fresh run (before any manifest exists)
// makes adoptManifest throw, so the wizard never leaves Screen 2 (frozen at 100%).
// Usage: CLEANDRIVER_BASE=http://localhost:4780 node preset-on-screen1-freezes-download-at-100.js
//   (server: dotnet run --project nvcleanstall -- --headless --mock-catalog)
const { chromium } = require('playwright');
const BASE = process.env.CLEANDRIVER_BASE || 'http://127.0.0.1:4805';
const PRESET = 'repro-s1-' + Date.now().toString(36);

(async () => {
  // Seed a preset through the real API (what "Save preset…" posts), so the run is self-contained.
  const save = await fetch(BASE + '/api/presets', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: PRESET, selection: {
      components: ['display-driver', 'hd-audio', 'gfe', 'telemetry', 'container'],
      tweaks: { 'installer-telemetry': true, 'clean-install': true } } }),
  });
  console.log('seeded preset', PRESET, '->', save.status);

  const browser = await chromium.launch();
  let red = 0;
  for (const variant of ['catalog/browser', 'catalog/shell', 'local/browser', 'during-download/browser']) {
    const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
    if (process.env.PATCHED_APPJS) await page.route('**/app.js', r => r.fulfill({ path: process.env.PATCHED_APPJS, contentType: 'application/javascript' })); // candidate-fix check only
    const errors = [], alerts = [];
    page.on('pageerror', e => errors.push(e.message));
    page.on('dialog', d => { alerts.push(d.message()); d.dismiss(); });
    await page.goto(BASE + (variant.endsWith('shell') ? '/?shell=1' : '/'));
    await page.waitForFunction(() => document.getElementById('combo-ver').textContent !== '');

    // Fresh run, before any manifest exists: Load preset… -> pick it (FR-015 "load it on a
    // later run"). Screen 1 for most variants; Screen 2 (mid-download) for the last one.
    if (variant.startsWith('during-download')) {
      await page.click('#btn-next');
      await page.waitForSelector('#screen-download.active');
    }
    await page.click('#btn-load-preset');
    await page.click(`#preset-pop .prow[data-name="${PRESET}"]`);
    await page.waitForTimeout(300);
    if (variant.startsWith('local')) {
      await page.click('#opt-local .opt-head');
      await page.click('#btn-browse');                     // fills the bundled sample package path
      await page.waitForFunction(() => document.getElementById('local-path').value !== '');
    }
    if (!variant.startsWith('during-download')) await page.click('#btn-next');
    await page.waitForTimeout(variant.startsWith('local') ? 1500 : 8000); // mock download ~5 s

    const st = await page.evaluate(() => ({
      screen: document.querySelector('.screen.active').id,
      fill: document.getElementById('dl-fill').style.width,
      done: document.getElementById('dl-done').textContent,
      nextDisabled: document.getElementById('btn-next').classList.contains('disabled'),
    }));
    console.log(`[${variant}] screen=${st.screen} dl-fill=${st.fill} dl-done="${st.done}" nextDisabled=${st.nextDisabled} alerts=${JSON.stringify(alerts)}`);
    console.log(`[${variant}] pageerrors: ${JSON.stringify(errors)}`);
    const ok = st.screen === 'screen-components';
    console.log(`[${variant}] ${ok ? 'GREEN' : 'RED'}: reached Screen 3 after Load preset (no manifest yet) + Next: ${ok} (expected true)`);
    if (!ok) red++;
    await page.close();
  }
  await browser.close();
  console.log(red ? `RESULT: RED (${red}/4 variants stuck)` : 'RESULT: GREEN');
})();
```
Run against the real app:
```bash
dotnet run --project nvcleanstall -- --headless --mock-catalog          # :4780
CLEANDRIVER_BASE=http://localhost:4780 node preset-on-screen1-freezes-download-at-100.js
```
(The harness used `:4805`. `PATCHED_APPJS` is a harness-only hook for checking a candidate `app.js`, and is unset for red runs.)

Captured **RED** output, unmodified `app.js`:
```
seeded preset repro-s1-mwt9rmq3 -> 200
[catalog/browser] screen=screen-download dl-fill=100% dl-done="812 MB" nextDisabled=true alerts=[]
[catalog/browser] pageerrors: ["Cannot read properties of undefined (reading 'name')"]
[catalog/browser] RED: reached Screen 3 after Load preset (no manifest yet) + Next: false (expected true)
[catalog/shell] screen=screen-download dl-fill=100% dl-done="812 MB" nextDisabled=true alerts=[]
[catalog/shell] pageerrors: ["Cannot read properties of undefined (reading 'name')"]
[catalog/shell] RED: reached Screen 3 after Load preset (no manifest yet) + Next: false (expected true)
[local/browser] screen=screen-source dl-fill= dl-done="0 MB" nextDisabled=false alerts=[]
[local/browser] pageerrors: ["Cannot read properties of undefined (reading 'name')"]
[local/browser] RED: reached Screen 3 after Load preset (no manifest yet) + Next: false (expected true)
[during-download/browser] screen=screen-download dl-fill=100% dl-done="812 MB" nextDisabled=true alerts=[]
[during-download/browser] pageerrors: ["Cannot read properties of undefined (reading 'name')"]
[during-download/browser] RED: reached Screen 3 after Load preset (no manifest yet) + Next: false (expected true)
RESULT: RED (4/4 variants stuck)
```

### B. Manual — running app
1. Save a preset in any earlier session: reach Screen 3, then **Save preset…**, name it `lean`, and click **Save**.
2. Restart the app (`dotnet run --project nvcleanstall`) or reload http://localhost:4780.
3. On Screen 1, click **Load preset…** and choose `lean`.
4. Click **Next**.
5. **Expected:** the download completes and Screen 3 opens with `lean`'s components checked.
   **Actual:** Screen 2 stays at 100% / "812 MB of 812 MB" with **Next** disabled, indefinitely. DevTools shows `TypeError … at renderComponents`. **Cancel**, then **Next**, repeats the freeze. Only a reload recovers, and the preset is not applied.
6. Variant: on step 3, pick **Use driver files on disk**, then **Browse…**, then **Next**. Nothing happens and the app stays on Screen 1.

## Root cause
Initialization order inside `adoptManifest`. `applySelection` has a rendering side effect (`renderComponents`), and `renderComponents` requires `state.hiComp` to name a component of the *current* manifest. On the pending-preset path, `applySelection` runs before `hiComp` is set for the new manifest. Its value is still `null` on a fresh run, or a previous manifest's id after Finish. The error is never caught, and the line that clears `pendingPreset` comes after the throw, so the failure repeats every time.

## Remedy
Choose the highlighted row before anything renders:
```diff
@@ app.js:184 @@ function adoptManifest(r) {
   const comps = state.manifest.components;
   state.components = comps.filter(c => c.required || c.recommended).map(c => c.id);
+  state.hiComp = comps.find(c => !c.required)?.id || comps[0].id;
   if (state.pendingPreset) {
     applySelection(state.pendingPreset);
     state.pendingPreset = null;
   }
-  state.hiComp = comps.find(c => !c.required)?.id || comps[0].id;
   renderComponents();
   show('components');
```
- **Why minimal:** this moves one existing line and adds no code. Behavior on the no-preset path is identical, because `hiComp` gets the same value before the same `renderComponents()` call.
- **Alternatives rejected:**
  - *Make `renderComponents` tolerate a missing `hi`.* This hides the ordering bug, and a stale-but-valid id from the previous package would still highlight the wrong row.
  - *Stop `applySelection` from rendering.* Its other callers (load on Screens 3–5) rely on the render.
  - *Wrap `adoptManifest` in try/catch.* This treats the symptom, and the preset would still not be applied.
- **Fences:** no new screens, controls, or dialogs. Escaping is unaffected. No JS toolchain.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active. GAP-C03 is server-side preset I/O and does not cover this, so it needs a **new owner-pinned entry**, for example "client preset application (FR-015)". It can share an entry with `preset-components-lost-after-back-to-source`, but should be a separate slice that lands first. Declare the no-test-surface slice: `slice_start --no-surface '^nvcleanstall/wwwroot/'`, sanctioned by that pattern appearing verbatim in the build plan.
   > *Pinned 2026-11-23 as **GAP-R09a** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup.** `git worktree add ../nvcleanclone-preset-s1 -b feat/preset-on-screen1-freezes-download-at-100 origin/main`.
2. **Red.** Run the §A script against the unmodified app (`--headless --mock-catalog`) and save the RED transcript above. The script and transcript go in the PR body, because the repo has no JS test location (HARD-04/HARD-06 precedent).
3. **Green.** Apply the diff and rerun the same script. Expect the GREEN transcript below. Run the full C# suite. Commit with `preset-on-screen1-freezes-download-at-100: pick the highlighted component before applying a pending preset`.
4. **Refactor.** None.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` (unchanged count), `git diff --check`.
6. **Live exercise.** Green transcript, plus manual §B in the WebView2 window. Also re-run AC-005 end to end: Recommended, expert tweaks, NVIDIA Container, save "lean", restart, load "lean" **on Screen 1**, Next. Every selection must be restored.
7. **PR.** Use the template. Evidence row: "preset loaded before first manifest applies and reaches Screen 3 | browser transcript | RED (4/4 variants stuck) | GREEN". Name **amendment A6**. Push, and never merge.

GREEN transcript (the same script with the candidate diff, served through the harness `PATCHED_APPJS` hook):
```
seeded preset repro-s1-mwt9s8dz -> 200
[catalog/browser] screen=screen-components dl-fill=100% dl-done="812 MB" nextDisabled=false alerts=[]
[catalog/browser] pageerrors: []
[catalog/browser] GREEN: reached Screen 3 after Load preset (no manifest yet) + Next: true (expected true)
[catalog/shell] screen=screen-components dl-fill=100% dl-done="812 MB" nextDisabled=false alerts=[]
[catalog/shell] pageerrors: []
[catalog/shell] GREEN: reached Screen 3 after Load preset (no manifest yet) + Next: true (expected true)
[local/browser] screen=screen-components dl-fill= dl-done="0 MB" nextDisabled=false alerts=[]
[local/browser] pageerrors: []
[local/browser] GREEN: reached Screen 3 after Load preset (no manifest yet) + Next: true (expected true)
[during-download/browser] screen=screen-components dl-fill=100% dl-done="812 MB" nextDisabled=false alerts=[]
[during-download/browser] pageerrors: []
[during-download/browser] GREEN: reached Screen 3 after Load preset (no manifest yet) + Next: true (expected true)
RESULT: GREEN
```

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green. It cannot exercise JS, so it only proves the backend is untouched.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-preset-s1 origin/feat/preset-on-screen1-freezes-download-at-100`. Range: `origin/main...HEAD`. The diff must touch only `nvcleanstall/wwwroot/app.js`, and should be a single moved line.
3. **Re-run gates and the §A script.** It must be RED on `origin/main` and GREEN on the head.
4. **Finder pass** (optionally run `/code-review` on the diff):
   - `hiComp` is assigned from the *new* `comps` before every render path in `adoptManifest`.
   - `pendingPreset` is still cleared after it is applied.
   - The preset's components really are applied: the checked rows equal the preset ids filtered to the manifest, plus required.
   - The no-preset path renders identically.
   - Nothing else in `applySelection` dereferences state that is not yet initialized (for example, `state.tweakDefs` is set at boot).
5. **Adversarial verify:**
   - Load a preset during Screen 2, after **Finish**, and before switching to a local package with different ids.
   - Load a preset whose ids are all unknown to the manifest.
   - Load two presets back to back on Screen 1.
   - Run each in `?shell=1` too.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with each finding marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Fix loop: C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review before relaying. Only the owner merges.

## Acceptance criteria
1. The §A script prints `GREEN` for all four variants (catalog plain, catalog `?shell=1`, local, load during download), with `pageerrors: []`.
2. After a preset is loaded on Screen 1 of a fresh run, Screen 3 shows exactly the preset's components (filtered to the manifest, plus required) and Screen 4 shows its tweaks.
3. After **Finish**, loading a preset and then choosing a local package whose component ids differ reaches Screen 3 without an error.
4. The flow without a preset is unchanged: same default selection, same highlighted row (first non-required component), and same "682 MB of 832 MB" total on the bundled 572.16 package.
5. The diff is confined to `nvcleanstall/wwwroot/app.js`. `dotnet build`, `dotnet test` (unchanged count), and `git diff --check` are green.
