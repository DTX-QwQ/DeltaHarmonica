namespace DeltaHarmonica.Core;

public sealed record ScheduledChord(
    TimeSpan Start, TimeSpan Duration, IReadOnlyList<MappedNote> Notes,
    HarmonicaRegister Register, bool Semitone, IReadOnlyList<char> RetriggerKeys)
{
    public TimeSpan End => Start + Duration;
}

public sealed class PlaybackPlan
{
    public required TimeSpan Duration { get; init; }
    public required IReadOnlyList<ScheduledChord> Chords { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public int SkippedNoteCount { get; init; }
    public int FoldedNoteCount { get; init; }
    public int IncompatibleNoteCount { get; init; }
}

/// <summary>Creates non-overlapping physical-key intervals with one global mouse modifier state.</summary>
public static class PlaybackPlanBuilder
{
    public static PlaybackPlan Build(MidiSong song, MappingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        options ??= new MappingOptions();
        options.Validate();
        var selectedTracks = options.SelectedTracks?.ToHashSet();
        var warnings = new List<string>(song.Warnings);
        var mapped = new List<PlannedNote>();
        var skipped = 0;
        var folded = 0;
        foreach (var source in song.Notes)
        {
            if (selectedTracks is not null && !selectedTracks.Contains(source.TrackIndex)) continue;
            if (source.Duration <= TimeSpan.Zero) continue;
            if (!HarmonicaMapper.TryMap(source.Pitch, options, out var note))
            {
                skipped++;
                continue;
            }
            if (note!.WasOctaveFolded) folded++;
            mapped.Add(new PlannedNote(mapped.Count, source, note, HarmonicaMapper.GetAlternatives(note, options)));
        }

        var boundaries = new SortedDictionary<long, List<Boundary>>();
        foreach (var note in mapped)
        {
            AddBoundary(note.Source.Start.Ticks, new Boundary(note, true));
            AddBoundary(note.Source.End.Ticks, new Boundary(note, false));
        }
        void AddBoundary(long tick, Boundary boundary)
        {
            if (!boundaries.TryGetValue(tick, out var list)) boundaries[tick] = list = [];
            list.Add(boundary);
        }

        var times = boundaries.Keys.ToArray();
        var active = new Dictionary<int, PlannedNote>();
        var incompatibleIds = new HashSet<int>();
        var chords = new List<ScheduledChord>();
        for (var i = 0; i + 1 < times.Length; i++)
        {
            var time = times[i];
            // Note-offs occur before note-ons at the same timestamp, allowing a clean retrigger.
            foreach (var boundary in boundaries[time].Where(b => !b.IsStart)) active.Remove(boundary.Note.Id);
            var starts = boundaries[time].Where(b => b.IsStart).Select(b => b.Note).ToArray();
            foreach (var start in starts) active[start.Id] = start;
            if (active.Count == 0 || times[i + 1] == time) continue;

            IReadOnlyList<(PlannedNote Source, MappedNote Physical)> picked;
            if (options.ChordPolicy == ChordPolicy.Compatible)
            {
                picked = SelectCompatible(active.Values);
                var retained = picked.Select(n => n.Source.Id).ToHashSet();
                foreach (var omitted in active.Values.Where(n => !retained.Contains(n.Id)))
                    incompatibleIds.Add(omitted.Id);
            }
            else
            {
                var melody = options.ChordPolicy == ChordPolicy.Highest
                    ? active.Values.OrderByDescending(n => n.Source.Pitch).ThenByDescending(n => n.Source.Velocity)
                        .ThenBy(n => n.Id).First()
                    : active.Values.OrderBy(n => n.Source.Pitch).ThenByDescending(n => n.Source.Velocity)
                        .ThenBy(n => n.Id).First();
                picked = [(melody, melody.Mapped)];
            }

            var physicalNotes = picked.Select(n => n.Physical).DistinctBy(n => n.Key)
                .OrderBy(n => HarmonicaMapper.Keys.IndexOf(n.Key)).ToArray();
            var retrigger = picked.Where(n => n.Source.Source.Start.Ticks == time)
                .Select(n => n.Physical.Key).Distinct().ToArray();
            var first = physicalNotes[0];
            var chord = new ScheduledChord(TimeSpan.FromTicks(time), TimeSpan.FromTicks(times[i + 1] - time),
                physicalNotes, first.Register, first.Semitone, retrigger);
            if (chords.Count > 0 && CanMerge(chords[^1], chord))
                chords[^1] = chords[^1] with { Duration = chords[^1].Duration + chord.Duration };
            else
                chords.Add(chord);
        }

        if (skipped > 0) warnings.Add($"{skipped} 个音符超出可演奏音域，已按设置跳过。");
        if (folded > 0) warnings.Add($"{folded} 个音符已按八度折回音域，保留原音名。");
        if (incompatibleIds.Count > 0)
            warnings.Add($"{incompatibleIds.Count} 个音符在部分重叠时间内需要不同鼠标状态，已保留同一鼠标状态下最多的和弦音。");
        if (chords.Count == 0) warnings.Add("当前音轨和映射设置下没有可演奏音符。");
        return new PlaybackPlan
        {
            Duration = song.Duration, Chords = chords, Warnings = warnings,
            SkippedNoteCount = skipped, FoldedNoteCount = folded, IncompatibleNoteCount = incompatibleIds.Count
        };
    }

    private static bool CanMerge(ScheduledChord previous, ScheduledChord next) =>
        previous.End == next.Start && previous.Register == next.Register && previous.Semitone == next.Semitone &&
        next.RetriggerKeys.Count == 0 && previous.Notes.Select(n => n.Key).SequenceEqual(next.Notes.Select(n => n.Key));

    private static IReadOnlyList<(PlannedNote Source, MappedNote Physical)> SelectCompatible(IEnumerable<PlannedNote> active)
    {
        // A pitch may have alternative key positions at the edge of an octave.
        // Consider every physical representation before discarding incompatible tones.
        var groups = active.SelectMany(note => note.Alternatives.Select(physical => (Source: note, Physical: physical)))
            .GroupBy(n => (n.Physical.Register, n.Physical.Semitone))
            .Select(group => new
            {
                group.Key,
                Notes = group.DistinctBy(n => n.Source.Id).ToArray(),
                UniqueKeys = group.Select(n => n.Physical.Key).Distinct().Count(),
                Highest = group.Max(n => n.Source.Source.Pitch)
            })
            .OrderByDescending(group => group.UniqueKeys)
            .ThenByDescending(group => group.Notes.Length)
            .ThenByDescending(group => group.Highest)
            .ThenBy(group => HarmonicaMapper.ModifierCount(group.Key.Register, group.Key.Semitone))
            .First();
        return groups.Notes;
    }

    private sealed record PlannedNote(int Id, MidiNote Source, MappedNote Mapped, IReadOnlyList<MappedNote> Alternatives);
    private sealed record Boundary(PlannedNote Note, bool IsStart);
}
