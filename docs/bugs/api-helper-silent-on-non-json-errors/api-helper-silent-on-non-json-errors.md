# A bodiless server error makes Start, Load preset… and app start-up fail silently (the `api` helper never checks `r.ok`)

| Field | Value |
|---|---|
| **Slug** | `api-helper-silent-on-non-json-errors` |
| **Severity** | Low. Bounded robustness. The UI stays usable: a press on Start does nothing, Load preset… does not open, or start-up stays on "Detecting…". The user is never told why, and every case leaves an uncaught `SyntaxError`. Real backend triggers exist today: BUG-06 (one malformed preset file) and a rejected `outputPath`, both bodiless 500s. |
| **Confidence** | Confirmed by a live browser repro (Playwright/Chromium). (a) and (b) are **real backend 500s**, not injected; (c) is a `page.route` injection. |
| **Area** | `nvcleanstall/wwwroot/app.js` (`api` helper; callers `startExecute`, the `btn-load-preset` handler, `leaveSource`, `boot`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both. `fetch`/`Response.json()` behave identically in WebView2 (reasoned). |
| **Related register entries** | The client half of three server-side 500s: **BUG-06** (malformed preset → `Presets.List` throws), `execute-malformed-body-500` (an `outputPath` that `Path.GetFullPath` rejects), and `preset-load-invalid-name-500`. The first two docs attribute the 500 to the server. That doc's remedy note "The wizard already shows `r.error` through `alert`" holds only for JSON bodies: today a bodiless 500 shows nothing. Distinct from `job-pollers-no-exit-on-404-or-network-error`, which covers only the two poll loops and names these non-poller calls out of scope. Not SEC-0x. |
| **Found** | 2026-11-23 (E2E functional pass) |

## Summary
Every request goes through one helper that does `fetch(p).then(r => r.json())`. Callers rely on a JSON `{error}` body to show an `alert`. When the server fails with an unhandled exception, Kestrel answers `500` with an empty body. `r.json()` then rejects with `SyntaxError: Unexpected end of JSON input`, no caller catches it, and the click handler silently ends:
- **Start** (Screen 5) with an Output folder that `Path.GetFullPath` rejects (here, a pasted NUL character): HTTP 500 with a 0-byte body. No alert, no run view, and the user stays on Action with no feedback.
- **Load preset…** when any one file in `presets/` is malformed (BUG-06: writes are not atomic, so a crash mid-write leaves one): the popover never opens, so *no* preset can be loaded.
- **App start-up** when `/api/system`, `/api/catalog` or `/api/tweaks` fails: the GPU card stays "Detecting…" and the version field stays empty. **Next** later posts `{version: undefined}` and shows the misleading alert "unknown version".

## Evidence
Failing UI tests in `docs/bugs/ui-functional-test-report.md`: **R-01** (injected text/plain 500 on `/api/execute`), **R-09** (boot), and **P-05** (the malformed preset: its row there is marked known BUG-06; this doc is the client half).

`app.js:7-12`:
```js
const api = {
  get: p => fetch(p).then(r => r.json()),                          // :8   no r.ok check, no catch
  post: (p, body) => fetch(p, { ... }).then(r => r.json()),        // :9-11
};
```
`app.js:383-391`: `startExecute` assumes a JSON body:
```js
  const r = await api.post('/api/execute', { ... });              // :385  rejects on an empty 500 body
  if (r.error) return alert(r.error);                              // :391  never reached
```
`app.js:507-509`: Load preset… reads `r.presets` (`:509`) from `GET /api/presets`. Its rejection escapes the handler.
`app.js:526-529`: `boot()` has no `try/catch` around `Promise.all`. On rejection, nothing after it runs: no GPU text, no `renderCombo()`, no `state.version`.

Server sides that produce the bodiless 500:
- `Api.cs:108-115`: `StartExecute` → `Path.GetFullPath(outputPath)` (`Jobs.cs:388`) throws `ArgumentException`, which is not in the `IOException or InvalidDataException` filter.
- `Presets.cs:17-28`: `List` parses every file (`:21`), so one bad file throws (BUG-06).

Captured RED (harness, `BASE_URL=http://127.0.0.1:4806`, `PRESETS_DIR` set):
```
RED   (a) Start with a NUL in Output folder → HTTP 500 (0-byte body); screen screen-action; dialogs []; pageerrors ["Failed to execute 'json' on 'Response': Unexpected end of JSON input"]
RED   (b) Load preset… with one truncated preset file → popover opened=false; dialogs []; pageerrors ["Failed to execute 'json' on 'Response': Unexpected end of JSON input"]
RED   (c) /api/catalog 500 at boot → GPU card "Detecting…", combo ""; Next → dialogs ["unknown version"]; pageerrors ["Failed to execute 'json' on 'Response': Unexpected end of JSON input"]
```
For contrast, the suite's R-01 shows that a **JSON** 500 (`{"error":"boom"}`) *is* alerted. The defect is specific to non-JSON error bodies.

## Impact
- A user whose `presets/` folder holds one damaged file cannot load any preset, and gets no message. Together with BUG-06 this is the most likely real-world trigger.
- An invalid Output folder can make **Start** a silent no-op. `execute-malformed-body-500` judged the UI unaffected because "the wizard always sends a token and arrays". The `outputPath` case is UI-reachable, as shown in (a).
- A failed start-up looks like a hang ("Detecting…"), and its later error ("unknown version") points at the wrong cause.
- Each case logs an uncaught promise rejection, the console noise this pass was asked to collect.

## How to reproduce
### A. Automated — browser transcript (amendment A6)
Script `api-helper-silent-on-non-json-errors.js`. Set `PRESETS_DIR` to the server's `presets/` folder to enable part (b); it creates and then deletes one file there.
```js
// Repro: the app.js `api` helper parses every response with r.json() and never checks r.ok, and no caller
// catches. A bodiless 500 (what Kestrel sends for an unhandled route exception) therefore makes the
// action a silent no-op with an uncaught SyntaxError; a boot failure leaves "Detecting…" forever.
// (a) REAL backend 500: Output folder containing a NUL → Path.GetFullPath throws in /api/execute.
// (b) REAL backend 500 (needs PRESETS_DIR = the server's presets/ folder): a truncated preset file (BUG-06).
// (c) injected: /api/catalog → 500 at boot.
const { chromium } = require('playwright');
const fs = require('fs'); const path = require('path');
const BASE = (process.env.BASE_URL || 'http://localhost:4780').replace(/\/$/, '');
const sleep = ms => new Promise(r => setTimeout(r, ms));
(async () => {
  const b = await chromium.launch(); const rows = [];
  const mk = async routes => { const ctx = await b.newContext({ viewport: { width: 960, height: 640 } }); await ctx.route(u => !u.href.startsWith(BASE), r => r.abort());
    const p = await ctx.newPage(); p._e = []; p._d = []; p.on('pageerror', e => p._e.push(e.message)); p.on('dialog', d => { p._d.push(d.message()); d.accept(); });
    if (routes) await routes(p); await p.goto(BASE + '/?shell=1'); return p; };
  { const p = await mk(); await p.waitForFunction(() => document.getElementById('combo-ver').textContent);
    await p.click('#opt-local .opt-head'); await p.click('#btn-browse'); await sleep(300);
    await p.click('#btn-next'); await p.waitForSelector('#screen-components.active'); await p.click('#btn-next'); await p.click('#btn-next');
    await p.click('.acard[data-action="extract"]'); await p.fill('#out-path', 'C:/builds/a\u0000b');
    const [resp] = await Promise.all([p.waitForResponse(r => r.url().endsWith('/api/execute')), p.click('#btn-next')]); await sleep(600);
    const scr = await p.evaluate(() => document.querySelector('.screen.active').id);
    rows.push({ red: p._d.length === 0, t: `(a) Start with a NUL in Output folder → HTTP ${resp.status()} (${(await resp.body()).length}-byte body); screen ${scr}; dialogs ${JSON.stringify(p._d)}; pageerrors ${JSON.stringify(p._e)}` }); }
  if (process.env.PRESETS_DIR) {
    const bad = path.join(process.env.PRESETS_DIR, 'zz-truncated.json'); fs.writeFileSync(bad, '{"name":"zz-truncated","savedAt":');
    try { const p = await mk(); await p.waitForFunction(() => document.getElementById('combo-ver').textContent);
      await p.click('#btn-load-preset'); await sleep(600);
      rows.push({ red: !(await p.locator('#preset-pop').isVisible()) && p._d.length === 0, t: `(b) Load preset… with one truncated preset file → popover opened=${await p.locator('#preset-pop').isVisible()}; dialogs ${JSON.stringify(p._d)}; pageerrors ${JSON.stringify(p._e)}` });
    } finally { fs.unlinkSync(bad); }
  } else rows.push({ red: false, t: '(b) skipped — set PRESETS_DIR to the server presets/ folder' });
  { const p = await mk(async pg => pg.route('**/api/catalog', r => r.fulfill({ status: 500, body: '' }))); await sleep(2500);
    const gpu = await p.textContent('#gpu-name'); await p.click('#btn-next'); await sleep(600);
    rows.push({ red: gpu === 'Detecting…', t: `(c) /api/catalog 500 at boot → GPU card "${gpu}", combo "${await p.textContent('#combo-ver')}"; Next → dialogs ${JSON.stringify(p._d)}; pageerrors ${JSON.stringify(p._e)}` }); }
  for (const r of rows) console.log(`${r.red ? 'RED  ' : 'GREEN'} ${r.t}`);
  await b.close(); process.exit(rows.some(r => r.red) ? 1 : 0);
})();
```
Run: `dotnet run --project nvcleanstall -- --headless --mock-catalog`, then `BASE_URL=http://localhost:4780 PRESETS_DIR=<app dir>/presets node api-helper-silent-on-non-json-errors.js`. The RED output is captured in **Evidence**.

### B. Manual — running app
1. Close the app. In the `presets/` folder next to `CleanDriver.exe`, create `broken.json` containing `{"name":`. Start the app and click **Load preset…**.
   **Expected:** the list opens (the bad file skipped) or an error is shown. **Actual:** nothing happens.
2. Delete `broken.json`. Run Use driver files on disk → Browse… → Next → Next → Next, then select **Extract only**. Paste an Output folder containing a NUL character, or any path `Path.GetFullPath` rejects, and press **Start**.
   **Expected:** an error alert. **Actual:** nothing happens, and the user stays on Action.

## Root cause
The client's error model is "the server always answers JSON, with `{error}` on failure". That is true only for errors the route catches itself. Anything uncaught becomes a bodiless 500, which the helper cannot parse, and no caller handles a rejected promise.

## Remedy
Frontend-only, A6. Use no new controls, and keep using the existing `alert` pattern for errors (`app.js:132,139,168,391,503,517`).
- Make the helper total, so every caller's existing `if (r.error) alert(r.error)` path just works:
  ```js
  const toJson = async r => {
    const text = await r.text();
    try { const j = JSON.parse(text); return r.ok || j.error ? j : { error: `server error (HTTP ${r.status})` }; }
    catch { return { error: r.ok ? 'unreadable server response' : `server error (HTTP ${r.status})` }; }
  };
  const net = e => ({ error: 'cannot reach the CleanDriver service' });
  const api = { get: p => fetch(p).then(toJson, net), post: (p, b) => fetch(p, { ... }).then(toJson, net) };
  ```
- Callers that read a field without checking `error` must check it first: `btn-load-preset` (`r.presets`), `btn-browse` (`r.path`), and `boot` (`sys`/`cat`/`tw`). For boot, `alert` the error and leave Screen 1 inert rather than posting `version: undefined`.
- Coordinate with `job-pollers-no-exit-on-404-or-network-error`. Its poller-local helper and this helper must not double-handle. Landing this first lets the pollers consume `{error}`.
- Show server text with `alert`/`textContent` only, never `innerHTML`.
- The server fixes stay where they are documented: BUG-06/GAP-C03, `execute-malformed-body-500`, `preset-load-invalid-name-500`.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active. GAP-C03 (presets) and the 500 docs are server-side, and no entry covers the client error model. Get a new owner-pinned entry, or fold this into the same slice as `job-pollers-no-exit-on-404-or-network-error`. Declare `slice_start --no-surface '^nvcleanstall/wwwroot/'`.
1. **Slice setup.** `git worktree add ../nvcleanclone-apierr -b feat/api-helper-silent-on-non-json-errors origin/main`.
2. **Red.** §A transcript with all three lines `RED`. A6 applies: there is no JS runner, and no test file.
3. **Green.** Make the minimal helper change and the caller checks. §A must show an alert for (a) and (b) and a boot alert for (c), with no `pageerror`. Run the full C# suite (unchanged). Commit `"api-helper-silent-on-non-json-errors: surface non-JSON server errors"`.
4. **Refactor**, green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Green transcript plus manual §B in the WebView2 window. Also re-run the full E2E suite (`docs/bugs/ui-functional-test-report.md` appendix). Every previously passing row stays PASS, and no new console or page errors appear.
7. **PR.** Use the template with A6 transcript rows. Push and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.**
2. **Isolated review worktree**, `origin/main...HEAD`.
3. **Re-run the gates** and §A.
4. **Finder pass:**
   - Every `api.get`/`api.post` call site handles `{error}` (`grep -n "api\.\(get\|post\)" app.js`).
   - A JSON 4xx `{error}` still reaches the same alert text as before (S1-06…S1-09 messages unchanged).
   - The pollers' behaviour is unchanged, or improved only in coordination with the pollers doc.
5. **Adversarial verify:**
   - `page.route` answering an HTML 502 on `/api/package`.
   - A network abort on `/api/presets` POST (Save).
   - `/api/tweaks` 500 at boot.
   - A 200 with a non-JSON body.
   - Each produces one readable alert and no uncaught rejection.
6. **Verdict doc.**
7. **Implementer handoff** → C0–C7.
8. **Report to the owner**; the owner merges.

## Acceptance criteria
1. §A prints `GREEN` for (a), (b) and (c): each shows a user-visible error (`alert`), with zero `pageerror`.
2. Existing JSON error alerts are unchanged in wording: S1-06 "expected a package folder or .zip file", S1-07, S1-08, S1-09, N-03 "unknown package token".
3. A failed start-up never lets **Next** post `version: undefined`.
4. The full C# suite is green, and `git diff --check` is clean.
