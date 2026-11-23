# The host ingests ambient configuration: an `appsettings.json` beside the exe, or `Kestrel__*` env vars, override the loopback bind (live-reloaded), and `ASPNETCORE_ENVIRONMENT=Development` serves stack traces

| Field | Value |
|---|---|
| **Slug** | `host-ambient-config-overrides-bind` |
| **Severity** | Medium |
| **Confidence** | Confirmed — live repro (Linux headless harness, a verbatim copy of `Program.cs`'s `--headless` host setup). A repro test mirroring `Program.cs:13-19` fails on HEAD in the Linux harness. |
| **Area** | `nvcleanstall/Program.cs` (`Main`: `WebApplication.CreateBuilder` / `UseUrls`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | Materially different from **SEC-02 / GAP-S02**. GAP-S02's fix is "bind only to loopback and reject non-loopback `CLEANDRIVER_URLS`". A `Kestrel:Endpoints` section from any configuration source *replaces* the `UseUrls(urls)` addresses, so a check on `CLEANDRIVER_URLS` never sees the address actually bound. If GAP-S02's Host filtering uses the stock `AllowedHosts` configuration key, the same planted file overrides it as well. Not covered by **GAP-F05**, which moves mutable data to LocalAppData; with `IncludeAllContentForSelfExtract` the content root would move to the extraction directory, but environment variables still apply. |
| **Found** | 2026-11-23 |

## Summary
`Program.cs` builds the server with the default `WebApplication.CreateBuilder` and sets `ContentRootPath = AppContext.BaseDirectory`, which is the folder `CleanDriver.exe` sits in (for a portable single exe, typically `Downloads`). That default builder loads three things:
- `appsettings.json` and `appsettings.{Environment}.json` from that folder, with `reloadOnChange: true`;
- every process environment variable;
- the `ASPNETCORE_`/`DOTNET_` host variables.

Kestrel then binds the `Kestrel:Endpoints` configuration section in preference to `UseUrls`. As a result, a file planted next to the exe, even *after* launch, or an inherited `Kestrel__Endpoints__*` variable decides which interfaces the unauthenticated, filesystem-capable API listens on. An ambient `ASPNETCORE_ENVIRONMENT=Development` switches on the developer exception page, which returns full stack traces with absolute paths.

## Evidence
- `nvcleanstall/Program.cs:11-19`:
  ```csharp
  string urls = Environment.GetEnvironmentVariable("CLEANDRIVER_URLS") ?? "http://localhost:4780";
  var builder = WebApplication.CreateBuilder(new WebApplicationOptions
  {
      ContentRootPath = AppContext.BaseDirectory,
      WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
  });
  builder.WebHost.UseUrls(urls);
  builder.Logging.SetMinimumLevel(LogLevel.Warning);
  ```
  - `CreateBuilder`'s defaults add JSON file sources from the content root (`reloadOnChange: true`) and environment-variable sources.
  - `WebApplicationOptions` does not pin `EnvironmentName`, so `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` decide it. `WebApplication` adds the developer exception page automatically in Development.
  - Kestrel's default options setup binds `Configuration.GetSection("Kestrel")`. When that section declares endpoints, Kestrel logs `Overriding address(es) '<UseUrls value>'. Binding to endpoints defined via IConfiguration and/or UseKestrel() instead.`
- `nvcleanstall/Api.cs:72` states the invariant this breaks: `// Never leak absolute filesystem paths to the client.`

The host setup assumes `UseUrls(urls)` decides where the server listens, and that the environment is Production. Neither is true for the default builder: both are inputs taken from the machine and the exe's folder.

## Impact
- **Bind override.** Anyone who can write one file into the folder holding `CleanDriver.exe` can make the API listen on any interface the next time the app starts, or immediately if the app is running, because of `reloadOnChange`. For the shipped portable exe, that folder is usually the user's Downloads folder, where browsers save files. For example, `{"Kestrel":{"Endpoints":{"x":{"Url":"http://*:4780"}}}}` binds every interface including loopback, so the native shell keeps working normally. The same applies to an inherited `Kestrel__Endpoints__X__Url` environment variable. LAN peers can then reach:
  - `/api/package`, which reads local packages by path;
  - `/api/execute`, which writes output to any user-supplied path;
  - `/api/open-folder`, which launches a process;
  - `/api/jobs/{id}`, which exposes absolute paths.

  This is SEC-02's High exposure, through a route the planned GAP-S02 check does not inspect.
- **Information leak.** With `ASPNETCORE_ENVIRONMENT=Development` set machine-wide, which is common on .NET developer machines, every unhandled 500 returns the full stack trace and absolute source and installation paths (see §B step 4).
- **Severity.** Medium: the consequence is High-class, but it requires a stronger precondition, namely write access to the exe's folder or to the process environment.

## How to reproduce
### A. Automated — failing xunit test
`Program.Main` is a blocking WinForms entry point. Following the `CatalogProviderFactory` precedent, the fix extracts the host construction into a pure seam, `Lib/AppHost.cs`, with `public static WebApplicationBuilder CreateBuilder(string urls, string contentRoot)`. The red commit contains this test plus a behavior-preserving extraction of `Program.cs:13-19`, so the test compiles and fails for the stated behavioral reason. Add as `nvcleanstall/CleanDriver.Tests/AppHostConfigTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace CleanDriver.Tests;

public class AppHostConfigTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // A file planted beside the exe must not change where the API listens. (Env-var and
    // ASPNETCORE_ENVIRONMENT variants are process-global, so they are proven in the live
    // exercise, not in a parallel test run.)
    [Fact]
    public async Task AppSettingsPlantedBesideExe_DoesNotChangeWhereTheApiListens()
    {
        var exeDir = TempDir.Create();
        int requested = FreePort(), planted = FreePort();
        // Loopback on a different port keeps the test local; the same key accepts http://*:4780.
        File.WriteAllText(Path.Combine(exeDir, "appsettings.json"),
            $"{{\"Kestrel\":{{\"Endpoints\":{{\"Planted\":{{\"Url\":\"http://127.0.0.1:{planted}\"}}}}}}}}");

        var app = AppHost.CreateBuilder($"http://127.0.0.1:{requested}", exeDir).Build();
        Api.Map(app, new MockCatalogProvider());
        await app.StartAsync();
        try
        {
            Assert.Equal(new[] { $"http://127.0.0.1:{requested}" }, app.Urls.ToArray());
            Assert.Equal("Production", app.Environment.EnvironmentName);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await http.GetAsync($"http://127.0.0.1:{requested}/api/tweaks");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
```

Command:
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~AppHostConfigTests"
```

RED, Linux net10.0 harness output. `Program.cs` is not compiled in the harness (WinForms), so the harness test `HostAmbientConfigOverridesBindRepro` inlines `Program.cs:13-19` verbatim in place of `AppHost.CreateBuilder`:
```
  Failed CleanDriver.Tests.HostAmbientConfigOverridesBindRepro.AppSettingsPlantedBesideExe_DoesNotChangeWhereTheApiListens [216 ms]
  Error Message:
   Assert.Equal() Failure: Collections differ
           ↓ (pos 0)
Expected: ["http://127.0.0.1:38215"]
Actual:   ["http://127.0.0.1:37171"]
           ↑ (pos 0)
Failed!  - Failed:     1, Passed:     0, Skipped:     0, Total:     1, Duration: 216 ms - Harness.Tests.dll (net10.0)
```
`37171` is the planted port. The requested `CLEANDRIVER_URLS` port was never bound.

### B. Live — running app
Run against the built app folder (`nvcleanstall/bin/Debug/net10.0-windows/`, which is `AppContext.BaseDirectory`) with `dotnet run --project nvcleanstall -- --headless --mock-catalog`, or run `CleanDriver.exe --headless --mock-catalog` from that folder. The output below was captured on the Linux headless harness, with `127.0.0.2` (still loopback) standing in for `*` so nothing listened beyond the machine.

1. **Planted file, before launch.** Expected: listens on the `CLEANDRIVER_URLS` address only. Actual:
   ```
   $ echo '{ "Kestrel": { "Endpoints": { "Planted": { "Url": "http://127.0.0.2:4803" } } } }' > <exe dir>/appsettings.json
   $ CLEANDRIVER_URLS=http://127.0.0.1:4803 <app> --headless --mock-catalog
   CleanDriver running at http://127.0.0.1:4803 (catalog: mock) (headless)
   warn: Microsoft.AspNetCore.Server.Kestrel[0]
         Overriding address(es) 'http://127.0.0.1:4803'. Binding to endpoints defined via IConfiguration and/or UseKestrel() instead.
   $ curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:4803/api/system
   000                      # the address the app announced is NOT served
   $ curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.2:4803/api/system
   200                      # the planted address is
   ```
   On Windows, `$env:CLEANDRIVER_URLS` must be set in the Windows environment (see the CLAUDE.md WSL quirk), or use the default `http://localhost:4780`.
2. **Planted file, after launch (live reload).** Start without the file, then write it while the app is running:
   ```
   before: 127.0.0.1 -> 200
   $ echo '{ "Kestrel": { "Endpoints": { "Planted": { "Url": "http://127.0.0.2:4803" } } } }' > <exe dir>/appsettings.json
   after:  127.0.0.1 -> 200
   after:  127.0.0.2 -> 200     # a new listener appeared without a restart
   ```
3. **Environment variable.** With no file, and `Kestrel__Endpoints__E__Url=http://127.0.0.2:4803` in the process environment, you get the same `Overriding address(es)` warning, `127.0.0.1 -> 000` and `127.0.0.2 -> 200`.
4. **Ambient Development environment.** With `ASPNETCORE_ENVIRONMENT=Development`, any unhandled 500 (here the null-token crash documented in `docs/bugs/execute-malformed-body-500/`) returns the developer exception page:
   ```
   $ curl -s -X POST localhost:4780/api/execute -H "Content-Type: application/json" -d '{}'
   System.ArgumentNullException: Value cannot be null. (Parameter 'key')
      at System.ThrowHelper.ThrowArgumentNullException(String name)
      at System.Collections.Concurrent.ConcurrentDictionary`2.TryGetValue(TKey key, TValue& value)
      at CleanDriver.Api.<>c.<Map>b__7_7(ExecuteRequest req) in /home/user/nvcleanclone/nvcleanstall/Api.cs:line 96
      ...
   ```
   Expected in every case: the server listens only where `CLEANDRIVER_URLS` (or the default) says, ignores the exe folder's JSON files and `Kestrel__*` variables, and runs as Production.

Delete the planted `appsettings.json` by name afterwards.

## Root cause
The host is built with the general-purpose web-server builder, whose design is "configuration comes from the deployment's files and environment". It is then placed in a folder the user does not control as a deployment (Downloads). `UseUrls` is the lowest-precedence way to choose endpoints, so `Kestrel:Endpoints` from any source wins. The code treats `UseUrls(urls)` and the absent environment name as authoritative when both are only defaults.

## Remedy
Build the host from explicit inputs only. Extract the setup into `Lib/AppHost.cs` so it is testable, and have `Program.cs` call it:

```csharp
using Microsoft.Extensions.Configuration;   // AddInMemoryCollection (other usings are global)

namespace CleanDriver.Lib;

public static class AppHost
{
    // The desktop host takes NO ambient configuration: no appsettings*.json from the exe's
    // folder (which is usually Downloads), no environment variables, no ASPNETCORE_ENVIRONMENT.
    // CLEANDRIVER_URLS / --mock-catalog are read explicitly by Program/CatalogProviderFactory.
    public static WebApplicationBuilder CreateBuilder(string urls, string contentRoot)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            WebRootPath = Path.Combine(contentRoot, "wwwroot"),
            EnvironmentName = Environments.Production,
        });
        builder.Configuration.Sources.Clear();          // drops JSON files (and their watchers) + env vars
        builder.Configuration.AddInMemoryCollection();  // UseUrls needs a writable source
        builder.WebHost.UseUrls(urls);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        return builder;
    }
}
```
```diff
-        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
-        {
-            ContentRootPath = AppContext.BaseDirectory,
-            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
-        });
-        builder.WebHost.UseUrls(urls);
-        builder.Logging.SetMinimumLevel(LogLevel.Warning);
-
-        var app = builder.Build();
+        var app = AppHost.CreateBuilder(urls, AppContext.BaseDirectory).Build();
```

This was validated in the Linux harness. With the remedy, the planted-file test passes. It also passes with `ASPNETCORE_ENVIRONMENT=Development` and `Kestrel__Endpoints__E__Url=http://127.0.0.1:1` exported into the test process: the environment reports `Production` and only the requested URL is bound.

