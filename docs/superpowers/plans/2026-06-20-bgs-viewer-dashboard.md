# BgsViewer 对局可视化仪表盘 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 写一个 Flask 本地 Web 仪表盘脚本，把 BgsDataBridge 插件推送的事件（实时 webhook 或日志回放）可视化为"当前状态 + 回合时间轴 + 进度曲线"，展示契约内全部字段。

**Architecture:** 三种数据源（webhook POST、浏览器轮询插件 `/state`、`.log`/`.jsonl` 文件回放）汇入归一化内存 `EventStore`；纯逻辑 `stateview.reconstruct_state` 对事件列表做 fold 重建任意位置的快照视图，前端只做渲染；Flask 提供 `/api/view`、`/api/events`、`/api/progression` 三个 JSON 端点，前端 1s 短轮询 `/api/events` 增量更新。

**Tech Stack:** Python 3.8+ 标准库 + Flask（`pip install flask`）、stdlib `unittest` 测试、Chart.js（CDN）、原生 HTML/CSS/JS。

## Global Constraints

- **位置**：所有文件放在 `BgsDataBridge/tools/bgs_viewer/`。
- **Python**：3.8+；遵循 `BgsDataBridge/tools/receiver.py` 的约定（`sys.stdout.reconfigure(encoding="utf-8")`、`argparse` CLI、默认绑 `127.0.0.1`）。
- **依赖**：仅 Flask 一个第三方包（`pip install flask`）；图表用 Chart.js CDN（查看页面需联网）；**测试用 stdlib `unittest`，不引入 pytest**。
- **运行测试**：`cd BgsDataBridge/tools/bgs_viewer && python -m unittest`（测试模块与源码同目录平铺，`from eventstore import EventStore` 直接可用）。
- **运行程序**：`python BgsDataBridge/tools/bgs_viewer/bgs_viewer.py [--replay <file>]`。
- **数据契约**（来自插件，勿改）：事件信封 `{schema:"bgs-event/v1", seq:int, event:str, at:str, match:obj, data:obj}`；完整快照 `data` 含 `{schema:"bgs-state/v1", match, availableRaces, player{name,tier,hero{health,armor?,cardId,name?},heroPower{cardId,name?,text?},trinkets[{slot,cardId,name?,text?}],questReward?{cardId,name?,text?,progress?,total?},board[{cardId,name?,attack?,health?,keywords?}]},shop{available,tier,offers[]},lastOpponent{turn,hero?,board[]},lobby{players[{name,heroCardId,accountId}]}}`；`ShopChanged.data = {shop,turn,phase}`；`HeroPick`/`TrinketPick.data = {}`。
- **死字段不展示**（插件从不填充）：`dbfId`、`minion.golden`、`shop.frozen`、`zonePosition`、`cost`、`match.anomaly`、`match.gameUuid`。
- **`/state`**：插件 `GET http://localhost:5273/state?text=1` 返回当前完整快照，已开 `CORS *`（浏览器直连，无需后端代理）。
- **提交**：每个 Task 末尾提交一次，scope 用 `bgs-viewer`（如 `feat(bgs-viewer): ...`）。

## File Structure

```
BgsDataBridge/tools/bgs_viewer/
  eventstore.py          # EventStore：归一化事件的内存存储（线程安全）
  logparser.py           # parse_ndjson / parse_text_log / parse → [event] + skipped 计数
  stateview.py           # reconstruct_state(events) / compute_progression(events)（fold）
  bgs_viewer.py          # CLI + Flask app + 路由（IO 层，薄）
  test_eventstore.py     # unittest
  test_logparser.py      # unittest
  test_stateview.py      # unittest
  templates/dashboard.html
  static/dashboard.css
  static/dashboard.js
  README.md
```

**边界**：`eventstore`/`logparser`/`stateview` 为纯逻辑（无 IO，可单测）；`bgs_viewer.py` 为 IO 层（Flask 路由 + 文件读 + CLI），靠运行时验证。归一化事件 dict 形状在 `eventstore`/`logparser`/`bgs_viewer` 三处入口统一为 `{"seq","type","at","match","data","source"}`。

---

### Task 1: EventStore（纯逻辑）

**Files:**
- Create: `BgsDataBridge/tools/bgs_viewer/eventstore.py`
- Test: `BgsDataBridge/tools/bgs_viewer/test_eventstore.py`

**Interfaces:**
- Produces: `EventStore` 类，方法 `append(event: dict) -> bool`、`all() -> list[dict]`、`since(seq: int|None) -> list[dict]`、`last_seq() -> int|None`。归一化事件 dict 形状：`{"seq":int|None,"type":str,"at":str|None,"match":dict|None,"data":dict,"source":str}`。

- [ ] **Step 1: 写失败测试**

Create `test_eventstore.py`:

```python
import threading
import unittest
from eventstore import EventStore


def ev(seq, **kw):
    return {"seq": seq, "type": kw.get("type", "ShopChanged"),
            "at": None, "match": None, "data": {}, "source": "test"}


class TestEventStore(unittest.TestCase):
    def test_append_and_all_sorted_by_seq(self):
        s = EventStore()
        s.append(ev(3)); s.append(ev(1)); s.append(ev(2))
        self.assertEqual([e["seq"] for e in s.all()], [1, 2, 3])

    def test_dedup_by_seq(self):
        s = EventStore()
        self.assertTrue(s.append(ev(2)))
        self.assertFalse(s.append(ev(2)))
        self.assertEqual(len(s.all()), 1)

    def test_since_returns_strictly_after(self):
        s = EventStore()
        for i in (1, 2, 3, 4):
            s.append(ev(i))
        self.assertEqual([e["seq"] for e in s.since(2)], [3, 4])

    def test_since_none_returns_all(self):
        s = EventStore()
        s.append(ev(1)); s.append(ev(2))
        self.assertEqual(len(s.since(None)), 2)

    def test_last_seq(self):
        s = EventStore()
        self.assertIsNone(s.last_seq())
        s.append(ev(5)); s.append(ev(7))
        self.assertEqual(s.last_seq(), 7)

    def test_concurrent_append_no_loss(self):
        s = EventStore()
        def worker(start):
            for i in range(start, start + 100):
                s.append(ev(i))
        threads = [threading.Thread(target=worker, args=(b * 100,))
                   for b in range(4)]
        for t in threads: t.start()
        for t in threads: t.join()
        self.assertEqual(len(s.all()), 400)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd BgsDataBridge/tools/bgs_viewer && python -m unittest test_eventstore -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'eventstore'`

