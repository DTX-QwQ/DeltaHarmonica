namespace DeltaHarmonica.Core;

public enum HarmonicaRegister { Lower = 0, Normal = 1, Upper = 2 }
public enum OutOfRangePolicy { FoldOctaves, Skip }
public enum ChordPolicy { Highest, Lowest, Compatible }

public sealed record MappingOptions
{
    public int BaseMidiNote { get; init; } = 60;
    public int TransposeSemitones { get; init; }
    public int LowerRegisterSemitones { get; init; } = -12;
    public int UpperRegisterSemitones { get; init; } = 12;
    public int SemitoneModifier { get; init; } = 1;
    public OutOfRangePolicy OutOfRangePolicy { get; init; } = OutOfRangePolicy.FoldOctaves;
    public ChordPolicy ChordPolicy { get; init; } = ChordPolicy.Highest;
    /// <summary>Null includes all tracks. An empty collection includes none.</summary>
    public IReadOnlyCollection<int>? SelectedTracks { get; init; }

    internal void Validate()
    {
        if (BaseMidiNote is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(BaseMidiNote), "基准 MIDI 音高必须为 0–127。");
        if (TransposeSemitones is < -127 or > 127)
            throw new ArgumentOutOfRangeException(nameof(TransposeSemitones), "移调必须为 -127–127 个半音。");
        if (LowerRegisterSemitones is < -127 or > 127 || UpperRegisterSemitones is < -127 or > 127)
            throw new ArgumentOutOfRangeException(nameof(LowerRegisterSemitones), "鼠标音区偏移必须为 -127–127 个半音。");
        if (SemitoneModifier is < -12 or > 12)
            throw new ArgumentOutOfRangeException(nameof(SemitoneModifier), "中键偏移必须为 -12–12 个半音。");
        if (!Enum.IsDefined(OutOfRangePolicy) || !Enum.IsDefined(ChordPolicy))
            throw new ArgumentOutOfRangeException(nameof(OutOfRangePolicy), "无效的音域或和弦策略。");
    }
}

public sealed record MappedNote(
    char Key, HarmonicaRegister Register, bool Semitone,
    int EffectivePitch, int SourcePitch, int TransposedPitch, bool WasOctaveFolded);

/// <summary>
/// The eight game keys play C D E F G A B C. Left/right mouse hold selects a register;
/// middle mouse hold adds a semitone. Lower and upper are mutually exclusive.
/// </summary>
public static class HarmonicaMapper
{
    public const string Keys = "zxcvbnm,";
    private static readonly int[] Scale = [0, 2, 4, 5, 7, 9, 11, 12];
    private static readonly HarmonicaRegister[] RegisterPreference =
        [HarmonicaRegister.Normal, HarmonicaRegister.Lower, HarmonicaRegister.Upper];

    public static bool TryMap(int pitch, MappingOptions options, out MappedNote? note)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (pitch is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(pitch));
        var target = pitch + options.TransposeSemitones;
        var candidates = GetCandidates(pitch, target, options);
        note = candidates.Where(n => n.EffectivePitch == target)
            .OrderBy(n => ModifierCount(n.Register, n.Semitone)).FirstOrDefault();
        if (note is not null) return true;
        if (options.OutOfRangePolicy == OutOfRangePolicy.Skip) return false;
        // Octave folding preserves pitch class; it never rounds to a different note.
        note = candidates.Where(n => (n.EffectivePitch - target) % 12 == 0)
            .OrderBy(n => Math.Abs(n.EffectivePitch - target))
            .ThenBy(n => ModifierCount(n.Register, n.Semitone))
            .FirstOrDefault();
        if (note is not null) note = note with { WasOctaveFolded = true };
        return note is not null;
    }

    /// <summary>All physical key/modifier combinations for the requested register.</summary>
    internal static IReadOnlyList<MappedNote> GetAlternatives(MappedNote mapped, MappingOptions options) =>
        GetCandidates(mapped.SourcePitch, mapped.TransposedPitch, options)
            .Where(n => n.EffectivePitch == mapped.EffectivePitch)
            .Select(n => n with { WasOctaveFolded = mapped.WasOctaveFolded }).ToArray();

    private static List<MappedNote> GetCandidates(int source, int target, MappingOptions options)
    {
        var candidates = new List<MappedNote>(48);
        foreach (var register in RegisterPreference)
        {
            var registerOffset = register switch
            {
                HarmonicaRegister.Lower => options.LowerRegisterSemitones,
                HarmonicaRegister.Upper => options.UpperRegisterSemitones,
                _ => 0
            };
            foreach (var semitone in new[] { false, true })
            {
                for (var i = 0; i < Scale.Length; i++)
                {
                    var effective = options.BaseMidiNote + Scale[i] + registerOffset +
                        (semitone ? options.SemitoneModifier : 0);
                    if (effective is >= 0 and <= 127)
                        candidates.Add(new MappedNote(Keys[i], register, semitone, effective, source, target, false));
                }
            }
        }
        return candidates;
    }

    internal static int ModifierCount(HarmonicaRegister register, bool semitone) =>
        (register == HarmonicaRegister.Normal ? 0 : 1) + (semitone ? 1 : 0);
}
