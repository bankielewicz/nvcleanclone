---
target: "NVCleanstall"
slug: nvcleanstall
spec: spec.md
---

# NVCleanstall — Build Progress

| Milestone | Delivers | Status | Verified how |
|---|---|---|---|
| M1 — Walking skeleton: full F1/F3 wizard happy path (catalog → download → components → tweaks → simulated install) | AC-001, AC-003 | done | curl API run-through + Playwright UI walk (screens 1–5, dependency logic, conflict warning, receipt file inspected); real GPU (RTX 5070) detected via WMI |
| M2 — Offline flow: local package load + extract-only | AC-002 | done | Loaded bundled 571.96 folder offline (no download step), extract-only dropped USB-C + Stereo 3D — verified absent from output payload/ and rewritten manifest |
| M3 — Packaging: build-package output, .reg artifacts, signature rebuild | AC-004 | done | Built package with MSI(High)+HDCP; output has payload, install.cmd, config.json, tweak-msi-mode.reg (DevicePriority=3), tweak-disable-hdcp.reg, manifest signature:rebuilt |
| M4 — Selection intelligence + presets: Recommended, dependencies, expert reveal/conflicts, preset save/load | AC-005 | done | Playwright: Recommended + dependency auto-select/deselect + container↔control-panel conflict warning; saved preset "lean", killed & relaunched the process, loaded it — components + tweaks + expert-reveal all restored |
| M5 — Distribution: self-contained single-file publish + WebView2 shell | (stack pin, no ACs) | done | `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true` → single 139 MB CleanDriver.exe; ran standalone (no runtime install), served + detected GPU. WebView2 window opening = manual check |

Verification: complete — see parity.md
