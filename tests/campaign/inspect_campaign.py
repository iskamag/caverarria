#!/usr/bin/env python3
"""Inspect unmodified Cave Story freeware campaign data, without running events.

The formats/command arities follow the checked-in NXEngine-evo and doukutsu-rs
readers. This is an integration inventory, not evidence of a completed game.
"""
from __future__ import annotations

import argparse
from collections import Counter, deque
from dataclasses import asdict, dataclass
import hashlib
import json
from pathlib import Path
import re
import struct


REPO = Path(__file__).resolve().parents[2]
ARITIES = {
    **dict.fromkeys("AE+ CAT CIL CLO CLR CPS CRE CSS END ESC FLA FMU FRE HMC INI KEY LDP MLP MM0 MNA MS2 MS3 MSG NOD PRI RMU SAT SLP SMC SPS STC SVP TUR WAS ZAM".split(), 0),
    **dict.fromkeys("AM- BOA BSL CMU EQ+ EQ- EVE FAC FAI FAO FL+ FL- FOM GIT IT+ IT- LI+ ML+ MP+ MPJ MYB MYD NUM QUA SIL SK+ SK- SOU SSS UNI WAI XX1 YNJ ACH DNA DNP".split(), 1),
    **dict.fromkeys("AM+ AMJ ECJ FLJ FOB FON ITJ MOV NCJ PS+ SKJ SMP UNJ".split(), 2),
    **dict.fromkeys("ANP CMP CNP INP TAM".split(), 3),
    **dict.fromkeys("MNP SNP TRA".split(), 4),
}
BRANCHES = {"AMJ", "ECJ", "FLJ", "ITJ", "NCJ", "SKJ", "UNJ"}
BOSS_NAMES = {0: "none", 1: "Omega", 2: "Balfrog", 3: "Monster X", 4: "Core", 5: "Ironhead", 6: "Sisters", 7: "Undead Core", 8: "Heavy Press", 9: "Ballos"}
RELEVANT_OPS = set("AM+ AM- AMJ ANP BOA BSL CMP CNP CRE DNA DNP ECJ EQ+ EQ- FL+ FL- FLJ HMC INP IT+ IT- ITJ KEY MOV MYB MYD NCJ PRI PS+ SNP TAM TRA UNI UNJ XX1 YNJ".split())


@dataclass(frozen=True)
class Command:
    opcode: str
    args: tuple[int, ...]
    offset: int


@dataclass
class Event:
    number: int
    commands: list[Command]
    text: str
    warnings: list[str]


@dataclass(frozen=True)
class Stage:
    id: int
    map: str
    name: str
    tileset: str
    background: str
    background_type: int
    npc1: str
    npc2: str
    boss: int


def decrypt_tsc(raw: bytes) -> bytes:
    if not raw:
        raise ValueError("Empty TSC")
    middle = len(raw) // 2
    # The midpoint itself is plaintext. Rust handles midpoint NUL as key 7;
    # the original English data has nonzero midpoint keys.
    key = raw[middle] or 7
    return bytes(value if i == middle else (value - key) & 255 for i, value in enumerate(raw))