- [ ] **Step 3: 写最小实现**

Create `eventstore.py`:

```python
"""归一化事件的内存存储，线程安全，按 seq 去重排序。"""
import threading


class EventStore:
    def __init__(self):
        self._lock = threading.Lock()
        self._events = []
        self._seqs = set()

    @staticmethod
    def _key(e):
        seq = e.get("seq")
        return seq if seq is not None else float("inf")

    def append(self, event):
        """按 seq 去重；返回是否新增。seq 为 None 时不去重。"""
        with self._lock:
            seq = event.get("seq")
            if seq is not None and seq in self._seqs:
                return False
            if seq is not None:
                self._seqs.add(seq)
            self._events.append(event)
            self._events.sort(key=self._key)
            return True

    def all(self):
        with self._lock:
            return list(self._events)

    def since(self, seq):
        with self._lock:
            if seq is None:
                return list(self._events)
            return [e for e in self._events if (e.get("seq") or 0) > seq]

    def last_seq(self):
        with self._lock:
            return self._events[-1].get("seq") if self._events else None
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd BgsDataBridge/tools/bgs_viewer && python -m unittest test_eventstore -v`
Expected: PASS（6 个测试）

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/tools/bgs_viewer/eventstore.py BgsDataBridge/tools/bgs_viewer/test_eventstore.py
git commit -m "feat(bgs-viewer): add EventStore with seq dedup/sort + thread safety"
```

---

### Task 2: logparser（纯逻辑）

**Files:**
- Create: `BgsDataBridge/tools/bgs_viewer/logparser.py`
- Test: `BgsDataBridge/tools/bgs_viewer/test_logparser.py`

**Interfaces:**
- Consumes: 无。
- Produces: `ParseResult`（`collections.namedtuple`，字段 `events: list[dict]`、`skipped: int`）；函数 `parse_ndjson(path: str) -> ParseResult`、`parse_text_log(path: str) -> ParseResult`、`parse(path: str) -> ParseResult`。返回的归一化事件 dict 与 Task 1 同形状，`source` 分别为 `"log-jsonl"` / `"log-text"`。
- `parse` 分发规则：`.jsonl` → NDJSON；`.log` 或其它 → 文本。

- [ ] **Step 1: 写失败测试**

Create `test_logparser.py`:

```python
import json
import os
import tempfile
import unittest
from logparser import parse, parse_ndjson, parse_text_log


def write_tmp(text, suffix):
    fd, path = tempfile.mkstemp(suffix=suffix, text=True)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(text)
    return path


class TestNdjson(unittest.TestCase):
    def test_parses_envelopes_and_counts_bad(self):
        a = {"schema": "bgs-event/v1", "seq": 1, "event": "MatchStart",
             "at": "2026-06-20T10:00:00+00:00", "match": {"turn": 0},
             "data": {"schema": "bgs-state/v1", "player": {"name": "x"}}}
        b = {"schema": "bgs-event/v1", "seq": 2, "event": "ShopChanged",
             "at": "2026-06-20T10:00:01+00:00", "data": {"shop": {"tier": 1}}}
        text = json.dumps(a, ensure_ascii=False) + "\n" + \
               "NOT JSON\n" + json.dumps(b, ensure_ascii=False) + "\n\n"
        path = write_tmp(text, ".jsonl")
        res = parse_ndjson(path)
        self.assertEqual([e["seq"] for e in res.events], [1, 2])
        self.assertEqual(res.skipped, 1)
        self.assertEqual(res.events[0]["type"], "MatchStart")
        self.assertEqual(res.events[1]["data"], {"shop": {"tier": 1}})
        self.assertEqual(res.events[0]["source"], "log-jsonl")


TEXT_LOG = """
────────────────────────────────────────────────────────────────
10:00:00.000  ← /
  #1  MatchStart   2026-06-20T10:00:00+00:00
  BattlegroundsSolo · turn 0
  ── data (json) ──
    {
      "schema": "bgs-state/v1",
      "match": {"turn": 0},
      "player": {"name": "iaun"}
    }

────────────────────────────────────────────────────────────────
10:00:01.000  ← /
  #2  ShopChanged   2026-06-20T10:00:01+00:00
  BattlegroundsSolo · turn 1
  ── shop (tier 1) ──
    [1] Risen Rider  2/1
  ── data (json) ──
    {
      "shop": {"tier": 1, "offers": []},
      "turn": 1,
      "phase": "Shop"
    }

────────────────────────────────────────────────────────────────
10:00:02.000  ← /
  #3  HeroPick   2026-06-20T10:00:02+00:00
  BattlegroundsSolo · turn 0
  data: (空 payload)
"""


class TestTextLog(unittest.TestCase):
    def test_parses_full_shop_and_empty(self):
        path = write_tmp(TEXT_LOG, ".log")
        res = parse_text_log(path)
        self.assertEqual([e["seq"] for e in res.events], [1, 2, 3])
        self.assertEqual(res.events[0]["type"], "MatchStart")
        # full snapshot data extracted
        self.assertEqual(res.events[0]["data"]["schema"], "bgs-state/v1")
        self.assertEqual(res.events[0]["data"]["player"]["name"], "iaun")
        # shop-only data extracted (human shop block ignored)
        self.assertEqual(res.events[1]["data"]["shop"]["tier"], 1)
        self.assertEqual(res.events[1]["data"]["turn"], 1)
        # empty payload -> {}
        self.assertEqual(res.events[2]["data"], {})
        self.assertEqual(res.events[0]["source"], "log-text")

    def test_parse_dispatches_by_extension(self):
        path = write_tmp(TEXT_LOG, ".log")
        self.assertEqual(len(parse(path).events), 3)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd BgsDataBridge/tools/bgs_viewer && python -m unittest test_logparser -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'logparser'`

- [ ] **Step 3: 写最小实现**

Create `logparser.py`:

```python
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

    for line in lines:
        line = line.rstrip("\n")
        m = _HEADER_RE.match(line)
        if m:
            flush(cur, json_lines, in_json)
            cur = {"seq": int(m.group(1)), "type": m.group(2),
                   "at": m.group(3), "match": None, "data": {},
                   "source": "log-text"}
            json_lines = []
            in_json = False
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
    flush(cur, json_lines, in_json)
    return ParseResult(events, skipped)


