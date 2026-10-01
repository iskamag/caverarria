"""Grounded supports and measured Terraria jump arcs; suggests normal controls.

No driver, native command, event call, or position setter is available here.
Prediction is a navigation aid. Only observed live movement proves traversal.
"""
from __future__ import annotations

import argparse
from dataclasses import asdict, dataclass
import gzip
import heapq
import json
import math
from pathlib import Path
import statistics

from navigation import Terrain


@dataclass
class PhysicsProfile:
    hz: int = 60
    max_speed: float = 1.366667
    acceleration: float = 1 / 30
    friction: float = 1 / 15
    jump_speed: float = 2.395
    gravity: float = .325 / 3
    max_fall_speed: float = 3.333334
    hold_frames: int = 16
    step_height: float = 16 / 3 + .02
    provenance: str = "Latest host source fallback (.325 gravity, 2.5 jump boost); confirm with a current trace"

    @classmethod
    def recording(cls, path, report=None):
        opener = gzip.open if str(path).endswith(".gz") else open
        records, complete = [], True
        with opener(path, "rt") as source:
            # A live recorder flushes complete JSON lines before closing the
            # gzip member. Its complete prefix is useful for calibration, but
            # must never be mistaken for a finished playthrough recording.
            try:
                for line in source:
                    records.append(json.loads(line))
            except EOFError:
                complete = False
        acceleration, friction, gravity, held_up, speeds, speed_caps = [], [], [], [], [], []
        observed_hold, current_hold = 0, 0
        for prior, current in zip(records, records[1:]):
            before, after, command = prior.get("state"), current.get("state"), current.get("command", {})
            if not before or not after or not command:
                current_hold = 0
                continue
            a, b = before["host"], after["host"]
            dt = b["frame"] - a["frame"]
            if not 0 < dt <= command.get("frames", 1) + 2 or before["stage"]["id"] != after["stage"]["id"]:
                current_hold = 0
                continue
            if not before.get("control_enabled") or not after.get("control_enabled"):
                current_hold = 0
                continue
            # Water/booster/knockback arcs need their own profile.
            if any(state.get("player", {}).get("flags", 0) & 256 or state.get("player", {}).get("booster_active") or state.get("player", {}).get("shock", 0) for state in (before, after)):
                current_hold = 0
                continue
            direction = int(bool(command.get("right"))) - int(bool(command.get("left")))
            if direction and b["vx"] * direction > .01:
                change = (b["vx"] - a["vx"]) * direction / dt
                if .01 < change < .08:
                    acceleration.append(change)
                speeds.append(abs(b["vx"]))
                if abs(b["vx"] - a["vx"]) < .005 and abs(b["vx"]) > .3 and abs(b["x"] - a["x"]) > .2:
                    speed_caps.append(abs(b["vx"]))
            if not direction and a["grounded"] and b["grounded"] and abs(a["y"] - b["y"]) < .05 and abs(b["vx"]) > .01 and a["vx"] * b["vx"] > 0:
                # Skip stopping/collision frames: those can remove the last
                # fraction of speed and underestimate ordinary friction.
                change = (abs(a["vx"]) - abs(b["vx"])) / dt
                if .02 < change < .10 and abs(b["x"] - a["x"]) > .01:
                    friction.append(change)
            if command.get("jump") and b["vy"] < -.7 and not b["grounded"]:
                # Held vanilla jump has a constant upward velocity before its
                # jump timer expires. The fastest recent cluster captures it.
                held_up.append(-b["vy"])
                if abs(b["vy"] - a["vy"]) < .005 or a["grounded"]:
                    current_hold += dt
                    observed_hold = max(observed_hold, current_hold)
                else:
                    current_hold = 0
            else:
                current_hold = 0
            if not command.get("jump") and not a["grounded"] and not b["grounded"] and b["vy"] > a["vy"]:
                change = (b["vy"] - a["vy"]) / dt
                if .04 < change < .20:
                    gravity.append(change)
        scope = "complete recording" if complete else "complete prefix of open or truncated recording"
        profile = cls(provenance=f"Measured dry-air host states from {path} ({scope}); jump hold observed for {observed_hold} frames, limit remains inferred")
        if acceleration:
            profile.acceleration = statistics.median(acceleration)
        if gravity:
            profile.gravity = statistics.median(gravity)
        if friction:
            profile.friction = statistics.median(friction)
        if held_up:
            fastest = max(held_up)
            profile.jump_speed = statistics.median(value for value in held_up if value > fastest * .98)
        if speed_caps:
            profile.max_speed = max(speed_caps)
        elif speeds:
            # A short acceleration trace has not reached the cap yet.
            profile.max_speed = max(profile.max_speed, max(speeds))
        if report is not None:
            report.update(records=len(records), complete_gzip_member=complete,
                          observed_constant_jump_hold_frames=observed_hold,
                          inferred_jump_hold_limit_frames=profile.hold_frames,
                          samples={"acceleration": len(acceleration), "friction": len(friction),
                                   "gravity": len(gravity), "held_jump_speed": len(held_up),
                                   "speed_cap_plateaus": len(speed_caps)})
        return profile


