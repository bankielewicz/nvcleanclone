# A long output-folder path in the result banner pushes "Finish" out of the window

| Field | Value |
|---|---|
| **Slug** | `long-output-path-pushes-finish-offscreen` |
| **Severity** | Low. It removes one of the two R4 exits from the run view. "Open output folder" stays visible but squeezed. Recovery is a page reload, and no data is affected. The trigger is a user-typed output folder of about 88 or more characters with no space or hyphen. |
| **Confidence** | Confirmed by a live browser repro (Playwright/Chromium, `?shell=1`, 960×640 = `ShellForm.ClientSize`). A length sweep in the page's own markup located the threshold (about 85–90 characters). |
| **Area** | `nvcleanstall/wwwroot/styles.css` (`.banner`, `.banner .btxt`, `.banner .bt code`) and `nvcleanstall/wwwroot/app.js` (`pollRun` banner text, `shortPath`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. The window is a fixed 960×640 in WebView2 (`ShellForm.cs`: `ClientSize = new Size(960, 640)`, `FormBorderStyle.FixedSingle`), so the user cannot widen it. |
| **Related register entries** | Ruling R4 ("'Open output folder' and 'Finish' are the exits"), D4 (fixed 960×640 window). Distinct from `pollrun-failed-job-dead-ends-run-screen`: there the banner never appears, here it appears but its exit is clipped. Not SEC-0x. |
| **Found** | 2026-11-23 (E2E functional pass) |

## Summary
After Extract only or Build package, the banner reads "… package written to `<path>`". For any output folder the user typed, `shortPath` cannot shorten the path because it does not contain `output/`, so the full path is printed inside a `<code>` element. A Windows path has break opportunities only at spaces and hyphens. With neither, the `<code>` is one unbreakable run of text.

The banner's text column is `flex:1` with the default `min-width:auto`, so it cannot shrink below that run. The button group is pushed right instead. The window clips its content (`overflow:hidden`), so **Finish** ends up partly or wholly outside the 960 px window, and "Open output folder" wraps onto three lines.

## Evidence
Failing UI test in `docs/bugs/ui-functional-test-report.md`: **L-03**. Screenshot: `L-03-long-path-banner.png` in the E2E shots folder (path in the report).

`app.js:433-434`: the full custom path is interpolated:
```js
        extract: `Extraction complete — package written to <code>${esc(winPath(shortPath(j.outDir)))}</code>`,
        package: `Package built — written to <code>${esc(winPath(shortPath(j.outDir)))}</code>`,
```
`app.js:446-450`: `shortPath` only shortens paths that contain `output/…` or `presets/…`.

`styles.css:274-278`:
```css
.banner .btxt{flex:1;}                                     /* min-width:auto → cannot shrink below the <code> run */
.banner .bt code{font-family:var(--mono); font-size:13px; font-weight:400; color:var(--text);}   /* no overflow-wrap */
.banner .bbtns{display:flex; gap:8px;}                     /* shrinks/wraps "Open output folder" instead */
```
`styles.css:57-59`: `.window{… overflow:hidden; …}` clips anything pushed past 960 px.

Captured RED (harness, `BASE_URL=http://127.0.0.1:4806`). The real extract ran, and only the displayed `outDir` string was swapped by a render-only `page.route`:
```
RED   98-char path: see screenshot; Finish leaves the window from 90 chars
50:919 55:919 60:919 65:919 70:919 75:919 80:919 85:925 90:965(clipped) 95:1004(clipped) 100:1043(clipped) 105:1082(clipped) 110:1121(clipped)
exit=1
```
Each `n:x` pair is the path length and Finish's right edge in px. The window's right edge is 960, and the banner's inner edge is 919. At 98 characters (`C:\Users\JonathanDoe\Documents\CleanDriverBuilds\GeForceRTX4070\Customized\572.16_lean_build_final`), Finish spans x 956–1027.

## Impact
- On the result screen the footer is hidden by R4, so Finish is the only way back to Screen 1. Once Finish is clipped, a mouse user's only way out is reloading the page, which loses every selection.
- Folder names built from underscores, dots or CamelCase (`572.16_lean_build_final`, `GeForceRTX4070`), under a long user profile or OneDrive path, reach the threshold. A path containing spaces (`OneDrive - Contoso`) or hyphens wraps and is fine, as the suite's L-03 first run showed.

## How to reproduce
### A. Automated — browser transcript (amendment A6)
Script `long-output-path-pushes-finish-offscreen.js`:
```js
// Repro: an output folder path without spaces/hyphens cannot wrap inside the result banner, so the banner's
// buttons are pushed right; from ~90 chars "Finish" leaves the 960px window (clipped by .window overflow:hidden).
// The real extract runs into the default folder; only the outDir string in the job snapshot is rewritten
// (render-only route) to sweep path lengths.
const { chromium } = require('playwright');
const BASE = (process.env.BASE_URL || 'http://localhost:4780').replace(/\/$/, '');
(async () => {
  const b = await chromium.launch(); const ctx = await b.newContext({ viewport: { width: 960, height: 640 } });
  await ctx.route(u => !u.href.startsWith(BASE), r => r.abort());
  const p = await ctx.newPage(); let fake = null;
  await p.route('**/api/jobs/*', async r => { const resp = await r.fetch(); const j = await resp.json(); if (j.outDir && fake) j.outDir = fake; await r.fulfill({ response: resp, json: j }); });
  await p.goto(BASE + '/?shell=1'); await p.waitForFunction(() => document.getElementById('combo-ver').textContent);
  await p.click('#opt-local .opt-head'); await p.click('#btn-browse'); await p.waitForTimeout(300);
  await p.click('#btn-next'); await p.waitForSelector('#screen-components.active'); await p.click('#btn-next'); await p.click('#btn-next');
  await p.click('.acard[data-action="extract"]');
  fake = 'C:\\Users\\JonathanDoe\\Documents\\CleanDriverBuilds\\GeForceRTX4070\\Customized\\572.16_lean_build_final';
  await p.click('#btn-next'); await p.waitForSelector('#run-banner:not(.hidden)', { timeout: 20000 }); await p.waitForTimeout(300);
  await p.screenshot({ path: process.env.SHOT || 'long-path-banner.png' });
  // sweep: re-render the banner text with shorter/longer unbroken paths using the page's own markup
  const rows = await p.evaluate(base => {
    const out = []; const win = document.querySelector('.window').getBoundingClientRect().right;
    for (let n = 50; n <= 110; n += 5) {
      const s = (base + 'x'.repeat(200)).slice(0, n);
      document.getElementById('banner-text').innerHTML = 'Extraction complete — package written to <code></code>';
      document.querySelector('#banner-text code').textContent = s;
      const f = document.getElementById('btn-finish').getBoundingClientRect();
      out.push({ n, finishRight: Math.round(f.right), inside: f.right <= win });
    }
    return out;
  }, fake);
  const first = rows.find(r => !r.inside);
  const red = rows.some(r => !r.inside);
  console.log(`${red ? 'RED  ' : 'GREEN'} 98-char path: see screenshot; Finish leaves the window from ${first ? first.n : '—'} chars`);
  console.log(rows.map(r => `${r.n}:${r.finishRight}${r.inside ? '' : '(clipped)'}`).join(' '));
  await b.close(); process.exit(red ? 1 : 0);
})();
```
Run: `dotnet run --project nvcleanstall -- --headless --mock-catalog`, then `BASE_URL=http://localhost:4780 node long-output-path-pushes-finish-offscreen.js`.

### B. Manual — running app
1. In the WebView2 window: Use driver files on disk → Browse… → Next → Next → Next.
2. Select **Extract only** and type the Output folder `C:\Users\<you>\Documents\CleanDriverBuilds\GeForceRTX4070\Customized\572.16_lean_build_final`. Any folder of about 90 or more characters with no spaces or hyphens works.
3. Press **Start** and wait for the banner.
4. **Expected:** the path wraps, and both "Open output folder" and "Finish" sit fully inside the window. **Actual:** "Finish" is cut off at the right window edge (or entirely hidden), and "Open output folder" is squeezed onto three lines.

## Root cause
A flex item that holds unbreakable text and keeps the default `min-width:auto` sizes itself to that text. The button group, the only other flexible item, absorbs the overflow, and the clipped window hides it.

## Remedy
CSS-only, A6. There is no visual change for normal paths, and no new controls.
```css
.banner .btxt{flex:1; min-width:0;}
.banner .bt code{overflow-wrap:anywhere;}
.banner .bbtns{flex:0 0 auto;}
```
Optionally, `shortPath` could also abbreviate the middle of very long paths with `…`, but the full path must stay discoverable (for example in a `title`), and the change is unnecessary with the CSS fix. Keep `esc()` on the interpolated path. Fences: frozen contract, R4 (the banner still holds both exits), no JS toolchain.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active, and no entry covers banner layout. Get a new owner-pinned entry, or fold this into a small "frontend robustness" item. Declare `slice_start --no-surface '^nvcleanstall/wwwroot/'`.
1. **Slice setup.** `git worktree add ../nvcleanclone-longpath -b feat/long-output-path-pushes-finish-offscreen origin/main`.
2. **Red.** The §A transcript must show `RED … from 90 chars`, with screenshot.
3. **Green.** Make the three CSS declarations. §A must print `GREEN` and every sweep row inside. Run the full C# suite (unchanged). Commit `"long-output-path-pushes-finish-offscreen: let the banner path wrap"`.
4. **Refactor**, green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Take green transcript screenshots for both 98 and 200 characters. Do a manual WebView2 check with a long typed path. The default `output\extract-<ver>` banner must look identical to before.
7. **PR.** Use the template's A6 rows. Push and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.**
2. **Isolated review worktree**, `origin/main...HEAD`.
3. **Re-run the gates** and §A.
4. **Finder pass:**
   - Only `styles.css` (and optionally `shortPath`) changed.
   - The short-path banner is pixel-identical to the mockup's Frame B.
   - The reboot caption ("A reboot is recommended.") still sits under the text.
5. **Adversarial verify:**
   - 300-character path. The banner grows taller but the log stays visible: `.log` is `flex:1; min-height:0`.
   - Dark theme.
   - Install banner (receipt path) unchanged.
   - 1280×720 plain mode.
6. **Verdict doc.**
7. **Implementer handoff** → C0–C7.
8. **Report to the owner**; the owner merges.

## Acceptance criteria
1. For unbroken output paths of 50–300 characters, Finish's and Open output folder's bounding boxes lie fully inside the 960×640 window. §A prints `GREEN`.
2. The default-path banner (`output\extract-<ver>`) renders exactly as before.
3. R4 holds: both exits remain in the banner, and the footer stays hidden.
4. The full C# suite is green, and `git diff --check` is clean.