def parse_tsc(raw: bytes, source: str = "script") -> dict[int, Event]:
    # Latin-1 preserves byte positions and Rust's deliberately permissive
    # four-byte numeric reader. Decode dialogue separately below.
    text = decrypt_tsc(raw).decode("latin-1")
    headers = list(re.finditer(r"(?m)^#(\d{4})[^\n]*\n?", text))
    if not headers:
        raise ValueError(f"{source}: No TSC events after decryption")
    events = {}
    for index, header in enumerate(headers):
        number = int(header.group(1))
        if number in events:
            # Both upstream engines preserve the first definition in nonstrict
            # mode. CentW.tsc contains a duplicate in the original freeware.
            events[number].warnings.append("Duplicate event ignored (original engine preserves first definition)")
            continue
        body = text[header.end():headers[index + 1].start() if index + 1 < len(headers) else len(text)]
        commands = []
        cursor = 0
        strings = []
        warnings = []
        while cursor < len(body):
            start = body.find("<", cursor)
            if start == -1:
                strings.append(body[cursor:])
                break
            strings.append(body[cursor:start])
            opcode = body[start + 1:start + 4]
            if opcode not in ARITIES:
                raise ValueError(f"{source}#{number:04}: Unknown opcode {opcode!r}")
            cursor = start + 4
            args = []
            for arg in range(ARITIES[opcode]):
                if arg:
                    if cursor >= len(body):
                        raise ValueError(f"{source}#{number:04}: Truncated {opcode} separator")
                    if body[cursor] != ":":
                        warnings.append(f"{opcode} non-colon separator at {cursor}")
                    cursor += 1
                value = body[cursor:cursor + 4]
                if len(value) != 4:
                    raise ValueError(f"{source}#{number:04}: Truncated {opcode} operand {value!r}")
                if not value.isdecimal():
                    warnings.append(f"{opcode} non-numeric operand {value!r} at {cursor}; interpreted like doukutsu-rs")
                args.append(sum(((ord(char) - 48) & 255) * place for char, place in zip(value, (1000, 100, 10, 1))))
                cursor += 4
            commands.append(Command(opcode, tuple(args), header.end() + start))
        dialogue = "".join(strings).replace("\r", "").strip().encode("latin-1").decode("cp932", errors="replace")
        events[number] = Event(number, commands, dialogue, warnings)
    return events


def _string(raw: bytes) -> str:
    return raw.split(b"\0", 1)[0].decode("cp932")


def resolve_original_path(path: Path) -> Path:
    if path.exists():
        return path
    matches = [candidate for candidate in path.parent.iterdir() if candidate.name.casefold() == path.name.casefold()]
    if len(matches) != 1:
        raise ValueError(f"Cannot uniquely resolve original asset {path}")
    return matches[0]


def _exe_stage_section(exe: bytes) -> bytes:
    """Locate gTMT through the same instruction signature used by doukutsu-rs."""
    if exe[:2] != b"MZ":
        raise ValueError("Not a PE executable")
    pe = struct.unpack_from("<I", exe, 0x3C)[0]
    if exe[pe:pe + 4] != b"PE\0\0":
        raise ValueError("Invalid PE header")
    sections, optional_size = struct.unpack_from("<H12xH", exe, pe + 6)
    optional = pe + 24
    if struct.unpack_from("<H", exe, optional)[0] != 0x10B:
        raise ValueError("Expected 32-bit freeware executable")
    image_base = struct.unpack_from("<I", exe, optional + 28)[0]
    records = []
    for i in range(sections):
        offset = optional + optional_size + 40 * i
        name = _string(exe[offset:offset + 8])
        virtual_size, virtual_address, size, file_offset = struct.unpack_from("<IIII", exe, offset + 8)
        records.append((name, virtual_address, max(virtual_size, size), file_offset, size))
    signature = bytes.fromhex("83 c4 08 8b 45 08 69 c0 c8 00 00 00 05")
    text_section = next(record for record in records if record[0] == ".text")
    base, length = text_section[3:5]
    location = exe.find(signature, base, base + length)
    if location < 0:
        raise ValueError("Cannot locate authoritative freeware stage table")
    address = struct.unpack_from("<I", exe, location + len(signature))[0] - image_base
    section = next(record for record in records if record[1] <= address < record[1] + record[2])
    start = section[3] + address - section[1]
    return exe[start:start + 95 * 200]


