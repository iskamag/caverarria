#!/usr/bin/env python3
"""Interactive real-control driver, with a recorded and auditable playthrough.

Run with ``python3 -i tests/campaign/drive.py`` after launching the test client.
All helpers use ordinary inputs. Geometry supplies waypoints, never positions.
"""
from __future__ import annotations

import argparse
import math
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from terraria_driver import Terraria
from navigation import Terrain
from physics_navigation import NavigationController
from playthrough import RecordingTerraria


def summary(state):
    host = state["host"]
    return {"map": state["stage"]["map"], "pos": [round(host["x"], 2), round(host["y"], 2)],
            "velocity": [round(host["vx"], 2), round(host["vy"], 2)], "life": host["life"],
            "grounded": host["grounded"], "script": state.get("script"),
            "mode": state.get("script_mode"), "control": state.get("control_enabled"),
            "weapons": state.get("weapons"), "items": state.get("items"), "error": state.get("error")}


def show():
    state = game.state()
    print(summary(state), flush=True)
    return state


def press(frames=4, **buttons):
    state = game.input(frames=frames, **buttons)
    print(summary(state), flush=True)
    return state


def advance(limit=160, choice="yes"):
    """Advance original text with pulsed confirmation; preserve menu choice."""
    for i in range(limit):
        state = game.state()
        if state["host"]["dead"]:
            raise RuntimeError("Player died during dialogue")
        if state.get("control_enabled") and state.get("script") == "Ended" and state.get("script_mode") == "Map":
            return state
        if "YesNo" in state.get("script", "") and choice == "no":
            game.input(frames=3, right=True)
            game.input(frames=3)
        game.input(frames=3, confirm=True)
        state = game.input(frames=6)
        if i % 12 == 0:
            print(summary(state), flush=True)
    raise RuntimeError("Original dialogue has not finished: " + str(summary(game.state())))


def interact(choice="yes", limit=160):
    game.input(frames=4)
    game.input(frames=4, interact=True)
    game.input(frames=4)
    return advance(limit, choice)


def enemies():
    return [npc for npc in game.state().get("npcs", []) + game.state().get("bosses", [])
            if npc.get("shootable") and npc.get("life", 0) > 0]


def walk(x, y, limit=1500, shoot=False):
    """Follow modeled supports with ordinary controls, checking live arrivals."""
    controller = NavigationController((x, y))
    stage = game.state()["stage"]["id"]
    for i in range(limit):
        state = game.state()
        if state["stage"]["id"] != stage:
            print("Original stage transfer:", summary(state), flush=True)
            return state
        if not state.get("control_enabled") or state.get("script_mode") != "Map":
            return advance()
        buttons = controller.buttons(state)
        if controller.status == "reached":
            print("Reached support:", summary(state), flush=True)
            return state
        if shoot:
            targets = [npc for npc in state.get("npcs", []) + state.get("bosses", [])
                       if npc.get("shootable") and npc.get("life", 0) > 0]
            if targets:
                target = min(targets, key=lambda npc: math.hypot(
                    npc["x"] - state["host"]["x"], npc["y"] - state["host"]["y"]))
                buttons.update(shoot=True, aimX=target["x"], aimY=target["y"])
        state = game.input(**buttons)
        if i % 120 == 0:
            print(controller.status, summary(state), flush=True)
    raise RuntimeError("Support traversal input budget exhausted: " + str(summary(game.state())))


def go(x, y, limit=400, shoot=False):
    """Follow authored terrain through actual Terraria walking and jumping."""
    state = game.state()
    stage = state["stage"]["id"]
    path = None
    stagnant = 0
    previous = None
    for i in range(limit):
        state = game.state()
        host = state["host"]
        if host["dead"]:
            raise RuntimeError("Player died while traversing " + state["stage"]["map"])
        if state["stage"]["id"] != stage:
            print("Original stage transfer:", summary(state), flush=True)
            return state
        if not state.get("control_enabled") or state.get("script_mode") != "Map":
            return advance()
        distance = math.hypot(host["x"] - x, host["y"] - y)
        if distance < 9:
            game.input(frames=4)
            print("Reached waypoint:", summary(game.state()), flush=True)
            return game.state()
        if path is None or i % 8 == 0:
            path = Terrain.snapshot(state).corridor((host["x"], host["y"]), (x, y))
        nearest = min(range(len(path)), key=lambda j: math.hypot(path[j][0] - host["x"], path[j][1] - host["y"]))
        ahead = path[nearest:min(nearest + 9, len(path))]
        target = ahead[-1]
        dx = target[0] - host["x"]
        rising = any(point[1] < host["y"] - 4 for point in ahead)
        buttons = {"left": dx < -2, "right": dx > 2,
                   "jump": rising and (host["grounded"] or host["vy"] < 0)}
        if shoot:
            targets = enemies()
            if targets:
                target_npc = min(targets, key=lambda npc: math.hypot(npc["x"] - host["x"], npc["y"] - host["y"]))
                buttons.update(shoot=True, aimX=target_npc["x"], aimY=target_npc["y"])
        state = game.input(frames=4, **buttons)
        position = state["host"]["x"], state["host"]["y"]
        stagnant = stagnant + 1 if previous and math.dist(position, previous) < .3 else 0
        previous = position
        if i % 20 == 0:
            print("Walking", i, summary(state), flush=True)
        if stagnant > 18:
            raise RuntimeError("Traversal stalled; review geometry and jump arc: " + str(summary(state)))
    raise RuntimeError("Waypoint input budget exhausted: " + str(summary(game.state())))


def fight(limit=1000):
    """Fire genuine held items at live native actors while evading contact."""
    for i in range(limit):
        state = game.state()
        host = state["host"]
        if host["dead"]:
            raise RuntimeError("Player died during combat")
        if not state.get("control_enabled"):
            return advance()
        targets = enemies()
        if not targets:
            return state
        target = min(targets, key=lambda npc: math.hypot(npc["x"] - host["x"], npc["y"] - host["y"]))
        dx = target["x"] - host["x"]
        retreat = abs(dx) < 45 and abs(target["y"] - host["y"]) < 30
        state = game.input(frames=4, shoot=True, aimX=target["x"], aimY=target["y"],
                           left=retreat and dx > 0, right=retreat and dx < 0,
                           jump=retreat and host["grounded"])
        game.input(frames=3)
        if i % 20 == 0:
            print("Combat", i, summary(state), [(npc["type"], npc["life"]) for npc in targets], flush=True)
    raise RuntimeError("Combat input budget exhausted")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--test-dir", type=Path, default=Path("artifacts/live-test"))
    parser.add_argument("--record", type=Path)
    args = parser.parse_args()
    driver = Terraria(args.test_dir)
    driver.wait_ready()
    record = args.record or Path(f"artifacts/playthroughs/live-{int(time.time())}.jsonl.gz")
    game = RecordingTerraria(driver, record)
    print("Recording actual controls to", record, flush=True)
    show()
