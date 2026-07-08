# CleanDriver — a functional clone of NVCleanstall

CleanDriver is a Windows 11 desktop wizard that reproduces NVCleanstall's driver-customization
flow: it detects your GPU, lets you pick a driver version from a catalog, uncheck the
components you don't want (GeForce Experience, telemetry, USB-C, …), apply installation and
expert tweaks (clean install, MSI mode, HDCP, etc.), then install, extract, or build a
customized package. The UI is a WebView2 window over an in-process ASP.NET Core (Kestrel)
server; the whole thing ships as one self-contained `CleanDriver.exe` — end users install no
runtime.

**Safety boundary:** CleanDriver never downloads, modifies, or installs real NVIDIA driver
packages and never writes to the live registry. It runs against a bundled **mock** driver
catalog and package format, and every "install"/"tweak" produces a real, inspectable
artifact (a customized package folder, an install-receipt JSON, `.reg` snippets it writes but
does **not** apply). It is a faithful clone of the *interaction design*, not a driver tool you
should point at your real GPU. Original branding only — no NVIDIA/TechPowerUp trademarks,
logos, or code; NVIDIA product names appear only as nominative labels in mock data.

Built from the spec at `specs/nvcleanstall/spec.md`; verification status in
`specs/nvcleanstall/parity.md`.

## Cloned / simplified / omitted

| Cloned (parity) | Simplified (and how) | Omitted |
|---|---|---|
| Version picker (WHQL/beta/older), component checklist with locked Display Driver, Recommended preset, dependency auto-select/warn, all installation + expert tweaks with per-option descriptions and warnings, container↔control-panel conflict, MSI priority, extract-only, save/load presets, per-tweak `.reg` emission | GPU detection (real via WMI, else simulated), driver "download" (progress simulation over a mock catalog — no NVIDIA servers), install/silent (simulated: log + receipt, nothing installed), driver-telemetry patch & signature rebuild (marked in output, not real), build-package (produces a package *directory* with install script, not a single EXE) | Nothing from the inventory is silently dropped; there is no `out` feature. Real driver download/modify/install is deliberately out of scope by the safety boundary. |

## Setup

Prerequisites: **.NET 10 SDK** (to build) — end users of the published exe need nothing.

```
cd nvcleanstall
dotnet build
```

## Run

Development (native window):

```
dotnet run
```

A 960×640 CleanDriver window opens on the wizard's first screen (detected GPU + version
picker). Headless / verification mode (server only, no window):

```
dotnet run -- --headless
# → "CleanDriver running at http://localhost:4780"
# open that URL in a browser, or curl the API
```

Ship a standalone single-file exe (no runtime required on the target machine):

```
dotnet publish -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
# → publish/CleanDriver.exe  (~139 MB, double-click to run)
```

A successful start prints the listening URL; the window (or browser) shows "Driver source"
as Step 1 of 5 with your GPU detected at the top.

## Sample data

Seeded under `data/`: a catalog of five driver releases (572.16 WHQL latest … 566.36),
each with a full component manifest and placeholder payload files. `566.36` intentionally
omits the USB-C component so the version picker visibly changes the component list. Outputs
land in `output/` (receipts, extracted/built packages) and `presets/` (saved presets) beside
the exe. Use **Browse…** on the "Use driver files on disk" option to auto-fill the bundled
`data/packages/571.96` folder for the offline flow.
