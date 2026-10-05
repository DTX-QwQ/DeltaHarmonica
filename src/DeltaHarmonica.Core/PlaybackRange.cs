namespace DeltaHarmonica.Core;

/// <summary>An absolute, half-open interval on the original MIDI timeline.</summary>
public readonly record struct PlaybackRange(TimeSpan Start, TimeSpan End)
{
    /// <summary>Restores a saved range, clamping it to the current song or falling back to the whole song.</summary>
    public static PlaybackRange Normalize(double startSeconds, double? endSeconds, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return new(TimeSpan.Zero, TimeSpan.Zero);
        var whole = new PlaybackRange(TimeSpan.Zero, duration);
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            endSeconds is { } savedEnd && (!double.IsFinite(savedEnd) || savedEnd <= startSeconds))
            return whole;

        if (startSeconds >= duration.TotalSeconds) return whole;
        var start = TimeSpan.FromSeconds(startSeconds);
        // A null end follows the current file duration if the MIDI is subsequently replaced or extended.
        var end = endSeconds is { } endValue
            ? endValue >= duration.TotalSeconds ? duration : TimeSpan.FromSeconds(endValue)
            : duration;
        return start < end ? new(start, end) : whole;
    }

    /// <summary>Finds the first playable onset and the final held-note release in the current mapped plan.</summary>
    public static bool TryGetAudibleRange(PlaybackPlan plan, out PlaybackRange range)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var start = plan.Duration;
        var end = TimeSpan.Zero;
        foreach (var chord in plan.Chords)
        {
            if (chord.Notes.Count == 0 || chord.Duration <= TimeSpan.Zero) continue;
            var chordStart = chord.Start < TimeSpan.Zero ? TimeSpan.Zero : chord.Start;
            var chordEnd = chord.End > plan.Duration ? plan.Duration : chord.End;
            if (chordStart >= chordEnd) continue;
            if (chordStart < start) start = chordStart;
            if (chordEnd > end) end = chordEnd;
        }
        range = start < end ? new(start, end) : new(TimeSpan.Zero, TimeSpan.Zero);
        return start < end;
    }
}
