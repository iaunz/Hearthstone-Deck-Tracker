# BgsAdvisor Stage 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Stage-1 BgsAdvisor MVP — a Flask + browser dashboard (BgsViewer superset) that receives BgsDataBridge webhooks / replays logs and produces cloud-LLM advice for Positioning and ShopPhase decisions, plus capture the Battlegrounds anomaly in the C# plugin.

**Architecture:** The advisor is a sibling program `BgsDataBridge/tools/bgs_advisor/` that imports BgsViewer's already-built pure-logic modules (`eventstore`/`logparser`/`stateview`) via `sys.path`. A pure-logic decision engine (`decision/`) orchestrates deterministic heuristic hints → cloud LLM (sole advice source; **no fallback**). A background thread runs the engine per request so Flask is never blocked. The C# plugin gets a one-line anomaly capture + one-line projector mapping.

**Tech Stack:** Python 3 (stdlib + `flask` + `httpx`); C# plugin on net472 / x86 / C# 7.3 built with classic MSBuild. Pure-logic modules tested with `unittest` (zero-dep, matching `bgs_viewer/test_*.py` style).

## Global Constraints

- **C# plugin (Task 1 only):** target framework `net472`, platform `x86`, language `C# 7.3`. Build with classic MSBuild (NOT `dotnet build` — it breaks on HDT's ResGen target):
  `"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge/BgsDataBridge.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo`
- **HDT integration layer has no unit tests** — verified by MSBuild build passing + runtime inspection of `/state`. Every HDT read is wrapped in `Safe(...)`/`SafeValue(...)`.
- **Python part:** stdlib + `flask` (already a BgsViewer dep) + `httpx` (`pip install httpx`). Pure-logic modules (`advice`, `heuristics`, `prompt`, `engine`, `advicestore`, `config` masking) are fully covered by `unittest`; IO layers (`llm_strategy`, Flask app, frontend) verified by build + runtime.
- **No automation / no input injection** (ToS). The advisor only emits text + action-card suggestions the human executes.
- **No graceful degradation:** if the cloud LLM call fails, return `Advice(status="error")` with empty actions — never heuristic-only advice.
- **bgs-advice/v1** JSON contract and **bgs-event/v1** / **bgs-state/v1** (from the plugin) are the only cross-process schemas.
- **Stage-2 deferred** (NOT in this plan): gold capture, HeroPick/TrinketPick offered-choices — these belong to a separate plugin spec. ShopPhase advice in Stage 1 is therefore *qualitative* (no gold budget).

## Scope note (deviation from spec §5.5)

Spec §5.5 lists 5 heuristics. Stage 1 implements **3**: `golden-opportunity`, `taunt-positioning`, `level-up-window`. `freeze-value` is deferred (needs gold — Stage-2 dependency). `quest-progress` is deferred to keep Stage 1 lean. The `Tactic` registry is a plain list, so adding them later is one class + one list entry — no interface change.

## File Structure

```
BgsDataBridge/Projector/HdtGameSource.cs         [MODIFY]  +anomaly capture (~5 lines)
BgsDataBridge/Projector/GameStateProjector.cs    [MODIFY]  +Anomaly mapping (1 line in Match init)

BgsDataBridge/tools/bgs_advisor/                 [CREATE dir]
  decision/__init__.py                           empty package marker
  decision/advice.py                             Advice/Action POCO + JSON serde (pure)
  decision/heuristics.py                         Tactic findings: golden/taunt/level-up (pure)
  decision/prompt.py                             snapshot+findings → (system,user) prompt (pure)
  decision/engine.py                             DecisionEngine orchestration, injectable advisor (pure)
  decision/llm_strategy.py                       LlmStrategyAdvisor: Anthropic API via httpx (IO)
  advicestore.py                                 AdviceStore: latest advice per decisionType (pure)
  config.py                                      load/save config + API-key masking (pure-ish)
  bgs_advisor.py                                 Flask app: reuses bgs_viewer modules + advice routes (IO)
  templates/dashboard.html                       copy of bgs_viewer's + ADVICE section + settings
  static/dashboard.js                            copy of bgs_viewer's + advice poller/renderer
  static/dashboard.css                           copy of bgs_viewer's + advice styles
  tests/test_advice.py                           unittest
  tests/test_heuristics.py                       unittest
  tests/test_prompt.py                           unittest
  tests/test_engine.py                           unittest (FakeAdvisor)
  tests/test_advicestore.py                      unittest
  tests/test_config.py                           unittest
  README.md                                      usage, key config, data dependencies
```

**Reuse from BgsViewer (already built, imported via `sys.path`):**
- `eventstore.EventStore` — `.append(event)` / `.all()` / `.since(seq)` / `.last_seq()`, thread-safe.
- `logparser.parse(path)` → object with `.events` (list) and `.skipped` (int).
- `stateview.reconstruct_state(events)` → snapshot-view dict (fold over events); `compute_progression(events)`.

---

## Task 1: Capture Battlegrounds anomaly in the C# plugin

Makes `match.anomaly` populated (currently a dead field). Low-risk: HDT has the API; DTO + View fields already exist. Benefits all decision types.

**Files:**
- Modify: `BgsDataBridge/Projector/HdtGameSource.cs` (inside `Capture()`, after the `DuosMmr` assignment ~line 149, before the `catch`)
- Modify: `BgsDataBridge/Projector/GameStateProjector.cs:19-25` (the `Match = new BgsMatch { ... }` initializer)

**Interfaces:**
- Consumes: `BattlegroundsUtils.GetBattlegroundsAnomalyDbfId(Entity?) → int?` (already in scope via `using Hearthstone_Deck_Tracker.Hearthstone;` — same symbol used for `GetAvailableRaces()` at HdtGameSource.cs:144); `Database.GetCardFromDbfId(int, bool) → Card?` (same namespace; `Card.Id` is the cardId string); `Entity` (already used; `new Entity { CardId = ... }` pattern used in `CaptureShop`).
- Produces: `GameStateView.Anomaly` (Entity, already declared at GameStateView.cs:22) now populated; `BgsSnapshot.Match.Anomaly` (BgsCardRef, already declared at BgsSnapshot.cs:37) now serialized.

- [ ] **Step 1: Add anomaly capture to `HdtGameSource.Capture()`**

In `BgsDataBridge/Projector/HdtGameSource.cs`, find the end of the meta-capture block:

```csharp
                v.Mmr = SafeValue(() => g.BattlegroundsRatingInfo?.Rating);
                v.DuosMmr = SafeValue(() => g.BattlegroundsRatingInfo?.DuosRating);
```

Insert immediately after those two lines (still inside the `try`, before the closing `}` of the try):

```csharp
                // 畸变(anomaly):读游戏实体的 BACON_GLOBAL_ANOMALY_DBID tag,转 cardId。
                // DTO/View 字段已存在,此处补捕获(此前为死字段)。全决策类型受益。
                var anomalyDbf = SafeValue(() => BattlegroundsUtils.GetBattlegroundsAnomalyDbfId(g.GameEntity));
                if (anomalyDbf.HasValue)
                {
                    var anomalyCardId = Safe(() => Database.GetCardFromDbfId(anomalyDbf.Value, false)?.Id);
                    if (!string.IsNullOrEmpty(anomalyCardId))
                        v.Anomaly = new Entity { CardId = anomalyCardId };
                }
```

- [ ] **Step 2: Map `v.Anomaly` → `BgsMatch.Anomaly` in `GameStateProjector.Project()`**

In `BgsDataBridge/Projector/GameStateProjector.cs`, the `Match` initializer (lines 19-25) currently ends:

```csharp
                    Rating = (v.Mmr.HasValue || v.DuosMmr.HasValue) ? new BgsRating { Mmr = v.Mmr, DuosMmr = v.DuosMmr } : null
                },
```

Add the `Anomaly` mapping before the closing `},` of the `Match` initializer:

```csharp
                    Rating = (v.Mmr.HasValue || v.DuosMmr.HasValue) ? new BgsRating { Mmr = v.Mmr, DuosMmr = v.DuosMmr } : null,
                    // 畸变:cardId+name+text(text 始终输出,描述规则改动,对 AI 决策关键)。
                    Anomaly = v.Anomaly != null ? ToCard(v.Anomaly, true) : null
                },
```

`ToCard(Entity, bool)` already exists (GameStateProjector.cs:71) and returns a `BgsCardRef` with `CardId`/`Name`/`Text`. `e.Card?.Name` / `e.Card?.Text` resolve from `CardId` (same mechanism `CaptureShop`/`ToShopOffer` rely on). `BgsMatch.Anomaly` is `[JsonProperty("anomaly", NullValueHandling.Ignore)]`, so null is omitted as before.

