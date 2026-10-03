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
            var loaded = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new()
                : new AppSettings();
            return loaded with { Hotkeys = loaded.Hotkeys ?? new(), Playlist = loaded.Playlist ?? [] };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}
