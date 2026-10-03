using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using DeltaHarmonica.App.Models;
using DeltaHarmonica.App.Services;
using DeltaHarmonica.Core;

// These tests always give the scheduler recording sinks. No input is injected or audio device opened.
var tests = new (string Name, Func<Task> Run)[]
{
    ("preview never touches the input sink", PreviewIsolation),
    ("preview preserves mapped pitches, held notes, and repeated onsets", PreviewContinuousChords),
    ("preview pause and seek release audio; resume retriggers all notes", PreviewPauseSeek),
    ("preview audio failures fault safely and allow cleanup retries", PreviewAudioFailure),
    ("real playback never calls the preview audio sink", RealPlaybackAudioIsolation),
    ("adjacent chords preserve held notes and retrigger only onsets", ContinuousChords),
    ("pause releases input and freezes the clock; resume retriggers", PauseResume),
    ("foreground denial blocks startup and resume", ForegroundDenial),
    ("foreground loss automatically pauses and releases", ForegroundLoss),
    ("stop waits for scheduler shutdown and releases modifiers", StopCleanup),
    ("seek releases input, selects the new chord, and clamps bounds", Seek),
    ("speed changes preserve elapsed time at each rate", SpeedChanges),
    ("range playback starts inside sustained notes and releases at its end", RangePlayback),
    ("loaded ranges support pre-start seek, stop reset, and plan reset", RangeLifecycle),
    ("range edits reset held chords and remain safe while paused", RangeEdits),
    ("range preview respects speed and never sends input", RangePreview),
    ("invalid and empty ranges are rejected without changing playback", RangeValidation),
    ("range edit release failures fault safely and permit cleanup retry", RangeReleaseFailure),
    ("input failure faults playback and releases partial input", InputFailure),
    ("denied stop cleanup reports a fault and can retry owned inputs", ReleaseFailure),
    ("pause and seek cleanup failures never escape UI handlers", PauseSeekReleaseFailure),
    ("immediate disposal cancels safely and forbids future playback", DisposeRace),
    ("real playback requires a foreground guard", RequiredGuard),
    ("hotkey parsing prevents note recursion and duplicate bindings", HotkeyParsing),
    ("start hotkey migrates old settings and persists custom bindings", StartHotkeySettings),
    ("audio preferences migrate defaults, normalize invalid values, and persist choices", AudioPreferenceSettings),
    ("native INPUT struct matches Windows ABI", NativeLayout)
};
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} playback/native tests passed. No input injected or audio device opened.");
return failures == 0 ? 0 : 1;

static async Task PreviewIsolation()
{
    var sink = new RecordingInput();
    var audio = new RecordingAudio();
    using var player = new PlaybackEngine(sink, audio);
    var snapshots = new SnapshotLog(player);
    player.Start(Plan(240, Chord(0, 240, [0, 2])), preview: true);
    await WaitUntil(() => snapshots.Last?.Chord is not null);
    player.Pause();
    player.Seek(TimeSpan.FromMilliseconds(120));
    Check(player.Resume(), "Preview did not resume.");
    await WaitUntil(() => player.State == PlaybackState.Completed);
    await player.StopAsync();
    Check(sink.Calls.Count == 0, "Preview invoked an input method, including ReleaseAll.");
    Check(audio.SetCount >= 1, "Preview did not send any mapped notes to its audio sink.");
    Check(audio.HeldNotes.Count == 0, "Preview completion left audio voices held.");
}