- [ ] **Step 3: Build the plugin with MSBuild**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge/BgsDataBridge.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
```
Expected: build succeeds, no errors. (Warnings about Private=false refs are fine.)

- [ ] **Step 4: Commit**

```bash
git add BgsDataBridge/Projector/HdtGameSource.cs BgsDataBridge/Projector/GameStateProjector.cs
git commit -m "feat(bgs-databridge): capture Battlegrounds anomaly in snapshot"
```

**Runtime verification (not a step — do this once deployed):** copy `BgsDataBridge/bin/x86/Debug/BgsDataBridge.dll` to `%APPDATA%\HearthstoneDeckTracker\Plugins\`, restart HDT, enable plugin, enter a BG match with an anomaly, and check `http://localhost:5273/state` — `match.anomaly` should now carry `{cardId, name, text}`. This is the integration-layer verification per CLAUDE.md (no unit test).

---

## Task 2: `decision/advice.py` — Advice/Action POCO + serde

The `bgs-advice/v1` contract. Every later module depends on these names.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/decision/__init__.py` (empty)
- Create: `BgsDataBridge/tools/bgs_advisor/decision/advice.py`
- Test: `BgsDataBridge/tools/bgs_advisor/tests/test_advice.py`

**Interfaces:**
- Produces: `Action(kind, cardId=None, name=None, index=None, note=None)` with `.to_dict()` / `.from_dict(d)`; `Advice(decisionType, trigger, snapshotRef, status="thinking", actions=None, rationale=None, llm=None, ts=0, schema="bgs-advice/v1")` with `.to_dict()` / `.from_dict(d)`; module constant `STATUSES = ("thinking", "ok", "error")`; `ActionKinds` constants. `ts` is a store-assigned monotonic int (default 0).

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge/tools/bgs_advisor/tests/test_advice.py`:

```python
import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.advice import Advice, Action, STATUSES


class TestAdvice(unittest.TestCase):
    def test_action_roundtrip(self):
        a = Action(kind="BUY", cardId="CS3_014", name="雷鳞海妖", note="核心")
        d = a.to_dict()
        self.assertEqual(d["kind"], "BUY")
        self.assertEqual(d["cardId"], "CS3_014")
        self.assertIsNone(d["index"])  # None keys still round-trip
        self.assertEqual(Action.from_dict(d), a)

    def test_advice_roundtrip_ok(self):
        adv = Advice(
            decisionType="ShopPhase", trigger="manual",
            snapshotRef={"seq": 7, "turn": 5, "capturedAt": "2026-06-22T00:00:00Z"},
            status="ok",
            actions=[Action(kind="LEVEL_UP"), Action(kind="SELL", cardId="X1", name="鱼人")],
            rationale="升本窗口已开",
            llm={"model": "claude-sonnet-4-6", "latencyMs": 1200, "tokensIn": 900,
                 "tokensOut": 40, "error": None},
        )
        d = adv.to_dict()
        self.assertEqual(d["schema"], "bgs-advice/v1")
        self.assertEqual(d["status"], "ok")
        self.assertEqual(len(d["actions"]), 2)
        back = Advice.from_dict(d)
        self.assertEqual(back.decisionType, "ShopPhase")
        self.assertEqual(back.actions[1].name, "鱼人")
        self.assertEqual(back.llm["model"], "claude-sonnet-4-6")

    def test_advice_error_status_no_actions(self):
        adv = Advice(decisionType="ShopPhase", trigger="manual",
                     snapshotRef={"seq": 1}, status="error", actions=[],
                     rationale=None, llm={"error": "timeout"})
        self.assertEqual(adv.to_dict()["status"], "error")

    def test_invalid_status_rejected(self):
        with self.assertRaises(ValueError):
            Advice(decisionType="ShopPhase", trigger="manual",
                   snapshotRef={}, status="bogus")


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_advice.py`
Expected: FAIL with `ModuleNotFoundError: No module named 'decision.advice'`.

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/tools/bgs_advisor/decision/__init__.py` (empty file).

Create `BgsDataBridge/tools/bgs_advisor/decision/advice.py`:

```python
"""bgs-advice/v1 契约:类型化的行动建议。纯逻辑,无 IO。"""
from dataclasses import dataclass, field, asdict

SCHEMA = "bgs-advice/v1"
STATUSES = ("thinking", "ok", "error")


@dataclass
class Action:
    kind: str
    cardId: str | None = None
    name: str | None = None
    index: int | None = None
    note: str | None = None

    def to_dict(self) -> dict:
        return asdict(self)

    @staticmethod
    def from_dict(d: dict) -> "Action":
        return Action(kind=d["kind"], cardId=d.get("cardId"), name=d.get("name"),
                      index=d.get("index"), note=d.get("note"))


@dataclass
class Advice:
    decisionType: str
    trigger: str
    snapshotRef: dict
    status: str = "thinking"
    actions: list = field(default_factory=list)   # list[Action]
    rationale: str | None = None
    llm: dict = field(default_factory=dict)       # {model,latencyMs,tokensIn,tokensOut,error}
    ts: int = 0                                    # AdviceStore 赋的 monotonic 序号
    schema: str = SCHEMA

    def __post_init__(self):
        if self.status not in STATUSES:
            raise ValueError(f"invalid status {self.status!r}; must be one of {STATUSES}")

    def to_dict(self) -> dict:
        return {
            "schema": self.schema,
            "decisionType": self.decisionType,
            "trigger": self.trigger,
            "snapshotRef": self.snapshotRef,
            "status": self.status,
            "actions": [a.to_dict() for a in self.actions],
            "rationale": self.rationale,
            "llm": self.llm,
        }

    @staticmethod
    def from_dict(d: dict) -> "Advice":
        return Advice(
            schema=d.get("schema", SCHEMA),
            decisionType=d["decisionType"],
            trigger=d["trigger"],
            snapshotRef=d.get("snapshotRef", {}),
            status=d.get("status", "thinking"),
            actions=[Action.from_dict(a) for a in d.get("actions", [])],
            rationale=d.get("rationale"),
            llm=d.get("llm", {}),
        )
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_advice.py`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/decision/__init__.py BgsDataBridge/tools/bgs_advisor/decision/advice.py BgsDataBridge/tools/bgs_advisor/tests/test_advice.py
git commit -m "feat(bgs-advisor): add Advice/Action POCO + bgs-advice/v1 serde"
```

---

## Task 3: `decision/heuristics.py` — deterministic tactic hints

Produces `TacticFinding`s that the prompt bakes in as hints. These are **not** user-facing advice (no fallback) — they only inform the LLM.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/decision/heuristics.py`
- Test: `BgsDataBridge/tools/bgs_advisor/tests/test_heuristics.py`

**Interfaces:**
- Produces: `TacticFinding(code, severity, message)`; base class `Tactic` (`code: str`, `evaluate(snapshot, dtype) -> TacticFinding | None`); concrete `GoldenOpportunity`, `TauntPositioning`, `LevelUpWindow`; `DEFAULT_TACTICS` list; `run_tactics(snapshot, dtype, tactics=None) -> list[TacticFinding]`.
- Consumes: snapshot-view dict shape from `stateview.reconstruct_state` — `{"match": {"turn", "phase"}, "player": {"tier", "board": [{"name","keywords",...}], "hand": [...]}, "shop": {"offers": [...]}}`. `board`/`hand`/`offers` items are minion dicts (may have `name`, `cardId`, `keywords`).

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge/tools/bgs_advisor/tests/test_heuristics.py`:

```python
import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.heuristics import run_tactics, GoldenOpportunity, TauntPositioning, LevelUpWindow


def minion(name, keywords=None):
    return {"name": name, "cardId": name, "attack": 1, "health": 1, "keywords": keywords or []}


class TestHeuristics(unittest.TestCase):
    def test_golden_opportunity_two_same_name(self):
        snap = {"player": {"board": [minion("雷鳞海妖")], "hand": [minion("雷鳞海妖")]}}
        findings = GoldenOpportunity().evaluate(snap, "ShopPhase")
        self.assertIsNotNone(findings)
        self.assertIn("雷鳞海妖", findings.message)

    def test_golden_opportunity_none(self):
        snap = {"player": {"board": [minion("A")], "hand": [minion("B")]}}
        self.assertIsNone(GoldenOpportunity().evaluate(snap, "ShopPhase"))

    def test_taunt_positioning_only_for_positioning_dtype(self):
        # TAUNT minion at index 1 (not leftmost)
        snap = {"player": {"board": [minion("A"), minion("B", ["TAUNT"])]}}
        self.assertIsNone(TauntPositioning().evaluate(snap, "ShopPhase"))  # wrong dtype
        f = TauntPositioning().evaluate(snap, "Positioning")
        self.assertIsNotNone(f)

    def test_taunt_positioning_already_leftmost(self):
        snap = {"player": {"board": [minion("A", ["TAUNT"]), minion("B")]}}
        self.assertIsNone(TauntPositioning().evaluate(snap, "Positioning"))

    def test_level_up_window_high_tier(self):
        # tier 5+ → no level-up window (already near top)
        snap = {"player": {"tier": 5}}
        self.assertIsNone(LevelUpWindow().evaluate(snap, "ShopPhase"))
        snap2 = {"player": {"tier": 3}}
        f = LevelUpWindow().evaluate(snap2, "ShopPhase")
        self.assertIsNotNone(f)

    def test_run_tactics_collects(self):
        snap = {"player": {"tier": 3, "board": [minion("X")], "hand": [minion("X")]}}
        codes = {f.code for f in run_tactics(snap, "ShopPhase")}
        self.assertIn("golden-opportunity", codes)
        self.assertIn("level-up-window", codes)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_heuristics.py`