@dataclass(frozen=True)
class Support:
    id: int
    left: float
    right: float
    y: float
    surface_y: float

    def contains(self, x, y, tolerance=1.0):
        return self.left - tolerance <= x <= self.right + tolerance and abs(y - self.y) <= tolerance

    def clamp(self, x, margin=0):
        margin = min(margin, max(0, (self.right - self.left) / 2))
        return min(self.right - margin, max(self.left + margin, x))


@dataclass
class Pose:
    x: float
    y: float
    vx: float = 0
    vy: float = 0
    grounded: bool = True
    held: int = 0
    bumped_head: bool = False
    hit_hazard: bool = False


class SupportMap:
    """Exposed host-cell tops, clipped to positions where the avatar fits."""
    def __init__(self, terrain):
        self.terrain = terrain
        if not terrain.projected:
            terrain.projected = True
            terrain.cell_size = 16 / 3
        self.supports = self._extract()

    def _extract(self):
        terrain = self.terrain
        cell = terrain.cell_size
        result = []
        for row in range(1, terrain.height * 3):
            exposed = [terrain.projected_cell_solid(col, row) and not terrain.projected_cell_solid(col, row - 1)
                       for col in range(terrain.width * 3)]
            start = None
            for col in range(terrain.width * 3 + 1):
                top = col < len(exposed) and exposed[col]
                if top and start is None:
                    start = col
                if start is not None and not top:
                    surface_y = row * cell - 8
                    center_y = surface_y - terrain.half_height
                    left = start * cell - 8 - terrain.half_width + .20
                    right = col * cell - 8 + terrain.half_width - .20
                    # Ceiling and adjacent stairs may block a portion of an
                    # otherwise exposed floor. Keep each walkable interval.
                    resolution = 1 / 3
                    samples = max(1, math.ceil((right - left) / resolution))
                    run = None
                    for i in range(samples + 1):
                        x = min(right, left + i * resolution)
                        fits = terrain.safe(x, center_y)
                        if fits and run is None:
                            run = x
                        if run is not None and (not fits or i == samples):
                            end = x if fits else x - resolution
                            if end >= run:
                                result.append(Support(len(result), run, end, center_y, surface_y))
                            run = None
                    start = None
        return result

    def standing(self, x, y, tolerance=1.5):
        candidates = [support for support in self.supports if support.contains(x, y, tolerance)]
        return min(candidates, key=lambda support: abs(support.y - y), default=None)

    def nearest(self, x, y, radius=32):
        candidates = [support for support in self.supports
                      if math.hypot(support.clamp(x) - x, support.y - y) <= radius]
        return min(candidates, key=lambda support: math.hypot(support.clamp(x) - x, support.y - y), default=None)

    def step(self, x, y, max_height):
        candidates = [support for support in self.supports
                      if support.left <= x <= support.right and -.1 <= y - support.y <= max_height]
        return min(candidates, key=lambda support: abs(support.y - y), default=None)


