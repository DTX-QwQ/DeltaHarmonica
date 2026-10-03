using System.Text;
using DeltaHarmonica.Core;

var tests = new (string Name, Action Run)[]
{
    ("running status, velocity-zero note-off and default tempo", RunningStatus),
    ("format 1 global tempo map and long notes across tempo changes", TempoChanges),
    ("track name, channels and programs", TrackMetadata),
    ("percussion filtering can be disabled", Percussion),
    ("repeated overlapping pitches use matching note-off events", RepeatedPitch),
    ("missing note-off closes at end of track with warning", MissingNoteOff),
    ("malformed headers and event streams fail clearly", MalformedFiles),
    ("bounded stream reading and event limits", ReadLimits),
    ("all chromatic pitches in the default range map exactly", ExactMapping),
    ("octave folding, skipping and transposition", MappingPolicies),
    ("melody selection segments overlaps without overlap", MelodySelection),
    ("compatible chord honors global mouse modifiers", CompatibleChord),
    ("alternative representations retain compatible notes", AlternativeRepresentations),
    ("repeated same key retains retrigger boundaries", Retrigger),
    ("unrelated note boundaries do not retrigger melody", UnrelatedBoundary),
    ("track filtering and explicit empty selection", TrackFiltering),
    ("bundled Twinkle Twinkle sample parses and maps without warnings", BundledSample)
};
var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} tests passed.");
return failures == 0 ? 0 : 1;

static void RunningStatus()
{
    var song = Read(Track(E(0, 0x90, 60, 100), E(240, 64, 100), E(240, 60, 0), E(240, 64, 0)));
    Equal(2, song.Notes.Count);
    Near(0.5, song.Notes[0].Duration.TotalSeconds);
    Near(0.25, song.Notes[1].Start.TotalSeconds);
    Near(0.5, song.Notes[1].Duration.TotalSeconds);
    Near(0.75, song.Duration.TotalSeconds);
}

static void TempoChanges()
{
    var conductor = Track(E(0, 0xff, 0x51, 3, 0x07, 0xa1, 0x20), E(480, 0xff, 0x51, 3, 0x0f, 0x42, 0x40));
    var music = Track(E(0, 0x90, 60, 100), E(960, 0x80, 60, 0));
    var song = ReadMidi(Midi(1, 480, conductor, music));
    Equal(2, song.TempoChanges.Count);
    Near(1.5, song.Notes.Single().Duration.TotalSeconds);
    Near(0.5, song.TempoChanges[1].Time.TotalSeconds);
    Near(60, song.TempoChanges[1].BeatsPerMinute);
    // Conflicting same-tick tempo events resolve deterministically to the last event.
    var sameTick = Read(Track(E(0, 0xff, 0x51, 3, 0x07, 0xa1, 0x20), E(0, 0xff, 0x51, 3, 0x0f, 0x42, 0x40),
        E(0, 0x90, 60, 100), E(480, 0x80, 60, 0)));
    Near(1, sameTick.Duration.TotalSeconds);
}

static void TrackMetadata()
{
    var song = Read(Track(E(0, 0xff, 3, 5, 80, 105, 97, 110, 111), E(0, 0xc2, 24), E(0, 0x92, 60, 100), E(480, 0x82, 60, 0)));
    Equal("Piano", song.Tracks[0].Name);
    Equal(24, song.Tracks[0].Programs.Single());
    Equal(2, song.Tracks[0].Channels.Single());
    Equal(1, song.Tracks[0].NoteCount);
}

static void Percussion()
{
    var bytes = Midi(0, 480, Track(E(0, 0x99, 60, 100), E(480, 0x89, 60, 0)));
    Equal(0, ReadMidi(bytes).Notes.Count);
    using var stream = new MemoryStream(bytes);
    Equal(1, MidiReader.Read(stream, new MidiReadOptions { IgnorePercussion = false }).Notes.Count);
    True(stream.CanRead, "Reader must leave caller stream open.");
}

static void RepeatedPitch()
{
    var song = Read(Track(E(0, 0x90, 60, 90), E(240, 0x90, 60, 100), E(240, 0x80, 60, 0), E(240, 0x80, 60, 0)));
    Equal(2, song.Notes.Count);
    Near(0.5, song.Notes[0].Duration.TotalSeconds);
    Near(0.5, song.Notes[1].Duration.TotalSeconds);
}

static void MissingNoteOff()
{
    var song = Read(Track(E(0, 0x90, 60, 100), E(480, 0xff, 0x2f, 0)));
    Near(0.5, song.Notes.Single().Duration.TotalSeconds);
    True(song.Warnings.Any(w => w.Contains("缺少释放")), "Missing note-off must be reported.");
}