def parse(path):
    if path.endswith(".jsonl"):
        return parse_ndjson(path)
    return parse_text_log(path)
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd BgsDataBridge/tools/bgs_viewer && python -m unittest test_logparser -v`
Expected: PASS（3 个测试）

- [ ] **Step 5: 用真实日志做回归冒烟**

Run: `cd BgsDataBridge/tools/bgs_viewer && python -c "from logparser import parse; r=parse('../../bgs-events-20260620-181754.log'); print('events', len(r.events), 'skipped', r.skipped); print('types', sorted({e[\"type\"] for e in r.events}))"`
Expected: 输出约 `events 74 skipped 0`，types 含 `MatchStart/MatchEnd/ShopPhaseStart/CombatPhaseStart/ShopChanged/HeroPick`（文件若已被移动则改用仓库内任一 `bgs-events-*.log`）。

- [ ] **Step 6: 提交**

```bash
git add BgsDataBridge/tools/bgs_viewer/logparser.py BgsDataBridge/tools/bgs_viewer/test_logparser.py
git commit -m "feat(bgs-viewer): add .log/.jsonl parser with real-log regression"
```

---

### Task 3: stateview reconstruct_state + compute_progression（纯逻辑）

**Files:**
- Create: `BgsDataBridge/tools/bgs_viewer/stateview.py`
- Test: `BgsDataBridge/tools/bgs_viewer/test_stateview.py`

**Interfaces:**
- Consumes: 归一化事件 dict 列表（Task 1/2 产物）。
- Produces: `reconstruct_state(events: list) -> dict`（对事件做 fold，返回当前快照视图；`{}` 表示无数据）；`compute_progression(events: list) -> list[dict]`（每个 `CombatPhaseStart` 采样一条 `{"turn":int,"heroHp":int|None,"boardAtk":int,"tier":int|None}`）。
- fold 规则：`data` 含 `"player"` 键（完整快照）→ 用 `dict(data)` 整体替换视图；`data` 含 `"shop"` 但无 `"player"`（ShopChanged）→ 仅 `view["shop"] = data["shop"]`；否则（空）→ 不变。

- [ ] **Step 1: 写失败测试**

Create `test_stateview.py`:

```python
import unittest
from stateview import reconstruct_state, compute_progression


def full(turn, hp=30, board=None, tier=2, shop_tier=None):
    return {"type": "CombatPhaseStart",
            "data": {"schema": "bgs-state/v1",
                     "match": {"turn": turn},
                     "player": {"tier": tier, "hero": {"health": hp},
                                "board": board or []},
                     "shop": ({"tier": shop_tier} if shop_tier is not None else None)}}


def shop(tier):
    return {"type": "ShopChanged", "data": {"shop": {"tier": tier},
            "turn": 9, "phase": "Shop"}}


def empty():
    return {"type": "HeroPick", "data": {}}


class TestReconstruct(unittest.TestCase):
    def test_full_replaces_view(self):
        v = reconstruct_state([full(1, hp=30), full(2, hp=25)])
        self.assertEqual(v["player"]["hero"]["health"], 25)
        self.assertEqual(v["match"]["turn"], 2)

    def test_shop_patches_only_shop(self):
        v = reconstruct_state([full(1, hp=30), shop(5)])
        # board/hero from full snapshot retained
        self.assertEqual(v["player"]["hero"]["health"], 30)
        # shop replaced by ShopChanged
        self.assertEqual(v["shop"]["tier"], 5)

    def test_empty_leaves_view(self):
        v = reconstruct_state([full(1, hp=30), empty()])
        self.assertEqual(v["player"]["hero"]["health"], 30)

    def test_empty_before_any_full(self):
        self.assertEqual(reconstruct_state([empty()]), {})


class TestProgression(unittest.TestCase):
    def test_samples_combat_only(self):
        events = [
            full(1, hp=30, board=[{"attack": 3}], tier=1),
            shop(2),  # not sampled
            full(2, hp=25, board=[{"attack": 3}, {"attack": 4}], tier=2),
            full(3, hp=18, board=[{"attack": 10}], tier=3),
        ]
        prog = compute_progression(events)
        self.assertEqual([p["turn"] for p in prog], [1, 2, 3])
        self.assertEqual(prog[1]["boardAtk"], 7)
        self.assertEqual(prog[2]["heroHp"], 18)
        self.assertEqual(prog[2]["tier"], 3)

    def test_no_combat_returns_empty(self):
        self.assertEqual(compute_progression([shop(1), empty()]), [])


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd BgsDataBridge/tools/bgs_viewer && python -m unittest test_stateview -v`
Expected: FAIL — `ModuleNotFoundError: No module named 'stateview'`

- [ ] **Step 3: 写最小实现**

Create `stateview.py`:

```python
"""对归一化事件列表做 fold：重建快照视图 + 计算回合进度。"""


def _fold_events(events):
    """yield (event, view_after_event)；view 为累积的快照视图。"""
    view = {}
    for e in events:
        data = e.get("data") or {}
        if isinstance(data, dict) and "player" in data:
            view = dict(data)
        elif isinstance(data, dict) and "shop" in data and "player" not in data:
            view = dict(view)
            view["shop"] = data.get("shop")
        yield e, view


def reconstruct_state(events):
    view = {}
    for _, v in _fold_events(events):
        view = v
    return view


def compute_progression(events):
    out = []
    for e, view in _fold_events(events):
        if e.get("type") != "CombatPhaseStart" or not view:
            continue
        player = view.get("player") or {}
        hero = player.get("hero") or {}
        board = player.get("board") or []
        turn = (view.get("match") or {}).get("turn")
        if turn is None:
            turn = (e.get("match") or {}).get("turn")
        out.append({
            "turn": turn,
            "heroHp": hero.get("health"),
            "boardAtk": sum((m.get("attack") or 0) for m in board),
            "tier": player.get("tier"),
        })
    return out