class Simulator:
    def __init__(self, supports, profile):
        self.supports, self.terrain, self.profile = supports, supports.terrain, profile

    def unsupported_environment(self, pose):
        for x in (pose.x - self.terrain.half_width + .02, pose.x, pose.x + self.terrain.half_width - .02):
            for y in (pose.y, pose.y + self.terrain.half_height - .02):
                attribute = self.terrain.attribute_at(x, y)
                if attribute in (0x02, 0x60, 0x61, 0x62) or attribute is not None and (0x70 <= attribute <= 0x77 or 0x80 <= attribute <= 0x83 or 0xa0 <= attribute <= 0xa3):
                    return True
        return False

    def _axis(self, x, y, amount, horizontal):
        if amount == 0:
            return (x if horizontal else y), False
        position = x if horizontal else y
        target = position + amount
        fits = lambda value: self.terrain.free(value, y) if horizontal else self.terrain.free(x, value)
        # A frame may cross several cells; find the first blocked part.
        pieces = max(1, math.ceil(abs(amount) / 1.0))
        safe = position
        for i in range(1, pieces + 1):
            candidate = position + amount * i / pieces
            if fits(candidate):
                safe = candidate
                continue
            low, high = 0.0, 1.0
            for _ in range(14):
                mix = (low + high) / 2
                value = safe + (candidate - safe) * mix
                if fits(value):
                    low = mix
                else:
                    high = mix
            return safe + (candidate - safe) * low, True
        return target, False

    def tick(self, pose, direction, jump, *, jump_edge=False):
        p = Pose(**asdict(pose))
        profile = self.profile
        if direction:
            p.vx += direction * profile.acceleration
            p.vx = min(profile.max_speed, max(-profile.max_speed, p.vx))
        elif p.grounded:
            p.vx = math.copysign(max(0, abs(p.vx) - profile.friction), p.vx)
        if p.grounded and jump_edge:
            p.vy = -profile.jump_speed
            p.held = profile.hold_frames
            p.grounded = False
        if jump and p.held > 0 and p.vy < 0:
            p.vy = -profile.jump_speed
            p.held -= 1
        else:
            p.held = 0
            p.vy = min(profile.max_fall_speed, p.vy + profile.gravity)
        x, blocked = self._axis(p.x, p.y, p.vx, True)
        if blocked and pose.grounded:
            step = self.supports.step(p.x + p.vx, p.y, profile.step_height)
            if step and self.terrain.free(p.x + p.vx, step.y):
                x, p.y = p.x + p.vx, step.y
                blocked = False
        p.x = x
        p.hit_hazard |= self.terrain.unsafe_segment((pose.x, pose.y), (p.x, p.y))
        if blocked:
            p.vx = 0
        y, blocked = self._axis(p.x, p.y, p.vy, False)
        p.y = y
        p.hit_hazard |= self.terrain.unsafe_segment((p.x, pose.y), (p.x, p.y))
        p.grounded = blocked and p.vy > 0
        if blocked:
            if p.vy < 0:
                p.bumped_head = True
                p.held = 0
            p.vy = 0
        return p

    def arc(self, launch, direction, hold_frames, max_frames=150):
        pose = Pose(**asdict(launch))
        trace = [[pose.x, pose.y]]
        launch_support = self.supports.standing(pose.x, pose.y)
        if self.terrain.unsafe(pose.x, pose.y):
            return None
        airborne = hold_frames > 0
        for frame in range(max_frames):
            pose = self.tick(pose, direction, frame < hold_frames, jump_edge=frame == 0 and hold_frames > 0)
            trace.append([pose.x, pose.y])
            if pose.hit_hazard or self.unsupported_environment(pose):
                return None
            airborne |= not pose.grounded
            if pose.grounded:
                support = self.supports.standing(pose.x, pose.y, .5)
                if not airborne and support and launch_support and support.id == launch_support.id:
                    continue
                if support:
                    return {"landing_support": support.id, "landing": [pose.x, pose.y],
                            "landing_vx": pose.vx, "frames": frame + 1, "trace": trace,
                            "bumped_head": pose.bumped_head}
                break
            if not self.terrain.free(pose.x, pose.y):
                break
        return None


