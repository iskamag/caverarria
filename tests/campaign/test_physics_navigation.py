"""Offline geometry/model regressions. These are never live completion evidence."""
from dataclasses import asdict
import gzip
import json
from pathlib import Path
import tempfile
import unittest

from navigation import Terrain
from physics_navigation import NavigationController, PhysicsProfile, PlatformPlanner, Pose, Simulator, SupportMap


class PhysicsNavigationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.terrain = Terrain.original("Start", projected=True)
        cls.old_measured = PhysicsProfile(jump_speed=2.37, gravity=2 / 15,
                                         provenance="Old recorded dry-air regression fixture")

    def test_supports_match_the_actual_stair_projection_and_avatar_clearance(self):
        supports = SupportMap(self.terrain)
        island = supports.standing(160, 129)
        self.assertIsNotNone(island)
        self.assertAlmostEqual(island.surface_y, 136)
        self.assertIsNotNone(supports.standing(94, 97))
        self.assertIsNotNone(supports.standing(112, 107 + 2 / 3))
        self.assertIsNotNone(supports.standing(160, 49))
        self.assertTrue(self.terrain.free(160, 95.01))
        self.assertFalse(self.terrain.free(160, 94.9))
        # This ceiling ends at x120, so the whole avatar must clear it.
        self.assertTrue(self.terrain.free(116.6, 94.9))

    def test_premature_jump_hits_head_but_delayed_running_launch_clears(self):
        sim = Simulator(SupportMap(self.terrain), self.old_measured)
        early = sim.arc(Pose(160, 129, -self.old_measured.max_speed), -1, 16)
        delayed = sim.arc(Pose(137, 129, -self.old_measured.max_speed), -1, 16)
        self.assertTrue(early["bumped_head"])
        self.assertFalse(delayed["bumped_head"])
        self.assertAlmostEqual(delayed["landing"][1], 97, delta=.01)

    def test_velocity_aware_route_walks_to_a_usable_launch(self):
        route = PlatformPlanner(self.terrain, self.old_measured).plan((160, 129), (160, 48), vx=0)
        self.assertTrue(route["moves"])
        first = route["moves"][0]
        self.assertLess(first["launch"][0], 140)
        self.assertLess(first["launch_vx"], -.7)
        self.assertTrue(all(not move.get("bumped_head") for move in route["moves"]))
        self.assertEqual(route["target_support"]["y"], 49)

    def test_normal_button_controller_reaches_exit_in_its_model(self):
        terrain = Terrain.original("Start", projected=True)
        profile = PhysicsProfile()
        sim = Simulator(SupportMap(terrain), profile)
        controller = NavigationController((160, 48), profile)
        pose, prior_jump = Pose(160, 129), False
        state = {"stage": {"id": 13, "width": terrain.width, "height": terrain.height},
                 "map": {"tiles": terrain.tiles, "attributes": terrain.attributes, "revision": 1},
                 "player": {}, "script_mode": "Map", "control_enabled": True}
        for frame in range(500):
            state["host"] = {**asdict(pose), "frame": frame, "width": terrain.half_width * 2,
                             "height": terrain.half_height * 2, "dead": False}
            buttons = controller.buttons(state)
            self.assertFalse(set(buttons) - {"frames", "left", "right", "jump"})
            if controller.status == "reached":
                self.assertLess(abs(pose.vx), .1)
                self.assertLess(((pose.x - 160) ** 2 + (pose.y - 49) ** 2) ** .5, 5)
                break
            jump = buttons.get("jump", False)
            pose = sim.tick(pose, int(buttons.get("right", False)) - int(buttons.get("left", False)),
                            jump, jump_edge=jump and not prior_jump)
            prior_jump = jump
        else:
            self.fail(f"Offline controller stalled: {pose}, {controller.status}")

    def test_goal_support_brakes_before_success_and_recovers_a_fast_arrival(self):
        terrain = Terrain.original("Start", projected=True)
        profile = PhysicsProfile()
        sim = Simulator(SupportMap(terrain), profile)
        state = {"stage": {"id": 13, "width": terrain.width, "height": terrain.height},
                 "map": {"tiles": terrain.tiles, "attributes": terrain.attributes, "revision": 1},
                 "player": {}, "script_mode": "Map", "control_enabled": True}
        for start in (Pose(148, 49, 1.3), Pose(155.37021, 49, 1.2998688)):
            controller, pose = NavigationController((160, 48), profile), start
            for frame in range(160):
                state["host"] = {**asdict(pose), "frame": frame, "width": terrain.half_width * 2,
                                 "height": terrain.half_height * 2, "dead": False}
                buttons = controller.buttons(state)
                if frame == 0:
                    self.assertEqual(controller.status, "braking_at_goal")
                    self.assertFalse(buttons.get("right"))
                    self.assertFalse(buttons.get("left"))
                if controller.status == "reached":
                    self.assertLess(abs(pose.vx), .1)
                    self.assertLess(abs(pose.x - 160), 5)
                    break
                pose = sim.tick(pose, int(buttons.get("right", False)) - int(buttons.get("left", False)), False)
            else:
                self.fail(f"Offline goal braking stalled: {pose}, {controller.status}")

    def test_original_tile_spikes_split_supports_and_require_a_clear_jump(self):
        tiles = [0] * (8 * 10)
        attributes = [0] * 256
        attributes[1], attributes[2] = 0x41, 0x42
        for col in range(8):
            tiles[8 * 8 + col] = 1
        tiles[7 * 8 + 3] = 2
        terrain = Terrain(8, 10, tiles, attributes, projected=True)
        self.assertFalse(terrain.unsafe(40, 112))  # original strict boundary
        self.assertTrue(terrain.unsafe(40.001, 112))
        self.assertFalse(terrain.unsafe(48, 105))
        self.assertTrue(terrain.unsafe(48, 105.001))
        supports = SupportMap(terrain)
        self.assertIsNone(supports.standing(48, 113))
        self.assertIsNotNone(supports.standing(30, 113))
        self.assertIsNotNone(supports.standing(70, 113))
        sim = Simulator(supports, PhysicsProfile())
        self.assertIsNone(sim.arc(Pose(30, 113, sim.profile.max_speed), 1, 0))
        jump = sim.arc(Pose(30, 113, sim.profile.max_speed), 1, 4)
        self.assertIsNotNone(jump)
        self.assertGreater(jump["landing"][0], 56)
        self.assertTrue(all(not terrain.unsafe(*point) for point in jump["trace"]))

    def test_cave_spike_actor_matches_real_death_sensor_and_braking_stays_on_ledge(self):
        terrain = Terrain.original("Cave", projected=True)
        self.assertEqual(sum(h["kind"] == "tile_spike" for h in terrain.hazards), 0)
        self.assertFalse(terrain.unsafe(499.66635, 214.95834))
        self.assertFalse(terrain.unsafe(499.66635, 216))
        self.assertTrue(terrain.unsafe(499.66635, 216.15))
        self.assertTrue(terrain.unsafe_segment((499.66635, 214.95834), (499.66635, 216.15)))
        sim = Simulator(SupportMap(terrain), PhysicsProfile())
        death_pose = sim.tick(Pose(499.66635, 214.95834, vy=1.0833334, grounded=False), 0, False)
        self.assertTrue(death_pose.hit_hazard)
        controller = NavigationController((496, 208), sim.profile)
        # Numeric values from the actual approach, before the mistaken extra
        # acceleration and fall. The actor is an observed stationary NPC211.
        pose = Pose(511.29965, 209, -1.1666662)
        state = {"stage": {"id": 12, "width": terrain.width, "height": terrain.height},
                 "map": {"tiles": terrain.tiles, "attributes": terrain.attributes, "revision": 1},
                 "npcs": [{"id": 196, "type": 211, "damage": 5, "x": 496, "y": 224,
                           "left": 6, "right": 6, "top": 6, "bottom": 6}],
                 "player": {}, "script_mode": "Map", "control_enabled": True}
        for frame in range(80):
            state["host"] = {**asdict(pose), "frame": frame, "width": terrain.half_width * 2,
                             "height": terrain.half_height * 2, "dead": False}
            buttons = controller.buttons(state)
            if controller.status == "reached":
                self.assertTrue(pose.grounded)
                self.assertLess(abs(pose.vx), .1)
                self.assertGreater(pose.x, 500.8)
                break
            if controller.final_braking:
                self.assertFalse(buttons.get("left"))
                self.assertFalse(buttons.get("right"))
            pose = sim.tick(pose, int(buttons.get("right", False)) - int(buttons.get("left", False)), False)
            self.assertFalse(pose.hit_hazard)
        else:
            self.fail(f"Offline Cave ledge approach stalled: {pose}, {controller.status}")

    def test_recording_profile_ignores_idle_gaps_and_measures_observed_motion(self):
        # Numbers taken from the older dry-air trace. This reduced fixture tests
        # the estimator and is not passed through the live playthrough auditor.
        samples = [(0, 0, 0, 129, True, {}), (914, -.1333333, -2.37, 119.52, False, {"frames": 4, "left": True, "jump": True}),
                   (918, -.2666667, -2.37, 110.04, False, {"frames": 4, "left": True, "jump": True}),
                   (926, -.5333333, 0, 95, False, {"frames": 4, "left": True, "jump": True}),
                   (930, -.6666667, .5333333, 96.333, False, {"frames": 4, "left": True})]
        records = []
        for frame, vx, vy, y, grounded, command in samples:
            records.append({"command": command, "state": {"stage": {"id": 13}, "player": {"flags": 0},
                            "control_enabled": True, "host": {"frame": frame, "x": 160, "y": y,
                                                             "vx": vx, "vy": vy, "grounded": grounded}}})
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "estimator-fixture.jsonl"
            path.write_text("".join(json.dumps(row) + "\n" for row in records))
            profile = PhysicsProfile.recording(path)
        self.assertAlmostEqual(profile.jump_speed, 2.37)
        self.assertAlmostEqual(profile.acceleration, 1 / 30, places=6)
        self.assertAlmostEqual(profile.gravity, 2 / 15, places=6)

    def test_live_recording_prefix_measures_friction_gravity_and_observed_hold(self):
        # This is a reduced numerical fixture. Keeping gzip open reproduces
        # the actual flushed recorder; it is not completion evidence.
        samples = [(0, 1.3, 0, 49, True, {}),
                   (1, 1.3 - 1 / 15, 0, 49, True, {"frames": 1}),
                   (2, 1.3 - 2 / 15, 0, 49, True, {"frames": 1}),
                   (3, 1.3 - 2 / 15, -2.395, 46.605, False, {"frames": 1, "jump": True}),
                   (4, 1.3 - 2 / 15, -2.395, 44.21, False, {"frames": 1, "jump": True}),
                   (5, 1.3 - 2 / 15, -2.395 + .325 / 3, 41.923, False, {"frames": 1})]
        records = [{"command": command, "state": {"stage": {"id": 13}, "player": {"flags": 0},
                    "control_enabled": True, "host": {"frame": frame, "x": 150 + frame, "y": y,
                                                     "vx": vx, "vy": vy, "grounded": grounded}}}
                   for frame, vx, vy, y, grounded, command in samples]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "open-estimator-fixture.jsonl.gz"
            with gzip.open(path, "wt") as writer:
                writer.write("".join(json.dumps(row) + "\n" for row in records))
                writer.flush()
                report = {}
                profile = PhysicsProfile.recording(path, report)
        self.assertFalse(report["complete_gzip_member"])
        self.assertEqual(report["records"], len(records))
        self.assertEqual(report["observed_constant_jump_hold_frames"], 2)
        self.assertEqual(report["inferred_jump_hold_limit_frames"], 16)
        self.assertAlmostEqual(profile.friction, 1 / 15)
        self.assertAlmostEqual(profile.jump_speed, 2.395)
        self.assertAlmostEqual(profile.gravity, .325 / 3)


if __name__ == "__main__":
    unittest.main()
