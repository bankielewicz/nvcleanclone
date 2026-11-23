# Keyboard-only users cannot pick a version, a local package, expert tweaks or any action but Install

| Field | Value |
|---|---|
| **Slug** | `keyboard-cannot-reach-wizard-controls` |
| **Severity** | Medium. A keyboard-only user can complete exactly one path: the latest catalog version, default tweaks (no expert section), and **Install**. Choosing a version, using a local package, revealing expert tweaks (MSI, HDCP, HD-audio sleep, driver telemetry, container), and choosing Silent install, Extract only or Build package are all impossible without a pointer. Toggling a component or tweak checkbox also throws focus back to `<body>`. |
| **Confidence** | Confirmed by a live browser repro (Playwright/Chromium, `?shell=1`, Tab/Enter/Space only). |
| **Area** | `nvcleanstall/wwwroot/index.html` (Screen 1 options, version combo, action cards) and `nvcleanstall/wwwroot/app.js` (`renderCombo`, `renderComponents`, `renderTweaks`, `renderAction`, preset popover) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. WebView2 uses the same Chromium focus model (reasoned). |
| **Related register entries** | Ruling R5 ("implementation uses a real disabled checkbox (semantic + keyboard-correct)"). The pinned design token in `docs/design/nvcleanstall-mockup-prompt.md` reads "Focus: 2px accent outline". No register row covers keyboard access. Distinct from GAP-F02 (native pickers). Not SEC-0x. |
| **Found** | 2026-11-23 (E2E functional pass) |
| **Register slice** | **GAP-R09b** — Wizard frontend, pass 2: keyboard access and layout (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
Every primary choice on Screens 1, 4 and 5 is a `<div>` or `<span>` with an `onclick` and no `tabindex`, `role` or key handler. That covers the two source radios, the version combo and its rows, the "Show expert tweaks" switch, the MSI priority segments, the four action cards, and the Load-preset rows. Tab never stops on them, and Enter or Space cannot activate them.

The checkboxes on Screens 3 and 4 are real `<button role="checkbox">` elements and are reachable. Every toggle re-renders the whole list with `innerHTML = ''`, though, which destroys the focused button and drops focus to `<body>`. The next Tab restarts at the top of the page.

## Evidence
Failing UI test in `docs/bugs/ui-functional-test-report.md`: **N-07**.

Non-focusable controls, each a div or span with a click handler only:
- `index.html:49-51` and `index.html:68-70`: `<div class="opt sel" id="opt-catalog"><div class="opt-head">…`. The handlers are `app.js:93-94`: `$('opt-catalog').querySelector('.opt-head').onclick = () => selectSource('catalog');`.
- `index.html:56`: `<div class="combo-field" id="combo-field">`, toggled at `app.js:127`. Menu rows are `document.createElement('div')` with `row.onclick` (`app.js:114-123`).
- `app.js:352-357`: `switchRow` (a `<div class="switch-row">`) has `onclick` only. Because it cannot be reached, the expert rows and therefore the MSI segmented control never render for a keyboard user. The segments are `<span>` with `onclick` (`app.js:337-343`).
- `index.html:151-170`: `<div class="acard" data-action="…">`, wired at `app.js:374-377` (`card.onclick = …`).
- `app.js:510`: preset rows `<div class="prow" data-name="…">`.

Focus loss on toggle:
- `app.js:223` (`list.innerHTML = '';` in `renderComponents`) and `app.js:279` (`renderTweaks`) rebuild the list on every checkbox press (`app.js:235-241`, `app.js:298-303`). Nothing restores focus.

Focus styling: `styles.css:108` defines the pinned 2 px accent outline only for `.btn` and `.tb-btn`. The `.cb` checkboxes fall back to the UA ring.

Captured RED (harness, `BASE_URL=http://127.0.0.1:4806`):
```
RED   unreachable by Tab: [Screen 1 #combo-field; Screen 1 #opt-catalog .opt-head; Screen 1 #opt-local .opt-head; Screen 4 #tweak-list .switch-row; Screen 5 .acard[data-action="silent"]; Screen 5 .acard[data-action="extract"]; Screen 5 .acard[data-action="package"]]
RED   after Space on the USB-C checkbox, focus is on <body>
exit=1
```
The full suite (N-07) also found the **Load preset** list rows unreachable after opening the popover with Enter.

## Impact
- Keyboard-only and switch-access users cannot use FR-002 (version picker), FR-004 (local package), FR-009 (expert tweaks), FR-011/FR-012 (Extract only, Build package), FR-010 Silent install, or FR-015 load. Each needs a pointer.
- Toggling several components by keyboard means re-tabbing from the top of the window after every press.
- R5 shows the contract wanted keyboard-correct semantics. The implementation honoured that for the locked checkbox only.

## How to reproduce
### A. Automated — browser transcript (amendment A6)
Script `keyboard-cannot-reach-wizard-controls.js`:
```js
// Repro: keyboard-only operation — Tab never reaches div-based controls; toggling a checkbox drops focus to <body>.
const { chromium } = require('playwright');
const BASE = (process.env.BASE_URL || 'http://localhost:4780').replace(/\/$/, '');
(async () => {
  const b = await chromium.launch(); const ctx = await b.newContext({ viewport: { width: 960, height: 640 } });
  await ctx.route(u => !u.href.startsWith(BASE), r => r.abort());
  const p = await ctx.newPage(); await p.goto(BASE + '/?shell=1'); await p.waitForFunction(() => document.getElementById('combo-ver').textContent);
  const reach = async sels => { const hit = Object.fromEntries(sels.map(s => [s, false])); await p.evaluate(() => document.activeElement.blur());
    for (let i = 0; i < 45; i++) { await p.keyboard.press('Tab'); (await p.evaluate(ss => ss.filter(s => [...document.querySelectorAll(s)].some(e => e === document.activeElement || e.contains(document.activeElement))), sels)).forEach(s => (hit[s] = true)); }
    return Object.entries(hit).filter(([, v]) => !v).map(([k]) => k); };
  const tabTo = async pred => { for (let i = 0; i < 60; i++) { await p.keyboard.press('Tab'); if (await p.evaluate(pred)) return; } throw new Error('not reachable'); };
  const un = [];
  un.push(...(await reach(['#combo-field', '#opt-catalog .opt-head', '#opt-local .opt-head', '#btn-next'])).map(s => 'Screen 1 ' + s));
  await tabTo(() => document.activeElement.id === 'btn-next'); await p.keyboard.press('Enter'); await p.waitForSelector('#screen-components.active', { timeout: 20000 });
  await p.evaluate(() => document.activeElement.blur()); await tabTo(() => document.activeElement.getAttribute('aria-label') === 'USB-C / VirtualLink Driver');
  await p.keyboard.press('Space');
  const focus = await p.evaluate(() => document.activeElement === document.body ? '<body>' : document.activeElement.outerHTML.slice(0, 60));
  await tabTo(() => document.activeElement.id === 'btn-next'); await p.keyboard.press('Enter'); await p.waitForSelector('#screen-tweaks.active');
  un.push(...(await reach(['#tweak-list .switch-row', '#tweak-list .cb'])).map(s => 'Screen 4 ' + s));
  await tabTo(() => document.activeElement.id === 'btn-next'); await p.keyboard.press('Enter'); await p.waitForSelector('#screen-action.active');
  un.push(...(await reach(['.acard[data-action="silent"]', '.acard[data-action="extract"]', '.acard[data-action="package"]', '#btn-next'])).map(s => 'Screen 5 ' + s));
  const red = un.length > 0 || focus === '<body>';
  console.log(`${red ? 'RED  ' : 'GREEN'} unreachable by Tab: [${un.join('; ')}]`);
  console.log(`${focus === '<body>' ? 'RED  ' : 'GREEN'} after Space on the USB-C checkbox, focus is on ${focus}`);
  await b.close(); process.exit(red ? 1 : 0);
})();
```
Run: `dotnet run --project nvcleanstall -- --headless --mock-catalog`, then `BASE_URL=http://localhost:4780 node keyboard-cannot-reach-wizard-controls.js`. The RED output is captured in **Evidence**.

### B. Manual — running app
1. Open the wizard and do not touch the mouse.
2. Press Tab repeatedly on Screen 1. Focus visits the theme buttons (browser mode), Save/Load preset, the path field and Browse…, then **Next**. It never reaches either radio option or the version dropdown.
3. Press Enter on **Next**. After the download, Tab to a component checkbox and press Space. The box toggles, but focus disappears, and the next Tab starts again at the top of the window.
4. Press Enter on **Next**. On Tweaks, "Show expert tweaks" cannot be focused.
5. Press Enter on **Next**. On Action, no card can be focused, so only the preselected **Install** can be started.
6. **Expected:** every control is reachable with Tab and operable with Enter/Space, and focus stays on the toggled control. **Actual:** as above.

## Root cause
The mockups' visual elements were implemented as styled `div`/`span` click targets rather than native controls (or ARIA widgets with `tabindex` and key handling). R5's fix (a real disabled checkbox) was applied to the component checkbox only. Separately, the list renderers rebuild DOM on every state change without restoring focus.

## Remedy
Frontend-only, A6. There is **no visual change** and no new controls (frozen five-screen contract, R1–R10).
- Make the existing elements keyboard-operable while keeping their classes and look:
  - `.opt-head`: `role="radio"`, `tabindex="0"`, `aria-checked`, and Enter/Space → `selectSource`.
  - `#combo-field`: `role="combobox"`, `tabindex="0"`, `aria-expanded`. Enter/Space/Alt+Down opens the menu; ArrowUp/Down moves between the rows (`role="option"`); Enter picks; Esc closes.
  - `.switch-row`: `role="switch"`, `tabindex="0"`, `aria-checked`.
  - `.seg span`: `role="radio"` inside `role="radiogroup"` with `tabindex`, or change them to `<button>` elements with the same class.
  - `.acard`: `role="radio"`, `tabindex="0"`, `aria-checked`, with Enter/Space.
  - `.prow`: `<button class="prow">` or `tabindex="0"` with Enter.
- Preserve focus across re-render. After `renderComponents`/`renderTweaks`, re-focus the `.cb` of `state.hiComp`/`state.hiTweak` when the re-render was caused by a keyboard toggle (`document.activeElement` was inside the list).
- Extend the existing `:focus-visible` rule (`styles.css:108`) to `.cb`, `.opt-head`, `.combo-field`, `.acard`, `.switch-row`, `.seg span` and `.prow`. This applies the pinned "2px accent outline" token, not a new style.
- Keep `esc()`/`textContent` for every interpolated name. `aria-label` values built from manifest names must stay escaped, as `app.js:229` already does.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active, and no entry covers accessibility. This needs a new owner-pinned register entry (suggested: a Wave C item, "keyboard parity for the frozen five screens", citing R5). Declare `slice_start --no-surface '^nvcleanstall/wwwroot/'`.
   > *Pinned 2026-11-23 as **GAP-R09b** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup.** `git worktree add ../nvcleanclone-kbd -b feat/keyboard-cannot-reach-wizard-controls origin/main`.
2. **Red.** Run the §A transcript (`RED unreachable by Tab: […]`, `focus is on <body>`). There is no JS test runner (A6), and no commit is needed for a red with no test file.
3. **Green.** Make the minimum attribute, key-handler and focus-restore changes. Re-run §A until both lines are `GREEN`. Run the full C# suite (unchanged, green). Commit `"keyboard-cannot-reach-wizard-controls: make wizard choices focusable and keep focus on toggle"`.
4. **Refactor**, green only. A small `activate(el, fn)` helper can wire click plus Enter/Space once.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Run the green transcript, then do a full manual keyboard-only pass in the WebView2 window: AC-003 (older beta), AC-002 (local, Extract only) and AC-004 (expert MSI High + HDCP, Build package), all without a mouse. Screenshots must show the same pixels as before apart from the focus ring.
7. **PR.** Use the template with TDD evidence rows naming amendment A6. Push and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage** (`gh pr checks`).
2. **Isolated review worktree**, `origin/main...HEAD`.
3. **Re-run the gates** and §A.
4. **Finder pass**, with a checklist for this diff:
   - Each new `role` has the matching `aria-checked`/`aria-expanded`, and it updates on change.
   - Enter and Space both work. Space does not scroll the list.
   - Disabled states (`.disabled-block` sub-row, `#out-row.disabled`) are skipped by Tab (`tabindex="-1"` or `inert`).
   - No `innerHTML` built from manifest text without `esc()`.
   - Screenshots show no pixel change apart from focus rings (frozen contract).
5. **Adversarial verify:**
   - Shift+Tab order is the reverse of Tab.
   - Esc closes the combo and the preset popover.
   - Arrow keys in the version menu wrap or stop sanely.
   - Toggle GFE by keyboard (auto-selects two dependencies): focus stays on GFE.
   - Hidden screens contribute no tab stops.
6. **Verdict doc** (APPROVE / REQUEST CHANGES; CONFIRMED / PLAUSIBLE).
7. **Implementer handoff** → fix loop C0–C7, one `F<n>:` commit per finding.
8. **Report to the owner**; the owner merges.

## Acceptance criteria
1. §A prints `GREEN` for both lines.
2. Keyboard-only: AC-002, AC-003 and AC-004 can each be completed end to end with Tab, Shift+Tab, Enter, Space and arrow keys only.
3. After toggling any component or tweak checkbox with Space, `document.activeElement` is that checkbox.
4. Every focusable control shows the pinned 2 px accent focus outline. There are no other visual changes, and the five-screen inventory and R1–R10 are unchanged.
5. The full C# suite is green, and `git diff --check` is clean.
