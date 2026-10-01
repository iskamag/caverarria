"""Record and audit genuine tModLoader controls, separate from native diagnostics.

The recorder wraps tests.terraria_driver.Terraria without exposing its engine
debug argument. The audit detects unsupported commands and missing campaign
milestones; it cannot turn a partial or injected run into completion evidence.
"""
from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import math
from pathlib import Path
import re
import time

from inspect_campaign import REPO, inspect_campaign

BUTTONS = {"left", "right", "up", "down", "jump", "interact", "confirm", "shoot", "inventory", "map", "nextWeapon", "previousWeapon", "retry"}
ALLOWED = BUTTONS | {"aimX", "aimY", "item", "frames"}
COMMON_STAGE_ORDER = [13, 12, 90, 11, 2, 6, 10, 35, 9, 39, 47, 48, 31, 49, 52, 53]
NORMAL_STAGE_ORDER = [56, 64, 65, 68, 70, 91, 71, 0]
BEST_STAGE_ORDER = [56, 64, 65, 68, 70, 79, 80, 81, 82, 87, 91, 92, 71, 0]
COMMON_NPC_COMBAT = {(12, 59), (2, 88), (25, 36), (29, 118), (35, 140), (41, 160), (44, 169)}
COMMON_BOSS_COMBAT = {(10, 1), (28, 2), (39, 3), (47, 4), (31, 5)}
FINAL_NPC_COMBAT = {(64, 247), (65, 263), (65, 267)}
FINAL_BOSS_COMBAT = {(68, 7)}
BEST_NPC_COMBAT = {(83, 313), (67, 276), (87, 340)}
BEST_BOSS_COMBAT = {(82, 8), (87, 9)}


def _canonical(value):
    return json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":"))


def _open(path, mode):
    return gzip.open(path, mode, encoding="utf-8") if str(path).endswith(".gz") else open(path, mode, encoding="utf-8")


def validate_controls(command):
    if set(command) - ALLOWED:
        raise ValueError(f"Unsupported/debug control fields: {sorted(set(command) - ALLOWED)}")
    frames = command.get("frames", 1)
    if type(frames) is not int or not 1 <= frames <= 36000:
        raise ValueError("frames must be an integer from 1 to 36000")
    for button in BUTTONS:
        if button in command and type(command[button]) is not bool:
            raise ValueError(f"{button} must be boolean")
    for axis in ("aimX", "aimY"):
        if axis in command and (not isinstance(command[axis], (int, float)) or not math.isfinite(command[axis])):
            raise ValueError(f"{axis} must be finite")
    if ("aimX" in command) != ("aimY" in command):
        raise ValueError("Both aim coordinates are required")
    if "item" in command and (type(command["item"]) is not int or not 0 <= command["item"] <= 9):
        raise ValueError("item must select a normal inventory slot from 0 to 9")


def validate_host_state(state):
    if not state or "host" not in state or "stage" not in state:
        raise ValueError("Missing live tModLoader host or original stage state")
    if state.get("debugCommandsUsed") != 0:
        raise ValueError("Completion run used engine debug commands or did not report their count")
    if state.get("error") or state.get("ok") is False:
        raise ValueError(f"Runtime error: {state.get('error')}")
    for field in ("x", "y", "vx", "vy", "frame"):
        if field not in state["host"] or not isinstance(state["host"][field], (int, float)) or not math.isfinite(state["host"][field]):
            raise ValueError(f"Invalid real player field {field}")


def validate_fresh_state(state):
    if state["stage"]["id"] != 13 or state.get("weapons") or state.get("items"):
        raise ValueError("A fresh recorded campaign must begin in Start Point with no native weapons/items")
    # Start200 sets430 during the introduction. No story or combat gates have
    # been acquired when control first becomes available in Start Point.
    if set(state.get("flags", [])) - {430}:
        raise ValueError("Fresh Start Point contains original story flags from an earlier campaign")
    player = state.get("player", {})
    if player.get("life") != 3 or player.get("max_life") != 3 or player.get("equipment") != 0:
        raise ValueError("Fresh campaign must retain the original starting life and unequipped native player")


