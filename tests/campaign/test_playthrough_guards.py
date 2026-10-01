"""Negative evidence checks; these fixtures are never claimed as live gameplay."""
import json
from pathlib import Path
import tempfile
import unittest

from playthrough import RecordingTerraria, audit_recording, damage_witnesses, validate_controls


class FixtureDriver:
    def __init__(self):
        self.value = {"stage": {"id": 13, "map": "Start"}, "player": {"life": 3, "max_life": 3, "equipment": 0}, "host": {"x": 208, "y": 160, "vx": 0, "vy": 0, "frame": 1, "projectiles": 0}, "debugCommandsUsed": 0, "weapons": [], "items": [], "flags": [], "ok": True, "error": None}

    def state(self):
        return json.loads(json.dumps(self.value))

    def input(self, frames=1, **controls):
        self.value["host"]["frame"] += frames
        if controls.get("right"):
            self.value["host"]["x"] += frames
        if controls.get("shoot"):
            self.value["bullets"] = [{"type": 4, "x": 208, "y": 160, "life": 10}]
        return self.state()


class PlaythroughGuardTests(unittest.TestCase):
    def test_debug_operations_are_rejected_before_driver_receives_them(self):
        for command in ({"engine": {"op": "flag", "id": 2000}}, {"warp": 0}, {"damage": 9999}, {"event": 100}):
            with self.assertRaises(ValueError):
                validate_controls(command)
        validate_controls({"frames": 1, "inventory": True, "map": False, "nextWeapon": True, "previousWeapon": False, "retry": True})

    def test_short_control_trace_never_passes_as_completed_game(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "fixture.jsonl"
            with RecordingTerraria(FixtureDriver(), path) as recorder:
                recorder.input(frames=4, right=True)
                recorder.input(frames=2, shoot=True)
            result = audit_recording(path, "normal")
            self.assertFalse(result["complete"])
            self.assertTrue(any("Missing common" in issue for issue in result["issues"]))
            self.assertTrue(any("THANK YOU" in issue for issue in result["issues"]))
            records = [json.loads(line) for line in path.read_text().splitlines()]
            records[-1]["state"]["flags"] = [2000]
            path.write_text("\n".join(json.dumps(record) for record in records) + "\n")
            altered = audit_recording(path, "best")
            self.assertFalse(altered["complete"])
            self.assertTrue(any("broken evidence hash chain" in issue for issue in altered["issues"]))

    def test_save_reload_and_script_setup_are_not_damage_witnesses(self):
        before = FixtureDriver().state()
        before["stage"]["id"] = 47
        before["stages"] = [{"id": 47, "boss": 4}]
        before["bosses"] = [{"id": 0, "type": 0, "generation": 0, "life": 600, "shootable": True}]
        after = json.loads(json.dumps(before))
        after["bosses"][0]["life"] = 596
        self.assertEqual(damage_witnesses(before, after, {"retry": True, "shoot": True}), [])
        self.assertEqual(damage_witnesses(before, after, {"confirm": True}), [])
        witness = damage_witnesses(before, after, {"shoot": True})
        self.assertEqual([(row["boss_number"], row["before_life"], row["after_life"]) for row in witness], [(4, 600, 596)])


if __name__ == "__main__":
    unittest.main()
