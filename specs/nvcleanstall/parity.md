---
target: "NVCleanstall"
slug: nvcleanstall
date: 2026-07-08
updated: 2026-07-09 (post-GAP re-grade — see §4)
verification_mode: executed in-terminal
---

# NVCleanstall — Parity & Verification Report

Clone name: **CleanDriver**. Stack: .NET 10 (Kestrel + WebView2), self-contained single exe.
All acceptance criteria were exercised against the built app running headless on
`localhost:4780` (curl for the API, Playwright for the wizard UI).

## 1. Acceptance criterion results

| ID | Scenario (short) | How exercised | Result |
|---|---|---|---|
| AC-001 | F1: latest driver → download → uncheck GFE+Telemetry → clean install → receipt | Playwright walk of all 5 screens + curl `/api/execute`; inspected `output/receipt-*.json` (correct components, `signature: rebuilt`, `cleanInstall: true`); log shows clean-install steps | pass |
| AC-002 | F2: load local package offline → deselect USB-C + Stereo 3D → extract-only | curl `/api/package` (kind=local, bundled 571.96 folder, no download) → `/api/execute` extract; output `payload/` lacks usbc/stereo3d, rewritten manifest lists only selected ids | pass |
| AC-003 | F3: pick older beta from dropdown → that version's package loads | Playwright: opened dropdown (5 versions, WHQL/Beta tags, Latest/Installed annotations), chose 571.59 Beta; download subtitle + component sizes matched that version | pass |
| AC-004 | F4: trimmed set + MSI(High) + HDCP → build package → artifacts + re-sign | curl `/api/execute` package; output dir has payload, `install.cmd`, `config.json` (both tweaks), `tweak-msi-mode.reg` (DevicePriority=3), `tweak-disable-hdcp.reg`, manifest `signature: rebuilt` | pass |
| AC-005 | F5: Recommended + dependency + conflict → save preset → restart → load restores all | Playwright: Recommended, dependency auto-select/deselect, container↔control-panel warning; saved "lean", killed & relaunched process, loaded — components + tweaks + expert-reveal all restored | pass |

Distribution (M5, no AC): `dotnet publish` produced one self-contained 139 MB `CleanDriver.exe`
that ran standalone with no runtime installed and served the wizard. The WebView2 **window
opening** is `not verifiable in-terminal — manual check required` (headless mode was used for
all automated checks; the server/API path it wraps is fully verified).

## 2. Parity table

