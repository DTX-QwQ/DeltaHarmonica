using System.ComponentModel;
using DeltaHarmonica.Core;

namespace DeltaHarmonica.App.Models;

public sealed class SongEntry(string Path, MidiSong Song) : INotifyPropertyChanged
{
    private string _displayName = Song.Name;
    private string _categoryId = "";

    public string Path { get; } = Path;
    public MidiSong Song { get; } = Song;
    public string DisplayName
    {
        get => _displayName;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? Song.Name : value.Trim();
            if (_displayName == normalized) return;
            _displayName = normalized;
            PropertyChanged?.Invoke(this, new(nameof(DisplayName)));
        }
    }

    public string CategoryId
    {
        get => _categoryId;
        set
        {
            var normalized = value?.Trim() ?? "";
            if (_categoryId == normalized) return;
            _categoryId = normalized;
            PropertyChanged?.Invoke(this, new(nameof(CategoryId)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string Detail => $"{Song.Duration:mm\\:ss} · {Song.Notes.Count} 音符 · {Song.Tracks.Count} 音轨";
    public override string ToString() => DisplayName;
}

public sealed record TrackChoice(int? Index, string Label)
{
    public override string ToString() => Label;
}
