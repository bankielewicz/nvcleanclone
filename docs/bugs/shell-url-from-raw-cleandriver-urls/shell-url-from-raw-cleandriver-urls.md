# The native shell's address is built from the raw `CLEANDRIVER_URLS` text: a `;` list crashes the app after the server starts, and port `0` opens a dead page

| Field | Value |
|---|---|
| **Slug** | `shell-url-from-raw-cleandriver-urls` |
| **Severity** | Low |
| **Confidence** | Plausible — reasoned, not dynamically reproduced end-to-end, because `ShellForm` (WinForms/WebView2) cannot run on Linux. Both halves are reproduced on .NET 10: Kestrel accepts and serves both values (live harness), and `Program.cs:46` + `ShellForm.cs:20`'s exact `Uri` expression throws or points at port 0 (a mirror repro test fails on HEAD). |
| **Area** | `nvcleanstall/Program.cs` (`Main`, native branch), `nvcleanstall/ShellForm.cs` (ctor) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Windows-only (native mode) |
| **Related register entries** | Not covered by **SEC-02 / GAP-S02**. Its "bind only to loopback and reject non-loopback `CLEANDRIVER_URLS`" accepts both failing values, because every entry is loopback (`http://127.0.0.1:4780;http://[::1]:4780`, `http://127.0.0.1:0`). GAP-S02 also plans to pass a token "in the URL fragment" of this same shell address, so it will build on this expression. This is not SEC-05 (navigation restriction). |
| **Found** | 2026-11-23 |
| **Register slice** | **GAP-R04** — App host and process launch (owner-pinned 2026-11-23, `docs/security_register.md` §4 Wave R). |

## Summary
`Program.cs` passes `CLEANDRIVER_URLS` verbatim to Kestrel (`UseUrls`), which accepts standard ASP.NET URL syntax: a `;`-separated list, or port `0` for a dynamic port. It then builds the WebView2 address by string concatenation from the same raw text, `urls.TrimEnd('/') + "/?shell=1"`. For a list, `ShellForm`'s `new Uri(url)` throws `UriFormatException`, *after* `app.Start()` has already bound the server. The exception unwinds `Main` through the `finally` (which stops the server), and the WinExe dies with no window and no message. For port `0`, the window navigates to `http://127.0.0.1:0/` and shows a connection error, even though the server is listening on the port the OS assigned.

## Evidence
- `nvcleanstall/Program.cs:11,18`. Raw text goes to Kestrel, which interprets it:
  ```csharp
  string urls = Environment.GetEnvironmentVariable("CLEANDRIVER_URLS") ?? "http://localhost:4780";
  builder.WebHost.UseUrls(urls);
  ```
- `nvcleanstall/Program.cs:42-51`. The server is started, then the shell is built from the *same raw text* instead of from what Kestrel actually bound:
  ```csharp
  app.Start();
  try
  {
      ApplicationConfiguration.Initialize();
      Application.Run(new ShellForm(urls.TrimEnd('/') + "/?shell=1"));
  }
  finally
  {
      app.StopAsync().Wait();
  }
  ```
- `nvcleanstall/ShellForm.cs:20` parses it:
  ```csharp
  Source = new Uri(url),
  ```
- `Program.cs:32-34` also prints the raw text as "running at …", so the console line is equally wrong for port 0.
- On .NET 10, `new Uri(urls.TrimEnd('/') + "/?shell=1")` gives:
  ```
  http://localhost:4780                    -> OK  host=localhost port=4780
  http://localhost:4780;http://[::1]:4781  -> UriFormatException: Invalid URI: Invalid port specified.
  http://127.0.0.1:0                       -> OK  host=127.0.0.1 port=0
  ```

The code assumes that `CLEANDRIVER_URLS` is a single, fixed, navigable URL. Kestrel's grammar for the same value is broader, and the address actually bound is only known after `Start()`, from `app.Urls`.

## Impact
- Native mode (the shipped product) crashes silently at startup for a valid list value. For example, `CLEANDRIVER_URLS=http://127.0.0.1:4780;http://[::1]:4780`, a natural way to pin both loopback stacks. Port `0`, the standard way to avoid the fixed-port collision documented in CLAUDE.md's `taskkill` quirk, produces a window stuck on a connection-error page.
- Headless mode, which every acceptance check uses, is unaffected. That is why this has gone unnoticed.
- `CLEANDRIVER_URLS` is an undocumented override, so likelihood is low and impact is bounded: the app does not start or does not load. **Low**.

