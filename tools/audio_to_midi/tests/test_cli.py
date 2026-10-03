"""Dependency-free tests: python -m unittest discover -s tools/audio_to_midi/tests."""
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).resolve().parents[1] / "audio_to_midi.py"
SPEC = importlib.util.spec_from_file_location("audio_to_midi", SCRIPT)
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class CliTests(unittest.TestCase):
    def invoke(self, *args):
        return subprocess.run([sys.executable, str(SCRIPT), *args], capture_output=True,
                              text=True, encoding="utf-8", timeout=15)

    def test_help_does_not_require_model_dependencies(self):
        result = self.invoke("--help")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--check", result.stdout)

    def test_invalid_arguments_fail_before_loading_model(self):
        for args in ((), ("in.wav",), ("in.wav", "out.mid", "--tempo", "0"),
                     ("in.wav", "out.mid", "--onset-threshold", "2")):
            with self.subTest(args=args):
                result = self.invoke(*args)
                self.assertEqual(result.returncode, 2)
                self.assertNotIn("ModuleNotFoundError", result.stderr)

    def test_missing_input_fails_with_nonzero_exit_and_no_output(self):
        with tempfile.TemporaryDirectory() as folder:
            target = Path(folder) / "out.mid"
            result = self.invoke(str(Path(folder) / "missing.wav"), str(target))
            self.assertEqual(result.returncode, 1)
            self.assertIn("音频文件不存在", result.stderr)
            self.assertFalse(target.exists())

    def test_existing_midi_is_preserved_with_unicode_and_spaces(self):
        with tempfile.TemporaryDirectory() as folder:
            audio = Path(folder) / "我的 音频.wav"
            audio.write_bytes(b"RIFF")
            output = Path(folder) / "已有 乐谱.mid"
            original = b"existing MIDI data"
            output.write_bytes(original)
            result = self.invoke(str(audio), str(output))
            self.assertEqual(result.returncode, 1)
            self.assertIn("拒绝覆盖", result.stderr)
            self.assertEqual(output.read_bytes(), original)

    def test_empty_audio_is_rejected_without_model_import(self):
        with tempfile.TemporaryDirectory() as folder:
            audio = Path(folder) / "empty.wav"
            audio.touch()
            with self.assertRaisesRegex(ValueError, "为空"):
                MODULE.validate_paths(str(audio), str(Path(folder) / "out.mid"))


if __name__ == "__main__":
    unittest.main()