static async Task PreviewContinuousChords()
{
    var input = new RecordingInput();
    var audio = new RecordingAudio();
    using var player = new PlaybackEngine(input, audio) { Speed = 2 };
    var lowFolded = new MappedNote('z', HarmonicaRegister.Lower, true, 49, 25, 37, true);
    var otherLowFolded = new MappedNote('b', HarmonicaRegister.Lower, true, 56, 32, 44, true);
    var first = Chord(0, 200, [0, 4], [0, 4], HarmonicaRegister.Lower, true) with { Notes = [lowFolded, otherLowFolded] };
    var plan = Plan(1100,
        first,
        first with { Start = TimeSpan.FromMilliseconds(200), RetriggerKeys = ['b'] },
        first with { Start = TimeSpan.FromMilliseconds(400), RetriggerKeys = ['z'] },
        Chord(800, 200, [6], [6], HarmonicaRegister.Upper, true));
    player.Start(plan, true);
    await WaitUntil(() => player.State == PlaybackState.Completed);
    await player.StopAsync();

    var calls = audio.Calls;
    var allSets = calls.Where(c => c.Kind == "set").ToArray();
    var sets = allSets.Where(c => c.Notes.Count > 0).ToArray();
    Equal(4, sets.Length);
    Sequence([lowFolded, otherLowFolded], sets[0].Notes);
    Sequence([49, 56], sets[0].Notes.Select(note => note.EffectivePitch));
    Sequence("zb", sets[0].RetriggerKeys);
    Sequence("zb", sets[1].Notes.Select(note => note.Key));
    Sequence("b", sets[1].RetriggerKeys);
    Sequence("z", sets[2].RetriggerKeys);
    Sequence("m", sets[3].RetriggerKeys);
    Equal(HarmonicaRegister.Upper, sets[3].Notes[0].Register);
    Check(sets[3].Notes[0].Semitone, "Preview lost the actual mapped note modifiers.");

    var indices = calls.Select((call, index) => (call, index)).Where(p => p.call.Kind == "set" && p.call.Notes.Count > 0).Select(p => p.index).ToArray();
    Equal(indices[0] + 1, indices[1]);
    Equal(indices[1] + 1, indices[2]);
    Check(calls.Skip(indices[2] + 1).Take(indices[3] - indices[2] - 1).Any(c => c.Kind == "set" && c.Notes.Count == 0 && c.RetriggerKeys.Count == 0),
        "The silent preview gap did not request a natural voice fade-out.");
    Check(calls.Skip(indices[0]).Take(indices[3] - indices[0] + 1).All(c => c.Kind != "release"),
        "A natural preview note transition or rest forcibly reset the audio device.");
    Check(calls.Skip(indices[3] + 1).Any(c => c.Kind == "release"), "Preview completion did not release its voices.");
    Check(sets[1].TimeSeconds - sets[0].TimeSeconds is > .025 and < .18, "Audio onset timing ignored 2x speed.");
    Equal(0, audio.HeldNotes.Count);
    Equal(0, input.Calls.Count);
}

static async Task PreviewPauseSeek()
{
    var input = new RecordingInput();
    var audio = new RecordingAudio();
    using var player = new PlaybackEngine(input, audio);
    var snapshots = new SnapshotLog(player);
    var plan = Plan(4000, Chord(0, 2000, [1, 3]), Chord(2000, 2000, [5, 7], [5]));
    player.Start(plan, true);
    await WaitUntil(() => audio.SetCount == 1);
    player.Pause();
    Equal(PlaybackState.Paused, player.State);
    Equal(0, audio.HeldNotes.Count);
    var pausedPosition = player.Position;
    await Task.Delay(100);
    Near(pausedPosition.TotalSeconds, player.Position.TotalSeconds, .000001);
    Equal(1, audio.SetCount);

    player.Seek(TimeSpan.FromMilliseconds(2500));
    Near(2.5, snapshots.Last!.Position.TotalSeconds, .000001);
    Equal(0, audio.HeldNotes.Count);
    Equal(1, audio.SetCount);
    Check(player.Resume(), "Preview did not resume after a paused seek.");
    await WaitUntil(() => audio.SetCount == 2);
    Sequence("n,", audio.Calls.Last(c => c.Kind == "set").RetriggerKeys);
    player.Seek(TimeSpan.FromMilliseconds(2600));
    await WaitUntil(() => audio.SetCount == 3);
    Sequence("n,", audio.Calls.Last(c => c.Kind == "set").RetriggerKeys);
    var calls = audio.Calls;
    var lastSets = calls.Select((call, index) => (call, index)).Where(p => p.call.Kind == "set").Select(p => p.index).ToArray();
    Check(calls.Skip(lastSets[1] + 1).Take(lastSets[2] - lastSets[1] - 1).Any(c => c.Kind == "release"),
        "Seeking within the current preview chord did not release and retrigger voices.");
    player.SetRange(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(900));
    await WaitUntil(() => audio.SetCount == 4);
    Sequence("xv", audio.Calls.Last(c => c.Kind == "set").RetriggerKeys);
    var rangeCalls = audio.Calls;
    var rangeSets = rangeCalls.Select((call, index) => (call, index)).Where(p => p.call.Kind == "set").Select(p => p.index).ToArray();
    Check(rangeCalls.Skip(rangeSets[2] + 1).Take(rangeSets[3] - rangeSets[2] - 1).Any(c => c.Kind == "release"),
        "Changing the preview range while playing did not release its previous voices.");
    await player.StopAsync();
    Equal(0, audio.HeldNotes.Count);
    var count = audio.SetCount;
    await Task.Delay(50);
    Equal(count, audio.SetCount);
    Equal(0, input.Calls.Count);
}

