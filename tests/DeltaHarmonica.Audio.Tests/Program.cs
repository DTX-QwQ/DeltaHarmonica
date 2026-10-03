using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using DeltaHarmonica.App.Services;
using DeltaHarmonica.Core;
using static DeltaHarmonica.App.Services.PreviewAudioService;

// The default suite renders PCM in memory and replaces the native boundary.
// Pass --device-smoke to also play a brief chord on the real default Windows device.
var tests = new List<(string Name, Func<Task> Run)>
{
    ("MIDI pitch frequency includes exact concert A and octave ratios", Sync(PitchFrequency)),
    ("rendered sound uses EffectivePitch instead of the source pitch", Sync(MappedPitch)),
    ("compatible polyphony contains every chord frequency", Sync(Polyphony)),
    ("held notes remain sample-continuous across adjacent chords", Sync(SustainContinuity)),
    ("removing one note preserves the other voice", Sync(PartialRelease)),
    ("retrigger restarts the onset while allowing a short smooth release", Sync(Retrigger)),
    ("the same game key can change effective pitch", Sync(PitchChange)),
    ("dense chords and repeated retriggers retain headroom", Sync(Limiting)),
    ("sustained chords mix linearly without intermodulation distortion", Sync(LinearMixing)),
    ("note-off starts continuously and fades to exact silence", Sync(SmoothNoteOff)),
    ("note-off fades and ReleaseAll clears both held notes and tails", Sync(Silence)),
    ("invalid chord updates leave the sounding chord unchanged", Sync(InvalidUpdate)),
    ("WinMM structures match the Windows native ABI", Sync(NativeLayout)),
    ("device initialization is lazy, reusable, and disposal stops writes", LazyLifecycle),
    ("empty chords fade naturally without resetting queued audio", EmptyChord),
    ("queued device buffers preserve PCM through batched completion and wraparound", QueuedContinuity),
    ("device open and prepare failures propagate and allow clean retry", InitializationFailure),
    ("a failed close after partial initialization cannot produce false success", FailedInitializationCleanup),
    ("background write failure is reported by SetChord and ReleaseAll", BackgroundFailure),
    ("a single sustained preview faults at completion if its output worker failed", EngineFailure)
};
if (args.Contains("--device-smoke")) tests.Add(("real Windows device open, sustained chord, retrigger, reset and dispose", DeviceSmoke));
var failures = 0;
foreach (var (name, run) in tests)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}: {exception}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} audio tests passed. " +
    (args.Contains("--device-smoke") ? "Real default output tested; no game input injected." : "No audio device or game input used."));
return failures == 0 ? 0 : 1;

static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

static void PitchFrequency()
{
    Near(440, HarmonicaSynthesizer.FrequencyForPitch(69), 1e-9);
    Near(261.6255653, HarmonicaSynthesizer.FrequencyForPitch(60), 1e-6);
    Near(2, HarmonicaSynthesizer.FrequencyForPitch(72) / HarmonicaSynthesizer.FrequencyForPitch(60), 1e-9);
    Throws<ArgumentOutOfRangeException>(() => HarmonicaSynthesizer.FrequencyForPitch(-1));
    Throws<ArgumentOutOfRangeException>(() => HarmonicaSynthesizer.FrequencyForPitch(128));
}

static void MappedPitch()
{
    var synth = new HarmonicaSynthesizer();
    synth.SetChord([Note('z', 69, 45)], ['z']);
    Render(synth, 4800);
    var samples = Render(synth, 24_000);
    var strongest = Enumerable.Range(0, 81).Select(index => 430 + index * .25)
        .MaxBy(frequency => ToneMagnitude(samples, frequency));
    Near(440, strongest, 1.5);
    Check(ToneMagnitude(samples, 440) > ToneMagnitude(samples, 110) * 100,
        "A source pitch was used instead of the mapped register pitch.");

    Check(HarmonicaMapper.TryMap(96, new MappingOptions(), out var folded), "A folded note was not mapped.");
    Check(folded!.WasOctaveFolded, "The test note must exercise octave folding.");
    synth.ReleaseAll();
    synth.SetChord([folded], [folded.Key]);
    Render(synth, 4800);
    samples = Render(synth, 24_000);
    Check(ToneMagnitude(samples, HarmonicaSynthesizer.FrequencyForPitch(folded.EffectivePitch)) > 2000,
        "The folded effective pitch is missing from the sound.");
}

