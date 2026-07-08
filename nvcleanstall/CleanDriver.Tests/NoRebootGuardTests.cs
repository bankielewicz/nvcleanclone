using System;
using System.IO;
using Xunit;

namespace CleanDriver.Tests;

// GAP-04 AC / FEAT-011 "never reboots" — shape-without-function safety guard: honoring
// the auto-reboot flag must NEVER invoke a real reboot/shutdown API. This is a SAFETY PIN
// (green from the start, same category as a back-compat pin): it locks an invariant, it
// does not drive new code. Complements NoExecutionGuardTests (no execution/extraction).
public class NoRebootGuardTests
{
    [Fact]
    public void JobsEngine_ContainsNoRebootOrShutdownApi()
    {
        var src = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "src", "Jobs.cs"));
        // Actual reboot/shutdown invokers on Windows. "reboot"/"RebootRecommended"/
        // "auto-reboot" are legitimate flag/field names and are intentionally NOT listed.
        foreach (var forbidden in new[] { "shutdown", "ExitWindowsEx", "InitiateSystemShutdown", "Process.Start" })
            Assert.DoesNotContain(forbidden, src, StringComparison.OrdinalIgnoreCase);
    }
}