Expected: FAIL with `ModuleNotFoundError: No module named 'decision.heuristics'`.

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/tools/bgs_advisor/decision/heuristics.py`:

```python
"""确定性战术启发式:产 TacticFinding 提示,喂进 LLM prompt(不对用户可见,无降级)。纯逻辑。"""
from dataclasses import dataclass


@dataclass
class TacticFinding:
    code: str
    severity: str          # info | warn | critical
    message: str


class Tactic:
    code: str = ""
    def evaluate(self, snapshot: dict, dtype: str):  # -> TacticFinding | None
        raise NotImplementedError


def _names(items):
    return [m.get("name") or m.get("cardId") for m in (items or []) if m]


class GoldenOpportunity(Tactic):
    code = "golden-opportunity"
    def evaluate(self, snapshot, dtype):
        player = snapshot.get("player") or {}
        names = _names(player.get("board")) + _names(player.get("hand")) + \
                _names((snapshot.get("shop") or {}).get("offers"))
        seen = {}
        for n in names:
            if n is None:
                continue
            seen[n] = seen.get(n, 0) + 1
            if seen[n] == 2:
                return TacticFinding(self.code, "warn", f"可合成金色: {n}")
        return None


class TauntPositioning(Tactic):
    """Positioning 决策:嘲讽随从应靠左(第一个承伤)。"""
    code = "taunt-positioning"
    def evaluate(self, snapshot, dtype):
        if dtype != "Positioning":
            return None
        board = (snapshot.get("player") or {}).get("board") or []
        for i, m in enumerate(board):
            if "TAUNT" in (m.get("keywords") or []) and i != 0:
                name = m.get("name") or m.get("cardId") or "嘲讽随从"
                return TacticFinding(self.code, "warn", f"{name} 是嘲讽但未在最左侧")
        return None


class LevelUpWindow(Tactic):
    """tier < 5 时提示存在升本空间(启发式提示,具体由 LLM 判断时机)。"""
    code = "level-up-window"
    def evaluate(self, snapshot, dtype):
        if dtype not in ("ShopPhase", "Positioning"):
            return None
        tier = (snapshot.get("player") or {}).get("tier")
        if isinstance(tier, int) and 1 <= tier <= 4:
            return TacticFinding(self.code, "info", f"当前酒馆 {tier} 级,可考虑升本窗口")
        return None


DEFAULT_TACTICS = [GoldenOpportunity(), TauntPositioning(), LevelUpWindow()]


def run_tactics(snapshot: dict, dtype: str, tactics=None):
    tactics = tactics if tactics is not None else DEFAULT_TACTICS
    out = []
    for t in tactics:
        f = t.evaluate(snapshot, dtype)
        if f is not None:
            out.append(f)
    return out
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_heuristics.py`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/decision/heuristics.py BgsDataBridge/tools/bgs_advisor/tests/test_heuristics.py
git commit -m "feat(bgs-advisor): add heuristic tactic findings (golden/taunt/level-up)"
```

---

## Task 4: `decision/prompt.py` — snapshot + findings → LLM prompt

Encodes the snapshot into compact LLM-friendly text and bakes heuristic findings in as hints. Pure logic.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/decision/prompt.py`
- Test: `BgsDataBridge/tools/bgs_advisor/tests/test_prompt.py`

**Interfaces:**
- Consumes: snapshot-view dict (Task 3 shape + `availableRaces`, `player.hero`, `player.heroPower`, `player.trinkets`, `player.questReward`, `match.anomaly`); `list[TacticFinding]` (Task 3).
- Produces: `encode_prompt(snapshot, dtype, findings) -> (system_msg: str, user_msg: str)`.

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge/tools/bgs_advisor/tests/test_prompt.py`:

```python
import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.prompt import encode_prompt
from decision.heuristics import TacticFinding


def snap():
    return {
        "match": {"turn": 5, "phase": "Shop", "anomaly": {"name": "畸变X", "text": "随从少花1金币"}},
        "availableRaces": ["MURLOC", "BEAST"],
        "player": {"tier": 3, "hero": {"name": "英雄A", "health": 40},
                   "board": [{"name": "雷鳞海妖", "attack": 3, "health": 2, "keywords": ["TAUNT"]}],
                   "hand": [{"name": "鱼人斥候", "attack": 1, "health": 1}],
                   "heroPower": {"name": "技能A", "text": "战吼:..."},
                   "trinkets": [], "questReward": None},
        "shop": {"available": True, "tier": 3, "offers": [{"name": "雷鳞海妖", "attack": 3, "health": 2}]},
    }


class TestPrompt(unittest.TestCase):
    def test_system_message_sets_role_and_json_contract(self):
        system, _ = encode_prompt(snap(), "ShopPhase", [])
        self.assertIn("酒馆战棋", system)
        self.assertIn("JSON", system)

    def test_user_message_contains_state_fields(self):
        _, user = encode_prompt(snap(), "ShopPhase", [])
        self.assertIn("雷鳞海妖", user)
        self.assertIn("3/2", user)            # attack/health compact form
        self.assertIn("TAUNT", user)
        self.assertIn("ShopPhase", user)
        self.assertIn("畸变X", user)           # anomaly surfaces
        self.assertIn("MURLOC", user)         # races surface

    def test_findings_baked_into_user_message(self):
        findings = [TacticFinding("golden-opportunity", "warn", "可合成金色: 雷鳞海妖")]
        _, user = encode_prompt(snap(), "ShopPhase", findings)
        self.assertIn("可合成金色", user)


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_prompt.py`
Expected: FAIL with `ModuleNotFoundError: No module named 'decision.prompt'`.

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/tools/bgs_advisor/decision/prompt.py`:

```python
"""快照 + 启发式 findings → LLM prompt(system+user)。纯逻辑。"""
from .heuristics import TacticFinding

SYSTEM = """你是酒馆战棋(Battlegrounds)军师。根据当前对局状态给出**类型化的行动建议**。
你必须只返回一个 JSON 对象,不要任何额外文字,结构为:
{"rationale": "一句话大局观", "actions": [{"kind": "...", "cardId": "...", "name": "...", "index": null, "note": "..."}]}
actions[].kind 按决策类型取值:
- ShopPhase: BUY | SELL | PLAY | REPOSITION | LEVEL_UP | REROLL | FREEZE | HERO_POWER
- Positioning: PLACE (带 index,给出完整排序,0=最左)
- HeroPick: PICK_HERO ; TrinketPick: PICK_TRINKET
你只建议,不假设玩家会自动执行。"""


def _minion(m):
    name = m.get("name") or m.get("cardId") or "?"
    atk = m.get("attack")
    hp = m.get("health")
    stats = f"{atk}/{hp}" if atk is not None and hp is not None else ""
    kws = " ".join(m.get("keywords") or [])
    return f"{name} {stats}" + (f" [{kws}]" if kws else "")


def _list(items):
    return ", ".join(_minion(m) for m in (items or [])) or "(空)"