static async Task PreviewAudioFailure()
{
    var plan = Plan(3000, Chord(0, 3000, [0]));
    var input = new RecordingInput();
    var failedAudio = new RecordingAudio { FailOnSet = true };
    using (var player = new PlaybackEngine(input, failedAudio))
    {
        var snapshots = new SnapshotLog(player);
        player.Start(plan, true);
        await WaitUntil(() => player.State == PlaybackState.Faulted && snapshots.Last?.State == PlaybackState.Faulted);
        Check(snapshots.Last!.Message.Contains("test audio output failure", StringComparison.Ordinal), "Audio output failure reason was lost.");
        Equal(0, failedAudio.HeldNotes.Count);
        await player.StopAsync();
    }

    foreach (var operation in new[] { "pause", "seek", "range", "stop", "completion", "dispose" })
    {
        var audio = new RecordingAudio();
        using var player = new PlaybackEngine(input, audio);
        var snapshots = new SnapshotLog(player);
        var currentPlan = operation == "completion" ? Plan(180, Chord(0, 180, [0])) : plan;
        player.Start(currentPlan, true);
        await WaitUntil(() => audio.SetCount == 1);
        audio.FailOnRelease = true;
        switch (operation)
        {
            case "pause": player.Pause(); break;
            case "seek": player.Seek(TimeSpan.FromSeconds(1)); break;
            case "range": player.SetRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)); break;
            case "stop": await player.StopAsync(); break;
            case "completion": break; // Let the scheduler's natural completion cleanup fail.
            case "dispose": player.Dispose(); break;
        }
        await WaitUntil(() => player.State == PlaybackState.Faulted);
        if (operation != "dispose")
            Check(snapshots.Last!.Message.Contains("test audio release denial", StringComparison.Ordinal), $"{operation} hid the audio cleanup failure.");
        Check(audio.HeldNotes.Count > 0, "The audio substitute should retain held notes after a denied cleanup.");
        audio.FailOnRelease = false;
        await player.StopAsync();
        Equal(0, audio.HeldNotes.Count);
    }
    Equal(0, input.Calls.Count);
}

