namespace CleanDriver.Lib;

// GAP-S01 — inert scaffold (red commit). Real validation lands in the green commit; here the
// methods do nothing and the caps carry their pinned defaults so the tests can size their data.
public static class PackageValidation
{
    public static int MaxComponents = 512;
    public static long MaxManifestBytes = 1L << 20;             // 1 MiB
    public static int MaxArchiveEntries = 4096;
    public static long MaxEntryBytes = 2L << 30;               // 2 GiB
    public static long MaxTotalUncompressedBytes = 4L << 30;   // 4 GiB

    public static void ValidateManifest(Manifest manifest) { }
    public static void ValidateVersion(string version) { }
}
