# Real download hangs forever when the server never sends response headers; the stall timeout covers only body reads

| Field | Value |
|---|---|
| **Slug** | `download-headers-phase-has-no-stall-timeout` |
| **Severity** | Medium (unbounded availability loss for the download path; no data or safety effect) |
| **Confidence** | Confirmed — repro test fails on HEAD (Linux harness) |
| **Area** | `nvcleanstall/Lib/Jobs.cs` (`GetFollowingRedirectsAsync`, `StartRealDownload`) |
| **Baseline** | `main` @ `54b3c31` |
| **Platform** | Both |
| **Related register entries** | None covers this. It is the same defect family as HARD-03, which closed the unbounded wait for the WMI read. It is distinct from HARD-05/A5: the hop **count** is bounded, but each hop's **wait** is not. Hardening candidate #6 (`Jobs.StallTimeout` static seam) is a test-parallelism note, not this. |
| **Found** | 2026-11-23 |

## Summary
The real-download client has no overall timeout. The code says liveness comes from "ResponseHeadersRead + a per-read stall timeout", but the stall window only wraps body `ReadAsync` calls. Each `GetAsync` in the redirect-follow loop, covering TCP connect, TLS handshake and the wait for response headers, gets only the job's cancel token. So when a peer accepts the request and never answers, the job stays `running` forever with progress 0. That peer can be the initial host or any followed redirect hop. Examples: a wedged proxy, an HTTPS-scanning antivirus that holds the response, or a half-open TLS session. The user never gets the `download stalled` error. Until the user presses Cancel, every retry of that version gets the same wedged job back.

## Evidence
- `nvcleanstall/Lib/Jobs.cs:157-159`. The design states the liveness guarantee:
  ```csharp
  // A separate client from GAP-01's 5s metadata client: a real installer legitimately
  // takes minutes, so the client has no overall timeout (ResponseHeadersRead + a
  // per-read stall timeout enforce liveness instead).
  ```
- `nvcleanstall/Lib/Jobs.cs:232-235`. The overall timeout is switched off, and the default `SocketsHttpHandler` has an infinite `ConnectTimeout` and no headers timeout:
  ```csharp
  var http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: ownsHandler)
  {
      Timeout = System.Threading.Timeout.InfiniteTimeSpan,
  };
  ```
- `nvcleanstall/Lib/Jobs.cs:261-263`. Every hop waits on the bare cancel token, with no stall window:
  ```csharp
  for (int redirects = 0; ; redirects++)
  {
      var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
  ```
- `nvcleanstall/Lib/Jobs.cs:321-325`. The stall window exists, but only around body reads, after headers have arrived:
  ```csharp
  using var stall = new CancellationTokenSource(StallTimeout);
  using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancelToken, stall.Token);
  int n = await src.ReadAsync(buf, linked.Token);
  ```
- `nvcleanstall/Lib/Jobs.cs:212-214`. Idempotency turns a wedged job into a sticky one. While it is `running`, every new `/api/download` for that version returns it:
  ```csharp
  if (ActiveDownloads.TryGetValue(version, out var existingId)
      && Get(existingId) is { Status: "running" } existing)
      return existing;
  ```
- The stall pin `RealDownloadTests.StartRealDownload_Stall_FailsWithStalledMessage` covers only a stream that stalls **after** headers (a `ScriptedStream` inside a 200 response). No test covers a pre-headers stall.

The faulty reasoning is that `ResponseHeadersRead` only moves the point where `GetAsync` returns. It does not bound how long `GetAsync` may take to get there.

