# BgsAdvisor — 酒馆战棋 AI 军师（设计）

- 日期：2026-06-22
- 状态：设计稿（待评审）
- 相关：
  - [`2026-06-17-bgs-data-bridge-plugin-design.md`](./2026-06-17-bgs-data-bridge-plugin-design.md)（插件本体契约）
  - [`2026-06-20-bgs-viewer-dashboard-design.md`](./2026-06-20-bgs-viewer-dashboard-design.md)（可视化仪表盘底座，本程序是其超集）
  - `BgsDataBridge/tools/receiver.py`（现有 webhook 接收器）

## 1. 目标

写一个**本地浏览器仪表盘 + AI 军师**，实时与复盘两种模式下，对酒馆战棋对局给出**类型化的行动建议 + 理由**，由玩家手动执行。满足：

1. **军师定位（非自动）**：AI 只输出"买 X / 卖 Y / 把 Z 放最右 / 升本 / 选英雄 A"这类建议，**程序绝不操作炉石客户端**（零 ToS / 封号风险，契合当前只读插件的能力边界）。
2. **两种模式，同一引擎**：
   - **实时陪练**：内嵌 webhook 接收器（插件 POST 事件信封），在决策时刻触发 AI；
   - **复盘**：读取 `.log` / `.jsonl`，回放到任意点位触发 AI 分析。
3. **四类决策**：商店期操作、战前摆位、英雄选择、饰品选择。
4. **决策引擎 = 启发式提示 + 云端 LLM**：确定性启发式把检测到的关键事实喂进 prompt，云端 LLM 作为建议的**唯一来源**产出最终动作列表 + 理由。

## 2. 背景与约束

### 2.1 数据契约（来自插件 DTO / Projector / HdtGameSource）

插件 `BgsDataBridge` 推送事件信封 `{schema:"bgs-event/v1", seq, event, at, match, data}`，10 种事件：`MatchStart` / `MatchEnd` / `HeroPick` / `TrinketPick` / `ShopPhaseStart` / `CombatPhaseStart` / `ShopChanged` / `BoardChanged` / `HandChanged` / `TavernUpgraded`。并暴露 `GET /state`（默认 `http://localhost:5273/state`，已开 `CORS *`）返回完整 `BgsSnapshot`（schema `bgs-state/v1`）。

事件 `data` 三种形状：

| 事件类型 | `data` 形状 |
|----------|-------------|
| `MatchStart` / `MatchEnd` / `ShopPhaseStart` / `CombatPhaseStart` | 完整 `BgsSnapshot` |
| `ShopChanged` | `{ shop, turn, phase }` |
| `HeroPick` / `TrinketPick` | `{}`（空 —— 可选项未捕获，见 §6） |

`BgsSnapshot` 插件实际填充字段：`match{gameType,isBattlegrounds,isDuos,spectator,phase,turn,rating?}` / `availableRaces[]` / `player{name,tier,hero,heroPower,trinkets[],questReward?,board[],hand[]}` / `shop{available,tier,frozen?,offers[]}` / `lastOpponent{turn,hero?,board[]}` / `lobby{players[]}`。随从 `keywords` 取值：`TAUNT/DIVINE_SHIELD/POISONOUS/VENOMOUS/REBORN/WINDFURY/STEALTH/FROZEN`。

### 2.2 现有可复用资产

- `receiver.py`：已能收 webhook（HMAC 可选），清晰文本 + NDJSON 日志。
- **BgsViewer 设计稿**（Flask + 浏览器）：已设计 `EventStore` / `logparser` / `stateview.reconstruct_state` / 仪表盘 UI / 日志回放，但**明确把"AI 分析/建议"列为非目标**。本程序是其**超集**：可视化底座原样保留，AI 是叠上去的新一层（取消 BgsViewer 该非目标）。

### 2.3 已识别的数据缺口（影响建议质量，见 §6）

读插件源码确认：

