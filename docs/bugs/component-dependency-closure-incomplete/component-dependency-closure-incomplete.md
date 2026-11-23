# Screen 3 resolves component dependencies only one level deep and can deselect the required Display Driver

| Field | Value |
|---|---|
| **Slug** | `component-dependency-closure-incomplete` |
| **Severity** | Medium. With a local manifest (a stronger precondition), the UI itself submits selections whose dependencies do not resolve, and can drop the required component. The extract/build output then has a dangling `dependsOn` edge, or no required component at all. |
| **Confidence** | Confirmed with a live browser repro (Playwright/Chromium) against real local package folders the backend accepts (HTTP 200). The written `manifest.json` was inspected. The candidate fix was checked green, and checked for no change on the bundled catalog. |
| **Area** | `nvcleanstall/wwwroot/app.js` (`setComponent`, `adoptManifest`, `renderComponents`, Recommended, `applySelection`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. Reproduced in Chromium; the logic does not depend on `?shell=1`. |
| **Related register entries** | FR-007 / FEAT-009. **BUG-01** (server enforcement of required components and dependency edges) records that the JS side is the only enforcement. This doc is distinct: it shows the JS enforcement *itself* is wrong, so the UI builds the inconsistent selection that BUG-01 imagines coming from a direct API call. **GAP-C01** will make the server reject "missing dependency" and "missing display driver". Its AC "Valid UI … selections remain green" is not achievable until this is fixed. Distinct from `dependency-target-check-ignores-case`, which covers case-variant ids that crash `compById`; that one is backend-rooted. Matters more under **GAP-F03**, which will parse real `.nvi` `requires` graphs. |
| **Found** | 2026-11-23 |

## Summary
`setComponent` adds only the *direct* `dependsOn` of a ticked component, and removes only the *direct* dependents of an unticked one. The initial default selection, **Recommended**, and preset application do not close dependencies at all. The dependents rule also ignores `required`. Two consequences follow:
- **Chains break.** With `app → runtime → telemetry`, ticking *app* selects *runtime* but not *telemetry*. Unticking *telemetry* removes *runtime* but leaves *app* selected without it.
- **The required component can be dropped.** With `display-driver (required) → runtime`, the default selection omits *runtime*. Unticking *runtime* (after ticking it) removes **Display Driver** as a "dependent". The row stays visible, locked and unticked, and the run submits a package with no required component.

The bundled sample manifests have only one-level edges with no required-component dependencies, so the mockup flow never exposes this.

## Evidence
`app.js:198-219`. Both directions are one level deep, and the dependents filter does not exclude `required`:
```js
  if (on) {
    sel.add(id);
    const missing = (c.dependsOn || []).filter(d => !sel.has(d));          // :204  direct deps only
    if (missing.length) { missing.forEach(d => sel.add(d)); ... }
  } else {
    sel.delete(id);
    const dependents = state.manifest.components
      .filter(o => sel.has(o.id) && (o.dependsOn || []).includes(id));     // :211-212 direct dependents only, required included
    if (dependents.length) {
      dependents.forEach(o => sel.delete(o.id));                           // :214  can delete the required component
```
`app.js:186`, `app.js:267-270` (Recommended) and `app.js:465-466` (presets) all build `state.components` from flags or ids with no dependency closure:
```js
  state.components = comps.filter(c => c.required || c.recommended).map(c => c.id);   // :186
```
`app.js:258-260`. The right-hand panel advertises only the direct edges ("Requires: Container Runtime — these will be selected automatically"), so the user is told the chain is handled when it is not.

The backend accepts both fixture shapes. `PackageValidation.cs:80-83` only requires every edge to resolve, and `Packages.cs:85-86` only requires at least one required component. The backend does not enforce selections (BUG-01). `Jobs.cs:406` (`var display = selected.FirstOrDefault(c => c.Required)`) quietly tolerates its absence, and `Jobs.cs:421` then logs `> Copying core payload (? MB)…`.

FR-007: "Checking a component with unmet dependencies auto-selects them". A transitive dependency is an unmet dependency.

## Impact
- **Extract only / Build package** write a customized package whose `manifest.json` declares a component without the component it depends on, or contains **no required component** (both shown in the transcript). The user saw a locked "(required)" row and had every reason to believe it was included.
- **Install / Silent** receipts list a component set that the package's own graph says cannot work.
- After GAP-C01, the same clicks return `400` and the user cannot see why.
- Likelihood: today this needs a local package with multi-level or required-component dependencies. Real NVIDIA packages declare `requires` dependencies in their `.nvi` metadata (`security_register.md` §2, GAP-F03), and GAP-F03 will feed that graph to this screen. Nothing guarantees those edges are one level deep or avoid the display driver, so the precondition becomes ordinary input there.

## How to reproduce
### A. Automated — browser transcript (amendment A6: no JS test runner)
Fixtures, in `agents/frontend/repro/fixtures/`. Each is a `manifest.json` plus one-line `payload/*.bin` files:
```json
// dep-chain/manifest.json
{ "version": "572.16", "channel": "WHQL", "components": [
  { "id": "display-driver", "name": "Display Driver", "sizeMB": 600, "required": true, "recommended": true, "payload": "display-driver.bin" },
  { "id": "app", "name": "Companion App", "sizeMB": 80, "payload": "app.bin", "dependsOn": ["runtime"] },
  { "id": "runtime", "name": "Container Runtime", "sizeMB": 20, "payload": "runtime.bin", "dependsOn": ["telemetry"] },
  { "id": "telemetry", "name": "Telemetry Services", "sizeMB": 10, "payload": "telemetry.bin" } ] }
// required-dep/manifest.json
{ "version": "572.16", "channel": "WHQL", "components": [
  { "id": "display-driver", "name": "Display Driver", "sizeMB": 600, "required": true, "recommended": true, "payload": "display-driver.bin", "dependsOn": ["runtime"] },
  { "id": "runtime", "name": "Container Runtime", "sizeMB": 20, "payload": "runtime.bin" },
  { "id": "physx", "name": "PhysX System Software", "sizeMB": 38, "recommended": true, "payload": "physx.bin" } ] }
```
(The `description` fields are omitted above. The real files carry one-line descriptions.)

Script `agents/frontend/repro/component-dependency-closure-incomplete.js`:
```js
// RED transcript: Screen 3 dependency enforcement (FR-007) is one level deep and ignores
// `required`, so the UI itself submits selections with broken dependencies — and can drop
// the required Display Driver.
//   fixtures/dep-chain    : app -> runtime -> telemetry (all optional)
//   fixtures/required-dep : display-driver (required) -> runtime (optional)
// Usage: CLEANDRIVER_BASE=http://localhost:4780 node component-dependency-closure-incomplete.js
//   (server: dotnet run --project nvcleanstall -- --headless --mock-catalog)
//   FIXTURE_ROOT / OUT_DIR override the paths as the SERVER sees them (Windows: C:/...).
const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');
const BASE = process.env.CLEANDRIVER_BASE || 'http://127.0.0.1:4805';
const FIX = process.env.FIXTURE_ROOT || path.join(__dirname, 'fixtures');
const OUT = process.env.OUT_DIR || path.join(__dirname, 'out');
const fwd = p => p.replace(/\\/g, '/');

const selected = page => page.evaluate(() =>
  [...document.querySelectorAll('#comp-list .crow')]
    .filter(r => r.querySelector('.cb').classList.contains('on'))
    .map(r => r.querySelector('.cname').firstChild.textContent.trim()));
const cb = (page, name) => page.locator('#comp-list .crow', { hasText: name }).locator('.cb');

async function open(browser, fixture) {
  const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
  if (process.env.PATCHED_APPJS) await page.route('**/app.js', r => r.fulfill({ path: process.env.PATCHED_APPJS, contentType: 'application/javascript' })); // candidate-fix check only
  page.errors = []; page.on('pageerror', e => page.errors.push(e.message));
  page.on('dialog', d => d.dismiss());
  page.on('request', r => { if (r.url().endsWith('/api/execute')) page.execSel = JSON.parse(r.postData()).selection; });
  await page.goto(BASE + '/');
  await page.waitForFunction(() => document.getElementById('combo-ver').textContent !== '');
  await page.click('#opt-local .opt-head');
  await page.fill('#local-path', fwd(path.join(FIX, fixture)));
  await page.click('#btn-next');
  await page.waitForSelector('#screen-components.active');
  return page;
}
async function extract(page, outDir) {
  if (!process.env.OUT_DIR) fs.mkdirSync(outDir, { recursive: true });
  await page.click('#btn-next'); await page.click('#btn-next');   // -> tweaks -> action
  await page.click('.acard[data-action="extract"]');
  await page.fill('#out-path', fwd(outDir));
  await page.click('#btn-next');                                  // Start
  await page.waitForSelector('#run-banner:not(.hidden)', { timeout: 15000 });
  console.log('   POST /api/execute components:', JSON.stringify(page.execSel.components));
  const mf = path.join(outDir, 'manifest.json');
  if (fs.existsSync(mf)) {
    const m = JSON.parse(fs.readFileSync(mf, 'utf8'));
    const ids = new Set(m.components.map(c => c.id));
    console.log('   written manifest.json: required present =', m.components.some(c => c.required),
      '| unresolved dependsOn =', JSON.stringify(m.components.flatMap(c => (c.dependsOn || []).filter(d => !ids.has(d)).map(d => `${c.id} -> ${d}`))));
  }
}

(async () => {
  const browser = await chromium.launch();
  let red = 0;
  const verdict = (tag, ok, what) => { console.log(`${tag} ${ok ? 'GREEN' : 'RED'}: ${what}`); if (!ok) red++; };

  // --- chained optional dependencies -------------------------------------------------
  let page = await open(browser, 'dep-chain');
  await cb(page, 'Companion App').click();
  const s1 = await selected(page);
  console.log('case 1 tick Companion App ->', JSON.stringify(s1), '| note:', JSON.stringify(await page.textContent('#comp-rp-dep')));
  verdict('case 1', s1.includes('Telemetry Services'), `transitive dependency "Telemetry Services" auto-selected: ${s1.includes('Telemetry Services')} (expected true)`);

  if (!(await selected(page)).includes('Telemetry Services'))
    await cb(page, 'Telemetry Services').click();                  // make sure all three are on
  await cb(page, 'Telemetry Services').click();                    // untick the leaf
  const s2 = await selected(page);
  const hole2 = s2.includes('Companion App') && !s2.includes('Container Runtime');
  console.log('case 2 untick Telemetry Services ->', JSON.stringify(s2), '| note:', JSON.stringify(await page.textContent('#comp-rp-dep')));
  verdict('case 2', !hole2, `"Companion App" kept without "Container Runtime": ${hole2} (expected false)`);
  await extract(page, path.join(OUT, 'dep-chain'));
  console.log(`   pageerrors=${page.errors.length}`);
  await page.close();

  // --- a required component with a dependency ----------------------------------------
  page = await open(browser, 'required-dep');
  const s0 = await selected(page);
  console.log('case 0 default selection on adoption ->', JSON.stringify(s0));
  verdict('case 0', s0.includes('Container Runtime'), `default selection includes "Container Runtime" required by the required Display Driver: ${s0.includes('Container Runtime')} (expected true)`);

  if (!s0.includes('Container Runtime')) await cb(page, 'Container Runtime').click();
  await cb(page, 'Container Runtime').click();                     // untick the dependency
  const s3 = await selected(page);
  console.log('case 3 untick Container Runtime ->', JSON.stringify(s3), '| note:', JSON.stringify(await page.textContent('#comp-rp-dep')));
  verdict('case 3', s3.includes('Display Driver'), `required "Display Driver" still selected: ${s3.includes('Display Driver')} (expected true)`);
  await extract(page, path.join(OUT, 'required-dep'));
  console.log(`   pageerrors=${page.errors.length}`);
  await page.close();

  await browser.close();
  console.log(red ? `RESULT: RED (${red}/4 cases)` : 'RESULT: GREEN');
})();
```
Run it against the real app:
```bash
dotnet run --project nvcleanstall -- --headless --mock-catalog          # :4780
CLEANDRIVER_BASE=http://localhost:4780 FIXTURE_ROOT=C:/path/to/fixtures OUT_DIR=C:/Temp/cd-deps \
  node component-dependency-closure-incomplete.js
```
The harness ran on `:4805`, and paths are shown as `<harness>`. `PATCHED_APPJS` is a candidate-fix hook used only by the harness and is unset for red runs.

Captured **RED** output with the unmodified `app.js`:
```
case 1 tick Companion App -> ["Display Driver","Companion App","Container Runtime"] | note: "Requires: Container Runtime — these will be selected automatically."
case 1 RED: transitive dependency "Telemetry Services" auto-selected: false (expected true)
case 2 untick Telemetry Services -> ["Display Driver","Companion App"] | note: "Required by: Container Runtime — deselected along with it."
case 2 RED: "Companion App" kept without "Container Runtime": true (expected false)
   POST /api/execute components: ["display-driver","app"]
   written manifest.json: required present = true | unresolved dependsOn = ["app -> runtime"]
   pageerrors=0
case 0 default selection on adoption -> ["Display Driver","PhysX System Software"]
case 0 RED: default selection includes "Container Runtime" required by the required Display Driver: false (expected true)
case 3 untick Container Runtime -> ["PhysX System Software"] | note: "Required by: Display Driver — deselected along with it."
case 3 RED: required "Display Driver" still selected: false (expected true)
   POST /api/execute components: ["physx"]
   written manifest.json: required present = false | unresolved dependsOn = []
   pageerrors=0
RESULT: RED (4/4 cases)
```

### B. Manual — running app
1. Create `C:/Temp/req-dep/manifest.json` with the `required-dep` content above, plus `payload/display-driver.bin`, `payload/runtime.bin` and `payload/physx.bin` (any bytes).
2. Open http://localhost:4780, select **Use driver files on disk**, enter `C:/Temp/req-dep`, and click **Next**.
3. Look at the list. **Display Driver (required)** and **PhysX** are ticked, but **Container Runtime**, which Display Driver requires, is not.
4. Tick **Container Runtime**, then untick it. The panel says "Required by: Display Driver — deselected along with it." The locked Display Driver row is now **unticked**.
5. Click **Next**, then **Next**, choose **Extract only**, set **Output folder** `C:/Temp/req-dep-out`, and click **Start**.
6. **Expected:** Display Driver can never be deselected, and its dependency is selected with it. Output contains `display-driver.bin`.
   **Actual:** the output contains only `payload/physx.bin`. Its `manifest.json` has no required component. An **Install** run from the same state logs `> Copying core payload (? MB)…` (`Jobs.cs:421`).
7. Chain variant: use the `dep-chain` content and tick **Companion App**. Only **Container Runtime** is added, and **Telemetry Services** is not.

## Root cause
Two reasoning errors in the same function.
1. Dependency resolution is modeled as a **one-step** relation, but FR-007 needs the **transitive closure**, in both directions: what a component needs, and what needs a component.
2. The dependents rule assumes a dependent can always be dropped. A required component cannot be, so a dependency of a required component is itself effectively required (locked).

The paths that *set* the selection without a user toggle (adoption defaults, Recommended, presets) were never routed through any closure, so they can start in an inconsistent state.

## Remedy
Compute closures from manifest edges, and route every path that sets `state.components` through one normalizer. Untick is refused for anything a required component needs, using the existing dependency panel copy style:
```diff
@@ app.js:196 @@
 function compById(id) { return state.manifest.components.find(c => c.id === id); }
+
+// FR-007: the ids plus everything they depend on, transitively (manifest edges only).
+function withDeps(ids) {
+  const out = new Set(ids);
+  for (const queue = [...out]; queue.length;)
+    for (const d of compById(queue.shift())?.dependsOn || []) if (!out.has(d)) { out.add(d); queue.push(d); }
+  return out;
+}
+// Every required component, and everything any of them needs, can never be deselected.
+function lockedIds() { return withDeps(state.manifest.components.filter(c => c.required).map(c => c.id)); }
+// A selection is always closed: locked ids + the dependency closure, in manifest order.
+function closeSelection(ids) {
+  const sel = withDeps([...ids, ...lockedIds()]);
+  return state.manifest.components.map(c => c.id).filter(i => sel.has(i));
+}
@@ app.js:186 (adoptManifest) @@
-  state.components = comps.filter(c => c.required || c.recommended).map(c => c.id);
+  state.components = closeSelection(comps.filter(c => c.required || c.recommended).map(c => c.id));
@@ app.js:204 (setComponent, tick) @@
-    const missing = (c.dependsOn || []).filter(d => !sel.has(d));
+    const missing = [...withDeps([id])].filter(d => !sel.has(d));
@@ app.js:209 (setComponent, untick) @@
   } else {
+    const lockers = state.manifest.components.filter(r => r.required && withDeps([r.id]).has(id));
+    if (lockers.length) {
+      panelMsgs.push(`<b>Required by:</b> ${esc(lockers.map(r => r.name).join(', '))} — it cannot be deselected.`);
+      return;
+    }
     sel.delete(id);
     const dependents = state.manifest.components
-      .filter(o => sel.has(o.id) && (o.dependsOn || []).includes(id));
+      .filter(o => sel.has(o.id) && withDeps([o.id]).has(id));
@@ app.js:259 (right panel) @@
-    const names = esc(hi.dependsOn.map(d => compById(d).name).join(', '));
+    const names = esc([...withDeps(hi.dependsOn)].map(d => compById(d).name).join(', '));
@@ app.js:268 (Recommended) @@
-  state.components = state.manifest.components
-    .filter(c => c.required || c.recommended).map(c => c.id);
+  state.components = closeSelection(state.manifest.components
+    .filter(c => c.required || c.recommended).map(c => c.id));
@@ app.js:465 (applySelection) @@
-    state.components = sel.components.filter(id => valid.has(id));
-    for (const c of state.manifest.components) if (c.required && !state.components.includes(c.id)) state.components.push(c.id);
+    state.components = closeSelection(sel.components.filter(id => valid.has(id)));
```
- **Why minimal:** it uses one closure helper and one normalizer, with no new UI. The `missing.forEach(d => sel.add(d))` line at `:206` stays and now adds the full closure. On the bundled catalog, behavior is **unchanged**. Verified in the harness: default `Display Driver, PhysX, HD Audio` at `682 MB of 832 MB`; tick GeForce Experience gives `+ Telemetry, Container Runtime` with the same note; untick Telemetry gives `Required by: GeForce Experience — deselected along with it.`; Recommended gives the same three.
- **New copy:** "— it cannot be deselected." appears only for a component a required one needs, which no bundled manifest has. It reuses the existing `#comp-rp-dep` panel, so it adds no control. Flag it for the owner as copy, not as a ruling-level change.
- **Alternatives rejected:**
  - *Render dependencies of required components as `locked`.* The R5-style disabled checkbox would be arguably better UX, but it changes the rendering contract. Raise it as an owner question rather than ship it.
  - *Leave it to GAP-C01.* The server would then reject the UI's own selection.
  - *Fix only the tick direction.* Untick would still strand dependents and drop the required component.
- **Fences:** the frozen five-screen contract is respected. Every interpolated name passes through `esc()`. There is no JS toolchain. `compById(d)?.` tolerates an unresolved id in the closure walk, though the *message* builders still assume resolution. That gap is `dependency-target-check-ignores-case`, and it is out of scope here.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active. This is the client half of **BUG-01 → GAP-C01**. Either fold it into GAP-C01 as an explicit client-side line item (its AC "Valid UI … selections remain green" depends on it), or pin a new owner entry ordered before GAP-C01. Either way, record the owner's acceptance of the "cannot be deselected" copy. Declare the no-test-surface slice: `slice_start --no-surface '^nvcleanstall/wwwroot/'` (sanctioned by that pattern appearing verbatim in the build plan).
1. **Slice setup.** `git worktree add ../nvcleanclone-dep-closure -b feat/component-dependency-closure-incomplete origin/main`.
2. **Red.** Run the §A script, with both fixtures, against the unmodified app. Save the RED transcript. The script, fixtures and transcript go in the PR body; the repo has no JS test location (HARD-04/HARD-06 precedent).
3. **Green.** Apply the diff. Rerun the script and expect the GREEN transcript below. Also run the bundled-catalog regression check, which must show the same output as before. Run the full C# suite. Commit `component-dependency-closure-incomplete: close component dependencies transitively and lock what required components need`.
4. **Refactor.** Optional, with the green transcript. For example, cache `lockedIds()` per manifest in `adoptManifest`.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, then `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj` (unchanged count), then `git diff --check`.
6. **Live exercise.** Green transcript, plus manual §B in the WebView2 window, plus AC-005 on the bundled catalog (Recommended / GFE dependency note unchanged).
7. **PR.** Use the template. Add one evidence row per case (0–3): "browser transcript | RED | GREEN". Name **amendment A6**. Disclose the new panel sentence under "Deviations". Push. Never merge.

GREEN transcript (the same script, with the candidate diff served through the harness `PATCHED_APPJS` hook):
```
case 1 tick Companion App -> ["Display Driver","Companion App","Container Runtime","Telemetry Services"] | note: "Requires: Container Runtime, Telemetry Services — these will be selected automatically."
case 1 GREEN: transitive dependency "Telemetry Services" auto-selected: true (expected true)
case 2 untick Telemetry Services -> ["Display Driver"] | note: "Required by: Companion App, Container Runtime — deselected along with it."
case 2 GREEN: "Companion App" kept without "Container Runtime": false (expected false)
   POST /api/execute components: ["display-driver"]
   written manifest.json: required present = true | unresolved dependsOn = []
   pageerrors=0
case 0 default selection on adoption -> ["Display Driver","Container Runtime","PhysX System Software"]
case 0 GREEN: default selection includes "Container Runtime" required by the required Display Driver: true (expected true)
case 3 untick Container Runtime -> ["Display Driver","Container Runtime","PhysX System Software"] | note: "Required by: Display Driver — it cannot be deselected."
case 3 GREEN: required "Display Driver" still selected: true (expected true)
   POST /api/execute components: ["display-driver","runtime","physx"]
   written manifest.json: required present = true | unresolved dependsOn = []
   pageerrors=0
RESULT: GREEN
```

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green. It does not exercise JS.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-dep-closure origin/feat/component-dependency-closure-incomplete`. Range `origin/main...HEAD`. The diff touches only `nvcleanstall/wwwroot/app.js`.
3. **Re-run the gates and the §A script** (RED on main, GREEN on head), plus the bundled-catalog regression.
4. **Finder pass.**
   - Every writer of `state.components` goes through `closeSelection` or `setComponent`. Grep `state.components =`.
   - `withDeps` terminates on cycles (`a → b → a`), because the visited set is checked before enqueueing.
   - Untick of a locked id returns *before* mutating `sel`.
   - Manifest order is preserved, because the server receipt lists `selected` in manifest order.
   - Every name in `panelMsgs` is `esc()`'d.
   - Size totals still reflect the closed selection.
   - `Recommended` still yields FEAT-008's three on the bundled manifest.
5. **Adversarial verify.**
   - A cyclic manifest.
   - A diamond (`a → b`, `a → c`, `b → d`, `c → d`), then untick `d`.
   - A required component depending on a chain.
   - A preset that omits a required dependency.
   - A 512-component manifest (the GAP-S01 cap), checking for responsiveness.
   - A component depending on itself.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with findings marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** C0 Orient → C1 reproduce red → C2 fix → C3 verify ACs → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report → C7 STOP.
8. **Report to owner.** Review it before relaying. Only the owner merges.

## Acceptance criteria
1. The §A script prints `GREEN` for cases 0–3. No written `manifest.json` has an unresolved `dependsOn` edge, and every one contains the required component.
2. Ticking a component selects its full transitive dependency set, and the panel names all of them.
3. Unticking a component deselects every selected component that transitively depends on it. Unticking anything a required component needs is refused with the panel message, and the selection is unchanged.
4. The initial selection, **Recommended**, and an applied preset are all dependency-closed and include every required component.
5. The bundled-catalog behavior is identical: the same defaults, totals (`682 MB of 832 MB`), GFE auto-select note, and Telemetry untick note.
6. The diff is confined to `nvcleanstall/wwwroot/app.js`. `dotnet build`, `dotnet test` (unchanged count), and `git diff --check` are green.
