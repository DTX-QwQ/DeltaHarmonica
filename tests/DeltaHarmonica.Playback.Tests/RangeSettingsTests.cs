using System.Text.Json;
using DeltaHarmonica.App.Models;
using DeltaHarmonica.Core;

internal static class RangeSettingsTests
{
    public static Task Persistence()
    {
        WithStore((store, settingsPath) =>
        {
            var first = Path.GetFullPath("library/first.mid");
            var second = Path.GetFullPath("library/second.mid");
            var settings = new AppSettings
            {
                Speed = 1.25,
                Playlist = [first, second],
                SongRanges = new()
                {
                    [first] = new(2.125, 15.75),
                    [second] = new(3.5)
                }
            };
            store.Save(settings);
            var restored = store.Load();
            Equal(2, restored.SongRanges.Count);
            Equal(settings.SongRanges[first], restored.SongRanges[first.ToUpperInvariant()]);
            Equal(settings.SongRanges[second], restored.SongRanges[second.ToLowerInvariant()]);
            Equal(settings.Speed, restored.Speed);
            Check(settings.Playlist.SequenceEqual(restored.Playlist), "The range save changed the playlist.");
            var fullEnd = PlaybackRange.Normalize(restored.SongRanges[second].StartSeconds,
                restored.SongRanges[second].EndSeconds, TimeSpan.FromSeconds(30));
            Equal(TimeSpan.FromSeconds(30), fullEnd.End);
            fullEnd = PlaybackRange.Normalize(restored.SongRanges[second].StartSeconds,
                restored.SongRanges[second].EndSeconds, TimeSpan.FromSeconds(60));
            Equal(TimeSpan.FromSeconds(60), fullEnd.End);

            // Direct serialization also retains the dictionary comparer and canonicalizes relative input keys.
            var serialized = JsonSerializer.Serialize(settings with
            {
                SongRanges = new() { ["library/../library/first.mid"] = new(4, 12) }
            });
            var deserialized = JsonSerializer.Deserialize<AppSettings>(serialized)!;
            Equal(new SongPlaybackRange(4, 12), deserialized.SongRanges[first.ToUpperInvariant()]);
            deserialized.SongRanges[first.ToLowerInvariant()] = new(5, 13);
            Equal(1, deserialized.SongRanges.Count);
            Check(File.Exists(settingsPath), "Settings were not written.");
        });
        return Task.CompletedTask;
    }

    public static Task Migration()
    {
        WithStore((store, settingsPath) =>
        {
            var path = Path.GetFullPath("library/valid.mid");
            foreach (var ranges in new[] { "null", "[]", "42", "\"invalid\"" })
            {
                File.WriteAllText(settingsPath, "{\"Speed\":1.5,\"SongRanges\":" + ranges + "}");
                var loaded = store.Load();
                Equal(1.5, loaded.Speed);
                Equal(0, loaded.SongRanges.Count);
            }
            File.WriteAllText(settingsPath, "{\"Speed\":1.5,\"Playlist\":[\"old.mid\"]}");
            var old = store.Load();
            Equal(1.5, old.Speed);
            Equal(0, old.SongRanges.Count);
            old.SongRanges[path] = new(1, 2);
            Check(old.SongRanges.ContainsKey(path.ToUpperInvariant()), "Old settings did not gain a case-insensitive range dictionary.");

            var entries = new Dictionary<string, object?>
            {
                [path] = new { StartSeconds = 2.5, EndSeconds = 7.25 },
                ["null.mid"] = null,
                ["text.mid"] = "bad",
                ["negative.mid"] = new { StartSeconds = -1, EndSeconds = 3 },
                ["reversed.mid"] = new { StartSeconds = 5, EndSeconds = 3 },
                ["zero.mid"] = new { StartSeconds = 3, EndSeconds = 3 },
                ["string.mid"] = new { StartSeconds = "NaN", EndSeconds = "Infinity" },
                ["\0bad.mid"] = new { StartSeconds = 1, EndSeconds = 2 }
            };
            var json = JsonSerializer.Serialize(new { Speed = 1.75, SongRanges = entries });
            // Syntactically valid JSON can still contain a double outside the finite range.
            var originalJson = json;
            json = json.Replace("\"string.mid\":{\"StartSeconds\":\"NaN\",\"EndSeconds\":\"Infinity\"}",
                "\"string.mid\":{\"StartSeconds\":1e400,\"EndSeconds\":2}", StringComparison.Ordinal);
            Check(json != originalJson, "The nonfinite JSON-number fixture was not installed.");
            File.WriteAllText(settingsPath, json);
            var normalized = store.Load();
            Equal(1.75, normalized.Speed);
            Equal(1, normalized.SongRanges.Count);
            Equal(new SongPlaybackRange(2.5, 7.25), normalized.SongRanges[path.ToUpperInvariant()]);
            // A bad in-memory entry cannot break serialization or discard the valid song's range.
            normalized.SongRanges[Path.GetFullPath("nonfinite.mid")] = new(double.NaN, double.PositiveInfinity);
            store.Save(normalized);
            Equal(1, store.Load().SongRanges.Count);
        });
        return Task.CompletedTask;
    }