static void Polyphony()
{
    var synth = new HarmonicaSynthesizer();
    synth.SetChord([Note('z', 60), Note('c', 64), Note('b', 67)], ['z', 'c', 'b']);
    Render(synth, 4800);
    var samples = Render(synth, 24_000);
    foreach (var pitch in new[] { 60, 64, 67 })
        Check(ToneMagnitude(samples, HarmonicaSynthesizer.FrequencyForPitch(pitch)) > 1800,
            $"The {pitch} voice is absent from the polyphonic output.");
}

static void SustainContinuity()
{
    var synth = new HarmonicaSynthesizer();
    var control = new HarmonicaSynthesizer();
    MappedNote[] notes = [Note('z', 60), Note('c', 64)];
    synth.SetChord(notes, ['z', 'c']); control.SetChord(notes, ['z', 'c']);
    Same(Render(control, 4096), Render(synth, 4096));
    synth.SetChord(notes, []);
    Same(Render(control, 4096), Render(synth, 4096));
}

static void PartialRelease()
{
    var synth = new HarmonicaSynthesizer();
    var heldControl = new HarmonicaSynthesizer();
    synth.SetChord([Note('z', 60), Note('c', 64)], ['z', 'c']);
    heldControl.SetChord([Note('z', 60)], ['z']);
    Render(synth, 4096); Render(heldControl, 4096);
    synth.SetChord([Note('z', 60)], []);
    Render(synth, 1920); Render(heldControl, 1920);
    Same(Render(heldControl, 4096), Render(synth, 4096));
}

static void Retrigger()
{
    var synth = new HarmonicaSynthesizer();
    var fresh = new HarmonicaSynthesizer();
    synth.SetChord([Note('z', 69)], ['z']);
    Render(synth, 8192);
    synth.SetChord([Note('z', 69)], ['z']);
    fresh.SetChord([Note('z', 69)], ['z']);
    // Once the previous voice's 40 ms tail ends, the new onset has the same
    // timeline as a freshly started note rather than the old sustained voice.
    Render(synth, 1920); Render(fresh, 1920);
    Same(Render(fresh, 4096), Render(synth, 4096));
}

static void PitchChange()
{
    var synth = new HarmonicaSynthesizer();
    var fresh = new HarmonicaSynthesizer();
    synth.SetChord([Note('z', 60)], ['z']); Render(synth, 4096);
    synth.SetChord([Note('z', 72)], []);
    fresh.SetChord([Note('z', 72)], ['z']);
    Render(synth, 1920); Render(fresh, 1920);
    Same(Render(fresh, 4096), Render(synth, 4096));
}

static void Limiting()
{
    var synth = new HarmonicaSynthesizer();
    var notes = HarmonicaMapper.Keys.Select(key => Note(key, 69)).ToArray();
    synth.SetChord(notes, HarmonicaMapper.Keys.ToCharArray());
    var samples = Render(synth, 8192);
    for (var index = 0; index < 100; index++)
    {
        synth.SetChord(notes, HarmonicaMapper.Keys.ToCharArray());
        samples = samples.Concat(Render(synth, 48)).ToArray();
    }
    var peak = samples.Max(sample => Math.Abs((int)sample));
    Check(peak > 8_000, "Chord normalization made the sound inaudibly quiet.");
    Check(peak <= Math.Ceiling(short.MaxValue * HarmonicaSynthesizer.MaximumAmplitude), "Dense retriggers exceeded PCM headroom.");
}

static void LinearMixing()
{
    var chord = new HarmonicaSynthesizer();
    var first = new HarmonicaSynthesizer();
    var second = new HarmonicaSynthesizer();
    chord.SetChord([Note('z', 60), Note('c', 64)], ['z', 'c']);
    first.SetChord([Note('z', 60)], ['z']); second.SetChord([Note('c', 64)], ['c']);
    Render(chord, 4800); Render(first, 4800); Render(second, 4800);
    var combined = Render(chord, 12_000);
    var a = Render(first, 12_000); var b = Render(second, 12_000);
    // Once both attacks settle, a two-note chord must be a linear average.
    // Nonlinear saturation creates new frequencies that fail this comparison.
    for (var i = 0; i < combined.Length; i++) Near((a[i] + b[i]) / 2d, combined[i], 1);
}