class PlatformPlanner:
    def __init__(self, terrain, profile=None):
        self.profile = profile or PhysicsProfile()
        self.support_map = SupportMap(terrain)
        self.supports = self.support_map.supports
        self.simulator = Simulator(self.support_map, self.profile)
        self._edges = {}

    def approach(self, source, arrival, target_x, target_vx):
        """Predict actual walking/braking to a launch; no instant speed change."""
        p = Pose(arrival[0], source.y, arrival[2])
        for _ in range(240):
            dx = target_x - p.x
            wants_stop = abs(target_vx) < .1
            if abs(dx) < .55 and (not wants_stop or abs(p.vx) < .12):
                return p
            direction = (1 if dx > 0 else -1)
            stopping = p.vx * p.vx / (2 * self.profile.friction)
            if wants_stop and p.vx * dx > 0 and abs(dx) < stopping + .3:
                direction = 0
            p = self.simulator.tick(p, direction, False)
            if p.hit_hazard or not p.grounded or not source.contains(p.x, p.y, .4):
                return None
        return None

    def _walking_edges(self, source):
        for target in self.supports:
            if source.id == target.id or abs(source.y - target.y) > self.profile.step_height:
                continue
            gap = max(0, target.left - source.right, source.left - target.right)
            if gap > .8:
                continue
            direction = 1 if (target.left + target.right) > (source.left + source.right) else -1
            launch_x = source.clamp(target.clamp((source.left + source.right) / 2))
            start = Pose(launch_x, source.y, direction * self.profile.max_speed)
            p = start
            trace = [[p.x, p.y]]
            for frame in range(30):
                p = self.simulator.tick(p, direction, False)
                trace.append([p.x, p.y])
                if p.hit_hazard:
                    break
                if p.grounded and target.contains(p.x, p.y, .4):
                    yield {"kind": "walk", "from_support": source.id, "to_support": target.id,
                           "launch": [launch_x, source.y], "launch_vx": start.vx,
                           "direction": direction, "hold_frames": 0,
                           "landing": [p.x, p.y], "landing_vx": p.vx, "frames": frame + 1, "trace": trace}
                    break
                if abs(p.x - trace[-2][0]) < .001:
                    break

    def outgoing(self, source_id):
        if source_id in self._edges:
            return self._edges[source_id]
        source = self.supports[source_id]
        margin = self.profile.max_speed * 2 + .5
        positions = {source.clamp(source.left, margin), source.clamp(source.right, margin),
                     source.clamp((source.left + source.right) / 2)}
        # Launch points near raised platforms matter on long continuous floors.
        for target in self.supports:
            if -85 < target.y - source.y < 200:
                distance = max(0, target.left - source.right, source.left - target.right)
                if distance < 120:
                    positions.add(source.clamp((target.left + target.right) / 2, margin))
        edges = list(self._walking_edges(source))
        best = {}
        for x in sorted(positions):
            for direction in (-1, 1):
                for speed in (0.0, self.profile.max_speed):
                    for held in (0, 4, 8, 12, self.profile.hold_frames):
                        arc = self.simulator.arc(Pose(x, source.y, direction * speed), direction, held)
                        if not arc or arc["landing_support"] == source.id or arc["bumped_head"]:
                            continue
                        edge = {"kind": "jump" if held else "drop", "from_support": source.id,
                                "to_support": arc["landing_support"], "launch": [x, source.y],
                                "launch_vx": direction * speed, "direction": direction,
                                "hold_frames": held, **arc}
                        key = edge["to_support"], round(x, 2), speed
                        score = edge["frames"]
                        if key not in best or score < best[key]["frames"]:
                            best[key] = edge
        edges.extend(best.values())
        self._edges[source_id] = edges
        return edges

    def plan(self, start, goal, vx=0, max_nodes=320):
        source = self.support_map.standing(*start) or self.support_map.nearest(*start, radius=10)
        target = self.support_map.nearest(*goal, radius=24)
        if not source or not target:
            raise ValueError("No reachable grounded support at source or goal; airborne/current/NPC support needs live handling")
        # Arriving on a small platform while running left is a different state
        # from arriving while running right. Collapsing those loses valid routes
        # that first brake or cross a staircase to get a usable launch velocity.
        state_key = lambda support, x, speed: (support, round(x / 8), round(speed / .5))
        initial = state_key(source.id, start[0], vx)
        frontier = [(0.0, initial)]
        scores, previous, arrivals = {initial: 0.0}, {}, {initial: [start[0], source.y, vx]}
        visited = set()
        while frontier:
            _, node = heapq.heappop(frontier)
            if node in visited:
                continue
            if node[0] == target.id:
                route = []
                while node in previous:
                    parent, edge = previous[node]
                    route.append(edge)
                    node = parent
                return {"scope": "Predicted support route; only normal live inputs can verify it",
                        "goal": list(goal), "target_support": asdict(target),
                        "profile": asdict(self.profile), "supports": [asdict(p) for p in self.supports],
                        "spike_center_hazards": self.simulator.terrain.hazards,
                        "moves": list(reversed(route))}
            visited.add(node)
            if len(visited) >= max_nodes:
                raise ValueError("Support-route search budget exhausted")
            arrival_x, _, arrival_vx = arrivals[node]
            for edge in self.outgoing(node[0]):
                edge = dict(edge)
                if edge["kind"] == "walk":
                    p = Pose(arrival_x, self.supports[node[0]].y, arrival_vx)
                    trace = [[p.x, p.y]]
                    target_support = self.supports[edge["to_support"]]
                    reached = False
                    for frame in range(120):
                        p = self.simulator.tick(p, edge["direction"], False)
                        trace.append([p.x, p.y])
                        if p.hit_hazard:
                            break
                        if p.grounded and target_support.contains(p.x, p.y, .4):
                            reached = True
                            break
                    if not reached:
                        continue
                    edge.update(launch=[arrival_x, self.supports[node[0]].y],
                                launch_vx=arrival_vx, landing=[p.x, p.y],
                                landing_vx=p.vx, frames=frame + 1, trace=trace)
                else:
                    approach = self.approach(self.supports[node[0]], arrivals[node], edge["launch"][0], edge["launch_vx"])
                    if approach is None:
                        continue
                    actual = self.simulator.arc(approach, edge["direction"], edge["hold_frames"])
                    if not actual or actual["landing_support"] == node[0] or actual["bumped_head"]:
                        continue
                    edge.update(actual)
                    edge["to_support"] = actual["landing_support"]
                    edge["launch"] = [approach.x, approach.y]
                    edge["launch_vx"] = approach.vx
                # Account for walking to the launch and changing direction.
                walk = abs(edge["launch"][0] - arrival_x) / self.profile.max_speed
                brake = abs(edge["launch_vx"] - arrival_vx) / max(.01, self.profile.acceleration) * .15
                score = scores[node] + walk + brake + edge["frames"]
                destination = state_key(edge["to_support"], edge["landing"][0], edge["landing_vx"])
                if score >= scores.get(destination, math.inf):
                    continue
                scores[destination] = score
                previous[destination] = node, edge
                arrivals[destination] = [*edge["landing"], edge["landing_vx"]]
                estimate = math.hypot(target.clamp(edge["landing"][0]) - edge["landing"][0],
                                      target.y - edge["landing"][1]) / self.profile.max_speed
                heapq.heappush(frontier, (score + estimate, destination))
        raise ValueError("No modeled walking/jump/drop route reaches that support")


