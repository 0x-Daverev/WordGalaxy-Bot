#!/usr/bin/env python3
"""Protocol catalog and coverage of PacketBot (see docs/PROTOCOL.md).

Crosses three sources:
  1. the client's AS3 export (WordGalaxy_main.swf repo, folder "scripts"):
     every command class (ID, fields) and, for those the server sends, the
     client's handler from its dispatch table;
  2. this repo: packets PacketBot reads (PacketManager) and writes
     (Commands/Write), and the text commands (4224) it looks for;
  3. telemetry files written by the bot (PacketTelemetry): how often the
     server really sends each packet the bot ignores, and on which maps.

Writes docs/protocol/catalog.json and docs/protocol/COVERAGE.md.

    python3 -I tools/protocol_catalog.py --client ../wordgalaxy_main.swf/scripts \\
        [--telemetry ~/.local/share/WordGalaxyPacketBot/telemetry] [--out docs/protocol]

Standard library only. The client export is read as text; nothing is run.
"""
from __future__ import annotations

import argparse
import glob
import json
import os
import re
import sys
from collections import defaultdict

COMMAND_PACKAGE = "§_-G4V§"
CLASS_RE = re.compile(r"public (?:final )?class (\S+)(?: extends \S+)? implements [^{\n]*\bICommand\b")
ID_RE = re.compile(r"public static const ID:int = (-?\d+);")
FIELD_RE = re.compile(r"^\s*public var (\S+?):([\w.<>§_-]+)", re.M)
# Client dispatch table: handlerTable[Command.ID] = Handler;
DISPATCH_RE = re.compile(r"\[(\S+?)\.ID\] = (\S+?);")
IMPORT_RE = re.compile(r"^\s*import ([\w.§_-]+);", re.M)
STRING_RE = re.compile(r'"([^"\\\n]{3,48})"')


def obfuscated(name: str) -> bool:
    return "§" in name


def read_text(path: str) -> str:
    with open(path, encoding="utf-8", errors="replace") as f:
        return f.read()


def client_commands(scripts: str) -> dict[int, dict]:
    """Every ICommand class of the client: id -> info."""
    commands: dict[int, dict] = {}
    for path in glob.glob(os.path.join(scripts, "**", "*.as"), recursive=True):
        text = read_text(path)
        cls = CLASS_RE.search(text)
        ident = ID_RE.search(text)
        if not cls or not ident:
            continue
        rel = os.path.relpath(path, scripts).replace(os.sep, "/")
        fields = [{"name": n, "type": t} for n, t in FIELD_RE.findall(text)]
        commands[int(ident.group(1))] = {
            "id": int(ident.group(1)),
            "class": cls.group(1),
            "file": rel,
            "fields": fields,
            "readInts": text.count("readInt()"),
            "readShorts": text.count("readShort()"),
            "readUTFs": text.count("readUTF()"),
            "readDoubles": text.count("readDouble()"),
            "readBooleans": text.count("readBoolean()"),
            "incoming": False,
            "handler": None,
        }
    return commands


_CLASS_FILES: dict[str, str] = {}


def class_files(scripts: str) -> dict[str, str]:
    """Class name -> first file declaring it (built once)."""
    if not _CLASS_FILES:
        for path in glob.glob(os.path.join(scripts, "**", "*.as"), recursive=True):
            for name in re.findall(r"public (?:final )?(?:class|interface) (\S+)", read_text(path)):
                _CLASS_FILES.setdefault(name, path)
    return _CLASS_FILES


def handler_hints(scripts: str, handler: str) -> dict:
    """Readable names (imports, strings) in a handler class: clues to its meaning."""
    path = class_files(scripts).get(handler)
    if path is None:
        return {"class": handler, "file": None, "imports": [], "strings": []}
    text = read_text(path)
    imports = sorted({i.rsplit(".", 1)[-1] for i in IMPORT_RE.findall(text)
                      if not i.startswith("flash.") and not obfuscated(i.rsplit(".", 1)[-1])})
    strings = sorted({s for s in STRING_RE.findall(text) if not obfuscated(s)})[:12]
    return {"class": handler, "file": os.path.relpath(path, scripts).replace(os.sep, "/"),
            "imports": imports[:12], "strings": strings}


def mark_incoming(scripts: str, commands: dict[int, dict]) -> int:
    """Commands registered in the client's dispatch table are sent by the server."""
    by_class = {c["class"]: c for c in commands.values()}
    best: list[tuple[str, str]] = []
    for path in glob.glob(os.path.join(scripts, "**", "*.as"), recursive=True):
        text = read_text(path)
        if COMMAND_PACKAGE not in text:
            continue
        pairs = [(c, h) for c, h in DISPATCH_RE.findall(text) if c in by_class and not h.startswith("new ")]
        if len(pairs) > len(best):
            best = pairs
    cache: dict[str, dict] = {}
    for cls, handler in best:
        cmd = by_class[cls]
        cmd["incoming"] = True
        if handler not in cache:
            cache[handler] = handler_hints(scripts, handler)
        cmd["handler"] = cache[handler]
    return len(best)