static void SmoothNoteOff()
{
    foreach (var pitch in new[] { 48, 60, 69, 85 })
    {
        var synth = new HarmonicaSynthesizer(); var held = new HarmonicaSynthesizer();
        synth.SetChord([Note('z', pitch)], ['z']); held.SetChord([Note('z', pitch)], ['z']);
        Render(synth, 4813); Render(held, 4813);
        synth.SetChord([], []);
        var released = Render(synth, 1921); var control = Render(held, 1921);
        Equal(control[0], released[0]);
        Check(Math.Abs(released[1919]) <= 1, "A note-off truncated an audible release tail.");
        Equal((short)0, released[1920]);
    }
}

static void Silence()
{
    var synth = new HarmonicaSynthesizer();
    synth.SetChord([Note('z', 60)], ['z']); Render(synth, 4800);
    synth.SetChord([], []);
    Check(Render(synth, 480).Any(sample => sample != 0), "Note-off should fade the reed voice smoothly.");
    Render(synth, 1920);
    Check(Render(synth, 480).All(sample => sample == 0), "The release tail never ended.");
    synth.SetChord([Note('c', 64)], ['c']); Render(synth, 4800);
    synth.SetChord([Note('c', 64)], ['c']);
    synth.ReleaseAll();
    Check(Render(synth, 4800).All(sample => sample == 0), "ReleaseAll retained a held or releasing voice.");
    synth.ReleaseAll();
}

static void InvalidUpdate()
{
    var synth = new HarmonicaSynthesizer();
    var control = new HarmonicaSynthesizer();
    synth.SetChord([Note('z', 60)], ['z']); control.SetChord([Note('z', 60)], ['z']);
    Render(synth, 2000); Render(control, 2000);
    Throws<ArgumentOutOfRangeException>(() => synth.SetChord([Note('c', 64), Note('z', 128)], []));
    Throws<ArgumentException>(() => synth.SetChord([Note('z', 60), Note('z', 64)], []));
    Throws<ArgumentException>(() => synth.SetChord([Note('q', 60)], []));
    Same(Render(control, 4096), Render(synth, 4096));
}

static void NativeLayout()
{
    Equal(18, Marshal.SizeOf<WaveFormatEx>());
    Equal(Environment.Is64BitProcess ? 48 : 32, Marshal.SizeOf<WaveHeader>());
    Equal(Environment.Is64BitProcess ? 24 : 16, Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Flags)).ToInt32());
    Equal(Environment.Is64BitProcess ? 32 : 24, Marshal.OffsetOf<WaveHeader>(nameof(WaveHeader.Next)).ToInt32());
    Equal(14, Marshal.OffsetOf<WaveFormatEx>(nameof(WaveFormatEx.BitsPerSample)).ToInt32());
}

static async Task LazyLifecycle()
{
    var native = new RecordingWaveOut();
    var audio = new PreviewAudioService(native);
    Equal(0, native.OpenCalls);
    audio.ReleaseAll(); audio.SetChord([], []);
    Equal(0, native.OpenCalls);
    audio.SetChord([Note('z', 60)], ['z']);
    await Until(() => native.WriteCalls >= 3);
    Equal(1, native.OpenCalls); Equal(3, native.PrepareCalls);
    Equal(48_000u, native.Format.SamplesPerSecond); Equal((ushort)16, native.Format.BitsPerSample);
    Check(native.LastPcm.Any(sample => sample != 0), "The worker queued silence for a sounding chord.");
    audio.SetChord([Note('z', 60), Note('c', 64)], ['c']);
    Equal(1, native.OpenCalls);
    audio.ReleaseAll();
    var writes = native.WriteCalls;
    await Until(() => native.WriteCalls > writes);
    Check(native.LastPcm.All(sample => sample == 0), "Reset retained queued sound.");
    audio.Dispose();
    Equal(3, native.UnprepareCalls); Equal(1, native.CloseCalls);
    writes = native.WriteCalls;
    await Task.Delay(130);
    Equal(writes, native.WriteCalls);
    audio.ReleaseAll(); audio.Dispose();
    Throws<ObjectDisposedException>(() => audio.SetChord([Note('z', 60)], ['z']));
}