```

- [ ] **Step 4: 跑测试确认通过**

Run: `cd BgsDataBridge/tools/bgs_viewer && python -m unittest test_stateview -v`
Expected: PASS（6 个测试）

- [ ] **Step 5: 跑全部纯逻辑测试**

Run: `cd BgsDataBridge/tools/bgs_viewer && python -m unittest -v`
Expected: PASS（eventstore + logparser + stateview 共 15 个测试）

- [ ] **Step 6: 提交**

```bash
git add BgsDataBridge/tools/bgs_viewer/stateview.py BgsDataBridge/tools/bgs_viewer/test_stateview.py
git commit -m "feat(bgs-viewer): add reconstruct_state fold + compute_progression"
```

---

### Task 4: Flask app + CLI + API 路由（IO 层）

**Files:**
- Create: `BgsDataBridge/tools/bgs_viewer/bgs_viewer.py`
- Create: `BgsDataBridge/tools/bgs_viewer/templates/dashboard.html`（占位，下个 Task 替换）

**Interfaces:**
- Consumes: `EventStore`（Task 1）、`parse`（Task 2）、`reconstruct_state`/`compute_progression`（Task 3）。
- Produces（HTTP 端点，供 Task 5 前端调用）：
  - `GET /` → `dashboard.html`，注入模板变量 `mode`、`state_url`、`state_poll`。
  - `POST /events` → 信封 JSON 体 → `EventStore.append`（source `"webhook"`）；坏 JSON 回 400。
  - `GET /api/events?since=<int>` → `store.since(since)` 的 JSON。
  - `GET /api/view?n=<int>` → `reconstruct_state(store.all()[:n])`；`n` 缺省/越界=全部。
  - `GET /api/progression` → `compute_progression(store.all())`。
  - `GET /api/mode` → `{"mode","stateUrl","statePoll","lastIndex"}`。
- CLI：`--port`(5000) `--host`(127.0.0.1) `--state-url`(http://localhost:5273) `--no-state-poll` `--replay <file>` `--jsonlog <path>`。

- [ ] **Step 1: 写 Flask 应用**

Create `bgs_viewer.py`:

```python
#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BgsViewer —— 酒馆战棋对局可视化仪表盘（实时 webhook + 日志回放）。"""
import argparse
import json
import os
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
except Exception:
    pass

from flask import Flask, request, jsonify, render_template

from eventstore import EventStore
from logparser import parse
from stateview import reconstruct_state, compute_progression

HERE = os.path.dirname(os.path.abspath(__file__))
app = Flask(__name__, template_folder=os.path.join(HERE, "templates"),
            static_folder=os.path.join(HERE, "static"))
store = EventStore()
CONFIG = {"mode": "live", "stateUrl": "http://localhost:5273",
          "statePoll": True, "jsonlog": None}


def _env_to_event(env, source):
    return {
        "seq": env.get("seq"),
        "type": env.get("event"),
        "at": env.get("at"),
        "match": env.get("match"),
        "data": env.get("data") or {},
        "source": source,
    }


def _write_jsonlog(event):
    path = CONFIG.get("jsonlog")
    if not path:
        return
    try:
        with open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps(event, ensure_ascii=False) + "\n")
    except Exception as e:
        sys.stderr.write(f"[jsonlog 写入失败: {e}]\n")


@app.route("/")
def index():
    return render_template("dashboard.html", mode=CONFIG["mode"],
                           state_url=CONFIG["stateUrl"],
                           state_poll=("true" if CONFIG["statePoll"] else "false"))


@app.route("/events", methods=["POST"])
def post_events():
    raw = request.get_data(cache=True)
    try:
        env = json.loads(raw.decode("utf-8")) if raw else {}
    except Exception:
        return jsonify({"error": "bad json"}), 400
    event = _env_to_event(env, "webhook")
    store.append(event)
    _write_jsonlog(event)
    return jsonify({"ok": True}), 200


@app.route("/api/events")
def api_events():
    since = request.args.get("since", type=int)
    return jsonify(store.since(since))


@app.route("/api/view")
def api_view():
    n = request.args.get("n", type=int)
    events = store.all()
    if n is None or n > len(events):
        n = len(events)
    if n < 0:
        n = 0
    return jsonify(reconstruct_state(events[:n]))


@app.route("/api/progression")
def api_progression():
    return jsonify(compute_progression(store.all()))


@app.route("/api/mode")
def api_mode():
    return jsonify({"mode": CONFIG["mode"], "stateUrl": CONFIG["stateUrl"],
                    "statePoll": CONFIG["statePoll"],
                    "lastIndex": len(store.all())})


def main():
    ap = argparse.ArgumentParser(description="BgsViewer 对局可视化仪表盘")
    ap.add_argument("--port", type=int, default=5000)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--state-url", default="http://localhost:5273")
    ap.add_argument("--no-state-poll", action="store_true")
    ap.add_argument("--replay", default=None, help=".log 或 .jsonl 文件路径")
    ap.add_argument("--jsonlog", default=None, help="实时模式下另写 NDJSON")
    args = ap.parse_args()

    print("BgsViewer 对局可视化仪表盘")
    if args.replay:
        CONFIG["mode"] = "replay"
        CONFIG["statePoll"] = False
        result = parse(args.replay)
        for e in result.events:
            store.append(e)
        print(f"  回放: 载入 {len(result.events)} 事件, 跳过 {result.skipped} 行")
    else:
        CONFIG["stateUrl"] = args.state_url
        CONFIG["statePoll"] = not args.no_state_poll
        CONFIG["jsonlog"] = os.path.abspath(args.jsonlog) if args.jsonlog else None
        print(f"  实时: webhook 目标设为 http://localhost:{args.port}/events")
        print(f"        (与 receiver.py 并存; 插件支持多 webhook URL)")
    if CONFIG["statePoll"]:
        print(f"  /state 轮询: {CONFIG['stateUrl']}/state")
    print(f"  打开: http://localhost:{args.port}/")
    app.run(host=args.host, port=args.port, debug=False)


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: 写占位 dashboard.html**

Create `templates/dashboard.html`:

```html
<!doctype html>
<html lang="zh-CN"><head><meta charset="utf-8">
<title>BgsViewer</title></head>
<body>
<h1>BgsViewer</h1>
<p>mode={{ mode }} · statePoll={{ state_poll }} · stateUrl={{ state_url }}</p>
<pre id="dump">加载中…</pre>
<script>
fetch("/api/mode").then(r=>r.json()).then(m=>{
  fetch("/api/events").then(r=>r.json()).then(ev=>{
    document.getElementById("dump").textContent =
      "mode="+JSON.stringify(m)+"\nevents="+ev.length;
  });
});
</script>
</body></html>
```

- [ ] **Step 3: 安装 Flask**

Run: `pip install flask`
Expected: 安装成功（Flask 2.x/3.x）。

- [ ] **Step 4: 回放模式冒烟（后端 + 路由）**

先在后台起服务（用仓库内任一真实日志）：
Run: `cd BgsDataBridge/tools/bgs_viewer && python bgs_viewer.py --replay ../../bgs-events-20260620-181754.log`
Expected: 打印 `回放: 载入 74 事件, 跳过 0` 后阻塞监听。