- **畸变 Anomaly 未捕获**：DTO 字段 `BgsMatch.Anomaly` 与 View 字段 `GameStateView.Anomaly` 都存在，但 `HdtGameSource.Capture()` 从未赋值（死字段）。HDT 已有现成 API：`BattlegroundsUtils.GetBattlegroundsAnomalyDbfId(g.GameEntity)`（读 `GameTag.BACON_GLOBAL_ANOMALY_DBID`）+ `Database.GetCardFromDbfId(dbfId)?.Id`（BobsBuddy 同款用法）。补一行 capture + 一行 Projector 映射即可。
- **金币 Gold 全链路缺失**：DTO/View/Capture/Projector 都没有；HDT 主程序未找到现成 BG 金币 API（命中的 `Coins` 全是佣兵战纪）。数据源待定位（`GameTag.BACON_*` / HearthMirror / Power 日志）。
- **HeroPick / TrinketPick 可选项未捕获**：payload 为 `{}`。

## 3. 非目标（YAGNI）

- **不做自动执行 / Bot**（ToS 铁律；也超出只读插件能力）。
- **不做"启发式兜底降级"**：云端不可用就不给建议（用户明确决策，见 §5.2）。
- 不替换 `receiver.py`（并存；插件支持多 webhook URL）。
- 不做多供应商路由、跨对局记忆（未来项）。
- v1 不接 BobsBuddy 战斗模拟（引擎接口预留，见 §5.4 / §15）。

## 4. 架构

单进程 Flask 应用 + 浏览器前端，BgsViewer 的超集。核心新增是 **AI 决策层**：实时与复盘两种数据源汇入同一 `EventStore`，前端用同一套渲染，决策走同一个 `DecisionEngine`。

```
插件 Core.Game
  │ webhook POST /events (bgs-event/v1 信封)
  ▼
EventStore(内存,归一化事件列表) ──► reconstruct_state() ──► 当前快照视图
                                         │
              [按需:玩家点"建议"]   [自动:HeroPick/TrinketPick 事件]
                                         ▼
                          DecisionEngine.decide(snapshot, decisionType)
                                         │
                          ┌──────────────┴──────────────┐
                          ▼                              ▼
                  HeuristicTactics               LlmStrategyAdvisor
                  (同步,产 findings 提示)         (云端 API, 异步, 建议唯一来源)
                          │                              │
                          └───── findings 烤进 prompt ───┘
                                         │
                                         ▼
                       Advice { actions[], rationale, snapshotRef, status }
                                         │
                  前端每 1s 短轮询 /api/advice（与 /api/events 同轮询）──► ADVICE 区渲染
```

**复盘模式**：载入 `.log`/`.jsonl` → `EventStore` 预填 → 拖时间轴到点位 i → `reconstruct_state(events[:i])` → 玩家点[建议] → 同一 `DecisionEngine`。无 webhook、无 `/state` 轮询。

**关键统一原则**：实时与复盘**共用同一引擎 + 同一渲染**，只是快照来源不同（实时 = 事件末尾；复盘 = 拖动位置）。这是 BgsViewer"单一 EventStore + 单一 reconstruct"原则的延伸 —— 再加一个"单一 DecisionEngine"。

### 组件清单（v1）

- 复用 BgsViewer：`eventstore.py` / `logparser.py` / `stateview.py` / Flask 路由 / `dashboard.html` + `dashboard.js`。
- 新增：
  - `decision/engine.py` — `DecisionEngine` 编排（**接口可插拔**，v2 接 BobsBuddy）。
  - `decision/llm_strategy.py` — 云端 LLM 策略器（`httpx` 异步）。
  - `decision/heuristics.py` — 确定性战术启发式，产 findings 提示（纯逻辑）。
  - `decision/advice.py` — `Advice` / `Action` POCO + JSON 序列化。
  - `decision/prompt.py` — 快照 + findings → LLM prompt 编码（纯逻辑）。
  - `advicestore.py` — `AdviceStore`，按 `decisionType` 存最近建议（纯逻辑）。
  - `config.py` — 配置加载/保存，API key 服务端持有。
  - Flask：`POST /api/advise` / `GET /api/advice` / `GET,POST /api/config`。
  - 前端：ADVICE 区（动作卡 + 理由 + 状态 + 新鲜度）+ 设置区。

