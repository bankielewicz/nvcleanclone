using System.Text.Json;
using System.Text.RegularExpressions;

namespace CleanDriver.Lib;

public static class Presets
{
    public static string PresetDir => Path.Combine(AppContext.BaseDirectory, "presets");

    private static string SafeName(string? name)
    {
        var s = Regex.Replace(name?.Trim() ?? "", "[^A-Za-z0-9 _-]", "");
        if (s.Length == 0) throw new InvalidDataException("preset name required");
        return s[..Math.Min(40, s.Length)];
    }

    public static List<object> List()
    {
        if (!Directory.Exists(PresetDir)) return new();
        return Directory.GetFiles(PresetDir, "*.json")
            .Select(f => JsonDocument.Parse(File.ReadAllText(f)).RootElement)
            .Select(e => (object)new
            {
                name = e.GetProperty("name").GetString(),
                savedAt = e.GetProperty("savedAt").GetString(),
            })
            .ToList();
    }

    public static object Save(string? name, Selection selection)
    {
        var n = SafeName(name);
        Directory.CreateDirectory(PresetDir);
        var preset = new { name = n, savedAt = DateTime.UtcNow.ToString("o"), selection };
        File.WriteAllText(Path.Combine(PresetDir, $"{n}.json"), JsonSerializer.Serialize(preset, Json.Web));
        return preset;
    }

    public static JsonElement? Load(string name)
    {
        var p = Path.Combine(PresetDir, $"{SafeName(name)}.json");
        if (!File.Exists(p)) return null;
        return JsonDocument.Parse(File.ReadAllText(p)).RootElement.Clone();
    }
}