另开终端验证端点：
Run: `curl -s http://localhost:5000/api/mode`
Expected: `{"lastIndex":74,"mode":"replay","statePoll":false,...}`

Run: `curl -s "http://localhost:5000/api/view?n=10" | python -c "import sys,json;d=json.load(sys.stdin);print(d.get('match'), (d.get('player') or {}).get('name'))"`
Expected: 打印某个 turn 与玩家名（如 `{'turn': 1} iaun#51100`）。

Run: `curl -s http://localhost:5000/api/progression`
Expected: 一个 JSON 数组，含若干 `{turn,heroHp,boardAtk,tier}`。

浏览器打开 `http://localhost:5000/` → 看到 `mode=replay ... events=74`。停止服务（Ctrl+C）。

- [ ] **Step 5: 实时模式冒烟（webhook + 增量）**

Run: `cd BgsDataBridge/tools/bgs_viewer && python bgs_viewer.py --port 5000`
另开终端模拟插件 POST 一个事件：
Run:
```bash
curl -s -X POST http://localhost:5000/events -H "Content-Type: application/json" -d '{"schema":"bgs-event/v1","seq":1,"event":"MatchStart","at":"2026-06-20T10:00:00+00:00","match":{"turn":0},"data":{"schema":"bgs-state/v1","match":{"turn":0},"player":{"name":"test"}}}'
```
Expected: `{"ok":true}`

Run: `curl -s "http://localhost:5000/api/events?since=0"`
Expected: 返回含 `seq=1` 的 1 条事件数组。停止服务。

- [ ] **Step 6: 提交**

```bash
git add BgsDataBridge/tools/bgs_viewer/bgs_viewer.py BgsDataBridge/tools/bgs_viewer/templates/dashboard.html
git commit -m "feat(bgs-viewer): add Flask app + CLI + API routes (live & replay)"
```

---

### Task 5: 前端仪表盘（HTML/CSS/JS）

**Files:**
- Modify: `BgsDataBridge/tools/bgs_viewer/templates/dashboard.html`（替换占位为完整 UI）
- Create: `BgsDataBridge/tools/bgs_viewer/static/dashboard.css`
- Create: `BgsDataBridge/tools/bgs_viewer/static/dashboard.js`

**Interfaces:**
- Consumes（浏览器）：`GET /api/mode`、`GET /api/events?since=N`、`GET /api/view?n=N`、`GET /api/progression`，以及实时模式下浏览器直连 `{{ state_url }}/state?text=1`。
- 渲染规则：`/api/view` 与 `/state` 返回同一 `BgsSnapshot` 形状，用同一 `renderSnapshot(view)` 渲染。关键字徽章配色与金标推断（`cardId` 以 `_G` 结尾→金标）在前端常量定义。

- [ ] **Step 1: 写 dashboard.html（完整 UI 骨架）**

Replace `templates/dashboard.html` entirely with:

```html
<!doctype html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>BgsViewer · 酒馆战棋对局</title>
<link rel="stylesheet" href="/static/dashboard.css">
<script src="https://cdn.jsdelivr.net/npm/chart.js@4"></script>
</head>
<body data-mode="{{ mode }}" data-state-url="{{ state_url }}" data-state-poll="{{ state_poll }}">
<header id="topbar"></header>
<section id="current">
  <div class="col"><h2>YOUR BOARD</h2><div id="board" class="cards"></div>
    <div id="hero" class="hero"></div></div>
  <div class="col"><h2>SHOP</h2><div id="shop" class="cards"></div>
    <div id="extras"></div></div>
  <div class="col"><h2>LAST OPPONENT</h2><div id="opponent" class="cards"></div>
    <div id="opponent-hero"></div></div>
</section>
<section id="timeline"><h2>ROUND TIMELINE</h2><div id="rounds"></div>
  <div id="controls">
    <button id="prev">◀</button><button id="play">▶</button>
    <button id="next">▶|</button>
    <input id="scrub" type="range" min="0" max="0" value="0">
    <select id="speed"><option value="1000">1×</option><option value="500">2×</option><option value="250">4×</option></select>
    <span id="pos"></span>
  </div>
</section>
<section id="bottom">
  <div id="eventstream"><h2>EVENTS</h2><ul id="evlist"></ul></div>
  <div id="progression"><h2>PROGRESSION</h2>
    <canvas id="chartHp"></canvas><canvas id="chartAtk"></canvas><canvas id="chartTier"></canvas></div>
</section>
<aside id="drawer" class="hidden"><button id="drawer-close">×</button><pre id="drawer-json"></pre></aside>
<script src="/static/dashboard.js"></script>
</body></html>
```

- [ ] **Step 2: 写 dashboard.css（暗色主题 + 卡片 + 徽章）**

Create `static/dashboard.css`:

```css
:root{--bg:#14171c;--panel:#1d2128;--ink:#e6e6e6;--dim:#8a93a3;
  --accent:#5b9cff;--gold:#e3b341;--taunt:#b07a3a;--ds:#e3b341;
  --pois:#4caf50;--venom:#9c5fd4;--reborn:#2fb3a5;--wind:#3a7bd5;
  --stealth:#7a7a7a;--frozen:#5ec8e0}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--ink);
  font:14px/1.4 -apple-system,Segoe UI,Roboto,Microsoft YaHei,sans-serif}
h2{font-size:12px;color:var(--dim);text-transform:uppercase;letter-spacing:.08em;margin:8px 0 4px}
#topbar{padding:8px 12px;background:var(--panel);border-bottom:1px solid #2a2f38;
  display:flex;gap:12px;align-items:center;flex-wrap:wrap}
#topbar .hero-name{font-weight:600;font-size:16px}
#topbar .chip{background:#2a2f38;border-radius:10px;padding:1px 8px;font-size:12px}
#topbar .race{background:#243049;color:#9fc2ff}
#topbar .badge-off{color:var(--dim)}
#current{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px;padding:10px}
.col{background:var(--panel);border-radius:8px;padding:8px}
.cards{display:flex;flex-wrap:wrap;gap:6px;min-height:20px}
.card{background:#262b34;border:1px solid #333a45;border-radius:6px;
  padding:4px 6px;min-width:96px}
.card .nm{font-weight:600;font-size:12px}
.card .st{color:var(--gold)}
.card.gold{border-color:var(--gold);box-shadow:0 0 0 1px var(--gold) inset}
.kw{font-size:10px;border-radius:3px;padding:0 4px;margin-right:3px;display:inline-block}
.k-TAUNT{background:var(--taunt);color:#fff}.k-DIVINE_SHIELD{background:var(--ds);color:#000}
.k-POISONOUS{background:var(--pois);color:#000}.k-VENOMOUS{background:var(--venom);color:#fff}
.k-REBORN{background:var(--reborn);color:#000}.k-WINDFURY{background:var(--wind);color:#fff}
.k-STEALTH{background:var(--stealth);color:#fff}.k-FROZEN{background:var(--frozen);color:#000}
.hero{margin-top:6px;color:var(--dim);font-size:12px}
#extras,#opponent-hero{margin-top:6px;font-size:12px;color:var(--dim)}
#timeline,#bottom{padding:10px;border-top:1px solid #2a2f38}
#rounds{display:flex;gap:6px;flex-wrap:wrap}
.round{background:#2a2f38;border-radius:14px;padding:3px 12px;cursor:pointer;font-size:13px}
.round.cur{background:var(--accent);color:#fff}
#controls{display:flex;gap:8px;align-items:center;margin-top:8px}
#scrub{flex:1}
#bottom{display:grid;grid-template-columns:1fr 1fr;gap:12px}
#evlist{list-style:none;margin:0;padding:0;max-height:240px;overflow:auto}
#evlist li{padding:3px 6px;cursor:pointer;border-radius:4px;font-size:12px}
#evlist li:hover{background:#2a2f38}
#progression canvas{max-height:120px}
#drawer{position:fixed;top:0;right:0;width:46%;height:100%;background:#0f1115;
  border-left:1px solid #2a2f38;padding:12px;overflow:auto}
#drawer.hidden{display:none}
#drawer-json{white-space:pre-wrap;font:12px/1.4 ui-monospace,Consolas,monospace;color:#cdd}
#drawer-close{position:absolute;top:8px;right:12px;font-size:20px;background:none;color:var(--ink);border:0;cursor:pointer}
button,select,input{background:#2a2f38;color:var(--ink);border:1px solid #333a45;border-radius:4px;padding:2px 8px}
```

- [ ] **Step 3: 写 dashboard.js 核心（轮询 + renderSnapshot + 卡片）**

Create `static/dashboard.js`:

```javascript
"use strict";
const body = document.body;
const MODE = body.dataset.mode;                  // "live" | "replay"
const STATE_URL = body.dataset.stateUrl;
const STATE_POLL = body.dataset.statePoll === "true";

let events = [];          // 归一化事件数组
let lastSeq = 0;
let n = 0;                // 已应用的事件数（live=末尾；replay=拖动位置）
let playing = null;       // setInterval id

const KW = {TAUNT:"TAUNT",DIVINE_SHIELD:"DS",POISONOUS:"POIS",VENOMOUS:"VENM",
  REBORN:"REBORN",WINDFURY:"WIND",STEALTH:"STLTH",FROZEN:"FRZN"};

function isGolden(cardId){ return typeof cardId === "string" && cardId.endsWith("_G"); }

function minionCard(m, idx){
  const cardId = m.cardId || m.name || "?";
  const div = document.createElement("div");
  div.className = "card" + (isGolden(cardId) ? " gold" : "");
  const nm = m.name || cardId;
  const st = (m.attack!=null && m.health!=null) ? `<span class="st">${m.attack}/${m.health}</span>` : "";
  const kws = (m.keywords||[]).map(k=>`<span class="kw k-${k}">${KW[k]||k}</span>`).join(" ");
  div.innerHTML = `<div class="nm">${idx!=null?"["+idx+"] ":""}${nm}</div>${st} ${kws}`;
  if (m.text) div.title = m.text;
  div.style.cursor = "pointer";
  div.onclick = () => showJson(m);   // spec §8.4：点随从看原始 JSON
  return div;
}
function fillCards(elId, list){
  const el = document.getElementById(elId); el.innerHTML = "";
  (list||[]).forEach((m,i)=>el.appendChild(minionCard(m,i+1)));
  if (!(list&&list.length)) el.innerHTML = `<span style="color:var(--dim)">（空）</span>`;
}

function renderSnapshot(view){
  if (!view || !Object.keys(view).length){
    document.getElementById("board").innerHTML = `<span style="color:var(--dim)">等待事件…</span>`;
    return;
  }
  const match = view.match || {}, player = view.player || {}, shop = view.shop || null,
        lo = view.lastOpponent || null, lobby = view.lobby || null, hero = player.hero || {};
  // 顶栏
  const races = (view.availableRaces||[]).map(r=>`<span class="chip race">${r}</span>`).join(" ");
  const hpTxt = hero.health!=null ? `${hero.health}hp`+(hero.armor?` +${hero.armor}`:"") : "";
  const mmr = (match.rating||{}).mmr;
  document.getElementById("topbar").innerHTML =
    `<span class="hero-name">${hero.name||player.name||"?"}</span>
     <span class="chip">turn ${match.turn!=null?match.turn:"-"}</span>
     <span class="chip">phase ${match.phase||"-"}</span>
     <span class="chip">${match.gameType||""}</span>
     ${mmr!=null?`<span class="chip">MMR ${mmr}</span>`:""}
     ${races}
     <span id="state-badge" class="badge-off">state?</span>`;
  // 三列
  fillCards("board", player.board);
  document.getElementById("hero").innerHTML =
    `hero: ${hero.name||hero.cardId||"?"} ${hpTxt}` +
    (player.heroPower?` · heroPower: ${player.heroPower.name||player.heroPower.cardId||""}`:"");
  const tierStr = shop && shop.tier!=null ? `(tier ${shop.tier})` : `(tier ${player.tier!=null?player.tier:"-"})`;
  document.querySelector("#shop").previousElementSibling.textContent = "SHOP "+tierStr;
  fillCards("shop", shop?shop.offers:[]);
  fillCards("opponent", lo?lo.board:[]);
  document.getElementById("opponent-hero").innerHTML =
    lo?`turn ${lo.turn||"?"} · ${((lo.hero||{}).name)||((lo.hero||{}).cardId)||"?"}`:"（无）";
  // extras：饰品 / 任务 / 大厅
  const trinkets = (player.trinkets||[]).map(t=>`<span class="chip">trinket[${t.slot}] ${t.name||t.cardId||""}</span>`).join(" ");
  const q = player.questReward;
  const qStr = q ? `<span class="chip">quest: ${q.name||q.cardId} ${q.progress!=null?`(${q.progress}/${q.total||"?"})`:""}</span>` : "";
  const lobbyStr = (lobby&&lobby.players||[]).length
    ? `<details><summary>lobby (${lobby.players.length})</summary>${lobby.players.map(p=>`<div>${p.name} · ${p.heroCardId}</div>`).join("")}</details>` : "";
  document.getElementById("extras").innerHTML = trinkets + qStr + lobbyStr;
}

async function refreshView(){
  if (MODE === "live" && STATE_POLL){
    try{
      const st = await fetch(`${STATE_URL}/state?text=1`).then(r=>r.json());
      renderSnapshot(st);                       // /state 更新鲜，直接覆盖
      setBadge("state 在线");
      return;
    }catch(e){ setBadge("state 离线"); }
  }
  const v = await fetch(`/api/view?n=${n}`).then(r=>r.json());
  renderSnapshot(v);
}
function setBadge(txt){ const b=document.getElementById("state-badge"); if(b) b.textContent=txt; }

// —— 事件流 + 时间轴 ——
function eventTurn(e){
  return (e.match&&e.match.turn!=null?e.match.turn:
          (e.data&&e.data.turn!=null?e.data.turn:
          (e.data&&e.data.match&&e.data.match.turn)));
}
function renderEventStream(){
  const ul = document.getElementById("evlist"); ul.innerHTML="";
  events.forEach((e,i)=>{
    const li=document.createElement("li");
    li.textContent = `#${e.seq||"?"} ${e.type||"?"} t${eventTurn(e)!=null?eventTurn(e):"-"} ${e.at||""}`;
    if(i===n-1) li.style.background="#243049";
    li.onclick=()=>showJson(events[i]);   // spec §8.4：点事件看完整信封 JSON
    ul.appendChild(li);
  });
  ul.scrollTop = ul.scrollHeight;
}
function renderRounds(){
  const el=document.getElementById("rounds"); el.innerHTML="";
  const turns=[...new Set(events.map(eventTurn).filter(t=>t!=null))].sort((a,b)=>a-b);
  const curTurn = eventTurn(events[n-1]);
  turns.forEach(t=>{
    const d=document.createElement("span"); d.className="round"+(t===curTurn?" cur":"");
    d.textContent=`T${t}`;
    const firstIdx = events.findIndex(e=>eventTurn(e)===t);
    d.onclick=()=>{ n=firstIdx+1; syncScrub(); refreshView(); renderEventStream(); renderRounds(); };
    el.appendChild(d);
  });
}

