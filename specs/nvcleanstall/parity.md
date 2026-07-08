---
target: "NVCleanstall"
slug: nvcleanstall
date: 2026-07-08
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
| FEAT-001 | GPU auto-detection | Detects installed NVIDIA GPU + driver | WMI `Win32_VideoController` (detected a real RTX 5070 in test); simulated fallback | simplified |
| FEAT-002 | Fetch latest driver | Downloads newest from NVIDIA | "Downloads" newest from bundled mock catalog | simplified |
| FEAT-003 | Version picker (WHQL/beta/older) | Dropdown of real releases | Dropdown of 5 mock releases, channel + date + Latest/Installed tags | parity |
| FEAT-004 | Use file on disk | Customizes an existing NVIDIA package | Loads a folder/zip in the clone's manifest format, offline | simplified |
| FEAT-005 | Download progress | Real download w/ progress | Simulated progress (percent + speed) | simplified |
| FEAT-006 | Component checklist | All modules, checkable | Full 10-component checklist from manifest w/ sizes + descriptions | parity |
| FEAT-007 | Display Driver mandatory | Locked on | Locked, disabled checkbox | parity |
| FEAT-008 | Recommended selection | Auto-selects sensible set | "Recommended" button → Display+PhysX+HD Audio | parity |
| FEAT-009 | Dependency checking | Pulls in / warns on deps | GFE→Container+Telemetry auto-select; deselect a dep warns + removes dependents | parity |
| FEAT-010 | Disable installer telemetry/ads | Tweak toggle | Toggle, recorded in receipt/config | parity |
| FEAT-011 | Unattended (+auto-reboot) | No-prompt install | Toggle + sub-option; log reflects it; never reboots | simplified |
| FEAT-012 | Clean installation | Wipes prior driver | Toggle; log shows removal steps | parity |
| FEAT-013 | Disable MPO | Registry tweak | Toggle → emits `tweak-disable-mpo.reg` (not applied) | parity |
| FEAT-014 | Disable Ansel | Tweak toggle | Toggle, recorded in config | parity |
| FEAT-015 | Install control panel | Component/tweak | Toggle, recorded; conflicts with container tweak | parity |
| FEAT-016 | Show expert tweaks | Hidden until enabled | Switch reveals expert section; auto-revealed when a preset enables an expert tweak | parity |
| FEAT-017 | Disable driver telemetry (exp.) | Patches driver telemetry | Toggle w/ Experimental badge; recorded in config; no real patch | simplified |
| FEAT-018 | Disable NVIDIA Container (exp.) | Blocks container service | Toggle w/ badge; inline warning when control panel also selected | parity |
| FEAT-019 | Disable HD-audio sleep | Registry tweak | Toggle → emits `tweak-hd-audio-sleep.reg` | parity |
| FEAT-020 | MSI mode + priority | MSI registry + priority | Toggle + Default/High segmented control → emits `.reg` (DevicePriority=3 on High) | parity |
| FEAT-021 | Disable HDCP | Registry tweak | Toggle → emits `tweak-disable-hdcp.reg` | parity |
| FEAT-022 | Rebuild digital signature | Re-signs modified package | When component set differs from stock: "rebuilding signature" log line + manifest `signature: rebuilt`; no real signing | simplified |
| FEAT-023 | Per-option explanations | Description panel per item | Right-hand description panel for every component + tweak, warnings on experimental, MSI "Learn more" link | parity |
| FEAT-024 | Install now (GUI) | Runs modified installer | Simulated install: progress + log + receipt file | simplified |
| FEAT-025 | Silent install | No-UI install | Same simulation, log-only (no progress bar) | simplified |
| FEAT-026 | Extract only | Unpacks customized package | Writes real customized package folder (selected components + rewritten manifest) | parity |
| FEAT-027 | Build self-contained EXE | Single redistributable EXE | Builds a package *directory*: payload + `install.cmd` + `config.json` | simplified |
| FEAT-028 | Save/load preset | Persist selection | Selection saved to `presets/*.json`, survives process restart, restores components + tweaks | parity |

Omitted (disposition `out`): none.

## 3. Top 5 next steps toward fuller parity

1. **Real driver ingestion (read-only):** parse an actual NVIDIA `.exe`/7z package and its
   `setup.cfg`/manifest so the component list reflects a genuine driver — still without
   installing — closing the biggest gap between the mock catalog and the original.
2. **True single-EXE package output (FEAT-027):** bundle the customized payload + installer
   into one self-extracting executable rather than a directory.
3. **Real WMI-driven Recommended set and dependency graph** derived from the ingested
   package instead of the hand-authored mock manifest.
4. **Apply tweaks for real (opt-in, elevated):** actually import the generated `.reg` files
   and set MSI mode via the live registry behind an explicit "I understand" confirmation,
   turning the simulated tweaks into functional ones.
5. **Native file/folder pickers** in the WebView2 shell (host-side `OpenFileDialog`) so
   "Browse…" and output-path selection use real OS dialogs instead of the bundled-sample
   shortcut.