def encode_prompt(snapshot: dict, dtype: str, findings: list) -> tuple:
    match = snapshot.get("match") or {}
    player = snapshot.get("player") or {}
    shop = snapshot.get("shop") or {}
    anomaly = match.get("anomaly")
    hero = player.get("hero") or {}

    lines = [f"决策类型: {dtype}", f"回合: {match.get('turn')}  相位: {match.get('phase')}"]
    if anomaly:
        lines.append(f"畸变: {anomaly.get('name')} ({anomaly.get('text')})")
    races = snapshot.get("availableRaces") or []
    if races:
        lines.append("可用种族: " + ", ".join(races))
    lines.append(f"酒馆等级(tier): {player.get('tier')}")
    if hero:
        lines.append(f"英雄: {hero.get('name')} {hero.get('health')}hp" +
                     (f" +{hero['armor']}" if hero.get("armor") else ""))
    hp_ = player.get("heroPower")
    if hp_:
        lines.append(f"英雄技能: {hp_.get('name')} ({hp_.get('text')})")
    for t in player.get("trinkets") or []:
        lines.append(f"饰品[{t.get('slot')}]: {t.get('name')} ({t.get('text')})")
    qr = player.get("questReward")
    if qr:
        prog = qr.get("progress"); tot = qr.get("total")
        lines.append(f"任务奖励: {qr.get('name')} 进度 {prog}/{tot}")
    lines.append("你的阵容: " + _list(player.get("board")))
    lines.append("手牌: " + _list(player.get("hand")))
    if shop and shop.get("available"):
        lines.append(f"商店(tier {shop.get('tier')}): " + _list(shop.get("offers")))
    if findings:
        lines.append("启发式提示(请据此校准建议): " + "; ".join(f.message for f in findings))
    lines.append(f"\n请针对 {dtype} 返回上述 JSON。")
    return SYSTEM, "\n".join(lines)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_prompt.py`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/decision/prompt.py BgsDataBridge/tools/bgs_advisor/tests/test_prompt.py
git commit -m "feat(bgs-advisor): add snapshot→LLM prompt encoder"
```

---

## Task 5: `advicestore.py` — latest advice per decisionType

Thread-safe store; the Flask app keeps the latest Advice per decisionType and serves incremental fetches.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/advicestore.py`
- Test: `BgsDataBridge/tools/bgs_advisor/tests/test_advicestore.py`

**Interfaces:**
- Consumes: `Advice` (Task 2).
- Produces: `AdviceStore` — `.put(advice)` (stamps `advice.ts` with a monotonic counter, stores latest per `decisionType`); `.latest(decisionType=None) -> Advice | None`; `.since(ts) -> list[Advice]` (advices with `.ts > ts`, ordered); `.all() -> list[Advice]`.

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge/tools/bgs_advisor/tests/test_advicestore.py`:

```python
import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from advicestore import AdviceStore
from decision.advice import Advice


def advice(dtype, status="ok"):
    return Advice(decisionType=dtype, trigger="manual", snapshotRef={"seq": 1}, status=status)


class TestAdviceStore(unittest.TestCase):
    def test_put_stamps_monotonic_ts(self):
        s = AdviceStore()
        a = advice("ShopPhase")
        s.put(a)
        self.assertGreater(a.ts, 0)

    def test_latest_per_decisiontype(self):
        s = AdviceStore()
        s.put(advice("ShopPhase"))
        s.put(advice("Positioning"))
        self.assertEqual(s.latest("ShopPhase").decisionType, "ShopPhase")
        self.assertEqual(s.latest("Positioning").decisionType, "Positioning")

    def test_put_replaces_latest_for_same_type(self):
        s = AdviceStore()
        first = advice("ShopPhase"); s.put(first)
        second = advice("ShopPhase"); s.put(second)
        self.assertIs(s.latest("ShopPhase"), second)
        self.assertGreater(second.ts, first.ts)

    def test_since_returns_newer_only(self):
        s = AdviceStore()
        a = advice("ShopPhase"); s.put(a)
        b = advice("Positioning"); s.put(b)
        got = s.since(a.ts)
        self.assertEqual([x.decisionType for x in got], ["Positioning"])

    def test_latest_none_default(self):
        self.assertIsNone(AdviceStore().latest())


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_advicestore.py`
Expected: FAIL with `ModuleNotFoundError: No module named 'advicestore'`.

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/tools/bgs_advisor/advicestore.py`:

```python
"""按 decisionType 存最近建议,线程安全,带 monotonic ts 供增量拉取。纯逻辑。"""
import threading


class AdviceStore:
    def __init__(self):
        self._lock = threading.Lock()
        self._latest = {}     # decisionType -> Advice
        self._counter = 0

    def put(self, advice):
        with self._lock:
            self._counter += 1
            advice.ts = self._counter
            self._latest[advice.decisionType] = advice
        return advice

    def latest(self, decisionType=None):
        with self._lock:
            if decisionType is None:
                advs = list(self._latest.values())
                return max(advs, key=lambda a: a.ts) if advs else None
            return self._latest.get(decisionType)

    def since(self, ts):
        with self._lock:
            advs = [a for a in self._latest.values() if a.ts > ts]
        advs.sort(key=lambda a: a.ts)
        return advs

    def all(self):
        with self._lock:
            advs = list(self._latest.values())
        advs.sort(key=lambda a: a.ts)
        return advs
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_advicestore.py`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/advicestore.py BgsDataBridge/tools/bgs_advisor/tests/test_advicestore.py
git commit -m "feat(bgs-advisor): add thread-safe AdviceStore"
```

---

## Task 6: `decision/engine.py` — orchestration with injectable advisor

Pure-logic orchestration: run heuristics → call advisor → stamp trigger/snapshotRef. The advisor (Task 8) owns the LLM call + error→error-Advice contract. Tested with a `FakeAdvisor`.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/decision/engine.py`
- Test: `BgsDataBridge/tools/bgs_advisor/tests/test_engine.py`

**Interfaces:**
- Consumes: `run_tactics` (Task 3); `Advice` (Task 2). The advisor is any object with `.advise(snapshot, dtype, findings) -> Advice`.
- Produces: `StrategyAdvisor` (Protocol); `DecisionEngine(advisor, tactics=None)` with `.decide(snapshot, dtype, trigger="manual", snapshotRef=None) -> Advice`.

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge/tools/bgs_advisor/tests/test_engine.py`:

```python
import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.engine import DecisionEngine
from decision.advice import Advice, Action


class FakeAdvisor:
    """Records what the engine passed; returns a canned Advice."""
    def __init__(self, status="ok"):
        self.status = status
        self.seen = None
    def advise(self, snapshot, dtype, findings):
        self.seen = (dtype, [f.code for f in findings])
        return Advice(decisionType=dtype, trigger="manual", snapshotRef={}, status=self.status,
                      actions=[Action(kind="LEVEL_UP")], rationale="ok",
                      llm={"model": "fake", "latencyMs": 1, "tokensIn": 0, "tokensOut": 0, "error": None})


def snap():
    return {"player": {"tier": 3, "board": [{"name": "X", "keywords": []}],
                       "hand": [{"name": "X", "keywords": []}]}}


class TestEngine(unittest.TestCase):
    def test_decide_runs_tactics_and_calls_advisor(self):
        adv = FakeAdvisor()
        eng = DecisionEngine(adv)
        out = eng.decide(snap(), "ShopPhase", trigger="manual", snapshotRef={"seq": 9})
        self.assertEqual(out.status, "ok")
        self.assertEqual(out.snapshotRef, {"seq": 9})
        self.assertEqual(out.trigger, "manual")
        # advisor received the golden-opportunity finding
        self.assertIn("golden-opportunity", adv.seen[1])

    def test_decide_propagates_advisor_error_unchanged(self):
        eng = DecisionEngine(FakeAdvisor(status="error"))
        out = eng.decide(snap(), "ShopPhase")
        self.assertEqual(out.status, "error")
        self.assertEqual(out.actions, [])


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_engine.py`
Expected: FAIL with `ModuleNotFoundError: No module named 'decision.engine'`.

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/tools/bgs_advisor/decision/engine.py`:

```python
"""决策引擎编排:启发式 → prompt(hints) → advisor(建议唯一来源)。纯逻辑,注入 advisor 可测。

'异步'由调用方(Flask 后台线程)保证;引擎本身同步。advisor 契约:始终返回一个 Advice
(status=ok 或 error);LLM 失败由 advisor 内部捕获并返回 error Advice(无降级)。"""
from typing import Protocol
from .advice import Advice
from .heuristics import run_tactics, DEFAULT_TACTICS


class StrategyAdvisor(Protocol):
    def advise(self, snapshot: dict, dtype: str, findings: list) -> Advice: ...


class DecisionEngine:
    def __init__(self, advisor, tactics=None):
        self._advisor = advisor
        self._tactics = tactics if tactics is not None else DEFAULT_TACTICS

    def decide(self, snapshot, dtype, trigger="manual", snapshotRef=None) -> Advice:
        findings = run_tactics(snapshot, dtype, self._tactics)
        advice = self._advisor.advise(snapshot, dtype, findings)
        advice.trigger = trigger
        if snapshotRef is not None:
            advice.snapshotRef = snapshotRef
        return advice
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_engine.py`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/decision/engine.py BgsDataBridge/tools/bgs_advisor/tests/test_engine.py
git commit -m "feat(bgs-advisor): add DecisionEngine orchestration with injectable advisor"
```

---

## Task 7: `config.py` — load/save + API-key masking