static async Task RealPlaybackAudioIsolation()
{
    var input = new RecordingInput();
    var audio = new RecordingAudio { FailOnSet = true, FailOnRelease = true };
    var player = new PlaybackEngine(input, audio);
    var plan = Plan(3000, Chord(0, 3000, [0]));
    player.Start(plan, false, () => true);
    await WaitUntil(() => input.SetCount == 1);
    player.Pause();
    player.Seek(TimeSpan.FromMilliseconds(500));
    player.SetRange(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(1500));
    Check(player.Resume(), "Real playback resume was blocked by the unused preview audio sink.");
    await WaitUntil(() => input.SetCount == 2);
    player.Seek(TimeSpan.FromMilliseconds(1500));
    await WaitUntil(() => player.State == PlaybackState.Completed);
    await player.StopAsync();
    player.Dispose();
    await player.StopAsync();
    Equal(0, audio.Calls.Count);
    Check(input.HeldChord is null, "Real playback failed to release its input.");
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

static async Task RangePlayback()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink) { Speed = 2 };
    var snapshots = new SnapshotLog(player);
    var plan = Plan(1600,
        Chord(0, 700, [0], [0], HarmonicaRegister.Lower, true),
        Chord(700, 400, [2]),
        Chord(1100, 500, [4]));
    player.Load(plan);
    player.SetRange(TimeSpan.FromMilliseconds(350), TimeSpan.FromMilliseconds(950));
    var watch = Stopwatch.StartNew();
    player.Start(plan, false, () => true);
    await WaitUntil(() => player.State == PlaybackState.Completed && snapshots.CompletedCount == 1);
    Check(watch.Elapsed.TotalSeconds is > .22 and < .7, "The selected range did not use absolute MIDI time at 2x speed.");
    Near(.95, snapshots.Last!.Position.TotalSeconds, .000001);
    Near(1.6, snapshots.Last!.Duration.TotalSeconds, .000001);
    Check(snapshots.Last!.Message.Contains("所选区间", StringComparison.Ordinal), "Completion did not identify the selected range.");
    var sets = sink.Calls.Where(c => c.Kind == "set").ToArray();
    Equal(2, sets.Length);
    Sequence([0], sets[0].Chord!.KeyIndices);
    Sequence([0], sets[0].RetriggerKeys);
    Equal(-1, sets[0].Chord!.OctaveShift);
    Check(sets[0].Chord!.Semitone, "Starting within a sustained chord lost its modifiers.");
    Sequence([2], sets[1].Chord!.KeyIndices);
    Check(sink.HeldChord is null, "The end marker did not truncate the sustained chord.");
    await Task.Delay(70);
    Equal(1, snapshots.CompletedCount);
    await player.StopAsync();
    Near(.35, player.Position.TotalSeconds, .000001);
    Equal(1, snapshots.CompletedCount);

    // A note starting exactly at the selected end belongs to the excluded remainder.
    var boundaryPlan = Plan(1000, Chord(0, 400, [1]), Chord(400, 600, [7]));
    var setCount = sink.SetCount;
    player.Load(boundaryPlan);
    player.SetRange(TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(400));
    player.Start(boundaryPlan, false, () => true);
    await WaitUntil(() => player.State == PlaybackState.Completed && snapshots.CompletedCount == 2);
    Equal(setCount + 1, sink.SetCount);
    Sequence([1], sink.Calls.Last(c => c.Kind == "set").Chord!.KeyIndices);
    Check(sink.HeldChord is null, "The half-open range retained its final chord.");
    await player.StopAsync();
}

static async Task RangeLifecycle()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    var plan = Plan(3000, Chord(0, 3000, [1]));
    player.Load(plan);
    player.SetRange(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(2400));
    Near(.4, player.Position.TotalSeconds, .000001);
    player.Seek(TimeSpan.FromSeconds(1));
    Near(1, player.Position.TotalSeconds, .000001);
    Equal(0, sink.Calls.Count);
    player.Start(plan, true);
    await WaitUntil(() => player.Position.TotalSeconds > 1.02);
    player.Pause();
    Check(player.Position.TotalSeconds is > 1 and < 1.3, "Startup discarded an in-range pre-start seek.");
    Throws<InvalidOperationException>(() => player.Load(plan));
    await player.StopAsync();
    Near(.4, player.Position.TotalSeconds, .000001);
    Near(.4, player.StartPosition.TotalSeconds, .000001);
    Near(2.4, player.EndPosition.TotalSeconds, .000001);
    player.Start(plan, true);
    await WaitUntil(() => player.Position.TotalSeconds > .42);
    player.Pause();
    Check(player.Position.TotalSeconds < .7, "Restart after stop did not begin at the selected start.");
    await player.StopAsync();
    var next = Plan(900, Chord(0, 900, [4]));
    player.Load(next);
    Near(0, player.StartPosition.TotalSeconds, .000001);
    Near(.9, player.EndPosition.TotalSeconds, .000001);
    Near(0, player.Position.TotalSeconds, .000001);
    Near(.9, player.Duration.TotalSeconds, .000001);
    Equal(0, sink.Calls.Count);
}

