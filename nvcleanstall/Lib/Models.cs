using System.Text.Json;
using System.Text.Json.Serialization;

namespace CleanDriver.Lib;

public record ComponentDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public int SizeMB { get; init; }
    public bool Required { get; init; }
    public bool Recommended { get; init; }
    public List<string> DependsOn { get; init; } = new();
    public string Payload { get; init; } = "";
    public string Description { get; init; } = "";
}

public record Manifest
{
    public string Version { get; init; } = "";
    public string Channel { get; init; } = "";
    public List<ComponentDef> Components { get; init; } = new();
}

public record Release
{
    public string Version { get; init; } = "";
    public string Channel { get; init; } = "";
    public string ReleaseDate { get; init; } = "";
    public int SizeMB { get; init; }
}

public record Selection
{
    public List<string> Components { get; init; } = new();
    public Dictionary<string, JsonElement> Tweaks { get; init; } = new();

    public bool TweakOn(string id) =>
        Tweaks.TryGetValue(id, out var v) && v.ValueKind == JsonValueKind.True;

    public string? TweakString(string id) =>
        Tweaks.TryGetValue(id, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public record PackageSource
{
    public string Kind { get; init; } = ""; // catalog | local-dir | local-zip
    public string? Dir { get; init; }
    public string? ZipPath { get; init; }
}

public static class Json
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}
