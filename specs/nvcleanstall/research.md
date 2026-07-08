---
target: "NVCleanstall"
slug: nvcleanstall
date: 2026-07-08
research_mode: live sources
---

# NVCleanstall — Research

## 1. What it is

**Category:** Windows desktop utility (driver installation customizer).
**Platform:** Windows 10/11, x64. Distributed as a small portable executable by TechPowerUp
(same developer as GPU-Z).
**Target users:** PC gamers, enthusiasts, and privacy-conscious users who want NVIDIA GPU
drivers without the bundled extras (GeForce Experience, telemetry, USB-C driver, etc.).
**Core problem it solves:** NVIDIA's official driver package installs many components the
user never asked for and runs background services that consume resources and phone home.
NVCleanstall fetches (or accepts) an NVIDIA driver package, lets the user choose exactly
which components to install, applies optional system tweaks, and then installs — or builds a
reusable customized installer package.

## 2. Feature inventory

### Driver acquisition

| ID | Feature | Behavior notes | Source |
|---|---|---|---|
| FEAT-001 | GPU auto-detection | Detects the installed NVIDIA GPU and currently installed driver version to suggest the right driver family | https://techmarsh.com/how-nvcleanstall-customizes-nvidia-drivers-for-performance-and-precision/ |
| FEAT-002 | Fetch latest driver from web | Downloads the newest matching driver package directly from NVIDIA servers | https://www.techpowerup.com/nvcleanstall/ (via search summary) |
| FEAT-003 | Driver version picker (WHQL / beta / older) | Dropdown of available driver versions; supports both WHQL and beta builds, not just the latest | https://techmarsh.com/…, https://www.techspot.com/downloads/7246-nvcleanstall.html |
| FEAT-004 | Use driver file already on disk | Accepts an existing NVIDIA installer package from disk and customizes the components inside; works fully offline | https://www.techpowerup.com/nvcleanstall/ (via search summary), techspot.com |
| FEAT-005 | Download progress with resume/mirror handling | Shows download progress; v1.19 added a Cloudflare bypass for censored regions | https://www.techspot.com/downloads/7246-nvcleanstall.html |

### Component customization

| ID | Feature | Behavior notes | Source |
|---|---|---|---|
| FEAT-006 | Component checklist | Expands the driver package and lists every module (Display Driver, PhysX, HD Audio, USB-C, GeForce Experience, Shield support, Stereo 3D, notebook optimizations, telemetry services, NVIDIA Container services); user checks/unchecks each | techsuse.com, techmarsh.com |
| FEAT-007 | "Display Driver" is mandatory | Core display driver is marked required and cannot be deselected | https://github.com/shoober420/windows11-scripts/blob/main/NVCleanstall.txt |
| FEAT-008 | Recommended selection | A "Recommended" mode auto-selects a sensible minimal component set | https://www.techspot.com/downloads/7246-nvcleanstall.html |
| FEAT-009 | Dependency checking | Automatically verifies component dependencies (e.g. a component that needs another gets it pulled in / warned about) | https://www.techspot.com/downloads/7246-nvcleanstall.html |

### Installation tweaks

| ID | Feature | Behavior notes | Source |
|---|---|---|---|
| FEAT-010 | Disable installer telemetry & advertising | Strips telemetry/ads from the install process itself | GitHub shoober420 NVCleanstall.txt |
| FEAT-011 | Unattended express installation (auto-reboot option) | Installer runs with no prompts; optional automatic reboot | gist.github.com/leotm, techspot.com |
| FEAT-012 | Perform clean installation | Wipes previous driver traces before installing | techsuse.com, search summary |
| FEAT-013 | Disable Multiplayer/Multi-Plane Overlay (MPO) | Toggle recorded in user configs as an installation tweak | GitHub shoober420 NVCleanstall.txt |
| FEAT-014 | Disable Ansel | Disables NVIDIA's in-game screenshot tool | GitHub shoober420 NVCleanstall.txt, techspot.com |
| FEAT-015 | Install NVIDIA Control Panel | Option to include the Control Panel app (normally a Store app) | search summary (Techlore forum) |

### Expert tweaks