**铁律延续**：纯逻辑（engine 编排 / heuristics / prompt / advice / advicestore）与 IO（LLM HTTP / Flask / 文件）严格分离，纯逻辑全覆盖单测（对齐插件与 BgsViewer 约定）。LLM 调用必须**异步**，不阻塞 Flask 请求线程；建议未就绪时前端显示"思考中…"。

## 5. 决策引擎（核心）

### 5.1 决策类型与可插拔接口

```python
class DecisionType(Enum):
    HERO_PICK = "HeroPick"        # 一次性,自动触发
    TRINKET_PICK = "TrinketPick"  # 一次性,自动触发
    SHOP_PHASE = "ShopPhase"      # 高频,按需触发
    POSITIONING = "Positioning"   # 战前,按需触发

class DecisionEngine:                       # 编排者,纯逻辑(注入依赖)
    def decide(self, snapshot, dtype) -> Advice: ...

class StrategyAdvisor(Protocol):            # v1=LlmStrategyAdvisor; v2 可换 BobsBuddy 策略器
    async def advise(self, snapshot, dtype, hints: list) -> Advice: ...

class Tactic(Protocol):                     # 启发式插件接口
    code: str
    def evaluate(self, snapshot, dtype) -> TacticFinding | None: ...
```

### 5.2 建议唯一来源 = 云端 LLM（无降级）

**用户明确决策：云端不可用就不给建议。** 因此：

- 启发式**不独立产出建议、不对用户可见**，只把检测到的关键事实作为 `findings` 烤进 LLM 的 prompt（让 LLM 更知情、不漏战术）。
- 最终 `Advice` = LLM 输出（单一来源）。
- LLM 失败/超时 → `status="error"`，`actions=[]`，前端渲染错误占位（"⚠️ 云端不可用，未生成建议"）。**不做启发式兜底**。

### 5.3 建议输出 schema（`bgs-advice/v1`，前后端契约）

```json
{
  "schema": "bgs-advice/v1",
  "decisionType": "ShopPhase",
  "trigger": "manual" | "auto:HeroPick",
  "snapshotRef": { "seq": 123, "turn": 5, "capturedAt": "…" },
  "status": "thinking" | "ok" | "error",
  "actions": [
    { "kind": "BUY", "cardId": "CS3_014", "name": "雷鳞海妖", "note": "流派发动机" },
    { "kind": "SELL", "cardId": "LOOT_088", "name": "鱼人斥候" },
    { "kind": "REPOSITION", "cardId": "…", "index": 0, "note": "嘲讽靠左" },
    { "kind": "LEVEL_UP" }
  ],
  "rationale": "3本雷鳞流雏形已成,建议买雷鳞海妖并升本;卖掉不搭配的鱼人。嘲讽靠左吸收首击。",
  "llm": { "model": "…", "latencyMs": 2400, "tokensIn": 1800, "tokensOut": 200, "error": null }
}
```

`actions[].kind` 按决策类型约束：ShopPhase = `BUY/SELL/PLAY/REPOSITION/LEVEL_UP/REROLL/FREEZE/HERO_POWER`；Positioning = `PLACE`（带 `index`，给完整排序）；HeroPick = `PICK_HERO`；TrinketPick = `PICK_TRINKET`。

### 5.4 状态编码（快照 → LLM prompt）

不直接 dump JSON，编码成模型友好的紧凑文本：`board/hand/shop` 各列成 `name(atk/hp)[keywords]`，附 `turn/tier/availableRaces/anomaly/hero/heroPower/trinkets/questReward 进度`；`gold` 为阶段二依赖（见 §6），插件捕获后加入。System message 内嵌 BG 规则 primer + 角色；user message 末尾要求**结构化 JSON 输出**（走模型 JSON mode / tool calling，保证 `actions` 可解析）。`prompt.py` 是纯逻辑、可单测（断言关键字段齐全）。

### 5.5 启发式战术（v1 清单，确定性、零成本，仅作 prompt 提示）