def load_stages(data: Path, exe: Path) -> list[Stage]:
    section_path = data / "stage.sect"
    section = section_path.read_bytes() if section_path.exists() else _exe_stage_section(exe.read_bytes())
    if not section or len(section) % 200:
        raise ValueError("Invalid freeware stage table length")
    stages = []
    for stage_id in range(len(section) // 200):
        row = section[stage_id * 200:(stage_id + 1) * 200]
        stages.append(Stage(stage_id, _string(row[32:64]), _string(row[165:197]), _string(row[:32]), _string(row[68:100]), struct.unpack_from("<I", row, 64)[0], _string(row[100:132]), _string(row[132:164]), row[164]))
    if len(stages) != 95 or stages[13].map != "Start":
        raise ValueError("Expected original 95-stage freeware campaign with Start stage 13")
    return stages


def load_npc_table(path: Path) -> list[dict]:
    raw = resolve_original_path(path).read_bytes()
    if not raw or len(raw) % 24:
        raise ValueError(f"{path}: Invalid NPC table length")
    count = len(raw) // 24
    records = [{} for _ in range(count)]
    cursor = 0
    for field, fmt in (("flags", "H"), ("life", "H"), ("spritesheet", "B"), ("death_sound", "B"), ("hurt_sound", "B"), ("size", "B"), ("experience", "I"), ("damage", "I"), ("hit_bounds", "4B"), ("display_bounds", "4B")):
        size = struct.calcsize("<" + fmt)
        for record in records:
            value = struct.unpack_from("<" + fmt, raw, cursor)
            record[field] = value[0] if len(value) == 1 else list(value)
            cursor += size
    return records


def load_npcs(path: Path, table: list[dict] | None = None) -> list[dict]:
    raw = resolve_original_path(path).read_bytes()
    if raw[:3] != b"PXE" or raw[3] not in (0, 0x10):
        raise ValueError(f"{path}: Invalid PXE header")
    count = struct.unpack_from("<I", raw, 4)[0]
    stride = 13 if raw[3] == 0x10 else 12
    if len(raw) != 8 + count * stride:
        raise ValueError(f"{path}: Invalid PXE length")
    npcs = []
    for i in range(count):
        x, y, flag, event, npc_type, flags = struct.unpack_from("<hhHHHH", raw, 8 + i * stride)
        raw_flags = flags
        if table is not None:
            if npc_type >= len(table):
                raise ValueError(f"{path}: Invalid NPC type {npc_type}")
            flags |= table[npc_type]["flags"]
        triggers = []
        for bit, label in ((8, "touch"), (9, "death"), (13, "interact")):
            if flags & (1 << bit):
                triggers.append(label)
        npcs.append({"index": i, "x": x, "y": y, "flag": flag, "event": event, "type": npc_type, "flags": flags, "pxe_flags": raw_flags, "triggers": triggers, "appear_when_flag_set": bool(flags & 0x800), "hide_unless_flag_set": bool(flags & 0x4000)})
    return npcs


def load_map(path: Path, attributes_path: Path) -> dict:
    raw = resolve_original_path(path).read_bytes()
    if raw[:4] != b"PXM\x10":
        raise ValueError(f"{path}: Invalid PXM header")
    width, height = struct.unpack_from("<HH", raw, 4)
    if len(raw) != 8 + width * height:
        raise ValueError(f"{path}: Invalid PXM length")
    attributes = resolve_original_path(attributes_path).read_bytes()
    if len(attributes) < 256:
        attributes += bytes(256 - len(attributes))
    used = Counter(attributes[value] for value in raw[8:])
    return {"width": width, "height": height, "attributes": {f"0x{k:02x}": v for k, v in sorted(used.items())}}


def inspect_campaign(data: Path, exe: Path) -> dict:
    stages = load_stages(data, exe)
    npc_table = load_npc_table(data / "npc.tbl")
    scripts = {str(path.relative_to(data)).lower(): parse_tsc(path.read_bytes(), str(path)) for path in sorted(data.rglob("*.tsc")) if path.name != "Credit.tsc"}
    head = scripts["head.tsc"]
    stage_reports = []
    transfers, endings, issues, unresolved = [], [], [], []
    opcodes = Counter()
    for stage in stages:
        page_name = f"Stage/{stage.map}.tsc".lower()
        if page_name not in scripts:
            issues.append(f"Stage {stage.id} missing script {page_name}")
            continue
        page = scripts[page_name]
        npcs = load_npcs(data / "Stage" / f"{stage.map}.pxe", npc_table)
        map_report = load_map(data / "Stage" / f"{stage.map}.pxm", data / "Stage" / f"{stage.tileset}.pxa")
        for npc in npcs:
            if npc["triggers"] and npc["event"] not in page and npc["event"] not in head:
                unresolved.append(f"Stage {stage.id}/{stage.map} NPC {npc['index']} triggers undefined event {npc['event']}")
        event_reports = []
        for event in page.values():
            commands = []
            after_terminal = False
            for command in event.commands:
                opcodes[command.opcode] += 1
                if command.opcode in RELEVANT_OPS:
                    commands.append(asdict(command))
                if command.opcode in BRANCHES | {"EVE", "YNJ", "MPJ"}:
                    target = command.args[-1]
                    if target not in page and target not in head:
                        unresolved.append(f"Stage {stage.id}/{stage.map} event {event.number} {command.opcode} targets undefined event {target}")
                if command.opcode == "TRA":
                    target, entry, x, y = command.args
                    transition = {"from_stage": stage.id, "from_map": stage.map, "from_event": event.number, "to_stage": target, "entry_event": entry, "x": x, "y": y}
                    transfers.append(transition)
                    if target >= len(stages):
                        issues.append(f"Stage {stage.id}/{stage.map} event {event.number} transfers to undefined stage {target}")
                    else:
                        target_page = scripts[f"Stage/{stages[target].map}.tsc".lower()]
                        if entry not in target_page and entry not in head:
                            issues.append(f"Stage {stage.id}/{stage.map} event {event.number} transfers to stage {target} undefined entry {entry}")
                if command.opcode in {"CRE", "XX1"}:
                    endings.append({"stage": stage.id, "map": stage.map, "event": event.number, "opcode": command.opcode, "args": list(command.args), "after_terminal": after_terminal, "text": event.text})
                if command.opcode in {"END", "ESC", "EVE", "TRA"}:
                    after_terminal = True
            if commands:
                event_reports.append({"number": event.number, "commands": commands, "text": event.text})
        stage_reports.append({**asdict(stage), "boss_name": BOSS_NAMES.get(stage.boss, "unknown"), "map_data": map_report, "npcs": npcs, "events": event_reports})
    visited, queue = {13}, deque([13])
    edges = {}
    for transfer in transfers:
        edges.setdefault(transfer["from_stage"], set()).add(transfer["to_stage"])
    while queue:
        for target in edges.get(queue.popleft(), ()):
            if target not in visited:
                visited.add(target)
                queue.append(target)
    warnings = [f"{page}#{number:04}: {warning}" for page, events in scripts.items() for number, event in events.items() for warning in event.warnings]
    return {"scope": "Static campaign inventory; does not prove movement, combat, progression or end-to-end completion", "data": str(data.resolve()), "stage_count": len(stages), "script_count": len(scripts), "event_count": sum(map(len, scripts.values())), "npc_types": npc_table, "command_counts": dict(sorted(opcodes.items())), "transfers": transfers, "ending_commands": endings, "statically_reachable_from_start": sorted(visited), "issues": sorted(set(issues)), "original_unresolved_references": sorted(set(unresolved)), "warnings": warnings, "stages": stage_reports, "source_sha256": {str(path.relative_to(data)): hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(data.rglob("*")) if path.is_file() and (path.suffix.lower() in {".tsc", ".pxe", ".pxm", ".pxa"} or path.name.lower() == "npc.tbl")}}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--data", type=Path, default=REPO / "runtime/data")
    parser.add_argument("--exe", type=Path, default=REPO / "runtime/Doukutsu.exe")
    parser.add_argument("--json", type=Path, help="Write complete machine-readable inventory")
    args = parser.parse_args()
    report = inspect_campaign(args.data, args.exe)
    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps({key: report[key] for key in ("scope", "stage_count", "script_count", "event_count", "ending_commands", "issues", "warnings")}, ensure_ascii=False, indent=2))
    if report["issues"]:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
