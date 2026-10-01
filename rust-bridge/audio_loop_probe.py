#!/usr/bin/env python3
"""Record real CPAL Organya output into an isolated Pulse sink and compare loops.

Only this process's stream is routed to the temporary sink. Existing clients,
default devices and live campaign state are not touched.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct
import subprocess
import tempfile
import time
import wave

import numpy as np

ROOT = Path(__file__).resolve().parents[1]


def tsc_decode(raw):
    midpoint = len(raw)//2
    key = raw[midpoint] or 7
    return bytes(x if i == midpoint else (x-key)&255 for i,x in enumerate(raw))


def tsc_encode(raw):
    midpoint = len(raw)//2
    key = raw[midpoint] or 7
    return bytes(x if i == midpoint else (x+key)&255 for i,x in enumerate(raw))


def pulse(*args):
    return subprocess.check_output(['pactl',*args],text=True).strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--library',type=Path,default=ROOT/'rust-bridge/target/release/libcaverarria_bridge.so')
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--expect-finite',action='store_true',help='Expect the pre-fix stalled third loop')
    args = parser.parse_args()
    args.output.parent.mkdir(parents=True,exist_ok=True)
    sink = f'caverarria_org_probe_{os.getpid()}'
    module = pulse('load-module','module-null-sink',f'sink_name={sink}','rate=48000','channels=2')
    os.environ.update(CAVERARRIA_AUDIO='1',CAVERARRIA_AUDIO_DEVICE='pulse',PULSE_SINK=sink)
    recorder = None
    handle = None
    try:
        with tempfile.TemporaryDirectory(prefix='caverarria-audio-') as directory:
            base = Path(directory)
            data = base/'data'
            shutil.copytree(ROOT/'runtime/data',data)
            head = data/'Head.tsc'
            head.write_bytes(tsc_encode(tsc_decode(head.read_bytes()) + b'\r\n#9004\r\n<CMU0021<END\r\n'))
            lib = ctypes.CDLL(str(args.library.resolve()))
            lib.cave_create.argtypes = [ctypes.c_char_p,ctypes.c_char_p,ctypes.c_int,ctypes.c_int]
            lib.cave_create.restype = ctypes.c_void_p
            lib.cave_command.argtypes = [ctypes.c_void_p,ctypes.c_char_p]
            lib.cave_command.restype = ctypes.c_char_p
            lib.cave_destroy.argtypes = [ctypes.c_void_p]
            lib.cave_last_error.restype = ctypes.c_char_p

            def command(**message):
                result = json.loads(lib.cave_command(handle,json.dumps(message).encode()))
                if not result.get('ok'): raise RuntimeError(result)
                return result

            raw_path = base/'capture.pcm'
            raw_file = raw_path.open('wb')
            recorder = subprocess.Popen(['parec',f'--device={sink}.monitor','--rate=48000',
                '--channels=2','--format=s16le','--raw'],stdout=raw_file,stderr=subprocess.PIPE)
            time.sleep(.5)
            handle = lib.cave_create(str(data).encode(),str(base/'save').encode(),320,240)
            if not handle: raise RuntimeError(lib.cave_last_error().decode())
            command(op='warp',stage=0,x=160,y=120)
            command(op='audio',music_volume=1,sfx_volume=0)
            command(op='event',event=9004)
            snapshot = command(op='tick',controls=0,player=dict(x=160,y=120,grounded=True))
            if not snapshot['audio_ready'] or snapshot['song'] != 21:
                raise RuntimeError(f'Original CPAL playback did not initialize: {snapshot}')
            inputs = json.loads(pulse('--format=json','list','sink-inputs'))
            stream = next(x for x in inputs if x['properties'].get('application.process.id') == str(os.getpid()))
            source_rate = int(re.search(r'(\d+)Hz',stream['sample_specification']).group(1))
            song = (data/'Org/ACCESS.org').read_bytes()
            wait = struct.unpack_from('<H',song,6)[0]
            start,end = struct.unpack_from('<ii',song,10)
            loop_seconds = (end-start)*(source_rate//1000)*wait/source_rate
            record_seconds = 3*loop_seconds+3
            print(f'Recording actual CPAL stream at {source_rate}Hz for {record_seconds:.2f}s, loop {loop_seconds:.6f}s',flush=True)
            time.sleep(record_seconds)
            snapshot = command(op='snapshot')
            recorder.terminate()
            recorder.wait(timeout=5)
            recorder = None
            raw_file.close()
            lib.cave_destroy(handle)
            handle = None
            samples = np.fromfile(raw_path,dtype='<i2').reshape(-1,2).astype(np.float64)
            # Ignore recorder startup silence; compare the same sample positions
            # in three complete original song passes, including both channels.
            active = np.flatnonzero(np.max(np.abs(samples),axis=1)>16)
            if not len(active): raise RuntimeError('Actual playback capture is silent')
            onset = int(active[0])
            length = round(loop_seconds*48000)
            loops = [samples[onset+i*length:onset+(i+1)*length].flatten() for i in range(3)]
            if min(map(len,loops)) != length*2: raise RuntimeError('Capture lacks three full loop passes')
            correlations = [float(np.corrcoef(loops[i],loops[i+1])[0,1]) for i in range(2)]
            rms = [float(np.sqrt(np.mean(x*x))) for x in loops]
            # Sample phase can differ between repeated synthesized notes. A 50ms
            # RMS envelope compares the whole musical phrase without relying on
            # phase identity, while still measuring actual captured PCM.
            block = 2400*2
            envelopes = [np.sqrt((x[:len(x)//block*block]**2).reshape(-1,block).mean(axis=1)) for x in loops]
            envelope_correlations = [float(np.corrcoef(envelopes[i],envelopes[i+1])[0,1]) for i in range(2)]
            # Normal playback repeats the full composition. The old finite-loop
            # bug repeats only a stalled held tone after its second boundary.
            passed = min(envelope_correlations)>.95 and min(rms)/max(rms)>.8
            report = dict(library=str(args.library.resolve()),song='ACCESS',song_id=21,
                original_sha256=hashlib.sha256(song).hexdigest(),sample_rate=source_rate,
                capture_rate=48000,loop_start=start,loop_end=end,loop_seconds=loop_seconds,
                recorded_seconds=len(samples)/48000,onset_seconds=onset/48000,
                loop_correlations=correlations,loop_envelope_correlations=envelope_correlations,
                loop_rms=rms,audio_ready=snapshot['audio_ready'],
                full_composition_repeats=passed,expect_finite=args.expect_finite)
            with wave.open(str(args.output),'wb') as wav:
                wav.setnchannels(2); wav.setsampwidth(2); wav.setframerate(48000)
                wav.writeframes(samples.astype('<i2').tobytes())
            args.output.with_suffix('.json').write_text(json.dumps(report,indent=2)+'\n')
            print(json.dumps(report,indent=2),flush=True)
            if passed == args.expect_finite:
                raise RuntimeError('PCM loop regression did not match expected behavior')
    finally:
        if recorder is not None:
            recorder.terminate(); recorder.wait(timeout=5)
        if handle is not None:
            lib.cave_destroy(handle)
        pulse('unload-module',module)


if __name__ == '__main__':
    main()
