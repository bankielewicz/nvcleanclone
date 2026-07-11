using System.Security.Cryptography;

namespace CleanDriver.Lib;

// GAP-S02a (SEC-02 pin 4): cryptographically-random identifiers, replacing the guessable
// Interlocked.Increment counters for session and job ids. Hex strings so existing string-keyed
// Get/TryGet lookups keep working unchanged.
public static class Ids
{
    // 128-bit session/job id — unguessable, collision-free at this scale.
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    // 256-bit per-launch API token.
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
