#!/usr/bin/env python3
"""Prepare Purr2D2 sound effects using only Python's standard library.

Default: synthesize original optional retraction/contact sounds only. Never overwrite
supplied ignite/hum unless --supplied-sounds-dir is explicitly provided. That option
prepares the user's lightsaber-ignite.wav and lightsaber-idle.wav as mono PCM,
retaining sample rate and applying a short overlap to make the idle loop continuous.
Run from any directory; outputs are relative to this script's repository root.
"""

from __future__ import annotations

import argparse
import hashlib
import math
from pathlib import Path
import random
import statistics
import struct
import wave
import xml.etree.ElementTree as ET

RATE = 48_000
TAU = math.tau
ROOT = Path(__file__).resolve().parent.parent


def periodic_noise(duration: float, seed: int, count: int, low: float, high: float):
    rng = random.Random(seed)
    return [(rng.randint(int(low * duration), int(high * duration)), rng.random() * TAU,
             rng.uniform(0.4, 1.0) / math.sqrt(count)) for _ in range(count)]


def noise_at(t: float, duration: float, partials) -> float:
    return sum(gain * math.sin(TAU * cycles * t / duration + phase) for cycles, phase, gain in partials)


def contact() -> list[float]:
    duration = 2.0
    partials = periodic_noise(duration, 0x53495A5A, 36, 600, 9_500)
    result = []
    for index in range(int(RATE * duration)):
        t = index / RATE
        fizz = noise_at(t, duration, partials)
        pulse = 0.55 + 0.25 * math.sin(TAU * 7.5 * t) ** 2 + 0.2 * math.sin(TAU * 23 * t) ** 2
        ring = 0.2 * math.sin(TAU * 361 * t) + 0.1 * math.sin(TAU * 713 * t)
        result.append(math.tanh(fizz * 0.9) * pulse + ring)
    return result


def retraction() -> list[float]:
    duration = 0.28
    rng = random.Random(0x52455452)
    phase = 0.0
    noise_low = 0.0
    noise_smooth = 0.0
    result = []
    for index in range(int(RATE * duration)):
        t = index / RATE
        u = t / duration
        frequency = 80 + 290 * (1 - u) ** 1.4
        phase += TAU * frequency / RATE
        noise_low += 0.06 * (rng.uniform(-1, 1) - noise_low)
        noise_smooth += 0.35 * (noise_low - noise_smooth)
        fizz = noise_low - noise_smooth
        attack = min(1.0, t / 0.012)
        release = min(1.0, (duration - t) / 0.065)
        envelope = math.sin(attack * math.pi / 2) ** 2 * math.sin(release * math.pi / 2) ** 2
        envelope *= (1 - u) ** 0.45
        tone = (0.6 * math.sin(phase) + 0.23 * math.sin(phase * 2.01)
                + 0.09 * math.sin(phase * 3.02))
        result.append(envelope * (tone + fizz * 1.6))
    result[0] = result[-1] = 0.0
    return result


def pcm_bytes(samples: list[float], peak: float) -> bytes:
    maximum = max(abs(value) for value in samples)
    scale = peak / maximum if maximum else 0
    return struct.pack(f"<{len(samples)}h", *(round(32767 * value * scale) for value in samples))


def write_sound(destination: Path, samples: list[float], peak: float | None, check: bool, rate: int = RATE) -> None:
    pcm = pcm_bytes(samples, peak) if peak is not None else struct.pack(
        f"<{len(samples)}h", *(round(value) for value in samples))
    destination.parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(destination), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(rate)
        output.writeframes(pcm)
    quantized = struct.unpack(f"<{len(samples)}h", pcm)
    rms = math.sqrt(statistics.fmean(value * value for value in quantized)) / 32767
    seam = abs(quantized[0] - quantized[-1])
    if check:
        assert max(abs(value) for value in quantized) < 32767, "clipped signal"
        assert rms > 0.005, "unexpectedly silent signal"
        with wave.open(str(destination), "rb") as generated:
            assert (generated.getnchannels(), generated.getsampwidth(), generated.getframerate()) == (1, 2, rate)
            assert generated.getnframes() == len(samples)
        if "hum" in destination.name or "contact" in destination.name:
            deltas = sorted(abs(quantized[index] - quantized[index - 1]) for index in range(1, len(quantized)))
            assert seam <= deltas[int(len(deltas) * 0.995)], "loop boundary has an anomalous jump"
        else:
            assert quantized[0] == quantized[-1] == 0, "one-shot must start/end at silence"
    print(f"{destination.name}: {len(samples) / rate:.5f}s, frames={len(samples)}, RMS={rms:.3f}, seam={seam}, "
          f"sha256={hashlib.sha256(destination.read_bytes()).hexdigest()}")