class RecordingTerraria:
    """Wrap an already-ready Terraria driver; retain full observed host states."""
    def __init__(self, driver, path: Path, *, fresh_campaign=True):
        self.driver, self.path = driver, Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        if self.path.exists():
            raise FileExistsError(f"Refusing to overwrite playthrough evidence: {self.path}")
        self.previous_hash = "0" * 64
        self.last_state = driver.state()
        validate_host_state(self.last_state)
        if fresh_campaign:
            validate_fresh_state(self.last_state)
        self.file = _open(self.path, "wt")
        self._record({"kind": "begin", "fresh_campaign": fresh_campaign, "state": self.last_state, "created_ns": time.time_ns(), "schema": 1})

    def _record(self, record):
        record["previous_sha256"] = self.previous_hash
        digest = hashlib.sha256(_canonical(record).encode()).hexdigest()
        record["sha256"] = digest
        self.file.write(_canonical(record) + "\n")
        self.file.flush()
        self.previous_hash = digest

    def state(self):
        return self.driver.state()

    def input(self, frames=1, timeout=20, **buttons):
        command = {"frames": frames, **buttons}
        validate_controls(command)
        before = self.last_state
        state = self.driver.input(frames=frames, timeout=timeout, **buttons)
        validate_host_state(state)
        if state["host"]["frame"] <= before["host"]["frame"]:
            raise ValueError("Input acknowledgement did not advance the actual tModLoader frame")
        self._record({"kind": "input", "command": command, "previous_host_frame": before["host"]["frame"], "state": state, "observed_ns": time.time_ns()})
        self.last_state = state
        return state

    def checkpoint(self, name, **artifacts):
        state = self.driver.state()
        validate_host_state(state)
        self._record({"kind": "checkpoint", "name": name, "state": state, "artifacts": artifacts, "observed_ns": time.time_ns()})
        self.last_state = state
        return state

    def close(self):
        self.file.close()

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


def _subsequence(sequence, requirement):
    index = 0
    for value in sequence:
        if index < len(requirement) and value == requirement[index]:
            index += 1
    return requirement[index:]


def damage_witnesses(before, after, command):
    """Observe native life decreases/removals during an ordinary firing input.

    This records consequences, not damage injection. Actor removals can also be
    scripted, so the report labels them separately from an observed HP decrease.
    """
    if before["stage"]["id"] != after["stage"]["id"] or command.get("retry"):
        return []
    if not (command.get("shoot") or before.get("bullets") or after.get("bullets")):
        return []
    stage = before["stage"]["id"]
    boss_number = before["stage"].get("boss")
    if boss_number is None:
        boss_number = next((entry.get("boss", 0) for entry in before.get("stages", []) if entry.get("id") == stage), 0)
    result = []
    for field in ("npcs", "bosses"):
        prior = before.get(field, [])
        current = {(npc.get("id"), npc.get("generation", 0), npc.get("type")): npc for npc in after.get(field, [])}
        boss_vulnerable = field == "bosses" and any(npc.get("shootable") for npc in prior)
        for npc in prior:
            if not (npc.get("shootable") or boss_vulnerable) or npc.get("life", 0) <= 0:
                continue
            key = (npc.get("id"), npc.get("generation", 0), npc.get("type"))
            target = current.get(key)
            if target is not None and target.get("life", npc["life"]) < npc["life"]:
                result.append({"stage": stage, "kind": field, "type": npc.get("type"), "id": npc.get("id"), "before_life": npc["life"], "after_life": target["life"], "evidence": "life_decreased", "boss_number": boss_number})
            elif target is None and field == "npcs" and command.get("shoot"):
                result.append({"stage": stage, "kind": field, "type": npc.get("type"), "id": npc.get("id"), "before_life": npc["life"], "after_life": None, "evidence": "actor_removed_while_firing", "boss_number": 0})
    return result