static async Task EmptyChord()
{
    var native = new RecordingWaveOut { QueuedPlayback = true };
    using var audio = new PreviewAudioService(native);
    audio.SetChord([Note('z', 60)], ['z']);
    await Until(() => native.WriteCalls >= 3);
    var resets = native.ResetCalls;
    audio.SetChord([], []);
    Equal(resets, native.ResetCalls);
    var writes = native.WriteCalls;
    native.Consume(3);
    await Until(() => native.WriteCalls == writes + 3);
    var fade = native.SubmittedBlocks.Skip(writes).ToArray();
    Check(fade[0].Any(sample => sample != 0), "Musical note-off hard-cut the sounding voice.");
    Check(fade[^1].All(sample => sample == 0), "The release did not settle to silence.");
    Equal(resets, native.ResetCalls);
    audio.ReleaseAll();
    Equal(resets + 1, native.ResetCalls);
}

static async Task QueuedContinuity()
{
    var native = new RecordingWaveOut { QueuedPlayback = true };
    using var audio = new PreviewAudioService(native);
    MappedNote[] notes = [Note('z', 60), Note('c', 64)];
    audio.SetChord(notes, ['z', 'c']);
    await Until(() => native.WriteCalls == 3);
    Equal(3, native.QueuedCount);
    Check(native.SubmittedBlocks.Sum(block => block.Length) / 48_000d >= .06,
        "The output queue has less than 60 ms of scheduling headroom.");
    var initialWrites = native.WriteCalls;
    await Task.Delay(120);
    Equal(initialWrites, native.WriteCalls); // No overwrites while the device owns all three blocks.
    foreach (var count in new[] { 1, 2, 1, 3, 2, 1 })
    {
        var previousWrites = native.WriteCalls;
        native.Consume(count);
        await Until(() => native.WriteCalls == previousWrites + count);
        Equal(3, native.QueuedCount);
    }
    var actual = native.SubmittedBlocks.SelectMany(block => block).ToArray();
    var control = new HarmonicaSynthesizer(); control.SetChord(notes, ['z', 'c']);
    Same(Render(control, actual.Length), actual);
    var stopWrites = native.WriteCalls;
    audio.ReleaseAll();
    await Until(() => native.WriteCalls >= stopWrites + 3);
    Check(native.SubmittedBlocks.TakeLast(3).All(block => block.All(sample => sample == 0)),
        "An explicit transport stop kept queued sound.");
}

static async Task InitializationFailure()
{
    var native = new RecordingWaveOut { OpenError = 6 };
    using var audio = new PreviewAudioService(native);
    var exception = Throws<InvalidOperationException>(() => audio.SetChord([Note('z', 60)], ['z']));
    Check(exception.Message.Contains("6", StringComparison.Ordinal), "Opening lost the native error code.");
    Equal(0, native.PrepareCalls);
    native.OpenError = 0; native.FailPrepareAt = 2;
    Throws<InvalidOperationException>(() => audio.SetChord([Note('z', 60)], ['z']));
    Equal(1, native.UnprepareCalls); Equal(1, native.CloseCalls);
    native.FailPrepareAt = 0;
    audio.SetChord([Note('z', 60)], ['z']);
    await Until(() => native.WriteCalls >= 3);
    Equal(3, native.OpenCalls);
}

static Task FailedInitializationCleanup()
{
    var native = new RecordingWaveOut { FailPrepareAt = 2, CloseError = 33 };
    var audio = new PreviewAudioService(native);
    Throws<InvalidOperationException>(() => audio.SetChord([Note('z', 60)], ['z']));
    native.FailPrepareAt = 0;
    var exception = Throws<InvalidOperationException>(() => audio.SetChord([Note('z', 60)], ['z']));
    Check(exception.InnerException is not null, "The partial initialization error was not retained.");
    Equal(1, native.OpenCalls); Equal(0, native.WriteCalls);
    audio.Dispose();
    native.CloseError = 0;
    audio.Dispose();
    Equal(3, native.CloseCalls);
    return Task.CompletedTask;
}