def prepare_supplied(source_dir: Path, output_dir: Path, check: bool) -> None:
    for source_name, destination_name in (
        ("lightsaber-ignite.wav", "saber-ignite.wav"),
        ("lightsaber-idle.wav", "saber-hum.wav"),
    ):
        source = source_dir / source_name
        print(f"Supplied {source_name}: sha256={hashlib.sha256(source.read_bytes()).hexdigest()}")
        with wave.open(str(source), "rb") as recording:
            channels, width, rate, frames, compression, _ = recording.getparams()
            if width != 2 or channels not in (1, 2) or compression != "NONE":
                raise ValueError(f"{source}: expected uncompressed 16-bit mono/stereo PCM")
            pcm = recording.readframes(frames)
        values = struct.unpack(f"<{len(pcm) // 2}h", pcm)
        # Arithmetic average preserves original gain; neither normalize nor resample.
        mono = [sum(values[i:i + channels]) / channels for i in range(0, len(values), channels)]
        if source_name == "lightsaber-idle.wav":
            overlap = round(rate * 0.035)
            if len(mono) < overlap * 3:
                raise ValueError("Idle recording is too short for a 35ms loop overlap")
            tail = []
            for index in range(overlap):
                mix = (1 - math.cos(math.pi * index / (overlap - 1))) / 2
                tail.append(mono[-overlap + index] * (1 - mix) + mono[index] * mix)
            mono = mono[overlap:-overlap] + tail
        write_sound(output_dir / destination_name, mono, None, check, rate)
        if source_name == "lightsaber-idle.wav":
            print(f"Hum XML loop bounds: Start=0, End={len(mono) - 1}, Unit=Pcm")


def check_manifest() -> None:
    manifest = ET.parse(ROOT / "Assets" / "Sounds.xml").getroot()
    assert manifest.tag == "Assets"
    sounds = manifest.findall("Sound")
    assert len(sounds) == 4, "expected four saber sound declarations"
    for sound in sounds:
        assert sound.get("ChannelId") == "Sfx" and sound.get("SimSpeed") == "Ignore"
        file = sound.find("SoundFile")
        assert file is not None
        assert file.get("TwoD", "false") == "false"
        path = ROOT / file.attrib["Path"]
        with wave.open(str(path), "rb") as recording:
            assert recording.getnchannels() == 1 and recording.getsampwidth() == 2
            frames = recording.getnframes()
        loop = file.find("Loop")
        if loop is not None:
            start, end = loop.find("Start"), loop.find("End")
            assert start is not None and end is not None
            assert start.attrib == {"Value": "0", "Unit": "Pcm"}
            assert end.attrib == {"Value": str(frames - 1), "Unit": "Pcm"}, "update XML bounds for prepared WAV"
    print("Sounds.xml paths, PCM loop bounds and mixing declarations match prepared WAVs.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-dir", type=Path, default=ROOT / "Assets" / "Audio")
    parser.add_argument("--check", action="store_true", help="verify PCM format, endpoints, levels and loop seam")
    parser.add_argument("--supplied-sounds-dir", type=Path,
                        help="explicitly prepare user-supplied lightsaber-ignite.wav and lightsaber-idle.wav")
    args = parser.parse_args()
    for name, samples, peak in (
        ("saber-retract.wav", retraction(), 0.60),
        ("saber-contact.wav", contact(), 0.48),
    ):
        write_sound(args.output_dir / name, samples, peak, args.check)
    if args.supplied_sounds_dir is not None:
        prepare_supplied(args.supplied_sounds_dir, args.output_dir, args.check)
    if args.check and args.output_dir.resolve() == (ROOT / "Assets" / "Audio").resolve():
        check_manifest()


if __name__ == "__main__":
    main()
