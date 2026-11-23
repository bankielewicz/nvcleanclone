# With the version dropdown open, the "Sample catalog" note pushes "Use driver files on disk" under the window's bottom edge

| Field | Value |
|---|---|
| **Slug** | `source-note-clips-local-option` |
| **Severity** | Low. This is cosmetic and partial: the bottom 14 px of the local-source card, including the lower border of its path field and Browse… button, is clipped while the menu is open. The controls stay clickable, and closing the menu restores the layout. |
| **Confidence** | Confirmed by a live browser repro (Playwright/Chromium, `?shell=1`, 960×640 = `ShellForm.ClientSize`). Compared against the frozen mockup `mockup-screen-01-driver-source.html`, which shows the same open menu with no clipping. |
| **Area** | `nvcleanstall/wwwroot/index.html` (`#catalog-source-note` inside the catalog option), `nvcleanstall/wwwroot/styles.css` (`.content` overflow, `.menu` height, `.source-note`), `nvcleanstall/wwwroot/app.js` (`selectSource` leaves the menu open) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. The window is fixed at 960×640 (`ShellForm.cs`), and plain mode reproduces at 1280×720 as well, because the window is the same 960×640 box. |
| **Related register entries** | D4 (fixed 960×640 canvas) and the frozen Screen 1 mockup. The note itself is a sanctioned post-mockup addition (GAP-01 / HARD-06 "render WHY the mock catalog is in play"); the mockup was never re-checked with it present. Not SEC-0x. |
| **Found** | 2026-11-23 (E2E functional pass) |

## Summary
The mockup's Screen 1 fits exactly at 960×640 with the five-row version menu open. The live page adds one line under the menu: "Sample catalog (`<reason>`) — showing bundled sample releases." That is 8 px of margin plus a 16 px line. `.content` clips its overflow, so the "Use driver files on disk" card is pushed down and its bottom is cut off by the footer.

Selecting the local option does not close the menu (`selectSource` only toggles classes), so the clipped layout persists exactly when the user starts using the local path field.

The note shows whenever the catalog source is mock: `--mock-catalog`, "GPU not matched", or "live lookup failed". That includes every user whose GPU is not in the 19-name live map.

## Evidence
Failing UI tests in `docs/bugs/ui-functional-test-report.md`: **L-01** (shell 960×640) and **L-02** (plain 1280×720). Screenshots: `L-01-shell-960x640-s1-combo-open.png` (live) and `M-mockup-screen-01.png` (mockup, same state), both in the E2E shots folder.

- `index.html:64`: the note sits inside `#catalog-body`, under the menu: `<div class="source-note hidden" id="catalog-source-note">…</div>`.
- `styles.css:148`: `.source-note{margin-top:8px; font-size:12px; …}` adds about 24 px.
- `styles.css:138`: `.menu{max-height:210px; …}` gives all five rows at full height.
- `styles.css:93`: `.content{… overflow:hidden; …}` clips instead of scrolling.
- `app.js:86-92`: `selectSource` never resets `state.comboOpen`. `app.js:536-543` shows the note when `cat.source === 'mock'`.

Captured RED (harness, `BASE_URL=http://127.0.0.1:4806`):
```
RED   note visible=true, menu still open=true; .content bottom y=580; #opt-local bottom=594, #local-path bottom=583, Browse bottom=583 (clipped by 14px)
exit=1
```
Mockup measurement with the same open menu: the local card ends 12 px *above* its content edge (`{"content":[152,686],"localOpt":[582,674]}`).

## Impact
- The Screen 1 layout breaks the frozen design whenever the mock or fallback catalog is in play and the menu is open. The local option's card border and the bottom of its field and button are cut, which looks broken at the fixed window size.
- Functionally the controls still work, which is why this is Low.

