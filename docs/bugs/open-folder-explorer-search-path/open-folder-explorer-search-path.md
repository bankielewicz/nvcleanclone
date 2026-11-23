# `/api/open-folder` starts Explorer by bare name: an `explorer.exe` beside CleanDriver.exe (or in its working directory) runs instead

| Field | Value |
|---|---|
| **Slug** | `open-folder-explorer-search-path` |
| **Severity** | Medium |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness: a planted `explorer` in the working directory was executed by `/api/open-folder`); also confirmed by live repro. The Windows `CreateProcess` search order is documented behavior, reasoned here and not dynamically exercised on Windows. |
| **Area** | `nvcleanstall/Api.cs` (`POST /api/open-folder`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Windows in production. The mechanism is the same on Unix, where .NET deliberately mirrors the Windows search order, and that is how it was reproduced. |
| **Related register entries** | Same class as **SEC-06** (process launched by a search-path-resolved name), but SEC-06 / **GAP-C04** pin only `powershell` in `Gpu.cs`; GAP-C04's fix never touches `Api.cs`. `NoExecutionGuardTests.cs:14-16` explicitly blesses this call ("Api.cs uses Process.Start only to open a *directory* in Explorer"). The guard checks *what is passed*, not *which binary runs*. Reachability from other local processes widens with **SEC-02**. |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R04** — App host and process launch (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`POST /api/open-folder` runs `Process.Start(new ProcessStartInfo("explorer", dir) { UseShellExecute = false })`. With `UseShellExecute = false`, .NET hands Windows `CreateProcess` a null application name and a command line that starts with `"explorer"`. `CreateProcess` then searches, in order:
1. the directory the application loaded from;
2. the current directory;
3. System32, then System;
4. the Windows directory, which is where `explorer.exe` actually lives;
5. `PATH`.

For the portable single-exe CleanDriver, steps 1 and 2 are both the folder holding `CleanDriver.exe`, usually Downloads. So any `explorer.exe` in that folder is executed with the user's rights every time the user clicks **Open folder** on the result screen. The same unquoted call also passes the directory raw, and Explorer splits its command line on commas.

## Evidence
- `nvcleanstall/Api.cs:142-151`:
  ```csharp
  app.MapPost("/api/open-folder", (OpenFolderRequest req) =>
  {
      var dir = req.Path != null && Directory.Exists(req.Path) ? req.Path : Jobs.OutputDir;
      try
      {
          Process.Start(new ProcessStartInfo("explorer", dir.Replace('/', '\\')) { UseShellExecute = false });
      }
      catch { /* best effort — headless/CI environments have no shell */ }
      return Results.Json(new { opened = dir }, Json.Web);
  });
  ```
- How .NET launches it:
  - On Windows, `UseShellExecute = false` goes through `CreateProcess(lpApplicationName: null, lpCommandLine: "\"explorer\" <dir>")`. `CreateProcess`'s documented search order for an unqualified module name puts the application directory and the current directory before `%SystemRoot%` (`C:\Windows\explorer.exe`), and it appends `.exe`.
  - On Unix, .NET's `Process` resolves a bare name as the exact path, then the executable's directory, then the current directory, then `PATH`. Its source comments say it follows Windows `CreateProcess`.
- `nvcleanstall/wwwroot/app.js:439` wires the result banner's **Open folder** button to this route: `api.post('/api/open-folder', { path: j.outDir || null })`.
- For contrast, `nvcleanstall/Lib/Gpu.cs:109-113` (`FileName = "powershell"`) is SEC-06. GAP-C04's fix there resolves the canonical `%SystemRoot%` path and is scoped to that one call.
- A second defect on the same line, reasoned and not reproduced. The directory is concatenated unquoted into `Arguments`. Explorer parses its command line as comma-separated switches (`/root,`, `/select,`, …), so a folder such as `C:\Builds\v1,old` is opened as `C:\Builds\v1` (or the default folder). The fix below quotes the argument.

The code assumes that "explorer" means `%SystemRoot%\explorer.exe`. With a bare name it means "the first `explorer.exe` along a search path whose first two entries are user-writable folders".

## Impact
- **Arbitrary code execution as the user**, when an `explorer.exe` is present in the folder that holds `CleanDriver.exe`, or in the process's working directory, and the user clicks **Open folder**. The working directory is the same folder when the exe is double-clicked or started via `dotnet run` from a checkout. The expected deployment of the portable single exe is the user's Downloads folder, where browsers save files. That is the classic binary-planting setup, the same one SEC-06 was graded for.
- A launch through `CreateProcess` does not pass through the shell's Mark-of-the-Web / SmartScreen prompt the way a double-click does. Smart App Control, where enabled, may still block an unsigned binary.
- Any local process (SEC-02) can trigger the launch through `POST /api/open-folder` without the user clicking anything.
- **Severity.** Medium, matching SEC-06: code execution, but with a planting precondition.

## How to reproduce
### A. Automated — failing xunit test
Add as `nvcleanstall/CleanDriver.Tests/OpenFolderLaunchTests.cs`, and copy `Api.cs` into the test output the same way `Jobs.cs` and `Packages.cs` already are. In `CleanDriver.Tests.csproj`, add `<Content Include="..\Api.cs" Link="src\Api.cs" CopyToOutputDirectory="PreserveNewest" />`.
- Test (a) is a shape guard in the `NoExecutionGuardTests` idiom. It is red on Windows CI at HEAD.
- Test (b) is behavioral. It needs a benign planted executable, so it runs on non-Windows hosts; the Linux harness is the red evidence.

```csharp
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CleanDriver.Tests;

public class OpenFolderLaunchTests
{
    private static async Task<(WebApplication app, HttpClient http)> StartApiAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        Api.Map(app, new MockCatalogProvider());
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    // (a) No process in Api.cs is started by a bare, search-path-resolved file name.
    [Fact]
    public void Api_NeverStartsAProcessByBareName()
    {
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "src", "Api.cs"));
        var bare = Regex.Matches(src, "ProcessStartInfo\\(\\s*\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value)
            .Where(name => !Path.IsPathFullyQualified(name))
            .ToList();
        Assert.Empty(bare);
    }

    // (b) A planted `explorer` in the app/working directory must NOT run. (The test host's
    // working directory is its output directory, as CleanDriver.exe's is when double-clicked.)
    [Fact]
    public async Task OpenFolder_DoesNotRunAnExplorerPlantedInTheAppOrWorkingDirectory()
    {
        if (OperatingSystem.IsWindows()) return; // covered by (a) + manual evidence on Windows

        var cwd = Directory.GetCurrentDirectory();
        var planted = Path.Combine(cwd, "explorer");
        var marker = Path.Combine(TempDir.Create(), "planted-ran.txt");
        File.WriteAllText(planted, $"#!/bin/sh\necho \"$@\" > '{marker}'\n");
        File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var (app, http) = await StartApiAsync();
            await using var host = app;

            var resp = await http.PostAsync("/api/open-folder",
                new StringContent("{\"path\":null}", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            await Task.Delay(1000); // let a spawned process (if any) write its marker
            Assert.False(File.Exists(marker), "the planted 'explorer' beside the app was executed");
        }
        finally
        {
            File.Delete(planted); // one named file
        }
    }
}
```

Command:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~OpenFolderLaunchTests"
```

RED, Linux net10.0 harness output (class named `OpenFolderExplorerSearchPathRepro` there):
```
  Failed CleanDriver.Tests.OpenFolderExplorerSearchPathRepro.OpenFolder_DoesNotRunAnExplorerPlantedInTheAppOrWorkingDirectory [1 s]
  Error Message:
   the planted 'explorer' beside the app was executed
  Failed CleanDriver.Tests.OpenFolderExplorerSearchPathRepro.Api_NeverStartsAProcessByBareName [6 ms]
  Error Message:
   Assert.Empty() Failure: Collection was not empty
Collection: ["explorer"]
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 1 s - Harness.Tests.dll (net10.0)
```

### B. Live — running app
**Linux harness, captured.** The server was started from its own folder (the CWD), and a harmless marker-writing script named `explorer` was planted there:
```
$ cat > <server dir>/explorer <<EOF
#!/bin/sh
echo "planted explorer ran: pid=\$\$ args=\$*" >> <scratch>/planted-explorer-ran.txt
EOF
$ chmod +x <server dir>/explorer
$ curl -s -X POST http://127.0.0.1:4803/api/open-folder -H "Content-Type: application/json" -d '{"path":null}'
{
  "opened": "<scratch>/server/out/output"
}
$ cat <scratch>/planted-explorer-ran.txt
planted explorer ran: pid=11457 args=	mp
```
The argument is the route's `dir.Replace('/', '\\')` value, `\tmp\claude-0\…`. The script's `/bin/sh` `echo` interprets `\t` as a TAB and `\c` as "stop output", so the logged argument is cut off after `mp`.
Expected: the system file manager, or nothing on a headless box. Actual: the planted binary ran, with the output directory as its argument. The marker file was then deleted by name.

**Windows, owner-run evidence (not run here):**
1. `dotnet publish nvcleanstall/CleanDriver.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o %TEMP%\cd-plant`.
2. Copy a benign, recognizable executable into that folder as `explorer.exe`, for example `copy %SystemRoot%\System32\notepad.exe %TEMP%\cd-plant\explorer.exe`.
3. Double-click `%TEMP%\cd-plant\CleanDriver.exe`, which sets the CWD to that folder. Run Screen 1 → … → Extract only, then click **Open folder**. Alternatively, start the exe with `--headless --mock-catalog` and run `curl -s -X POST localhost:4780/api/open-folder -H "Content-Type: application/json" -d "{\"path\":null}"`.
4. Expected: File Explorer opens `output\`. Actual on HEAD, by the documented `CreateProcess` order: **Notepad** opens, launched by CleanDriver with the folder path as its argument.
5. Delete `explorer.exe` from the folder by name.

## Root cause
A process is launched by an unqualified name, with `UseShellExecute = false`. Name resolution is therefore delegated to `CreateProcess`'s search order, which consults the application directory and the current directory first. Neither is a trusted location for a portable executable. Both come before every system directory, including `%SystemRoot%`, where the real `explorer.exe` lives.

## Remedy
Launch `%SystemRoot%\explorer.exe` by absolute path, never by bare name. Quote the single folder argument while at it, which fixes the comma split. Do nothing when the canonical Explorer is absent, which is the existing "best effort" contract for headless and CI. This was validated in a scratch copy of `Api.cs`: both tests above went green, along with the full suite (151) and all repros.

```diff
         app.MapPost("/api/open-folder", (OpenFolderRequest req) =>
         {
             var dir = req.Path != null && Directory.Exists(req.Path) ? req.Path : Jobs.OutputDir;
-            try
-            {
-                Process.Start(new ProcessStartInfo("explorer", dir.Replace('/', '\\')) { UseShellExecute = false });
-            }
-            catch { /* best effort — headless/CI environments have no shell */ }
+            // Launch %SystemRoot%\explorer.exe by absolute path: a bare "explorer" is resolved
+            // by CreateProcess from the app's own folder and the working directory FIRST.
+            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
+            var explorer = string.IsNullOrEmpty(windir) ? null : Path.Combine(windir, "explorer.exe");
+            if (explorer != null && Path.IsPathFullyQualified(explorer) && File.Exists(explorer))
+            {
+                try
+                {
+                    // Quoted: Explorer splits an unquoted argument on commas. '"' cannot occur
+                    // in an existing Windows directory name.
+                    Process.Start(new ProcessStartInfo(explorer, $"\"{dir.Replace('/', '\\')}\"") { UseShellExecute = false });
+                }
+                catch { /* best effort — headless/CI environments have no shell */ }
+            }
             return Results.Json(new { opened = dir }, Json.Web);
         });
```

- **Why this is minimal.** One call site. The same resolution GAP-C04 prescribes for powershell (canonical `%SystemRoot%` path; do not search the app, current or PATH directories). The response shape is unchanged.
- **Alternatives rejected.**
  - `UseShellExecute = true` with the folder as `FileName`: avoids the search path, but routes through per-user (HKCU-writable) folder-verb registrations and differs from GAP-C04's chosen pattern.
  - `ArgumentList.Add(dir)`: .NET quotes only arguments that contain whitespace or quotes, not commas, so it leaves the comma split.
  - Setting `NoDefaultCurrentDirectoryInExePath`: this removes only the CWD step, not the application-directory step.
- **Fences.**
  - The safety boundary is untouched. Still only a folder is opened, and the downloaded installer is never passed here, which the `NoExecutionGuardTests` comment stays true to.
  - No UI change: the same **Open folder** button.
  - SEC-02's caller authentication is out of scope for this slice.
- **Back-compat pins.** `NoExecutionGuardTests` is unaffected; it scans `Jobs.cs` only.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** `docs/security_register.md` is active. The natural home is **GAP-C04** ("Remove PATH-based PowerShell execution"), widened by the owner to "remove every search-path-resolved process launch (`Gpu.cs` powershell, `Api.cs` explorer)". That needs an owner-pinned amendment. Otherwise it needs a new SEC-ID, because §7 says a new finding is never silently folded into an unrelated PR.
   > *Pinned 2026-11-23 as **GAP-R04** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied, and any ID suggested in this step is superseded.*
1. **Slice setup:** `git worktree add ../nvcleanclone-explorer -b feat/open-folder-absolute-explorer origin/main`.
2. **Red.** Add §A at `nvcleanstall/CleanDriver.Tests/OpenFolderLaunchTests.cs` plus the `src\Api.cs` content link in `CleanDriver.Tests.csproj`. Run the filter on Windows: (a) fails with `Collection: ["explorer"]`. Attach the Linux (b) red from §A and the Windows notepad evidence from §B as additional red. Commit `"open-folder-explorer-search-path (red): Api.cs never launches a process by bare name"`.
3. **Green.** Apply the diff and run the FULL suite. Commit `"open-folder-explorer-search-path: launch %SystemRoot%\\explorer.exe by absolute path, quoted"`.
4. **Refactor**, with the suite green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise** on Windows:
   - Rerun §B's Windows steps 1–4: File Explorer opens `output\`, not Notepad.
   - Create `C:\Temp\cd,comma\`, run an extract with `outputPath` `C:/Temp/cd,comma`, and click **Open folder**: that exact folder opens.
   - `curl -s -X POST localhost:4780/api/open-folder -H "Content-Type: application/json" -d "{\"path\":null}"` still returns `{"opened": "...\\output"}`.
7. **PR.** Fill the template:
   - TDD rows: bare-name guard; planted binary not run.
   - Edited-test disclosure: none (the csproj content link is a test-project addition).
   - Deviations: the guard test is textual by design, in the GAP-02 AC-3 idiom.
   - Live evidence.

   Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green.
2. **Isolated worktree:** `git worktree add ../nvcleanclone-review-explorer origin/feat/open-folder-absolute-explorer`. The range is `origin/main...HEAD`.
3. **Re-run the gates**, then run the Windows planted-notepad exercise yourself. Do not accept the builder's screenshot as proof (Invariant 2).
4. **Finder pass:**
   - Is the path built from `SpecialFolder.Windows` (not `%windir%` via an env-var lookup, which the process environment can set)?
   - Is `File.Exists` checked before launch?
   - Is there no remaining `ProcessStartInfo("…")` with a bare name anywhere in `Api.cs` (or a `FileName = "…"` form that the regex in (a) would miss)? Extend the guard if so.
   - Is the argument quoted exactly once?
   - Is `UseShellExecute` still `false`?
5. **Adversarial verify:**
   - Plant `explorer.exe` and `explorer.com` in the app folder and in the CWD.
   - Put a folder with a planted `explorer.exe` first on `PATH`.
   - Use a folder name with a comma, one with spaces, one with a trailing backslash, and a drive root (`C:/`).
   - Send `"path":"C:/Windows/System32/notepad.exe"`, a file not a directory: `Directory.Exists` is false, so the default `output\` opens.

   Only `C:\Windows\explorer.exe` may ever start.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with findings marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Fix loop C0–C7: Orient (fence: SEC-06 class, GAP-C04 amendment), reproduce red, fix, verify ACs, gates, one `F<n>:` commit per finding, closing report, STOP.
8. **Report to owner.** Review comes before relay. Only the owner merges.

## Acceptance criteria
1. `Api.cs` contains no process launch by an unqualified file name; the guard test passes.
2. With `explorer.exe` planted in the application directory, in the current directory, or first on `PATH`, `/api/open-folder` never runs it. Only `%SystemRoot%\explorer.exe` starts (Windows evidence).
3. A directory whose name contains a comma or spaces opens exactly that directory.
4. On a host without `%SystemRoot%\explorer.exe` (headless or CI), the route still returns `200 {"opened": …}` and launches nothing.
5. The full suite is green.