## How to reproduce
### A. Automated — failing xunit test
The fix extracts the address choice into a pure seam, `Lib/ShellAddress.cs` with `public static Uri From(IEnumerable<string> addresses)`, following the `CatalogProviderFactory` precedent. Add the test as `nvcleanstall/CleanDriver.Tests/ShellAddressTests.cs`.

Before the seam exists this is a compile-level red. Written first with HEAD's semantics, `From(a) => new Uri(string.Join(";", a).TrimEnd('/') + "/?shell=1")`, called from `Program.cs:46` as `ShellAddress.From(new[] { urls })`, the `;`-list case is a behavioral red at the seam.

The port-0 case is a call-site defect: `Program.cs:46` passes the raw text instead of `app.Urls`. Fed `app.Urls`, the seam test passes for it. Its red/green evidence is therefore the harness mirror below plus the Windows live exercise (§B step 2), and this must be disclosed in the PR.

```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using CleanDriver.Lib;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CleanDriver.Tests;

public class ShellAddressTests
{
    [Theory]
    [InlineData("http://127.0.0.1:0")]                       // dynamic port
    [InlineData("http://127.0.0.1:0;http://127.0.0.2:0")]    // ';' list, every entry loopback
    public async Task ShellAddress_IsAnAddressTheServerActuallyListensOn(string cleandriverUrls)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = TempDir.Create() });
        builder.WebHost.UseUrls(cleandriverUrls);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        Api.Map(app, new MockCatalogProvider());
        await app.StartAsync();
        try
        {
            Uri shell = ShellAddress.From(app.Urls);

            var listening = app.Urls.Select(u => new Uri(u)).ToList();
            Assert.Contains(listening, u => u.Host == shell.Host && u.Port == shell.Port);
            Assert.Equal("?shell=1", shell.Query);
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(new Uri(shell, "/api/tweaks"))).StatusCode);
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
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~ShellAddressTests"
```

RED, Linux net10.0 harness output. The harness test `ShellUrlFromRawCleandriverUrlsRepro` evaluates `Program.cs:46` + `ShellForm.cs:20` verbatim, `new Uri(urls.TrimEnd('/') + "/?shell=1")`, in place of the seam:
```
  Failed CleanDriver.Tests.ShellUrlFromRawCleandriverUrlsRepro.ShellAddress_IsAnAddressTheServerActuallyListensOn(cleandriverUrls: "http://127.0.0.1:0;http://127.0.0.2:0") [145 ms]
  Error Message:
   System.UriFormatException : Invalid URI: Invalid port specified.
  Failed CleanDriver.Tests.ShellUrlFromRawCleandriverUrlsRepro.ShellAddress_IsAnAddressTheServerActuallyListensOn(cleandriverUrls: "http://127.0.0.1:0") [25 ms]
  Error Message:
   Assert.Contains() Failure: Filter not matched in collection
Collection: [http://127.0.0.1:35747/]
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2, Duration: 165 ms - Harness.Tests.dll (net10.0)
```
In both cases Kestrel bound successfully (`app.StartAsync()` returned). Only the shell address is wrong.

### B. Live — running app
**Server half, Linux harness, captured.** The server accepts a `;` list and serves both entries:
```
$ CLEANDRIVER_URLS='http://127.0.0.1:4803;http://127.0.0.2:4803' <app> --headless --mock-catalog
CleanDriver (linux harness) running at http://127.0.0.1:4803;http://127.0.0.2:4803
$ curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:4803/api/system
200
$ curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.2:4803/api/system
200
```

**Shell half, Windows, owner-run (not run here: WinForms).** Set the variable in the Windows environment; inline WSL env vars do not cross into the exe (CLAUDE.md quirk).
1. In PowerShell, `$env:CLEANDRIVER_URLS = "http://127.0.0.1:4780;http://[::1]:4780"; .\CleanDriver.exe`.
   - Expected: the wizard window opens on one of the bound addresses.
   - Actual (reasoned from the code above): no window. The process exits, and Event Viewer → Application shows a `.NET Runtime` error with `System.UriFormatException: Invalid URI: Invalid port specified.` at `CleanDriver.ShellForm..ctor`.
