using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CleanDriver.Lib;

public record GpuInfo
{
    public string Name { get; init; } = "";
    public string InstalledDriverVersion { get; init; } = "";
    public bool IsSimulated { get; init; }
    public string DetectedVia { get; init; } = "";
}

public static class Gpu
{
    private static GpuInfo? _cached;

    public static readonly GpuInfo Simulated = new()
    {
        Name = "GeForce RTX 4070",
        InstalledDriverVersion = "570.86",
        IsSimulated = true,
        DetectedVia = "simulated (no GPU query available)",
    };

    // NVIDIA reports e.g. 32.0.15.7086 through WMI; the marketing version is
    // the last five digits of the dot-stripped string, split 3.2 -> "570.86".
    public static string? MarketingVersion(string? wmiVersion)
    {
        var digits = Regex.Replace(wmiVersion ?? "", @"\.", "");
        if (digits.Length < 5) return null;
        return $"{digits[^5..^2]}.{digits[^2..]}";
    }

    public static GpuInfo Detect()
    {
        if (_cached != null) return _cached;
        _cached = QueryPowershell() ?? Simulated;
        return _cached;
    }

    private static GpuInfo? QueryPowershell()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("Get-CimInstance Win32_VideoController | ForEach-Object { $_.Name + '|' + $_.DriverVersion }");
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(5000)) { proc.Kill(); return null; }

            var line = output.Split('\n')
                .Select(s => s.Trim())
                .FirstOrDefault(s => Regex.IsMatch(s, "NVIDIA|GeForce|Quadro|RTX|GTX", RegexOptions.IgnoreCase));
            if (line == null) return null;

            var parts = line.Split('|');
            return new GpuInfo
            {
                Name = Regex.Replace(parts[0], @"^NVIDIA\s+", "", RegexOptions.IgnoreCase).Trim(),
                InstalledDriverVersion = MarketingVersion(parts.ElementAtOrDefault(1)) ?? parts.ElementAtOrDefault(1) ?? "unknown",
                IsSimulated = false,
                DetectedVia = "system query (WMI Win32_VideoController)",
            };
        }
        catch
        {
            return null;
        }
    }
}
