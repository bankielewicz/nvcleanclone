using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// HARD-03 — Gpu.QueryWmi hang guard.
//
// The defect: QueryWmi called StandardOutput.ReadToEnd() *before* WaitForExit(5000). A
// wedged powershell that holds stdout open blocks ReadToEnd forever, so the 5s budget
// could never fire and the first /api/system call hung with it (and /api/catalog behind
// it — both route through Gpu.Detect()).
//
// The fix bounds the READ, not merely the wait, under ONE budget: Gpu.ReadBounded reads
// with a deadline, kills the process on either timeout, and returns null — which
// DetectFrom already maps to the Simulated fallback, exactly as the documented
// `WMI -> simulated` order promises. No GpuInfo field distinguishes "timed out" from
// "returned nothing"; today's WaitForExit-timeout path already returned null.
//
// These tests drive the pure seam only. Detect() is never called here: it shells out to
// real powershell and populates a static cache (the GpuDetectionTests idiom).
public class GpuTimeoutTests
{
    // A stdout stand-in for a wedged child that holds the pipe open: ReadToEnd blocks
    // until the reader is disposed. Disposal stands in for the pipe closing when the
    // real process is killed — which is what releases the abandoned read.
    private sealed class NeverClosingReader : TextReader
    {
        private readonly ManualResetEventSlim _released = new(false);

        public override int Read(char[] buffer, int index, int count)
        {
            _released.Wait();   // a wedged child: no bytes, no EOF
            return 0;           // released -> EOF
        }

        protected override void Dispose(bool disposing)
        {
            _released.Set();
            base.Dispose(disposing);
        }
    }

    // A stdout stand-in whose read costs real time, so the budget left for WaitForExit
    // is observable.
    private sealed class SlowReader : TextReader
    {
        private readonly TimeSpan _delay;
        private readonly string _content;
        private bool _done;

        public SlowReader(TimeSpan delay, string content) { _delay = delay; _content = content; }

        public override int Read(char[] buffer, int index, int count)
        {
            if (_done) return 0;
            Thread.Sleep(_delay);
            _done = true;
            _content.CopyTo(0, buffer, index, _content.Length);
            return _content.Length;
        }
    }

    private static Func<int, bool> ExitsImmediately => _ => true;

    // ---- HARD-03 AC-1 --------------------------------------------------------------
    // A never-closing stdout yields the Simulated fallback WITHIN the budget. The
    // assertion is on elapsed wall clock: against the unbounded ReadToEnd this test
    // hangs rather than fails, so the time bound is what makes the negative path real.
    //
    // waitForExit throws if reached: when the read times out there is nothing left to
    // wait for, and reaching it would mean a second budget stacked on the first.
    [Fact]
    public void ReadBounded_NeverClosingStdout_FallsBackToSimulatedWithinTheBudget()
    {
        using var stdout = new NeverClosingReader();
        var killed = 0;
        var sw = Stopwatch.StartNew();

        var output = Gpu.ReadBounded(
            stdout,
            _ => throw new InvalidOperationException("waitForExit must not be reached when the read times out"),
            () => Interlocked.Increment(ref killed),
            Gpu.WmiBudget);

        sw.Stop();

        Assert.Null(output);
        Assert.Equal(1, killed);                       // the wedged process is killed (watchpoint 3)
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10),
            $"the read must be bounded by the 5s budget; it took {sw.Elapsed.TotalSeconds:F1}s");

        // The register's pin: on timeout, fall back to simulated — the same GpuInfo a
        // failed/empty query has always produced. No new field, no new DetectedVia.
        var gpu = Gpu.DetectFrom(output);
        Assert.Equal(Gpu.Simulated, gpu);
        Assert.True(gpu.IsSimulated);
        Assert.Equal("simulated (no GPU query available)", gpu.DetectedVia);
    }

    // The budget really is the 5s the register calls "the existing 5s budget". A literal,
    // so production's own arithmetic cannot define the thing under test (rider #29).
    [Fact]
    public void WmiBudget_IsFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), Gpu.WmiBudget);
    }

    // ---- HARD-03 AC-2 --------------------------------------------------------------
    // The single-NVIDIA happy path is byte-identical: the same GpuInfo GpuDetectionTests
    // pins, reached through the new bounded read, with the process never killed.
    [Fact]
    public void ReadBounded_ProcessExits_ReturnsOutputVerbatim_AndNeverKills()
    {
        var killed = 0;
        const string wmi = "NVIDIA GeForce RTX 5070|32.0.15.7086";

        var output = Gpu.ReadBounded(new StringReader(wmi), ExitsImmediately, () => killed++, Gpu.WmiBudget);

        Assert.Equal(wmi, output);
        Assert.Equal(0, killed);

        var gpu = Gpu.DetectFrom(output);
        Assert.Equal("GeForce RTX 5070", gpu.Name);
        Assert.Equal("570.86", gpu.InstalledDriverVersion);
        Assert.False(gpu.IsSimulated);
        Assert.Equal("system query (WMI Win32_VideoController)", gpu.DetectedVia);
    }

    // Today's behavior, preserved: the read completes but the process never exits ->
    // kill it and fall back. This is the branch the old WaitForExit(5000) guarded.
    [Fact]
    public void ReadBounded_ProcessNeverExits_KillsAndFallsBackToSimulated()
    {
        var killed = 0;

        var output = Gpu.ReadBounded(new StringReader("x"), _ => false, () => killed++, Gpu.WmiBudget);

        Assert.Null(output);
        Assert.Equal(1, killed);
        Assert.Equal(Gpu.Simulated, Gpu.DetectFrom(output));
    }

    // ---- Watchpoint 4: one bound, not two ------------------------------------------
    // The time the read spent comes OUT of the budget. A stacked implementation would
    // hand WaitForExit a fresh 2000ms; a single-bounded one hands it what is left (~1400).
    [Fact]
    public void ReadBounded_SlowRead_LeavesOnlyTheRemainingBudgetForWaitForExit()
    {
        var budget = TimeSpan.FromMilliseconds(2000);
        int? waitMs = null;

        var output = Gpu.ReadBounded(
            new SlowReader(TimeSpan.FromMilliseconds(600), "out"),
            ms => { waitMs = ms; return true; },
            () => Assert.Fail("nothing timed out; kill must not be called"),
            budget);

        Assert.Equal("out", output);
        Assert.NotNull(waitMs);
        // A fresh-budget (stacked) implementation passes 2000 here and fails this bound.
        Assert.InRange(waitMs!.Value, 1, 1500);
    }
}
