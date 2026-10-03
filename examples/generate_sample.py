"""Rebuild the public-domain Twinkle Twinkle Little Star MIDI sample."""
from pathlib import Path
import struct


def vlq(value: int) -> bytes:
    parts = [value & 0x7F]
    while value >> 7:
        value >>= 7
        parts.insert(0, (value & 0x7F) | 0x80)
    return bytes(parts)


def event(delta: int, data: bytes) -> bytes:
    return vlq(delta) + data


def track(data: bytes) -> bytes:
    return b"MTrk" + struct.pack(">I", len(data)) + data


# C4 G4 A4 ...; doubled last beat in each phrase, brief articulation gaps.
phrases = [
    [60, 60, 67, 67, 69, 69, 67],
    [65, 65, 64, 64, 62, 62, 60],
    [67, 67, 65, 65, 64, 64, 62],
    [67, 67, 65, 65, 64, 64, 62],
    [60, 60, 67, 67, 69, 69, 67],
    [65, 65, 64, 64, 62, 62, 60],
]
conductor = event(0, bytes([0xFF, 0x51, 3, 0x07, 0xA1, 0x20]))
conductor += event(0, bytes([0xFF, 0x58, 4, 4, 2, 24, 8]))
conductor += event(48 * 480, bytes([0xFF, 0x2F, 0]))

name = "小星星 · 演奏示例".encode("utf-8")
melody = event(0, bytes([0xFF, 3]) + vlq(len(name)) + name)
melody += event(0, bytes([0xC0, 22]))  # GM harmonica program, zero-based.
gap = 0
for phrase in phrases:
    for index, pitch in enumerate(phrase):
        length = 960 if index == 6 else 480
        melody += event(gap, bytes([0x90, pitch, 96]))
        melody += event(length - 60, bytes([0x80, pitch, 0]))
        gap = 60
melody += event(gap, bytes([0xFF, 0x2F, 0]))

output = Path(__file__).with_name("小星星.mid")
output.write_bytes(b"MThd" + struct.pack(">IHHH", 6, 1, 2, 480) + track(conductor) + track(melody))
print(f"Generated {output.name}: 42 notes, 24 seconds, 120 BPM")