每个 `Tactic` 产出 `TacticFinding{code, severity, message}`，烤进 prompt。v1 先做：

| code | 检查 |
|------|------|
| `golden-opportunity` | board+hand+shop 有 2 同名 → 可三连 |
| `taunt-positioning` | 嘲讽随从未靠左（Positioning 决策）|
| `freeze-value` | 商店有买不起的高价值随从 |
| `level-up-window` | 当前 tier/状态可安全升本 |
| `quest-progress` | 任务奖励接近完成 |

接口开放，后续可加。

### 5.6 触发与异步流程

- **Picks（HeroPick/TrinketPick）**：收到该事件 → 引擎自动跑一次 → 存 Advice → 前端弹横幅。一局一两次，便宜且必看。
- **ShopPhase / Positioning**：玩家点[建议] → `POST /api/advise {decisionType, atSeq?}` → **立即返回 202 + `status=thinking` 占位** → 引擎后台异步跑 → 前端每 1s 短轮询 `GET /api/advice` 直到 `status≠thinking`。
- **新鲜度**：每条 Advice 带 `snapshotRef`；若游戏已推进到更新回合，面板标"⚠️ 基于第 N 回合，可能过期"。

## 6. 插件扩展前置依赖（决定 v1 哪些决策可用）

### 6.1 数据就绪度矩阵

| 决策 | 当前数据 | 缺什么 | 插件改动量 |
|------|----------|--------|-----------|
| 战前摆位 | ✅ 够（board+hero 都在快照）| 无 | 零 |
| 商店期操作 | ⚠️ 半够 | 缺金币 → 只能定性建议 | 中 |
| 英雄选择 | ❌ | 缺可选英雄；payload `{}` | 中 |
| 饰品选择 | ❌ | 缺可选饰品；payload `{}` | 中 |
| *所有策略受益* | ❌ | 缺畸变（死字段）| **极小** |

### 6.2 插件扩展清单（4 项）

1. **捕获畸变 Anomaly** — 低风险，最优先。HDT 现成 API：`BattlegroundsUtils.GetBattlegroundsAnomalyDbfId(g.GameEntity)` + `Database.GetCardFromDbfId(dbfId, false)?.Id`；DTO/View 字段已存在，补 `HdtGameSource.Capture()` 一行赋值 + `GameStateProjector` 一行映射即可。**全决策类型立刻受益。**
2. **捕获金币 Gold** — 中风险，需源码调查。全链路缺失；HDT 无现成 BG 金币 API，需定位数据源（`GameTag.BACON_*` / HearthMirror / Power 日志）。属"集成层、靠运行时验证"。
3. **HeroPick 捕获可选项** — 中风险，需源码调查。捕获可选英雄列表 + 玩家所选；数据源待定（HeroPick 相位 entities 某 zone / HearthMirror）。建议同时把 `availableRaces` + `anomaly` 塞进 payload。
4. **TrinketPick 捕获可选项** — 同 3。

### 6.3 MVP 排序（可并行）

- **阶段一（零/极小插件改动，立刻能做）**：Positioning + ShopPhase 定性建议 + 畸变捕获（"一行"）。引擎/前端/启发式全可 TDD 开发。
- **阶段二（插件扩展并行推进）**：金币 → ShopPhase 升级"预算感知"；HeroPick/TrinketPick 可选项 → 解锁选择类建议。

### 6.4 Spec 分工

插件扩展属于 **BgsDataBridge 系列**，单独立一份 spec（如 `2026-06-22-bgs-databridge-capture-anomaly-gold-picks-design.md`），沿用现有插件 spec 的 TDD 约定。**本军师 spec 只声明数据依赖 + 假设契约**（HeroPick/TrinketPick payload 形状、`gold` 字段、`anomaly` 填充），把插件扩展当外部前置。

## 7. GUI 建议面板 + 数据流

### 7.1 布局

BgsViewer 原布局：顶栏 / CURRENT / TIMELINE+STREAM / PROGRESSION。军师在 **CURRENT 与 TIMELINE 之间**插入 **ADVICE 区**（"该怎么做"紧跟"现在是什么状态"）：

