# BgsViewer — 酒馆战棋对局可视化仪表盘（设计）

- 日期：2026-06-20
- 状态：设计稿（待评审）
- 相关：[`2026-06-17-bgs-data-bridge-plugin-design.md`](./2026-06-17-bgs-data-bridge-plugin-design.md)（插件本体契约）、`BgsDataBridge/tools/receiver.py`（现有 webhook 接收器）

## 1. 目标

写一个本地 Web 仪表盘脚本，**可视化展示单场酒馆战棋对局的进度与全量状态**，满足：

1. **两种数据来源**，同一套渲染：
   - **实时**：内嵌 webhook 接收器（插件 POST 事件信封），事件随到随追加；
   - **回放**：读取已有日志文件（`.log` 文本或 `.jsonl` NDJSON），可拖动/播放。
2. **展示尽可能多的信息**：`bgs-state/v1` / `bgs-event/v1` 契约里插件实际填充的全部字段，外加完整原始 JSON 兜底。

## 2. 背景与约束

- 插件 `BgsDataBridge` 通过 webhook 推送事件信封 `{schema, seq, event, at, match, data}`（schema `bgs-event/v1`），并暴露 `GET /state`（默认 `http://localhost:5273/state`，已开 `CORS *`）返回当前完整快照 `BgsSnapshot`（schema `bgs-state/v1`）。
- 现有 `receiver.py` 把事件以清晰文本打印到终端 + 文本日志（`bgs-events-*.log`），可选 NDJSON 日志（`--jsonlog`）。它是**线性事件流**视图，无时间轴、无回合演进、无统计曲线。
- 插件**已自带一个简易 HTML 查看器**（`GET /` on :5273，每秒轮询 `/state`，仅渲染 board+shop），但无时间轴、无日志回放、字段不全。本工具是其上位替代。
- 文本 `.log` 文件**每个事件都内嵌完整 JSON**（`── data (json) ──` 块），机器生成、完整可靠；周围的人类可读装饰才有渲染瑕疵。因此 `.log` 可被稳健地解析。

### 数据契约要点（来自插件 DTO / Projector）

事件 `data` 按类型分三种形状：

| 事件类型 | `data` 形状 | 说明 |
|----------|-------------|------|
| `MatchStart` / `MatchEnd` / `ShopPhaseStart` / `CombatPhaseStart` | 完整 `BgsSnapshot`（含 `schema:"bgs-state/v1"`、`player`、`shop`、`lastOpponent`、`lobby`、`availableRaces`、`match`） | 阶段事件 |
| `ShopChanged` | `{ shop, turn, phase }`（`shop` = `BgsShop`，printed-base 数值） | 商店增量 |
| `HeroPick` / `TrinketPick` | `{}`（空） | 信号事件（offered-choices 尚未实现，见插件 spec §11） |

`BgsSnapshot` 字段树（仅列**插件实际填充**的字段；`dbfId`/`minion.golden`/`shop.frozen`/`zonePosition`/`cost`/`match.anomaly`/`match.gameUuid` 等为从不填充的死字段，不展示）：

- `match`：`gameType`、`isBattlegrounds`、`isDuos`、`spectator`、`phase`、`turn`、`rating{mmr?,duosMmr?}`
- `availableRaces`：`string[]`
- `player`：`name`、`tier`、`hero{health,armor?,cardId,name?}`、`heroPower{cardId,name?,text?}`、`trinkets[]{slot,cardId,name?,text?}`、`questReward?{cardId,name?,text?,progress?,total?}`、`board[]{cardId,name?,attack?,health?,keywords?}`
- `shop`：`available`、`tier`、`offers[]{...同 minion}`
- `lastOpponent`：`turn`、`hero?{cardId,name?}`、`board[]{...}`
- `lobby`：`players[]{name,heroCardId,accountId}`
- `minion.keywords` 取值：`TAUNT`、`DIVINE_SHIELD`、`POISONOUS`、`VENOMOUS`、`REBORN`、`WINDFURY`、`STEALTH`、`FROZEN`

信封 `match` 是匿名精简对象（仅 `gameType/isBattlegrounds/isDuos/spectator/turn`，无 `phase`/`rating`）；完整 `match` 在快照 `data.match` 内。

## 3. 非目标（YAGNI）

- 不做对局**建议/模拟**（BobsBuddy、AI 分析等）——仅展示。
- 不做多人/跨对局统计、历史胜率、MMR 趋势聚合。
- 不替换 `receiver.py`（与之并存；但可选地承担其 NDJSON 日志职责）。
- 不解析插件从不填充的死字段。
- 不做 Duos 专属视图（字段照常展示，但不为双人对局做特殊 2 人阵容布局）。

