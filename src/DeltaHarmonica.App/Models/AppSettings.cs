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
    [JsonConverter(typeof(LibraryCategoriesJsonConverter))]
    public List<LibraryCategorySettings> LibraryCategories { get; init; } = [];
    [JsonConverter(typeof(SongMetadataJsonConverter))]
    public Dictionary<string, SongLibraryMetadata> SongMetadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonConverter(typeof(ExpandedStateJsonConverter))]
    public bool UncategorizedExpanded { get; init; } = true;
    [JsonConverter(typeof(CategoryIndexJsonConverter))]
    public int UncategorizedIndex { get; init; }
    [JsonConverter(typeof(SongRangesJsonConverter))]
    public Dictionary<string, SongPlaybackRange> SongRanges { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Saved positions in original MIDI seconds; a null end means the entire remaining song.</summary>
public sealed record SongPlaybackRange(double StartSeconds = 0, double? EndSeconds = null);

public sealed record LibraryCategorySettings(string Id = "", string Name = "", bool IsExpanded = true);
public sealed record SongLibraryMetadata(string? DisplayName = null, string CategoryId = "");

/// <summary>Repairs library data independently of playback preferences and saved song ranges.</summary>
public static class LibrarySettingsNormalizer
{
    public static AppSettings Normalize(AppSettings settings)
    {
        var categories = NormalizeCategories(settings.LibraryCategories);
        var categoryIds = categories.ToDictionary(category => category.Id, StringComparer.OrdinalIgnoreCase);
        var metadata = NormalizeMetadata(settings.SongMetadata);
        foreach (var (path, song) in metadata.ToArray())
        {
            var id = categoryIds.TryGetValue(song.CategoryId, out var category) ? category.Id : "";
            metadata[path] = song with { CategoryId = id };
        }
        return settings with { LibraryCategories = categories, SongMetadata = metadata,
            UncategorizedIndex = Math.Clamp(settings.UncategorizedIndex, 0, categories.Count) };
    }

    public static List<LibraryCategorySettings> NormalizeCategories(IEnumerable<LibraryCategorySettings>? source)
    {
        var categories = new List<LibraryCategorySettings>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return categories;
        foreach (var category in source)
        {
            if (category is null) continue;
            var id = CleanText(category.Id) ?? "";
            if (id.Length == 0 || !ids.Add(id))
            {
                do { id = Guid.NewGuid().ToString("N"); } while (!ids.Add(id));
            }
            var baseName = CleanText(category.Name) ?? "未命名分类";
            var name = baseName;
            for (var suffix = 2; !names.Add(name); suffix++) name = $"{baseName}（{suffix}）";
            categories.Add(new(id, name, category.IsExpanded));
        }
        return categories;
    }

    public static Dictionary<string, SongLibraryMetadata> NormalizeMetadata(Dictionary<string, SongLibraryMetadata>? source)
    {
        var metadata = new Dictionary<string, SongLibraryMetadata>(StringComparer.OrdinalIgnoreCase);
        if (source is null) return metadata;
        foreach (var (path, song) in source)
        {
            if (song is null || !TryNormalizePath(path, out var fullPath)) continue;
            metadata[fullPath] = new(CleanText(song.DisplayName), CleanText(song.CategoryId) ?? "");
        }
        return metadata;
    }

    public static bool TryNormalizePath(string? path, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { fullPath = Path.GetFullPath(path); return true; }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = new string(value.Where(character => !char.IsControl(character)).ToArray()).Trim();
        return clean.Length == 0 ? null : clean;
    }
}

internal sealed class CategoryIndexJsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.ValueKind == JsonValueKind.Number && document.RootElement.TryGetInt32(out var index)
            ? Math.Max(0, index) : 0;
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options) => writer.WriteNumberValue(Math.Max(0, value));
}

internal sealed class LibraryCategoriesJsonConverter : JsonConverter<List<LibraryCategorySettings>>
{
    public override bool HandleNull => true;
    public override List<LibraryCategorySettings> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var categories = new List<LibraryCategorySettings>();
        if (document.RootElement.ValueKind != JsonValueKind.Array) return categories;
        foreach (var category in document.RootElement.EnumerateArray())
        {
            if (category.ValueKind != JsonValueKind.Object) continue;
            var id = ReadText(category, nameof(LibraryCategorySettings.Id)) ?? "";
            var name = ReadText(category, nameof(LibraryCategorySettings.Name)) ?? "";
            var expanded = !category.TryGetProperty(nameof(LibraryCategorySettings.IsExpanded), out var savedExpanded) ||
                savedExpanded.ValueKind != JsonValueKind.False;
            categories.Add(new(id, name, expanded));
        }
        return LibrarySettingsNormalizer.NormalizeCategories(categories);
    }

    internal static string? ReadText(JsonElement element, string name) =>
        element.TryGetProperty(name, out var saved) && saved.ValueKind == JsonValueKind.String ? saved.GetString() : null;

    public override void Write(Utf8JsonWriter writer, List<LibraryCategorySettings> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var category in LibrarySettingsNormalizer.NormalizeCategories(value)) JsonSerializer.Serialize(writer, category, options);
        writer.WriteEndArray();
    }
}

internal sealed class SongMetadataJsonConverter : JsonConverter<Dictionary<string, SongLibraryMetadata>>
{
    public override bool HandleNull => true;
    public override Dictionary<string, SongLibraryMetadata> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var metadata = new Dictionary<string, SongLibraryMetadata>(StringComparer.OrdinalIgnoreCase);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return metadata;
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var name = LibraryCategoriesJsonConverter.ReadText(entry.Value, nameof(SongLibraryMetadata.DisplayName));
            var categoryId = LibraryCategoriesJsonConverter.ReadText(entry.Value, nameof(SongLibraryMetadata.CategoryId)) ?? "";
            if (LibrarySettingsNormalizer.TryNormalizePath(entry.Name, out var path)) metadata[path] = new(name, categoryId);
        }
        return LibrarySettingsNormalizer.NormalizeMetadata(metadata);
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, SongLibraryMetadata> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (path, song) in LibrarySettingsNormalizer.NormalizeMetadata(value))
        {
            writer.WritePropertyName(path);
            JsonSerializer.Serialize(writer, song, options);
        }
        writer.WriteEndObject();
    }
}

internal sealed class ExpandedStateJsonConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.ValueKind != JsonValueKind.False;
    }
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

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
            return LibrarySettingsNormalizer.Normalize(loaded with
            {
                Hotkeys = hotkeys, Playlist = loaded.Playlist ?? [], SongRanges = SongRangesJsonConverter.Normalize(loaded.SongRanges),
                AudioPreset = loaded.AudioPreset is "solo" or "balanced" or "ensemble" ? loaded.AudioPreset : "balanced",
                AudioMelodyMode = loaded.AudioMelodyMode is "highest" or "smart" or "polyphonic" ? loaded.AudioMelodyMode : "highest",
                AudioQuantization = loaded.AudioQuantization is "none" or "1/4" or "1/8" or "1/16" ? loaded.AudioQuantization : "none"
            });
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
        File.WriteAllText(temp, JsonSerializer.Serialize(LibrarySettingsNormalizer.Normalize(settings with { SongRanges = SongRangesJsonConverter.Normalize(settings.SongRanges) }),
            new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}
