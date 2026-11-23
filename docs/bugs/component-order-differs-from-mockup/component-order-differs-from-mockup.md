# Screen 3 lists "Container Runtime Services" 7th instead of last, unlike the frozen component inventory

| Field | Value |
|---|---|
| **Slug** | `component-order-differs-from-mockup` |
| **Severity** | Low. Cosmetic deviation from the frozen design contract. The inventory, sizes, states and dependency behaviour are correct; only the row order differs. |
| **Confidence** | Confirmed by a live browser repro (Playwright/Chromium): the live list for 572.16 (the mockup's sample version) was compared row by row with `mockup-screen-03-components.html`. |
| **Area** | `nvcleanstall/data/packages/*/manifest.json` (component order). The root cause is data, rendered in manifest order by `nvcleanstall/wwwroot/app.js` (`renderComponents`). |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | The frozen mockup contract (`docs/design/CleanDriver GPU driver wizard/`) and the design prompt's "Frozen component catalog (name — size — checked state in mockup): 1. Display Driver … 10. Container Runtime Services". CLAUDE.md: "the five screens are a frozen design contract". Not SEC-0x. |
| **Found** | 2026-11-23 (E2E functional pass) |

## Summary
The frozen Screen 3 inventory numbers the components 1–10 with **Container Runtime Services** last. Every bundled mock manifest lists `container` immediately after `telemetry`, next to the two dependencies of GeForce Experience. The wizard renders components in manifest order, so rows 7–10 are shifted. This affects the sample data the mockup was drawn from (572.16, the default "latest") and every other bundled version, including the live-download sample template, which reuses the 572.16 manifest.

## Evidence
Failing UI test in `docs/bugs/ui-functional-test-report.md`: **M-01**.

`data/packages/572.16/manifest.json`: `"id": "telemetry"` at line 53, `"id": "container"` at line 61, `"id": "shield"` at 69, …, `"id": "notebook"` at 85. The same order holds in 571.96, 571.59, 570.86 and 566.36.

`app.js:224` iterates `state.manifest.components` in order (`for (const c of state.manifest.components)`).

Captured RED (harness, `BASE_URL=http://127.0.0.1:4806`):
```
  row 7: mockup "Shield Controller Support" vs live "Container Runtime Services"
  row 8: mockup "Stereo 3D Support" vs live "Shield Controller Support"
  row 9: mockup "Notebook Optimizations" vs live "Stereo 3D Support"
  row 10: mockup "Container Runtime Services" vs live "Notebook Optimizations"
RED   same inventory=true, same order=false
exit=1
```

## Impact
- The shipped Screen 3 does not match the approved mockup that the parity/progress docs say it was built from.
- There is no functional effect. Receipts and manifests list component ids in manifest order, so they will change order if the data is fixed. No test pins that order: `grep` over `CleanDriver.Tests/` finds no order-dependent assertion.

## How to reproduce
### A. Automated — browser transcript (amendment A6)
Script `component-order-differs-from-mockup.js` (set `MOCKUP_DIR` to the repo's `docs/design/CleanDriver GPU driver wizard`):
```js
// Repro: Screen 3 row order vs the frozen mockup (docs/design/.../mockup-screen-03-components.html).
const { chromium } = require('playwright');
const path = require('path');
const BASE = (process.env.BASE_URL || 'http://localhost:4780').replace(/\/$/, '');
const DIR = process.env.MOCKUP_DIR || '/home/user/nvcleanclone/docs/design/CleanDriver GPU driver wizard';
(async () => {
  const b = await chromium.launch();
  const m = await b.newContext(); await m.route(u => !u.href.startsWith('file:'), r => r.abort());
  const mp = await m.newPage(); await mp.goto('file://' + path.join(DIR, 'mockup-screen-03-components.html'));
  const mock = (await mp.$$eval('.crow .cname', e => e.map(x => x.textContent.replace(/\s+/g, ' ').trim()))).map(s => s.replace(' (required)', ''));
  const ctx = await b.newContext({ viewport: { width: 960, height: 640 } }); await ctx.route(u => !u.href.startsWith(BASE), r => r.abort());
  const p = await ctx.newPage(); await p.goto(BASE + '/?shell=1'); await p.waitForFunction(() => document.getElementById('combo-ver').textContent);
  await p.click('#btn-next'); await p.waitForSelector('#screen-components.active', { timeout: 20000 });  // catalog 572.16 = mockup data
  const live = await p.$$eval('#comp-list .crow .cname', e => e.map(x => x.childNodes[0].textContent.trim()));
  const red = JSON.stringify(mock) !== JSON.stringify(live);
  mock.forEach((n, i) => { if (n !== live[i]) console.log(`  row ${i + 1}: mockup "${n}" vs live "${live[i]}"`); });
  console.log(`${red ? 'RED  ' : 'GREEN'} same inventory=${JSON.stringify([...mock].sort()) === JSON.stringify([...live].sort())}, same order=${!red}`);
  await b.close(); process.exit(red ? 1 : 0);
})();
```
Run: `dotnet run --project nvcleanstall -- --headless --mock-catalog`, then `BASE_URL=http://localhost:4780 MOCKUP_DIR="$PWD/docs/design/CleanDriver GPU driver wizard" node component-order-differs-from-mockup.js`.

### B. Manual — running app
1. Open the wizard, keep the latest version (572.16), press **Next** and wait for Components.
2. Open `docs/design/CleanDriver GPU driver wizard/mockup-screen-03-components.html` side by side.
3. **Expected:** rows 7–10 read Shield, Stereo 3D, Notebook, Container. **Actual:** Container, Shield, Stereo 3D, Notebook.

## Root cause
The mock manifests were authored grouping GeForce Experience's dependencies (`telemetry`, `container`) together, and nobody compared the data order against the frozen inventory, which the renderer follows faithfully.

## Remedy
Data-only, with no code change. Move the `container` component object to the end of `components[]` in each `data/packages/<ver>/manifest.json` (5 files). Keep ids, sizes, payloads and `dependsOn` unchanged. 566.36 has no USB-C row, so its order becomes Display, PhysX, HD Audio, GFE, Telemetry, Shield, Stereo 3D, Notebook, Container. Do **not** sort in `app.js`: local manifests are user data and must render in their own order. Keep the mock catalog's other properties untouched. 566.36 still intentionally lacks USB-C.

## TDD fix workflow (CONTRIBUTING.md + amendment A6)
0. **Register gate.** `docs/security_register.md` is active, and no entry covers mock-data conformance. Get a new owner-pinned entry, or a docs/data ruling. A data-only change under `data/` is not `wwwroot/`, so A6 does not apply automatically: ask the owner whether an xunit order pin is wanted (a red is then possible) or whether the browser transcript suffices.
1. **Slice setup.** `git worktree add ../nvcleanclone-comporder -b feat/component-order-differs-from-mockup origin/main`.
2. **Red.** Optionally add xunit `MockManifestOrderTests`: for each bundled version, the component id order equals the frozen inventory order (minus absent ids). It fails today at index 6 (`container` vs `shield`). Commit `"component-order-differs-from-mockup (red): pin mock manifest order to the frozen inventory"`. Also capture the §A transcript red.
3. **Green.** Reorder the five manifests. Run the full suite and §A (`GREEN`). Commit `"component-order-differs-from-mockup: list container last as in the frozen inventory"`.
4. **Refactor**, green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** Green transcript plus a WebView2 check of Screen 3 for 572.16 and 566.36. Recommended and the GFE dependency notice must behave unchanged.
7. **PR.** Use the template (TDD rows, xunit and/or transcript). Push and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.**
2. **Isolated review worktree**, `origin/main...HEAD`.
3. **Re-run the gates** and §A.
4. **Finder pass:**
   - Only array order changed (`git diff -w` shows moved blocks, no field edits).
   - The 566.36 USB-C omission is preserved.
   - The live sample template (`LoadSampleTemplate` reuses the newest mock) inherits the new order.
5. **Adversarial verify:**
   - The dependency notice text order ("Requires: Container Runtime Services, Telemetry Services") still follows `dependsOn`, not row order.
   - The "Skipping deselected components (N)" count is unchanged.
6. **Verdict doc.**
7. **Implementer handoff** → C0–C7.
8. **Report to the owner**; the owner merges.

## Acceptance criteria
1. For 572.16 the Screen 3 rows match `mockup-screen-03-components.html` exactly in name and order (§A `GREEN`). The other bundled versions follow the same relative order.
2. Component ids, sizes, payloads, `dependsOn` and the 566.36 USB-C omission are unchanged.
3. The full C# suite is green, and `git diff --check` is clean.