static async Task RangeEdits()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    var snapshots = new SnapshotLog(player);
    var plan = Plan(5000, Chord(0, 1000, [0]), Chord(1000, 4000, [6], [6], HarmonicaRegister.Upper, true));
    player.Start(plan, false, () => true);
    await WaitUntil(() => sink.SetCount == 1);
    player.SetRange(TimeSpan.FromMilliseconds(1200), TimeSpan.FromMilliseconds(1600));
    await WaitUntil(() => sink.SetCount == 2);
    var calls = sink.Calls;
    var indices = calls.Select((call, index) => (call, index)).Where(p => p.call.Kind == "set").Select(p => p.index).ToArray();
    Check(calls.Skip(indices[0] + 1).Take(indices[1] - indices[0] - 1).Any(c => c.Kind == "release"),
        "Editing a range while playing left the previous chord held.");
    Sequence([6], sink.HeldChord!.KeyIndices);
    player.Pause();
    player.SetRange(TimeSpan.FromMilliseconds(3000), TimeSpan.FromMilliseconds(3400));
    Near(3, snapshots.Last!.Position.TotalSeconds, .000001);
    Equal(PlaybackState.Paused, player.State);
    Check(sink.HeldChord is null, "Editing a paused range sent input.");
    player.Seek(TimeSpan.FromSeconds(-1));
    Near(3, player.Position.TotalSeconds, .000001);
    player.Seek(TimeSpan.FromSeconds(100));
    Near(3.4, player.Position.TotalSeconds, .000001);
    player.Seek(TimeSpan.FromMilliseconds(3200));
    Check(player.Resume(), "A paused range edit prevented resume.");
    await WaitUntil(() => sink.SetCount == 3);
    Sequence([6], sink.Calls.Last(c => c.Kind == "set").RetriggerKeys);
    await WaitUntil(() => player.State == PlaybackState.Completed && snapshots.CompletedCount == 1);
    Near(3.4, player.Position.TotalSeconds, .000001);
    Check(sink.HeldChord is null, "Completion after a paused range edit left modifiers held.");
    await player.StopAsync();
    Near(3, player.Position.TotalSeconds, .000001);
}

static async Task RangePreview()
{
    var sink = new RecordingInput();
    var audio = new RecordingAudio();
    using var player = new PlaybackEngine(sink, audio) { Speed = .5 };
    var snapshots = new SnapshotLog(player);
    var plan = Plan(2000, Chord(0, 1600, [3]), Chord(1600, 400, [7]));
    player.Load(plan);
    player.SetRange(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1600));
    player.Start(plan, true);
    await WaitUntil(() => snapshots.Last?.Chord is not null);
    Equal(1, audio.SetCount);
    Sequence("v", audio.Calls.Last(c => c.Kind == "set").RetriggerKeys);
    player.Pause();
    Equal(0, audio.HeldNotes.Count);
    player.SetRange(TimeSpan.FromMilliseconds(1100), TimeSpan.FromMilliseconds(1600));
    Equal(PlaybackState.Paused, player.State);
    Equal(1, audio.SetCount);
    player.Seek(TimeSpan.FromMilliseconds(1200));
    player.Speed = 2;
    var watch = Stopwatch.StartNew();
    Check(player.Resume(), "Range preview did not resume.");
    await WaitUntil(() => player.State == PlaybackState.Completed && snapshots.CompletedCount == 1);
    Check(watch.Elapsed.TotalSeconds is > .15 and < .5, "Preview range completion ignored the updated playback speed.");
    Near(1.6, player.Position.TotalSeconds, .000001);
    Equal(2, audio.SetCount);
    Sequence("v", audio.Calls.Last(c => c.Kind == "set").RetriggerKeys);
    Check(audio.Calls.Where(c => c.Kind == "set").All(c => c.Notes.All(note => note.Key == 'v')),
        "Audio played a note starting exactly at the excluded range end.");
    Equal(0, audio.HeldNotes.Count);
    await player.StopAsync();
    Near(1.1, player.Position.TotalSeconds, .000001);
    Equal(0, sink.Calls.Count);
}