## Impact
- The download screen (Screen 2) sits at 0 % / 0 MB with no error, and stays there until the user notices and presses Cancel. `pollDownload` polls every 150 ms indefinitely (`app.js:155-176`). The `download stalled` error this engine was built to show never appears.
- Pressing Next again for the same version returns the same wedged job (ActiveDownloads), so a retry without Cancel cannot recover.
- Headless/API consumers (the terminal-verifiable acceptance path) get a job that never ends. Each wedged job holds an `HttpClient`, a socket and a `CancellationTokenSource` indefinitely.
- How likely: it needs a peer that goes silent after accepting the connection. That is uncommon on NVIDIA's CDN but routine behind corporate proxies and HTTPS-intercepting AV. It also applies to every followed redirect hop, so HARD-05's manual follow loop inherits it. TCP connect itself is bounded by the OS (~21 s SYN retry on Windows). TLS handshake and the headers wait are not.

## How to reproduce
### A. Automated — failing xunit test
Add the helper to `nvcleanstall/CleanDriver.Tests/TestDownload.cs`. Add the facts to `nvcleanstall/CleanDriver.Tests/RealDownloadTests.cs`; that class already mutates `Jobs.StallTimeout` and xunit serializes facts within a class.

```csharp
// TestDownload.cs — a peer that accepts the request and never sends headers. It honours
// the token it is given, so a bounded headers phase (and the cleanup cancel) can end it.
internal sealed class SilentPeerHandler : HttpMessageHandler
{
    private readonly Func<Uri, HttpResponseMessage?> _script;
    public SilentPeerHandler(Func<Uri, HttpResponseMessage?>? script = null) => _script = script ?? (_ => null);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var scripted = _script(request.RequestUri!);
        if (scripted != null) return scripted;
        await Task.Delay(Timeout.Infinite, ct); // headers never arrive
        throw new InvalidOperationException("unreachable");
    }
}
```

```csharp
// RealDownloadTests.cs
[Fact]
public async Task StartRealDownload_PeerNeverSendsHeaders_FailsAsStalled()
{
    var dir = TempDir.Create();
    var prior = Jobs.StallTimeout;
    Jobs.StallTimeout = TimeSpan.FromMilliseconds(150);
    Job? job = null;
    try
    {
        job = Jobs.StartRealDownload(LiveRelease.New(version: "611.01"), dir, new SilentPeerHandler());
        var first = await Task.WhenAny(job.Completion!, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.True(ReferenceEquals(first, job.Completion),
            $"job still '{job.Status}' after 3 s with a 150 ms stall window (headers phase is unbounded)");
        Assert.Equal("failed", job.Status);
        Assert.Contains("stall", job.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(dir));
    }
    finally
    {
        if (job != null) { Jobs.Cancel(job.Id); await job.Completion!; }
        Jobs.StallTimeout = prior;
    }
}

[Fact]
public async Task StartRealDownload_RedirectHopNeverSendsHeaders_FailsAsStalled()
{
    var dir = TempDir.Create();
    var prior = Jobs.StallTimeout;
    Jobs.StallTimeout = TimeSpan.FromMilliseconds(150);
    Job? job = null;
    try
    {
        var rel = LiveRelease.New(version: "611.02");
        var initial = rel.DownloadUrl!;
        var handler = new SilentPeerHandler(uri => uri.AbsoluteUri == initial
            ? RecordingRedirectHandler.Redirect("https://uk.download.nvidia.com/hop2/driver.exe")
            : null);
        job = Jobs.StartRealDownload(rel, dir, handler);
        var first = await Task.WhenAny(job.Completion!, Task.Delay(TimeSpan.FromSeconds(3)));

        Assert.True(ReferenceEquals(first, job.Completion),
            $"job still '{job.Status}' after 3 s with a 150 ms stall window (redirect hop is unbounded)");
        Assert.Equal("failed", job.Status);
        Assert.Contains("stall", job.Error ?? "", StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
        if (job != null) { Jobs.Cancel(job.Id); await job.Completion!; }
        Jobs.StallTimeout = prior;
    }
}

// Consequence pin: a wedged version must not be handed back to a retry forever.
[Fact]
public async Task StartRealDownload_WedgedHeaders_RetryReturnsSameWedgedJob()
{
    var dir = TempDir.Create();
    var prior = Jobs.StallTimeout;
    Jobs.StallTimeout = TimeSpan.FromMilliseconds(150);
    Job? first = null;
    try
    {
        first = Jobs.StartRealDownload(LiveRelease.New(version: "611.03"), dir, new SilentPeerHandler());
        await Task.Delay(TimeSpan.FromSeconds(1)); // well past the stall window
        var retry = Jobs.StartRealDownload(LiveRelease.New(version: "611.03"), dir,
            DownloadHandler.Bytes(new byte[64 * 1024]));

        Assert.NotEqual(first.Id, retry.Id);
    }
    finally
    {
        if (first != null) { Jobs.Cancel(first.Id); await first.Completion!; }
        Jobs.StallTimeout = prior;
    }
}
```

