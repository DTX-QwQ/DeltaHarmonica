#!/usr/bin/env python3
"""Local Basic Pitch transcription using the bundled ONNX model on the CPU.

The final line is RESULT_JSON=<JSON> for the C# host. Progress goes to stdout;
errors go to stderr and produce a nonzero exit code. Audio never leaves the PC.
"""
from __future__ import annotations

import argparse
import importlib.metadata
import json
import logging
import os
from pathlib import Path
import sys
import uuid


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
    return {
        "ready": True,
        "basic_pitch": importlib.metadata.version("basic-pitch"),
        "onnxruntime": importlib.metadata.version("onnxruntime"),
        "providers": model.model.get_providers(),
        "python": sys.version.split()[0],
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
    emit("正在加载 Basic Pitch ONNX 模型…")
    model = load_model()
    from basic_pitch.inference import predict
    import pretty_midi
    emit("正在识别音高和节奏，长音频需要更多时间…")
    _, midi, _ = predict(
        audio,
        model_or_model_path=model,
        onset_threshold=args.onset_threshold,
        frame_threshold=args.frame_threshold,
        minimum_note_length=args.minimum_note_length,
        midi_tempo=args.tempo,
    )
    # The game's switches play discrete semitones, so bend curves cannot be sent.
    for instrument in midi.instruments:
        instrument.pitch_bends.clear()
    notes = sum(len(instrument.notes) for instrument in midi.instruments)
    if notes == 0:
        raise RuntimeError("没有识别出音符。请使用清晰的单一乐器音频，或在外部工具中调整转谱阈值。")
    emit(f"识别到 {notes} 个音符，正在写入 MIDI…")
    temporary = output.with_name(f".{output.stem}-{uuid.uuid4().hex}.tmp.mid")
    try:
        midi.write(str(temporary))
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
    return {"output": str(output), "notes": notes, "duration": float(midi.get_end_time())}


def create_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(description="使用 Basic Pitch ONNX 在本机将音频转换为 MIDI。")
    parser.add_argument("audio", nargs="?", help="输入 WAV / MP3 / FLAC / OGG 音频路径")
    parser.add_argument("output", nargs="?", help="输出 MIDI 文件路径（不得已存在）")
    parser.add_argument("--check", action="store_true", help="检查运行时、依赖和 ONNX 模型")
    parser.add_argument("--onset-threshold", type=float, default=0.5)
    parser.add_argument("--frame-threshold", type=float, default=0.3)
    parser.add_argument("--minimum-note-length", type=float, default=127.7, help="最短音符长度，单位 ms")
    parser.add_argument("--tempo", type=float, default=120.0, help="MIDI 内部时间基准 BPM；不改变音频节奏")
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
    if not (0.0 < args.onset_threshold <= 1.0 and 0.0 < args.frame_threshold <= 1.0):
        parser.error("音符阈值必须大于 0 且不大于 1。")
    if args.minimum_note_length <= 0 or args.tempo <= 0:
        parser.error("最短音符长度和 BPM 必须大于 0。")
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
