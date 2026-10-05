using System.Text.Json;
using System.Text.Json.Serialization;
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
    [JsonConverter(typeof(SongRangesJsonConverter))]
    public Dictionary<string, SongPlaybackRange> SongRanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Saved positions in original MIDI seconds; a null end means the entire remaining song.</summary>
public sealed record SongPlaybackRange(double StartSeconds = 0, double? EndSeconds = null);

internal sealed class SongRangesJsonConverter : JsonConverter<Dictionary<string, SongPlaybackRange>>
{
    public override Dictionary<string, SongPlaybackRange> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var ranges = new Dictionary<string, SongPlaybackRange>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return ranges;
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var start = 0d;
            double? end = null;
            if (entry.Value.TryGetProperty(nameof(SongPlaybackRange.StartSeconds), out var savedStart) &&
                (savedStart.ValueKind != JsonValueKind.Number || !savedStart.TryGetDouble(out start))) continue;
            if (entry.Value.TryGetProperty(nameof(SongPlaybackRange.EndSeconds), out var savedEnd) && savedEnd.ValueKind != JsonValueKind.Null)
            {
                if (savedEnd.ValueKind != JsonValueKind.Number || !savedEnd.TryGetDouble(out var seconds)) continue;
                end = seconds;
            }
            AddValidRange(ranges, entry.Name, new(start, end));
        }
        return ranges;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, SongPlaybackRange> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (path, range) in Normalize(value))
        {
            writer.WritePropertyName(path);
            JsonSerializer.Serialize(writer, range, options);
        }
        writer.WriteEndObject();
    }

    public static Dictionary<string, SongPlaybackRange> Normalize(Dictionary<string, SongPlaybackRange>? source)
    {
        var ranges = new Dictionary<string, SongPlaybackRange>(StringComparer.OrdinalIgnoreCase);
        if (source is not null)
            foreach (var (path, range) in source) AddValidRange(ranges, path, range);
        return ranges;
    }

    private static void AddValidRange(Dictionary<string, SongPlaybackRange> ranges, string path, SongPlaybackRange? range)
    {
        if (string.IsNullOrWhiteSpace(path) || range is null || !double.IsFinite(range.StartSeconds) || range.StartSeconds < 0 ||
            range.EndSeconds is { } end && (!double.IsFinite(end) || end <= range.StartSeconds)) return;
        try { ranges[Path.GetFullPath(path)] = range; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
    }
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
                Hotkeys = hotkeys, Playlist = loaded.Playlist ?? [], SongRanges = SongRangesJsonConverter.Normalize(loaded.SongRanges),
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
        File.WriteAllText(temp, JsonSerializer.Serialize(settings with { SongRanges = SongRangesJsonConverter.Normalize(settings.SongRanges) },
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}