## 4. 架构

单进程 Flask 应用 + 浏览器前端。核心是**归一化事件存储 `EventStore`**：三种数据源都汇入同一事件列表，前端只用一套渲染/重建逻辑。

```
插件 Core.Game
   │ (a) webhook POST /events   (b) 浏览器→GET 5273/state   (c) 文件
   ▼                             ▼                            ▼
POST /events               /state 轮询(可选)           .log / .jsonl
   │                             │                            │
   └──────────────►  EventStore (内存)  ◄──────────────────────┘
                            │  归一化 event:{seq,type,at,match,data}
                            ▼
                  GET /api/events?since=N   (前端每 1s 短轮询)
                            ▼
            仪表盘：当前状态 + 回合时间轴 + 事件流 + Chart.js 曲线
```

- **(a) webhook（主实时源）**：离散事件构建时间轴——轮询 `/state` 无法重建事件边界，故时间轴依赖 webhook。
- **(b) `/state` 轮询（实时模式的可选新鲜度补充 / 零配置兜底）**：浏览器直接打插件 5273（CORS 已开），每 1–2s 取当前快照覆盖"当前视图"。
- **(c) 文件回放**：`logparser` 解析 `.log`（抽 JSON 块）或 `.jsonl`（NDJSON）。

## 5. 运行模式（CLI）

| 模式 | 命令 | 行为 |
|------|------|------|
| 实时 | `python bgs_viewer.py` | 启动 webhook 接收（默认 `:5000`，路径 `/events`）；前端追加事件、自动滚到最新。启动时打印提示：把 `http://localhost:5000/events` 加为插件 webhook 目标（与 receiver.py 并存，插件支持多 URL） |
| 回放 | `python bgs_viewer.py --replay <file>` | 载入 `.log`/`.jsonl`（自动识别）；前端显示播放条（▶/⏸/步进/速度/拖动）、回合可点跳转；不开 webhook |

通用参数：
- `--port`（默认 5000）、`--host`（默认 127.0.0.1）
- `--state-url`（默认 `http://localhost:5273`；实时模式下浏览器轮询其 `/state`，回放模式忽略）
- `--no-state-poll`（实时模式下关闭 `/state` 轮询，仅靠 webhook）
- `--jsonlog <path>`（可选；实时模式下把收到的事件另写 NDJSON，可替代 receiver.py 的日志职责）

## 6. 数据源与归一化

### 6.1 归一化事件模型

无论来源，统一为：

```python
Event = {
  "seq":   int,            # 信封 seq（webhook/jsonl 直带；.log 从 #NNN 头解析）
  "type":  str,            # "MatchStart" | "ShopPhaseStart" | ... | "MatchEnd"
  "at":    str | None,     # ISO-8601
  "match": dict | None,    # 信封精简 match
  "data":  dict,           # 完整 data（三种形状之一）；空事件为 {}
  "source": "webhook" | "log-jsonl" | "log-text"
}
```

`shape`（前端用，可由 `type` 直接推导，不存单独字段）：阶段事件=`full`、`ShopChanged`=`shop`、`HeroPick`/`TrinketPick`=`empty`。

### 6.2 实时（webhook）

Flask 路由 `POST /events`：
- 读取 body，`json.loads`；失败 → 回 400 并记一行错误日志，不污染 EventStore。
- 成功 → `EventStore.append(event, source="webhook")`，立即回 200（让插件不重试）。
- **不做签名校验**（本工具仅本地；如需可后续加 `--secret`，对齐 receiver.py 的 `X-BgsBridge-Signature` HMAC）。

### 6.3 实时（`/state` 轮询）

- 由**浏览器**直接发起（插件 CORS `*`），后端不代理。
- 默认每 1.5s 请求一次 `http://localhost:5273/state?text=1`（带 `text=1` 取随从文案）。
- 拿到的 `BgsSnapshot` 作为"当前视图"的**新鲜度覆盖**（见 §8.2）。请求失败（插件未运行/未启用）→ 顶栏显示"state 离线"徽章，不阻断 webhook 时间轴。
- `--no-state-poll` 关闭。

### 6.4 回放（文件）

`logparser` 提供：

- `parse_ndjson(path) -> list[Event]`：逐行 `json.loads`，坏行跳过并计数。
- `parse_text_log(path) -> list[Event]`：状态机扫描 `bgs-events-*.log`：
  - 事件头 `^\s+#(?P<seq>\d+)\s+(?P<type>\w+)\s+(?P<at>\S+)`；
  - 其后第一个 `── data (json) ──` 块 → 收集缩进 JSON 行 → `json.loads` 为 `data`；
  - `data: (空 payload)` → `data = {}`；
  - `match` 行（`render_match` 输出）**不解析**（人类可读、易碎），改由：回放时若该事件 `data` 是完整快照则用 `data.match`，否则 `match=None`（信封精简 match 仅用于时间轴标签，缺省可接受）。