| FEAT | Feature | Original behavior | Clone behavior | Status |
|---|---|---|---|---|
| FEAT-001 | GPU auto-detection | Detects installed NVIDIA GPU + driver | WMI `Win32_VideoController`, pure testable parse + deterministic multi-GPU pick + documented WMI→simulated fallback (GAP-03, PR #5); no PCI-id database | simplified |
| FEAT-002 | Fetch latest driver | Downloads newest from NVIDIA | Live NVIDIA metadata lookup behind `ICatalogProvider` (GAP-01, PR #3) + real byte-for-byte installer download to `output/drivers/` (GAP-02, PR #4); mock fallback offline. The downloaded installer is never parsed/executed — the component list stays a labeled sample (GAP-OUT-1) | parity |
| FEAT-003 | Version picker (WHQL/beta/older) | Dropdown of real releases | Dropdown of live NVIDIA releases (`source:"live"`) with channel + date + Latest/Installed tags; 5 mock releases when offline/`--mock-catalog` (GAP-01) | parity |
| FEAT-004 | Use file on disk | Customizes an existing NVIDIA package | Loads a folder/zip in the clone's manifest format, offline. Real NVIDIA package parsing is deferred (register GAP-OUT-1) — the one `simplified` row no gap closed | simplified |
| FEAT-005 | Download progress | Real download w/ progress | Real streaming progress from `Content-Length`/bytes-read on the live path, atomic `.part`→final, cancel/failure cleanup (GAP-02); simulation kept on the mock path (byte-identity pin) | parity |
| FEAT-006 | Component checklist | All modules, checkable | Full 10-component checklist from manifest w/ sizes + descriptions | parity |
| FEAT-007 | Display Driver mandatory | Locked on | Locked, disabled checkbox | parity |
| FEAT-008 | Recommended selection | Auto-selects sensible set | "Recommended" button → Display+PhysX+HD Audio | parity |
| FEAT-009 | Dependency checking | Pulls in / warns on deps | GFE→Container+Telemetry auto-select; deselect a dep warns + removes dependents | parity |
| FEAT-010 | Disable installer telemetry/ads | Tweak toggle | Toggle, recorded in receipt/config | parity |
| FEAT-011 | Unattended (+auto-reboot) | No-prompt install | Toggle + sub-option; log honors the flag (*reboot allowed but not performed — simulated*), no-reboot guard test (GAP-04). Permanent: install stays simulated by the safety boundary | simplified |
| FEAT-012 | Clean installation | Wipes prior driver | Toggle; log shows removal steps | parity |
| FEAT-013 | Disable MPO | Registry tweak | Toggle → emits `tweak-disable-mpo.reg` (not applied) | parity |
| FEAT-014 | Disable Ansel | Tweak toggle | Toggle, recorded in config | parity |
| FEAT-015 | Install control panel | Component/tweak | Toggle, recorded; conflicts with container tweak | parity |
| FEAT-016 | Show expert tweaks | Hidden until enabled | Switch reveals expert section; auto-revealed when a preset enables an expert tweak | parity |
| FEAT-017 | Disable driver telemetry (exp.) | Patches driver telemetry | Toggle w/ badge; `driverTelemetrySimulated: true` marker on receipt/manifest/config + qualified log line (GAP-05). Permanent: no real patching by the safety boundary | simplified |
| FEAT-018 | Disable NVIDIA Container (exp.) | Blocks container service | Toggle w/ badge; inline warning when control panel also selected | parity |
| FEAT-019 | Disable HD-audio sleep | Registry tweak | Toggle → emits `tweak-hd-audio-sleep.reg` | parity |
| FEAT-020 | MSI mode + priority | MSI registry + priority | Toggle + Default/High segmented control → emits `.reg` (DevicePriority=3 on High) | parity |
| FEAT-021 | Disable HDCP | Registry tweak | Toggle → emits `tweak-disable-hdcp.reg` | parity |
| FEAT-022 | Rebuild digital signature | Re-signs modified package | `signature: rebuilt` + `signatureSimulated: true` marker and `(simulated — no real signing performed)` log qualifier (GAP-05). Permanent: no real signing by the safety boundary | simplified |
| FEAT-023 | Per-option explanations | Description panel per item | Right-hand description panel for every component + tweak, warnings on experimental, MSI "Learn more" link | parity |
| FEAT-024 | Install now (GUI) | Runs modified installer | Simulated install: progress + log + receipt; receipt records the real downloaded installer's path/size when the live path supplied it (GAP-04). Permanent: never executes by the safety boundary | simplified |
| FEAT-025 | Silent install | No-UI install | Same simulation, log-only; same real-artifact receipt fields (GAP-04). Permanent: never executes by the safety boundary | simplified |
| FEAT-026 | Extract only | Unpacks customized package | Writes real customized package folder (selected components + rewritten manifest) | parity |
| FEAT-027 | Build self-contained EXE | Single redistributable EXE | One redistributable archive `<version>-cleandriver-package.zip` packed from exactly that build's outputs, beside the kept directory (GAP-06, PR #12). Format is a `.zip`, not a self-extracting EXE — the register-approved deliverable | parity |
| FEAT-028 | Save/load preset | Persist selection | Selection saved to `presets/*.json`, survives process restart, restores components + tweaks | parity |

Omitted (disposition `out`): none.

Post-GAP tally: 21 parity / 7 simplified (was 18/10 at baseline). The 7 remaining
`simplified` rows split: 5 permanent by the safety boundary (FEAT-011, -017, -022, -024,
-025 — never execute, never install, never patch), 2 deferred with named destinations
(FEAT-001 PCI-id database; FEAT-004 real package parsing → GAP-OUT-1).

## 3. Next steps toward fuller parity (revised 2026-07-09)

The original five next-steps were written at baseline; GAP-01…GAP-06 (PRs #3–#6, #8, #12)
have since landed. Step 2 (single-file package output) is done. The remainder, renumbered,
each mapped to its register destination:

1. **Real driver ingestion (read-only)** — parse an actual NVIDIA `.exe`/7z package and its
   `setup.cfg`/manifest so the component list reflects the genuine driver (closes FEAT-004
   and FEAT-002's sample-component caveat). Register: `GAP-OUT-1`; requires a future
   register that lifts the *extraction* boundary explicitly (execution stays forbidden).
2. **Real dependency graph** derived from the ingested package instead of the hand-authored
   mock manifest. Depends on 1.
3. **Apply tweaks for real (opt-in, elevated)** — import the generated `.reg` files behind
   an explicit confirmation. Register: `GAP-OUT-3`; needs its own opt-in/elevation ADR.
4. **Native file/folder pickers** in the WebView2 shell. Register: `GAP-OUT-4`.
5. **PCI-id database for detection** (FEAT-001's remaining gap) — exact device matching
   instead of WMI name heuristics.

Not a parity item, but filed from the GAP-06 review and belonging in the next register:
the build **output directory is never cleaned** (`extract`/`package` accumulate stale
payloads across runs into the same default path; the archive is already immune). Owner
decision required on whether a user-supplied `outputPath` may ever be cleaned at all.

## 4. Post-baseline updates

- **2026-07-09 — §2 re-graded and §3 revised after the gap register closed** (GAP-01…06
  all merged; see `docs/gaps_analysis.md` and `CHANGELOG.md`). §1's AC results are the
  original baseline verification and still hold (the suite grew 30 → 75 tests across the
  gap PRs, all green at each merge). Baseline text of the changed rows is preserved in
  git history at `c704800`.
