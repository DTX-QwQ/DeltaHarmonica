"""Deterministic cleanup and voice-selection fixtures, without audio packages."""
import copy
import importlib.util
import math
from pathlib import Path
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "audio_to_midi.py"
SPEC = importlib.util.spec_from_file_location("audio_to_midi_cleanup", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def note(pitch, start, duration, velocity=90, attack=None, instrument=0):
    result = {"pitch": pitch, "start": start, "end": start + duration,
              "velocity": velocity, "instrument": instrument}
    MODULE.refresh_note_beats(result, MODULE.BeatTimeline(120, [], "fallback"))
    if attack is not None:
        result["onsetConfidence"] = attack
    return result


class CleanupTests(unittest.TestCase):
    def setUp(self):
        self.timeline = MODULE.BeatTimeline(120, [0.25, 0.75, 1.35, 1.80])
        self.config = dict(MODULE.AUDIO_PRESETS["balanced"])

    def clean(self, events, config=None):
        prepared = copy.deepcopy(events)
        for event in prepared:
            MODULE.refresh_note_beats(event, self.timeline)
        return MODULE.clean_note_events(prepared, self.timeline, config or self.config)

    def test_out_of_order_decoded_amplitudes_remain_bound_to_pitch_and_time(self):
        # The decoder traverses onsets backwards; Basic Pitch's MIDI conversion
        # sorts separately. Strong melody must not inherit the weak note value.
        detected = [(1.2, 1.5, 72, 0.12, None), (0.3, 0.7, 69, 0.9, None),
                    (0.6, 0.9, 60, 0.25, None)]
        events = MODULE.decoded_note_events(detected, self.timeline)
        self.assertEqual([(event["pitch"], event["start"], event["amplitude"])
                          for event in events], [(69, 0.3, 0.9), (60, 0.6, 0.25), (72, 1.2, 0.12)])
        result, stats = MODULE.clean_note_events(events, self.timeline, self.config)
        self.assertEqual([event["pitch"] for event in result], [69, 60])
        self.assertEqual(stats["removedWeak"], 1)

    def test_preset_decode_floors_and_override_do_not_stack(self):
        parser = MODULE.create_parser()
        for preset, frames, inferred in (("solo", 5, True), ("balanced", 12, False),
                                         ("ensemble", 17, False)):
            with self.subTest(preset=preset):
                config = MODULE.transcription_config(parser.parse_args(["--preset", preset]))
                self.assertEqual(config["min_note_frames"], frames)
                self.assertEqual(config["infer_onsets"], inferred)
                self.assertLess(config["min_duration_ms"], frames * MODULE.MODEL_FRAME_MS)
        config = MODULE.transcription_config(parser.parse_args(
            ["--preset", "ensemble", "--minimum-note-length", "58", "--onset-threshold", "0.4"]))
        self.assertEqual(config["min_note_frames"], 5)
        self.assertEqual(config["min_duration_ms"], 58)
        self.assertEqual(config["onset_threshold"], 0.4)
        self.assertEqual(MODULE.AUDIO_PRESETS["ensemble"]["min_note_frames"], 17)

    def test_invalid_range_short_weak_and_density_counts_are_conserved(self):
        events = [note(20, 0.1, 0.3), note(60, 0.2, 0.03), note(62, 0.3, 0.3, 10),
                  note(64, math.nan, 0.3)]
        events.extend(note(pitch, 0.5, 0.3, 60 + index * 5)
                      for index, pitch in enumerate((55, 59, 62, 65, 69, 72)))
        result, stats = self.clean(events)
        self.assertEqual(len(result), 4)
        self.assertEqual((stats["removedInvalid"], stats["removedRange"], stats["removedShort"],
                          stats["removedWeak"], stats["removedDensity"]), (1, 1, 1, 1, 2))
        self.assertEqual(stats["rawCount"], stats["cleanCount"] + sum(stats[name] for name in (
            "removedInvalid", "removedRange", "removedShort", "removedWeak", "mergedFragments", "removedDensity")))
        self.assertEqual(stats["amplitudeFloor"], 0.14)
        self.assertEqual({event["pitch"] for event in result}, {62, 65, 69, 72})

    def test_secondary_length_filters_distinguish_solo_standard_and_long_notes(self):
        events = [note(69, 0.25, 0.06), note(72, 0.75, 0.15), note(74, 1.35, 0.23)]
        expected = {"solo": [69, 72, 74], "balanced": [72, 74], "ensemble": [74]}
        for preset, pitches in expected.items():
            with self.subTest(preset=preset):
                config = dict(MODULE.AUDIO_PRESETS[preset])
                config["amplitude_quantile"] = 0  # Isolate the duration decision.
                cleaned, _ = self.clean(copy.deepcopy(events), config)
                self.assertEqual([event["pitch"] for event in cleaned], pitches)

    def test_exact_amplitude_takes_precedence_over_rounded_velocity(self):
        # A 0.14 activation can round to velocity 18 / 127 (< 0.142).
        keep = note(69, 0.25, 0.3, 18)
        keep["amplitude"] = 0.14
        drop = note(72, 0.75, 0.3, 18)
        drop["amplitude"] = 0.139
        result, stats = self.clean([keep, drop])
        self.assertEqual([event["pitch"] for event in result], [69])
        self.assertEqual(stats["removedWeak"], 1)

    def test_duplicates_and_weak_fragments_extend_sustain_without_shifting_start(self):
        events = [note(69, 0.3, 0.2, 80, 0.8), note(69, 0.302, 0.23, 90, 0.8),
                  note(69, 0.56, 0.3, 75, 0.1)]
        snapshot = copy.deepcopy(events)
        result, stats = self.clean(events)
        self.assertEqual(len(result), 1)
        self.assertEqual(stats["mergedFragments"], 2)
        self.assertEqual(result[0]["start"], 0.3)
        self.assertAlmostEqual(result[0]["end"], 0.86)
        self.assertEqual(result[0]["velocity"], 90)
        self.assertEqual(events, snapshot)
        self.assertAlmostEqual(result[0]["durationBeats"],
                               self.timeline.seconds_to_beats(0.86) - result[0]["beat"])

    def test_confident_repeated_onsets_are_kept_and_overlap_is_released(self):
        events = [note(69, 0.3, 0.4, 90, 0.8), note(69, 0.65, 0.3, 85, 0.7),
                  note(69, 0.99, 0.2, 88, 0.65)]
        result, stats = self.clean(events)
        self.assertEqual([event["start"] for event in result], [0.3, 0.65, 0.99])
        self.assertEqual(stats["mergedFragments"], 0)
        self.assertEqual(stats["truncatedRepeated"], 1)
        self.assertEqual(result[0]["end"], 0.65)

    def test_missing_attack_evidence_preserves_close_repeated_notes(self):
        events = [note(69, 0.3, 0.2), note(69, 0.54, 0.2)]
        result, stats = self.clean(events)
        self.assertEqual(len(result), 2)
        self.assertEqual(stats["mergedFragments"], 0)
        self.assertEqual(result[1]["start"], 0.54)

    def test_other_tracks_and_long_silent_gaps_are_never_merged(self):
        events = [note(69, 0.3, 0.2, attack=0.8),
                  note(69, 0.31, 0.2, attack=0.1, instrument=1),
                  note(69, 1.2, 0.2, attack=0.1)]
        result, stats = self.clean(events)
        self.assertEqual(len(result), 3)
        self.assertEqual(stats["mergedFragments"], 0)


class MelodyTests(unittest.TestCase):
    def setUp(self):
        self.timeline = MODULE.BeatTimeline(120, [], "fallback")

    def test_highest_voice_keeps_original_winning_starts_and_releases_overlap(self):
        events = [note(48, 0.25, 0.8), note(72, 0.27, 0.7),
                  note(69, 0.75, 0.9), note(52, 0.76, 0.5), note(74, 1.35, 0.3)]
        result, stats = MODULE.choose_melody(events, self.timeline, "highest")
        self.assertEqual([(event["pitch"], event["start"]) for event in result],
                         [(72, 0.27), (69, 0.75), (74, 1.35)])
        self.assertEqual(stats["collapsedChordNotes"], 2)
        self.assertEqual(stats["truncatedNotes"], 2)
        for previous, following in zip(result, result[1:]):
            self.assertLessEqual(previous["end"], following["start"])
            self.assertAlmostEqual(previous["durationBeats"],
                                   self.timeline.seconds_to_beats(previous["end"]) - previous["beat"])

    def test_smart_keeps_fast_single_voice_and_isolated_quiet_phrases(self):
        fast = [note(69 + index % 3, 0.25 + index * 0.08, 0.07, 30) for index in range(12)]
        self.assertEqual(MODULE.smart_melody(fast, self.timeline), fast)
        phrases = [note(69, 0.25, 0.06, 16), note(48, 0.25, 0.06, 15),
                   note(72, 2.0, 0.07, 16), note(52, 2.0, 0.07, 15)]
        selected = MODULE.smart_melody(phrases, self.timeline)
        self.assertEqual([event["start"] for event in selected], [0.25, 2.0])

    def test_smart_holds_stable_melody_over_high_arpeggio(self):
        events = [note(69, 0.25, 0.9, 105), note(71, 1.25, 0.85, 105),
                  note(72, 2.25, 0.8, 105)]
        events.extend(note(84 + index % 4, 0.27 + index * 0.125, 0.065, 45) for index in range(22))
        highest, _ = MODULE.choose_melody(events, self.timeline, "highest")
        smart, _ = MODULE.choose_melody(events, self.timeline, "smart")
        self.assertGreater(len(highest), len(smart))
        self.assertEqual([event["pitch"] for event in smart], [69, 71, 72])
        self.assertEqual([event["start"] for event in smart], [0.25, 1.25, 2.25])

    def test_polyphonic_preserves_cleaned_chords_and_does_not_mutate_input(self):
        events = [note(48, 0.25, 0.8), note(72, 0.25, 0.4), note(69, 0.5, 0.6)]
        snapshot = copy.deepcopy(events)
        result, stats = MODULE.choose_melody(events, self.timeline, "polyphonic")
        self.assertEqual(result, snapshot)
        self.assertEqual(stats["collapsedChordNotes"], 0)
        result[0]["end"] = 100
        self.assertEqual(events, snapshot)

    def test_post_quantization_only_collapses_equal_grid_onsets(self):
        # Fine quantization can place adjacent melody steps less than 45 ms
        # apart. These are separate grid onsets, not another physical chord.
        events = [note(69, 0.25, 0.08), note(72, 0.30, 0.08)]
        quantized = MODULE.quantize_note_events(events, self.timeline, "1/16")
        result, _ = MODULE.enforce_monophonic(quantized, self.timeline, chord_window=1e-9)
        self.assertEqual(len(result), 2)
        self.assertLessEqual(result[0]["end"], result[1]["start"])
        self.assertGreaterEqual(result[0]["durationBeats"], 1 / 16)
        for event in result:
            self.assertAlmostEqual(event["durationBeats"] * 16, round(event["durationBeats"] * 16))
        chord = MODULE.quantize_note_events([note(69, 0.25, 0.3), note(72, 0.26, 0.3)],
                                            self.timeline, "1/4")
        result, stats = MODULE.enforce_monophonic(chord, self.timeline, chord_window=1e-9)
        self.assertEqual([event["pitch"] for event in result], [72])
        self.assertEqual(stats["collapsedChordNotes"], 1)


if __name__ == "__main__":
    unittest.main()
