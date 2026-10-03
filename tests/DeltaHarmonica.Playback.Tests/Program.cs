using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DeltaHarmonica.App.Services;
using DeltaHarmonica.Core;

// These tests always give the scheduler a recording sink. No keyboard or mouse input is injected.
var tests = new (string Name, Func<Task> Run)[]
{
    ("preview never touches the input sink", PreviewIsolation),
    ("adjacent chords preserve held notes and retrigger only onsets", ContinuousChords),
    ("pause releases input and freezes the clock; resume retriggers", PauseResume),
    ("foreground denial blocks startup and resume", ForegroundDenial),
    ("foreground loss automatically pauses and releases", ForegroundLoss),
    ("stop waits for scheduler shutdown and releases modifiers", StopCleanup),
    ("seek releases input, selects the new chord, and clamps bounds", Seek),
    ("speed changes preserve elapsed time at each rate", SpeedChanges),
    ("input failure faults playback and releases partial input", InputFailure),
    ("denied stop cleanup reports a fault and can retry owned inputs", ReleaseFailure),
    ("pause and seek cleanup failures never escape UI handlers", PauseSeekReleaseFailure),
    ("immediate disposal cancels safely and forbids future playback", DisposeRace),
    ("real playback requires a foreground guard", RequiredGuard),
    ("hotkey parsing prevents note recursion and duplicate bindings", HotkeyParsing),
    ("native INPUT struct matches Windows ABI", NativeLayout)
};
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} playback/native tests passed. No input injected.");
return failures == 0 ? 0 : 1;

static async Task PreviewIsolation()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    var snapshots = new SnapshotLog(player);
    player.Start(Plan(240, Chord(0, 240, [0, 2])), preview: true);
    await WaitUntil(() => snapshots.Last?.Chord is not null);
    player.Pause();
    player.Seek(TimeSpan.FromMilliseconds(120));
    Check(player.Resume(), "Preview did not resume.");
    await WaitUntil(() => player.State == PlaybackState.Completed);
    await player.StopAsync();
    Check(sink.Calls.Count == 0, "Preview invoked an input method, including ReleaseAll.");
}

static async Task ContinuousChords()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink) { Speed = 2 };
    var plan = Plan(1100,
        Chord(0, 200, [0, 2], [0, 2]),
        Chord(200, 200, [0, 4], [4]),
        Chord(400, 200, [0, 4], [0]),
        Chord(800, 200, [6], [6], HarmonicaRegister.Upper, true));
    player.Start(plan, preview: false, () => true);
    await WaitUntil(() => player.State == PlaybackState.Completed);
    await player.StopAsync();

    var calls = sink.Calls;
    var sets = calls.Where(c => c.Kind == "set").ToArray();
    Equal(4, sets.Length);
    Equal(0, sink.SendChordCount);
    Sequence([0, 2], sets[0].Chord!.KeyIndices);
    Sequence([0, 4], sets[1].Chord!.KeyIndices);
    Sequence([0, 4], sets[2].Chord!.KeyIndices);
    Sequence([6], sets[3].Chord!.KeyIndices);
    Sequence([0, 2], sets[0].RetriggerKeys);
    Sequence([4], sets[1].RetriggerKeys);
    Sequence([0], sets[2].RetriggerKeys);
    Sequence([6], sets[3].RetriggerKeys);
    Equal(1, sets[3].Chord!.OctaveShift);
    Check(sets[3].Chord!.Semitone, "Upper semitone modifiers were lost.");

    var setIndices = calls.Select((call, index) => (call, index)).Where(p => p.call.Kind == "set").Select(p => p.index).ToArray();
    Equal(setIndices[0] + 1, setIndices[1]);
    Equal(setIndices[1] + 1, setIndices[2]);
    Check(calls.Skip(setIndices[2] + 1).Take(setIndices[3] - setIndices[2] - 1).Any(c => c.Kind == "release"),
        "The silent gap did not release held notes.");
    Check(calls.Skip(setIndices[3] + 1).Any(c => c.Kind == "release"), "Completion did not release notes.");
    Check(sink.HeldChord is null, "Completion left modifiers or notes down.");
    // With 2x speed, the second onset should occur near 100 ms, not the original 200 ms.
    Check(sets[1].TimeSeconds - sets[0].TimeSeconds is > .025 and < .18, "The 2x playback onset timing is incorrect.");
}

