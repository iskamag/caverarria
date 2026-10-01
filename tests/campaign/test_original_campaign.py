"""Data integration checks, explicitly separate from a live game playthrough."""
import json
from pathlib import Path
import unittest

from inspect_campaign import REPO, inspect_campaign, parse_tsc
from build_story_routes import build, select_ending


class OriginalCampaignTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.data = REPO / "runtime/data"
        cls.report = inspect_campaign(cls.data, REPO / "runtime/Doukutsu.exe")

    def test_every_original_stage_has_assets_and_valid_transfers(self):
        self.assertEqual(self.report["stage_count"], 95)
        self.assertEqual(self.report["script_count"], 98)
        self.assertEqual(self.report["event_count"], 1826)
        self.assertEqual(self.report["issues"], [])

    def test_original_quirks_are_visible_and_keep_first_definition(self):
        page = parse_tsc((self.data / "Stage/CentW.tsc").read_bytes())
        transfers = [command.args for command in page[152].commands if command.opcode == "TRA"]
        self.assertEqual(transfers, [(1, 99, 5, 8)])
        self.assertTrue(page[152].warnings)
        river = parse_tsc((self.data / "Stage/River.tsc").read_bytes())
        self.assertTrue(river[110].warnings)
        self.assertEqual([command.args for command in river[110].commands if command.opcode == "FLJ"], [(8510, 1331)])

    def test_live_ending_events_distinguish_normal_best_and_unreachable_debug(self):
        live = {(end["map"], end["event"], end["opcode"], tuple(end["args"])) for end in self.report["ending_commands"] if not end["after_terminal"]}
        self.assertEqual(live, {("0", 100, "CRE", ()), ("Island", 100, "XX1", (0,)), ("Island", 110, "XX1", (1,))})
        debug = next(end for end in self.report["ending_commands"] if end["map"] == "Blcny1")
        self.assertTrue(debug["after_terminal"])
        bad = parse_tsc((self.data / "Stage/Oside.tsc").read_bytes())[401]
        self.assertIn("- The End -", bad.text)
        self.assertIn((9999,), [command.args for command in bad.commands if command.opcode == "WAI"])
        self.assertFalse(any(command.opcode == "CRE" for command in bad.commands))

    def test_route_targets_exist_and_are_not_test_commands(self):
        route = json.loads((Path(__file__).parent / "early_route.json").read_text())
        by_id = {stage["id"]: stage for stage in self.report["stages"]}
        for step in route["steps"]:
            stage = by_id[step["stage"]]
            self.assertEqual(step["map"], stage["map"])
            page = parse_tsc((self.data / "Stage" / f"{step['map']}.tsc").read_bytes())
            self.assertIn(step["event"], page, step["name"])
            if "npc" in step:
                self.assertTrue(any((npc["x"], npc["y"]) == tuple(step["npc"]) for npc in stage["npcs"]), step["name"])

    def test_ending_routes_preserve_the_irreversible_curly_branch(self):
        route = build()
        best = select_ending(route, "best")["steps"]
        normal = select_ending(route, "normal")["steps"]
        bad = select_ending(route, "bad")["steps"]
        observations = [(row.get("map"), row.get("observed_event")) for row in best]
        self.assertNotIn(("MazeB", 501), observations)
        self.assertIn(("MazeB", 501), [(row.get("map"), row.get("observed_event")) for row in normal])
        self.assertLess(observations.index(("Almond", 240)), observations.index(("Almond", 450)))
        self.assertLess(observations.index(("Almond", 306)), observations.index(("River", 110)))
        cabin = [row for row in best if row.get("map") == "Pixel" and "result_event" in row]
        self.assertEqual([row.get("result_event") for row in cabin], [251, 202, 222, 253, 254, 255])
        self.assertEqual(cabin[-1]["dialogue_choice"], "no")
        self.assertEqual(bad[-1]["map"], "Oside")
        self.assertEqual(bad[-1]["result_event"], 401)
        self.assertFalse(any(row.get("action") == "wait_original_credits" for row in bad))
        self.assertEqual(len(build()["steps"]), len(route["steps"]))


if __name__ == "__main__":
    unittest.main()
