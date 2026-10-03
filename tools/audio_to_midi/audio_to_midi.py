#!/usr/bin/env python3
"""Local Basic Pitch transcription using the bundled ONNX model on the CPU.

The final line is RESULT_JSON=<JSON> for the C# host. Progress goes to stdout;
errors go to stderr and produce a nonzero exit code. Audio never leaves the PC.
"""
from __future__ import annotations

import argparse
from bisect import bisect_right
import importlib.metadata
import json
import logging
import math
import os
from pathlib import Path
import sys
import uuid


MUSIC_TEMPO_VERSION = "1.0.3"
MUSIC_TEMPO_SCRIPT = Path(__file__).with_name("vendor") / "music-tempo.min.js"
FALLBACK_BPM = 120.0
TEMPO_SAMPLE_RATE = 44100
MIDI_RESOLUTION = 960  # Every requested beat subdivision is exactly representable.
QUANTIZATION_STEPS = {"none": None, "1/4": 1 / 4, "1/8": 1 / 8, "1/16": 1 / 16}
MODEL_FRAME_MS = 256 / 22050 * 1000
CHORD_WINDOW_SECONDS = 0.045
# Parameter tiers informed by Delta-Force-Harmonica's published preset table.
# The cleanup and melody algorithms below are independent implementations.
AUDIO_PRESETS = {
    "solo": {"label": "独奏 / 单声部", "onset_threshold": 0.34, "frame_threshold": 0.27,
             "min_note_frames": 5, "infer_onsets": True, "min_midi": 36, "max_midi": 96,
             "min_duration_ms": 48, "min_amplitude": 0.12, "amplitude_quantile": 0.08,
             "merge_gap_ms": 58, "chord_window_ms": 26, "max_chord_notes": 8},
    "balanced": {"label": "标准 / 推荐", "onset_threshold": 0.46, "frame_threshold": 0.34,
                 "min_note_frames": 12, "infer_onsets": False, "min_midi": 45, "max_midi": 88,
                 "min_duration_ms": 110, "min_amplitude": 0.14, "amplitude_quantile": 0,
                 "merge_gap_ms": 86, "chord_window_ms": 38, "max_chord_notes": 4},
    "ensemble": {"label": "长音优先", "onset_threshold": 0.46, "frame_threshold": 0.34,
                 "min_note_frames": 17, "infer_onsets": False, "min_midi": 45, "max_midi": 88,
                 "min_duration_ms": 190, "min_amplitude": 0.14, "amplitude_quantile": 0,
                 "merge_gap_ms": 86, "chord_window_ms": 38, "max_chord_notes": 4},
}


def transcription_config(args: argparse.Namespace) -> dict:
    config = dict(AUDIO_PRESETS[args.preset])
    for name in ("onset_threshold", "frame_threshold"):
        if getattr(args, name) is not None:
            config[name] = getattr(args, name)
    if args.minimum_note_length is not None:
        config["min_note_frames"] = max(1, math.floor(args.minimum_note_length / MODEL_FRAME_MS + 0.5))
        # An explicit shorter decode floor must not be defeated by the preset's
        # secondary duration filter.
        config["min_duration_ms"] = min(config["min_duration_ms"], args.minimum_note_length)
    return config


def amplitude(event: dict) -> float:
    return max(0.0, min(1.0, float(event.get("amplitude", event["velocity"] / 127))))


def refresh_note_beats(event: dict, timeline: "BeatTimeline") -> None:
    event["beat"] = timeline.seconds_to_beats(event["start"])
    event["durationBeats"] = timeline.seconds_to_beats(event["end"]) - event["beat"]


def decoded_note_events(detected, timeline: "BeatTimeline") -> list[dict]:
    """Keep amplitude bound to its own decoded pitch/time, regardless of order.

    Basic Pitch's returned MIDI is sorted separately from its decoded tuples;
    zipping those two collections would silently attach unrelated amplitudes.
    """
    events = []
    for start, end, pitch, activation, *_ in detected:
        strength = max(0.0, min(1.0, float(activation)))
        event = {"instrument": 0, "pitch": int(pitch), "start": float(start),
                 "end": float(end), "velocity": int(round(127 * strength)), "amplitude": strength}
        refresh_note_beats(event, timeline)
        events.append(event)
    return sorted(events, key=lambda event: (event["start"], -event["pitch"]))