Run (real repo):
```
dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj --filter "FullyQualifiedName~RealDownloadTests.StartRealDownload_PeerNeverSendsHeaders_FailsAsStalled|FullyQualifiedName~RealDownloadTests.StartRealDownload_RedirectHopNeverSendsHeaders_FailsAsStalled|FullyQualifiedName~RealDownloadTests.StartRealDownload_WedgedHeaders_RetryReturnsSameWedgedJob"
```

Captured RED on unmodified HEAD (Linux net10.0 harness, class `DownloadHeadersPhaseHasNoStallTimeoutRepro`, same bodies):
```
Failed ...StartRealDownload_RedirectHopNeverSendsHeaders_FailsAsStalled [3 s]
  Error Message:
   job still 'running' after 3 s with a 150 ms stall window (redirect hop is unbounded)
Failed ...StartRealDownload_WedgedHeaders_RetryReturnsSameWedgedJob [1 s]
  Error Message:
   Assert.NotEqual() Failure: Strings are equal
Expected: Not "2"
Actual:       "2"
Failed ...StartRealDownload_PeerNeverSendsHeaders_FailsAsStalled [3 s]
  Error Message:
   job still 'running' after 3 s with a 150 ms stall window (headers phase is unbounded)
Failed!  - Failed:     3, Passed:     0, Skipped:     0, Total:     3
```

### B. Live — running app (if applicable)
Not applicable with `--mock-catalog`. `ShouldRealDownload` only takes the real path for a `Source == "live"` release, and the initial-host and per-hop `.nvidia.com` checks rule out pointing the app at a local silent server. A live repro would need either a live catalog behind a proxy that holds HTTPS responses, or a hosts-file redirect of `us.download.nvidia.com` to a silent TLS listener. Both are outside the "no network beyond localhost" rule for repros. The unit test drives the exact production code path (`StartRealDownload` → `GetFollowingRedirectsAsync`); only the transport is injected, as CONTRIBUTING.md requires for network logic.

## Root cause
The engine's liveness rule ("no single wait may exceed `StallTimeout`") is enforced at one await only, the body `ReadAsync`. The other unbounded wait in the pipeline is `http.GetAsync(..., ResponseHeadersRead, ct)` in `GetFollowingRedirectsAsync`, run once per hop. Its only bound is the user's cancel token. The comment at `Jobs.cs:157-159` treats `ResponseHeadersRead` as a bound, but it only selects when `GetAsync` returns.

## Remedy
Give each hop's headers phase the same stall window, linked to the cancel token, so existing classification works unchanged. A stall-caused cancellation is not tied to `cancelCts`, so it reaches the generic `catch` and becomes `"download stalled"`.

```diff
     for (int redirects = 0; ; redirects++)
     {
-        var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
+        // Liveness: the connect/TLS/headers wait of every hop gets the same stall window
+        // as each body read (the only other unbounded await in the pipeline).
+        HttpResponseMessage resp;
+        using (var headers = CancellationTokenSource.CreateLinkedTokenSource(ct))
+        {
+            headers.CancelAfter(StallTimeout);
+            resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, headers.Token);
+        }
         int code = (int)resp.StatusCode;
```
Also correct the comment at `Jobs.cs:157-159` so it describes both bounds.

