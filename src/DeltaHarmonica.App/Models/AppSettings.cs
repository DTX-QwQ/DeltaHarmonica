using System.Text.Json;
using DeltaHarmonica.App.Services;

namespace DeltaHarmonica.App.Models;

public sealed record AppSettings
{
    public double Speed { get; init; } = 1;
    public int BaseMidiNote { get; init; } = 60;
    public int Transpose { get; init; }
    public int LowerOffset { get; init; } = -12;
    public int UpperOffset { get; init; } = 12;
    public int SemitoneOffset { get; init; } = 1;
    public int CountdownSeconds { get; init; } = 5;
    public bool FoldOctaves { get; init; } = true;
    public int ChordMode { get; init; }
    public bool AutoNext { get; init; }
    public string PythonPath { get; init; } = "";
    public string AudioOutputDirectory { get; init; } = "";
    public string AudioPreset { get; init; } = "balanced";
    public string AudioMelodyMode { get; init; } = "highest";
    public string AudioQuantization { get; init; } = "none";
    public HotkeySettings Hotkeys { get; init; } = new();
    public List<string> Playlist { get; init; } = [];
}

public sealed class SettingsStore(string directory)
{
    public string DirectoryPath { get; } = directory;
    private string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public AppSettings Load()
    {
        try
        {
            var json = File.Exists(FilePath) ? File.ReadAllText(FilePath) : null;
            var loaded = json is null ? new AppSettings() : JsonSerializer.Deserialize<AppSettings>(json) ?? new();
            var hotkeys = loaded.Hotkeys ?? new();
            if (json is not null) hotkeys = AddMissingStartHotkey(json, hotkeys);
            return loaded with
            {
                Hotkeys = hotkeys, Playlist = loaded.Playlist ?? [],
                AudioPreset = loaded.AudioPreset is "solo" or "balanced" or "ensemble" ? loaded.AudioPreset : "balanced",
                AudioMelodyMode = loaded.AudioMelodyMode is "highest" or "smart" or "polyphonic" ? loaded.AudioMelodyMode : "highest",
                AudioQuantization = loaded.AudioQuantization is "none" or "1/4" or "1/8" or "1/16" ? loaded.AudioQuantization : "none"
            };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }

    private static HotkeySettings AddMissingStartHotkey(string json, HotkeySettings hotkeys)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty(nameof(AppSettings.Hotkeys), out var savedHotkeys) ||
            savedHotkeys.ValueKind != JsonValueKind.Object || savedHotkeys.TryGetProperty(nameof(HotkeySettings.Start), out _))
            return hotkeys;

        // Older versions allowed F5 for other actions. Preserve those bindings during the upgrade.
        foreach (var key in new[] { "F5" }.Concat(Enumerable.Range(10, 15).Select(number => $"F{number}")))
        {
            var candidate = hotkeys with { Start = key };
            if (GlobalHotkeyService.Validate(candidate) is null) return candidate;
        }
        return hotkeys;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}
