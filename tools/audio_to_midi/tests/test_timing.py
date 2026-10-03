"""Beat mapping tests; optional native-runtime checks use the conversion venv."""
import importlib.util
import json
import math
from pathlib import Path
from types import SimpleNamespace
import sys
import tempfile
import unittest
from unittest.mock import Mock, patch, sentinel


SCRIPT = Path(__file__).resolve().parents[1] / "audio_to_midi.py"
SPEC = importlib.util.spec_from_file_location("audio_to_midi_timing", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def mock_midi(*instruments):
    """Each instrument is a sequence of (pitch, start_seconds, end_seconds)."""
    return SimpleNamespace(instruments=[
        SimpleNamespace(notes=[SimpleNamespace(pitch=pitch, velocity=90, start=start, end=end)
                               for pitch, start, end in notes],
                        name="Timing test", program=0, is_drum=False)
        for notes in instruments
    ])


class BeatTimelineTests(unittest.TestCase):
    def test_detected_beats_are_integer_coordinates_with_nominal_extrapolation(self):
        timeline = MODULE.BeatTimeline(120, [0.25, 0.75, 1.35, 1.80])
        for index, seconds in enumerate(timeline.beats):
            with self.subTest(index=index):
                self.assertAlmostEqual(timeline.seconds_to_beats(seconds), index)
                self.assertAlmostEqual(timeline.beats_to_seconds(index), seconds)
        self.assertAlmostEqual(timeline.seconds_to_beats(0), -0.5)
        self.assertAlmostEqual(timeline.seconds_to_beats(1.05), 1.5)
        self.assertAlmostEqual(timeline.seconds_to_beats(2.30), 4)
        for seconds in (0, 0.1, 0.25, 0.4, 0.75, 0.9, 1.35, 1.6, 1.80, 2.30):
            with self.subTest(seconds=seconds):
                self.assertAlmostEqual(
                    timeline.beats_to_seconds(timeline.seconds_to_beats(seconds)), seconds)

    def test_fallback_is_120_bpm_with_audio_zero_as_beat_zero(self):
        timeline = MODULE.BeatTimeline(MODULE.FALLBACK_BPM, [], "fallback")
        self.assertEqual(timeline.bpm, 120)
        self.assertEqual(timeline.origin, 0)
        self.assertAlmostEqual(timeline.seconds_to_beats(0.75), 1.5)
        self.assertAlmostEqual(timeline.beats_to_seconds(1.5), 0.75)

    def test_invalid_bpm_and_beat_estimates_are_rejected(self):
        for bpm in (0, -1, 0.1, float("nan"), float("inf"), 601):
            with self.subTest(bpm=bpm), self.assertRaises(ValueError):
                MODULE.BeatTimeline(bpm, [0.25, 0.75])
        for beats in ([0.25], [-0.1, 0.4], [0.4, 0.4], [0.8, 0.4],
                      [0.1, float("nan")], [0.1, float("inf")]):
            with self.subTest(beats=beats), self.assertRaises(ValueError):
                MODULE.BeatTimeline(120, beats)

    def test_no_quantization_preserves_seconds_and_cross_interval_duration(self):
        timeline = MODULE.BeatTimeline(120, [0.25, 0.75, 1.35, 1.80])
        midi = mock_midi([(69, 0.10, 0.20), (72, 0.55, 1.50)], [(76, 1.05, 2.30)])
        events = MODULE.make_note_events(midi, timeline, "none")
        self.assertEqual(len(events), 3)
        self.assertEqual(events[0]["start"], 0.10)
        self.assertEqual(events[0]["end"], 0.20)
        self.assertAlmostEqual(events[0]["beat"], -0.3)
        self.assertAlmostEqual(events[1]["beat"], 0.6)
        self.assertAlmostEqual(events[1]["durationBeats"], 2 + 1 / 3 - 0.6)
        self.assertEqual(events[2]["instrument"], 1)
        for event in events:
            self.assertAlmostEqual(event["durationBeats"],
                                   timeline.seconds_to_beats(event["end"]) - event["beat"])

    def test_quantization_uses_literal_fraction_of_one_beat(self):
        timeline = MODULE.BeatTimeline(120, [0.25, 0.75, 1.35, 1.80])
        midi = mock_midi([(69, 0.55, 1.50), (72, 0.80, 0.801)])
        expected = {"1/4": (0.5, 1.75), "1/8": (0.625, 1.75),
                    "1/16": (0.625, 1.6875)}
        for quantization, (expected_start, expected_duration) in expected.items():
            with self.subTest(quantization=quantization):
                step = MODULE.QUANTIZATION_STEPS[quantization]
                events = MODULE.make_note_events(midi, timeline, quantization)
                self.assertAlmostEqual(events[0]["beat"], expected_start)
                self.assertAlmostEqual(events[0]["durationBeats"], expected_duration)
                for event in events:
                    self.assertAlmostEqual(event["beat"] / step, round(event["beat"] / step))
                    self.assertAlmostEqual(event["durationBeats"] / step,
                                           round(event["durationBeats"] / step))
                    self.assertGreaterEqual(event["durationBeats"], step)
                    self.assertGreater(event["end"], event["start"])

    def test_quantized_pickup_uses_first_grid_at_or_after_audio_zero(self):
        timeline = MODULE.BeatTimeline(120, [0.20, 0.70, 1.20])
        event = MODULE.make_note_events(mock_midi([(69, 0, 0.003)]), timeline, "1/4")[0]
        self.assertEqual(event["beat"], -0.25)
        self.assertAlmostEqual(event["start"], 0.075)
        self.assertAlmostEqual(event["end"], 0.20)
        self.assertEqual(event["durationBeats"], 0.25)

    def test_half_ties_match_javascript_math_round_including_negative_beats(self):
        self.assertEqual(MODULE.round_grid(0.125, 0.25), 0.25)
        self.assertEqual(MODULE.round_grid(-0.125, 0.25), 0)
        self.assertEqual(MODULE.round_grid(-0.375, 0.25), -0.25)

    def test_same_pitch_on_same_quantized_beat_merges_duration_and_velocity(self):
        timeline = MODULE.BeatTimeline(120, [], "fallback")
        midi = mock_midi([(69, 0.51, 0.54), (69, 0.56, 0.85)], [(69, 0.51, 0.54)])
        midi.instruments[0].notes[0].velocity = 50
        midi.instruments[0].notes[1].velocity = 110
        events = MODULE.make_note_events(midi, timeline, "1/4")
        self.assertEqual(len(events), 2)
        merged = next(event for event in events if event["instrument"] == 0)
        self.assertEqual(merged["pitch"], 69)
        self.assertEqual(merged["beat"], 1)
        self.assertEqual(merged["velocity"], 110)
        self.assertEqual(merged["durationBeats"], 0.75)
        self.assertEqual((merged["start"], merged["end"]), (0.5, 0.875))
        self.assertEqual(len(MODULE.make_note_events(midi, timeline, "none")), 3)

    def test_repeated_same_pitch_ends_at_next_onset_after_quantization(self):
        timeline = MODULE.BeatTimeline(120, [], "fallback")
        midi = mock_midi([(69, 0.51, 0.85), (69, 0.66, 0.90), (72, 0.70, 0.95)])
        events = MODULE.make_note_events(midi, timeline, "1/4")
        repeated = [event for event in events if event["pitch"] == 69]
        self.assertEqual(len(repeated), 2)
        self.assertEqual(repeated[0]["end"], repeated[1]["start"])
        self.assertEqual(repeated[0]["durationBeats"], 0.25)
        self.assertGreater(repeated[1]["end"], repeated[1]["start"])


class TempoEstimationTests(unittest.TestCase):
    def estimate(self, result=None, error=None):
        decoder = Mock(return_value=(sentinel.samples, 44100))
        engine = Mock(return_value=result, side_effect=error)
        with patch.dict(sys.modules, {"librosa": SimpleNamespace(load=decoder)}), \
                patch.object(MODULE, "run_music_tempo", engine), patch.object(MODULE, "emit") as progress:
            timeline = MODULE.estimate_tempo(Path("带 空格音频.wav"))
        return timeline, decoder, engine, progress

    def test_success_requests_44100_hz_mono_and_accepts_numeric_tempo_string(self):
        timeline, decoder, engine, _ = self.estimate({"bpm": "117.300", "beats": [0.25, 0.76, 1.30]})
        self.assertEqual(timeline.bpm, 117.3)
        self.assertEqual(timeline.origin, 0.25)
        self.assertEqual(timeline.source, "music-tempo")
        decoder.assert_called_once_with("带 空格音频.wav", sr=44100, mono=True)
        engine.assert_called_once_with(sentinel.samples)

    def test_malformed_estimates_return_exact_120_bpm_fallback(self):
        for result in ({"bpm": 120, "beats": []}, {"bpm": 120, "beats": [0.2]},
                       {"bpm": 120, "beats": (0.2, 0.7)},
                       {"bpm": float("nan"), "beats": [0.2, 0.7]},
                       {"bpm": "invalid", "beats": [0.2, 0.7]},
                       {"bpm": 0, "beats": [0.2, 0.7]},
                       {"bpm": 120, "beats": [0.7, 0.2]}, {}, None):
            with self.subTest(result=result):
                timeline, _, _, progress = self.estimate(result)
                self.assertEqual((timeline.bpm, timeline.beats, timeline.origin, timeline.source),
                                 (120, [], 0, "fallback"))
                self.assertTrue(any("120 BPM" in call.args[0] for call in progress.call_args_list))

    def test_engine_and_decoder_errors_fall_back_but_cancellation_propagates(self):
        timeline, _, _, _ = self.estimate(error=RuntimeError("Tempo extraction failed"))
        self.assertEqual(timeline.source, "fallback")
        with patch.dict(sys.modules, {"librosa": SimpleNamespace(load=Mock(side_effect=OSError("decode")))}), \
                patch.object(MODULE, "emit"):
            self.assertEqual(MODULE.estimate_tempo(Path("bad.wav")).bpm, 120)
        with self.assertRaises(KeyboardInterrupt):
            self.estimate(error=KeyboardInterrupt())


def available(*names):
    return all(importlib.util.find_spec(name) is not None for name in names)


@unittest.skipUnless(available("numpy", "py_mini_racer", "librosa"),
                     "Requires numpy, librosa and mini-racer in the conversion environment")
class NativeTempoTests(unittest.TestCase):
    def test_native_numpy_note_values_are_json_serializable(self):
        import numpy as np
        midi = mock_midi([(np.int64(69), np.float64(0.1), np.float64(0.3))])
        midi.instruments[0].notes[0].velocity = np.int64(95)
        events = MODULE.make_note_events(midi, MODULE.BeatTimeline(120, [], "fallback"), "none")
        decoded = json.loads(json.dumps(events, allow_nan=False))
        self.assertEqual(decoded[0]["pitch"], 69)
        self.assertEqual(decoded[0]["velocity"], 95)

    def test_real_music_tempo_tracks_90_120_150_bpm_with_leading_silence(self):
        import numpy as np
        rate = 44100
        duration = 12
        origin = 0.25
        pulse_length = int(0.035 * rate)
        pulse_time = np.arange(pulse_length) / rate
        noise = np.random.RandomState(17).normal(size=pulse_length)
        pulse = np.exp(-pulse_time / 0.008) * (
            0.4 * noise + 0.6 * np.sin(2 * np.pi * 1200 * pulse_time))
        pulse = (pulse / max(abs(pulse)) * 0.8).astype(np.float32)
        for bpm in (90, 120, 150):
            with self.subTest(bpm=bpm):
                samples = np.zeros(duration * rate, dtype=np.float32)
                interval = 60 / bpm
                for time in np.arange(origin, duration - 0.1, interval):
                    start = round(time * rate)
                    samples[start:start + pulse_length] += pulse
                result = MODULE.run_music_tempo(samples)
                timeline = MODULE.BeatTimeline(result["bpm"], result["beats"])
                self.assertAlmostEqual(timeline.bpm, bpm, delta=1)
                self.assertGreaterEqual(len(timeline.beats), 10)
                self.assertAlmostEqual(timeline.origin, origin, delta=0.05)
                self.assertAlmostEqual(float(np.median(np.diff(timeline.beats))), interval, delta=0.02)
                for beat_time in timeline.beats:
                    nearest = origin + round((beat_time - origin) / interval) * interval
                    self.assertAlmostEqual(beat_time, nearest, delta=0.03)

    def test_real_silent_pcm_causes_documented_120_bpm_fallback(self):
        import numpy as np
        samples = np.zeros(2 * 44100, dtype=np.float32)
        with patch.dict(sys.modules, {"librosa": SimpleNamespace(load=Mock(return_value=(samples, 44100)))}), \
                patch.object(MODULE, "emit"):
            timeline = MODULE.estimate_tempo(Path("silence.wav"))
        self.assertEqual((timeline.bpm, timeline.source, timeline.beats), (120, "fallback", []))


@unittest.skipUnless(available("mido", "pretty_midi"),
                     "Requires mido and pretty_midi in the conversion environment")
class MidiTimingRoundtripTests(unittest.TestCase):
    def write_and_read(self, folder, midi, timeline, quantization):
        import mido
        import pretty_midi
        events = MODULE.make_note_events(midi, timeline, quantization)
        output = Path(folder) / "带 空格节拍.mid"
        MODULE.write_beat_midi(output, midi, timeline, events)
        return events, mido.MidiFile(str(output)), pretty_midi.PrettyMIDI(str(output))

    def test_unequal_intervals_pickup_and_tail_preserve_original_seconds(self):
        timeline = MODULE.BeatTimeline(120, [0.25, 0.75, 1.35, 1.80])
        midi = mock_midi([(69, 0.10, 0.20), (72, 0.55, 1.50), (76, 1.95, 2.30)])
        with tempfile.TemporaryDirectory() as folder:
            events, raw, parsed = self.write_and_read(folder, midi, timeline, "none")
        self.assertEqual(raw.ticks_per_beat, 960)
        notes = {note.pitch: note for instrument in parsed.instruments for note in instrument.notes}
        self.assertEqual(set(notes), {69, 72, 76})
        tolerance = max(timeline.interval, *beat_intervals(timeline.beats)) / 960
        for event in events:
            self.assertAlmostEqual(notes[event["pitch"]].start, event["start"], delta=tolerance)
            self.assertAlmostEqual(notes[event["pitch"]].end, event["end"], delta=tolerance)
        ticks = 0
        tempos = {}
        for message in raw.tracks[0]:
            ticks += message.time
            if message.type == "set_tempo":
                tempos[ticks] = message.tempo
        self.assertEqual(tempos, {0: 500000, 480: 500000, 1440: 600000,
                                 2400: 450000, 3360: 500000})

    def test_quantized_midi_ticks_follow_phase_relative_grid_for_every_step(self):
        timeline = MODULE.BeatTimeline(120, [0.213, 0.713, 1.313, 1.763])
        midi = mock_midi([(69, 0.01, 0.12), (72, 0.531, 1.437), (76, 1.814, 2.137)])
        offset_ticks = math.floor(timeline.origin / timeline.interval * 960 + 0.5)
        for quantization in ("1/4", "1/8", "1/16"):
            with self.subTest(quantization=quantization), tempfile.TemporaryDirectory() as folder:
                events, raw, parsed = self.write_and_read(folder, midi, timeline, quantization)
                step_ticks = round(MODULE.QUANTIZATION_STEPS[quantization] * 960)
                ticks = 0
                starts = {}
                ends = {}
                for message in raw.tracks[1]:
                    ticks += message.time
                    if message.type == "note_on" and message.velocity:
                        starts[message.note] = ticks
                    elif message.type == "note_off":
                        ends[message.note] = ticks
                notes = {note.pitch: note for instrument in parsed.instruments for note in instrument.notes}
                for event in events:
                    pitch = event["pitch"]
                    self.assertEqual((starts[pitch] - offset_ticks) % step_ticks, 0)
                    self.assertEqual((ends[pitch] - offset_ticks) % step_ticks, 0)
                    self.assertGreaterEqual(ends[pitch] - starts[pitch], step_ticks)
                    self.assertGreaterEqual(notes[pitch].start, 0)
                    self.assertAlmostEqual(notes[pitch].start, event["start"], delta=0.001)
                    self.assertAlmostEqual(notes[pitch].end, event["end"], delta=0.001)

    def test_sub_tick_note_survives_midi_roundtrip(self):
        timeline = MODULE.BeatTimeline(120, [], "fallback")
        midi = mock_midi([(69, 0.1, 0.100001)])
        with tempfile.TemporaryDirectory() as folder:
            _, _, parsed = self.write_and_read(folder, midi, timeline, "none")
        self.assertEqual(len(parsed.instruments[0].notes), 1)
        note = parsed.instruments[0].notes[0]
        self.assertGreater(note.end, note.start)

    def test_unused_tempo_tail_does_not_extend_song_duration(self):
        timeline = MODULE.BeatTimeline(120, [0.25, 0.75, 1.35, 1.80, 2.30])
        midi = mock_midi([(69, 0.30, 0.60)])
        with tempfile.TemporaryDirectory() as folder:
            _, raw, parsed = self.write_and_read(folder, midi, timeline, "none")
        self.assertAlmostEqual(raw.length, 0.60, delta=0.001)
        self.assertAlmostEqual(parsed.get_end_time(), 0.60, delta=0.001)
        ticks = 0
        for message in raw.tracks[0]:
            ticks += message.time
        self.assertLessEqual(ticks, round(0.60 / 0.5 * 960))

    def test_coalesced_repeated_notes_match_exported_events_after_roundtrip(self):
        timeline = MODULE.BeatTimeline(120, [], "fallback")
        midi = mock_midi([(69, 0.51, 0.54), (69, 0.56, 0.85), (69, 0.66, 0.90)])
        with tempfile.TemporaryDirectory() as folder:
            events, _, parsed = self.write_and_read(folder, midi, timeline, "1/4")
        self.assertEqual(len(events), 2)
        notes = sorted(parsed.instruments[0].notes, key=lambda note: note.start)
        self.assertEqual(len(notes), 2)
        for note, event in zip(notes, events):
            self.assertEqual(note.pitch, event["pitch"])
            self.assertAlmostEqual(note.start, event["start"], delta=0.001)
            self.assertAlmostEqual(note.end, event["end"], delta=0.001)


def beat_intervals(beats):
    return [right - left for left, right in zip(beats, beats[1:])]


if __name__ == "__main__":
    unittest.main()