```
├─ ADVICE ──────────────────────────────────────────────────────────┤
│  [建议:商店期] [建议:摆位]   状态:●就绪 / ○思考中… / ✕错误        │
│  ┌ 动作卡 ┐ ┌ 动作卡 ┐ ┌ 动作卡 ┐   理由: ……（LLM 大局观文本）   │
│  │BUY 雷鳞│ │SELL 鱼人│ │LEVEL_UP│   来源: Claude · 2.4s · 2k tok │
│  │ 海妖   │ │ 斥候    │ │        │   基于第5回合 ✓新鲜             │
│  └────────┘ └────────┘ └────────┘                                 │
└───────────────────────────────────────────────────────────────────┘
```

- 动作卡**复用随从卡渲染**（name/stats/keywords），加动作图标。
- Picks 自动触发时，ADVICE 区顶上弹**横幅**（一局一两次，必看）。
- 状态三态：`thinking`（转圈）/ `ok` / `error`（"⚠️ 云端不可用，未生成建议"红标）。
- 新鲜度徽章：`snapshotRef.turn` vs 当前 turn；落后 → "⚠️ 基于第 N 回合，可能过期"。

### 7.2 实时模式数据流

```
插件 POST /events → EventStore.append → reconstruct_state → 当前视图
      │ (HeroPick/TrinketPick 事件)            │ (玩家点[建议])
      ▼                                         ▼
   自动触发 DecisionEngine                 手动触发
      │ 都走: 启发式→prompt→LLM(异步)→Advice|error
      ▼
   AdviceStore（按 decisionType 存最近一条,带 snapshotRef）
      ▲
前端每 1s GET /api/advice（与 /api/events 同一轮询）→ 渲染 ADVICE 区
```

自动触发失败（云端挂）→ 存 `status=error`，面板红标，不打扰（不弹窗）。手动触发 → `POST /api/advise` 立即回 202 + `thinking` 占位。

### 7.3 复盘模式数据流

```
载入 .log/.jsonl → EventStore 预填全量
玩家拖时间轴到点位 i → reconstruct_state(events[:i]) → 该时刻快照
玩家点[建议] → POST /api/advise {decisionType, atSeq: i_seq}
  → 引擎在该快照上跑（同一 DecisionEngine）→ Advice → 渲染
```

## 8. HTTP 路由（AI 层新增）

| 方法 | 路径 | 用途 |
|------|------|------|
| POST | `/api/advise` | body `{decisionType, atSeq?}` → 触发引擎，回 202 + `thinking` 占位 |
| GET | `/api/advice` | `?decisionType=&sinceTs=` 取最近建议 |
| GET/POST | `/api/config` | 读/改军师配置（API key 读取时打码）|

BgsViewer 原路由（`POST /events` / `GET /api/events` / `GET /api/mode` / `GET /`）原样保留。

## 9. 配置与密钥

- 配置项：LLM base URL / API key / model（v1 默认 Anthropic Claude）；自动触发开关；超时/重试。
- API key 存本地配置文件（gitignore），**只在服务端用于调 LLM**；前端读取打码、永不写日志、永不发给插件。
- 前端设置区可粘贴 key（localhost 工具，可接受）。

## 10. 错误处理

| 场景 | 行为 |
|------|------|
| LLM 调用失败/超时 | 有限重试 1 次；仍失败 → `status=error`，面板红标，无 actions（**不降级**）|
| LLM 返回 JSON 不合规 | 重试 1 次（强化"必须合法 JSON"提示）；仍失败 → error |
| API key 缺失/无效 | 首次触发即 error，提示"请先在设置配置 API key" |
| 快照 `partial=true` | 面板警告"快照不完整，建议质量可能下降"，仍允许触发 |
| webhook / 复盘文件错误 | 沿用 BgsViewer（400 不入库 / 启动报错 / 坏行跳过计数）|
| 配置文件损坏 | 回退默认 + 提示 |

## 11. 测试（纯逻辑 unittest，零依赖）