- **Why minimal:** a single seam, the existing `StallTimeout` knob (no new UI knob, §4), and no change to error strings: `download stalled` is the error users already know. The linked CTS is disposed as soon as headers arrive. With `ResponseHeadersRead`, SocketsHttpHandler drops its registration on the send token once headers are parsed, so the body stream is not tied to `headers` and long downloads are unaffected. Body liveness stays with the per-read window.
- **Validated:** with this patch applied to a scratch copy of `Jobs.cs`, all three reds turn green. The full suite stays green: 151 existing tests plus all repro tests, 163/163 on the Linux harness.
- **Validated against real sockets:** the fake handler cannot show this, so a scratch console tested a real `SocketsHttpHandler` against a local `TcpListener`. Headers came immediately, then eight 1 KiB chunks 100 ms apart, under a 150 ms headers CTS. Results:
  - `body complete: 8192 bytes` when the CTS is disposed after `GetAsync`.
  - `body complete: 8192 bytes` even when the CTS is left to fire mid-body.
  - `TaskCanceledException` for a peer that never sends headers.

  So the bound does not leak into the body stream, and the engine's generic `catch` maps the silent-peer case to `download stalled`.
- **Alternatives rejected:** setting `HttpClient.Timeout` would cap the whole download, and installers legitimately take minutes. `SocketsHttpHandler.ConnectTimeout` covers TCP+TLS but not the headers wait, and does not apply to the injected handler the tests use. A separate "headers timeout" knob means a new constant to justify, so reusing `StallTimeout` matches the existing semantics ("no single wait longer than the stall window").
- **Fences:** safety boundary untouched (nothing executed). No deletion change. On stall, the `.part` file is never created because the failure happens before `FileStream` opens, and the existing `TryDelete` path is unchanged. UI contract untouched. Back-compat pins: `StartRealDownload_Stall_FailsWithStalledMessage`, the HARD-05 redirect tests and `RedirectChainExceedingCap_FailsWithCapError` must stay green unchanged. The cap test's request count of `MaxRedirectHops + 1` is unaffected.

## TDD fix workflow (CONTRIBUTING.md — strict red-first)
0. **Register gate.** The active register is `docs/security_register.md`, and no item there covers this. The owner pins a new finding ID first. Destination: **fold into GAP-S03**, which already rewrites this exact loop to require `https` on the initial URL and every redirect before contact. The liveness bound is a natural same-seam rider, but it must carry its own ID and AC, per §7 ("never silently folded"). If GAP-S03 is not next, it can go out as a standalone Wave C entry.
1. **Slice setup.** `git worktree add ../nvcleanclone-hdr-stall -b feat/download-headers-stall-timeout origin/main`
2. **Red.** Add `SilentPeerHandler` to `TestDownload.cs` and the three facts to `RealDownloadTests.cs` (§A). Run the single-test filter and confirm `job still 'running' after 3 s …` and `Expected: Not "N"`. Neither may be a compile error. Commit `"<ID> (red): headers phase of every hop is bounded by the stall window"`.
3. **Green.** Apply the `GetFollowingRedirectsAsync` diff and run the full suite. Commit `"<ID>: bound each hop's headers wait by StallTimeout"`.
4. **Refactor.** Fix the stale liveness comment (`Jobs.cs:157-159`) with the suite green. No behavior change.
5. **Gates.** `dotnet build nvcleanstall/CleanDriver.csproj`, `dotnet test nvcleanstall/CleanDriver.Tests/CleanDriver.Tests.csproj`, `git diff --check`.
6. **Live exercise.** On Windows with the live catalog, run `dotnet run --project nvcleanstall -- --headless`. Then `curl -s -X POST localhost:4780/api/download -H 'Content-Type: application/json' -d '{"version":"<latest live>"}'`, poll `/api/jobs/<id>` to `done`, and confirm the happy path is unchanged (the bound does not fire on a healthy CDN). If the reviewer can, also route through a local proxy that holds the response (e.g. `HTTPS_PROXY` to a listener that accepts and never answers), set the Windows-side environment so it crosses into the exe, and show `failed` / `download stalled` within about 30 s.
7. **PR.** Fill `.github/pull_request_template.md`. TDD table: one row per fact (initial-host stall, redirect-hop stall, retry not wedged), each with red and green output. Disclose that no existing test was edited, and that the deviation is "reuses StallTimeout for the headers phase". Include the live transcript, push, and never merge.