def bot_coverage(repo: str) -> tuple[dict[int, str], dict[int, str], set[str]]:
    """(ids read by PacketManager -> class, ids written -> class, text literals in Core)."""
    core = os.path.join(repo, "PacketBot.Core")
    ids: dict[str, int] = {}
    written: dict[int, str] = {}
    for path in glob.glob(os.path.join(core, "**", "*.cs"), recursive=True):
        text = read_text(path)
        # Each "const short ID" belongs to the last class declared before it.
        classes = [(m.start(), m.group(1)) for m in re.finditer(r"\bclass (\w+)", text)]
        for m in re.finditer(r"const short ID = (-?\d+);", text):
            owner = [name for pos, name in classes if pos < m.start()]
            if not owner:
                continue
            ids[owner[-1]] = int(m.group(1))
            if os.sep + "Write" + os.sep in path:
                written[int(m.group(1))] = owner[-1]
    manager = read_text(os.path.join(core, "PacketHandlers", "PacketManager.cs"))
    read: dict[int, str] = {}
    for name in re.findall(r"case (\w+)\.ID:", manager):
        if name in ids:
            read[ids[name]] = name
    for value in re.findall(r"case (-?\d+):", manager):
        read.setdefault(int(value), "PacketManager")
    literals: set[str] = set()
    for path in glob.glob(os.path.join(core, "**", "*.cs"), recursive=True):
        code = re.sub(r"//[^\n]*", "", read_text(path))  # comments are not code
        literals.update(re.findall(r'"([^"\n]{1,80})"', code))
    return read, written, literals


def load_telemetry(paths: list[str]) -> tuple[dict[int, dict], dict[str, int], list[str]]:
    files: list[str] = []
    for p in paths:
        p = os.path.expanduser(p)
        files.extend(sorted(glob.glob(os.path.join(p, "*.json"))) if os.path.isdir(p) else [p])
    packets: dict[int, dict] = {}
    text: dict[str, int] = defaultdict(int)
    for f in files:
        try:
            data = json.loads(read_text(f))
        except (OSError, json.JSONDecodeError):
            print(f"aviso: no se pudo leer {f}", file=sys.stderr)
            continue
        for key, e in (data.get("Packets") or {}).items():
            t = packets.setdefault(int(key), {"Count": 0, "MinBytes": 1 << 30, "MaxBytes": 0,
                                              "Maps": defaultdict(int), "Samples": []})
            t["Count"] += e.get("Count", 0)
            t["MinBytes"] = min(t["MinBytes"], e.get("MinBytes", t["MinBytes"]))
            t["MaxBytes"] = max(t["MaxBytes"], e.get("MaxBytes", 0))
            for m, n in (e.get("Maps") or {}).items():
                t["Maps"][int(m)] += n
            for s in e.get("Samples") or []:
                if len(t["Samples"]) < 3 and s not in t["Samples"]:
                    t["Samples"].append(s)
        for k, n in (data.get("TextKeys") or {}).items():
            text[k] += n
    return packets, dict(text), files


def text_handled(key: str, literals: set[str]) -> bool:
    """Whether some string in Core mentions this text command key."""
    parts = key.split("|")
    candidates = {key, "0|" + key, "|" + key + "|", parts[0]} if len(parts) == 1 else {key, "0|" + key, "|" + key}
    return any(c in lit for lit in literals for c in candidates if len(c) >= 2)


def hint(cmd: dict) -> str:
    h = cmd.get("handler") or {}
    words = (h.get("imports") or []) + (h.get("strings") or [])
    return ", ".join(words[:4])


def fields_summary(cmd: dict) -> str:
    counts: dict[str, int] = defaultdict(int)
    for f in cmd["fields"]:
        counts[f["type"]] += 1
    return " ".join(f"{n}×{t}" for t, n in sorted(counts.items())) or "-"