static Task RangeValidation()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    Throws<InvalidOperationException>(() => player.SetRange(TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    Throws<ArgumentOutOfRangeException>(() => player.Load(Plan(-1)));
    var empty = Plan(0);
    player.Load(empty);
    Throws<ArgumentOutOfRangeException>(() => player.SetRange(TimeSpan.Zero, TimeSpan.Zero));
    Throws<InvalidOperationException>(() => player.Start(empty, true));
    player.Load(Plan(2000, Chord(0, 2000, [0])));
    player.SetRange(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(1800));
    foreach (var (start, end) in new[] { (-1, 500), (500, 500), (900, 500), (0, 2001), (2000, 2100) })
        Throws<ArgumentOutOfRangeException>(() => player.SetRange(TimeSpan.FromMilliseconds(start), TimeSpan.FromMilliseconds(end)));
    Near(.3, player.StartPosition.TotalSeconds, .000001);
    Near(1.8, player.EndPosition.TotalSeconds, .000001);
    Near(.3, player.Position.TotalSeconds, .000001);
    Equal(0, sink.Calls.Count);
    return Task.CompletedTask;
}

static async Task RangeReleaseFailure()
{
    var sink = new RecordingInput();
    using var player = new PlaybackEngine(sink);
    var snapshots = new SnapshotLog(player);
    player.Start(Plan(3000, Chord(0, 3000, [0])), false, () => true);
    await WaitUntil(() => sink.SetCount == 1);
    sink.FailOnRelease = true;
    player.SetRange(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    Equal(PlaybackState.Faulted, player.State);
    Check(snapshots.Last!.Message.Contains("test release denial", StringComparison.Ordinal), "Range editing hid the native release error.");
    sink.FailOnRelease = false;
    await player.StopAsync();
    Check(sink.HeldChord is null, "Cleanup retry after a range edit failed.");
    Near(1, player.Position.TotalSeconds, .000001);
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
        var audio = new RecordingAudio();
        var player = new PlaybackEngine(sink, audio);
        var preview = index % 2 == 0;
        player.Start(plan, preview, () => true);
        player.Dispose();
        await player.StopAsync();
        Equal(PlaybackState.Stopped, player.State);
        Check(sink.HeldChord is null, "Dispose left input held.");
        Equal(0, audio.HeldNotes.Count);
        var setCount = audio.SetCount;
        await Task.Delay(8);
        Equal(setCount, audio.SetCount);
        if (preview) Equal(0, sink.Calls.Count);
        else Equal(0, audio.Calls.Count);
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
    Equal("F5", new HotkeySettings().Start);
    Check(GlobalHotkeyService.Validate(new("Ctrl+Alt+F8", "Shift+F6", "Win+F7", "Escape")) is null, "Custom modifiers failed.");
    Check(GlobalHotkeyService.Validate(new() { Start = "Ctrl+Alt+P" }) is null, "Custom start shortcut failed.");
    foreach (var key in new[] { "Z", "X", "C", "V", "B", "N", "M", ",", "Comma", "OemComma" })
    {
        Check(GlobalHotkeyService.Validate(new(key)) is not null, $"Bare harmonica key {key} could trigger itself.");
        Check(GlobalHotkeyService.Validate(new() { Start = key }) is not null, $"Bare start shortcut {key} could trigger itself.");
    }
    foreach (var key in new[] { "Ctrl", "Alt+Shift", "Ctrl+Ctrl+F8", "Ctrl+", "MouseLeft", "F25", "F8+F9" })
    {
        Check(GlobalHotkeyService.Validate(new(key)) is not null, $"Invalid shortcut {key} was accepted.");
        Check(GlobalHotkeyService.Validate(new() { Start = key }) is not null, $"Invalid start shortcut {key} was accepted.");
    }
    foreach (var key in new[] { "F6", "F7", "F8", "F9" })
        Check(GlobalHotkeyService.Validate(new() { Start = key }) is not null, $"Start shortcut duplicated {key}.");
    Check(GlobalHotkeyService.Validate(new() { Start = "Ctrl+F8", Pause = "Control+F8" }) is not null,
        "Equivalent start/pause aliases accepted twice.");
    Check(GlobalHotkeyService.Validate(new("F6")) is not null, "Duplicate shortcut accepted.");
    Check(GlobalHotkeyService.Validate(new("Ctrl+F8", "Control+F8")) is not null, "Equivalent shortcut aliases accepted twice.");
    return Task.CompletedTask;
}

static Task StartHotkeySettings()
{
    var directory = Path.Combine(Path.GetTempPath(), "DeltaHarmonica.HotkeyTests", Guid.NewGuid().ToString("N"));
    var settingsPath = Path.Combine(directory, "settings.json");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new SettingsStore(directory);
        File.WriteAllText(settingsPath, """
            { "Hotkeys": { "Pause": "Ctrl+F8", "Previous": "Shift+F6", "Next": "Alt+F7", "Stop": "Escape" } }
            """);
        var migrated = store.Load();
        Equal("F5", migrated.Hotkeys.Start);
        Equal("Ctrl+F8", migrated.Hotkeys.Pause);
        Equal("Shift+F6", migrated.Hotkeys.Previous);
        Equal("Alt+F7", migrated.Hotkeys.Next);
        Equal("Escape", migrated.Hotkeys.Stop);
        Check(GlobalHotkeyService.Validate(migrated.Hotkeys) is null, "Migrated shortcuts conflict.");

        File.WriteAllText(settingsPath, """
            { "Hotkeys": { "Pause": "F5", "Previous": "F10", "Next": "F11", "Stop": "F12" } }
            """);
        var occupiedDefault = store.Load();
        Equal("F13", occupiedDefault.Hotkeys.Start);
        Equal("F5", occupiedDefault.Hotkeys.Pause);
        Equal("F10", occupiedDefault.Hotkeys.Previous);
        Equal("F11", occupiedDefault.Hotkeys.Next);
        Equal("F12", occupiedDefault.Hotkeys.Stop);
        Check(GlobalHotkeyService.Validate(occupiedDefault.Hotkeys) is null, "An old F5 binding blocked the new start shortcut.");

        var customized = migrated with { Hotkeys = migrated.Hotkeys with { Start = "Ctrl+Alt+P" } };
        store.Save(customized);
        Equal(customized.Hotkeys, store.Load().Hotkeys);
        File.WriteAllText(settingsPath, "{}");
        Equal("F5", store.Load().Hotkeys.Start);
        File.WriteAllText(settingsPath, "null");
        Equal("F5", store.Load().Hotkeys.Start);
    }
    finally
    {
        File.Delete(settingsPath);
        File.Delete(settingsPath + ".tmp");
        Directory.Delete(directory);
    }
    return Task.CompletedTask;
}

static Task AudioPreferenceSettings()
{
    var directory = Path.Combine(Path.GetTempPath(), "DeltaHarmonica.AudioPreferenceTests", Guid.NewGuid().ToString("N"));
    var settingsPath = Path.Combine(directory, "settings.json");
    Directory.CreateDirectory(directory);
    try
    {
        var store = new SettingsStore(directory);
        void CheckDefaults(AppSettings settings)
        {
            Equal("balanced", settings.AudioPreset);
            Equal("highest", settings.AudioMelodyMode);
            Equal("none", settings.AudioQuantization);
        }
        CheckDefaults(store.Load());
        // Older settings must gain audio defaults without resetting their other preferences.
        File.WriteAllText(settingsPath, "{ \"Speed\": 1.5, \"Transpose\": 3, \"Playlist\": [\"existing.mid\"] }");
        var migrated = store.Load();
        CheckDefaults(migrated);
        Equal(1.5, migrated.Speed);
        Equal(3, migrated.Transpose);
        Sequence(new[] { "existing.mid" }, migrated.Playlist);
        foreach (var invalid in new[]
        {
            "{ \"AudioPreset\": null, \"AudioMelodyMode\": null, \"AudioQuantization\": null }",
            "{ \"AudioPreset\": \"\", \"AudioMelodyMode\": \"\", \"AudioQuantization\": \"\" }",
            "{ \"AudioPreset\": \"unknown\", \"AudioMelodyMode\": \"raw\", \"AudioQuantization\": \"1/3\" }"
        })
        {
            File.WriteAllText(settingsPath, invalid);
            CheckDefaults(store.Load());
        }
        foreach (var preset in new[] { "solo", "balanced", "ensemble" })
        foreach (var melody in new[] { "highest", "smart", "polyphonic" })
        foreach (var quantization in new[] { "none", "1/4", "1/8", "1/16" })
        {
            store.Save(migrated with { AudioPreset = preset, AudioMelodyMode = melody, AudioQuantization = quantization });
            var restored = store.Load();
            Equal(preset, restored.AudioPreset);
            Equal(melody, restored.AudioMelodyMode);
            Equal(quantization, restored.AudioQuantization);
            Equal(migrated.Speed, restored.Speed);
            Sequence(migrated.Playlist, restored.Playlist);
        }
        File.WriteAllText(settingsPath, "{ \"AudioPreset\": 42 }");
        CheckDefaults(store.Load());
        File.WriteAllText(settingsPath, "{ invalid JSON");
        CheckDefaults(store.Load());
    }
    finally
    {
        File.Delete(settingsPath);
        File.Delete(settingsPath + ".tmp");
        Directory.Delete(directory);
    }
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
static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
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
    private int _completedCount;
    public SnapshotLog(PlaybackEngine player) => player.Updated += snapshot =>
    {
        lock (_gate)
        {
            _last = snapshot;
            if (snapshot.State == PlaybackState.Completed) _completedCount++;
        }
    };
    public PlaybackSnapshot? Last { get { lock (_gate) return _last; } }
    public int CompletedCount { get { lock (_gate) return _completedCount; } }
}

sealed record InputCall(string Kind, InputChord? Chord, IReadOnlyList<int> RetriggerKeys, double TimeSeconds);

sealed record AudioCall(string Kind, IReadOnlyList<MappedNote> Notes, IReadOnlyList<char> RetriggerKeys, double TimeSeconds);

sealed class RecordingAudio : IPreviewAudioSink
{
    private readonly object _gate = new();
    private readonly List<AudioCall> _calls = [];
    private IReadOnlyList<MappedNote> _heldNotes = [];
    private int _failOnRelease;
    public bool FailOnSet { get; init; }
    public bool FailOnRelease { get => Volatile.Read(ref _failOnRelease) != 0; set => Volatile.Write(ref _failOnRelease, value ? 1 : 0); }
    public IReadOnlyList<AudioCall> Calls { get { lock (_gate) return _calls.ToArray(); } }
    public IReadOnlyList<MappedNote> HeldNotes { get { lock (_gate) return _heldNotes.ToArray(); } }
    public int SetCount { get { lock (_gate) return _calls.Count(c => c.Kind == "set"); } }

    public void SetChord(IReadOnlyList<MappedNote> notes, IReadOnlyList<char> retriggerKeys)
    {
        lock (_gate)
        {
            _heldNotes = notes.ToArray();
            _calls.Add(new("set", _heldNotes, retriggerKeys.ToArray(), Now()));
            if (FailOnSet) throw new InvalidOperationException("test audio output failure");
        }
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            _calls.Add(new("release", [], [], Now()));
            if (FailOnRelease) throw new InvalidOperationException("test audio release denial");
            _heldNotes = [];
        }
    }

    private static double Now() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}

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