static async Task BackgroundFailure()
{
    var native = new RecordingWaveOut { WriteError = 11 };
    using var audio = new PreviewAudioService(native);
    audio.SetChord([Note('z', 60)], ['z']);
    await Until(() => native.ResetCalls > 0);
    var exception = Throws<InvalidOperationException>(() => audio.SetChord([Note('z', 60)], []));
    Check(exception.InnerException?.Message.Contains("11", StringComparison.Ordinal) == true, "The asynchronous write error was lost.");
    Throws<InvalidOperationException>(audio.ReleaseAll);
    audio.ReleaseAll(); // A fault report must not prevent a subsequent cleanup retry.
}

static async Task EngineFailure()
{
    var native = new RecordingWaveOut { WriteError = 11 };
    using var audio = new PreviewAudioService(native);
    var input = new ForbiddenInput();
    using var player = new PlaybackEngine(input, audio);
    string lastMessage = "";
    player.Updated += snapshot => lastMessage = snapshot.Message;
    var duration = TimeSpan.FromMilliseconds(250);
    var note = Note('z', 60);
    player.Start(new PlaybackPlan
    {
        Duration = duration,
        Chords = [new ScheduledChord(TimeSpan.Zero, duration, [note], HarmonicaRegister.Normal, false, ['z'])]
    }, preview: true);
    await Until(() => player.State == PlaybackState.Faulted);
    Check(lastMessage.Contains("11", StringComparison.Ordinal), "The scheduler did not publish the native output failure.");
    Equal(0, input.Calls);
    await player.StopAsync();
    Equal(PlaybackState.Stopped, player.State);
}

static async Task DeviceSmoke()
{
    Check(OperatingSystem.IsWindows(), "The optional device smoke requires Windows.");
    using var audio = new PreviewAudioService();
    audio.SetChord([Note('z', 60), Note('c', 64), Note('b', 67)], ['z', 'c', 'b']);
    await Task.Delay(250);
    audio.SetChord([Note('z', 60), Note('c', 64), Note('b', 67)], ['c']);
    await Task.Delay(200);
    audio.SetChord([Note('z', 72)], ['z']);
    await Task.Delay(150);
    audio.ReleaseAll();
    await Task.Delay(60);
}

static MappedNote Note(char key, int effective, int? source = null) =>
    new(key, HarmonicaRegister.Normal, false, effective, source ?? effective, source ?? effective, false);
static short[] Render(HarmonicaSynthesizer synth, int count) { var samples = new short[count]; synth.Render(samples); return samples; }
static double ToneMagnitude(short[] samples, double frequency)
{
    var real = 0d; var imaginary = 0d;
    for (var index = 0; index < samples.Length; index++)
    {
        var window = .5 - .5 * Math.Cos(2 * Math.PI * index / (samples.Length - 1));
        var phase = 2 * Math.PI * frequency * index / HarmonicaSynthesizer.SampleRate;
        real += samples[index] * window * Math.Cos(phase);
        imaginary -= samples[index] * window * Math.Sin(phase);
    }
    return 4 * Math.Sqrt(real * real + imaginary * imaginary) / samples.Length;
}
static async Task Until(Func<bool> condition)
{
    var watch = Stopwatch.StartNew();
    while (!condition())
    {
        if (watch.ElapsedMilliseconds > 3000) throw new TimeoutException("Audio worker condition was not met.");
        await Task.Delay(4);
    }
}
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual) where T : notnull
{ if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}."); }
static void Near(double expected, double actual, double tolerance)
{ if (Math.Abs(expected - actual) > tolerance) throw new Exception($"Expected {expected} ± {tolerance}, got {actual}."); }
static void Same(short[] expected, short[] actual)
{ if (!expected.SequenceEqual(actual)) throw new Exception("PCM continuity differs from the independently rendered control voice."); }
static T Throws<T>(Action action) where T : Exception
{ try { action(); } catch (T exception) { return exception; } throw new Exception($"Expected {typeof(T).Name}."); }