// —— 增量轮询（live）——
async function poll(){
  try{
    const got = await fetch(`/api/events?since=${lastSeq}`).then(r=>r.json());
    if(got.length){
      events = events.concat(got);
      lastSeq = events.length?Math.max(...events.map(e=>e.seq||0)):lastSeq;
      if(MODE==="live"){ n=events.length; }
      const max=events.length;
      const scrub=document.getElementById("scrub"); scrub.max=max;
      if(MODE==="live"){ scrub.value=max; }
      renderEventStream(); renderRounds();
    }
    refreshView();
  }catch(e){ setBadge("后端离线"); }
}

// —— 回放控制 ——
function syncScrub(){ document.getElementById("scrub").value=n;
  document.getElementById("pos").textContent=`${n}/${events.length}`; }
function initReplay(){
  if(MODE!=="replay") return;
  const c=document.getElementById("controls");
  const scrub=document.getElementById("scrub");
  scrub.oninput=()=>{ n=+scrub.value; syncScrub(); refreshView(); renderEventStream(); renderRounds(); };
  document.getElementById("prev").onclick=()=>{ if(n>0){n--;} syncScrub(); refreshView(); renderEventStream(); renderRounds(); };
  document.getElementById("next").onclick=()=>{ if(n<events.length){n++;} syncScrub(); refreshView(); renderEventStream(); renderRounds(); };
  document.getElementById("play").onclick=function(){
    if(playing){ clearInterval(playing); playing=null; this.textContent="▶"; return; }
    this.textContent="⏸";
    playing=setInterval(()=>{
      if(n>=events.length){ clearInterval(playing); playing=null; document.getElementById("play").textContent="▶"; return; }
      n++; syncScrub(); refreshView(); renderEventStream(); renderRounds();
    }, +document.getElementById("speed").value);
  };
  c.style.display = "";
}

// —— 进度曲线 ——
let chartHp,chartAtk,chartTier;
function ensureCharts(){
  if(chartHp) return;
  const opt=(y)=>({responsive:true,maintainAspectRatio:false,plugins:{legend:{display:false}},
    scales:{y:{beginAtZero:true}}});
  chartHp=new Chart(document.getElementById("chartHp"),{type:"line",data:{labels:[],datasets:[{label:"hero hp",borderColor:"#e34c4c",data:[]}]},options:opt()});
  chartAtk=new Chart(document.getElementById("chartAtk"),{type:"line",data:{labels:[],datasets:[{label:"board atk",borderColor:"#5b9cff",data:[]}]},options:opt()});
  chartTier=new Chart(document.getElementById("chartTier"),{type:"bar",data:{labels:[],datasets:[{label:"tier",borderColor:"#4caf50",backgroundColor:"#4caf50",data:[]}]},options:opt()});
}
async function refreshProgression(){
  const prog = await fetch("/api/progression").then(r=>r.json());
  ensureCharts();
  const labels=prog.map(p=>"T"+p.turn);
  chartHp.data.labels=labels; chartHp.data.datasets[0].data=prog.map(p=>p.heroHp); chartHp.update();
  chartAtk.data.labels=labels; chartAtk.data.datasets[0].data=prog.map(p=>p.boardAtk); chartAtk.update();
  chartTier.data.labels=labels; chartTier.data.datasets[0].data=prog.map(p=>p.tier); chartTier.update();
}

// —— 详情抽屉 ——
function showJson(obj){
  const d=document.getElementById("drawer");
  document.getElementById("drawer-json").textContent=JSON.stringify(obj,null,2);
  d.classList.remove("hidden");
}
document.getElementById("drawer-close").onclick=()=>document.getElementById("drawer").classList.add("hidden");
document.getElementById("topbar").addEventListener("click",ev=>{
  if(ev.target.classList.contains("hero-name")) refreshView();
});

