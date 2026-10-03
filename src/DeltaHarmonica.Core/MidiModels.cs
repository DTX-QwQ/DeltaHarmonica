namespace DeltaHarmonica.Core;

public sealed record MidiReadOptions
{
    /// <summary>MIDI channel 10 (zero-based channel 9) contains percussion.</summary>
    public bool IgnorePercussion { get; init; } = true;
    public int MaximumFileBytes { get; init; } = 64 * 1024 * 1024;
    public int MaximumEvents { get; init; } = 2_000_000;
}

public sealed record MidiNote(
    int TrackIndex, int Channel, int Pitch, int Velocity,
    TimeSpan Start, TimeSpan Duration, long StartTick, long DurationTicks)
{
    public TimeSpan End => Start + Duration;
}

public sealed record MidiTrackInfo(
    int Index, string Name, int NoteCount, IReadOnlyList<int> Channels,
    IReadOnlyList<int> Programs, TimeSpan Duration);

public sealed record MidiTempoChange(long Tick, int MicrosecondsPerQuarterNote, TimeSpan Time)
{
    public double BeatsPerMinute => 60_000_000d / MicrosecondsPerQuarterNote;
}

public sealed class MidiSong
{
    public required string Name { get; init; }
    public required int Format { get; init; }
    public required int TicksPerQuarterNote { get; init; }
    public required TimeSpan Duration { get; init; }
    public required IReadOnlyList<MidiNote> Notes { get; init; }
    public required IReadOnlyList<MidiTrackInfo> Tracks { get; init; }
    public required IReadOnlyList<MidiTempoChange> TempoChanges { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class MidiFormatException : IOException
{
    public MidiFormatException(string message) : base(message) { }
    public MidiFormatException(string message, Exception innerException) : base(message, innerException) { }
}