- `heuristics.py`：每个 Tactic 的 `evaluate`。
- `prompt.py`：给定快照+findings → 编码文本，断言关键字段齐全（board/hand/shop/tier/gold?/races/anomaly/hero/trinkets/quest）。
- `advice.py`：Advice/Action 序列化 round-trip；status 状态机。
- `engine.py`：编排（启发式→prompt→调用→advice；LLM 失败→error），用 mock `StrategyAdvisor` 注入成功/失败/超时三例。
- `advicestore.py`：按 decisionType 存最近、`sinceTs` 增量、线程安全（类比 EventStore）。
- IO 层（LLM HTTP / Flask / 前端）靠构建通过 + 手动运行时验证。

## 12. 成功标准

1. **实时**：起服务 + 配 key，对局中 `ShopPhaseStart` 后点[建议:商店期]，几秒内出动作卡 + 理由。
2. **复盘**：`--replay <log>` 拖到某回合点[建议]，在该时刻快照上出建议。
3. **Picks**（插件扩展后）：HeroPick/TrinketPick 自动弹横幅。
4. **不降级验证**：断网/关 key → 手动触发 → 面板"云端不可用"红标、无 actions。
5. 纯逻辑模块单测全绿。
6. **阶段二**（依赖插件扩展 spec）：畸变/金币/可选项字段正确进入 prompt 与 advice。

## 13. 文件结构

```
BgsDataBridge/tools/bgs_advisor/
  bgs_advisor.py        CLI + Flask app + 路由（薄）
  advicestore.py        AdviceStore（纯逻辑）
  config.py             配置加载/保存（API key 服务端持有）
  decision/
    engine.py           DecisionEngine 编排（纯逻辑,注入 mock 可测）
    llm_strategy.py     LlmStrategyAdvisor（IO,httpx 异步）
    heuristics.py       Tactic 集合（纯逻辑）
    advice.py           Advice/Action POCO + JSON（纯逻辑）
    prompt.py           快照+findings→prompt（纯逻辑）
  templates/dashboard.html   扩展 BgsViewer,加 ADVICE 区 + 设置区
  static/dashboard.js        加建议轮询 + 动作卡渲染
  static/dashboard.css
  tests/                unittest
  README.md             用法、配 key、与 BgsViewer/receiver.py 关系、数据依赖
```

**与 BgsViewer 的关系（实现期定的低风险细节）**：`eventstore/logparser/stateview` 三个纯逻辑模块在 BgsViewer spec 已定义；advisor 复用它们。两种做法 —— 把 `bgs_viewer/` 当可 import 的兄弟包（倾向），或把 3 个小模块拷进 `bgs_advisor/` 日后去重。不影响契约。

## 14. 技术抉择表

| 决策 | 选择 | 备选（未选原因）|
|------|------|----------------|
| 后端 | Python + Flask（复用 BgsViewer）| Node/C#（与现有 Python 工具链分裂）|
| LLM 调用 | httpx **异步** | requests（阻塞 Flask 线程）|
| LLM 来源 | 云端 API（Anthropic Claude，v1）| 本地（BG 推理弱）/ 多供应商（v1 过度设计）|
| 建议触发 | Picks 自动 + 商店/摆位手动 | 全自动（烧 token+延迟）/ 全手动（picks 易漏）|
| 引擎架构 | 启发式提示 + 单一 LLM（**无降级**）| 纯 LLM（弱）/ +模拟（v2）/ 启发式兜底（已否决）|
| 结构化输出 | LLM JSON mode / tool calling | 自由文本解析（脆）|
| 实时推送 | 前端 1s 短轮询（复用 BgsViewer）| SSE/WebSocket（过度）|
| 配置/密钥 | 本地配置文件，服务端持有 key | 环境变量（GUI 不便）|

## 15. 非目标 / 未来项

- **不做自动执行**（ToS 铁律）。
- **BobsBuddy 战斗模拟插槽**：引擎接口已预留，v2 接 C 方案 → 战前摆位质量跃升。
- 本地模型支持（LLM 接口抽象后可切）。
- 跨对局学习/记忆、多供应商按决策类型路由。
