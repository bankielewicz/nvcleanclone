using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace CleanDriver.Lib;

// GAP-S02a (SEC-02): the pure predicates behind the private-HTTP-boundary middleware. Each is
// unit-tested; the middleware that wires them into the request pipeline (Program.cs) is verified
// by live curl transcript, the HARD-03/05 live-only-glue precedent.
public static class HttpSecurity
{
    public const string TokenHeader = "X-CleanDriver-Token";

    private static readonly char[] Separators = { ';', ' ' };

    // Loopback-only bind (pin 2): every url in the (semicolon/space-separated) bind list must
    // target a loopback host. A single non-loopback entry taints the whole set; an empty list
    // is not a valid loopback bind.
    public static bool IsLoopbackBind(string urls)
    {
        var parts = (urls ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts)
        {
            if (!Uri.TryCreate(part, UriKind.Absolute, out var u) || !IsLoopbackHost(u.Host))
                return false;
        }
        return true;
    }

    // Origin-matching (pin 7): a present Origin on a mutating request must equal one of the
    // server's own loopback origins — a loopback host at the bound scheme+port. Foreign host,
    // wrong port, wrong scheme, or opaque ("null") → not allowed.
    public static bool IsAllowedOrigin(string origin, string boundUrls)
    {
        if (string.IsNullOrEmpty(origin) || !Uri.TryCreate(origin, UriKind.Absolute, out var o))
            return false;
        if (!IsLoopbackHost(o.Host)) return false;
        foreach (var part in (boundUrls ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Uri.TryCreate(part, UriKind.Absolute, out var b)
                && string.Equals(o.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
                && o.Port == b.Port)
                return true;
        }
        return false;
    }

    // Constant-time token comparison (pin 1). A missing/empty token never matches; unequal
    // lengths return false without an early-out.
    public static bool TokenMatches(string? provided, string expected)
    {
        if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(expected)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
    }

    private static bool IsLoopbackHost(string host)
    {
        host = host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
