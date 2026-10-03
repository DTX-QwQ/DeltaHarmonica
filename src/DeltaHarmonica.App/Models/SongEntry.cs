using DeltaHarmonica.Core;

namespace DeltaHarmonica.App.Models;

public sealed record SongEntry(string Path, MidiSong Song)
{
    public string DisplayName => Song.Name;
    public string Detail => $"{Song.Duration:mm\\:ss} · {Song.Notes.Count} 音符 · {Song.Tracks.Count} 音轨";
    public override string ToString() => DisplayName;
}

public sealed record TrackChoice(int? Index, string Label)
{
    public override string ToString() => Label;
}
