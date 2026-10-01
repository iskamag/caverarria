#!/usr/bin/env python3
"""Coverage of the native adapter. These diagnostics are not a playthrough."""
from pathlib import Path
import tempfile
import unittest

from PIL import Image

from native_client import Campaign, ROOT


class NativeRuntime(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="caverarria-native-")
        self.engine = Campaign(Path(self.temporary.name))

    def tearDown(self):
        self.engine.close()
        self.temporary.cleanup()

    def test_original_campaign_and_rendering(self):
        state = self.engine.command(op="snapshot")
        self.assertEqual(95, len(state["stages"]))
        self.assertEqual(60, state["timing_hz"])
        self.assertEqual(3, state["render_layers"])
        self.assertEqual("Start", state["stage"]["map"])
        self.assertEqual(state["stage"]["width"] * state["stage"]["height"], len(state["map"]["tiles"]))
        for tick in range(1600):
            state = self.engine.command(op="tick", external=False, controls=64 if tick % 8 < 4 else 0)
            if tick > 30 and state["control_enabled"] and state["script"] == "Ended":
                break
        self.assertTrue(state["control_enabled"], state["script"])
        self.assertEqual("Start", state["stage"]["map"])
        # Start Point's authored solid rock is in the foreground layer. Its
        # background/entity palette legitimately varies with sprite animation;
        # verify an exact original atlas tile instead of counting its colors.
        tile = (ROOT / "runtime/data/Stage/Start.pxm").read_bytes()[8 + state["stage"]["width"] + 1]
        with Image.open(ROOT / "runtime/data/Stage/PrtCave.pbm") as atlas:
            expected = atlas.convert("RGBA").crop((tile % 16 * 16, tile // 16 * 16,
                tile % 16 * 16 + 16, tile // 16 * 16 + 16))
        foreground = Image.frombytes("RGBA", (320, 240), self.engine.pixels(1))
        self.assertEqual(expected.tobytes(), foreground.crop((8, 8, 24, 24)).tobytes(),
            "Foreground must contain the exact authored Cave atlas artwork")
        self.assertTrue(any(self.engine.pixels(2)[3::4]), "Original HUD must be captured after foreground terrain")
        before = state["tick"]
        for _ in range(60):
            state = self.engine.command(op="tick", external=False, controls=0)
        self.assertEqual(60, state["tick"] - before)

    def test_every_original_stage_initializes(self):
        initial = self.engine.command(op="snapshot")
        for stage in initial["stages"]:
            with self.subTest(stage=stage["map"]):
                state = self.engine.command(op="warp", stage=stage["id"], x=80, y=80)
                self.assertEqual(stage["id"], state["stage"]["id"])
                self.assertGreater(state["stage"]["width"], 0)
                self.assertGreater(state["stage"]["height"], 0)
                self.assertEqual(state["stage"]["width"] * state["stage"]["height"], len(state["map"]["tiles"]))
                self.assertEqual(256, len(state["map"]["attributes"]))

    def test_save_reload_restores_campaign_state(self):
        self.engine.command(op="flag", id=500, value=True)
        saved = self.engine.command(op="save")
        self.engine.command(op="warp", stage=10, x=100, y=100)
        restored = self.engine.command(op="load")
        self.assertEqual(saved["stage"]["id"], restored["stage"]["id"])
        self.assertEqual(saved["player"]["life"], restored["player"]["life"])
        self.assertEqual(saved["weapons"], restored["weapons"])
        self.assertEqual(saved["items"], restored["items"])
        self.assertIn(500, restored["flags"])

    def test_stale_room_hit_is_rejected(self):
        first = self.engine.command(op="snapshot")
        self.engine.command(op="warp", stage=12, x=100, y=100)
        result = self.engine.command(op="hit", epoch=first["epoch"], id=0, damage=100)
        self.assertFalse(result["hit_accepted"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