def write_outputs(out: str, commands, read, written, packets, text, files, literals) -> str:
    os.makedirs(out, exist_ok=True)
    incoming = [c for c in commands.values() if c["incoming"]]
    for c in commands.values():
        c["botReads"] = read.get(c["id"])
        c["botWrites"] = written.get(c["id"])
        seen = packets.get(c["id"])
        c["seen"] = None if seen is None else {
            "count": seen["Count"], "minBytes": seen["MinBytes"], "maxBytes": seen["MaxBytes"],
            "maps": dict(seen["Maps"])}
    with open(os.path.join(out, "catalog.json"), "w", encoding="utf-8") as f:
        json.dump(sorted(commands.values(), key=lambda c: c["id"]), f, ensure_ascii=False, indent=1)

    handled_in = [c for c in incoming if c["botReads"]]
    missing = [c for c in incoming if not c["botReads"]]
    # Seen in real sessions first (most frequent), then by id.
    missing.sort(key=lambda c: (-(packets.get(c["id"], {}).get("Count", 0)), c["id"]))
    unknown_ids = sorted(set(packets) - set(commands))

    lines = ["# Cobertura del protocolo", "",
             "Generado por `tools/protocol_catalog.py`; no editar a mano. Ver [PROTOCOL.md](../PROTOCOL.md).", "",
             "| | Cliente | PacketBot |", "|---|---|---|",
             f"| Paquetes que manda el servidor | {len(incoming)} | {len(handled_in)} leídos |",
             f"| Paquetes que manda el cliente | {len(commands) - len(incoming)} | {len(written)} escritos |",
             f"| Ficheros de registro usados | | {len(files)} |", ""]
    if not files:
        lines += ["> Sin registros: la prioridad es solo por ID. Juega una sesión con el bot y vuelve a "
                  "generar con `--telemetry` para ordenar por lo que el servidor manda de verdad.", ""]
    lines += ["## Pendientes: el servidor los manda y PacketBot no los lee", "",
              "Ordenados por veces vistos en los registros.", "",
              "| ID | Visto | Bytes | Mapas | Campos | Pistas del manejador | Clase |",
              "|---|---|---|---|---|---|---|"]
    for c in missing:
        s = c["seen"]
        maps = ", ".join(str(m) for m, _ in sorted((s or {}).get("maps", {}).items(), key=lambda kv: -kv[1])[:4])
        lines.append(f"| {c['id']} | {s['count'] if s else ''} | "
                     f"{(str(s['minBytes']) + '-' + str(s['maxBytes'])) if s else ''} | {maps} | "
                     f"{fields_summary(c)} | {hint(c)} | `{c['file']}` |")
    lines += ["", "## Leídos por PacketBot", "", "| ID | Clase del bot | Visto | Clase del cliente |", "|---|---|---|---|"]
    for c in sorted(handled_in, key=lambda c: c["id"]):
        lines.append(f"| {c['id']} | `{c['botReads']}` | | `{c['file']}` |")
    lines += ["", "## Escritos por PacketBot", "", "| ID | Clase del bot | Clase del cliente |", "|---|---|---|"]
    for i, cls in sorted(written.items()):
        lines.append(f"| {i} | `{cls}` | `{commands[i]['file'] if i in commands else 'no está en el cliente'}` |")
    if text:
        lines += ["", "## Comandos de texto (4224)", "", "| Clave | Visto | ¿Lo busca PacketBot? |", "|---|---|---|"]
        for k, n in sorted(text.items(), key=lambda kv: -kv[1]):
            lines.append(f"| `{k}` | {n} | {'sí' if text_handled(k, literals) else 'no'} |")
    if unknown_ids:
        lines += ["", "## IDs vistos que no están en el cliente", "",
                  ", ".join(str(i) for i in unknown_ids), ""]
    report = "\n".join(lines) + "\n"
    with open(os.path.join(out, "COVERAGE.md"), "w", encoding="utf-8") as f:
        f.write(report)
    return (f"{len(incoming)} paquetes del servidor, {len(handled_in)} leídos por el bot, "
            f"{len(missing)} pendientes; {len(files)} registros.")


def main(argv: list[str] | None = None) -> int:
    here = os.path.dirname(os.path.abspath(__file__))
    parser = argparse.ArgumentParser(description="Catálogo y cobertura del protocolo de PacketBot.")
    parser.add_argument("--client", required=True, help="carpeta 'scripts' de la exportación del cliente")
    parser.add_argument("--repo", default=os.path.dirname(here), help="raíz de este repositorio")
    parser.add_argument("--telemetry", action="append", default=[],
                        help="carpeta o fichero de registro del bot (se puede repetir)")
    parser.add_argument("--out", default=None, help="carpeta de salida (por defecto docs/protocol)")
    args = parser.parse_args(argv)
    if not os.path.isdir(args.client):
        parser.error("no existe la carpeta del cliente: " + args.client)
    commands = client_commands(args.client)
    if not commands:
        parser.error("no hay comandos en " + args.client + " (¿es la carpeta 'scripts'?)")
    mark_incoming(args.client, commands)
    read, written, literals = bot_coverage(args.repo)
    packets, text, files = load_telemetry(args.telemetry)
    out = args.out or os.path.join(args.repo, "docs", "protocol")
    print(write_outputs(out, commands, read, written, packets, text, files, literals))
    return 0


if __name__ == "__main__":
    sys.exit(main())
