using System;
using System.Collections.Generic;
using System.Linq;
using CleanDriver.Lib;
using Xunit;

namespace CleanDriver.Tests;

// GAP-S02a — the pure, headless-testable core of the HTTP boundary: crypto ids (SEC-02 pin 4),
// the loopback-bind / origin / token predicates (pins 1,2,7), and the bounded expiring store
// (BUG-05 pin 5). The middleware WIRING that composes these is verified by live curl/browser
// transcript (no HTTP integration harness in this repo — the HARD-03/05 live-only precedent).
public class HttpBoundaryTests
{
    // ---- crypto ids (AC-5) -------------------------------------------------------
    [Fact]
    public void Ids_AreCryptoRandom_UniqueAndNonSequential()
    {
        var a = Ids.NewId();
        var b = Ids.NewId();
        Assert.NotEqual(a, b);
        Assert.Equal(32, a.Length);                 // 128-bit hex
        Assert.Matches("^[0-9A-Fa-f]{32}$", a);
        Assert.NotEqual("1", a);
        Assert.NotEqual("2", b);
        // 500 ids, all distinct — no counter, no collisions.
        var many = Enumerable.Range(0, 500).Select(_ => Ids.NewId()).ToHashSet();
        Assert.Equal(500, many.Count);
    }

    [Fact]
    public void Ids_Token_Is256BitHex()
    {
        var t = Ids.NewToken();
        Assert.Equal(64, t.Length);                 // 256-bit hex
        Assert.Matches("^[0-9A-Fa-f]{64}$", t);
        Assert.NotEqual(Ids.NewToken(), t);
    }

    // ---- loopback bind predicate (AC-3) ------------------------------------------
    [Theory]
    [InlineData("http://localhost:4780")]
    [InlineData("http://127.0.0.1:4780")]
    [InlineData("http://[::1]:4780")]
    [InlineData("http://localhost:4780;http://127.0.0.1:5000")]   // every url must be loopback
    public void IsLoopbackBind_TrueForLoopback(string urls) =>
        Assert.True(HttpSecurity.IsLoopbackBind(urls));

    [Theory]
    [InlineData("http://0.0.0.0:4780")]
    [InlineData("http://*:4780")]
    [InlineData("http://192.168.1.10:4780")]
    [InlineData("http://evil.example.com:4780")]
    [InlineData("http://localhost:4780;http://0.0.0.0:5000")]     // one non-loopback taints the set
    [InlineData("")]
    public void IsLoopbackBind_FalseForNonLoopback(string urls) =>
        Assert.False(HttpSecurity.IsLoopbackBind(urls));

    // ---- origin predicate (AC-8) -------------------------------------------------
    [Theory]
    [InlineData("http://localhost:4780")]
    [InlineData("http://127.0.0.1:4780")]
    [InlineData("http://[::1]:4780")]
    public void IsAllowedOrigin_TrueForLoopbackAtBoundSchemePort(string origin) =>
        Assert.True(HttpSecurity.IsAllowedOrigin(origin, "http://localhost:4780"));

    [Theory]
    [InlineData("http://evil.example.com")]           // foreign host
    [InlineData("http://evil.example.com:4780")]       // foreign host, right port
    [InlineData("http://localhost:9999")]              // loopback, wrong port
    [InlineData("https://localhost:4780")]             // wrong scheme
    [InlineData("null")]                               // opaque origin
    public void IsAllowedOrigin_FalseForForeignOrMismatched(string origin) =>
        Assert.False(HttpSecurity.IsAllowedOrigin(origin, "http://localhost:4780"));

    // ---- token compare (AC-1) ----------------------------------------------------
    [Fact]
    public void TokenMatches_OnlyForExactToken()
    {
        var t = Ids.NewToken();
        Assert.True(HttpSecurity.TokenMatches(t, t));
        Assert.False(HttpSecurity.TokenMatches(null, t));
        Assert.False(HttpSecurity.TokenMatches("", t));
        Assert.False(HttpSecurity.TokenMatches("wrong", t));
        Assert.False(HttpSecurity.TokenMatches(t + "x", t));   // length mismatch, constant-time false
    }