def percentile(values: list[float], fraction: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    position = (len(ordered) - 1) * fraction
    left = math.floor(position)
    right = math.ceil(position)
    return ordered[left] + (ordered[right] - ordered[left]) * (position - left)


def note_strength(event: dict) -> float:
    # Long stable notes should survive alongside a louder transient.
    duration = event["end"] - event["start"]
    middle_register = max(0.0, 1 - abs(event["pitch"] - 67) / 34)
    return amplitude(event) * 1.55 + min(1, duration / 0.65) * 0.72 + middle_register * 0.2


def onset_groups(events: list[dict], window: float):
    group = []
    for event in sorted(events, key=lambda item: (item["start"], -item["pitch"], item["instrument"])):
        if group and event["start"] - group[0]["start"] > window:
            yield group
            group = []
        group.append(event)
    if group:
        yield group


def clean_note_events(events: list[dict], timeline: "BeatTimeline", config: dict) -> tuple[list[dict], dict]:
    """Filter model artifacts without shifting surviving note onset times.

    A confident new attack always retains a repeated pitch. Only duplicate
    starts or weakly attacked fragments may be joined into a sustained note.
    """
    stats = {"rawCount": len(events), "cleanCount": 0, "removedInvalid": 0,
             "removedRange": 0, "removedShort": 0, "removedWeak": 0,
             "mergedFragments": 0, "truncatedRepeated": 0, "removedDensity": 0}
    candidates = []
    for source in events:
        event = dict(source)
        if (not all(math.isfinite(event.get(name, float("nan")))
                    for name in ("pitch", "start", "end", "velocity"))
                or event["start"] < 0 or event["end"] <= event["start"]):
            stats["removedInvalid"] += 1
        elif not config["min_midi"] <= event["pitch"] <= config["max_midi"]:
            stats["removedRange"] += 1
        elif (event["end"] - event["start"]) * 1000 + 1e-8 < config["min_duration_ms"]:
            stats["removedShort"] += 1
        else:
            candidates.append(event)
    floor = max(config["min_amplitude"],
                percentile([amplitude(event) for event in candidates], config["amplitude_quantile"]) - 1e-6)
    stats["amplitudeFloor"] = floor
    strong = [event for event in candidates if amplitude(event) >= floor]
    stats["removedWeak"] = len(candidates) - len(strong)
    merged = []
    last = {}
    for event in sorted(strong, key=lambda item: (item["start"], -item["pitch"], item["instrument"])):
        key = (event["instrument"], event["pitch"])
        previous = last.get(key)
        if previous is not None:
            gap = event["start"] - previous["end"]
            duplicate = abs(event["start"] - previous["start"]) <= 0.01
            weak_attack = ("onsetConfidence" in event
                           and event["onsetConfidence"] < config["onset_threshold"])
            fragment = (weak_attack and gap <= config["merge_gap_ms"] / 1000
                        and event["start"] - previous["start"] <= 0.85)
            if duplicate or fragment:
                previous["end"] = max(previous["end"], event["end"])
                previous["velocity"] = max(previous["velocity"], event["velocity"])
                previous["amplitude"] = max(amplitude(previous), amplitude(event))
                refresh_note_beats(previous, timeline)
                stats["mergedFragments"] += 1
                continue
            if previous["end"] > event["start"]:
                previous["end"] = event["start"]
                refresh_note_beats(previous, timeline)
                stats["truncatedRepeated"] += 1
        merged.append(event)
        last[key] = event
    result = []
    for group in onset_groups(merged, config["chord_window_ms"] / 1000):
        selected = sorted(group, key=lambda event: (-note_strength(event), -event["pitch"],
                                                   event["start"], event["instrument"]))[:config["max_chord_notes"]]
        stats["removedDensity"] += len(group) - len(selected)
        result.extend(selected)
    result.sort(key=lambda event: (event["start"], -event["pitch"], event["instrument"]))
    stats["cleanCount"] = len(result)
    return result, stats


def enforce_monophonic(events: list[dict], timeline: "BeatTimeline",
                       chord_window: float = CHORD_WINDOW_SECONDS) -> tuple[list[dict], dict]:
    """Choose the highest onset in a chord and release it at the next onset."""
    result = []
    collapsed = 0
    for group in onset_groups(events, chord_window):
        winner = max(group, key=lambda event: (event["pitch"], event["end"] - event["start"],
                                              amplitude(event), -event["instrument"]))
        result.append(dict(winner))
        collapsed += len(group) - 1
    truncated = 0
    for previous, following in zip(result, result[1:]):
        if previous["end"] > following["start"]:
            previous["end"] = following["start"]
            refresh_note_beats(previous, timeline)
            truncated += 1
    return result, {"collapsedChordNotes": collapsed, "truncatedNotes": truncated}


def melody_evidence(event: dict, rank: float, scale: float) -> float:
    duration = (event["end"] - event["start"]) / scale
    return (0.7 + 0.8 * rank + 0.5 * amplitude(event)
            + min(1.0, duration / 0.45) * 0.9 - (0.6 if duration < 0.1 else 0))


def melody_transition(previous: dict, event: dict, scale: float) -> float:
    gap = (event["start"] - previous["start"]) / scale
    if gap <= CHORD_WINDOW_SECONDS / scale:
        return -math.inf
    jump = abs(previous["pitch"] - event["pitch"])
    overlap = max(0.0, previous["end"] - event["start"]) / scale
    silence = max(0.0, event["start"] - previous["end"]) / scale
    return (0.4 - min(2.5, max(0, jump - 3) * 0.12)
            - min(3.0, overlap * 4) - min(1.5, silence * 0.7)
            - (1.4 if gap < 0.09 else 0.5 if gap < 0.14 else 0))


def smart_melody(events: list[dict], timeline: "BeatTimeline") -> list[dict]:
    """Bounded voice tracking with a hold alternative for dense accompaniment.

    Each onset considers pitch, duration, strength and continuity with the last
    selected note. Sustained coverage rewards holding a voice while short
    accompaniment passes. Isolated phrases are solved independently.
    """
    scale = max(0.3, min(3.0, 120 / timeline.bpm))
    phrases = []
    phrase = []
    sounding_end = -math.inf
    for event in sorted(events, key=lambda item: (item["start"], -item["pitch"])):
        if phrase and event["start"] - sounding_end > 0.6 * scale:
            phrases.append(phrase)
            phrase = []
        phrase.append(event)
        sounding_end = max(sounding_end, event["end"])
    if phrase:
        phrases.append(phrase)
    selected = []
    for phrase in phrases:
        if all(following["start"] - previous["start"] > CHORD_WINDOW_SECONDS
               and previous["end"] - following["start"] <= min(0.06 * scale,
                                  (previous["end"] - previous["start"]) * 0.2)
               for previous, following in zip(phrase, phrase[1:])):
            selected.extend(phrase)
            continue
        # State = (score, last event, linked (event, previous) path). Linked
        # paths bound memory independently of the number of phrase onsets.
        states = [(0.0, None, None)]
        boundary = phrase[0]["start"]
        for group in onset_groups(phrase, CHORD_WINDOW_SECONDS):
            next_boundary = max(event["start"] for event in group)
            def coverage(last, begin, end):
                return (max(0.0, min(last["end"], end) - max(last["start"], begin))
                        / (0.5 * scale) if last else 0.0)
            options = [(score + coverage(last, boundary, next_boundary), last, path)
                       for score, last, path in states]
            unique = {}
            for event in group:
                old = unique.get(event["pitch"])
                if old is None or note_strength(event) > note_strength(old):
                    unique[event["pitch"]] = event
            candidates = sorted(unique.values(), key=lambda event: event["pitch"])
            for index, event in enumerate(candidates[-12:]):
                rank = (index / (len(candidates[-12:]) - 1)) if len(candidates[-12:]) > 1 else 0.6
                evidence = melody_evidence(event, rank, scale) - 1.6
                best = None
                for score, last, path in states:
                    proposal = (score + coverage(last, boundary, event["start"])
                                + (melody_transition(last, event, scale) if last else 0)
                                + evidence + coverage(event, event["start"], next_boundary))
                    if best is None or proposal > best[0]:
                        best = (proposal, event, (event, path))
                if best is not None and math.isfinite(best[0]):
                    options.append(best)
            states = sorted(options, key=lambda state: -state[0])[:48]
            boundary = next_boundary
        phrase_end = max(event["end"] for event in phrase)
        winner = max(states, key=lambda state: state[0] + coverage(state[1], boundary, phrase_end))
        path = []
        link = winner[2]
        while link is not None:
            path.append(link[0])
            link = link[1]
        selected.extend(reversed(path) if path else [max(phrase, key=note_strength)])
    return [dict(event) for event in selected]


def choose_melody(events: list[dict], timeline: "BeatTimeline", mode: str) -> tuple[list[dict], dict]:
    if mode == "polyphonic":
        return [dict(event) for event in events], {"removedMelody": 0, "collapsedChordNotes": 0,
                                                 "truncatedNotes": 0}
    candidates = smart_melody(events, timeline) if mode == "smart" else events
    result, stats = enforce_monophonic(candidates, timeline)
    stats["removedMelody"] = len(events) - len(candidates)
    return result, stats


class BeatTimeline:
    """Beat zero is the first detected beat; earlier pickup notes may be negative."""

    def __init__(self, bpm: float, beats: list[float], source: str = "music-tempo"):
        self.bpm = float(bpm)
        self.beats = [float(value) for value in beats]
        self.source = source
        if not math.isfinite(self.bpm) or not 60_000_000 / 0xFFFFFF <= self.bpm <= 600:
            raise ValueError("无效的 BPM。")
        if self.beats and (len(self.beats) < 2 or any(
            not math.isfinite(value) or value < 0 for value in self.beats
        ) or any(not 1e-6 <= right - left <= 0xFFFFFF / 1_000_000
                 for left, right in zip(self.beats, self.beats[1:]))):
            raise ValueError("无效的拍点。")
        self.interval = 60 / self.bpm
        self.origin = self.beats[0] if self.beats else 0.0

    def seconds_to_beats(self, seconds: float) -> float:
        if not self.beats or seconds <= self.origin:
            return (seconds - self.origin) / self.interval
        index = bisect_right(self.beats, seconds) - 1
        if index == len(self.beats) - 1:
            return index + (seconds - self.beats[index]) / self.interval
        return index + (seconds - self.beats[index]) / (self.beats[index + 1] - self.beats[index])

    def beats_to_seconds(self, beat: float) -> float:
        if not self.beats or beat <= 0:
            return self.origin + beat * self.interval
        index = math.floor(beat)
        if index >= len(self.beats) - 1:
            return self.beats[-1] + (beat - len(self.beats) + 1) * self.interval
        return self.beats[index] + (beat - index) * (self.beats[index + 1] - self.beats[index])


def run_music_tempo(samples) -> dict:
    """Run the unmodified JS library over mono float32 PCM at 44.1 kHz."""
    import numpy as np
    from py_mini_racer import mini_racer

    samples = np.ascontiguousarray(samples, dtype="<f4")
    if samples.ndim != 1 or samples.size < TEMPO_SAMPLE_RATE or not np.isfinite(samples).all():
        raise ValueError("音频过短或采样数据无效。")
    with mini_racer() as context:
        context.set_hard_memory_limit(512 * 1024 * 1024)
        context.eval(MUSIC_TEMPO_SCRIPT.read_text(encoding="utf-8"), timeout_sec=5)
        # Transfer binary PCM directly; avoid millions of JSON sample values.
        buffer = context.eval(f"var pcm = new ArrayBuffer({samples.nbytes}); pcm", timeout_sec=5)
        buffer[:] = samples.tobytes()
        try:
            result = context.eval(
                "var mt = new MusicTempo(new Float32Array(pcm));"
                "JSON.stringify({bpm: Number(mt.tempo), beats: mt.beats});",
                timeout_sec=120,
            )
        finally:
            buffer.release()
        return json.loads(result)


def estimate_tempo(audio: Path) -> BeatTimeline:
    emit("正在使用 music-tempo / Beatroot 估算 BPM 和拍点…")
    try:
        import librosa
        # The upstream 441-sample hop and 0.01-second step require this rate.
        samples, _ = librosa.load(str(audio), sr=TEMPO_SAMPLE_RATE, mono=True)
        result = run_music_tempo(samples)
        if not isinstance(result.get("beats"), list) or len(result["beats"]) < 2:
            raise ValueError("未检测到足够的拍点。")
        timeline = BeatTimeline(result["bpm"], result["beats"])
        emit(f"估算 BPM：{timeline.bpm:.3f}，检测到 {len(timeline.beats)} 个拍点。")
        return timeline
    except Exception as error:
        # Silence, sparse onsets, engine errors and malformed estimates all
        # share the same documented fallback. Cancellation is not swallowed.
        emit(f"BPM / 拍点估算失败，回退到 120 BPM：{error}")
        return BeatTimeline(FALLBACK_BPM, [], "fallback")


def round_grid(value: float, step: float) -> float:
    # Match JS Math.round, including half ties before beat zero.
    return math.floor(value / step + 0.5) * step


def make_note_events(midi, timeline: BeatTimeline, quantization: str) -> list[dict]:
    events = []
    for instrument_index, instrument in enumerate(midi.instruments):
        for note in instrument.notes:
            beat = timeline.seconds_to_beats(note.start)
            end_beat = timeline.seconds_to_beats(note.end)
            events.append({
                "instrument": instrument_index, "pitch": int(note.pitch), "velocity": int(note.velocity),
                "start": float(note.start), "end": float(note.end),
                "beat": float(beat), "durationBeats": float(end_beat - beat),
            })
    return quantize_note_events(events, timeline, quantization)


def quantize_note_events(source: list[dict], timeline: BeatTimeline, quantization: str) -> list[dict]:
    step = QUANTIZATION_STEPS[quantization]
    events = [dict(event) for event in source]
    if step is not None:
        first_grid = math.ceil(timeline.seconds_to_beats(0) / step - 1e-9) * step
        for event in events:
            beat = max(first_grid, round_grid(event["beat"], step))
            end_beat = max(beat + step, round_grid(event["beat"] + event["durationBeats"], step))
            event.update(beat=beat, durationBeats=end_beat - beat,
                         start=max(0.0, timeline.beats_to_seconds(beat)),
                         end=timeline.beats_to_seconds(end_beat))
    if step is not None:
        # Quantization may put repeated notes of the same pitch on one onset.
        # Merge those duplicates and end each repetition before its successor,
        # so MIDI note-off events cannot silence or drop a following note.
        previous = {}
        merged = []
        for event in sorted(events, key=lambda value: value["start"]):
            key = (event["instrument"], event["pitch"])
            earlier = previous.get(key)
            if earlier is not None and earlier["beat"] == event["beat"]:
                earlier["end"] = max(earlier["end"], event["end"])
                earlier["durationBeats"] = max(earlier["durationBeats"], event["durationBeats"])
                earlier["velocity"] = max(earlier["velocity"], event["velocity"])
                continue
            if earlier is not None and earlier["end"] > event["start"]:
                earlier["end"] = event["start"]
                earlier["durationBeats"] = event["beat"] - earlier["beat"]
            previous[key] = event
            merged.append(event)
        events = merged
    return events


def write_beat_midi(path: Path, midi, timeline: BeatTimeline, events: list[dict]) -> None:
    """Encode tracked beat intervals as MIDI tempo changes, retaining seconds."""
    import mido

    result = mido.MidiFile(type=1, ticks_per_beat=MIDI_RESOLUTION)
    tempo_track = mido.MidiTrack()
    result.tracks.append(tempo_track)
    nominal_tempo = round(timeline.interval * 1_000_000)
    offset_ticks = math.floor(timeline.origin / timeline.interval * MIDI_RESOLUTION + 0.5)
    final_tick = max((offset_ticks + math.floor(
        (event["beat"] + event["durationBeats"]) * MIDI_RESOLUTION + 0.5)
        for event in events), default=0)
    tempo_events = [(0, nominal_tempo)]
    for index, (start, end) in enumerate(zip(timeline.beats, timeline.beats[1:])):
        tempo_events.append((offset_ticks + index * MIDI_RESOLUTION, round((end - start) * 1_000_000)))
    if timeline.beats:
        tempo_events.append((offset_ticks + (len(timeline.beats) - 1) * MIDI_RESOLUTION, nominal_tempo))
    previous_tick = 0
    for tick, tempo in tempo_events:
        if tick > final_tick:
            break  # Untranscribed accompaniment/tail must not extend the MIDI.
        if not 0 < tempo <= 0xFFFFFF:
            raise ValueError("拍点间隔超出 MIDI 速度范围。")
        tempo_track.append(mido.MetaMessage("set_tempo", tempo=tempo, time=tick - previous_tick))
        previous_tick = tick
    tempo_track.append(mido.MetaMessage("end_of_track"))
    melodic_channels = [channel for channel in range(16) if channel != 9]
    for index, instrument in enumerate(midi.instruments):
        track = mido.MidiTrack()
        result.tracks.append(track)
        channel = 9 if instrument.is_drum else melodic_channels[index % len(melodic_channels)]
        track.append(mido.MetaMessage("track_name", name=instrument.name or "Basic Pitch"))
        track.append(mido.Message("program_change", program=instrument.program, channel=channel))
        messages = []
        for event in events:
            if event["instrument"] != index:
                continue
            start_tick = max(0, offset_ticks + math.floor(event["beat"] * MIDI_RESOLUTION + 0.5))
            end_tick = max(start_tick + 1, offset_ticks + math.floor(
                (event["beat"] + event["durationBeats"]) * MIDI_RESOLUTION + 0.5))
            messages.append((start_tick, 1, mido.Message("note_on", note=event["pitch"],
                                                       velocity=event["velocity"], channel=channel)))
            messages.append((end_tick, 0, mido.Message("note_off", note=event["pitch"],
                                                     velocity=0, channel=channel)))
        previous_tick = 0
        for tick, _, message in sorted(messages, key=lambda item: (item[0], item[1])):
            track.append(message.copy(time=tick - previous_tick))
            previous_tick = tick
        track.append(mido.MetaMessage("end_of_track"))
    result.save(str(path))


def emit(message: str) -> None:
    print(message, flush=True)


def load_model():
    # Basic Pitch warns about optional runtimes we deliberately do not install.
    original_level = logging.root.level
    logging.root.setLevel(logging.ERROR)
    try:
        from basic_pitch import FilenameSuffix, build_icassp_2022_model_path
        from basic_pitch.inference import Model
    finally:
        logging.root.setLevel(original_level)
    model_path = build_icassp_2022_model_path(FilenameSuffix.onnx)
    if not model_path.is_file():
        raise RuntimeError("Basic Pitch 的 ONNX 模型缺失，请重新安装音频转换环境。")
    model = Model(model_path)
    if model.model_type != Model.MODEL_TYPES.ONNX:
        raise RuntimeError("模型未使用 ONNX 运行时。")
    # Explicit CPU execution even if someone added a GPU provider to this venv.
    model.model.set_providers(["CPUExecutionProvider"])
    return model


def check_environment() -> dict:
    model = load_model()
    # Verify dependencies/model together, rather than merely finding a marker.
    import librosa  # noqa: F401
    import pretty_midi  # noqa: F401
    from py_mini_racer import mini_racer
    with mini_racer() as context:
        context.eval(MUSIC_TEMPO_SCRIPT.read_text(encoding="utf-8"), timeout_sec=5)
        if context.eval("typeof MusicTempo", timeout_sec=5) != "function":
            raise RuntimeError("music-tempo 运行时验证失败。")
    return {
        "ready": True,
        "basic_pitch": importlib.metadata.version("basic-pitch"),
        "onnxruntime": importlib.metadata.version("onnxruntime"),
        "providers": model.model.get_providers(),
        "python": sys.version.split()[0],
        "music_tempo": MUSIC_TEMPO_VERSION,
        "mini_racer": importlib.metadata.version("mini-racer"),
    }


def validate_paths(audio_arg: str, output_arg: str) -> tuple[Path, Path]:
    # Preserve the caller's absolute spelling. Path.resolve() on a packaged
    # Windows host can expand LocalAppData into that package's LocalCache path,
    # even though both names address the same file through OS virtualization.
    audio = Path(os.path.abspath(Path(audio_arg).expanduser()))
    output = Path(os.path.abspath(Path(output_arg).expanduser()))
    if not audio.is_file():
        raise ValueError(f"音频文件不存在：{audio}")
    if audio.stat().st_size == 0:
        raise ValueError("音频文件为空。")
    if output.suffix.lower() not in (".mid", ".midi"):
        raise ValueError("输出文件必须使用 .mid 或 .midi 扩展名。")
    if audio == output:
        raise ValueError("音频和输出文件不能使用同一个路径。")
    if output.exists():
        raise ValueError(f"输出文件已存在，拒绝覆盖：{output}")
    output.parent.mkdir(parents=True, exist_ok=True)
    return audio, output


def transcribe(audio: Path, output: Path, args: argparse.Namespace) -> dict:
    config = transcription_config(args)
    timeline = estimate_tempo(audio)
    emit("正在加载 Basic Pitch ONNX 模型…")
    model = load_model()
    from basic_pitch.inference import run_inference
    from basic_pitch.note_creation import model_frames_to_time, model_output_to_notes
    import numpy as np
    import pretty_midi
    emit(f"正在识别音高和节奏：{config['label']}，长音频需要更多时间…")
    model_output = run_inference(audio, model_or_model_path=model)
    # The high-level predict() fixes infer_onsets=True. Decode directly so the
    # calibrated standard/long-note presets can disable inferred extra attacks.
    # Decoding can mutate frame/onset arrays; keep the original attack evidence.
    midi, detected = model_output_to_notes(
        {key: value.copy() for key, value in model_output.items()},
        onset_thresh=config["onset_threshold"],
        frame_thresh=config["frame_threshold"],
        infer_onsets=config["infer_onsets"],
        min_note_len=config["min_note_frames"],
        include_pitch_bends=False,
        midi_tempo=timeline.bpm,
    )
    # The game's switches play discrete semitones, so bend curves cannot be sent.
    for instrument in midi.instruments:
        instrument.pitch_bends.clear()
    raw_events = decoded_note_events(detected, timeline)
    if not raw_events:
        raise RuntimeError("没有识别出音符。请使用清晰的单一乐器音频，或在外部工具中调整转谱阈值。")
    frame_times = model_frames_to_time(model_output["onset"].shape[0])
    for event in raw_events:
        frame = min(len(frame_times) - 1, int(np.searchsorted(frame_times, event["start"])))
        pitch_bin = event["pitch"] - 21  # Basic Pitch's A0-based semitone bins.
        event["onsetConfidence"] = float(np.max(model_output["onset"][max(0, frame - 1):frame + 2,
                                                                      pitch_bin]))
    emit("正在过滤弱音、碎音、重复片段与密集和弦…")
    cleaned, cleanup_stats = clean_note_events(raw_events, timeline, config)
    if not cleaned:
        raise RuntimeError("过滤后没有留下稳定音符。请改用“独奏 / 单声部”，或换用更清晰的片段。")
    selected, melody_stats = choose_melody(cleaned, timeline, args.melody)
    events = quantize_note_events(selected, timeline, args.quantize)
    if args.melody != "polyphonic" and args.quantize != "none":
        # Snapping can make distinct onsets coincide or overlap. Enforce the
        # same one-note rule again on the final grid, retaining beat metadata.
        events, quantized_stats = enforce_monophonic(events, timeline, chord_window=1e-9)
        melody_stats["quantizedCollapsedChordNotes"] = quantized_stats["collapsedChordNotes"]
        melody_stats["quantizedTruncatedNotes"] = quantized_stats["truncatedNotes"]
    notes = len(events)
    emit(f"原始 {len(raw_events)} → 清理 {len(cleaned)} → 输出 {notes} 个音符；"
         f"旋律：{args.melody}，量化：{args.quantize}，正在写入 MIDI…")
    temporary = output.with_name(f".{output.stem}-{uuid.uuid4().hex}.tmp.mid")
    try:
        write_beat_midi(temporary, midi, timeline, events)
        # Parse the generated file again to verify header, tracks and note data.
        verified = pretty_midi.PrettyMIDI(str(temporary))
        if sum(len(track.notes) for track in verified.instruments) != notes:
            raise RuntimeError("MIDI 输出验证失败。")
        # Exclusive output creation protects an existing file if a race occurs.
        created_output = False
        try:
            with output.open("xb") as target, temporary.open("rb") as source:
                created_output = True
                import shutil
                shutil.copyfileobj(source, target)
        except BaseException:
            if created_output:
                output.unlink(missing_ok=True)
            raise
    finally:
        temporary.unlink(missing_ok=True)
    return {"output": str(output), "notes": notes, "duration": float(verified.get_end_time()),
            "bpm": timeline.bpm, "beats": timeline.beats, "beatOrigin": timeline.origin,
            "tempoSource": timeline.source, "quantization": args.quantize, "noteEvents": events,
            "preset": args.preset, "melodyMode": args.melody,
            "rawNotes": len(raw_events), "cleanedNotes": len(cleaned),
            "cleanupStats": cleanup_stats, "melodyStats": melody_stats,
            "rawNoteEvents": raw_events}


def create_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="使用 Basic Pitch ONNX 在本机将音频转换为 MIDI。")
    parser.add_argument("audio", nargs="?", help="输入 WAV / MP3 / FLAC / OGG 音频路径")
    parser.add_argument("output", nargs="?", help="输出 MIDI 文件路径（不得已存在）")
    parser.add_argument("--check", action="store_true", help="检查运行时、依赖和 ONNX 模型")
    parser.add_argument("--preset", choices=AUDIO_PRESETS, default="balanced",
                        help="转谱预设：solo 独奏、balanced 标准/推荐、ensemble 长音优先")
    parser.add_argument("--melody", choices=("highest", "smart", "polyphonic"), default="highest",
                        help="highest 保留起音并选最高声部；smart 简化连续主旋律；polyphonic 保留和弦")
    parser.add_argument("--onset-threshold", type=float, help="覆盖预设的起音阈值")
    parser.add_argument("--frame-threshold", type=float, help="覆盖预设的持续音阈值")
    parser.add_argument("--minimum-note-length", type=float, help="覆盖预设的最短音符长度，单位 ms")
    parser.add_argument("--quantize", choices=QUANTIZATION_STEPS, default="none",
                        help="量化步长：none 保留原时序，1/4、1/8、1/16 分别为该分数拍")
    return parser


def main(argv: list[str] | None = None) -> int:
    # Redirected pipes on Windows otherwise inherit the active system codepage.
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    parser = create_parser()
    args = parser.parse_args(argv)
    if not args.check and (not args.audio or not args.output):
        parser.error("请同时指定输入音频和输出 MIDI 路径，或使用 --check。")
    if any(value is not None and not 0.0 < value <= 1.0
           for value in (args.onset_threshold, args.frame_threshold)):
        parser.error("音符阈值必须大于 0 且不大于 1。")
    if args.minimum_note_length is not None and (
            not math.isfinite(args.minimum_note_length) or args.minimum_note_length <= 0):
        parser.error("最短音符长度必须为大于 0 的有限数值。")
    try:
        if args.check:
            result = check_environment()
        else:
            audio, output = validate_paths(args.audio, args.output)
            result = transcribe(audio, output, args)
        emit("RESULT_JSON=" + json.dumps(result, ensure_ascii=False))
        return 0
    except (Exception, KeyboardInterrupt) as error:
        print(f"转换失败：{error}", file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
