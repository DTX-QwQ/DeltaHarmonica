using System.Diagnostics;
using DeltaHarmonica.Core;

namespace DeltaHarmonica.App.Services;

public enum PlaybackState { Stopped, Playing, Paused, Completed, Faulted }
public sealed record PlaybackSnapshot(PlaybackState State, TimeSpan Position, TimeSpan Duration, string Message, ScheduledChord? Chord);

/// <summary>Single monotonic clock; tempo changes are already resolved by the MIDI reader.</summary>
public sealed class PlaybackEngine(IInputSink input) : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private PlaybackPlan? _plan;
    private PlaybackState _state;
    private double _position;
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
    public double Speed
    {
        get { lock (_gate) return _speed; }
        set
        {
            if (!double.IsFinite(value) || value < .25 || value > 3) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); AdvanceClock(); _speed = value; }
        }
    }
    public void Start(PlaybackPlan plan, bool preview, Func<bool>? canSend = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_task is { IsCompleted: false }) throw new InvalidOperationException("请先停止上一首。 ");
            if (!preview && canSend is null) throw new ArgumentException("真实演奏需要目标窗口检查。", nameof(canSend));
            _plan = plan;
            _position = 0;
            _active = -1;
            _preview = preview;
            _canSend = canSend;
            _state = PlaybackState.Playing;
            _message = preview ? "预览中 · 不发送按键" : "演奏中";
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
            _message = _preview ? "预览中 · 不发送按键" : "演奏中";
            return true;
        }
    }
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_disposed || _plan is null || _state is not (PlaybackState.Playing or PlaybackState.Paused)) return;
            _position = Math.Clamp(position.TotalSeconds, 0, _plan.Duration.TotalSeconds);
            _lastTick = Stopwatch.GetTimestamp();
            TryRelease();
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
        lock (_gate) { if (!_disposed) Publish(); }
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
                        if (!_preview && _canSend?.Invoke() != true)
                        {
                            _state = PlaybackState.Paused;
                            _message = "目标窗口失去焦点，已自动暂停";
                            Release();
                        }
                        else if (_position >= _plan.Duration.TotalSeconds)
                        {
                            _position = _plan.Duration.TotalSeconds;
                            _state = PlaybackState.Completed;
                            _message = "本曲演奏完成";
                            Release();
                            Publish();
                            return;
                        }
                        else
                        {
                            var index = FindChord(_plan, _position);
                            if (index != _active)
                            {
                                if (index >= 0)
                                {
                                    var chord = _plan.Chords[index];
                                    if (!_preview)
                                    {
                                        var continuous = _active >= 0 && index == _active + 1 &&
                                            _plan.Chords[_active].End == chord.Start;
                                        if (!continuous) Release();
                                        input.SetChord(new(chord.Notes.Select(n => Array.IndexOf(NoteKeys, n.Key)).Distinct().ToArray(),
                                            (int)chord.Register - 1, chord.Semitone),
                                            continuous ? chord.RetriggerKeys.Select(k => Array.IndexOf(NoteKeys, k)).ToArray()
                                                : chord.Notes.Select(n => Array.IndexOf(NoteKeys, n.Key)).ToArray());
                                    }
                                    _active = index;
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
                _message = $"演奏失败：{e.Message}";
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
            _position += (now - _lastTick) / (double)Stopwatch.Frequency * _speed;
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
        if (!_preview) input.ReleaseAll();
    }
    private bool TryRelease()
    {
        try { Release(); return true; }
        catch (Exception exception)
        {
            _state = PlaybackState.Faulted;
            _message = $"释放按键失败：{exception.Message}。请切回游戏并再次停止，确保按键已释放。";
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