- `parse(path)`：按扩展名/首行自动分发（`.jsonl`→NDJSON；否则当文本 `.log`）。两种解析器均为纯函数，可单测。

## 7. EventStore（纯逻辑，可单测）

```python
class EventStore:
    def append(self, event): ...           # 维护按 seq 有序、去重（同 seq 覆盖）
    def all(self) -> list[Event]: ...
    def since(self, seq: int) -> list[Event]: ...   # 支持 GET /api/events?since=N
    def last_seq(self) -> int: ...
```

- 线程安全（webhook 在 Flask 工作线程、回放在主线程预填）；用 `threading.Lock` 包住列表操作即可（事件量小，每局百来条）。
- `since()` 供前端短轮询增量拉取。

## 8. 前端

### 8.1 布局

```
┌ 顶栏：英雄·血量/护甲 · turn · phase · MMR · gameType · 种族 chips · [state 在线/离线] ┐
├─ CURRENT（最新/选中事件重建的快照）─────────────────────────────────────────────────┤
│  YOUR BOARD        │  SHOP(tier)       │  LAST OPPONENT (turn N)                    │
│  minion 卡片×N     │  offer 卡片×N     │  minion 卡片×N + 对手英雄名                │
│  英雄/英雄技能     │  （frozen 永远 null，不显示）                                   │
│  饰品 chips · 任务进度条（若有）· 大厅（8 人，可折叠）                            │
├─ ROUND TIMELINE + EVENT STREAM ────────────────────────────────────────────────────┤
│  R1● R2● R3● R4▶ ...   （回合 = turn；●=已完成 ▶=当前）                            │
│  事件流：#seq type at 一句话摘要（点击 → 详情抽屉显示完整 JSON）                    │
│  回放时：拖动条/▶⏸/步进/速度；实时时：自动追尾                                     │
├─ PROGRESSION（Chart.js，按回合聚合 CombatPhaseStart 快照）──────────────────────────┤
│  英雄血量曲线 / 阵容总攻击力曲线 / tier 阶梯                                       │
└───────────────────────────────────────────────────────────────────────────────────┘
```

### 8.2 "当前视图"重建算法（核心，纯逻辑可单测）

`reconstruct_state(events[:i]) -> snapshot_view`：对事件列表做 fold——

- 遇 `full` 形状事件 → 用其 `data`（完整快照）整体替换当前视图（board/hero/shop/opponent/races/lobby/match）；
- 遇 `shop` 形状事件 → 仅 `view.shop = data.shop`（并更新 turn/phase）；
- 遇 `empty` → 不变。

- **实时模式**：`i = 末尾事件`；若 `/state` 轮询开启且返回了更新鲜的快照（按 `capturedAt` 比较），则用 `/state` 快照覆盖当前视图。
- **回放模式**：`i = 拖动位置`；无 `/state`。

回合时间轴的"当前回合"取自当前视图的 `match.turn`。

### 8.3 随从卡片与关键字

- 卡片：`name`（缺则 `cardId`）、`attack/health`、`keywords` 徽章、金标（注：插件当前不填充 minion `golden`，故金标仅在 `cardId` 以 `_G` 结尾时由前端推断显示——标注为"推断"）。
- 关键字配色（前端常量 map）：
  - `TAUNT` 棕、`DIVINE_SHIELD` 金、`POISONOUS` 绿、`VENOMOUS` 紫、`REBORN` 青、`WINDFURY` 蓝、`STEALTH` 灰、`FROZEN` 冰蓝。
- `heroPower`/`trinket`/`questReward` 的 `text` 以悬浮 tooltip 展示。

### 8.4 详情抽屉

点击任意事件或随从 → 抽屉显示该事件**完整信封 JSON**（`schema/seq/event/at/match/data` 原文）。这是"展示所有信息"的兜底：任何没有专属 UI 的字段都在这里可见。

### 8.5 增量更新

前端每 1s `GET /api/events?since=<lastSeq>`；有新事件则并入并重渲染。事件量小，短轮询足够。SSE 留作后续升级（见 §12）。

## 9. HTTP 路由