class NavigationController:
    """Feedback controller for a caller who exclusively owns the live driver."""
    def __init__(self, goal, profile=None):
        self.goal, self.profile = tuple(goal), profile or PhysicsProfile()
        self.planner = None
        self.key = None
        self.route = None
        self.active = None
        self.launch_frame = None
        self.status = "unplanned"
        self.final_braking = False

    def buttons(self, state):
        host = state["host"]
        if host.get("dead"):
            raise ValueError("Live player is dead; navigation cannot repair the campaign")
        if not state.get("control_enabled") or state.get("script_mode", "Map") != "Map":
            self.status = "original_script_controls_player"
            return {"frames": 1}
        if state.get("player", {}).get("flags", 0) & 256 or state.get("player", {}).get("booster_active"):
            raise ValueError("Dry grounded profile cannot predict immersed/Booster motion; use live handling or a separately measured profile")
        spikes = tuple((n.get("id"), n.get("generation"), n["x"], n["y"], n.get("damage"),
                        n.get("left"), n.get("right"), n.get("top"), n.get("bottom"))
                       for n in state.get("npcs", ()) if n.get("type") == 211)
        key = state["stage"]["id"], state["map"].get("revision"), spikes
        if key != self.key:
            self.key = key
            self.planner = PlatformPlanner(Terrain.snapshot(state), self.profile)
            self.route, self.active = None, None
            self.final_braking = False
        # Actor coordinates need not coincide with the avatar's standing center.
        # Stop at the closest fitting support; interaction/combat still checks
        # the actual actor and original event gate in the caller.
        target_support = self.planner.support_map.nearest(*self.goal, radius=24)
        target_x = target_support.clamp(self.goal[0]) if target_support else self.goal[0]
        target_y = target_support.y if target_support else self.goal[1]
        if host.get("grounded") and math.hypot(host["x"] - target_x, host["y"] - target_y) < 5 and abs(host["vx"]) < .1:
            self.status = "reached"
            return {"frames": 1}
        current_support = self.planner.support_map.standing(host["x"], host["y"])
        still_approaching_drop = self.active and self.active["kind"] in {"drop", "walk"} and current_support and current_support.id == self.active["from_support"]
        if self.active and (not host.get("grounded") or still_approaching_drop):
            elapsed = host["frame"] - self.launch_frame
            self.status = "following_predicted_arc"
            return {"frames": 1, "left": self.active["direction"] < 0,
                    "right": self.active["direction"] > 0,
                    "jump": elapsed < self.active["hold_frames"]}
        if self.active and host.get("grounded"):
            self.active = None
            self.route = None
            self.status = "landed_replanning"
            return {"frames": 1, "jump": False}
        if not host.get("grounded"):
            self.status = "awaiting_grounded_support"
            return {"frames": 1}
        if self.route is None:
            self.route = self.planner.plan((host["x"], host["y"]), self.goal, host["vx"])
        if not self.route["moves"]:
            support = self.planner.supports[self.route["target_support"]["id"]]
            target_x = support.clamp(self.goal[0])
            dx = target_x - host["x"]
            self.status = "walking_on_goal_support"
            # Keep releasing until stopped. Re-evaluating the threshold each
            # frame can briefly re-accelerate while braking and push a player
            # off a narrow ledge next to a spike actor.
            if self.final_braking and abs(host["vx"]) < .1:
                self.final_braking = False
            if self.final_braking or dx * host["vx"] > 0 and abs(dx) <= host["vx"] ** 2 / (2 * self.profile.friction) + .3:
                self.final_braking = True
                self.status = "braking_at_goal"
                return {"frames": 1, "jump": False}
            return {"frames": 1, "left": dx < -.8, "right": dx > .8, "jump": False}
        edge = self.route["moves"][0]
        dx = edge["launch"][0] - host["x"]
        if edge["kind"] != "walk" and abs(dx) < 2:
            dynamic = self.planner.simulator.arc(Pose(host["x"], host["y"], host["vx"]), edge["direction"], edge["hold_frames"])
            if dynamic and dynamic["landing_support"] != edge["from_support"] and not dynamic["bumped_head"]:
                self.active = {**edge, **dynamic, "to_support": dynamic["landing_support"]}
                self.launch_frame = host["frame"]
                self.status = "launching_predicted_arc"
                return {"frames": 1, "left": edge["direction"] < 0,
                        "right": edge["direction"] > 0, "jump": edge["hold_frames"] > 0}
        if edge["kind"] == "walk" and abs(dx) < 2:
            self.active = edge
            self.launch_frame = host["frame"]
            return {"frames": 1, "left": edge["direction"] < 0, "right": edge["direction"] > 0, "jump": False}
        # One-frame approach prevents stepping past a narrow launch window.
        self.status = "walking_to_launch"
        if abs(edge["launch_vx"]) < .1 and host["vx"] * dx > 0 and abs(dx) <= host["vx"] ** 2 / (2 * self.profile.friction) + .3:
            return {"frames": 1, "jump": False}
        if abs(dx) < .5:
            # The current speed invalidated the arc. Release movement to brake,
            # then replan from the next observed grounded state.
            self.route = None
            return {"frames": 1, "jump": False}
        return {"frames": 1, "left": dx < 0, "right": dx > 0, "jump": False}


