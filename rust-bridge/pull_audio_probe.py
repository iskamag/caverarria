#!/usr/bin/env python3
"""Verify original music loops and PixTone PCM without opening an audio device."""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import shutil
import struct
import tempfile
import wave

import numpy as np

from audio_loop_probe import ROOT, tsc_decode, tsc_encode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--library", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    os.environ["CAVERARRIA_AUDIO"] = "1"
    library = ctypes.CDLL(str(args.library.resolve()))
    library.cave_create.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_int]
    library.cave_create.restype = ctypes.c_void_p
    library.cave_last_error.restype = ctypes.c_char_p
    library.cave_command.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    library.cave_command.restype = ctypes.c_char_p
    library.cave_audio.argtypes = [ctypes.c_void_p, ctypes.c_int]
    library.cave_audio.restype = ctypes.c_void_p
    library.cave_audio_rate.restype = ctypes.c_int
    library.cave_audio_length.argtypes = [ctypes.c_void_p]
    library.cave_audio_length.restype = ctypes.c_int
    library.cave_destroy.argtypes = [ctypes.c_void_p]
    rate = library.cave_audio_rate()
    assert rate == 48000, rate

    with tempfile.TemporaryDirectory(prefix="caverarria-pull-audio-") as temporary:
        root = Path(temporary)
        data = root / "data"
        shutil.copytree(ROOT / "runtime/data", data)
        head = data / "Head.tsc"
        head.write_bytes(tsc_encode(tsc_decode(head.read_bytes()) +
            b"\r\n#9004\r\n<CMU0021<END\r\n#9005\r\n<SOU0015<END\r\n"))
        handle = library.cave_create(str(data).encode(), str(root / "save").encode(), 320, 240)
        if not handle:
            raise RuntimeError(library.cave_last_error().decode())
        try:
            def command(**request):
                result = json.loads(library.cave_command(handle, json.dumps(request).encode()))
                if not result.get("ok"):
                    raise RuntimeError(result)
                return result

            def render(frames):
                blocks = []
                while frames:
                    size = min(frames, 4096)
                    pointer = library.cave_audio(handle, size)
                    assert pointer and library.cave_audio_length(handle) == size * 4
                    blocks.append(ctypes.string_at(pointer, size * 4))
                    frames -= size
                return np.frombuffer(b"".join(blocks), dtype="<i2").reshape(-1, 2)

            command(op="warp", stage=0, x=160, y=120)
            command(op="audio", music_volume=1, sfx_volume=0)
            command(op="event", event=9004)
            state = command(op="tick", controls=0, player=dict(x=160, y=120, grounded=True))
            assert state["song"] == 21 and state["audio_ready"]
            original = (data / "Org/ACCESS.org").read_bytes()
            wait = struct.unpack_from("<H", original, 6)[0]
            start, end = struct.unpack_from("<ii", original, 10)
            assert start == 0
            length = (end - start) * (rate // 1000) * wait
            pcm = render(length * 3)
            loops = [pcm[n * length:(n + 1) * length].astype(np.float64).flatten() for n in range(3)]
            rms = [float(np.sqrt(np.mean(part ** 2))) for part in loops]
            window = (rate // 20) * 2
            envelopes = [np.sqrt((part[:len(part) // window * window] ** 2)
                         .reshape(-1, window).mean(axis=1)) for part in loops]
            correlations = [float(np.corrcoef(envelopes[n], envelopes[n + 1])[0, 1]) for n in range(2)]
            assert min(correlations) > .95 and min(rms) / max(rms) > .8, (rms, correlations)
            command(op="audio", music_volume=0, sfx_volume=0)
            silence = render(rate // 4)
            assert np.max(np.abs(silence.astype(np.int32))) == 0
            command(op="audio", music_volume=0, sfx_volume=1)
            command(op="event", event=9005)
            command(op="tick", controls=0, player=dict(x=160, y=120, grounded=True))
            effect = render(rate // 2)
            peak = int(np.max(np.abs(effect.astype(np.int32))))
            assert peak > 100, peak
            report = dict(library=str(args.library.resolve()), backend="pull PCM; no audio device",
                sample_rate=rate, channels=2, format="s16le", original_sha256=hashlib.sha256(original).hexdigest(),
                song="ACCESS", loop_seconds=length / rate, loop_rms=rms,
                loop_envelope_correlations=correlations, full_composition_repeats=True,
                music_and_sfx_muted_are_silent=True, pixtone_sfx_id=15, pixtone_peak=peak)
            with wave.open(str(args.output), "wb") as wav:
                wav.setnchannels(2); wav.setsampwidth(2); wav.setframerate(rate)
                wav.writeframes(pcm.tobytes())
            args.output.with_suffix(".json").write_text(json.dumps(report, indent=2) + "\n")
            print(json.dumps(report, indent=2))
        finally:
            library.cave_destroy(handle)


if __name__ == "__main__":
    main()
