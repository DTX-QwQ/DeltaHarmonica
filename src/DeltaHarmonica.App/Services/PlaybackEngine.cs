using System.Diagnostics;
using DeltaHarmonica.Core;

namespace DeltaHarmonica.App.Services;

public enum PlaybackState { Stopped, Playing, Paused, Completed, Faulted }
public sealed record PlaybackSnapshot(PlaybackState State, TimeSpan Position, TimeSpan Duration, string Message, ScheduledChord? Chord);

/// <summary>Single monotonic clock; tempo changes are already resolved by the MIDI reader.</summary>
public sealed class PlaybackEngine(IInputSink input, IPreviewAudioSink? previewAudio = null) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private PlaybackPlan? _plan;
    private PlaybackState _state;
    private double _position;
    private double _startPosition;
    private double _endPosition;
    private double _speed = 1;
    private long _lastTick;
    private int _active = -1;
    private Func<bool>? _canSend;
    private bool _preview;
    private bool _disposed;
    private string _message = "就绪";
    private static readonly char[] NoteKeys = "zxcvbnm,".ToCharArray();
    public event Action<PlaybackSnapshot>? Updated;
    public PlaybackState State { get { lock (_gate) return _state; } }
    public TimeSpan Position { get { lock (_gate) return TimeSpan.FromSeconds(_position); } }
    public TimeSpan Duration { get { lock (_gate) return _plan?.Duration ?? TimeSpan.Zero; } }
    public TimeSpan StartPosition { get { lock (_gate) return TimeSpan.FromSeconds(_startPosition); } }
    public TimeSpan EndPosition { get { lock (_gate) return TimeSpan.FromSeconds(_endPosition); } }
    public double Speed
    {
        get { lock (_gate) return _speed; }
        set
        {
            if (!double.IsFinite(value) || value < .25 || value > 3) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); AdvanceClock(); _speed = value; }
        }
    }
    /// <summary>Loads a stopped plan and resets the selected range to the whole MIDI timeline.</summary>
    public void Load(PlaybackPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(plan));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_task is { IsCompleted: false }) throw new InvalidOperationException("请先停止上一首。 ");
            LoadPlan(plan);
            Publish();
        }
    }
    /// <summary>Selects a non-empty, half-open range in absolute MIDI time.</summary>
    public void SetRange(TimeSpan start, TimeSpan end)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_plan is null) throw new InvalidOperationException("请先载入曲目。 ");
            if (start < TimeSpan.Zero || start >= end || end > _plan.Duration)
                throw new ArgumentOutOfRangeException(nameof(start), "开始位置必须小于结束位置，且都位于曲目内。 ");
            AdvanceClock();
            _startPosition = start.TotalSeconds;
            _endPosition = end.TotalSeconds;
            if (_position < _startPosition || _position >= _endPosition) _position = _startPosition;
            // A range edit invalidates continuity, including a sustained chord containing the new start.
            if (_state is PlaybackState.Playing or PlaybackState.Paused) TryRelease();
            if (_state == PlaybackState.Completed) _state = PlaybackState.Stopped;
            if (_state == PlaybackState.Stopped) _message = "已设置演奏区间";
            Publish();
        }
    }
    private void LoadPlan(PlaybackPlan plan)
    {
        _plan = plan;
        _position = _startPosition = 0;
        _endPosition = plan.Duration.TotalSeconds;
        _active = -1;
        _lastTick = 0;
        _state = PlaybackState.Stopped;
        _message = "就绪";
    }
    public void Start(PlaybackPlan plan, bool preview, Func<bool>? canSend = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_task is { IsCompleted: false }) throw new InvalidOperationException("请先停止上一首。 ");
            if (!preview && canSend is null) throw new ArgumentException("真实演奏需要目标窗口检查。", nameof(canSend));
            if (!ReferenceEquals(_plan, plan))
            {
                if (plan.Duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(plan));
                LoadPlan(plan);
            }
            if (_endPosition <= _startPosition) throw new InvalidOperationException("曲目没有可演奏的时间区间。 ");
            if (_position < _startPosition || _position >= _endPosition) _position = _startPosition;
            _active = -1;
            _preview = preview;
            _canSend = canSend;
            _state = PlaybackState.Playing;
            _message = preview ? "预览中 · 内置口琴音效 · 不发送按键" : "演奏中";
            _lastTick = Stopwatch.GetTimestamp();
            _cancellation?.Dispose();
            _cancellation = new();
            var token = _cancellation.Token;
            _task = Task.Run(() => RunAsync(token));
        }
    }
    public void Pause(string reason = "已暂停")
    {
        lock (_gate)
        {
            if (_disposed || _state != PlaybackState.Playing) return;
            AdvanceClock();
            _state = PlaybackState.Paused;
            _message = reason;
            TryRelease();
            Publish();
        }
    }
    public bool Resume()
    {
        lock (_gate)
        {
            if (_disposed || _state != PlaybackState.Paused) return false;
            try
            {
                if (!_preview && _canSend?.Invoke() != true) { _message = "请切回原目标窗口，再按暂停快捷键恢复"; Publish(); return false; }
            }
            catch (Exception exception)
            {
                _state = PlaybackState.Faulted;
                _message = $"目标窗口检查失败：{exception.Message}";
                _cancellation?.Cancel();
                TryRelease();
                Publish();
                return false;
            }
            _lastTick = Stopwatch.GetTimestamp();
            _state = PlaybackState.Playing;
            _message = _preview ? "预览中 · 内置口琴音效 · 不发送按键" : "演奏中";
            return true;
        }
    }
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_disposed || _plan is null || _state == PlaybackState.Faulted) return;
            _position = Math.Clamp(position.TotalSeconds, _startPosition, _endPosition);
            _lastTick = Stopwatch.GetTimestamp();
            if (_state is PlaybackState.Playing or PlaybackState.Paused) TryRelease();
            if (_state == PlaybackState.Completed) { _state = PlaybackState.Stopped; _message = "已定位"; }
            Publish();
        }
    }
    public async Task StopAsync()
    {
        Task? pending;
        lock (_gate)
        {
            if (!_disposed)
            {
                _cancellation?.Cancel();
                _state = PlaybackState.Stopped;
                _position = _startPosition;
                _lastTick = 0;
                _message = "已停止";
                TryRelease();
            }
            pending = _task;
        }
        if (pending is not null)
        {
            try { await pending.ConfigureAwait(false); }
            catch (Exception exception)
            {
                lock (_gate)
                {
                    _state = PlaybackState.Faulted;
                    _message = $"停止演奏失败：{exception.Message}";
                    TryRelease();
                }
            }
        }
        lock (_gate)
        {
            // Allow cleanup retries even after disposal if the output previously refused to stop.
            if (_disposed && _state == PlaybackState.Faulted && TryRelease())
            {
                _state = PlaybackState.Stopped;
                _message = "已停止";
            }
            if (!_disposed) Publish();
        }
    }
    private async Task RunAsync(CancellationToken cancellation)
    {
        var nextReport = 0L;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                lock (_gate)
                {
                    if (cancellation.IsCancellationRequested) break;
                    AdvanceClock();
                    if (_state == PlaybackState.Playing && _plan is not null)
                    {
                        if (_position >= _endPosition)
                        {
                            _position = _endPosition;
                            _state = PlaybackState.Completed;
                            _message = _endPosition < _plan.Duration.TotalSeconds || _startPosition > 0
                                ? "所选区间演奏完成" : "本曲演奏完成";
                            Release();
                            Publish();
                            return;
                        }
                        else if (!_preview && _canSend?.Invoke() != true)
                        {
                            _state = PlaybackState.Paused;
                            _message = "目标窗口失去焦点，已自动暂停";
                            Release();
                        }
                        else
                        {
                            var index = FindChord(_plan, _position);
                            if (index != _active)
                            {
                                if (index >= 0)
                                {
                                    var chord = _plan.Chords[index];
                                    var continuous = _active >= 0 && index == _active + 1 &&
                                        _plan.Chords[_active].End == chord.Start;
                                    // Preview voices fade/retrigger within the continuous audio stream.
                                    // Resetting the device at ordinary note boundaries cuts the waveform.
                                    if (!continuous && !_preview) Release();
                                    var retriggerKeys = continuous ? chord.RetriggerKeys : chord.Notes.Select(n => n.Key).ToArray();
                                    if (_preview)
                                    {
                                        previewAudio?.SetChord(chord.Notes, retriggerKeys);
                                    }
                                    else
                                    {
                                        input.SetChord(new(chord.Notes.Select(n => Array.IndexOf(NoteKeys, n.Key)).Distinct().ToArray(),
                                            (int)chord.Register - 1, chord.Semitone),
                                            retriggerKeys.Select(k => Array.IndexOf(NoteKeys, k)).ToArray());
                                    }
                                    _active = index;
                                }
                                else if (_preview)
                                {
                                    previewAudio?.SetChord([], []);
                                    _active = -1;
                                }
                                else Release();
                            }
                        }
                    }
                    var now = Stopwatch.GetTimestamp();
                    if (now >= nextReport) { Publish(); nextReport = now + Stopwatch.Frequency / 20; }
                }
                await Task.Delay(4, cancellation).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception e)
        {
            lock (_gate)
            {
                _state = PlaybackState.Faulted;
                _message = $"{(_preview ? "预览音效" : "演奏")}失败：{e.Message}";
                TryRelease();
                Publish();
            }
        }
        finally { lock (_gate) { if (!TryRelease() && !_disposed) Publish(); } }
    }
    private void AdvanceClock()
    {
        var now = Stopwatch.GetTimestamp();
        if (_state == PlaybackState.Playing && _lastTick != 0)
            _position = Math.Min(_endPosition, _position + (now - _lastTick) / (double)Stopwatch.Frequency * _speed);
        _lastTick = now;
    }
    private static int FindChord(PlaybackPlan plan, double position)
    {
        int low = 0, high = plan.Chords.Count - 1, candidate = -1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (plan.Chords[mid].Start.TotalSeconds <= position) { candidate = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return candidate >= 0 && position < plan.Chords[candidate].End.TotalSeconds ? candidate : -1;
    }
    private void Release()
    {
        _active = -1;
        if (_preview) previewAudio?.ReleaseAll();
        else input.ReleaseAll();
    }
    private bool TryRelease()
    {
        try { Release(); return true; }
        catch (Exception exception)
        {
            _state = PlaybackState.Faulted;
            _message = _preview
                ? $"停止预览音效失败：{exception.Message}。请再次停止，确保声音已停止。"
                : $"释放按键失败：{exception.Message}。请切回游戏并再次停止，确保按键已释放。";
            _cancellation?.Cancel();
            return false;
        }
    }
    private void Publish()
    {
        var snapshot = new PlaybackSnapshot(_state, TimeSpan.FromSeconds(_position), _plan?.Duration ?? TimeSpan.Zero,
            _message, _active >= 0 ? _plan!.Chords[_active] : null);
        if (Updated is not { } handlers) return;
        foreach (Action<PlaybackSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(snapshot); }
            catch (Exception exception) { Trace.TraceError("Playback update callback failed: {0}", exception); }
        }
    }
    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation?.Cancel();
            _state = PlaybackState.Stopped;
            TryRelease();
            cancellation = _cancellation;
            _cancellation = null;
        }
        cancellation?.Dispose();
    }
}