Loads config merged with defaults; masks the API key for frontend reads.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/config.py`
- Test: `BgsDataBridge/tools/bgs_advisor/tests/test_config.py`

**Interfaces:**
- Produces: `DEFAULTS` dict; `load(path) -> dict`; `save(path, cfg) -> None`; `mask(cfg) -> dict` (returns a copy with `apiKey` reduced to a presence hint, never the raw key).

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge/tools/bgs_advisor/tests/test_config.py`:

```python
import os, sys, tempfile, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import config


class TestConfig(unittest.TestCase):
    def test_load_missing_file_returns_defaults(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = config.load(os.path.join(d, "nope.json"))
            self.assertEqual(cfg["model"], config.DEFAULTS["model"])
            self.assertEqual(cfg["apiKey"], "")

    def test_save_then_load_roundtrip(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "c.json")
            config.save(p, {"apiKey": "sk-secret", "model": "claude-sonnet-4-6"})
            cfg = config.load(p)
            self.assertEqual(cfg["apiKey"], "sk-secret")

    def test_mask_hides_key(self):
        cfg = {"apiKey": "sk-secret", "model": "m", "baseUrl": "u",
               "autoTriggerPicks": True, "timeoutMs": 30000}
        m = config.mask(cfg)
        self.assertNotIn("sk-secret", str(m))
        self.assertTrue(m["hasApiKey"])
        self.assertNotIn("apiKey", m)

    def test_load_merges_defaults(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "c.json")
            config.save(p, {"apiKey": "x"})
            cfg = config.load(p)
            self.assertEqual(cfg["apiKey"], "x")
            self.assertEqual(cfg["timeoutMs"], config.DEFAULTS["timeoutMs"])


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_config.py`
Expected: FAIL with `ModuleNotFoundError: No module named 'config'`.

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/tools/bgs_advisor/config.py`:

```python
"""军师配置加载/保存 + API key 打码(前端只看 hasApiKey)。"""
import json
import os

DEFAULTS = {
    "baseUrl": "https://api.anthropic.com",
    "apiKey": "",
    "model": "claude-sonnet-4-6",
    "autoTriggerPicks": True,
    "timeoutMs": 30000,
}