// —— 启动 ——
(async function init(){
  document.getElementById("controls").style.display = (MODE==="replay")?"":"none";
  // 首次拉全量
  events = await fetch(`/api/events?since=0`).then(r=>r.json());
  lastSeq = events.length?Math.max(...events.map(e=>e.seq||0)):0;
  n = (MODE==="live")?events.length:events.length;
  document.getElementById("scrub").max=events.length;
  if(MODE==="replay"){ document.getElementById("scrub").value=n; }
  syncScrub();
  renderEventStream(); renderRounds(); initReplay();
  await refreshView(); await refreshProgression();
  if(MODE==="live"){ setInterval(poll, 1000); setInterval(refreshProgression, 3000); }
})();
```

- [ ] **Step 4: 回放模式浏览器验证**

Run: `cd BgsDataBridge/tools/bgs_viewer && python bgs_viewer.py --replay ../../bgs-events-20260620-181754.log`
浏览器打开 `http://localhost:5000/`，逐项核对：
- 顶栏显示英雄名/turn/phase/MMR/种族 chips；
- YOUR BOARD / SHOP / LAST OPPONENT 三列有随从卡片、关键字徽章、金标（`_G` 结尾卡有金边）；
- 回合 chips `T1..T9` 可点击跳转，scrub 滑块可拖、▶ 可自动播放、速度可切；
- 事件流列表可点击打开右侧详情抽屉显示原始 JSON；
- 下方三条 Chart.js 曲线（hero hp / board atk / tier）按回合渲染。
核对无误后停止服务。

- [ ] **Step 5: 实时模式浏览器验证**

确保 HDT + 插件运行，且插件 webhook URL 含 `http://localhost:5000/events`。
Run: `cd BgsDataBridge/tools/bgs_viewer && python bgs_viewer.py --port 5000`
开一局酒馆战棋，浏览器打开 `http://localhost:5000/`，核对：
- 顶栏 `state?` 徽章在 `/state` 可达时变 `state 在线`；
- 随对局进行，事件流每秒增量追加，当前状态自动追尾更新；
- 进度曲线随回合增长。
核对无误后停止服务。

- [ ] **Step 6: 提交**

```bash
git add BgsDataBridge/tools/bgs_viewer/templates/dashboard.html BgsDataBridge/tools/bgs_viewer/static/dashboard.css BgsDataBridge/tools/bgs_viewer/static/dashboard.js
git commit -m "feat(bgs-viewer): add dashboard UI (current state + timeline + charts)"
```

---

### Task 6: README + 收尾冒烟

**Files:**
- Create: `BgsDataBridge/tools/bgs_viewer/README.md`

- [ ] **Step 1: 写 README**

Create `README.md`:

```markdown
# BgsViewer · 酒馆战棋对局可视化仪表盘

把 BgsDataBridge 插件的事件流（实时 webhook 或日志回放）可视化为
"当前状态 + 回合时间轴 + 进度曲线"，展示 bgs-state/v1 契约内的全部字段。

## 依赖
- Python 3.8+
- `pip install flask`
- 浏览器查看页面时需联网加载 Chart.js（CDN）

## 实时模式
1. `python bgs_viewer.py --port 5000`
2. HDT → Options → Tracker → Plugins → BgsDataBridge 设置里，把 webhook URL
   设为 `http://localhost:5000/events`（可与 receiver.py 并存，插件支持多 URL）。
3. 浏览器打开 `http://localhost:5000/`。

默认每 1.5s 由浏览器直连插件 `http://localhost:5273/state` 取最新快照（插件已开
CORS）。用 `--no-state-poll` 关闭，仅靠 webhook。

## 回放模式
`python bgs_viewer.py --replay <bgs-events-*.log | *.jsonl>`
拖动条/▶/步进/速度可逐事件浏览，回合 chips 可点跳转。

## 可选
- `--jsonlog out.jsonl` 实时模式下把收到的事件另写 NDJSON（可替代 receiver.py 日志）。
- `--state-url http://host:port` 覆盖插件 /state 地址。
- `--host 0.0.0.0` 对外暴露（默认仅 127.0.0.1）。

## 字段说明
顶栏 + 三列 + 详情抽屉合起来覆盖契约内所有插件实际填充字段。
死字段（dbfId、minion.golden、shop.frozen、zonePosition、cost、match.anomaly、
match.gameUuid）插件从不填充，不展示。金标按 cardId 以 `_G` 结尾推断。

## 测试
`cd BgsDataBridge/tools/bgs_viewer && python -m unittest`
```

- [ ] **Step 2: 全量回归**

Run: `cd BgsDataBridge/tools/bgs_viewer && python -m unittest -v`
Expected: 15 个测试全 PASS。

Run: `cd BgsDataBridge/tools/bgs_viewer && python -c "from logparser import parse; r=parse('../../bgs-events-20260620-164858.log'); print('events', len(r.events), 'skipped', r.skipped)"`（用仓库内任一真实日志）
Expected: 解析成功，`skipped 0`，types 齐全。

- [ ] **Step 3: 提交**

```bash
git add BgsDataBridge/tools/bgs_viewer/README.md
git commit -m "docs(bgs-viewer): add README (usage, modes, fields, tests)"
```

---

## Self-Review 记录

**Spec coverage**：spec §2 契约→Task 1-3 数据模型；§4 架构 EventStore→Task 1；§5 两模式 CLI→Task 4；§6 三数据源（webhook→Task 4 `/events`、`/state` 轮询→Task 5 浏览器直连、文件回放→Task 2 parser + Task 4 `--replay`）；§7 EventStore→Task 1；§8 前端布局/重建算法/卡片/抽屉→Task 3 `reconstruct_state` + Task 5；§9 路由→Task 4（`/api/view`、`/api/progression` 在 spec §9 基础上新增，是实现 §8.2 重建与 §8 进度曲线的必要端点）；§10 技术抉择→全部落地；§11 文件结构→一一对应；§12 错误处理→Task 4（坏 JSON 400）、Task 2（坏行计数）、Task 5（state/后端离线徽章）；§13 测试→Task 1-3 unittest；§14 成功标准→Task 4/5/6 验证步骤覆盖。无遗漏。

**Placeholder scan**：无 TBD/TODO；每个代码步骤均含完整可运行代码。

**Type consistency**：归一化事件 dict 形状 `{seq,type,at,match,data,source}` 在 Task 1（EventStore）、Task 2（logparser）、Task 4（`_env_to_event`）三处一致；`reconstruct_state`/`compute_progression` 签名 Task 3 定义、Task 4 `/api/view`/`/api/progression` 调用一致；`ParseResult.events/skipped` Task 2 定义、Task 4 `--replay` 读取一致；端点名 `/api/view?n=`、`/api/progression`、`/api/events?since=`、`/api/mode` 在 Task 4 定义、Task 5 调用一致。