sealed class RecordingWaveOut : IWaveOutApi
{
    private readonly object _gate = new();
    private readonly List<IntPtr> _prepared = [];
    private readonly Queue<IntPtr> _queued = [];
    private readonly List<short[]> _submitted = [];
    private EventWaitHandle? _callback;
    private short[] _lastPcm = [];
    private int _openCalls, _prepareCalls, _writeCalls, _resetCalls, _unprepareCalls, _closeCalls;
    public uint OpenError { get; set; }
    public uint WriteError { get; init; }
    public uint CloseError { get; set; }
    public int FailPrepareAt { get; set; }
    public bool QueuedPlayback { get; init; }
    public int QueuedCount { get { lock (_gate) return _queued.Count; } }
    public short[][] SubmittedBlocks { get { lock (_gate) return _submitted.Select(block => block.ToArray()).ToArray(); } }
    public int OpenCalls => Volatile.Read(ref _openCalls);
    public int PrepareCalls => Volatile.Read(ref _prepareCalls);
    public int WriteCalls => Volatile.Read(ref _writeCalls);
    public int ResetCalls => Volatile.Read(ref _resetCalls);
    public int UnprepareCalls => Volatile.Read(ref _unprepareCalls);
    public int CloseCalls => Volatile.Read(ref _closeCalls);
    public WaveFormatEx Format { get; private set; }
    public short[] LastPcm { get { lock (_gate) return _lastPcm.ToArray(); } }
    public uint Open(out IntPtr device, ref WaveFormatEx format, IntPtr callbackEvent)
    {
        Interlocked.Increment(ref _openCalls);
        Format = format;
        device = OpenError == 0 ? new IntPtr(1234) : IntPtr.Zero;
        if (QueuedPlayback && OpenError == 0)
        {
            _callback = new EventWaitHandle(false, EventResetMode.AutoReset);
            _callback.SafeWaitHandle.Dispose();
            _callback.SafeWaitHandle = new SafeWaitHandle(callbackEvent, ownsHandle: false);
        }
        return OpenError;
    }
    public uint Prepare(IntPtr device, IntPtr header, uint headerSize)
    {
        var count = Interlocked.Increment(ref _prepareCalls);
        if (count == FailPrepareAt) return 5;
        lock (_gate) _prepared.Add(header);
        var value = Marshal.PtrToStructure<WaveHeader>(header); value.Flags = 2;
        Marshal.StructureToPtr(value, header, false);
        return 0;
    }
    public uint Write(IntPtr device, IntPtr header, uint headerSize)
    {
        var value = Marshal.PtrToStructure<WaveHeader>(header);
        var pcm = new short[value.BufferLength / sizeof(short)];
        Marshal.Copy(value.Data, pcm, 0, pcm.Length);
        lock (_gate)
        {
            if (_queued.Contains(header)) throw new Exception("The worker overwrote a device-owned buffer.");
            _lastPcm = pcm; _submitted.Add(pcm);
            if (QueuedPlayback) _queued.Enqueue(header);
            value.Flags = QueuedPlayback ? 0x12u : 3u;
            Marshal.StructureToPtr(value, header, false);
            Interlocked.Increment(ref _writeCalls);
        }
        return WriteError;
    }
    public void Consume(int count)
    {
        lock (_gate)
        {
            if (count > _queued.Count) throw new Exception("The test consumed an empty device queue.");
            for (var i = 0; i < count; i++)
            {
                var header = _queued.Dequeue(); var value = Marshal.PtrToStructure<WaveHeader>(header);
                value.Flags = 3; Marshal.StructureToPtr(value, header, false);
            }
            _callback?.Set();
        }
    }
    public uint Reset(IntPtr device)
    {
        lock (_gate)
        {
            Consume(_queued.Count);
            Interlocked.Increment(ref _resetCalls);
        }
        return 0;
    }
    public uint Unprepare(IntPtr device, IntPtr header, uint headerSize)
    {
        Interlocked.Increment(ref _unprepareCalls);
        lock (_gate) _prepared.Remove(header);
        return 0;
    }
    public uint Close(IntPtr device)
    {
        Interlocked.Increment(ref _closeCalls);
        if (CloseError == 0) { _callback?.Dispose(); _callback = null; }
        return CloseError;
    }
    public string DescribeError(uint error) => $"test audio error {error}";
}

sealed class ForbiddenInput : IInputSink
{
    public int Calls { get; private set; }
    public void SendChord(InputChord chord) { Calls++; throw new Exception("Preview injected game input."); }
    public void SetChord(InputChord chord, IReadOnlyList<int> retriggerKeys) { Calls++; throw new Exception("Preview injected game input."); }
    public void ReleaseAll() { Calls++; throw new Exception("Preview touched the game input sink."); }
}