def render_svg(terrain, route, path):
    """Standalone geometry/trajectory diagram, explicitly a model artifact."""
    width, height = terrain.width * 16, terrain.height * 16
    svg = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{width*3}" height="{(height+28)*3}" viewBox="-8 -8 {width} {height+28}">',
           '<rect x="-8" y="-8" width="100%" height="100%" fill="#101827"/>']
    cell = terrain.cell_size
    for row in range(terrain.height * 3):
        for col in range(terrain.width * 3):
            if terrain.projected_cell_solid(col, row):
                svg.append(f'<rect x="{col*cell-8:.3f}" y="{row*cell-8:.3f}" width="{cell:.3f}" height="{cell:.3f}" fill="#37465b"/>')
    for support in route["supports"]:
        svg.append(f'<path d="M {support["left"]:.2f} {support["surface_y"]:.2f} H {support["right"]:.2f}" stroke="#dfb76c" stroke-width=".8"/>')
    for hazard in route.get("spike_center_hazards", ()):
        svg.append(f'<rect x="{hazard["left"]:.3f}" y="{hazard["top"]:.3f}" width="{hazard["right"]-hazard["left"]:.3f}" height="{hazard["bottom"]-hazard["top"]:.3f}" fill="#f57f83" fill-opacity=".3" stroke="#f57f83" stroke-width=".6"/>')
    for i, move in enumerate(route["moves"]):
        points = " ".join(f'{x:.2f},{y:.2f}' for x, y in move["trace"])
        svg.append(f'<polyline points="{points}" fill="none" stroke="#85ddf4" stroke-width="1"/>')
        x, y = move["launch"]
        svg.append(f'<circle cx="{x:.2f}" cy="{y:.2f}" r="1.8" fill="#f57f83"/>')
        svg.append(f'<text x="{x+3:.2f}" y="{y+1:.2f}" fill="#ffffff" font-family="monospace" font-size="4">{i+1}: hold {move["hold_frames"]}</text>')
    svg.append(f'<text x="0" y="{height+5}" fill="#dfb76c" font-family="monospace" font-size="5">Predicted supports and Terraria arcs; live inputs must verify traversal.</text>')
    svg.append('</svg>')
    Path(path).write_text("\n".join(svg) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--snapshot", type=Path)
    parser.add_argument("--map", default="Start")
    parser.add_argument("--recording", type=Path)
    parser.add_argument("--profile", type=Path)
    parser.add_argument("--start", nargs=2, type=float)
    parser.add_argument("--goal", nargs=2, type=float, required=True)
    parser.add_argument("--json", type=Path)
    parser.add_argument("--svg", type=Path)
    args = parser.parse_args()
    profile = PhysicsProfile.recording(args.recording) if args.recording else PhysicsProfile(**json.loads(args.profile.read_text())) if args.profile else PhysicsProfile()
    if args.snapshot:
        state = json.loads(args.snapshot.read_text())
        terrain = Terrain.snapshot(state)
        start = args.start or [state["host"]["x"], state["host"]["y"]]
        vx = state["host"]["vx"]
    else:
        terrain = Terrain.original(args.map, projected=True)
        start, vx = args.start, 0
    if start is None:
        parser.error("--start is required without a live snapshot")
    result = PlatformPlanner(terrain, profile).plan(start, args.goal, vx)
    if args.svg:
        render_svg(terrain, result, args.svg)
    rendered = json.dumps(result, indent=2) + "\n"
    if args.json:
        args.json.write_text(rendered)
    else:
        print(rendered, end="")


if __name__ == "__main__":
    main()