def load(path: str) -> dict:
    cfg = dict(DEFAULTS)
    if path and os.path.exists(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                cfg.update(json.load(f))
        except Exception:
            pass  # 损坏 → 回退默认(对齐 spec §10)
    return cfg


def save(path: str, cfg: dict) -> None:
    merged = dict(DEFAULTS)
    merged.update(cfg)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(merged, f, ensure_ascii=False, indent=2)


def mask(cfg: dict) -> dict:
    """供前端读取:永不暴露原始 key。"""
    out = {k: v for k, v in cfg.items() if k != "apiKey"}
    out["hasApiKey"] = bool(cfg.get("apiKey"))
    return out
```

- [ ] **Step 4: Run test to verify it passes**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_config.py`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/config.py BgsDataBridge/tools/bgs_advisor/tests/test_config.py
git commit -m "feat(bgs-advisor): add config load/save + API-key masking"
```

---

## Task 8: `decision/llm_strategy.py` — cloud LLM advisor (IO)

Implements `StrategyAdvisor` via the Anthropic Messages API (`httpx.Client`, sync — the engine runs it in a background thread, satisfying the "don't block Flask" intent). Always returns an `Advice` (`ok` or `error`); never raises.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/decision/llm_strategy.py`
- Test: `BgsDataBridge/tools/bgs_advisor/tests/test_llm_strategy.py`

**Interfaces:**
- Consumes: `encode_prompt` (Task 4), `Advice`/`Action` (Task 2).
- Produces: `LlmStrategyAdvisor(baseUrl, apiKey, model, timeoutMs=30000)` with `.advise(snapshot, dtype, findings) -> Advice`. The HTTP call is isolated in `._call_llm(system, user) -> str` so tests monkeypatch it.

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge/tools/bgs_advisor/tests/test_llm_strategy.py`:

```python
import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.llm_strategy import LlmStrategyAdvisor


def snap():
    return {"match": {"turn": 5}, "player": {"tier": 3, "board": [], "hand": []}, "shop": {"offers": []}}


class TestLlmStrategy(unittest.TestCase):
    def test_advice_ok_parses_llm_json(self):
        adv = LlmStrategyAdvisor("https://x", "k", "m")
        adv._call_llm = lambda system, user: '{"rationale":"升本","actions":[{"kind":"LEVEL_UP"}]}'
        out = adv.advise(snap(), "ShopPhase", [])
        self.assertEqual(out.status, "ok")
        self.assertEqual(out.actions[0].kind, "LEVEL_UP")
        self.assertEqual(out.rationale, "升本")
        self.assertEqual(out.llm["model"], "m")
        self.assertIsNone(out.llm["error"])

    def test_advice_error_when_call_raises(self):
        adv = LlmStrategyAdvisor("https://x", "k", "m")
        def boom(s, u): raise RuntimeError("network down")
        adv._call_llm = boom
        out = adv.advise(snap(), "ShopPhase", [])
        self.assertEqual(out.status, "error")
        self.assertEqual(out.actions, [])
        self.assertIn("network down", out.llm["error"])

    def test_advice_error_when_json_unparseable_after_retry(self):
        adv = LlmStrategyAdvisor("https://x", "k", "m")
        adv._call_llm = lambda s, u: "not json at all"
        out = adv.advise(snap(), "ShopPhase", [])
        self.assertEqual(out.status, "error")


if __name__ == "__main__":
    unittest.main()
```

- [ ] **Step 2: Run test to verify it fails**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_llm_strategy.py`
Expected: FAIL with `ModuleNotFoundError: No module named 'decision.llm_strategy'`.

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/tools/bgs_advisor/decision/llm_strategy.py`:

```python
"""云端 LLM 策略器(Anthropic Messages API)。建议的唯一来源;失败 → error Advice(无降级)。

'异步'由调用方在后台线程跑 decide() 保证;此处用同步 httpx.Client。
_call_llm 被 isolate 以便单测 monkeypatch(不打真网络)。"""
import json
import time

import httpx

from .advice import Advice, Action
from .prompt import encode_prompt


class LlmStrategyAdvisor:
    def __init__(self, baseUrl, apiKey, model, timeoutMs=30000):
        self._baseUrl = baseUrl.rstrip("/")
        self._apiKey = apiKey
        self._model = model
        self._timeoutMs = timeoutMs

    def advise(self, snapshot, dtype, findings):
        system, user = encode_prompt(snapshot, dtype, findings)
        t0 = time.monotonic()
        try:
            raw = self._call_llm(system, user)
            actions, rationale = self._parse(raw)  # may raise → caught below
            status = "ok"
            error = None
        except _LLMError as e:
            actions, rationale, status, error = [], None, "error", str(e)
        except Exception as e:  # 网络/未知 → error(不降级)
            actions, rationale, status, error = [], None, "error", str(e)
        return Advice(
            decisionType=dtype, trigger="manual", snapshotRef={},
            status=status, actions=actions, rationale=rationale,
            llm={"model": self._model,
                 "latencyMs": int((time.monotonic() - t0) * 1000),
                 "tokensIn": None, "tokensOut": None, "error": error},
        )

    def _parse(self, raw: str):
        # 容忍模型在 JSON 前后夹带文字:抽取第一个 {...} 块。解析失败重试一次。
        try:
            return self._extract(raw)
        except _LLMError:
            # 重试一次,强化"只返回 JSON"
            raw2 = self._last_raw_retry(raw)
            return self._extract(raw2)

    def _extract(self, raw: str):
        s = raw.strip()
        i, j = s.find("{"), s.rfind("}")
        if i < 0 or j < 0 or j < i:
            raise _LLMError("LLM 未返回 JSON")
        obj = json.loads(s[i:j + 1])
        actions = [Action(kind=a["kind"], cardId=a.get("cardId"), name=a.get("name"),
                          index=a.get("index"), note=a.get("note"))
                   for a in obj.get("actions", [])]
        return actions, obj.get("rationale")

    def _last_raw_retry(self, original: str):
        # 简化:一次重试;若 _call_llm 本身不可重入,这里仍抛错由上层兜成 error
        try:
            return self._call_llm_retry()
        except Exception as e:
            raise _LLMError(f"JSON 解析失败且重试失败: {e}")

    # ---- IO 边界:以下方法在测试中被 monkeypatch ----
    def _call_llm(self, system: str, user: str) -> str:
        url = f"{self._baseUrl}/v1/messages"
        headers = {"x-api-key": self._apiKey,
                   "anthropic-version": "2023-06-01",
                   "content-type": "application/json"}
        body = {"model": self._model, "max_tokens": 1024,
                "system": system,
                "messages": [{"role": "user", "content": user}]}
        with httpx.Client(timeout=self._timeoutMs / 1000) as c:
            r = c.post(url, headers=headers, json=body)
            r.raise_for_status()
            data = r.json()
        # 取第一个 text block 的 text
        for block in data.get("content", []):
            if block.get("type") == "text":
                return block.get("text", "")
        raise _LLMError("LLM 响应无 text block")

    def _call_llm_retry(self) -> str:
        # 重试时无法重建原 prompt 上下文(无 state);直接失败。
        # 真正的"重试"是 advise() 层重新 _call_llm,此处保留接口给 _parse 的二次尝试。
        raise _LLMError("retry unavailable without re-invoking advise()")


class _LLMError(Exception):
    pass
```

Note on the retry design: `_parse` does a best-effort second extraction; a full retry (re-calling the API with a "return only JSON" nudge) would require re-invoking `advise`. For Stage 1, an unparseable response after the local re-extraction yields `status=error`, which is the correct no-fallback behavior. A genuine API-level retry can be layered in later without changing the interface.

- [ ] **Step 4: Run test to verify it passes**

Run: `python BgsDataBridge/tools/bgs_advisor/tests/test_llm_strategy.py`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/decision/llm_strategy.py BgsDataBridge/tools/bgs_advisor/tests/test_llm_strategy.py
git commit -m "feat(bgs-advisor): add Anthropic LLM strategy advisor (no fallback)"
```

---

## Task 9: `bgs_advisor.py` — Flask app (reuse bgs_viewer + advice routes)

Mounts the advisor on top of BgsViewer's modules. Webhook → `EventStore`; replay → `parse`; state → `reconstruct_state`. New routes: `POST /api/advise`, `GET /api/advice`, `GET/POST /api/config`. The engine runs in a background thread per advise request.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/bgs_advisor.py`

**Interfaces:**
- Consumes (via `sys.path` into sibling `bgs_viewer/`): `eventstore.EventStore`, `logparser.parse`, `stateview.reconstruct_state`. From this package: `decision.engine.DecisionEngine`, `decision.llm_strategy.LlmStrategyAdvisor`, `advicestore.AdviceStore`, `config` (load/save/mask), `decision.advice.Advice`.
- Produces: a runnable `python bgs_advisor.py [--port 5001] [--replay <file>] [--config <path>]` Flask app.

- [ ] **Step 1: Create the Flask app**

Create `BgsDataBridge/tools/bgs_advisor/bgs_advisor.py`:

```python
#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BgsAdvisor —— 酒馆战棋 AI 军师(BgsViewer 超集):实时 webhook / 复盘 + 云端 LLM 建议。"""
import argparse
import json
import os
import sys
import threading

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
except Exception:
    pass

from flask import Flask, request, jsonify, render_template

# 复用 BgsViewer 已构建的纯逻辑模块(兄弟目录)
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "bgs_viewer"))
from eventstore import EventStore          # noqa: E402
from logparser import parse                # noqa: E402
from stateview import reconstruct_state    # noqa: E402

sys.path.insert(0, HERE)
from decision.engine import DecisionEngine                       # noqa: E402
from decision.llm_strategy import LlmStrategyAdvisor             # noqa: E402
from advicestore import AdviceStore                              # noqa: E402
import config as advisor_config                                  # noqa: E402

app = Flask(__name__, template_folder=os.path.join(HERE, "templates"),
            static_folder=os.path.join(HERE, "static"))
store = EventStore()
advice_store = AdviceStore()
CONFIG_PATH = os.path.join(os.path.expanduser("~"), ".bgs_advisor_config.json")
RUNTIME = {"mode": "live", "engine": None, "cfg": None}


def _env_to_event(env, source):
    return {"seq": env.get("seq"), "type": env.get("event"), "at": env.get("at"),
            "match": env.get("match"), "data": env.get("data") or {}, "source": source}


def _make_engine():
    cfg = advisor_config.load(CONFIG_PATH)
    advisor = LlmStrategyAdvisor(cfg["baseUrl"], cfg["apiKey"], cfg["model"], cfg["timeoutMs"])
    return DecisionEngine(advisor), cfg


def _snapshot_ref(view, seq):
    match = (view or {}).get("match") or {}
    return {"seq": seq, "turn": match.get("turn"), "capturedAt": (view or {}).get("capturedAt")}


def _trigger_async(dtype, trigger, seq):
    """后台线程跑引擎(不阻塞 Flask)。结果入 AdviceStore。"""
    def run():
        events = store.all()
        # 定位触发点位:实时取末尾;复盘按 seq
        n = len(events)
        if seq is not None:
            n = min(n, max(1, sum(1 for e in events if (e.get("seq") or 0) <= seq)))
        view = reconstruct_state(events[:n]) or {}
        engine, _ = RUNTIME.get("engine") or (None, None)
        if engine is None:
            engine, _ = _make_engine()
            RUNTIME["engine"] = (engine, None)
        advice = engine.decide(view, dtype, trigger=trigger, snapshotRef=_snapshot_ref(view, seq))
        advice_store.put(advice)
    t = threading.Thread(target=run, daemon=True)
    t.start()


@app.route("/")
def index():
    return render_template("dashboard.html", mode=RUNTIME["mode"])


@app.route("/events", methods=["POST"])
def post_events():
    if RUNTIME["mode"] != "live":
        return jsonify({"error": "webhook disabled in replay mode"}), 404
    raw = request.get_data(cache=True)
    try:
        env = json.loads(raw.decode("utf-8")) if raw else {}
    except Exception:
        return jsonify({"error": "bad json"}), 400
    event = _env_to_event(env, "webhook")
    store.append(event)
    # Picks 自动触发
    cfg = RUNTIME.get("cfg") or advisor_config.load(CONFIG_PATH)
    RUNTIME["cfg"] = cfg
    etype = event.get("type")
    if cfg.get("autoTriggerPicks", True) and etype in ("HeroPick", "TrinketPick"):
        dtype = "HeroPick" if etype == "HeroPick" else "TrinketPick"
        _trigger_async(dtype, f"auto:{etype}", event.get("seq"))
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


@app.route("/api/mode")
def api_mode():
    return jsonify({"mode": RUNTIME["mode"], "lastIndex": len(store.all())})


@app.route("/api/advise", methods=["POST"])
def api_advise():
    body = request.get_json(silent=True) or {}
    dtype = body.get("decisionType")
    if dtype not in ("ShopPhase", "Positioning", "HeroPick", "TrinketPick"):
        return jsonify({"error": "bad decisionType"}), 400
    seq = body.get("atSeq")
    # 立即占位
    from decision.advice import Advice
    placeholder = Advice(decisionType=dtype, trigger="manual",
                         snapshotRef={"seq": seq}, status="thinking")
    advice_store.put(placeholder)
    _trigger_async(dtype, "manual", seq)
    return jsonify({"ok": True, "decisionType": dtype}), 202


@app.route("/api/advice")
def api_advice():
    dtype = request.args.get("decisionType")
    since_ts = request.args.get("sinceTs", type=int)
    if since_ts is not None:
        return jsonify([a.to_dict() for a in advice_store.since(since_ts)])
    a = advice_store.latest(dtype)
    return jsonify(a.to_dict() if a else None)


@app.route("/api/config", methods=["GET", "POST"])
def api_config():
    if request.method == "GET":
        cfg = RUNTIME.get("cfg") or advisor_config.load(CONFIG_PATH)
        RUNTIME["cfg"] = cfg
        return jsonify(advisor_config.mask(cfg))
    body = request.get_json(silent=True) or {}
    cfg = advisor_config.load(CONFIG_PATH)
    for k in ("baseUrl", "model", "autoTriggerPicks", "timeoutMs"):
        if k in body:
            cfg[k] = body[k]
    if body.get("apiKey"):  # 空串不改(前端无法读回原文,不主动清空)
        cfg["apiKey"] = body["apiKey"]
    advisor_config.save(CONFIG_PATH, cfg)
    RUNTIME["cfg"] = cfg
    RUNTIME["engine"] = None  # 强制下次用新配置重建引擎
    return jsonify(advisor_config.mask(cfg))


def main():
    ap = argparse.ArgumentParser(description="BgsAdvisor 酒馆战棋 AI 军师")
    ap.add_argument("--port", type=int, default=5001)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--replay", default=None)
    ap.add_argument("--config", default=None)
    args = ap.parse_args()

    global CONFIG_PATH
    if args.config:
        CONFIG_PATH = args.config
    cfg = advisor_config.load(CONFIG_PATH)
    RUNTIME["cfg"] = cfg
    RUNTIME["engine"] = None  # 懒构造(首次 advise 时建,用最新 cfg)

    print("BgsAdvisor 酒馆战棋 AI 军师")
    if args.replay:
        RUNTIME["mode"] = "replay"
        result = parse(args.replay)
        for e in result.events:
            store.append(e)
        print(f"  回放: 载入 {len(result.events)} 事件, 跳过 {result.skipped} 行")
    else:
        print(f"  实时: webhook 目标 http://localhost:{args.port}/events")
    print(f"  API key: {'已配置' if cfg.get('apiKey') else '未配置(请在设置区填)'}")
    print(f"  打开: http://localhost:{args.port}/")
    app.run(host=args.host, port=args.port, debug=False)


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Smoke-test the app boots and serves routes**

Run (from repo root):
```bash
python BgsDataBridge/tools/bgs_advisor/bgs_advisor.py --port 5099 --config /tmp/bgs_advisor_test.json &
sleep 2
curl -s http://127.0.0.1:5099/api/mode
curl -s http://127.0.0.1:5099/api/advice
curl -s -X POST http://127.0.0.1:5099/api/advise -H "content-type: application/json" -d "{\"decisionType\":\"ShopPhase\"}"
kill %1
```
Expected: `/api/mode` → `{"mode":"live","lastIndex":0}`; `/api/advice` → `null`; `POST /api/advise` → `{"ok":true,"decisionType":"ShopPhase"}` with HTTP 202. (The engine runs in a background thread; with no API key configured it will produce an `error` Advice that shows up on a subsequent `/api/advice` poll — that is the expected no-key behavior.)

- [ ] **Step 3: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/bgs_advisor.py
git commit -m "feat(bgs-advisor): add Flask app reusing bgs_viewer + advice routes"
```

---

## Task 10: Frontend ADVICE panel (copy bgs_viewer dashboard + extend)

The dashboard is a superset of BgsViewer's. Copy its templates/static as the base, then add a self-contained ADVICE section + its own 1s poller (independent of the existing event poller, so no coupling to bgs_viewer's JS internals) + a settings form for the API key.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/templates/dashboard.html` (copy from `bgs_viewer/templates/dashboard.html`, then insert the ADVICE section + settings)
- Create: `BgsDataBridge/tools/bgs_advisor/static/dashboard.js` (copy from `bgs_viewer/static/dashboard.js`, then append the advice module)
- Create: `BgsDataBridge/tools/bgs_advisor/static/dashboard.css` (copy from `bgs_viewer/static/dashboard.css`, then append advice styles)

- [ ] **Step 1: Copy BgsViewer's frontend as the base**

Run:
```bash
mkdir -p BgsDataBridge/tools/bgs_advisor/templates BgsDataBridge/tools/bgs_advisor/static
cp BgsDataBridge/tools/bgs_viewer/templates/dashboard.html BgsDataBridge/tools/bgs_advisor/templates/dashboard.html
cp BgsDataBridge/tools/bgs_viewer/static/dashboard.js BgsDataBridge/tools/bgs_advisor/static/dashboard.js
cp BgsDataBridge/tools/bgs_viewer/static/dashboard.css BgsDataBridge/tools/bgs_advisor/static/dashboard.css
```

- [ ] **Step 2: Insert the ADVICE section + settings form into `dashboard.html`**

Open `BgsDataBridge/tools/bgs_advisor/templates/dashboard.html` and insert this block immediately **after** the CURRENT-state section and **before** the ROUND TIMELINE section (find the timeline container in the copied file and place this above it):

```html
<section id="advice-section">
  <h2>AI 军师</h2>
  <div id="advice-controls">
    <button id="advise-shop" type="button">建议:商店期</button>
    <button id="advise-pos" type="button">建议:摆位</button>
    <span id="advice-status" class="advice-status">就绪</span>
    <span id="advice-freshness" class="advice-freshness"></span>
  </div>
  <div id="advice-banner"></div>
  <div id="advice-actions" class="advice-actions"></div>
  <div id="advice-rationale" class="advice-rationale"></div>
  <div id="advice-meta" class="advice-meta"></div>

  <details id="advice-settings">
    <summary>设置(API key / 模型)</summary>
    <label>API key <input type="password" id="cfg-apikey" placeholder="sk-ant-..."></label>
    <label>模型 <input type="text" id="cfg-model"></label>
    <label>Base URL <input type="text" id="cfg-baseurl"></label>
    <label><input type="checkbox" id="cfg-autopick"> 自动触发英雄/饰品建议</label>
    <button id="cfg-save" type="button">保存</button>
    <span id="cfg-msg"></span>
  </details>