| 方法 | 路径 | 用途 |
|------|------|------|
| GET | `/` | 仪表盘页面（`templates/dashboard.html`） |
| POST | `/events` | webhook 接收 → `EventStore.append`（实时模式） |
| GET | `/api/events` | `?since=N` 返回增量事件 JSON；回放模式返回预载全量 |
| GET | `/api/mode` | 返回 `{mode:"live"|"replay", stateUrl, statePoll:bool}` 供前端初始化 |

静态资源走 Flask `static/`。`GET /` 注入 `stateUrl`/`mode` 给前端 JS（模板变量）。

## 10. 技术抉择

| 决策 | 选择 | 备选（未选原因） |
|------|------|------------------|
| 后端 | **Flask**（`pip install flask`） | FastAPI（更重、需 uvicorn）；stdlib http.server（无模板/静态便利） |
| 实时推送 | **前端短轮询 1s** | SSE（多一点代码、断线处理）；WebSocket（依赖最重） |
| 图表 | **Chart.js（CDN）** | 原生 canvas（自写代码）；Chart.js 曲线/阶梯开箱即用 |
| 回放格式 | **`.log` + `.jsonl` 自动识别** | 仅 `.jsonl`（要求用户改 receiver.py 习惯；现有 `.log` 不可回放） |
| 与 receiver.py | **并存**（多 webhook URL） | 替换（破坏现有工作流） |
| 签名校验 | **暂不做**（仅本地） | HMAC（后续按需加 `--secret`） |

**联网依赖**：Chart.js 走 CDN，查看页面时需联网。若需离线，后续可 vendoring 单文件 `chart.min.js` 进 `static/`（列为未来项，不阻塞 v1）。

## 11. 文件结构

```
BgsDataBridge/tools/bgs_viewer/
  bgs_viewer.py        CLI + Flask app + 路由（薄）
  eventstore.py        EventStore（纯逻辑，可单测）
  logparser.py         parse_ndjson / parse_text_log / parse（纯逻辑，可单测）
  stateview.py         reconstruct_state(events[:i])（纯逻辑，可单测）
  templates/dashboard.html
  static/dashboard.js   渲染 + 短轮询 + Chart.js + 详情抽屉
  static/dashboard.css
  README.md            用法：实时/回放、与 receiver.py 并存、webhook 配置、字段说明
```

纯逻辑（`eventstore`/`logparser`/`stateview`）与 IO（Flask 路由、文件读写）分离，符合项目"纯逻辑核心全覆盖单测"约定。

## 12. 错误处理

- **webhook JSON 解析失败** → 回 400，记 stderr 一行，不入库。
- **`/state` 不可达**（插件未启）→ 顶栏"state 离线"徽章；webhook 时间轴照常。
- **回放文件不存在/格式错误** → 启动即报错退出，清晰提示。
- **NDJSON 坏行 / `.log` 解析失败的事件** → 跳过并计数，顶栏显示"跳过 N 行"；不整体失败。
- **前端短轮询失败** → 静默重试，顶栏"后端离线"徽章。

## 13. 测试

纯逻辑模块用 Python 标准库 `unittest`（零额外依赖，与 `receiver.py` 的 stdlib-only 风格一致）：

- `eventstore`：append/去重/有序、`since()` 增量、并发 append（线程安全）。
- `logparser`：
  - NDJSON 正常 + 坏行跳过计数；
  - 文本 `.log`：阶段事件抽 full JSON、`ShopChanged` 抽 `{shop,turn,phase}`、`HeroPick` 空 payload、多事件连续解析；
  - 用仓库里现有 `bgs-events-*.log` 作真实回归样本。
- `stateview.reconstruct_state`：fold 语义——full 替换、shop 增量、empty 不变；末尾位置 = 当前视图。

IO 层（Flask 路由、浏览器）靠构建通过 + 手动运行时验证（与插件 HDT 集成层一致的做法）。

## 14. 成功标准

1. `python bgs_viewer.py` 起服务，浏览器打开能看到实时对局：顶栏/三列当前状态/时间轴随事件追加。
2. `python bgs_viewer.py --replay bgs-events-*.log` 能用现有日志回放，拖动条可逐事件浏览，回合可点跳转。
3. 顶栏 + 三列 + 详情抽屉合起来覆盖契约内**所有插件实际填充字段**（§2 清单）。
4. Chart.js 三条曲线按回合正确聚合。
5. 纯逻辑模块单测全绿；`.log` 解析对现有日志样本回归通过。

## 15. 未来项（非本次范围）

- SSE 推送替换短轮询。
- Chart.js vendoring 离线化。
- webhook `--secret` HMAC 校验。
- Duos 专属布局。
- 跨对局统计 / MM 趋势。
- 把 offered-choices（HeroPick/TrinketPick payload）接入展示（依赖插件先实现该捕获）。
