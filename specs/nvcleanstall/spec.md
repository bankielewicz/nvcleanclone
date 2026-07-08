---
target: "NVCleanstall"
slug: nvcleanstall
status: approved
scope: "default — core feature set an average user touches in their first hour"
fidelity: functional
platform: Windows 11 desktop
stack: ".NET 10 (LTS) C# — Kestrel minimal API + vanilla HTML/CSS/JS frontend in a WebView2 shell; self-contained single-file CleanDriver.exe"
---

# NVCleanstall — Clone Specification

# Part I — Specification (written in Phase 2)

## 1. Scope cut

A safety boundary shapes this cut: the clone **does not download, modify, or install real
NVIDIA driver packages**. It reproduces the full workflow — detection, version catalog,
component selection, tweaks, packaging — against a bundled mock driver catalog and mock
package layout, and it emits real, inspectable artifacts (customized package folder, install
log, tweak scripts it does NOT execute). This keeps behavior parity while never touching the
user's actual driver state.

| FEAT | Feature | Disposition | Detail |
|---|---|---|---|
| FEAT-001 | GPU auto-detection | simplified | Reads the real GPU name via WMI on Windows; falls back to a simulated GPU when none/undetectable. No PCI-id database. |
| FEAT-002 | Fetch latest driver from web | simplified | "Downloads" from a bundled mock catalog with a realistic progress simulation; no real NVIDIA servers contacted. |
| FEAT-003 | Driver version picker (WHQL/beta/older) | in | Version dropdown backed by the mock catalog (multiple versions, WHQL/beta channels, release dates). |
| FEAT-004 | Use driver file on disk | simplified | Accepts a package folder/zip in the clone's mock package format; validates and loads its component manifest. |
| FEAT-005 | Download progress / mirror handling | simplified | Progress bar with speed/percent over the simulated download; no Cloudflare bypass. |
| FEAT-006 | Component checklist | in | Full checkbox list from the package manifest: Display Driver, PhysX, HD Audio, USB-C, GeForce Experience, Telemetry, Shield, Stereo 3D, notebook optimizations, container services. |
| FEAT-007 | Display Driver mandatory | in | Locked checked, cannot be deselected. |
| FEAT-008 | Recommended selection | in | One-click Recommended preset (Display Driver + PhysX + HD Audio; everything else off). |
| FEAT-009 | Dependency checking | in | Manifest declares dependencies (e.g. GeForce Experience → Container + Telemetry); selecting a dependent auto-selects/warns; deselecting a dependency warns. |
| FEAT-010 | Disable installer telemetry & advertising | in | Tweak toggle; reflected in the generated package config. |
| FEAT-011 | Unattended express install (auto-reboot) | simplified | Toggle with auto-reboot sub-option; affects simulated install (no prompts in log); never reboots the machine. |
| FEAT-012 | Clean installation | in | Toggle; simulated install log shows "removing previous driver traces" steps. |
| FEAT-013 | Disable MPO | in | Tweak toggle; emits the corresponding .reg snippet into the output artifacts (not applied). |
| FEAT-014 | Disable Ansel | in | Tweak toggle; recorded in package config. |
| FEAT-015 | Install NVIDIA Control Panel | in | Component/tweak toggle recorded in package config. |
| FEAT-016 | Show expert tweaks toggle | in | Expert section hidden until enabled. |
| FEAT-017 | Disable driver telemetry (experimental) | simplified | Toggle with experimental warning; recorded in config; no real driver patching. |
| FEAT-018 | Disable NVIDIA Container (experimental) | in | Toggle with "breaks NVIDIA Control Panel" warning; conflict surfaced if Control Panel also selected. |
| FEAT-019 | Disable HD audio sleep timer | in | Toggle; emits .reg snippet artifact. |
| FEAT-020 | MSI mode with priority | in | Toggle plus priority sub-setting (default/high); emits .reg snippet artifact. |
| FEAT-021 | Disable HDCP | in | Toggle; emits .reg snippet artifact. |
| FEAT-022 | Rebuild digital signature | simplified | When the component set differs from stock, a "rebuild signature" step appears in the build log and the package manifest is marked re-signed; no real code signing. |
| FEAT-023 | Per-option explanations | in | Every component and tweak shows a description panel on selection, with warnings for experimental items. |
| FEAT-024 | Install now (GUI) | simplified | Runs a simulated installer: step-by-step progress + log; writes an install receipt file; changes nothing system-wide. |
| FEAT-025 | Silent install | simplified | Same simulation without interactive progress UI; log only. |
| FEAT-026 | Extract only | in | Writes the customized package (selected components only, rewritten manifest) to a chosen folder — a real, inspectable artifact. |
| FEAT-027 | Build self-contained installer EXE | simplified | Builds a self-contained *package directory* with a runnable installer script + config (not a single EXE). |
| FEAT-028 | Save/load preset | in | Full selection (components + tweaks) saved to a JSON preset file; loadable on a later run. |