    public static Task Normalization()
    {
        var duration = TimeSpan.FromSeconds(20);
        var whole = new PlaybackRange(TimeSpan.Zero, duration);
        Equal(new PlaybackRange(TimeSpan.FromSeconds(2.125), TimeSpan.FromSeconds(18.75)),
            PlaybackRange.Normalize(2.125, 18.75, duration));
        Equal(new PlaybackRange(TimeSpan.FromSeconds(2), duration), PlaybackRange.Normalize(2, 30, duration));
        Equal(new PlaybackRange(TimeSpan.FromSeconds(2), duration), PlaybackRange.Normalize(2, null, duration));
        Equal(new PlaybackRange(TimeSpan.FromTicks(1), TimeSpan.FromTicks(3)),
            PlaybackRange.Normalize(TimeSpan.FromTicks(1).TotalSeconds, TimeSpan.FromTicks(3).TotalSeconds, TimeSpan.FromTicks(4)));
        foreach (var (start, end) in new (double, double?)[]
        {
            (double.NaN, 18), (double.PositiveInfinity, 18), (double.NegativeInfinity, 18),
            (2, double.NaN), (2, double.PositiveInfinity), (2, double.NegativeInfinity),
            (-1, 10), (10, 10), (10, 9), (25, 40), (20, null)
        }) Equal(whole, PlaybackRange.Normalize(start, end, duration));
        Equal(new PlaybackRange(TimeSpan.Zero, TimeSpan.Zero), PlaybackRange.Normalize(2, 10, TimeSpan.Zero));
        Equal(new PlaybackRange(TimeSpan.Zero, TimeSpan.Zero), PlaybackRange.Normalize(2, 10, TimeSpan.FromSeconds(-1)));
        return Task.CompletedTask;
    }

    public static Task AudibleRange()
    {
        var plan = new PlaybackPlan
        {
            Duration = TimeSpan.FromSeconds(60),
            // Deliberately unsorted: the trim follows the earliest start and latest release, not list order.
            Chords = [Chord(20, 25), Chord(5.125, 4), Chord(18, 2)]
        };
        Check(PlaybackRange.TryGetAudibleRange(plan, out var trimmed), "Playable notes produced no automatic range.");
        Equal(new PlaybackRange(TimeSpan.FromSeconds(5.125), TimeSpan.FromSeconds(45)), trimmed);
        var empty = new PlaybackPlan { Duration = plan.Duration, Chords = [] };
        Check(!PlaybackRange.TryGetAudibleRange(empty, out _), "An empty plan should not be trimmed.");
        var silent = new PlaybackPlan { Duration = plan.Duration, Chords = [Chord(1, 2) with { Notes = [] }, Chord(5, 0)] };
        Check(!PlaybackRange.TryGetAudibleRange(silent, out _), "Zero-length or empty chords are not playable notes.");
        var clipped = new PlaybackPlan { Duration = TimeSpan.FromSeconds(10), Chords = [Chord(-2, 4), Chord(8, 5), Chord(20, 3)] };
        Check(PlaybackRange.TryGetAudibleRange(clipped, out trimmed), "Intersecting note spans were lost.");
        Equal(new PlaybackRange(TimeSpan.Zero, TimeSpan.FromSeconds(10)), trimmed);
        var changedMapping = new PlaybackPlan { Duration = plan.Duration, Chords = [Chord(18, 2)] };
        Check(PlaybackRange.TryGetAudibleRange(changedMapping, out trimmed), "The selected mapped plan did not produce a range.");
        Equal(new PlaybackRange(TimeSpan.FromSeconds(18), TimeSpan.FromSeconds(20)), trimmed);
        return Task.CompletedTask;
    }

    private static ScheduledChord Chord(double start, double duration) => new(TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(duration),
        [new MappedNote('z', HarmonicaRegister.Normal, false, 60, 60, 60, false)], HarmonicaRegister.Normal, false, ['z']);

    private static void WithStore(Action<SettingsStore, string> test)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DeltaHarmonicaRangeSettings-" + Guid.NewGuid().ToString("N"));
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
