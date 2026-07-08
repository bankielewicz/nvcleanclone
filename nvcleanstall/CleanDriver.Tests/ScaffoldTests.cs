using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// PF-1: proves the test harness builds against the app assembly and runs green.
// Asserts an existing pure function so the scaffold references real production code.
public class ScaffoldTests
{
    [Fact]
    public void MarketingVersion_DerivesFromWmiString()
    {
        Assert.Equal("570.86", Gpu.MarketingVersion("32.0.15.7086"));
    }
}
