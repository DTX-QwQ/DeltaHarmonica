"""Optional real-model smoke test; run with the installed conversion Python.

Synthesizes repeated A4 / C5 with leading silence, then parses the MIDI
to check actual note detection. No fixture downloads or audio devices needed.
"""
import array
import argparse
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import wave


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    import pretty_midi
    parser = argparse.ArgumentParser()
    parser.add_argument("--quantize", choices=("none", "1/4", "1/8", "1/16"), default="none")
    parser.add_argument("--preset", choices=("solo", "balanced", "ensemble"), default="balanced")
    parser.add_argument("--melody", choices=("highest", "smart", "polyphonic"), default="highest")
    parser.add_argument("--matrix", action="store_true", help="逐一验证三种预设和三种旋律模式")
    args = parser.parse_args()
    rate = 22050
    samples = array.array("h", [0] * (rate // 4))
    for frequency in (440.0, 440.0, 523.251):
        for index in range(rate):
            time = index / rate
            envelope = min(1.0, time / 0.03, (1.0 - time) / 0.06)
            tone = math.sin(2 * math.pi * frequency * time)
            tone += 0.25 * math.sin(4 * math.pi * frequency * time)
            samples.append(int(15000 * envelope * tone))
        samples.extend([0] * (rate // 3))
    with tempfile.TemporaryDirectory(prefix="delta-midi-smoke-") as folder:
        source = Path(folder) / "测试 音频.wav"
        with wave.open(str(source), "wb") as handle:
            handle.setnchannels(1)
            handle.setsampwidth(2)
            handle.setframerate(rate)
            handle.writeframes(samples.tobytes())
        script = Path(__file__).with_name("audio_to_midi.py")
        configurations = [(args.preset, args.melody)]
        if args.matrix:
            configurations = [(preset, melody) for preset in ("solo", "balanced", "ensemble")
                              for melody in ("highest", "smart", "polyphonic")]
        for preset, melody in configurations:
            output = Path(folder) / f"测试 乐谱-{preset}-{melody}.mid"
            result = subprocess.run([sys.executable, "-I", "-u", str(script), str(source), str(output),
                                     "--quantize", args.quantize, "--preset", preset, "--melody", melody],
                                    capture_output=True, text=True, encoding="utf-8", timeout=180)
            # Keep progress visible without printing large raw diagnostics.
            print("\n".join(line for line in result.stdout.splitlines()
                            if not line.startswith("RESULT_JSON=")))
            if result.returncode:
                raise RuntimeError(result.stderr)
            markers = [line.removeprefix("RESULT_JSON=") for line in result.stdout.splitlines()
                       if line.startswith("RESULT_JSON=")]
            metadata = json.loads(markers[-1])
            midi = pretty_midi.PrettyMIDI(str(output))
            notes = sorted((note for instrument in midi.instruments for note in instrument.notes),
                           key=lambda note: note.start)
            assert {note.pitch for note in notes} == {69, 72}, metadata["rawNoteEvents"]
            # The permissive solo tier may detect a short release attack near
            # the tail. Check the three sustained source notes and both true
            # repeated A4 starts rather than assuming the decoder splits none.
            sustained = [note for note in notes if note.end - note.start > 0.65]
            assert [note.pitch for note in sustained] == [69, 69, 72], metadata["noteEvents"]
            for actual, expected in zip(sustained, (0.25, 0.25 + 4 / 3, 0.25 + 8 / 3)):
                assert abs(actual.start - expected) < 0.1, (actual.start, expected)
            assert metadata["notes"] == len(notes) >= 3
            assert metadata["bpm"] > 0 and metadata["quantization"] == args.quantize
            assert metadata["preset"] == preset and metadata["melodyMode"] == melody
            assert metadata["rawNotes"] >= metadata["cleanedNotes"] >= metadata["notes"]
            assert len(metadata["rawNoteEvents"]) == metadata["rawNotes"]
            assert len(metadata["noteEvents"]) == metadata["notes"]
            assert midi.resolution == 960
            for previous, following in zip(notes, notes[1:]):
                if melody != "polyphonic":
                    assert previous.end <= following.start + 0.001
            assert notes[0].start >= 0.15, notes[0].start  # Leading silence remains.
            for note in metadata["noteEvents"]:
                assert note["durationBeats"] > 0 and note["end"] > note["start"] >= 0
                if args.quantize != "none":
                    step = 1 / int(args.quantize.split("/")[1])
                    assert math.isclose(note["beat"] / step, round(note["beat"] / step), abs_tol=1e-8)
                    assert math.isclose(note["durationBeats"] / step,
                                        round(note["durationBeats"] / step), abs_tol=1e-8)
            assert output.read_bytes()[:4] == b"MThd"
            print(f"PASS: A4 repeated / C5; notes={metadata['notes']}; preset={preset}; "
                  f"melody={melody}; bpm={metadata['bpm']}; quantization={args.quantize}")


if __name__ == "__main__":
    main()