- **Why this is minimal.** It changes three lines of behavior. `CLEANDRIVER_URLS`, `--headless`, `--mock-catalog` and `CLEANDRIVER_MOCK_CATALOG` are read through `Environment.GetEnvironmentVariable` / `args` directly, so they are unaffected. The logging level is preserved. Static files and routing are unchanged.
- **Alternatives rejected.**
  - `WebApplication.CreateEmptyBuilder`: requires hand-wiring Kestrel, routing and static files, which is a larger diff.
  - Moving `ContentRootPath`: leaves the environment-variable and Development vectors open.
  - Validating `CLEANDRIVER_URLS` only (GAP-S02 as written): bypassed by `Kestrel:Endpoints`.
  - Calling `ConfigureKestrel(o => o.Configure(emptyConfig))`: narrower, and leaves the Development page and `AllowedHosts` override open.
- **Fences.** No UI change (R1–R10). The safety boundary, deletion rules and honesty markers are not involved. GAP-S02's planned Host filtering should configure `HostFilteringOptions` in code, not via `AllowedHosts` config; the register's GAP-S02 text should say so.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** `docs/security_register.md` is active. This belongs in **GAP-S02** (the HTTP boundary), as an amendment: "the host ignores ambient configuration; endpoints, environment and host filtering come only from code and `CLEANDRIVER_URLS`". GAP-S02 is next in Wave S, so the owner should pin this before that slice starts, because GAP-S02's loopback check is otherwise bypassable.
1. **Slice setup:** `git worktree add ../nvcleanclone-hostcfg -b feat/host-ignores-ambient-config origin/main`.
2. **Red.** Add §A at `nvcleanstall/CleanDriver.Tests/AppHostConfigTests.cs` together with the behavior-preserving extraction `Lib/AppHost.cs` (`Program.cs:13-19` moved verbatim) and `Program.cs` calling it. Disclose this in the PR as the minimum production change the test needs in order to compile. Run the filter: it fails with `Expected ["http://127.0.0.1:<requested>"] / Actual ["http://127.0.0.1:<planted>"]`. Commit `"host-ambient-config-overrides-bind (red): extract AppHost; planted appsettings.json must not rebind the API"`.
3. **Green.** Add `EnvironmentName = Production`, `Sources.Clear()` and `AddInMemoryCollection()`, then run the FULL suite. Commit `"host-ambient-config-overrides-bind: build the host from explicit inputs only"`.
4. **Refactor**, with the suite green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise** on Windows:
   - Rerun §B steps 1–4 against `CleanDriver.exe --headless --mock-catalog`.
   - Step 1: the `Overriding address(es)` warning is gone, and `localhost:4780` answers 200.
   - Step 2: no new listener appears; `netstat -ano | findstr 4780` shows loopback only.
   - Step 3: the `Kestrel__…` variable is ignored.
   - Step 4: `curl -s -i -X POST localhost:4780/api/execute -d "{}" -H "Content-Type: application/json"` returns a 500 with an empty body or, if `execute-malformed-body-500` has landed, a 400. It never returns a stack trace.
   - Also run the native shell once to show the wizard loads.
