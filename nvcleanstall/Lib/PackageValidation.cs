using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace CleanDriver.Lib;

// GAP-S01 (SEC-01 path traversal, SEC-04 archive/memory exhaustion): the single server-side
// validator that confines and bounds every untrusted manifest/package value before any path is
// derived from it. Applied by the folder and zip loads (Packages.LoadLocal / ReadManifest), at
// the sample-version relabel (LoadSampleTemplate), and at the prior-build delete
// (Packages.CleanPreviousBuild) — so the six filesystem sinks are neutralized at one place, not
// patched individually.
public static class PackageValidation
{
    // Overridable bounds (the Jobs.StallTimeout idiom) — defaults are the register's GAP-S01
    // pins. Tests set a tiny cap to exercise the SEC-04 limits that cannot be materialized.
    public static int MaxComponents = 512;
    public static long MaxManifestBytes = 1L << 20;            // 1 MiB
    public static int MaxArchiveEntries = 4096;
    public static long MaxEntryBytes = 2L << 30;               // 2 GiB
    public static long MaxTotalUncompressedBytes = 4L << 30;   // 4 GiB

    // Stable, path-free rejection messages. Api.cs surfaces InvalidDataException.Message as an
    // HTTP 400 body, so none of these interpolate an untrusted value or a filesystem path
    // (watchpoint 5). The offending value is never echoed — it could itself be a path.
    public const string BadVersion = "manifest rejected: version is not a valid driver version";
    public const string BadComponentId = "manifest rejected: component id is not allowed";
    public const string BadPayload = "manifest rejected: component payload is not a safe filename";
    public const string DuplicateId = "manifest rejected: duplicate component id";
    public const string DuplicatePayload = "manifest rejected: duplicate component payload";
    public const string MissingDependency = "manifest rejected: dependency target not found";
    public const string TooManyComponents = "manifest rejected: too many components";
    public const string ManifestTooLarge = "manifest rejected: manifest is too large";
    public const string TooManyEntries = "package rejected: archive has too many entries";
    public const string EntryTooLarge = "package rejected: archive entry is too large";
    public const string ArchiveTooLarge = "package rejected: archive is too large";
    public const string PayloadMissing = "package rejected: a selected payload is missing";
    public const string UnsafePath = "package rejected: a path escaped its intended directory";
    public const string ReparsePoint = "package rejected: a path crosses a reparse point";

    // Current NVIDIA driver version format: dotted decimals only. Every dot is followed by a
    // digit, so a separator or `..` cannot appear — accepts 572.16 / 610.74, rejects the attacks.
    private static readonly Regex VersionRe = new(@"^\d{1,5}(\.\d{1,5}){1,3}$", RegexOptions.Compiled);
    private static readonly Regex ComponentIdRe = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex PayloadRe = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.Compiled);

    public static void ValidateVersion(string version)
    {
        if (string.IsNullOrEmpty(version) || !VersionRe.IsMatch(version))
            throw new InvalidDataException(BadVersion);
    }

    // A payload is a single safe leaf filename: allowlisted characters only, and — belt to the
    // regex's suspenders — equal to its own GetFileName, no separator, no parent, never rooted.
    public static bool IsSafeLeafPayload(string payload) =>
        !string.IsNullOrEmpty(payload)
        && PayloadRe.IsMatch(payload)
        && payload != "." && payload != ".."
        && !payload.Contains('/') && !payload.Contains('\\')
        && Path.GetFileName(payload) == payload
        && !Path.IsPathRooted(payload);

    public static void ValidateManifest(Manifest manifest)
    {
        if (manifest.Components.Count > MaxComponents)
            throw new InvalidDataException(TooManyComponents);
        ValidateVersion(manifest.Version);

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var payloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in manifest.Components)
        {
            if (string.IsNullOrEmpty(c.Id) || !ComponentIdRe.IsMatch(c.Id))
                throw new InvalidDataException(BadComponentId);
            if (!IsSafeLeafPayload(c.Payload))
                throw new InvalidDataException(BadPayload);
            if (!ids.Add(c.Id)) throw new InvalidDataException(DuplicateId);
            if (!payloads.Add(c.Payload)) throw new InvalidDataException(DuplicatePayload);
        }
        // Every dependency edge must resolve to a declared component.
        foreach (var c in manifest.Components)
            foreach (var dep in c.DependsOn)
                if (!ids.Contains(dep)) throw new InvalidDataException(MissingDependency);
    }

    public static void ValidateManifestBytes(long byteCount)
    {
        if (byteCount > MaxManifestBytes) throw new InvalidDataException(ManifestTooLarge);
    }

    // SEC-04: bound a loaded archive by entry count, per-entry uncompressed size, and total
    // uncompressed size, using the declared central-directory Lengths as a cheap up-front
    // reject. The streaming copy (Packages.CopyPayload) additionally caps bytes actually read,
    // so an entry whose local header lies about its size still cannot exceed the per-entry cap.
    public static void ValidateArchiveBounds(ZipArchive zip)
    {
        if (zip.Entries.Count > MaxArchiveEntries) throw new InvalidDataException(TooManyEntries);
        long total = 0;
        foreach (var e in zip.Entries)
        {
            if (e.Length > MaxEntryBytes) throw new InvalidDataException(EntryTooLarge);
            total += e.Length;
            if (total > MaxTotalUncompressedBytes) throw new InvalidDataException(ArchiveTooLarge);
        }
    }

    // Canonicalize Path.Combine(root, leaf) and require it stays under `root`, rejecting any
    // reparse point along the traversal from `root` to the target. Path.GetFullPath collapses
    // ../. but does NOT resolve reparse points, so the reparse check is a separate, explicit
    // gate: a junction can redirect an in-bounds name to an out-of-bounds target.
    public static string EnsureConfinedRealPath(string root, string leaf)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(Path.Combine(fullRoot, leaf));
        var rel = Path.GetRelativePath(fullRoot, full);
        if (rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(rel))
            throw new InvalidDataException(UnsafePath);
        RejectReparsePointsUnder(fullRoot, full);
        return full;
    }

    // Walk from the target up to (and including everything below) `root`; reject if any existing
    // directory or the target is a reparse point. A non-existent leaf is fine — the writer
    // creates it fresh inside a confined, reparse-free chain.
    private static void RejectReparsePointsUnder(string root, string full)
    {
        for (var cur = full;
             !string.IsNullOrEmpty(cur) && !string.Equals(cur, root, StringComparison.OrdinalIgnoreCase);
             cur = Path.GetDirectoryName(cur) ?? "")
        {
            if ((File.Exists(cur) || Directory.Exists(cur))
                && File.GetAttributes(cur).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(ReparsePoint);
        }
    }

    // O(buffer) streaming copy: memory is independent of payload size by construction (a fixed
    // 80 KiB buffer, whatever the payload length), and the running total caps bytes at `max`.
    public static long BoundedCopy(Stream src, Stream dest, long max)
    {
        var buffer = new byte[81920];
        long total = 0;
        int n;
        while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += n;
            if (total > max) throw new InvalidDataException(EntryTooLarge);
            dest.Write(buffer, 0, n);
        }
        return total;
    }
}
