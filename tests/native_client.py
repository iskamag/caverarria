"""C ABI test client. Native coverage is distinct from a Terraria playthrough."""
from __future__ import annotations

import ctypes
import json
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class Campaign:
    def __init__(self, save: Path, width: int = 320, height: int = 240):
        os.environ.setdefault("CAVERARRIA_AUDIO", "0")
        self.width, self.height = width, height
        library = ROOT / "rust-bridge/target/release/libcaverarria_bridge.so"
        if not library.exists():
            library = ROOT / "rust-bridge/target/debug/libcaverarria_bridge.so"
        self.lib = ctypes.CDLL(str(library))
        self.lib.cave_create.argtypes = [ctypes.c_char_p, ctypes.c_char_p, ctypes.c_int, ctypes.c_int]
        self.lib.cave_create.restype = ctypes.c_void_p
        self.lib.cave_command.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
        self.lib.cave_command.restype = ctypes.c_char_p
        self.lib.cave_pixels.argtypes = [ctypes.c_void_p, ctypes.c_int]
        self.lib.cave_pixels.restype = ctypes.c_void_p
        self.lib.cave_destroy.argtypes = [ctypes.c_void_p]
        self.lib.cave_last_error.restype = ctypes.c_char_p
        save.mkdir(parents=True, exist_ok=True)
        self.handle = self.lib.cave_create(str(ROOT / "runtime/data").encode(), str(save.resolve()).encode(), width, height)
        if not self.handle:
            detail = self.lib.cave_last_error().decode(errors="replace")
            raise RuntimeError(f"Cave Story runtime initialization failed: {detail}")

    def command(self, **request):
        response = self.lib.cave_command(self.handle, json.dumps(request).encode())
        if not response:
            raise RuntimeError(f"Native runtime returned no response to {request}")
        result = json.loads(response)
        if result.get("error"):
            raise RuntimeError(result["error"])
        return result

    def pixels(self, layer: int = 0) -> bytes:
        pointer = self.lib.cave_pixels(self.handle, layer)
        if not pointer:
            raise RuntimeError("Native renderer returned no pixel data")
        return ctypes.string_at(pointer, self.width * self.height * 4)

    def capture(self, filename: Path):
        from PIL import Image
        background = Image.frombytes("RGBA", (self.width, self.height), self.pixels(0))
        overlay = Image.frombytes("RGBA", (self.width, self.height), self.pixels(1))
        ui = Image.frombytes("RGBA", (self.width, self.height), self.pixels(2))
        Image.alpha_composite(Image.alpha_composite(background, overlay), ui).save(filename)

    def close(self):
        if self.handle:
            self.lib.cave_destroy(self.handle)
            self.handle = None

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()