2. `$env:CLEANDRIVER_URLS = "http://127.0.0.1:0"; .\CleanDriver.exe`.
   - Expected: the wizard loads.
   - Actual: the window shows WebView2's "can't reach this page" for `127.0.0.1:0`, while `netstat -ano | findstr LISTENING` shows CleanDriver on an ephemeral port.
3. Control: `Remove-Item Env:CLEANDRIVER_URLS; .\CleanDriver.exe --headless` serves `http://localhost:4780` as before.

## Root cause
Two different parsers read one string. Kestrel interprets `CLEANDRIVER_URLS` as a list of bind specifications, where port 0 means "assign one". The shell code treats the same string as one literal navigable URL. The authoritative answer to "where is the server?" exists only after `Start()`, in `app.Urls` (`IServerAddressesFeature`), and the code never consults it.

## Remedy
Derive the shell address from the bound addresses, preferring a loopback one:

```csharp
using System.Net;

namespace CleanDriver.Lib;

public static class ShellAddress
{
    public const string NoLoopbackError = "CleanDriver is not listening on a loopback address; the window cannot open";

    // The address the WebView2 shell loads: one Kestrel actually bound (read after Start()),
    // never derived from the CLEANDRIVER_URLS text. Loopback only: an any-address bind maps to
    // localhost on its port, and nothing else is ever used. Parsing never throws.
    public static Uri From(IEnumerable<string> boundUrls)
    {
        Uri? viaAny = null;
        foreach (var raw in boundUrls)
        {
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var u)) continue;
            if (u.IsLoopback) return Shell(u);
            if (viaAny is null && IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip)
                && (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
                viaAny = new UriBuilder(u) { Host = "localhost" }.Uri;
        }
        return viaAny is not null ? Shell(viaAny) : throw new InvalidOperationException(NoLoopbackError);

        static Uri Shell(Uri u) => new(new Uri(u.GetLeftPart(UriPartial.Authority)), "/?shell=1");
    }
}
```
```diff
         app.Start();
         try
         {
             ApplicationConfiguration.Initialize();
-            Application.Run(new ShellForm(urls.TrimEnd('/') + "/?shell=1"));
+            Application.Run(new ShellForm(ShellAddress.From(app.Urls).ToString()));
         }
```
The same logic, inlined into the harness test, turned both cases green. *Revised after the PR #25 review:* the first sketch fell back to `bound.First()`, a non-loopback address, which the fence below forbids. On .NET 10 (Linux harness) `app.Urls` reports `http://0.0.0.0:<port>` after `Start()` for `http://*:0` and `http://+:0`, so that fallback would have loaded an any-address. The revised sketch passed 8/8 pure cases in the harness: loopback kept (`127.0.0.1`, `localhost`, `[::1]`); `0.0.0.0` and `[::]` mapped to `localhost:<port>`; loopback preferred over an earlier any-address; `http://192.168.1.5:4780` and an unparseable `http://*:4781` failing with `NoLoopbackError`.

- **Why this is minimal.** One seam and a one-line call-site change. `ShellForm` is unchanged; it still receives a string. Optionally, print `string.Join(", ", app.Urls)` in the startup line for the same reason; that is a separate commit, forced by its own test if wanted.
- **Alternatives rejected.**
  - Splitting `urls` on `;` and rewriting port 0: this re-implements Kestrel's grammar and is still wrong for port 0 until after bind.
  - Forbidding lists: an arbitrary restriction on a standard knob.
- **Fences.**
  - No UI change (R1–R10). The same window and page; `?shell=1` is preserved.
  - Do not use a non-loopback `[::]`/`0.0.0.0` entry as the shell address. GAP-S02 rejects those at bind time anyway.
  - GAP-S02's planned "token in the URL fragment" composes onto `ShellAddress.From(...)`.
- **Back-compat pins.** None exist for `Program.cs`/`ShellForm.cs`, which are not covered by the suite.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** `docs/security_register.md` is active. Fold into **GAP-S02** (it already rewrites how the shell address is formed, to add the token fragment) as an added AC: "the shell loads an address Kestrel actually bound". The owner must pin it; otherwise it needs a new owner-pinned Low entry.
   > *Pinned 2026-11-23 as **GAP-R04** (`docs/security_register.md` §4 Wave R): use that ID; the register gate is satisfied. **One slice is one PR:** steps 1 and 7 below apply to the slice as a whole, with one worktree and branch for all of its reports (for example `feat/<slice-id>-<short>`), keeping one red/green commit pair per report.*
