using DeltaHarmonica.Core;

namespace DeltaHarmonica.App.Services;

/// <summary>
/// Built-in, band-limited reed sound. The scheduler's effective pitch determines the
/// frequency, including register changes, semitones, transposition and octave folding.
/// All access is serialized by PreviewAudioService; this class does not use a device.
/// </summary>
internal sealed class HarmonicaSynthesizer
{
    internal const int SampleRate = 48_000;
    internal const double MaximumAmplitude = .78;
    private const int MaximumReleaseVoices = 16;
    private readonly Dictionary<char, Voice> _held = [];
    private readonly List<Voice> _releasing = [];

    internal static double FrequencyForPitch(int effectivePitch)
    {
        if (effectivePitch is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(effectivePitch));
        return 440 * Math.Pow(2, (effectivePitch - 69) / 12d);
    }

    internal void SetChord(IReadOnlyList<MappedNote> notes, IReadOnlyList<char> retriggerKeys)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(retriggerKeys);
        // Validate the whole update before mutating the currently sounding chord.
        var next = new Dictionary<char, MappedNote>();
        foreach (var note in notes)
        {
            ArgumentNullException.ThrowIfNull(note);
            if (!HarmonicaMapper.Keys.Contains(note.Key))
                throw new ArgumentException("预览包含无效的口琴按键。", nameof(notes));
            _ = FrequencyForPitch(note.EffectivePitch);
            if (!next.TryAdd(note.Key, note))
                throw new ArgumentException("预览和弦包含重复的口琴按键。", nameof(notes));
        }

        var retrigger = retriggerKeys.ToHashSet();
        foreach (var key in _held.Keys.ToArray())
        {
            var voice = _held[key];
            if (!next.TryGetValue(key, out var note) || note.EffectivePitch != voice.Pitch || retrigger.Contains(key))
            {
                voice.BeginRelease();
                _releasing.Add(voice);
                _held.Remove(key);
            }
        }
        // Rapid scrubbing/retriggering must not accumulate an unbounded number of tails.
        if (_releasing.Count > MaximumReleaseVoices)
            _releasing.RemoveRange(0, _releasing.Count - MaximumReleaseVoices);
        foreach (var (key, note) in next)
            if (!_held.ContainsKey(key)) _held.Add(key, new Voice(note.EffectivePitch));
    }

    internal void ReleaseAll()
    {
        _held.Clear();
        _releasing.Clear();
    }

    internal void Render(Span<short> destination)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            var mixed = 0d;
            var level = 0d;
            foreach (var voice in _held.Values) { level += voice.EnvelopeLevel; mixed += voice.NextSample(); }
            foreach (var voice in _releasing) { level += voice.EnvelopeLevel; mixed += voice.NextSample(); }
            // Normalize by the actual envelopes, so changing a chord does not step
            // the gain. Linear mixing avoids tanh's buzzy harmonics/intermodulation.
            var sample = mixed * .36 / Math.Max(1, level);
            destination[index] = (short)Math.Round(Math.Clamp(sample, -MaximumAmplitude, MaximumAmplitude) * short.MaxValue);
        }
        _releasing.RemoveAll(voice => voice.Finished);
    }

    private sealed class Voice
    {
        private static readonly double[] Harmonics = [1, .30, .20, .10, .055, .03];
        private readonly double _frequency;
        private readonly int _harmonicCount;
        private double _phase;
        private long _age;
        private int _releaseAge = -1;
        private double _releaseLevel;

        internal Voice(int pitch)
        {
            Pitch = pitch;
            _frequency = FrequencyForPitch(pitch);
            _harmonicCount = Math.Min(Harmonics.Length, Math.Max(1, (int)(SampleRate * .45 / _frequency)));
        }

        internal int Pitch { get; }
        internal bool Finished => _releaseAge >= SampleRate * .04;

        internal void BeginRelease()
        {
            _releaseLevel = AttackLevel();
            _releaseAge = 0;
        }

        // Raised-cosine envelopes reach their endpoints with zero slope. The
        // release ends at exact silence rather than truncating an exponential tail.
        private double AttackLevel() => .5 - .5 * Math.Cos(Math.PI * Math.Min(1, _age / (SampleRate * .012)));
        internal double EnvelopeLevel => _releaseAge < 0 ? AttackLevel() :
            _releaseLevel * (.5 + .5 * Math.Cos(Math.PI * Math.Min(1, _releaseAge / (SampleRate * .04))));

        internal double NextSample()
        {
            if (Finished) return 0;
            var time = _age / (double)SampleRate;
            var envelope = EnvelopeLevel;
            var sample = 0d;
            for (var harmonic = 1; harmonic <= _harmonicCount; harmonic++)
                sample += Harmonics[harmonic - 1] * Math.Sin(_phase * harmonic);
            // A subtle breath-like pulse and vibrato make sustained notes sound like a reed.
            sample *= envelope * (.98 + .02 * Math.Sin(2 * Math.PI * 5.5 * time));
            var vibrato = 1 + .0015 * Math.Sin(2 * Math.PI * 4.8 * time) * Math.Min(1, time / .1);
            _phase = (_phase + 2 * Math.PI * _frequency * vibrato / SampleRate) % (2 * Math.PI);
            _age++;
            if (_releaseAge >= 0) _releaseAge++;
            return sample;
        }
    }
}
