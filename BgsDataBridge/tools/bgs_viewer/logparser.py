"""解析 receiver.py 的文本日志与 NDJSON 日志为归一化事件列表。"""
import json
import re
from collections import namedtuple

ParseResult = namedtuple("ParseResult", ["events", "skipped"])

#   #NNN  EventType   2026-...+00:00
_HEADER_RE = re.compile(r"^\s+#(\d+)\s+(\w+)\s+(\S+)")


def parse_ndjson(path):
    events = []
    skipped = 0
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                env = json.loads(line)
            except Exception:
                skipped += 1
                continue
            events.append({
                "seq": env.get("seq"),
                "type": env.get("event"),
                "at": env.get("at"),
                "match": env.get("match"),
                "data": env.get("data") or {},
                "source": "log-jsonl",
            })
    return ParseResult(events, skipped)


def parse_text_log(path):
    events = []
    skipped = 0
    cur = None
    json_lines = []
    in_json = False
    with open(path, encoding="utf-8") as f:
        lines = f.readlines()

    def flush(e, jlines, inj):
        nonlocal skipped
        if e is None:
            return
        if inj and jlines:
            try:
                e["data"] = json.loads("\n".join(jlines))
            except Exception:
                skipped += 1
                e["data"] = {}

    def commit():
        """Finalize cur: parse any buffered JSON and append to events."""
        nonlocal cur, json_lines, in_json
        if cur is None:
            return
        flush(cur, json_lines, in_json)
        events.append(cur)
        cur = None
        json_lines = []
        in_json = False

    for line in lines:
        line = line.rstrip("\n")
        m = _HEADER_RE.match(line)
        if m:
            commit()
            cur = {"seq": int(m.group(1)), "type": m.group(2),
                   "at": m.group(3), "match": None, "data": {},
                   "source": "log-text"}
            continue
        if cur is None:
            continue
        if "data (json)" in line:
            in_json = True
            continue
        if "空 payload" in line:
            in_json = False
            cur["data"] = {}
            json_lines = []
            continue
        if in_json:
            if line.startswith("    "):
                json_lines.append(line[4:])
            else:
                flush(cur, json_lines, in_json)
                in_json = False
                json_lines = []
    commit()
    return ParseResult(events, skipped)


def parse(path):
    if path.endswith(".jsonl"):
        return parse_ndjson(path)
    return parse_text_log(path)