## Code-review workflow (docs/ai-review-workflow.html → docs/ai-fix-loop-workflow.html)
1. **CI triage.** `gh pr checks <n>`. `build-test-smoke` must be green before reading the diff.
2. **Isolated worktree.** `git worktree add ../nvcleanclone-review-hdr origin/feat/download-headers-stall-timeout`. Review range `origin/main...HEAD`.
3. **Re-run gates** on the reviewer's own checkout: build, full test, `git diff --check`.
4. **Finder pass.** Check each of these:
   - The bound applies to **every** `GetAsync` in the loop, not just the first. There is exactly one call site.
   - The linked CTS is disposed before the body streams. It must not be held across `ReadAsStreamAsync`/body reads, or a slow but live download would be cut at `StallTimeout`.
   - A user cancel during the headers wait still yields `cancelled`. The filter `when (cancelCts.IsCancellationRequested)` must still match, because the linked token's OCE propagates.
   - No new error string; the stall still says `download stalled`.
   - No `.part` is created on a headers-phase stall, and `ActiveDownloads` is released (finally).
   - The guard tokens in `NoExecutionGuardTests`/`NoRebootGuardTests` are not introduced. `Jobs.cs` must not contain `shutdown`, `System.Diagnostics`, etc.
   - Optionally run `/code-review` on the PR diff as the finder.
5. **Adversarial verify.** Throw these at the fix:
   - A peer that sends headers at `StallTimeout - ε` followed by a fast body. It must succeed.
   - A body that takes longer than `StallTimeout` in total but never pauses a single read past it. It must succeed; this proves the headers CTS was not leaked into the body.
   - Cancel at 50 % of the headers wait gives `cancelled`, not `stalled`.
   - A 5-hop chain where only hop 5 is silent fails as stalled, and request count is 5.
   - `MaxRedirectHops + 1` redirects still give `too many redirects`.
   - A retry after the stall gets a new job id.
6. **Verdict document.** APPROVE or REQUEST CHANGES, each finding marked CONFIRMED (reproduced by a failing test) or PLAUSIBLE.
7. **Feedback to builder.** On REQUEST CHANGES, write an implementer handoff. The fix loop runs: C0 Orient (branch, head, fence) → C1 reproduce the reviewer's red → C2 fix → C3 verify every AC below → C4 gates → C5 one `F<n>:` commit per finding → C6 closing report (finding → commit → proof) → C7 STOP.
8. **Report to owner.** Review before relay. The owner merges, nobody else.

## Acceptance criteria
1. A transport that never returns response headers on the initial request fails the job within `StallTimeout` (+ scheduling slack), with `Status == "failed"`, `Error` containing `stall`, and no file in `destDir`.
2. The same holds when the silent peer is a followed, on-allowlist redirect hop.
3. After such a stall, a new `StartRealDownload` for the same version returns a **new** job id.
4. A user cancel during the headers wait still ends `Status == "cancelled"`.
5. A download whose total duration exceeds `StallTimeout`, with no single read exceeding it, still completes `done`.
6. All existing `RealDownloadTests` (stall, cancel, size cap, redirect chain, off-allowlist, hop cap) pass unedited.
