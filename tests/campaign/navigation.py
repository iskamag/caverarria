"""Find geometric corridors through original tiles for a real-input game driver.

Paths account for the hosted Terraria avatar's size, Cave Story's centered tile
coordinates, and half slopes. They are suggestions: only the running Terraria
physics can establish that the jumps along a path are actually possible. NPCs,
currents, doors and destructible tiles need live replanning and combat handling.
"""
from __future__ import annotations

import argparse
from functools import lru_cache
import heapq
import json
import math
from pathlib import Path
import struct

from inspect_campaign import REPO, load_npc_table, load_npcs, load_stages, resolve_original_path


class Terrain:
    def __init__(self, width, height, tiles, attributes, half_width=10 / 3, half_height=7, projected=False, spike_actors=()):
        self.width, self.height = width, height
        self.tiles, self.attributes = tiles, attributes
        self.half_width, self.half_height = half_width, half_height
        self.projected = projected
        self.cell_size = 16 / 3 if projected else 16
        self._solid_grid = None
        # These rectangles describe unsafe player CENTER positions. Upstream
        # tile spikes test the fixed +/-4 player sensor against a +/-4 by +/-3
        # tile sensor. NPC211 uses its own hit bounds against the fixed +/-2
        # non-solid NPC contact sensor. Neither shape uses host avatar bounds.
        self.hazards = []
        for row in range(height):
            for col in range(width):
                if attributes[tiles[row * width + col]] in (0x42, 0x62):
                    self.hazards.append({"kind": "tile_spike", "left": col * 16 - 8, "right": col * 16 + 8,
                                         "top": row * 16 - 7, "bottom": row * 16 + 7})
        for actor in spike_actors:
            if actor.get("type") == 211 and actor.get("damage", 0) > 0:
                # NPC211 is stationary and has symmetric original bounds.
                self.hazards.append({"kind": "npc_spike", "id": actor.get("id"),
                                     "left": actor["x"] - actor["left"] - 2,
                                     "right": actor["x"] + actor["right"] + 2,
                                     "top": actor["y"] - actor["top"] - 2,
                                     "bottom": actor["y"] + actor["bottom"] + 2})

    @staticmethod
    @lru_cache(maxsize=256)
    def _projected_mask(attribute):
        if attribute in (0x05, 0x41, 0x43, 0x44, 0x46, 0x61):
            return (True,) * 9
        slope = attribute - 0x20 if 0x70 <= attribute <= 0x77 else attribute
        cells = []
        for sy in range(3):
            for sx in range(3):
                if not 0x50 <= slope <= 0x57:
                    cells.append(False)
                    continue
                cx, cy = (sx + .5) / 3, (sy + .5) / 3
                boundary = {0x50: 1 - cx / 2, 0x51: .5 - cx / 2, 0x52: cx / 2,
                            0x53: .5 + cx / 2, 0x54: cx / 2, 0x55: .5 + cx / 2,
                            0x56: 1 - cx / 2, 0x57: .5 - cx / 2}[slope]
                cells.append(cy < boundary if slope < 0x54 else cy > boundary)
        return tuple(cells)

    @classmethod
    def original(cls, map_name: str, data: Path = REPO / "runtime/data", **bounds):
        stage = next(stage for stage in load_stages(data, REPO / "runtime/Doukutsu.exe") if stage.map.lower() == map_name.lower())
        raw = resolve_original_path(data / "Stage" / f"{stage.map}.pxm").read_bytes()
        width, height = struct.unpack_from("<HH", raw, 4)
        attributes = resolve_original_path(data / "Stage" / f"{stage.tileset}.pxa").read_bytes()
        table = load_npc_table(data / "npc.tbl")
        actors = []
        for npc in load_npcs(data / "Stage" / f"{stage.map}.pxe", table):
            if npc["type"] == 211:
                left, top, right, bottom = table[211]["hit_bounds"]
                actors.append({"id": npc["index"], "type": 211, "damage": table[211]["damage"],
                               "x": npc["x"] * 16, "y": npc["y"] * 16,
                               "left": left, "top": top, "right": right, "bottom": bottom})
        return cls(width, height, list(raw[8:]), list(attributes) + [0] * max(0, 256 - len(attributes)), spike_actors=actors, **bounds)

    @classmethod
    def snapshot(cls, state: dict):
        stage, map_data = state["stage"], state["map"]
        host = state.get("host", {})
        return cls(stage["width"], stage["height"], map_data["tiles"], map_data["attributes"], host.get("width", 20 / 3) / 2, host.get("height", 14) / 2, projected=True, spike_actors=state.get("npcs", ()))

    def unsafe(self, x, y):
        """Original spike contact sensor, kept separate from solid collision."""
        return any(h["left"] < x < h["right"] and h["top"] < y < h["bottom"] for h in self.hazards)

    def unsafe_segment(self, start, end):
        """Swept center sensor; an arc must not cross a spike between frames."""
        for hazard in self.hazards:
            enter, leave = 0.0, 1.0
            for axis, low_key, high_key in ((0, "left", "right"), (1, "top", "bottom")):
                delta = end[axis] - start[axis]
                if abs(delta) < 1e-12:
                    if not hazard[low_key] < start[axis] < hazard[high_key]:
                        break
                else:
                    low = (hazard[low_key] - start[axis]) / delta
                    high = (hazard[high_key] - start[axis]) / delta
                    enter, leave = max(enter, min(low, high)), min(leave, max(low, high))
                    if enter >= leave:
                        break
            else:
                if enter < leave:
                    return True
        return False

    def safe(self, x, y):
        return self.free(x, y) and not self.unsafe(x, y)

    def projected_cell_solid(self, col, row):
        """The host's actual three Terraria cells per centered original tile."""
        if col < 0 or row < 0 or col >= self.width * 3 or row >= self.height * 3:
            return True
        if self._solid_grid is None:
            self._solid_grid = [[self._projected_mask(self.attributes[self.tiles[(r // 3) * self.width + c // 3]])[(r % 3) * 3 + c % 3]
                                 for c in range(self.width * 3)] for r in range(self.height * 3)]
        return self._solid_grid[row][col]

    def point_solid(self, x: float, y: float) -> bool:
        if self.projected:
            return self.projected_cell_solid(math.floor((x + 8) / self.cell_size), math.floor((y + 8) / self.cell_size))
        tx, ty = math.floor((x + 8) / 16), math.floor((y + 8) / 16)
        if tx < 0 or ty < 0 or tx >= self.width or ty >= self.height:
            return True
        attribute = self.attributes[self.tiles[ty * self.width + tx]]
        if attribute in (0x05, 0x41, 0x43, 0x44, 0x46, 0x61):
            return True
        slope = attribute & ~0x20 if attribute in range(0x70, 0x78) else attribute
        dx, dy = x - tx * 16, y - ty * 16
        if slope == 0x50:
            return dy < -dx / 2 + 4
        if slope == 0x51:
            return dy < -dx / 2 - 4
        if slope == 0x52:
            return dy < dx / 2 - 4
        if slope == 0x53:
            return dy < dx / 2 + 4
        if slope == 0x54:
            return dy > dx / 2 - 4
        if slope == 0x55:
            return dy > dx / 2 + 4
        if slope == 0x56:
            return dy > -dx / 2 + 4
        if slope == 0x57:
            return dy > -dx / 2 - 4
        return False

    def attribute_at(self, x, y):
        tx, ty = math.floor((x + 8) / 16), math.floor((y + 8) / 16)
        if not 0 <= tx < self.width or not 0 <= ty < self.height:
            return None
        return self.attributes[self.tiles[ty * self.width + tx]]

    def free(self, x: float, y: float) -> bool:
        if self.projected:
            # Test every overlapped host cell rather than three sample points;
            # a thin corner overlap is enough to block a real Terraria avatar.
            skin = .002
            left, right = x - self.half_width + skin, x + self.half_width - skin
            top, bottom = y - self.half_height + skin, y + self.half_height - skin
            return not any(self.projected_cell_solid(col, row)
                           for col in range(math.floor((left + 8) / self.cell_size), math.floor((right + 8) / self.cell_size) + 1)
                           for row in range(math.floor((top + 8) / self.cell_size), math.floor((bottom + 8) / self.cell_size) + 1))
        left, right = x - self.half_width + 0.15, x + self.half_width - 0.15
        top, bottom = y - self.half_height + 0.15, y + self.half_height - 0.15
        return not any(self.point_solid(px, py) for px in (left, x, right) for py in (top, y, bottom))

    def nearest_free(self, x, y, grid=4, radius=32):
        base = (round(x / grid), round(y / grid))
        if self.safe(base[0] * grid, base[1] * grid):
            return base
        candidates = ((base[0] + dx, base[1] + dy) for dx in range(-radius // grid, radius // grid + 1) for dy in range(-radius // grid, radius // grid + 1))
        candidates = (node for node in candidates if self.safe(node[0] * grid, node[1] * grid))
        return min(candidates, key=lambda node: math.hypot(node[0] * grid - x, node[1] * grid - y), default=None)

    def corridor(self, start, goal, grid=4, max_nodes=100000):
        source, target = self.nearest_free(*start, grid), self.nearest_free(*goal, grid)
        if source is None or target is None:
            raise ValueError("Cannot find free terrain near source or target")
        frontier = [(0, source)]
        scores, previous = {source: 0.0}, {}
        visited = set()
        while frontier:
            _, current = heapq.heappop(frontier)
            if current in visited:
                continue
            if current == target:
                path = [current]
                while current in previous:
                    current = previous[current]
                    path.append(current)
                return [(x * grid, y * grid) for x, y in reversed(path)]
            visited.add(current)
            if len(visited) > max_nodes:
                raise ValueError("Corridor search exceeded node budget")
            for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, -1), (-1, 1), (1, 1)):
                candidate = current[0] + dx, current[1] + dy
                if candidate in visited or not self.safe(candidate[0] * grid, candidate[1] * grid) or self.unsafe_segment((current[0] * grid, current[1] * grid), (candidate[0] * grid, candidate[1] * grid)):
                    continue
                if dx and dy and not (self.free(candidate[0] * grid, current[1] * grid) and self.free(current[0] * grid, candidate[1] * grid)):
                    continue
                # Prefer natural downhill corridors and walking. This does not
                # grant a movement ability; the driver must jump in the game.
                score = scores[current] + math.hypot(dx, dy) + (0.25 if dy < 0 else 0)
                if score >= scores.get(candidate, math.inf):
                    continue
                scores[candidate] = score
                previous[candidate] = current
                estimate = score + math.hypot(candidate[0] - target[0], candidate[1] - target[1])
                heapq.heappush(frontier, (estimate, candidate))
        raise ValueError("No geometric corridor connects source and target")


def steer(state: dict, path: list, jump_cooldown=0) -> dict:
    """Return normal input buttons toward a short lookahead on a live path."""
    host = state["host"]
    current = host["x"], host["y"]
    nearest = min(range(len(path)), key=lambda i: math.hypot(path[i][0] - current[0], path[i][1] - current[1]))
    target = path[min(nearest + 2, len(path) - 1)]
    dx, dy = target[0] - current[0], target[1] - current[1]
    return {"frames": 4, "left": dx < -1.5, "right": dx > 1.5, "jump": bool(host.get("grounded") and dy < -2 and jump_cooldown <= 0)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--map", dest="map_name", default="Start")
    parser.add_argument("--snapshot", type=Path)
    parser.add_argument("--start", nargs=2, type=float, required=True)
    parser.add_argument("--goal", nargs=2, type=float, required=True)
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()
    terrain = Terrain.snapshot(json.loads(args.snapshot.read_text())) if args.snapshot else Terrain.original(args.map_name)
    result = {"scope": "Geometric corridor only; movement must be proven in Terraria", "path": terrain.corridor(args.start, args.goal)}
    output = json.dumps(result, indent=2) + "\n"
    if args.json:
        args.json.write_text(output)
    else:
        print(output, end="")


if __name__ == "__main__":
    main()
