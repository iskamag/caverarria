"""Drive genuine Terraria controls through the mod's test input hook."""
from __future__ import annotations

import json
import os
from pathlib import Path
import time


class Terraria:
    def __init__(self, directory: Path):
        self.directory = directory
        self.sequence = int(time.time_ns() // 1000)
        self.last_state = None

    def state(self):
        try:
            self.last_state = json.loads((self.directory / "state.json").read_text())
        except (FileNotFoundError, json.JSONDecodeError):
            pass
        return self.last_state

    def wait_ready(self, timeout: float = 120):
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            result = self.state()
            if result and result.get("stage"):
                return result
            time.sleep(0.1)
        raise TimeoutError("tModLoader campaign did not become ready")

    def input(self, frames: int = 1, timeout: float = 20, **buttons):
        self.sequence += 1
        command = {"id": self.sequence, "frames": frames, **buttons}
        temporary = self.directory / "input.pending"
        temporary.write_text(json.dumps(command))
        os.replace(temporary, self.directory / "input.json")
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            state = self.state()
            if state and state.get("lastCommandId") == self.sequence and state.get("commandFramesRemaining", 1) == 0:
                return state
            # Prompt acknowledgements keep a controller from inserting a long
            # unrequested release between short, ordinary control inputs.
            time.sleep(0.001)
        raise TimeoutError(f"Terraria input was not consumed: {command}; last state={self.last_state}")