7. **PR.** Fill the template:
   - TDD row.
   - Edited-test disclosure: none.
   - Deviations: the extraction-in-red commit.
   - Live transcripts.

   Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green.
2. **Isolated worktree:** `git worktree add ../nvcleanclone-review-hostcfg origin/feat/host-ignores-ambient-config`. The range is `origin/main...HEAD`.
3. **Re-run the gates**, then run the live steps above, including the native shell.
4. **Finder pass:**
   - Is `Sources.Clear()` called before `UseUrls`?
   - Is `EnvironmentName` pinned in `WebApplicationOptions` (not via config)?
   - Does any remaining code read `builder.Configuration` / `IConfiguration` for app behavior that now silently gets defaults? Grep `Configuration[`, `GetSection`, `IOptions`.
   - Does `Program.cs` still print the same startup line?
   - Did the red commit contain only the verbatim extraction?
5. **Adversarial verify:**
   - Plant `appsettings.Production.json` with endpoints.
   - Set `ASPNETCORE_URLS=http://*:4780`, `DOTNET_URLS`, `ASPNETCORE_HTTP_PORTS=80` and `ASPNETCORE_PREFERHOSTINGURLS=true` in the process environment.
   - Plant `appsettings.json` with `"Logging":{"LogLevel":{"Default":"Trace"}}`, which must not raise verbosity.
   - Set `DOTNET_ENVIRONMENT=Development`.

   Each must be ignored.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with findings marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Fix loop C0–C7: Orient (fence: Wave S, GAP-S02 amendment), reproduce red, fix, verify ACs, gates, one `F<n>:` commit per finding, closing report, STOP.
8. **Report to owner.** Review comes before relay. Only the owner merges.

## Acceptance criteria
1. With an `appsettings.json` or `appsettings.Production.json` declaring `Kestrel:Endpoints` in the content root, the app binds exactly the `CLEANDRIVER_URLS` (or default) address. No `Overriding address(es)` warning is logged.
2. Writing such a file while the app runs creates no new listener.
3. `Kestrel__Endpoints__*`, `ASPNETCORE_URLS`, `ASPNETCORE_HTTP_PORTS` and `ASPNETCORE_PREFERHOSTINGURLS` in the environment do not change the bound addresses.
4. With `ASPNETCORE_ENVIRONMENT` or `DOTNET_ENVIRONMENT` set to `Development`, `app.Environment.EnvironmentName` is `Production`, and no unhandled error returns a stack trace or path.
5. Headless and native modes, `--mock-catalog` and `CLEANDRIVER_MOCK_CATALOG=1` behave as before, and the full suite is green.
