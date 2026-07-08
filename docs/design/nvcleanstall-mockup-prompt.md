# CleanDriver (NVCleanstall clone) — design-session prompt

## Mockup review — verdict and rulings (2026-07-08)

Export reviewed: `docs/design/CleanDriver GPU driver wizard/` (5 files, full-file
subagent review against the frozen inventory). **Verdict: approve-with-nits.** All
inventory items present on every screen; design tokens, chrome, and sample data
verbatim-correct; no unspecced screens, dialogs, or animations.

Rulings (frozen spec from this point; later phases cite R-numbers):

- **R1** `accepted-into-spec` — Delivery path is `docs/design/CleanDriver GPU driver wizard/` (not D5's `docs/design/mockups/`). D5 amended; file names stand.
- **R2** `accepted-into-spec` — Layout-split text formats adopted: screen 2 caption as two justified spans ("512 MB of 812 MB" | "41.2 MB/s"); screen 3 total as label/value ("Install size" | "682 MB of 832 MB").
- **R3** `rejected-with-replacement` — Screen 2 shows both an enabled Back and Cancel. Replacement: Back is hidden during download; Cancel is the sole return path to screen 1.
- **R4** `accepted-into-spec` — Screen 5 Frame B hides footer Back/Start after completion; "Open output folder" and "Finish" are the exits.
- **R5** `fix-at-implementation` — Locked "Display Driver" checkbox is opacity-styled in the mockup; implementation uses a real disabled checkbox (semantic + keyboard-correct).
- **R6** `accepted-into-spec` — Page-title headings on screens 1/3/4/5A kept; screen 2's in-card heading serves as its title (no h1 added).
- **R7** `accepted-into-spec` — Cosmetic inventions kept: detection-card GPU icon, download icon, "Select components" label, "Component"/"Tweak" eyebrows, "Output folder" field label. Frame A/B meta-labels are mockup-only and are NOT implemented.
- **R8** `accepted-into-spec` — Derived tokens ratified: dark accent-hover `#7BC97F`; result-banner tokens light `#EAF3EA`/`#2E7D32`, dark `#22301F`/`#66BB6A`.
- **R9** `accepted-into-spec` — 9-line install log (3 plausible lines beyond the six required beats) adopted as the reference log shape.
- **R10** `accepted-into-spec` (maintainer veto invited) — NVIDIA component names in mock data ("GeForce Experience", "PhysX", "Ansel", "Shield Controller") are nominative references to what real driver packages contain, same carve-out as GPU model strings. App branding remains CleanDriver-only; no NVIDIA logos or trade dress anywhere.

Handoff: implementation (clone Phase 4) builds `public/` directly from these mockups
plus rulings R3/R5; the design session is DONE — no further screens are needed.


Generated 2026-07-08 by the architect session (delegate `design` phase, sub-mode a).
Source of truth: `specs/nvcleanstall/spec.md` §4 (screens) + §1/§2 (frozen option
catalogs). Spec status at generation time: **draft** — if the scope-cut checkpoint
changes the spec, regenerate this prompt.

## Architect design decisions (maintainer veto applies)

No stylesheet exists yet (greenfield), so tokens below are proposed, not quoted from
code. Once the maintainer approves, they become the project's design tokens and Phase 4
implements against them.

- **D1 — Brand:** app is named **CleanDriver** (original name; IP boundary forbids
  NVCleanstall/NVIDIA branding).
- **D2 — Visual language:** Windows 11 Fluent — Segoe UI Variable, layered
  Mica-style background, 8 px card radius — own design "in the spirit of" the
  original wizard, per `fidelity: functional`.
- **D3 — Accent:** green family (#2E7D32 light / #66BB6A dark) as a nod to the GPU-tool
  heritage without NVIDIA's trademark green.
- **D4 — Canvas:** fixed 960 × 640 wizard window.
- **D5 — Deliverable:** five single-file static HTML mockups with an in-page
  light/dark toggle, dropped at `docs/design/mockups/mockup-screen-0N-<name>.html`.

## Paste-ready prompt for Claude Design

The block below is the complete, self-contained prompt. Paste it into a Claude Design
session verbatim.

```text
You are designing high-fidelity mockups for CleanDriver, a Windows 11 desktop utility
that lets users install a customized GPU driver package: pick a driver version,
uncheck unwanted components, apply optional tweaks, then install or build a package.
It is a five-screen wizard. Design all five screens, one at a time, in order, and
keep them visually coherent — screen N renders in the same window chrome as screen 1.

DELIVERABLE (per screen)
- One self-contained static HTML file (inline CSS, no external assets, no JS
  frameworks; a small inline script for the theme toggle is fine).
- File names, in order: mockup-screen-01-driver-source.html,
  mockup-screen-02-download.html, mockup-screen-03-components.html,
  mockup-screen-04-tweaks.html, mockup-screen-05-action-result.html.
- Each file shows the full 960×640 window on a neutral page background, plus a
  light/dark toggle above the window that switches the mockup's theme.
- Screen 5 shows BOTH of its states (action-choice and running/result) as two
  stacked 960×640 frames in the same file.

HARD CONSTRAINTS
- Platform: Windows 11 desktop app. Fixed window 960×640, not resizable, not
  responsive — design exactly one desktop layout.
- Window chrome on every screen: title bar with app glyph + "CleanDriver", and a
  right-aligned toolbar with two small buttons: "Save preset…" and "Load preset…";
  standard minimize/close caption buttons.
- Wizard footer on every screen: step indicator "Step N of 5 — <screen name>" on
  the left; "Back" and "Next" buttons on the right (screen-specific labels noted
  per screen below). Back is hidden on screen 1.
- No NVIDIA, GeForce, or TechPowerUp logos, wordmarks, or trade dress anywhere.
  GPU model strings in sample data are allowed (they are user data).
- Every control on these screens comes from the catalogs below. This is the
  complete catalog — do not invent additional components, tweaks, buttons,
  screens, dialogs, or settings.

DESIGN TOKENS (use exactly; define once as CSS variables)
- Font: "Segoe UI Variable", "Segoe UI", system-ui. Body 14px/20px; page title
  20px semibold; section labels 12px uppercase +0.04em tracking; captions 12px.
- Light theme: window background #F3F3F3; content cards #FFFFFF with 1px #E5E5E5
  border; primary text #1B1B1B; secondary text #5D5D5D; accent #2E7D32; accent
  hover #276B2B; on-accent text #FFFFFF.
- Dark theme: window background #202020; cards #2B2B2B with 1px #3A3A3A border;
  primary text #FFFFFF; secondary text #C5C5C5; accent #66BB6A; on-accent #103312.
- Warning (both themes): amber — light #9A6700 text on #FFF8E1; dark #F8D57E text
  on #3A3115. "Experimental" badge: warning colors, 11px, pill.
- Radii: 8px cards, 4px buttons/inputs/checkboxes. Spacing scale 4/8/12/16/24px.
- Primary buttons: accent fill; secondary: card background with border. Disabled
  controls at 40% opacity. Focus: 2px accent outline.

SAMPLE DATA (use verbatim so the five screens tell one story)
- Detected GPU: "GeForce RTX 4070" — installed driver "570.86".
- Driver versions for the dropdown (version — channel — date):
  572.16 — WHQL — 2026-06-24 (marked "latest")
  571.96 — WHQL — 2026-05-30
  571.59 — Beta — 2026-05-12
  570.86 — WHQL — 2026-04-08 (marked "installed")
  566.36 — WHQL — 2026-02-17
- Selected version everywhere downstream: 572.16 WHQL, package size 812 MB.

SCREEN 1 — Driver source (mockup-screen-01-driver-source.html)
Frozen inventory:
1. Detection card at top: GPU name, installed driver version, small "detected via
   system query" caption.
2. Radio option A (selected): "Install a driver from the catalog" with the version
   dropdown (shows the 5 sample versions; channel shown as a small WHQL/Beta tag;
   latest and installed annotated).
3. Radio option B: "Use driver files on disk" with a disabled-until-selected path
   field and "Browse…" button.
4. Footer: Step 1 of 5 — Driver source; primary button "Next".
Nothing else.

SCREEN 2 — Download (mockup-screen-02-download.html)
Frozen inventory:
1. Heading: "Downloading driver package" with subtitle "572.16 WHQL — 812 MB".
2. Large progress bar at 63%, caption row: "512 MB of 812 MB — 41.2 MB/s".
3. Secondary button "Cancel" (returns to screen 1 — no dialog).
4. Footer: Step 2 of 5 — Download; Next disabled with caption "continues
   automatically when complete".
Nothing else.

SCREEN 3 — Components (mockup-screen-03-components.html)
Two-pane card: left = component checklist, right = description panel for the
highlighted row. Frozen component catalog (name — size — checked state in mockup):
1. Display Driver — 623 MB — checked, locked (checkbox disabled, "(required)")
2. PhysX System Software — 38 MB — checked
3. HD Audio Driver — 21 MB — checked
4. USB-C / VirtualLink Driver — 9 MB — unchecked
5. GeForce Experience — 87 MB — unchecked
6. Telemetry Services — 12 MB — unchecked
7. Shield Controller Support — 6 MB — unchecked
8. Stereo 3D Support — 4 MB — unchecked
9. Notebook Optimizations — 8 MB — unchecked
10. Container Runtime Services — 24 MB — unchecked
Also on this screen, exactly:
- Secondary button "Recommended" above the list (applies items 1–3 only).
- Highlighted row in the mockup: "GeForce Experience"; right panel shows its
  description and a dependency note: "Requires: Container Runtime Services,
  Telemetry Services — these will be selected automatically."
- Running total under the list: "Install size: 682 MB of 832 MB".
- Footer: Step 3 of 5 — Components; buttons "Back" and "Next".
Nothing else.

SCREEN 4 — Tweaks (mockup-screen-04-tweaks.html)
Two-pane card like screen 3: left = tweak checkboxes in two sections, right =
description panel. Frozen tweak catalog.
Section "Installation tweaks":
1. Disable installer telemetry & advertising — checked
2. Unattended express installation — checked, with indented sub-checkbox "Allow
   automatic reboot" — unchecked
3. Perform clean installation — checked
4. Disable Multi-Plane Overlay (MPO) — unchecked
5. Disable Ansel — unchecked
6. Install driver control panel — checked
Toggle row: "Show expert tweaks" — ON in the mockup, revealing section "Expert
tweaks" (each row carries an "Experimental" badge where noted):
7. Disable driver telemetry — unchecked — Experimental badge
8. Disable display container service — CHECKED — Experimental badge; because it
   conflicts with item 6, show an inline warning banner under it: "Breaks the
   driver control panel — it is currently selected for install."
9. Disable HD-audio device sleep timer — unchecked
10. Enable Message Signaled Interrupts (MSI) — checked, with indented sub-control
    "Interrupt priority:" segmented Default | High, High selected
11. Disable HDCP — unchecked
Right panel in the mockup shows item 10's description with a "Learn more"
text link.
Footer: Step 4 of 5 — Tweaks; buttons "Back" and "Next".
Nothing else.

SCREEN 5 — Action & result (mockup-screen-05-action-result.html), two frames.
Frame A "Choose action" — frozen inventory:
1. Four selectable action cards in a 2×2 grid, each icon + title + one-line
   description: "Install" (interactive install with progress), "Silent install"
   (no prompts, log only), "Extract only" (write customized package to a folder),
   "Build package" (self-contained installer package). "Install" selected.
2. Output path row (enabled only for Extract/Build; shown disabled in Frame A):
   path field + "Browse…".
3. Footer: Step 5 of 5 — Action; buttons "Back" and primary "Start".
Frame B "Running / result" — frozen inventory:
1. Progress header: "Installing customized driver package…" with step progress
   bar at 100% and status "Completed".
2. Scrolling log panel (monospace 12px) with ~8 sample lines: removing previous
   driver traces, copying Display Driver payload, skipping deselected components,
   "Rebuilding digital signature… done", writing install receipt, finished.
3. Result banner (accent): "Installation complete — receipt written to
   output\receipt-2026-07-08.json", with buttons "Open output folder" and
   "Finish", and caption "A reboot is recommended."
Nothing else.

PROCESS
- Produce the five files in order; after each, wait for my go-ahead.
- Keep tokens and chrome identical across files; screen 1 sets the reference.
- STOP after screen 5. Do not design additional screens, empty states, dialogs,
  or a settings page — they are out of scope.
```
