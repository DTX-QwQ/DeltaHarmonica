using System.Text.Json;
using DeltaHarmonica.App.Models;
using DeltaHarmonica.Core;

internal static class LibrarySettingsTests
{
    public static Task Persistence()
    {
        WithStore((store, _) =>
        {
            var first = Path.GetFullPath("library/first.mid");
            var second = Path.GetFullPath("library/second.mid");
            var saved = new AppSettings
            {
                Speed = 1.4,
                Playlist = [second, first],
                LibraryCategories = [new("practice", "练习", false), new("favorites", "收藏")],
                SongMetadata = new()
                {
                    ["library/../library/first.mid"] = new("  改名后的曲目  ", "PRACTICE"),
                    [second] = new(null, "favorites")
                },
                UncategorizedExpanded = false,
                UncategorizedIndex = 1,
                SongRanges = new() { [first] = new(2.5, 12) }
            };
            store.Save(saved);
            var loaded = store.Load();
            Check(saved.Playlist.SequenceEqual(loaded.Playlist), "The library save changed the requested song order.");
            Equal(1.4, loaded.Speed);
            Equal(false, loaded.UncategorizedExpanded);
            Equal(1, loaded.UncategorizedIndex);
            Check(saved.LibraryCategories.SequenceEqual(loaded.LibraryCategories), "Categories changed order or expansion state.");
            Equal(new SongLibraryMetadata("改名后的曲目", "practice"), loaded.SongMetadata[first.ToUpperInvariant()]);
            Equal(new SongLibraryMetadata(null, "favorites"), loaded.SongMetadata[second.ToLowerInvariant()]);
            Equal(new SongPlaybackRange(2.5, 12), loaded.SongRanges[first.ToUpperInvariant()]);
            loaded.SongMetadata[first.ToLowerInvariant()] = new("再次改名", "favorites");
            Equal(2, loaded.SongMetadata.Count);
            loaded = loaded with { LibraryCategories = [loaded.LibraryCategories[1], loaded.LibraryCategories[0]], UncategorizedIndex = 2 };
            store.Save(loaded);
            var reordered = store.Load();
            Equal("再次改名", reordered.SongMetadata[first].DisplayName!);
            Equal(2, reordered.UncategorizedIndex);
            Check(reordered.LibraryCategories.Select(category => category.Id).SequenceEqual(new[] { "favorites", "practice" }), "Reordered categories were not persisted.");
            Equal(false, reordered.LibraryCategories[1].IsExpanded);
            Check(reordered.Playlist.SequenceEqual(saved.Playlist), "Persisting category positions unexpectedly rewrote the saved playlist.");
            Equal(new SongPlaybackRange(2.5, 12), reordered.SongRanges[first]);

            var direct = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(saved))!;
            Equal("改名后的曲目", direct.SongMetadata[first.ToUpperInvariant()].DisplayName!);
            Equal(1, direct.UncategorizedIndex);
        });
        return Task.CompletedTask;
    }

    public static Task Migration()
    {
        WithStore((store, settingsPath) =>
        {
            File.WriteAllText(settingsPath, "{\"Speed\":1.5,\"Playlist\":[\"old.mid\"]}");
            var old = store.Load();
            Equal(1.5, old.Speed);
            Equal(0, old.LibraryCategories.Count);
            Equal(0, old.SongMetadata.Count);
            Equal(true, old.UncategorizedExpanded);
            Equal(0, old.UncategorizedIndex);
            var path = Path.GetFullPath("library/valid.mid");
            old.SongMetadata[path] = new("兼容旧曲库");
            Check(old.SongMetadata.ContainsKey(path.ToUpperInvariant()), "Legacy settings lack a case-insensitive metadata dictionary.");

            foreach (var malformed in new[] { "null", "[]", "42", "\"invalid\"", "true" })
            {
                var rangeJson = JsonSerializer.Serialize(new Dictionary<string, SongPlaybackRange> { [path] = new(1, 9) });
                File.WriteAllText(settingsPath,
                    "{\"Speed\":1.75,\"SongRanges\":" + rangeJson + ",\"LibraryCategories\":" + malformed +
                    ",\"SongMetadata\":" + malformed + ",\"UncategorizedExpanded\":" + malformed + ",\"UncategorizedIndex\":" + malformed + "}");
                var loaded = store.Load();
                Equal(1.75, loaded.Speed);
                Equal(new SongPlaybackRange(1, 9), loaded.SongRanges[path]);
                Equal(0, loaded.LibraryCategories.Count);
                Equal(0, loaded.SongMetadata.Count);
                Equal(true, loaded.UncategorizedExpanded);
                Equal(0, loaded.UncategorizedIndex);
            }

            foreach (var (value, expected) in new[] { ("-5", 0), ("99", 2), ("1", 1), ("1.5", 0), ("2147483648", 0), ("{}", 0), ("\"1\"", 0) })
            {
                File.WriteAllText(settingsPath, "{\"Speed\":1.6,\"LibraryCategories\":[{\"Id\":\"first\",\"Name\":\"收藏\"},{\"Id\":\"second\",\"Name\":\"练习\"}],\"UncategorizedIndex\":" + value + "}");
                var loaded = store.Load();
                Equal(expected, loaded.UncategorizedIndex);
                Equal(1.6, loaded.Speed);
                Equal(2, loaded.LibraryCategories.Count);
            }

            var json = JsonSerializer.Serialize(new
            {
                Speed = 1.25,
                LibraryCategories = new object?[] { null, 5, "bad", new { Id = "valid", Name = "收藏", IsExpanded = "broken" } },
                SongMetadata = new Dictionary<string, object?>
                {
                    [path] = new { DisplayName = "保留曲名", CategoryId = "valid" },
                    ["name-invalid.mid"] = new { DisplayName = 42, CategoryId = "valid" },
                    ["category-invalid.mid"] = new { DisplayName = "名称仍可用", CategoryId = new[] { 1 } },
                    ["null.mid"] = null,
                    ["wrong.mid"] = "bad",
                    ["\0bad.mid"] = new { DisplayName = "无效路径", CategoryId = "valid" }
                },
                SongRanges = new Dictionary<string, SongPlaybackRange> { [path] = new(3, 8) }
            });
            File.WriteAllText(settingsPath, json);
            var repaired = store.Load();
            Equal(1.25, repaired.Speed);
            Equal(1, repaired.LibraryCategories.Count);
            Equal(true, repaired.LibraryCategories[0].IsExpanded);
            Equal(3, repaired.SongMetadata.Count);
            Equal(new SongLibraryMetadata("保留曲名", "valid"), repaired.SongMetadata[path]);
            Equal(new SongLibraryMetadata(null, "valid"), repaired.SongMetadata[Path.GetFullPath("name-invalid.mid")]);
            Equal(new SongLibraryMetadata("名称仍可用"), repaired.SongMetadata[Path.GetFullPath("category-invalid.mid")]);
            Equal(new SongPlaybackRange(3, 8), repaired.SongRanges[path]);
            store.Save(repaired);
            Equal(3, store.Load().SongMetadata.Count);
        });
        return Task.CompletedTask;
    }

    public static Task Normalization()
    {
        var first = Path.GetFullPath("library/first.mid");
        var normalized = LibrarySettingsNormalizer.Normalize(new AppSettings
        {
            LibraryCategories = [new(" practice ", "  练习  ", false), new("PRACTICE", "练习"), new("", " "), null!],
            UncategorizedIndex = 50,
            SongMetadata = new()
            {
                [first] = new("first version", "practice"),
                [first.ToUpperInvariant()] = new(" new version ", "PRACTICE"),
                ["orphan.mid"] = new("保留未分类曲目", "missing-category"),
                ["unnamed.mid"] = new(" \t\r\n ", ""),
                ["null.mid"] = null!,
                ["\0bad.mid"] = new("invalid path")
            }
        });
        Equal(3, normalized.LibraryCategories.Count);
        Equal(3, normalized.UncategorizedIndex);
        Equal("practice", normalized.LibraryCategories[0].Id);
        Equal("练习", normalized.LibraryCategories[0].Name);
        Equal(false, normalized.LibraryCategories[0].IsExpanded);
        Equal("练习（2）", normalized.LibraryCategories[1].Name);
        Equal("未命名分类", normalized.LibraryCategories[2].Name);
        Equal(3, normalized.LibraryCategories.Select(category => category.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Check(normalized.LibraryCategories.All(category => !string.IsNullOrWhiteSpace(category.Id)), "A repaired category has an empty ID.");
        Equal(3, normalized.SongMetadata.Count);
        Equal(new SongLibraryMetadata("new version", "practice"), normalized.SongMetadata[first]);
        Equal(new SongLibraryMetadata("保留未分类曲目"), normalized.SongMetadata[Path.GetFullPath("orphan.mid")]);
        Equal(new SongLibraryMetadata(), normalized.SongMetadata[Path.GetFullPath("unnamed.mid")]);
        var repeated = LibrarySettingsNormalizer.Normalize(normalized);
        Check(normalized.LibraryCategories.SequenceEqual(repeated.LibraryCategories), "Normalization changes repaired category identities.");
        Equal(normalized.SongMetadata[first], repeated.SongMetadata[first]);
        Equal(3, repeated.UncategorizedIndex);
        Equal(0, LibrarySettingsNormalizer.Normalize(normalized with { UncategorizedIndex = -1 }).UncategorizedIndex);
        return Task.CompletedTask;
    }

    public static Task Notifications()
    {
        var song = new MidiSong
        {
            Name = "原始曲名", Format = 1, TicksPerQuarterNote = 480, Duration = TimeSpan.FromSeconds(4),
            Notes = [], Tracks = [], TempoChanges = []
        };
        var entry = new SongEntry("original.mid", song);
        var changes = new List<string?>();
        entry.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        Equal(song.Name, entry.DisplayName);
        entry.DisplayName = " 新曲名 ";
        entry.DisplayName = "新曲名";
        Equal(1, changes.Count(name => name == nameof(SongEntry.DisplayName)));
        Equal("新曲名", entry.ToString());
        Equal("原始曲名", song.Name);
        Equal("original.mid", entry.Path);
        entry.DisplayName = " ";
        Equal(song.Name, entry.DisplayName);
        var category = new SongCategory("practice", "练习");
        var categoryChanges = new List<string?>();
        category.PropertyChanged += (_, args) => categoryChanges.Add(args.PropertyName);
        category.Songs.Add(entry);
        entry.CategoryId = category.Id;
        Equal(1, category.Count);
        Equal("练习 · 1 首", category.Summary);
        category.IsExpanded = false;
        category.Name = "慢速练习";
        Check(categoryChanges.Contains(nameof(SongCategory.IsExpanded)), "Collapsing a category did not notify bindings.");
        Check(categoryChanges.Contains(nameof(SongCategory.Name)), "Renaming a category did not notify bindings.");
        Check(changes.Contains(nameof(SongEntry.CategoryId)), "Moving a song did not notify bindings.");
        category.Songs.Remove(entry);
        Equal(0, category.Count);
        Equal(2, categoryChanges.Count(name => name == nameof(SongCategory.Count)));
        Equal("慢速练习 · 0 首", category.Summary);
        Equal(3, categoryChanges.Count(name => name == nameof(SongCategory.Summary)));
        return Task.CompletedTask;
    }

    private static void WithStore(Action<SettingsStore, string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DeltaHarmonicaLibrarySettings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { test(new SettingsStore(directory), Path.Combine(directory, "settings.json")); }
        finally
        {
            File.Delete(Path.Combine(directory, "settings.json"));
            File.Delete(Path.Combine(directory, "settings.json.tmp"));
            Directory.Delete(directory);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal<T>(T expected, T actual) where T : notnull
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
    }
}