1. **Slice setup:** `git worktree add ../nvcleanclone-shelladdr -b feat/shell-address-from-bound-urls origin/main`.
2. **Red.** Add §A at `nvcleanstall/CleanDriver.Tests/ShellAddressTests.cs`, plus `Lib/ShellAddress.cs` with HEAD's semantics (`From(a) => new Uri(string.Join(";", a).TrimEnd('/') + "/?shell=1")`) and `Program.cs:46` calling `ShellAddress.From(new[] { urls })`. This is behavior-preserving, and it is the minimum production code the test needs to compile; disclose it. Run the filter: the `;`-list case fails with `UriFormatException`. Attach the harness mirror red from §A for the port-0 case, which is a call-site defect. Commit `"shell-url-from-raw-cleandriver-urls (red): the shell loads an address the server actually bound"`.
3. **Green.** Implement `From` over the bound addresses, switch `Program.cs:46` to `ShellAddress.From(app.Urls)`, and run the FULL suite. Commit `"shell-url-from-raw-cleandriver-urls: derive the WebView2 address from app.Urls"`.
4. **Refactor**, with the suite green only.
5. **Gates:** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise** on Windows: rerun §B's Windows steps 1–3. Steps 1 and 2 now open the wizard; record a screenshot and `netstat` output. Step 3 is unchanged. Also run plain `.\CleanDriver.exe` (no variable): the window loads `http://localhost:4780/?shell=1`.
7. **PR.** Fill the template:
   - TDD row.
   - Edited-test disclosure: none.
   - Deviations: the seam-in-red commit.
   - Native-mode evidence.

   Never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `build-test-smoke` must be green.
2. **Isolated worktree:** `git worktree add ../nvcleanclone-review-shelladdr origin/feat/shell-address-from-bound-urls`. The range is `origin/main...HEAD`.
3. **Re-run the gates**, then run the Windows native steps yourself. Headless proof alone does not cover this change.
4. **Finder pass:**
   - Is `From` called after `app.Start()`? Before `Start`, `app.Urls` is the configured text, not the bound list.
   - Is loopback preferred?
   - Is `?shell=1` preserved exactly? `app.js` keys on it.
   - Is `localhost` handled? Kestrel reports `http://localhost:4780` for the default; it is not `IsLoopback` by IP parse, but `Uri.IsLoopback` is true for `localhost`.
   - Is there no change to `ShellForm` navigation policy (SEC-05 is a separate slice)?
5. **Adversarial verify:**
   - Try `CLEANDRIVER_URLS` values `http://localhost:4780/` (trailing slash), `http://[::1]:0`, `http://127.0.0.1:4780;http://localhost:4781` and `http://localhost:0` (Kestrel refuses dynamic ports on `localhost`; the app should fail at `Start()` exactly as today, not later).
   - Try the default with no variable.
   - Feed `ShellAddress.From` `http://0.0.0.0:4781`, `http://[::]:4781`, `http://192.168.1.5:4780` and `http://*:4781`: the first two map to `localhost`, the last two fail by name.

   Each either opens the wizard on a bound address or fails before the window, never after.
6. **Verdict document.** APPROVE or REQUEST CHANGES, with findings marked CONFIRMED or PLAUSIBLE.
7. **Feedback to builder.** Fix loop C0–C7: Orient, then reproduce red, fix, verify ACs, gates, one `F<n>:` commit per finding, closing report, STOP.
8. **Report to owner.** Review comes before relay. Only the owner merges.

## Acceptance criteria
1. With `CLEANDRIVER_URLS` set to a `;` list of loopback URLs, the native window opens and loads the wizard from one of the bound addresses.
2. With `CLEANDRIVER_URLS=http://127.0.0.1:0`, the window loads the wizard from the OS-assigned port.
3. The shell address always carries `?shell=1`, and its host and port match an entry of `app.Urls`.
4. Default launch (no variable) still loads `http://localhost:4780/?shell=1`. Headless behavior is unchanged, and the full suite is green.
5. The shell address is never a non-loopback address. An any-address bind (`0.0.0.0`, `[::]`) opens `http://localhost:<port>/?shell=1`; a bound list with no loopback or any-address entry (or only unparseable entries) fails before the window opens with `ShellAddress.NoLoopbackError`, never with a `UriFormatException`.