## 2. Functional requirements

| ID | Requirement | Implements |
|---|---|---|
| FR-001 | On launch, the app shows the detected GPU name and installed driver version (real via WMI when available, otherwise a labeled simulated GPU). | FEAT-001 |
| FR-002 | The user can choose "install latest" or pick any version from a dropdown listing ≥5 catalog versions with channel (WHQL/beta) and release date. | FEAT-002, FEAT-003 |
| FR-003 | Selecting a catalog version starts a simulated download showing percent and speed, then advances to component selection. | FEAT-002, FEAT-005 |
| FR-004 | The user can instead point at a local package (folder or zip in the clone's format); the app validates it and loads its component manifest, fully offline. | FEAT-004 |
| FR-005 | The component screen lists every component in the manifest with checkbox, size, and description; Display Driver is checked and disabled. | FEAT-006, FEAT-007, FEAT-023 |
| FR-006 | A "Recommended" button applies the recommended set (Display Driver + PhysX + HD Audio, all else off). | FEAT-008 |
| FR-007 | Checking a component with unmet dependencies auto-selects them and tells the user; unchecking a component others depend on warns and lists the dependents. | FEAT-009 |
| FR-008 | The tweaks screen offers all installation tweaks (installer telemetry, unattended+auto-reboot, clean install, MPO, Ansel, Control Panel) with per-option descriptions. | FEAT-010–FEAT-015, FEAT-023 |
| FR-009 | Expert tweaks are hidden until "Show expert tweaks" is enabled, then offer driver telemetry (experimental), NVIDIA Container (with breakage warning + conflict check vs Control Panel), HD audio sleep timer, MSI mode with priority sub-setting, HDCP, each with descriptions/warnings. | FEAT-016–FEAT-021, FEAT-023 |
| FR-010 | The action screen offers Install, Silent install, Extract only, Build package; Install/Silent run a simulated install producing a timestamped log and receipt file, honoring clean-install and unattended flags in the log output. | FEAT-011, FEAT-012, FEAT-024, FEAT-025 |
| FR-011 | Extract-only writes a customized package folder containing only selected components and a rewritten manifest; deselected components are absent. | FEAT-026 |
| FR-012 | Build-package writes a self-contained package directory with the customized payload, an install script, and the chosen tweak config. | FEAT-027 |
| FR-013 | When the component set differs from stock, the build log shows a "rebuilding digital signature" step and the output manifest is marked `signature: rebuilt`. | FEAT-022 |
| FR-014 | Registry-affecting tweaks (MPO, HD audio sleep, MSI, HDCP) each emit a correctly-formed .reg snippet into the output artifacts; nothing is applied to the live system. | FEAT-013, FEAT-019, FEAT-020, FEAT-021 |
| FR-015 | The user can save the current selections as a named preset file and load it on a later run, restoring every component and tweak choice. | FEAT-028 |
| FR-016 | Driver-telemetry tweak state is recorded in the output config with its experimental flag. | FEAT-017 |

## 3. Data model

- **GpuInfo** { name, isSimulated, installedDriverVersion } — detected at startup.
- **DriverRelease** { version, channel: whql|beta, releaseDate, sizeMB, packageRef } — rows of the bundled catalog (`catalog.json`).
- **DriverPackage** { version, components: Component[] } — loaded from a package manifest (`manifest.json`).
- **Component** { id, name, description, sizeMB, required: bool, recommended: bool, dependsOn: id[] }.
- **Tweak** { id, name, description, category: install|expert, experimental: bool, warning?, params? (e.g. msiPriority: default|high), regSnippet? }.
- **Selection** { componentIds: id[], tweaks: { id → value } } — the working state carried across wizard pages.
- **Preset** { name, savedAt, selection: Selection } — serialized JSON.
- **Job** { action: install|silent|extract|package, outputPath?, logLines[], status, receipt? } — one per executed action.

Relationships: catalog → releases → package → components; selection references component and
tweak ids; a job snapshots one selection against one package.

## 4. Screens / states

### Screen 1 — Driver source
- Shows: detected GPU + installed driver version; radio "Install latest" with version dropdown (version, channel, date); radio "Use driver files on disk" with browse field.
- Actions: pick version, browse local package, Next.

### Screen 2 — Download
- Shows: progress bar, percent, simulated speed, version being fetched.
- Actions: cancel (back to Screen 1); auto-advances on completion.

### Screen 3 — Components
- Shows: checkbox list of components with sizes; description panel for highlighted item; running total install size; dependency notices.
- Actions: toggle components (Display Driver locked), Recommended button, Back, Next.

### Screen 4 — Tweaks
- Shows: installation tweak checkboxes; "Show expert tweaks" toggle revealing expert list; description/warning panel; MSI priority sub-control; conflict warnings (Container vs Control Panel).
- Actions: toggle tweaks, set MSI priority, save preset, load preset, Back, Next.

### Screen 5 — Action & result
- Shows: action choices (Install / Silent / Extract / Build package) with output path picker where relevant; then live log view with progress; final status + receipt/artifact location; reboot-recommended notice when applicable.
- Actions: run chosen action, open output folder, Finish/start over.

## 5. Acceptance criteria

| ID | Proves | Exercises | End-to-end scenario |
|---|---|---|---|
| AC-001 | F1 | FR-001, FR-002, FR-003, FR-005, FR-008, FR-010 | Launch → GPU shown → pick latest → simulated download completes → uncheck GeForce Experience & Telemetry → enable clean install → Install → log shows clean-install steps and only selected components; receipt file written. |
| AC-002 | F2 | FR-004, FR-005, FR-011 | Point at the bundled sample package on disk (no catalog/download) → manifest loads → deselect USB-C and Stereo 3D → Extract only → output folder lacks those components' payloads and its manifest lists only selected ones. |
| AC-003 | F3 | FR-002, FR-003 | Open the version dropdown → choose an older beta release → download screen shows that exact version → component list matches that version's manifest. |
| AC-004 | F4 | FR-012, FR-013, FR-014 | Configure a trimmed component set + MSI mode (high) + HDCP off → Build package → output directory contains payload, install script, config listing both tweaks, .reg snippets for MSI and HDCP, and a manifest marked `signature: rebuilt`; build log shows the re-sign step. |
| AC-005 | F5 | FR-015, FR-006, FR-007, FR-009 | Apply Recommended → enable expert tweaks → toggle NVIDIA Container (warning about Control Panel appears) → save preset "lean" → restart app → load "lean" → every selection restored exactly. |

# Part II — Architecture & Plan (written in Phase 3)

## 6. Stack

**.NET 10 (LTS) C#: an ASP.NET Core (Kestrel) minimal API serves the wizard frontend
(vanilla HTML/CSS/JS from the approved mockups) and JSON API at `http://localhost:4780`,
hosted inside a WebView2 native window — published as a self-contained single-file
`CleanDriver.exe`, so end users install no runtime.** WebView2 is preinstalled on every
Windows 11 machine, giving a real desktop window while the UI stays the mockups' HTML
verbatim. The Kestrel split keeps every acceptance criterion terminal-verifiable
(`--headless` runs the server without the window); only "the window opens" is a manual
check. Maintainer-pinned 2026-07-08 after challenging the original Node.js choice on its
end-user runtime prerequisite; Go single-exe was the runner-up (smaller binary) but .NET
was chosen for native-Windows feel and the growth path toward real WMI/registry/driver
work.

Historical note: M1 scaffolding was first written in Node.js before the pin; it was
ported to C# with no verified behavior lost (nothing had been verified yet).

## 7. Project structure

```
nvcleanstall/
  CleanDriver.csproj   — net10.0-windows, WinForms + WebView2 + ASP.NET Core framework ref
  Program.cs           — entry: starts Kestrel; opens the WebView2 shell unless --headless
  ShellForm.cs         — fixed 976×704 native window hosting WebView2 → localhost:4780
  Api.cs               — minimal-API route map (/api/system, catalog, package, download,
                          execute, jobs, presets, open-folder)
  Lib/
    Gpu.cs             — GPU detection (WMI Win32_VideoController, simulated fallback)
    Catalog.cs         — loads data/catalog.json, version listing
    Packages.cs        — manifest load/validate (folder or zip), customized-package writer
    Tweaks.cs          — tweak catalog (descriptions, warnings, params) + .reg snippet emitters
    Jobs.cs            — simulated download + install/silent/extract/package jobs, log + receipt
    Presets.cs         — preset save/load (JSON under presets/)
  wwwroot/
    index.html         — wizard shell (5 screens)
    styles.css         — pinned design tokens (Fluent, light/dark, green accent)
    app.js             — wizard state machine + API calls
  data/
    catalog.json       — mock driver releases (versions, channels, dates)
    packages/<ver>/    — mock package: manifest.json + per-component payload files
  output/              — install receipts, extracted/built packages (scratch)
  presets/             — saved presets
  README.md
```

## 8. Build order

| Milestone | Goal | Delivers | Demonstrable result |
|---|---|---|---|
| M1 | Walking skeleton of F1 + F3: full wizard happy path against the mock catalog — source screen with version dropdown, simulated download, component checklist, tweaks screen, simulated install with log + receipt | AC-001, AC-003 | `node server.js`, open localhost:4780, complete an install end-to-end; receipt JSON appears in output/ |
| M2 | Offline flow F2: load a local package from disk, extract-only writes a customized package folder | AC-002 | Point the wizard at data/packages/…, deselect components, extract; output folder inspectably lacks them |
| M3 | Packaging F4: build-package output (payload + install script + config), .reg snippet artifacts, signature-rebuild step in build log | AC-004 | Build a package with MSI+HDCP tweaks; output dir contains script, config, .reg files, `signature: rebuilt` manifest |
| M4 | Selection intelligence + presets F5: Recommended button, dependency auto-select/warnings, expert-tweak reveal + conflict warning, preset save/load across restarts | AC-005 | Save preset "lean", restart server, load it, selections restored |
| M5 | Distribution: self-contained single-file publish + WebView2 shell window | (no ACs — delivers the stack pin's end-user requirement) | `dotnet publish` produces one CleanDriver.exe; running it opens the native window (window itself: manual check) |

Coverage check: AC-001 (M1), AC-002 (M2), AC-003 (M1), AC-004 (M3), AC-005 (M4) — every
in-scope AC appears in exactly one milestone. M5 delivers the pinned distribution
requirement rather than an AC.

## Changelog

- 2026-07-08 — draft created.
- 2026-07-08 — checkpoint: approved as-is (scope cut + mock-driver safety boundary). Design pins D1–D5 (CleanDriver brand, Fluent Win11 look, green accent, 960×640, HTML mockups) kept as pinned.
- 2026-07-08 — Part II filled: stack pinned (Node.js zero-dep + vanilla web), structure and milestones M1–M4 defined.
- 2026-07-08 — stack re-pinned by maintainer to .NET 10 + WebView2 (self-contained single exe, Kestrel split, --headless for verification) after challenging the Node.js end-user prerequisite; Go single-exe evaluated as runner-up. §7 structure rewritten; M5 (distribution) added.