| ID | Feature | Behavior notes | Source |
|---|---|---|---|
| FEAT-016 | Show expert tweaks toggle | Expert section hidden by default behind a checkbox | gist.github.com/leotm |
| FEAT-017 | Disable driver telemetry (experimental) | Patches driver-level telemetry; flagged experimental, interacts with signature enforcement | GitHub shoober420 NVCleanstall.txt |
| FEAT-018 | Disable NVIDIA Container (experimental) | Prevents NVDisplay.Container LS service; warned that it breaks NVIDIA Control Panel | GitHub shoober420 NVCleanstall.txt |
| FEAT-019 | Disable HD Audio device sleep timer | Registry tweak stopping audio dropout from the HD audio device sleeping | GitHub shoober420 NVCleanstall.txt |
| FEAT-020 | Enable Message Signaled Interrupts (MSI mode) | Sets the GPU to MSI interrupts with configurable priority (default/high) and CPU affinity; links to Microsoft docs for each setting | search summary, gist.github.com/leotm |
| FEAT-021 | Disable HDCP | Turns off HDCP on the GPU outputs | GitHub shoober420 NVCleanstall.txt |
| FEAT-022 | Rebuild digital signature | After modifying driver files, re-signs the package so Windows accepts it; noted compatible with Easy Anti-Cheat; can auto-accept unsigned-driver warnings | GitHub shoober420 NVCleanstall.txt |
| FEAT-023 | Per-option explanations | Clicking any option shows an explanation panel describing what it does | search summary (TechPowerUp) |

### Install & packaging

| ID | Feature | Behavior notes | Source |
|---|---|---|---|
| FEAT-024 | Install now (GUI installer) | Runs the modified NVIDIA installer interactively | techmarsh.com |
| FEAT-025 | Silent install | Runs the modified installer with no UI | techmarsh.com |
| FEAT-026 | Extract only | Unpacks the customized package to a folder for manual/later deployment | techmarsh.com |
| FEAT-027 | Build self-contained installer EXE | Packages the customized driver + chosen options into a single redistributable EXE | search summary (TechPowerUp) |

### Presets & automation

| ID | Feature | Behavior notes | Source |
|---|---|---|---|
| FEAT-028 | Save/load settings preset | Saves the full selection (components + tweaks) to reuse on future driver updates | techmarsh.com, techspot.com |

## 3. Core flows

### F1 — Download-and-customize install (the primary flow)

1. Launch app → GPU and current driver are auto-detected.
2. Choose "Install latest driver" (or pick a specific version from the dropdown).
3. Driver package downloads with a progress bar.
4. Component checklist appears with the Recommended set pre-selected; user checks/unchecks modules (Display Driver locked on).
5. Tweaks page: installation tweaks and (optionally revealed) expert tweaks; each option shows an explanation when clicked.
6. Choose install action: GUI install / silent install / extract only / build package.
7. Package is rebuilt (signature rebuilt if modified), installation runs, user reboots if prompted.

### F2 — Customize an existing driver file (offline flow)

1. Launch app → choose "Use driver file on disk".
2. Browse to a downloaded NVIDIA package.
3. Continue through the same component → tweaks → install steps as F1.

### F3 — Pick a specific driver version

1. On the driver selection screen, open the version list (latest, older releases, beta vs WHQL).
2. Select a version; app fetches that package; continue as F1 step 4.

### F4 — Build a redistributable package

1. Complete F1/F2 through the tweaks step.
2. Choose "Build package" instead of installing.
3. App produces a self-contained installer EXE embedding the component/tweak choices.

### F5 — Preset-driven repeat update

1. User saves a preset after configuring components + tweaks.
2. On a later run, load the preset; selections apply automatically; user goes straight to install.

## 4. UI/UX

NVCleanstall is a **wizard**: a fixed-size window with a sequence of pages, Back/Next
navigation at the bottom, and a final action page. Windows-native look (WinForms-style).

### Screen 1 — Welcome / driver source

Shows detected GPU name and installed driver version at top. Radio choices: "Install latest
driver version" (with a version dropdown that lists newer/older/beta builds and marks WHQL),
and "Use driver files on disk" with a file browse field. Next button proceeds; choosing a web
driver goes to the download screen, a local file skips it.

### Screen 2 — Download progress

Progress bar with percent, speed, and the version being fetched. Automatically advances when
complete.

