using DeltaHarmonica.Core;

namespace DeltaHarmonica.App.Services;

/// <summary>Plays mapped game notes locally without sending keyboard or mouse input.</summary>
public interface IPreviewAudioSink
{
    /// <summary>An empty chord is a musical note-off: fade the voices without discarding queued audio.</summary>
    void SetChord(IReadOnlyList<MappedNote> notes, IReadOnlyList<char> retriggerKeys);
    /// <summary>Immediately clear voices and queued output for pause, seek, stop and cleanup.</summary>
    void ReleaseAll();
}
