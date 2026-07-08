# nvcleanclone — CleanDriver

**CleanDriver** is a functional clone of TechPowerUp's NVCleanstall, built for Windows 11:
a five-screen wizard that detects your GPU, lets you pick a driver version from a catalog,
uncheck the components you don't want (companion app, telemetry, USB-C, …), apply
installation and expert tweaks (clean install, MSI mode, HDCP, …), then install, extract,
or build a customized package. It ships as a single self-contained `CleanDriver.exe` —
no runtime to install.

It is a clone of the **interaction design**, not a driver tool: everything driver-touching
runs against a bundled mock catalog/package format and produces real, inspectable artifacts
(customized package folders, install receipts, `.reg` snippets that are written but never
applied). See the safety boundary below.

## Quick start

Requires the Windows .NET 10 SDK (end users of a published exe need nothing).

```bash
dotnet run --project nvcleanstall                 # native WebView2 window
dotnet run --project nvcleanstall -- --headless   # server only → http://localhost:4780
```

Publish one redistributable file:

```bash
dotnet publish nvcleanstall/CleanDriver.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o nvcleanstall/publish
```

Details, sample data, and run expectations: [`nvcleanstall/README.md`](nvcleanstall/README.md).

## Repository map

| Path | What it is |
|---|---|
| `nvcleanstall/` | The app: C# backend (`Lib/`, `Api.cs`, `Program.cs`), wizard frontend (`wwwroot/`), mock driver data (`data/`) |
| `specs/nvcleanstall/` | The build record: `research.md` → `spec.md` (scope, FRs, ACs, stack pin) → `progress.md` (milestones) → `parity.md` (verified feature-parity vs. the original) |
| `docs/design/` | The frozen UI contract: five high-fidelity mockups + the design prompt with numbered rulings R1–R10 |
| `docs/gaps_analysis.md` | The active gap register — the closed task list toward real-driver parity (introduced in PR #1) |
| `prompts/` | Kickoff prompts for cold implementation sessions, one gap per session |
| `CONTRIBUTING.md` | Mandatory process: TDD, gates, worktree/branch/PR workflow, project invariants |
| `CLAUDE.md` | Working notes for Claude Code sessions (architecture, commands, environment quirks) |

## Status

All 5 acceptance criteria pass, verified end-to-end against the running app (API + browser
automation) — see [`specs/nvcleanstall/parity.md`](specs/nvcleanstall/parity.md): 18 of 28
inventoried features at full parity, 10 simplified (the driver-touching ones, simulated by
design), none omitted. The path from "simulated" to "real" (live NVIDIA version lookup,
real installer download, …) is specified gap-by-gap in `docs/gaps_analysis.md`.

## Hard boundaries

- **Safety:** the app never executes downloaded installers, never installs drivers, and
  never writes the live registry. Individual gaps in the register may lift a *named* part
  of this (e.g. downloading real installer bytes to disk — still never executed); nothing
  else does.
- **IP:** original implementation and branding only ("CleanDriver"). No NVIDIA/TechPowerUp
  logos, trademarks, or copied assets; NVIDIA product names appear only as nominative
  labels for what real driver packages contain.

## Contributing

One slice = one worktree = one branch = one PR; TDD without exception; the owner merges
every PR. Full rules in [`CONTRIBUTING.md`](CONTRIBUTING.md).