### Screen 3 — Components to install

Two-pane layout: left pane is a checkbox tree of driver components ("Display Driver
(required)" locked/checked; PhysX, HD Audio, USB-C, GeForce Experience, Telemetry, Shield,
Stereo 3D, notebook optimizations, container services…); right pane shows a description of
the highlighted component. A "Recommended" hint/preset marks the sensible defaults. Next
proceeds to tweaks.

### Screen 4 — Installation tweaks

Checkbox list: disable installer telemetry & advertising; unattended express installation
(sub-option: allow auto-reboot); perform clean installation; disable MPO; disable Ansel;
install NVIDIA Control Panel; "Show expert tweaks" reveals the expert set (driver telemetry,
NVIDIA Container, HD audio sleep timer, MSI mode with priority sub-setting, HDCP, rebuild
digital signature). Clicking any row shows its explanation in a side/detail panel, with
warnings on experimental items.

### Screen 5 — Install action & result

Buttons/choices: Install (GUI), Silent install, Extract only, Build package (choose output
path). During execution a log/progress view shows repackaging and installer steps; finish
page reports success and prompts for reboot when needed. Preset save/load is available from
the main menu/toolbar.

## 5. Implied data model

- **GpuInfo** — vendor/device id, name, laptop vs desktop, current driver version.
- **DriverRelease** — version, release date, channel (WHQL/beta), OS target, download URL, size. A catalog of these backs the version dropdown.
- **DriverPackage** — a downloaded/local archive; contains many **Component**s.
- **Component** — id, display name, description, size, required flag, default-selected flag, dependencies (component ids).
- **Tweak** — id, name, description, category (install/expert), experimental flag, parameters (e.g. MSI priority), warnings.
- **Preset** — named saved set of component selections + tweak settings, serialized to a file.
- **BuildJob / InstallJob** — source package + selections + action (install/silent/extract/package) + output path + log lines + status.

## 6. Non-obvious behaviors

- **Repackaging, not just flag-passing** (inferred): the tool physically removes component
  folders from the extracted driver package and edits `setup.cfg`/manifest before
  reinstalling — which is why a signature rebuild step exists.
- **Signature handling:** modifying driver files invalidates NVIDIA's signature; the
  "rebuild digital signature" tweak re-signs with a locally generated cert, and driver
  telemetry patching may require disabling Windows Driver Signature Enforcement. The tool
  warns and can auto-accept unsigned-driver prompts. [partly inferred]
- **Dependency rules:** GeForce Experience requires the telemetry/container services, so
  deselecting one affects the other (dependency checker exists per TechSpot). [inferred detail]
- **MSI mode** is a registry edit under the GPU's PCI device key (`MSISupported=1`,
  priority), applied post-install — with documented risk links. [inferred mechanism]
- **Disable NVIDIA Container breaks the Control Panel** — the app surfaces this as an
  explicit warning rather than preventing it.
- **Recommended preset** unchecks GeForce Experience, telemetry, USB-C, Stereo 3D and keeps
  Display Driver (+ PhysX, HD Audio commonly). [inferred exact set]

## 7. Research boundary

Not researched: the exact NVIDIA download-catalog API NVCleanstall queries; the precise
on-disk layout of NVIDIA packages and `setup.cfg` schema; the full text of every option
explanation; laptop/OEM-specific driver handling; the "hardware support added" feature for
unsupported GPUs (mentioned in changelogs but not detailed in consulted sources); exact
Recommended component set. techpowerup.com and elevenforum.com returned HTTP 403, so
first-party pages were only seen via search summaries.

## Sources consulted

- https://www.techpowerup.com/nvcleanstall/ (403 — content via search summary)
- https://techmarsh.com/how-nvcleanstall-customizes-nvidia-drivers-for-performance-and-precision/
- https://techsuse.com/nvcleanstall-removes-bloatware-from-nvidia-drivers-heres-how-it-works/
- https://www.techspot.com/downloads/7246-nvcleanstall.html
- https://github.com/shoober420/windows11-scripts/blob/main/NVCleanstall.txt
- https://gist.github.com/leotm/da98d29d98ddaf94c58821f92bf47f21
- WebSearch result summaries (TechPowerUp download page, Techlore forum)