static async Task PauseResume()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    var snapshots = new SnapshotLog(player);
    player.Start(Plan(3000, Chord(0, 3000, [1, 3])), false, () => true);
    await WaitUntil(() => sink.SetCount == 1);
    await Task.Delay(70);
    player.Pause();
    var paused = snapshots.Last!;
    var count = sink.SetCount;
    Equal(PlaybackState.Paused, paused.State);
    Check(sink.HeldChord is null, "Pause did not release input.");
    await Task.Delay(130);
    Near(paused.Position.TotalSeconds, snapshots.Last!.Position.TotalSeconds, .002);
    Equal(count, sink.SetCount);
    Check(player.Resume(), "Resume was rejected despite a valid foreground guard.");
    await WaitUntil(() => sink.SetCount == count + 1);
    Sequence([1, 3], sink.Calls.Last(c => c.Kind == "set").RetriggerKeys);
    await player.StopAsync();
}

static async Task ForegroundDenial()
{
    var foreground = new ForegroundGate();
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    player.Start(Plan(3000, Chord(0, 3000, [0])), false, () => foreground.Allowed);
    await WaitUntil(() => player.State == PlaybackState.Paused);
    Equal(0, sink.SetCount);
    Check(!player.Resume(), "Resume emitted input into an unrelated foreground window.");
    await Task.Delay(40);
    Equal(0, sink.SetCount);
    foreground.Allowed = true;
    Check(player.Resume(), "Resume did not accept the restored target.");
    await WaitUntil(() => sink.SetCount == 1);
    await player.StopAsync();
}

static async Task ForegroundLoss()
{
    var foreground = new ForegroundGate { Allowed = true };
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    var snapshots = new SnapshotLog(player);
    player.Start(Plan(3000, Chord(0, 3000, [4], [4], HarmonicaRegister.Lower, true)), false, () => foreground.Allowed);
    await WaitUntil(() => sink.SetCount == 1);
    foreground.Allowed = false;
    await WaitUntil(() => player.State == PlaybackState.Paused && snapshots.Last?.State == PlaybackState.Paused);
    Check(sink.HeldChord is null, "Focus loss left mouse modifiers held.");
    var position = snapshots.Last!.Position;
    var count = sink.SetCount;
    await Task.Delay(100);
    Near(position.TotalSeconds, snapshots.Last!.Position.TotalSeconds, .002);
    Equal(count, sink.SetCount);
    await player.StopAsync();
}

static async Task StopCleanup()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    player.Start(Plan(3000, Chord(0, 3000, [0, 7], [0, 7], HarmonicaRegister.Upper, true)), false, () => true);
    await WaitUntil(() => sink.SetCount == 1);
    await player.StopAsync();
    Equal(PlaybackState.Stopped, player.State);
    Check(sink.HeldChord is null, "Stop left inputs held.");
    var count = sink.SetCount;
    await Task.Delay(80);
    Equal(count, sink.SetCount);
    await player.StopAsync();
    Check(sink.HeldChord is null, "Repeated stop should be harmless.");
}