    // ---- bounded store (BUG-05 pin 5, AC-6) --------------------------------------
    private static BoundedStore<string> Store(int max, TimeSpan retention, Func<DateTimeOffset> clock,
        Func<string, bool>? pinned = null) =>
        new() { MaxEntries = max, Retention = retention, Clock = clock, Pinned = pinned ?? (_ => false) };

    [Fact]
    public void BoundedStore_Cap_EvictsOldestUnpinned()
    {
        var t = DateTimeOffset.UnixEpoch;
        var s = Store(2, TimeSpan.FromHours(1), () => t);
        s.Add("a", "A"); t = t.AddSeconds(1);
        s.Add("b", "B"); t = t.AddSeconds(1);
        s.Add("c", "C");                          // over cap → oldest ("a") evicted
        Assert.False(s.TryGet("a", out _));
        Assert.True(s.TryGet("b", out _));
        Assert.True(s.TryGet("c", out _));
        Assert.Equal(2, s.Count);
    }

    // The BUG-05 landmine: a pinned (running) entry is never evicted by the cap, even when it
    // is the oldest — an unpinned one goes instead.
    [Fact]
    public void BoundedStore_Cap_NeverEvictsPinned_EvenWhenOldest()
    {
        var t = DateTimeOffset.UnixEpoch;
        var pinned = new HashSet<string> { "a" };
        var s = Store(2, TimeSpan.FromHours(1), () => t, k => pinned.Contains(k));
        s.Add("a", "A"); t = t.AddSeconds(1);     // oldest, but pinned
        s.Add("b", "B"); t = t.AddSeconds(1);
        s.Add("c", "C");                          // over cap → "b" (oldest UNPINNED) evicted, "a" stays
        Assert.True(s.TryGet("a", out _), "pinned oldest survives the cap");
        Assert.False(s.TryGet("b", out _));
        Assert.True(s.TryGet("c", out _));
    }

    // If everything at the cap is pinned, the cap is necessarily soft — a new entry exceeds it
    // rather than a running entry being killed (the only reading consistent with the pin).
    [Fact]
    public void BoundedStore_Cap_IsSoftWhenAllPinned()
    {
        var t = DateTimeOffset.UnixEpoch;
        var s = Store(1, TimeSpan.FromHours(1), () => t, _ => true);
        s.Add("a", "A"); t = t.AddSeconds(1);
        s.Add("b", "B");
        Assert.True(s.TryGet("a", out _));
        Assert.True(s.TryGet("b", out _));
        Assert.Equal(2, s.Count);                 // exceeds MaxEntries=1: soft cap, nothing pinned killed
    }

    [Fact]
    public void BoundedStore_Expiry_RemovesStaleUnpinned_KeepsPinnedAndFresh()
    {
        var t = DateTimeOffset.UnixEpoch;
        var pinned = new HashSet<string> { "old-pinned" };
        var s = Store(100, TimeSpan.FromMinutes(30), () => t, k => pinned.Contains(k));
        s.Add("old-unpinned", "U");
        s.Add("old-pinned", "P");
        t = t.AddMinutes(31);                     // both now stale by age
        s.Add("fresh", "F");                      // Add triggers eviction at the new time
        Assert.False(s.TryGet("old-unpinned", out _), "stale unpinned expired");
        Assert.True(s.TryGet("old-pinned", out _), "stale but pinned survives");
        Assert.True(s.TryGet("fresh", out _));
    }

    [Fact]
    public void BoundedStore_GetAndValues_Work()
    {
        var s = Store(10, TimeSpan.FromHours(1), () => DateTimeOffset.UnixEpoch);
        s.Add("k", "V");
        Assert.Equal("V", s.Get("k"));
        Assert.Null(s.Get("missing"));
        Assert.Contains("V", s.Values);
    }
}
