namespace CleanDriver.Lib;

// GAP-S02a — inert scaffold (red commit). Real predicates land in green.
public static class HttpSecurity
{
    public const string TokenHeader = "X-CleanDriver-Token";

    public static bool IsLoopbackBind(string urls) => true;
    public static bool IsAllowedOrigin(string origin, string boundUrls) => true;
    public static bool TokenMatches(string? provided, string expected) => true;
}