def audit_recording(path: Path, ending: str) -> dict:
    issues, records = [], []
    previous_hash = "0" * 64
    with _open(path, "rt") as source:
        for line_number, line in enumerate(source, 1):
            record = json.loads(line)
            digest = record.pop("sha256", None)
            expected = hashlib.sha256(_canonical(record).encode()).hexdigest()
            if digest != expected or record.get("previous_sha256") != previous_hash:
                issues.append(f"Line {line_number}: broken evidence hash chain")
            previous_hash = digest
            records.append(record)
    if not records:
        raise ValueError("Empty playthrough record")
    first = records[0]
    if first.get("kind") != "begin" or first.get("fresh_campaign") is not True:
        issues.append("Run lacks a fresh-campaign starting record")
    states = []
    command_count = 0
    last_frame = None
    previous_state = None
    observed_projectiles = False
    real_movement = False
    stage_transitions = []
    stages_seen = set()
    combat = []
    for index, record in enumerate(records, 1):
        state = record.get("state")
        try:
            validate_host_state(state)
            if record["kind"] == "input":
                validate_controls(record["command"])
                command_count += 1
                if last_frame is not None and state["host"]["frame"] <= last_frame:
                    issues.append(f"Record {index}: input frame did not advance")
                if record["command"].get("shoot") and (state["host"].get("projectiles", 0) > 0 or state.get("bullets")):
                    observed_projectiles = True
                if previous_state and any(record["command"].get(button) for button in ("left", "right", "jump")):
                    before, after = previous_state["host"], state["host"]
                    if state["stage"]["id"] == previous_state["stage"]["id"] and math.hypot(after["x"] - before["x"], after["y"] - before["y"]) > 1:
                        real_movement = True
                if previous_state:
                    combat.extend({**witness, "record": index} for witness in damage_witnesses(previous_state, state, record["command"]))
            elif record["kind"] not in {"begin", "checkpoint"}:
                issues.append(f"Record {index}: unknown record kind")
            last_frame = state["host"]["frame"]
            if previous_state and state["stage"]["id"] != previous_state["stage"]["id"]:
                source, target = previous_state["stage"]["id"], state["stage"]["id"]
                retry = record["kind"] == "input" and record["command"].get("retry")
                stage_transitions.append((index, source, target, retry))
                if retry and target not in stages_seen:
                    issues.append(f"Record {index}: retry loaded a stage absent from this fresh run")
            stages_seen.add(state["stage"]["id"])
            previous_state = state
            states.append(state)
        except (KeyError, TypeError, ValueError) as error:
            issues.append(f"Record {index}: {error}")
    if not states:
        return {"complete": False, "issues": issues + ["No valid live states"]}
    try:
        validate_fresh_state(states[0])
    except ValueError as error:
        issues.append(str(error))
    if not command_count or not real_movement or not observed_projectiles:
        issues.append("Run lacks recorded real controls, player movement, or real firing/projectiles")
    stage_sequence = []
    for state in states:
        if not stage_sequence or stage_sequence[-1] != state["stage"]["id"]:
            stage_sequence.append(state["stage"]["id"])
    missing = _subsequence(stage_sequence, COMMON_STAGE_ORDER)
    if missing:
        issues.append(f"Missing common authored route stages in order: {missing}")
    if ending in {"normal", "best"}:
        expected = NORMAL_STAGE_ORDER if ending == "normal" else BEST_STAGE_ORDER
        missing = _subsequence(stage_sequence, expected)
        if missing:
            issues.append(f"Missing {ending} authored ending route stages in order: {missing}")
        terminal_event = 1100 if ending == "normal" else 1200
        complete_states = [state for state in states if state["stage"]["id"] == 0 and state.get("credits") and re.fullmatch(rf"WaitTicks\({terminal_event}, \d+, 9999\)", state.get("script", "")) and any(npc.get("type") == 360 for npc in state.get("npcs", []))]
        if not complete_states:
            issues.append("Original credits have not reached their native THANK YOU actor and terminal event")
        elif ending == "best" and not any(2000 in state.get("flags", []) and 1460 in state.get("flags", []) for state in complete_states):
            issues.append("Best ending flags are absent from the completed original credits")
        elif ending == "normal" and any(2000 in state.get("flags", []) for state in complete_states):
            issues.append("Recording reached best ending instead of the requested normal ending")
        if ending == "best":
            flags_observed = set(flag for state in states for flag in state.get("flags", []))
            required_flags = {835, 836, 1441, 1442, 1443, 1444, 1042, 1045, 1046, 1393, 1534, 1600, 2000, 1460}
            if required_flags - flags_observed:
                issues.append(f"Missing best-route gameplay gates: {sorted(required_flags - flags_observed)}")
            if any(834 in state.get("flags", []) for state in states):
                issues.append("Best route took Booster v0.8, which suppresses the original Tow Rope")
            if any(851 in state.get("flags", []) for state in states):
                issues.append("Best route reached the Waterway death trigger before recovering Curly in the Cabin")
    elif ending == "bad":
        terminal = [state for state in states if state["stage"]["id"] == 53 and 960 in state.get("flags", []) and state.get("song") == 26 and re.fullmatch(r"WaitTicks\(401, \d+, 9999\)", state.get("script", ""))]
        if not terminal:
            issues.append("Bad ending has not reached the native event401 final narrative wait with song26")
    else:
        raise ValueError(f"Unknown ending {ending}")
    expected_npcs, expected_bosses = set(COMMON_NPC_COMBAT), set(COMMON_BOSS_COMBAT)
    if ending in {"normal", "best"}:
        expected_npcs |= FINAL_NPC_COMBAT
        expected_bosses |= FINAL_BOSS_COMBAT
    if ending == "best":
        expected_npcs |= BEST_NPC_COMBAT
        expected_bosses |= BEST_BOSS_COMBAT
    npc_combat = {(witness["stage"], witness["type"]) for witness in combat if witness["kind"] == "npcs"}
    boss_combat = {(witness["stage"], witness["boss_number"]) for witness in combat if witness["kind"] == "bosses" and witness["evidence"] == "life_decreased"}
    if expected_npcs - npc_combat:
        issues.append(f"Missing native actor combat evidence (stage,type): {sorted(expected_npcs - npc_combat)}; record short firing inputs around actual HP decreases/deaths")
    if expected_bosses - boss_combat:
        issues.append(f"Missing native stage-boss HP decreases (stage,boss): {sorted(expected_bosses - boss_combat)}; record short firing inputs during each fight")
    # Exact room edges come from the unmodified authored data, not a test route.
    inventory = inspect_campaign(REPO / "runtime/data", REPO / "runtime/Doukutsu.exe")
    edges = {(transfer["from_stage"], transfer["to_stage"]) for transfer in inventory["transfers"]}
    for index, source, target, retry in stage_transitions:
        if (source, target) not in edges and not retry:
            issues.append(f"Record {index}: stage transition {source}->{target} lacks an original TSC transfer or recorded retry; record more frequently or audit its provenance")
    return {"complete": not issues, "scope": "Live tModLoader normal-control trace with authored milestones, native combat consequences, and full terminal ending; no engine debug commands", "ending": ending, "input_commands": command_count, "stage_sequence": stage_sequence, "native_combat_witnesses": combat, "issues": issues, "last_sha256": previous_hash}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("recording", type=Path)
    parser.add_argument("--ending", choices=("bad", "normal", "best"), required=True)
    parser.add_argument("--json", type=Path)
    args = parser.parse_args()
    result = audit_recording(args.recording, args.ending)
    rendered = json.dumps(result, indent=2) + "\n"
    if args.json:
        args.json.write_text(rendered)
    print(rendered, end="")
    if not result["complete"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