static async Task Seek()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    var snapshots = new SnapshotLog(player);
    player.Start(Plan(2000, Chord(0, 700, [0]), Chord(700, 1300, [5], [5], HarmonicaRegister.Lower, true)), false, () => true);
    await WaitUntil(() => sink.SetCount == 1);
    player.Pause();
    player.Seek(TimeSpan.FromSeconds(-1));
    Near(0, snapshots.Last!.Position.TotalSeconds, .001);
    player.Seek(TimeSpan.FromSeconds(100));
    Near(2, snapshots.Last!.Position.TotalSeconds, .001);
    player.Seek(TimeSpan.FromMilliseconds(1000));
    Near(1, snapshots.Last!.Position.TotalSeconds, .001);
    Check(sink.HeldChord is null, "Seek while paused emitted input.");
    Check(player.Resume(), "Seeking prevented resume.");
    await WaitUntil(() => sink.SetCount == 2);
    Sequence([5], sink.HeldChord!.KeyIndices);
    Equal(-1, sink.HeldChord!.OctaveShift);
    Check(sink.HeldChord!.Semitone, "Seek lost the semitone modifier.");
    player.Seek(TimeSpan.Zero);
    await WaitUntil(() => sink.SetCount == 3);
    Sequence([0], sink.HeldChord!.KeyIndices);
    player.Seek(TimeSpan.FromSeconds(100));
    await WaitUntil(() => player.State == PlaybackState.Completed);
    Check(sink.HeldChord is null, "Seek to the end did not release input.");
    await player.StopAsync();
}

static async Task SpeedChanges()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink) { Speed = .5 };
    var snapshots = new SnapshotLog(player);
    var watch = Stopwatch.StartNew();
    player.Start(Plan(5000, Chord(0, 5000, [0])), true);
    await Task.Delay(180);
    player.Pause();
    var firstPosition = snapshots.Last!.Position.TotalSeconds;
    Near(watch.Elapsed.TotalSeconds * .5, firstPosition, .03);

    Check(player.Resume(), "Preview resume failed.");
    watch.Restart();
    await Task.Delay(120);
    var slowElapsed = watch.Elapsed.TotalSeconds;
    player.Speed = 2;
    watch.Restart();
    await Task.Delay(120);
    player.Pause();
    var fastElapsed = watch.Elapsed.TotalSeconds;
    var advance = snapshots.Last!.Position.TotalSeconds - firstPosition;
    Near(slowElapsed * .5 + fastElapsed * 2, advance, .04);
    player.Speed = 3;
    await Task.Delay(60);
    Near(firstPosition + advance, snapshots.Last!.Position.TotalSeconds, .002);
    foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, 0d, .24, 3.01 })
        Throws<ArgumentOutOfRangeException>(() => player.Speed = invalid);
    await player.StopAsync();
    Equal(0, sink.Calls.Count);
}

static async Task InputFailure()
{
    var sink = new RecordingInput { FailOnSet = true };
    using var player = new PlaybackEngine(sink);
    var snapshots = new SnapshotLog(player);
    player.Start(Plan(1000, Chord(0, 1000, [2])), false, () => true);
    await WaitUntil(() => player.State == PlaybackState.Faulted);
    Check(sink.HeldChord is null, "Failure left partially accepted input held.");
    Check(snapshots.Last!.Message.Contains("test injection failure", StringComparison.Ordinal), "Failure reason was lost.");
    await player.StopAsync();
}

static async Task ReleaseFailure()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    var snapshots = new SnapshotLog(player);
    player.Start(Plan(3000, Chord(0, 3000, [0], [0], HarmonicaRegister.Upper, true)), false, () => true);
    await WaitUntil(() => sink.SetCount == 1);
    sink.FailOnRelease = true;
    await player.StopAsync();
    Equal(PlaybackState.Faulted, player.State);
    Check(snapshots.Last!.Message.Contains("test release denial", StringComparison.Ordinal), "Stop hid the native release error.");
    Check(sink.HeldChord is not null, "The test sink must retain ownership after a denied release.");
    sink.FailOnRelease = false;
    await player.StopAsync();
    Equal(PlaybackState.Stopped, player.State);
    Check(sink.HeldChord is null, "Retry did not release the previously owned inputs.");
}