</section>
```

- [ ] **Step 3: Append the advice module to `dashboard.js`**

Append to the end of `BgsDataBridge/tools/bgs_advisor/static/dashboard.js`:

```javascript
// ── BgsAdvisor:ADVICE 面板(自包含,独立 1s 轮询) ──────────────────────────
(function () {
  const KIND_LABEL = { BUY: "买", SELL: "卖", PLAY: "上场", REPOSITION: "调位",
    LEVEL_UP: "升本", REROLL: "刷新", FREEZE: "冻结", HERO_POWER: "技能",
    PLACE: "摆位", PICK_HERO: "选英雄", PICK_TRINKET: "选饰品" };
  let lastTs = 0;

  function postAdvise(dtype) {
    fetch("/api/advise", { method: "POST", headers: { "content-type": "application/json" },
      body: JSON.stringify({ decisionType: dtype }) });
  }
  document.getElementById("advise-shop").onclick = () => postAdvise("ShopPhase");
  document.getElementById("advise-pos").onclick = () => postAdvise("Positioning");

  function render(a) {
    const status = document.getElementById("advice-status");
    const actions = document.getElementById("advice-actions");
    const rationale = document.getElementById("advice-rationale");
    const meta = document.getElementById("advice-meta");
    const freshness = document.getElementById("advice-freshness");
    if (!a) { status.textContent = "就绪"; actions.innerHTML = ""; rationale.textContent = ""; meta.textContent = ""; return; }
    status.textContent = { thinking: "思考中…", ok: "●就绪", error: "✕错误" }[a.status] || a.status;
    status.className = "advice-status " + a.status;
    if (a.status === "error") { actions.innerHTML = ""; rationale.textContent = "⚠️ 云端不可用,未生成建议"; }
    else if (a.status === "thinking") { actions.innerHTML = ""; rationale.textContent = ""; }
    else {
      actions.innerHTML = (a.actions || []).map(act => {
        const lbl = KIND_LABEL[act.kind] || act.kind;
        const name = act.name || act.cardId || "";
        const idx = act.index != null ? `→位${act.index}` : "";
        const note = act.note ? ` <em>${act.note}</em>` : "";
        return `<div class="action-card"><span class="kind">${lbl}</span> ${name}${idx}${note}</div>`;
      }).join("");
      rationale.textContent = a.rationale || "";
    }
    const llm = a.llm || {};
    meta.textContent = a.status === "ok" && llm.model
      ? `来源: ${llm.model} · ${llm.latencyMs || "?"}ms` : "";
    const turn = (a.snapshotRef || {}).turn;
    freshness.textContent = turn != null ? `基于第${turn}回合` : "";
  }

  async function poll() {
    try {
      const r = await fetch(`/api/advice?sinceTs=${lastTs}`);
      const list = await r.json();
      if (Array.isArray(list) && list.length) {
        list.forEach(a => { if (a.ts > lastTs) lastTs = a.ts; });
        render(list[list.length - 1]);
      } else if (list === null) {
        render(null);
      }
    } catch (e) { /* 静默重试 */ }
  }
  setInterval(poll, 1000); poll();

  // ── 设置 ─────────────────────────────────────────────────────────────
  async function loadConfig() {
    const cfg = await (await fetch("/api/config")).json();
    document.getElementById("cfg-model").value = cfg.model || "";
    document.getElementById("cfg-baseurl").value = cfg.baseUrl || "";
    document.getElementById("cfg-autopick").checked = !!cfg.autoTriggerPicks;
    document.getElementById("cfg-apikey").placeholder = cfg.hasApiKey ? "(已配置,留空不改)" : "sk-ant-...";
  }
  document.getElementById("cfg-save").onclick = async () => {
    const body = { model: document.getElementById("cfg-model").value,
      baseUrl: document.getElementById("cfg-baseurl").value,
      autoTriggerPicks: document.getElementById("cfg-autopick").checked };
    const key = document.getElementById("cfg-apikey").value;
    if (key) body.apiKey = key;
    await fetch("/api/config", { method: "POST", headers: { "content-type": "application/json" }, body: JSON.stringify(body) });
    document.getElementById("cfg-msg").textContent = "已保存";
    loadConfig();
  };
  loadConfig();
})();
```

- [ ] **Step 4: Append advice styles to `dashboard.css`**

Append to the end of `BgsDataBridge/tools/bgs_advisor/static/dashboard.css`:

```css
/* ── BgsAdvisor ADVICE 面板 ── */
#advice-section { border-top: 1px solid #333; padding: 12px; margin-top: 8px; }
#advice-controls { display: flex; gap: 8px; align-items: center; margin: 8px 0; }
#advice-controls button { padding: 6px 12px; cursor: pointer; }
.advice-status.thinking { color: #5b9cff; }
.advice-status.ok { color: #4caf50; }
.advice-status.error { color: #e57373; }
.advice-actions { display: flex; flex-wrap: wrap; gap: 8px; margin: 8px 0; }
.action-card { background: #1e1e1e; border: 1px solid #444; border-radius: 6px; padding: 8px 12px; }
.action-card .kind { font-weight: bold; color: #e3b341; margin-right: 6px; }
.advice-rationale { color: #ccc; font-size: 14px; }
.advice-meta, .advice-freshness { color: #888; font-size: 12px; margin-top: 4px; }
#advice-banner { background: #2a2a1e; border-left: 3px solid #e3b341; padding: 8px; margin: 8px 0; }
#advice-settings { margin-top: 12px; color: #aaa; }
#advice-settings label { display: block; margin: 6px 0; }
```

- [ ] **Step 5: Verify the panel renders**

Run:
```bash
python BgsDataBridge/tools/bgs_advisor/bgs_advisor.py --port 5099 --config /tmp/bgs_advisor_test.json &
sleep 2
curl -s http://127.0.0.1:5099/ | grep -o "AI 军师"
kill %1
```
Expected: prints `AI 军师` (the ADVICE section is in the served HTML). Open `http://localhost:5099/` in a browser to visually confirm the ADVICE section + settings form appear below the CURRENT section.

- [ ] **Step 6: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/templates/dashboard.html BgsDataBridge/tools/bgs_advisor/static/dashboard.js BgsDataBridge/tools/bgs_advisor/static/dashboard.css
git commit -m "feat(bgs-advisor): add ADVICE panel + settings (bgs_viewer dashboard superset)"
```

---

## Task 11: README + end-to-end smoke test

Document usage, key configuration, the data-dependency story, and the Stage-2 deferrals. Record a reproducible end-to-end check.

**Files:**
- Create: `BgsDataBridge/tools/bgs_advisor/README.md`

- [ ] **Step 1: Write the README**

Create `BgsDataBridge/tools/bgs_advisor/README.md`:

````markdown
# BgsAdvisor — 酒馆战棋 AI 军师

BgsViewer 的超集:实时 webhook / 日志回放 + 云端 LLM 给**类型化行动建议**(军师,不自动操作)。

## 快速开始

```bash
pip install flask httpx
python bgs_advisor.py                         # 实时,默认 :5001
python bgs_advisor.py --replay ../bgs_viewer/bgs-events-*.log   # 复盘
```

浏览器打开 `http://localhost:5001/`。实时模式下把插件 webhook URL 设为 `http://localhost:5001/events`(与 receiver.py / bgs_viewer 并存,插件支持多 URL)。

## 配置 API key

页面右上"设置"区粘贴 Anthropic API key(存 `~/.bgs_advisor_config.json`,仅服务端使用,前端只读 `hasApiKey`)。或 `--config <path>` 指定配置文件。

## 怎么用

- **商店期 / 摆位**:对局中点[建议:商店期]或[建议:摆位],几秒后 ADVICE 区出动作卡 + 理由。`status=error`(云端不可用)时不显示建议(无降级)。
- **英雄/饰品选择**:收到 HeroPick/TrinketPick 事件时自动触发(可在设置关闭)。

## 数据依赖(Stage 1 现状)

| 决策 | Stage 1 | 说明 |
|------|---------|------|
| 战前摆位 | ✅ | 现有快照即可 |
| 商店期操作 | ⚠️ 定性 | 缺金币(Stage 2),LLM 不做预算分析 |
| 英雄/饰品选择 | ❌ Stage 2 | 插件未捕获可选项,payload 为 `{}` |
| 畸变 | ✅(需部署含 anomaly 捕获的插件) | 见插件 Task 1 |

## 架构要点

- 复用 BgsViewer 的 `eventstore` / `logparser` / `stateview`(兄弟目录 import)。
- 决策引擎 = 启发式提示 + 云端 LLM(建议唯一来源,无降级)。引擎在后台线程跑,不阻塞 Flask。
- 纯逻辑模块(`advice`/`heuristics`/`prompt`/`engine`/`advicestore`/`config`)全覆盖 `unittest`:`python tests/test_*.py`。

## 非目标

- 不做自动执行(ToS)。
- 不接 BobsBuddy(引擎接口已预留,v2)。
- Stage 2(金币 / picks 可选项)属另一份插件 spec。
````

- [ ] **Step 2: Run the full pure-logic test suite**

Run:
```bash
for t in BgsDataBridge/tools/bgs_advisor/tests/test_*.py; do python "$t" || break; done
```
Expected: every test file prints `OK`. (6 test files: advice, heuristics, prompt, engine, advicestore, config, llm_strategy.)

- [ ] **Step 3: End-to-end smoke (realtime, no key → error advice; with stub → ok)**

Run:
```bash
python BgsDataBridge/tools/bgs_advisor/bgs_advisor.py --port 5099 --config /tmp/bgs_advisor_test.json &
sleep 2
# 1) 无 key 触发 → 应得到 error advice(无降级)
curl -s -X POST http://127.0.0.1:5099/api/advise -H "content-type: application/json" -d "{\"decisionType\":\"ShopPhase\"}"
sleep 4
curl -s "http://127.0.0.1:5099/api/advice?decisionType=ShopPhase"
kill %1
```
Expected: the final `/api/advice` returns an object with `"status":"error"` and `"actions":[]` (no API key configured → LLM call fails → no fallback, per spec §5.2). This is the **negative-path success criterion** (spec §12.4).

- [ ] **Step 4: Commit**

```bash
git add BgsDataBridge/tools/bgs_advisor/README.md
git commit -m "docs(bgs-advisor): add README + Stage-1 data-dependency table"
```

---

## Self-Review (run after all tasks)

**Spec coverage** (spec `2026-06-22-bgs-advisor-design.md`):
- §1 目标(军师/双模/四类决策/引擎): Tasks 2–10. ✅
- §2 数据契约: consumed via `reconstruct_state` (Tasks 9). ✅
- §2.3 / §6 anomaly capture: Task 1. ✅
- §3 非目标(无降级/no-bot/no-BobsBuddy): enforced in Task 8 (error→no advice) + Global Constraints. ✅
- §4 架构(BgsViewer 超集/单引擎/后台线程): Tasks 6, 9. ✅
- §5 引擎(接口/schema/prompt/heuristics/触发): Tasks 2, 3, 4, 6, 8. ✅
- §6 插件依赖(anomaly=Stage1, gold/picks=Stage2 deferred): Task 1 + README table. ✅ (gold/picks correctly NOT implemented — Stage 2.)
- §7 GUI 面板 + 实时/复盘流: Tasks 9, 10. ✅
- §8 HTTP 路由: Task 9 (`/api/advise`, `/api/advice`, `/api/config`). ✅
- §9 配置/密钥: Task 7 + Task 9 `/api/config` + Task 10 settings. ✅
- §10 错误处理: Task 8 (LLM fail→error), Task 9 (bad json→400), config损坏→defaults (Task 7). ✅
- §11 测试: Tasks 2–8 unit tests; Task 11 runs suite. ✅
- §12 成功标准: 1(realtime) Task 11.3; 2(replay) Task 9 supports `--replay`; 3(picks) deferred Stage 2; 4(no-degradation) Task 11.3; 5(tests) Task 11.2; 6(Stage 2) deferred. ✅
- §13 文件结构: matches exactly. ✅
- §14 技术抉择: embodied throughout (flask/httpx sync in bg thread/cloud LLM/no-fallback/short-poll). ✅

**Placeholder scan:** none — every code step has complete code; the only "copy existing file" steps use literal `cp` commands (Tasks 10.1), which is concrete.

**Type consistency:** `DecisionEngine.decide(snapshot, dtype, trigger=, snapshotRef=)` (Task 6) matches the call in Task 9 (`engine.decide(view, dtype, trigger=..., snapshotRef=...)`). `LlmStrategyAdvisor(baseUrl, apiKey, model, timeoutMs)` (Task 8) matches Task 9 construction. `Advice(decisionType, trigger, snapshotRef, status=, actions=, rationale=, llm=)` (Task 2) matches all constructions (Tasks 6, 8, 9). `AdviceStore.put/latest/since` (Task 5) matches Task 9 usage. `run_tactics(snapshot, dtype, tactics=)` (Task 3) matches Task 6. `encode_prompt(snapshot, dtype, findings)` (Task 4) matches Task 8.

**Deferred (Stage 2, separate plugin spec):** gold capture, HeroPick/TrinketPick offered-choices. Not gaps — intentional.