static void MalformedFiles()
{
    Throws<MidiFormatException>(() => ReadMidi([0, 1, 2]));
    Throws<MidiFormatException>(() => ReadMidi(Midi(2, 480, Track())));
    Throws<MidiFormatException>(() => ReadMidi(Midi(0, 0xe728, Track())));
    Throws<MidiFormatException>(() => ReadMidi(Midi(0, 0, Track())));
    Throws<MidiFormatException>(() => Read(Track(E(0, 60, 100))));
    Throws<MidiFormatException>(() => Read(Track([0x81, 0x81, 0x81, 0x81, 0x00])));
    Throws<MidiFormatException>(() => Read(Track(E(0, 0x90, 60, 128))));
    Throws<MidiFormatException>(() => Read(Track(E(0, 0xff, 0x51, 2, 1, 2))));
    Throws<MidiFormatException>(() => Read(Track(E(0, 0xff, 0x51, 3, 0, 0, 0))));
    Throws<MidiFormatException>(() => Read(Track(E(0, 0x90, 60, 100), E(0, 0xff, 1, 0), E(0, 60, 0))));
    var truncated = Midi(0, 480, Track(E(0, 0x90, 60, 100)))[..^1];
    Throws<MidiFormatException>(() => ReadMidi(truncated));
    Throws<MidiFormatException>(() => Read(Track(E(0, 0xf0, 127, 1))));
}

static void ReadLimits()
{
    var bytes = Midi(0, 480, Track(E(0, 0x90, 60, 100), E(480, 0x80, 60, 0)));
    using var one = new MemoryStream(bytes);
    Throws<MidiFormatException>(() => MidiReader.Read(one, new MidiReadOptions { MaximumFileBytes = 14 }));
    using var two = new MemoryStream(bytes);
    Throws<MidiFormatException>(() => MidiReader.Read(two, new MidiReadOptions { MaximumEvents = 1 }));
}

static void ExactMapping()
{
    var options = new MappingOptions { OutOfRangePolicy = OutOfRangePolicy.Skip };
    for (var pitch = 48; pitch <= 85; pitch++)
    {
        True(HarmonicaMapper.TryMap(pitch, options, out var mapped), $"Pitch {pitch} was not mapped.");
        Equal(pitch, mapped!.EffectivePitch);
        True(HarmonicaMapper.Keys.Contains(mapped.Key), "Unexpected key.");
    }
    HarmonicaMapper.TryMap(60, options, out var c);
    Equal('z', c!.Key); Equal(HarmonicaRegister.Normal, c.Register); Equal(false, c.Semitone);
    HarmonicaMapper.TryMap(61, options, out var sharp);
    Equal('z', sharp!.Key); Equal(true, sharp.Semitone);
    HarmonicaMapper.TryMap(84, options, out var high);
    Equal(',', high!.Key); Equal(HarmonicaRegister.Upper, high.Register);
}

static void MappingPolicies()
{
    True(HarmonicaMapper.TryMap(36, new MappingOptions(), out var low), "Octave folding failed.");
    Equal(48, low!.EffectivePitch); Equal(true, low.WasOctaveFolded);
    Equal(false, HarmonicaMapper.TryMap(47, new MappingOptions { OutOfRangePolicy = OutOfRangePolicy.Skip }, out _));
    True(HarmonicaMapper.TryMap(60, new MappingOptions { TransposeSemitones = 2 }, out var transposed), "Transposition failed.");
    Equal('x', transposed!.Key); Equal(62, transposed.TransposedPitch);
    True(HarmonicaMapper.TryMap(72, new MappingOptions { BaseMidiNote = 72 }, out var baseNote), "Custom base failed.");
    Equal('z', baseNote!.Key); Equal(HarmonicaRegister.Normal, baseNote.Register);
    Throws<ArgumentOutOfRangeException>(() => HarmonicaMapper.TryMap(60, new MappingOptions { BaseMidiNote = 128 }, out _));
}

static void MelodySelection()
{
    var song = Song(Note(60, 0, 3), Note(67, 1, 1));
    var highest = PlaybackPlanBuilder.Build(song);
    Equal(3, highest.Chords.Count);
    Equal('z', highest.Chords[0].Notes[0].Key); Equal('b', highest.Chords[1].Notes[0].Key);
    Equal('z', highest.Chords[2].Notes[0].Key);
    for (var i = 1; i < highest.Chords.Count; i++) True(highest.Chords[i - 1].End <= highest.Chords[i].Start, "Overlapping plan.");
    var lowest = PlaybackPlanBuilder.Build(song, new MappingOptions { ChordPolicy = ChordPolicy.Lowest });
    Equal(1, lowest.Chords.Count); Near(3, lowest.Chords[0].Duration.TotalSeconds);
}