## How to reproduce
### A. Automated — browser transcript (amendment A6)
Script `source-note-clips-local-option.js`:
```js
// Repro: at the shell size (960×640, ?shell=1) with the version dropdown open, the mock/fallback
// "Sample catalog (…)" note pushes "Use driver files on disk" below .content's overflow:hidden edge.
const { chromium } = require('playwright');
const BASE = (process.env.BASE_URL || 'http://localhost:4780').replace(/\/$/, '');
(async () => {
  const b = await chromium.launch(); const ctx = await b.newContext({ viewport: { width: 960, height: 640 } });
  await ctx.route(u => !u.href.startsWith(BASE), r => r.abort());
  const p = await ctx.newPage(); await p.goto(BASE + '/?shell=1'); await p.waitForFunction(() => document.getElementById('combo-ver').textContent);
  await p.click('#combo-field'); await p.click('#opt-local .opt-head');   // selecting the local option does not close the menu
  const m = await p.evaluate(() => { const c = document.querySelector('.content').getBoundingClientRect(); const r = id => document.getElementById(id).getBoundingClientRect();
    return { contentBottom: Math.round(c.bottom), optLocal: Math.round(r('opt-local').bottom), pathField: Math.round(r('local-path').bottom), browse: Math.round(r('btn-browse').bottom),
      noteVisible: !document.getElementById('catalog-source-note').classList.contains('hidden'), comboOpen: document.getElementById('combo').classList.contains('open') }; });
  await p.screenshot({ path: process.env.SHOT || 'source-note-clip.png' });
  const red = m.optLocal > m.contentBottom;
  console.log(`${red ? 'RED  ' : 'GREEN'} note visible=${m.noteVisible}, menu still open=${m.comboOpen}; .content bottom y=${m.contentBottom}; #opt-local bottom=${m.optLocal}, #local-path bottom=${m.pathField}, Browse bottom=${m.browse} (clipped by ${m.optLocal - m.contentBottom}px)`);
  await b.close(); process.exit(red ? 1 : 0);
})();
```
Run: `dotnet run --project nvcleanstall -- --headless --mock-catalog`, then `BASE_URL=http://localhost:4780 node source-note-clips-local-option.js`.

### B. Manual — running app
1. Start with `--mock-catalog`, or on a machine whose GPU is not matched, so the note shows.
2. Click the version field to open the menu, then click **Use driver files on disk**.
3. **Expected:** as in the mockup, the whole local card (field and Browse…) is visible above the footer. **Actual:** the card's bottom border and the lower edge of the field and button are cut off by the footer while the menu stays open.

## Root cause
A sanctioned extra line (HARD-06's source note) was added to a fixed-height screen whose open-menu state had zero slack in the mockup, and `.content` clips rather than scrolls.

## Remedy
Frontend-only, A6. No new controls, and the note text and position stay as HARD-06 pinned them. Pick the smallest option:
- **(a)** Close the menu when the user picks a source or clicks outside it: `state.comboOpen = false; renderCombo();` in `selectSource`, plus a document click handler like the preset popover's (`app.js:482-489`). The clipped state then lasts only while the user is choosing a version.
- **(b)** Reduce `.menu{max-height}` by the note's height when the note is visible (for example `#catalog-body:has(.source-note:not(.hidden)) .menu{max-height:182px}`). The menu then scrolls one row, and nothing clips.
- Do not make `.content` scroll globally. That changes every screen of the frozen contract.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active, and no entry covers this. Get a new owner-pinned entry, or fold it into a frontend-layout item with `long-output-path-pushes-finish-offscreen`. Declare `slice_start --no-surface '^nvcleanstall/wwwroot/'`.
1. **Slice setup.** `git worktree add ../nvcleanclone-srcnote -b feat/source-note-clips-local-option origin/main`.
2. **Red.** §A transcript (`RED … clipped by 14px`) plus screenshot.
3. **Green.** Make the minimal change from the Remedy. §A must print `GREEN`. Run the full C# suite (unchanged). Commit `"source-note-clips-local-option: keep the local card inside the window with the menu open"`.
4. **Refactor**, green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Green transcript plus a WebView2 check with the mock catalog (note shown) and the live catalog (note hidden; the layout must be unchanged from the mockup).
7. **PR.** Use the template's A6 rows. Push and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.**
2. **Isolated review worktree**, `origin/main...HEAD`.
3. **Re-run the gates** and §A.
4. **Finder pass:**
   - Live mode (no note) renders pixel-identical to `mockup-screen-01`.
   - Picking a version still closes the menu, and the check mark and Latest/Installed annotations are intact.
   - The outside-click handler does not swallow clicks on Next or the presets.
5. **Adversarial verify:**
   - A long `sourceDetail` reason (wraps to two lines).
   - Dark theme.
   - 1280×720 plain mode.
   - Menu open, then Next pressed. The download still starts with the shown version.
6. **Verdict doc.**
7. **Implementer handoff** → C0–C7.
8. **Report to the owner**; the owner merges.

## Acceptance criteria
1. In `?shell=1` at 960×640, with the note visible and the menu open, `#opt-local`'s bottom ≤ `.content`'s bottom (§A `GREEN`).
2. With the note hidden (live catalog), Screen 1 matches the mockup in both the open-menu and closed-menu states.
3. The HARD-06 note text and placement are unchanged, and there are no new controls.
4. The full C# suite is green, and `git diff --check` is clean.
