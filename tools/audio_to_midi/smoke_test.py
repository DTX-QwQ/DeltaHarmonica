"""Optional real-model smoke test; run with the installed conversion Python.

Synthesizes A4 / C5 separated by silence, transcribes them, then parses the MIDI
to check actual note detection. No fixture downloads or audio devices needed.
"""
import array
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import wave


def main():
    import pretty_midi
    rate = 22050
    samples = array.array("h")
    for frequency in (440.0, 523.251):
        for index in range(rate):
            time = index / rate
            envelope = min(1.0, time / 0.03, (1.0 - time) / 0.06)
            tone = math.sin(2 * math.pi * frequency * time)
            tone += 0.25 * math.sin(4 * math.pi * frequency * time)
            samples.append(int(15000 * envelope * tone))
        samples.extend([0] * (rate // 3))
    with tempfile.TemporaryDirectory(prefix="delta-midi-smoke-") as folder:
        source = Path(folder) / "测试 音频.wav"
        output = Path(folder) / "测试 乐谱.mid"
        with wave.open(str(source), "wb") as handle:
            handle.setnchannels(1)
            handle.setsampwidth(2)
            handle.setframerate(rate)
            handle.writeframes(samples.tobytes())
        script = Path(__file__).with_name("audio_to_midi.py")
        result = subprocess.run([sys.executable, "-I", "-u", str(script), str(source), str(output)],
                                capture_output=True, text=True, encoding="utf-8", timeout=180)
        print(result.stdout)
        if result.returncode:
            raise RuntimeError(result.stderr)
        markers = [line.removeprefix("RESULT_JSON=") for line in result.stdout.splitlines()
                   if line.startswith("RESULT_JSON=")]
        metadata = json.loads(markers[-1])
        midi = pretty_midi.PrettyMIDI(str(output))
        pitches = {note.pitch for instrument in midi.instruments for note in instrument.notes}
        assert metadata["notes"] > 0 and {69, 72}.issubset(pitches), pitches
        assert output.read_bytes()[:4] == b"MThd"
        print(f"PASS: detected A4 / C5; notes={metadata['notes']}; pitches={sorted(pitches)}")


if __name__ == "__main__":
    main()