static async Task PauseSeekReleaseFailure()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    var plan = Plan(3000, Chord(0, 3000, [0]));
    player.Start(plan, false, () => true);
    await WaitUntil(() => sink.SetCount == 1);
    sink.FailOnRelease = true;
    player.Pause();
    Equal(PlaybackState.Faulted, player.State);
    sink.FailOnRelease = false;
    await player.StopAsync();
    player.Start(plan, false, () => true);
    await WaitUntil(() => sink.SetCount == 2);
    sink.FailOnRelease = true;
    player.Seek(TimeSpan.FromSeconds(1));
    Equal(PlaybackState.Faulted, player.State);
    sink.FailOnRelease = false;
    await player.StopAsync();
    Check(sink.HeldChord is null, "Cleanup retry after pause/seek failed.");
}

static async Task DisposeRace()
{
    var plan = Plan(3000, Chord(0, 3000, [0]));
    for (var index = 0; index < 12; index++)
    {
        var sink = new RecordingInput();
        var player = new PlaybackEngine(sink);
        player.Start(plan, index % 2 == 0, () => true);
        player.Dispose();
        await player.StopAsync();
        Equal(PlaybackState.Stopped, player.State);
        Check(sink.HeldChord is null, "Dispose left input held.");
        Throws<ObjectDisposedException>(() => player.Start(plan, true));
        Throws<ObjectDisposedException>(() => player.Speed = 1);
        Check(!player.Resume(), "Disposed engine resumed.");
        player.Pause();
        player.Seek(TimeSpan.FromSeconds(1));
        player.Dispose();
    }

    var deniedSink = new RecordingInput();
    var deniedPlayer = new PlaybackEngine(deniedSink);
    deniedPlayer.Start(plan, false, () => true);
    await WaitUntil(() => deniedSink.SetCount == 1);
    deniedSink.FailOnRelease = true;
    deniedPlayer.Dispose(); // A denied native release must not interrupt the rest of the window's cleanup.
    await deniedPlayer.StopAsync();
    Equal(PlaybackState.Faulted, deniedPlayer.State);
    Throws<ObjectDisposedException>(() => deniedPlayer.Start(plan, true));
    deniedSink.FailOnRelease = false;
    deniedSink.ReleaseAll();
}

static Task RequiredGuard()
{
    using var player = new PlaybackEngine(new RecordingInput());
    Throws<ArgumentException>(() => player.Start(Plan(1000, Chord(0, 1000, [0])), false));
    return Task.CompletedTask;
}

static Task HotkeyParsing()
{
    Check(GlobalHotkeyService.Validate(new()) is null, "Default shortcuts are invalid.");
    Check(GlobalHotkeyService.Validate(new("Ctrl+Alt+F8", "Shift+F6", "Win+F7", "Escape")) is null, "Custom modifiers failed.");
    foreach (var key in new[] { "Z", "X", "C", "V", "B", "N", "M", ",", "Comma", "OemComma" })
        Check(GlobalHotkeyService.Validate(new(key)) is not null, $"Bare harmonica key {key} could trigger itself.");
    foreach (var key in new[] { "Ctrl", "Alt+Shift", "Ctrl+Ctrl+F8", "Ctrl+", "MouseLeft", "F25", "F8+F9" })
        Check(GlobalHotkeyService.Validate(new(key)) is not null, $"Invalid shortcut {key} was accepted.");
    Check(GlobalHotkeyService.Validate(new("F6")) is not null, "Duplicate shortcut accepted.");
    Check(GlobalHotkeyService.Validate(new("Ctrl+F8", "Control+F8")) is not null, "Equivalent shortcut aliases accepted twice.");
    return Task.CompletedTask;
}

static Task NativeLayout()
{
    var type = typeof(NativeInputService).GetNestedType("NativeInput", BindingFlags.NonPublic)!;
    Equal(Environment.Is64BitProcess ? 40 : 28, Marshal.SizeOf(type));
    return Task.CompletedTask;
}