static void CompatibleChord()
{
    var song = Song(Note(60, 0, 1), Note(64, 0, 1), Note(67, 0, 1), Note(61, 0, 1));
    var plan = PlaybackPlanBuilder.Build(song, new MappingOptions { ChordPolicy = ChordPolicy.Compatible });
    Equal(3, plan.Chords.Single().Notes.Count);
    Equal(1, plan.IncompatibleNoteCount);
    True(plan.Warnings.Any(w => w.Contains("鼠标状态")), "Incompatible chord must be reported.");
    foreach (var chord in plan.Chords)
        True(chord.Notes.All(n => n.Register == chord.Register && n.Semitone == chord.Semitone), "Incompatible modifier state.");
}

static void AlternativeRepresentations()
{
    // F65 normally uses 'v', but 'c' plus middle mouse lets it harmonize with C#61.
    var plan = PlaybackPlanBuilder.Build(Song(Note(61, 0, 1), Note(65, 0, 1)),
        new MappingOptions { ChordPolicy = ChordPolicy.Compatible });
    Equal(2, plan.Chords.Single().Notes.Count); Equal(true, plan.Chords[0].Semitone);
    Equal(0, plan.IncompatibleNoteCount);
}

static void Retrigger()
{
    var plan = PlaybackPlanBuilder.Build(Song(Note(60, 0, 1), Note(60, 1, 1)));
    Equal(2, plan.Chords.Count);
    Equal('z', plan.Chords[1].RetriggerKeys.Single());
    Equal(plan.Chords[0].End, plan.Chords[1].Start);
}

static void UnrelatedBoundary()
{
    var plan = PlaybackPlanBuilder.Build(Song(Note(67, 0, 3), Note(60, 1, 1)));
    Equal(1, plan.Chords.Count); Near(3, plan.Chords[0].Duration.TotalSeconds);
}

static void TrackFiltering()
{
    var song = Song(Note(60, 0, 1, 0), Note(67, 0, 1, 1));
    var plan = PlaybackPlanBuilder.Build(song, new MappingOptions { SelectedTracks = [0] });
    Equal('z', plan.Chords.Single().Notes.Single().Key);
    Equal(0, PlaybackPlanBuilder.Build(song, new MappingOptions { SelectedTracks = [] }).Chords.Count);
}

static void BundledSample()
{
    var song = MidiReader.Read(Path.Combine(AppContext.BaseDirectory, "小星星.mid"));
    Equal(42, song.Notes.Count); Equal(2, song.Tracks.Count);
    Near(24, song.Duration.TotalSeconds);
    Equal(0, song.Warnings.Count);
    var plan = PlaybackPlanBuilder.Build(song);
    Equal(42, plan.Chords.Count); Equal(0, plan.Warnings.Count);
}

static MidiNote Note(int pitch, double start, double duration, int track = 0) =>
    new(track, 0, pitch, 100, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(duration), (long)(start * 960), (long)(duration * 960));
static MidiSong Song(params MidiNote[] notes) => new()
{
    Name = "test", Format = 1, TicksPerQuarterNote = 480, Duration = notes.Max(n => n.End),
    Notes = notes, Tracks = [], TempoChanges = []
};
static MidiSong Read(byte[] track) => ReadMidi(Midi(0, 480, track));
static MidiSong ReadMidi(byte[] bytes) => MidiReader.Read(new MemoryStream(bytes));
static byte[] Midi(int format, int division, params byte[][] tracks)
{
    using var stream = new MemoryStream();
    stream.Write(Encoding.ASCII.GetBytes("MThd")); stream.Write(Be32(6));
    stream.Write(Be16(format)); stream.Write(Be16(tracks.Length)); stream.Write(Be16(division));
    foreach (var track in tracks)
    {
        stream.Write(Encoding.ASCII.GetBytes("MTrk")); stream.Write(Be32(track.Length)); stream.Write(track);
    }
    return stream.ToArray();
}
static byte[] Track(params byte[][] events) => events.SelectMany(e => e).Concat(new byte[] { 0, 0xff, 0x2f, 0 }).ToArray();
static byte[] E(int delta, params int[] data) => Vlq(delta).Concat(data.Select(n => (byte)n)).ToArray();
static byte[] Vlq(int value)
{
    var parts = new List<byte> { (byte)(value & 0x7f) };
    while ((value >>= 7) != 0) parts.Insert(0, (byte)((value & 0x7f) | 0x80));
    return parts.ToArray();
}
static byte[] Be16(int value) => [(byte)(value >> 8), (byte)value];
static byte[] Be32(int value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];
static void True(bool value, string message) { if (!value) throw new Exception(message); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}.");
}
static void Near(double expected, double actual)
{
    if (Math.Abs(expected - actual) > 0.000001) throw new Exception($"Expected {expected}; got {actual}.");
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