static PlaybackPlan Plan(int durationMilliseconds, params ScheduledChord[] chords) => new()
{
    Duration = TimeSpan.FromMilliseconds(durationMilliseconds), Chords = chords
};

static ScheduledChord Chord(int startMilliseconds, int durationMilliseconds, int[] keyIndices,
    int[]? retriggerIndices = null, HarmonicaRegister register = HarmonicaRegister.Normal, bool semitone = false) => new(
    TimeSpan.FromMilliseconds(startMilliseconds), TimeSpan.FromMilliseconds(durationMilliseconds),
    keyIndices.Select(index => new MappedNote(HarmonicaMapper.Keys[index], register, semitone, 60 + index, 60 + index, 60 + index, false)).ToArray(),
    register, semitone, (retriggerIndices ?? keyIndices).Select(index => HarmonicaMapper.Keys[index]).ToArray());

static async Task WaitUntil(Func<bool> condition, int timeoutMilliseconds = 3000)
{
    var watch = Stopwatch.StartNew();
    while (!condition())
    {
        if (watch.ElapsedMilliseconds > timeoutMilliseconds) throw new TimeoutException("The playback condition was not met.");
        await Task.Delay(4);
    }
}

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) where T : notnull
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}.");
}
static void Near(double expected, double actual, double tolerance)
{
    if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected:F5} ± {tolerance:F5}, got {actual:F5}.");
}
static void Sequence(IEnumerable<int> expected, IEnumerable<int> actual)
{
    if (!expected.SequenceEqual(actual)) throw new Exception($"Expected [{string.Join(',', expected)}], got [{string.Join(',', actual)}].");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class ForegroundGate
{
    private int _allowed;
    public bool Allowed { get => Volatile.Read(ref _allowed) != 0; set => Volatile.Write(ref _allowed, value ? 1 : 0); }
}

sealed class SnapshotLog
{
    private readonly object _gate = new();
    private PlaybackSnapshot? _last;
    public SnapshotLog(PlaybackEngine player) => player.Updated += snapshot => { lock (_gate) _last = snapshot; };
    public PlaybackSnapshot? Last { get { lock (_gate) return _last; } }
}

sealed record InputCall(string Kind, InputChord? Chord, IReadOnlyList<int> RetriggerKeys, double TimeSeconds);

sealed class RecordingInput : IInputSink
{
    private readonly object _gate = new();
    private readonly List<InputCall> _calls = [];
    private InputChord? _heldChord;
    private int _sendChordCount;
    private int _failOnRelease;
    public bool FailOnSet { get; init; }
    public bool FailOnRelease { get => Volatile.Read(ref _failOnRelease) != 0; set => Volatile.Write(ref _failOnRelease, value ? 1 : 0); }
    public IReadOnlyList<InputCall> Calls { get { lock (_gate) return _calls.ToArray(); } }
    public InputChord? HeldChord { get { lock (_gate) return _heldChord; } }
    public int SetCount { get { lock (_gate) return _calls.Count(c => c.Kind == "set"); } }
    public int SendChordCount { get { lock (_gate) return _sendChordCount; } }

    public void SendChord(InputChord chord)
    {
        lock (_gate) _sendChordCount++;
        SetChord(chord, chord.KeyIndices);
    }

    public void SetChord(InputChord chord, IReadOnlyList<int> retriggerKeys)
    {
        lock (_gate)
        {
            _heldChord = chord with { KeyIndices = chord.KeyIndices.ToArray() };
            _calls.Add(new("set", _heldChord, retriggerKeys.ToArray(), Now()));
            if (FailOnSet) throw new InvalidOperationException("test injection failure");
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            _calls.Add(new("release", null, [], Now()));
            if (FailOnRelease) throw new InvalidOperationException("test release denial");
            _heldChord = null;
        }
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
