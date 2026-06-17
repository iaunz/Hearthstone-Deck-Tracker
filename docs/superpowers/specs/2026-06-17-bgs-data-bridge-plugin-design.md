# BgsDataBridge —— 酒馆战棋数据桥接插件 设计文档

- **日期**：2026-06-17
- **状态**：已通过设计评审，待实现
- **目标项目**：Hearthstone-Deck-Tracker-v2（fork，`v2` 分支，.NET Framework 4.7.2 / WPF）
- **交付物**：一个 HDT 插件（单 DLL），把酒馆战棋对局状态以 HTTP 接口 + Webhook 形式暴露给本地下游消费方

---

## 1. 背景与目标

### 1.1 需求

基于 Hearthstone Deck Tracker（下称 HDT）构建一个插件，**聚焦酒馆战棋（Battlegrounds / Bgs）**，向本地下游提供实时对局数据：

1. 一个 **基于 HTTP 的 Web 接口**（拉取当前状态）。
2. 一个 **事件监听 → Webhook 推送**机制（在关键决策点把状态推给下游）。

下游消费方（均已确认）：

- **本地伴侣应用 / Web 面板**：实时展示战棋状态（轮询拉取）。
- **AI/LLM 助手分析**：在决策点把状态喂给 LLM 做战术建议（事件驱动）。

**架构边界**：插件**只做数据管道**——HTTP 拉取端点 + 事件 webhook 推送。LLM 调用逻辑、prompt、API key 全部放在独立的下游服务中。插件不耦合 LLM。

### 1.2 可行性调研结论（来自代码探查）

| 调研问题 | 结论 |
|---|---|
| HDT 是否支持插件 | ✅ 成熟机制：实现 `IPlugin`，反射加载，进程内全信任，无隔离 |
| 酒馆当前版本卡牌信息 | ✅ `HearthDb` + 远程 `CardDefs` XML + `BattlegroundsTagOverrides`，可解析任意 `BACON_*` 卡 ID 为本地化名称/描述/数值 |
| 玩家战棋阵容 / 对手 / 场上随从 | ✅ 本方阵容（日志）、当前战斗对手阵容（日志）、上一对手快照（日志）、大厅 8 人名单+段位（内存） |
| 英雄技能/饰品/任务描述 | ✅ 经 `Database.GetCardFromId(id).Text` 取本地化文本 |
| 其它 7 人实时血量/排名 | ❌ 日志不输出、HDT 所用内存 API 也不给——契约中不出现 |
| 商店（酒馆）售卖随从 | ⚠️ 仅"购物阶段 + 商店在屏"时可读（内存镜像） |
| 现有 HTTP/Webhook 基础设施 | ❌ 无任何监听端——全部为全新建 |

### 1.3 实现路线选型（已定）

选定**路线 A**：`HttpListener` + 轮询 + Webhook，Costura 单 DLL。

- `HttpListener` 为 .NET Framework 内置，零外部依赖、与 HDT 依赖零冲突；
- 战棋决策点本质低频，webhook 推送已覆盖 LLM 实时需求，面板 ≤1s 轮询无感；
- 最简、最快跑通全链路，未来可平滑迁移到 SSE。

被否的备选：路线 B（自托管 Kestrel + WebSocket/SSE，ASP.NET Core 进 .NET FW 4.7.2 进程版本冲突风险高、过度设计）；路线 C（HttpListener + SSE，收益有限）。

---

## 2. HDT 关键接入点（实现参考）

实现时需引用的 HDT 内部结构（均来自探查，路径相对仓库根）：

### 2.1 插件契约

`Hearthstone Deck Tracker/Plugins/IPlugin.cs`：

```csharp
public interface IPlugin
{
    string Name { get; }
    string Description { get; }
    string ButtonText { get; }
    string Author { get; }
    Version Version { get; }
    MenuItem MenuItem { get; }   // System.Windows.Controls.MenuItem；可返回 null
    void OnLoad();
    void OnUnload();
    void OnButtonPress();
    void OnUpdate();             // 约 10Hz
}
```

- 加载位置：`%AppData%\HearthstoneDeckTracker\Plugins\*.dll`（`PluginManager.PluginDirectory`）。
- 反射加载（`Assembly.LoadFrom`），公开非抽象类 + 无参构造。
- 无 API 版本门禁；HDT 自带 `MaxExceptions=100`、`MaxPluginExecutionTime=2000ms` 作为兜底。

### 2.2 运行时状态入口

- `Hearthstone_Deck_Tracker.API.Core.Game` —— 全局 `GameV2` 单例（与 `Hearthstone_Deck_Tracker.Core.Game` 同一对象）。
- `GameV2`（`Hearthstone Deck Tracker/Hearthstone/GameV2.cs`）关键字段：
  - `Player Player` / `Player Opponent`
  - `Dictionary<int, Entity> Entities` —— 当前对局**全部**实体
  - `bool IsInMenu`、`IsBattlegroundsMatch`、`IsBattlegroundsSoloMatch`、`IsBattlegroundsDuosMatch`、`IsBattlegroundsCombatPhase`、`IsBattlegroundsHeroPickingDone`
  - `Mode CurrentMode` / `PreviousMode`
  - `GameStats? CurrentGameStats`、`GameMetaData MetaData`（含 `BattlegroundsLobbyInfo`）、`BattlegroundsRatingInfo`、`BattlegroundsDuosBoardState`
  - `GetBattlegroundsBoardStateFor(int entityId)` —— 取上一对手快照
  - `List<BattlegroundsTrinketPickState>` —— 饰品选择记录
- `Player`（`Hearthstone Deck Tracker/Hearthstone/Player.cs`）关键视图：
  - `Hero`、`Trinkets`、`QuestRewards`、`Board`、`Minions`、`Hand`、`Deck`、`Secrets`
  - ⚠️ **`Player` 无 `HeroPower` 属性**；英雄技能需在 `Entities` 中扫 `CARDTYPE == HERO_POWER` 且 `IsControlledBy(player.Id)` 的实体解析（参考 `LogReader/Handlers/BoardStateWatcher.cs:413-415`）。`Player.HeroPowerChanged(Entity)` 是事件处理方法，非读取入口。
- `Entity`（`Hearthstone Deck Tracker/Hearthstone/Entities/Entity.cs`）关键成员：
  - `CardId`、`Tags`、`Attack`、`Health`、`IsMinion`、`IsHero`、`IsHeroPower`、`IsBattlegroundsTrinket`、`IsBgsQuestReward`、`IsInPlay`、`IsControlledBy(int)`、`GetTag(GameTag)`、`Clone()`

### 2.3 事件总线

`Hearthstone Deck Tracker/API/`（均为 `ActionList`，订阅 `.Add(handler)`，按调用方插件归属自动随禁用解绑）：

- `GameEvents`（`API/GameEvents.cs`）：`OnGameStart` / `OnGameEnd` / `OnGameWon` / `OnGameLost` / `OnGameTied` / `OnInMenu` / `OnTurnStart` / `OnModeChanged` 等。
- `DeckManagerEvents`（`API/DeckManagerEvents.cs`）。
- `LogEvents`（`API/LogEvents.cs`）：`OnPowerLogLine` / `OnBobLogLine` 等原始日志流。

> 注意：**没有**"商店阶段/战斗阶段切换"这类事件；需基于 `OnUpdate` 轮询标志位做边沿检测（见 §5）。

### 2.4 数据来源三件套

1. **日志解析**：`PowerTaskList`/`TagChange` → `Entity`（驱动 `Player`/`Opponent`/`Entities`）。
2. **HearthMirror 内存读取**（`lib/HearthMirror.dll`，`HearthMirror.Reflection.Client.*`）：
   - `GetBattlegroundsLobbyInfo()` —— 大厅 8 人（英雄卡/名字/账号/GameUuid）
   - `GetBattlegroundRatingInfo()` —— 本方 MMR/段位
   - `GetOpponentBoardState()` —— 酒馆战棋中复用为**商店当前售卖**（仅购物阶段 + 商店在屏）
   - `GetAvailableBattlegroundsRaces()` —— 本局可用种族
   - `GetBattlegroundsTeammateBoardState()` —— 双排队友阵容
   - `IsSpectating()`、`GetSpecialShopChoices()` 等
3. **HearthDb 静态库**（`lib/HearthDb.dll`）+ 远程 CardDefs：经 `Hearthstone Deck Tracker/Hearthstone/Database.cs` 的 `GetCardFromId(id)` 取 `Card.Text`/`LocalizedName`/`Name`。

### 2.5 线程模型（关键约束）

- 游戏状态由**后台日志线程 ~10Hz 写入**，`Entities`/`Player`/`Entity.Tags` 等集合**非线程安全、无锁**。
- `OnUpdate` 跑在**线程池线程**（非 UI 线程）。
- `GameEvents.*` 回调跑在**日志线程**。
- WPF 对象（`Core.OverlayWindow`/`MainWindow`/`OverlayCanvas`）只能 UI 线程触碰——本插件不直接读这些。
- 任何跨线程读 `Core.Game` 必须先快照（`.ToList()` / `Entity.Clone()`），见 §6.1。

---

## 3. 整体架构与组件

```
┌─────────────────────────────────────────────────────────┐
│  HDT 进程 (插件运行于此, 全信任)                          │
│                                                         │
│  BgsBridgePlugin (IPlugin)                              │
│    OnLoad  → 读配置 → 起 HttpServer → 装配触发检测+订阅    │
│    OnUnload → flush webhook → 停 HttpServer → 退订        │
│        │                                                │
│        ├── HttpServer  (HttpListener, 独立后台线程)       │
│        │     GET /            状态页(含最小查看器)         │
│        │     GET /health      存活 + 是否在对局            │
│        │     GET /state[?text=1&pretty=1]  完整快照 JSON   │
│        │                                                │
│        ├── GameStateProjector  ★核心                    │
│        │     读 Core.Game + HearthMirror → 投影成不可变    │
│        │     DTO (BgsSnapshot)。线程安全边界在此          │
│        │                                                │
│        ├── TriggerDetector                              │
│        │     OnUpdate(10Hz) 状态机边沿检测 + GameEvents    │
│        │     订阅 → 商店/战斗/英雄选/饰品选/商店变化/对局   │
│        │                                                │
│        └── WebhookDispatcher                            │
│              ConcurrentQueue + 专用发送线程               │
│              POST 事件 payload → 配置的下游 URL(可多个)     │
└─────────────────────────────────────────────────────────┘
          ↑ HTTP 拉取              ↑ Webhook 推送
    [本地 Web 面板]           [LLM 编排服务 / 面板后端]
```

### 组件职责（单一职责，各自可测）

| 组件 | 职责 |
|---|---|
| `BgsBridgePlugin` | `IPlugin` 实现。装配/拆卸生命周期。`MenuItem` 打开设置窗。 |
| `HttpServer` | 封装 `HttpListener`，路由分发，CORS，错误隔离。独立后台线程接受连接。 |
| `GameStateProjector` | **核心**。读 `Core.Game` + 必要内存镜像，快照+克隆，映射成不可变 POCO DTO。线程安全边界。 |
| `TriggerDetector` | `OnUpdate` 状态机边沿检测 + `GameEvents`/watcher 订阅，产出事件。 |
| `ShopChangedDebouncer` | 商店变化 trailing-edge 静默期合并（400ms）。 |
| `WebhookDispatcher` | 事件入队 `ConcurrentQueue`；专用发送线程 drain + 退避 + flush。 |
| `Config` | 读写 `config.json`；变更触发热重载。 |
| `Logger` | 独立日志文件，大小轮转。 |
| DTOs | `BgsSnapshot`/`BgsMinion`/`BgsCard`/`BgsShop`/`BgsEvent` 等纯 POCO，HDT 类型零外泄。 |

---

## 4. 数据契约

所有出参为纯 POCO，字段稳定、可版本化、可空字段显式标注。`schema` 字段标识版本。

### 4.1 状态快照 `GET /state` —— `bgs-state/v1`

```jsonc
{
  "schema": "bgs-state/v1",
  "locale": "zhCN",
  "capturedAt": "2026-06-17T15:30:00.123Z",
  "inMatch": true,
  "partial": false,                      // true 表示快照期间遇到并发修改,字段可能不完整

  "match": {
    "gameType": "BattlegroundsSolo",     // BattlegroundsSolo | BattlegroundsDuos | Constructed | Mercenaries | ...
    "isBattlegrounds": true,
    "isDuos": false,
    "spectator": false,
    "phase": "Shop",                     // HeroPick | TrinketPick | Shop | Combat | None
    "turn": 5,
    "gameUuid": "....",
    "rating": { "mmr": 8500, "duosMmr": null },
    "anomaly": { "cardId": "...", "name": "泥泞之地" }   // null 若无
  },

  "availableRaces": ["MURLOC", "DEMON", "MECHANICAL"],

  "player": {
    "name": "玩家名",
    "tier": 4,
    "hero":      { "cardId": "...", "dbfId": 12345, "name": "...", "health": 42, "armor": 5 },
    "heroPower": { "cardId": "...", "dbfId": 12346, "name": "...", "text": "<本地化描述>", "cost": 2 },
    "trinkets": [
      { "slot": "Lesser",  "cardId": "...", "name": "...", "text": "..." },
      { "slot": "Greater", "cardId": "...", "name": "...", "text": "..." }
    ],                                    // [] 若无
    "questReward": { "cardId": "...", "name": "...", "text": "...", "progress": 3, "total": 5 },  // null 若无
    "board": [
      { "zonePosition": 1, "cardId": "...", "dbfId": 7, "name": "...",
        "attack": 8, "health": 8, "golden": false,
        "keywords": ["TAUNT", "DIVINE_SHIELD"], "text": null }
    ]
  },

  "shop": {                               // 可空: 仅"购物阶段 + 商店在屏"时才有; 否则 null
    "available": true, "tier": 4, "frozen": null,   // frozen 尽力而为(可空): 需在实现期确认 GameTag 可靠性
    "offers": [
      { "slot": 0, "cardId": "...", "dbfId": 9, "name": "...",
        "attack": 3, "health": 3, "keywords": [], "text": null }
    ]
  },

  "lastOpponent": {                        // 最近一次战斗开始时的对手快照; null 若尚未战斗
    "turn": 4,
    "hero": { "cardId": "...", "name": "..." },
    "board": [ /* 同 player.board 结构 */ ]
  },

  "lobby": {                               // 大厅 8 人名单(内存); null 若未就绪
    "players": [ { "name": "...", "heroCardId": "...", "accountId": "..." } ]
  }
}
```

**卡牌描述 `text` 规则（已定）**：

- 英雄技能 / 饰品 / 任务奖励 / 异变：**始终含** `text`（数量少、效果关键，LLM 必须理解）。
- 随从 / 商店：默认 `text: null`，仅给 `keywords` 数组（`TAUNT`/`DIVINE_SHIELD`/`POISONOUS`/`REBORN`/`WINDFURY`/`CLEAVE` 等，派生自 `GameTag`）。
- `GET /state?text=1`：补全随从/商店全文 `text`。

**派生字段**：

- `golden`：由卡 ID（金色卡有独立 ID）+ `GameTag.BACON_TRIPLED_BASE_MINION_ID` 推断（HDT `Entity` 无现成 `IsGolden`）。
- `phase`：由 `CurrentMode` + `IsBattlegroundsCombatPhase` + 选择状态对象派生。
- `tier`：英雄 TECH_LEVEL / 当前酒馆等级。
- `keywords`：从 `GameTag` 映射（`TAUNT`/`DIVINE_SHIELD`/`POISONOUS`/`VENOM`/`REBORN`/`WINDFURY`/`CLEAVE`/`STEALTH`/`FROZEN` 等）。

**不在快照中出现的字段（确认拿不到）**：其它 7 人实时血量/排名、对手手牌（战棋无手牌）、商店关闭时的售卖内容。

### 4.2 Webhook 事件包络 —— `bgs-event/v1`

所有 webhook 共用一个信封 + 单调递增 `seq`（**每次插件加载从 0 起**，进程内单调；下游用于同一次运行内去重/排序）：

```jsonc
{
  "schema": "bgs-event/v1",
  "seq": 137,
  "event": "ShopPhaseStart",
  "at": "2026-06-17T15:30:00.123Z",
  "match": { /* 与 /state 的 match 段一致 */ },
  "data":  { /* 事件相关 payload */ }
}
```

事件类型与 `data`：

| event | 何时触发 | `data` 内容 |
|---|---|---|
| `MatchStart` | 对局开始 | 完整 snapshot |
| `MatchEnd` | 对局结束 | `{ result: "Win"\|"Loss"\|"Tied" }` + 完整 snapshot |
| `HeroPick` | 进入英雄选择 | `{ offered: [{cardId,name,...}], picked?: {cardId} }` |
| `TrinketPick` | 进入饰品选择 | `{ offered: [{cardId,name,text,tier,rarity,slot}], chosen?: {cardId} }` |
| `ShopPhaseStart` | 每回合进购物阶段（下降沿） | **完整 snapshot** |
| `CombatPhaseStart` | 每回合进战斗阶段（上升沿） | **完整 snapshot**（含 lastOpponent） |
| `ShopChanged` | 商店内容变化（含刷新），含 400ms 合并 | `{ shop: {...}, turn, phase }` 聚焦载荷 |

**载荷策略**：阶段/选择类事件（每回合 1 次）携带完整 snapshot，保证下游总有完整上下文；`ShopChanged`（一回合可能多次）只带聚焦载荷，避免刷爆下游。

**签名**（可选）：配了 `secret` 的 webhook，每个请求附 `X-BgsBridge-Signature: hmac-sha256(body, secret)`。

---

## 5. 事件流映射与触发检测

### 5.1 触发点检测落点

| 事件 | 检测方式 | 运行线程 |
|---|---|---|
| `MatchStart` / `MatchEnd` / `Won` / `Lost` | 订阅 `GameEvents.OnGameStart`/`OnGameEnd`/`OnGameWon`/`OnGameLost`/`OnGameTied` | 日志线程 |
| `ShopPhaseStart` | `OnUpdate`(10Hz) 轮询 `IsBattlegroundsCombatPhase`，**下降沿** true→false | 插件 update 线程 |
| `CombatPhaseStart` | 同上，**上升沿** false→true | 插件 update 线程 |
| `HeroPick` | `OnUpdate` 检测 `BattlegroundsHeroPickState.OfferedHeroDbfIds` 空→非空；`PickedHeroDbfId` 出现补 `picked` | 插件 update 线程 |
| `TrinketPick` | `OnUpdate` 检测 `GameV2` 的 `List<BattlegroundsTrinketPickState>` 长度增加 | 插件 update 线程 |
| `ShopChanged` | 商店阶段内轮询 `HearthMirror.Reflection.Client.GetOpponentBoardState()`，与上次比较；**含合并** | 插件 update 线程 |

**为何用 `OnUpdate` 边沿检测**：HDT `GameEvents` 无"阶段切换"事件，但 `GameV2` 有现成标志位；`OnUpdate` 已 10Hz、跑在线程池线程，跑轻量状态机最稳。所有边沿检测加 `IsBattlegroundsMatch && !IsInMenu` 门控，构造/佣兵/菜单中绝不误触。

### 5.2 `ShopChanged` 数据源（已定）

基线方案：商店阶段内直接调 `HearthMirror.Reflection.Client.GetOpponentBoardState()`，与上次比较，自包含且把合并逻辑握在自己手里。实现前做小 spike 验证 `Watchers.OpponentBoardStateWatcher.Change` 事件是否可被插件订阅——若可，可改为事件驱动以省一次轮询；**轮询方案作为可靠基线先定下**。

### 5.3 `ShopChanged` 合并算法（trailing-edge 静默期）

```
进入商店阶段后开始轮询：
  每次读到 shop 与 pending 不同 → 更新 pending.payload,记录 t_change
  OnUpdate 检查:若 t_change 距今 ≥ QUIET_MS(400ms) 且 pending 未发出 → 发出
  立即 flush 的特例:阶段切换 / 战斗开始 / 插件卸载 → 立刻发出当前 pending
语义:等商店稳定 400ms 再推最终态;一次刷新在 400ms 内多步变化只推一份最终结果。
```

---

## 6. 线程安全与错误处理

### 6.1 线程模型

```
日志线程(写 Core.Game) ──┐
                        │  共享读 Core.Game → 必须经 Projector 快照
插件 OnUpdate 线程(读) ──┤
                        │
HTTP 线程(读) ───────────┘  → Projector.Snapshot() → 不可变 DTO → 序列化(任意线程安全)

事件/检测线程 ──→ ConcurrentQueue.Enqueue(payload) ──→ [专用发送线程] ──→ POST 各 URL
                   (生产者绝不阻塞)                       (drain + 退避 + flush)
```

**铁律**：游戏线程上的回调（`OnUpdate`、`GameEvents.*`）只做"快照入队"立刻返回，**绝不发 HTTP**。HTTP 发送走独立发送线程。原因：① 不拖慢 HDT 主循环/日志解析；② 网络 I/O 失败不反压游戏线程；③ `OnUnload` 可对队列超时 flush 再退出。

**Projector 快照安全**：进入 `Snapshot()` 即把所需 `Entity` 用 `.ToList()`/`Entity.Clone()` 拷成自洽图、立即映射成 POCO DTO 后释放对 HDT 对象的引用。枚举处 try/catch `InvalidOperationException`（"集合已修改"）→ 重试至多 2 次；仍失败返回 `partial:true` 部分快照，而非 500。

### 6.2 错误处理（按组件隔离）

| 组件 | 失败处理 |
|---|---|
| `GameStateProjector` | 枚举异常重试 2 次；失败返回 `{partial:true, error}`，不抛 |
| HTTP handler | 全 try/catch → 500 JSON `{error}`，写日志，绝不抛回 HttpListener |
| `WebhookDispatcher` | **按 URL 隔离**：某 URL 失败不影响其它；4xx(非429)不重试，5xx/429/超时指数退避至 N 次后丢弃并记日志；队列设上限（防下游挂掉时内存爆） |
| `TriggerDetector` 状态机 | 异常计数；单组件连续失败超阈值→停该组件并日志，不影响其它（shop 读取坏，其它事件照常） |
| 插件生命周期 | `OnLoad` 幂等（重复调用先拆再装）；`OnUnload` 幂等 + flush 队列(超时如 3s) + 停 HttpListener；HDT `MaxExceptions=100` 兜底 |

### 6.3 HTTP 细节

- **绑定**：`http://localhost:<port>/`（**仅 localhost**，不用 `+`/`*`）。`localhost` 前缀在 Windows 上通常**无需管理员/防火墙**授权，不暴露公网。
- **路由**（v1 只读，不做控制端点）：`GET /`、`GET /health`、`GET /state[?text=1&pretty=1]`。返回 `application/json`。
- **CORS**：所有响应加 `Access-Control-Allow-Origin: *`，处理 `OPTIONS` 预检——浏览器里的 Web 面板（可能由别的端口/进程托管）能直接 fetch。
- **端口**：默认 `5273`，配置可改；启动失败（占用）自动 +1 重试若干次，实际端口写进日志和 `/health`。

---

## 7. 配置、打包与安装

### 7.1 配置

**位置**：`%AppData%\HearthstoneDeckTracker\Plugins\BgsDataBridge\config.json`（与日志同目录）。

```jsonc
{
  "enabled": true,                 // 总开关:false 时不起 HTTP、不推 webhook
  "port": 5273,                    // HTTP 端口;占用则自动 +1 重试
  "webhooks": [
    { "url": "http://localhost:8000/bgs",
      "events": ["*"],             // 或 ["ShopPhaseStart","CombatPhaseStart",...] 子集
      "secret": "可选共享密钥" }   // 用于签名校验,可省
  ],
  "shopChangedQuietMs": 400,       // 商店合并静默期
  "webhook": { "timeoutMs": 3000, "maxRetries": 4, "queueCap": 1000 },
  "logLevel": "Info"               // Info | Debug | Verbose
}
```

**编辑入口**：`IPlugin.MenuItem` 返回"配置…"菜单项，点开简易 WPF 设置窗（端口/webhook 列表/开关），保存后**热重载**（重启 HTTP、重订阅 webhook），无需重启 HDT。

**Webhook 签名**（可选）：配了 `secret` 则附 `X-BgsBridge-Signature: hmac-sha256(body, secret)`，下游可校验，防本机其它进程伪造。

### 7.2 打包

- **代码位置**：本仓库 sln 新增项目 `BgsDataBridge/BgsDataBridge.csproj`（类库，**.NET Framework 4.7.2**）。开发期项目引用 `Hearthstone Deck Tracker`，方便 attach 调试。
- **引用依赖**：`Hearthstone Deck Tracker.exe` + `HearthDb.dll` + `HearthMirror.dll` + `Newtonsoft.Json`（HDT 已加载，**Copy Local = False**，避免版本冲突——官方插件教程明确警告）。
- **打包**：Costura.Fody 内嵌插件自身的额外依赖；**排除** HDT 已有的依赖。产物：单个 `BgsDataBridge.dll`。
- **安装**：用户把 `BgsDataBridge.dll`（或 zip）放进 `%AppData%\HearthstoneDeckTracker\Plugins\`，在「Options → Tracker → Plugins」启用。
- **开发调试**：post-build 复制到 plugins 目录 → 跑 HDT → VS「附加到进程」`Hearthstone Deck Tracker.exe`；**单排/单机**对局调试（无计时器、无真人对手，官方教程同款建议）。

---

## 8. 测试策略

难点：插件强依赖实时 Hearthstone 状态（日志解析 + 内存镜像填充 `Core.Game`），无法对着真对局做单测。对策——**把可测逻辑从 HDT 依赖里剥离**：

| 可单测单元 | 怎么测 |
|---|---|
| `GameStateProjector` | 抽象 `IGameSource`；测试用手搓 `Entity`/`GameV2` 对象图喂入，断言投影出的 DTO（含 null 字段、keywords、golden 派生） |
| `ShopChangedDebouncer` | 纯逻辑 + 虚拟时钟：给定商店变化流，断言合并/发出时机 |
| `TriggerDetector`（阶段/选择边沿检测） | 喂 `IsBattlegroundsCombatPhase`/mode/pick 状态序列，断言触发的事件序列与门控（非 BGS/菜单不触发） |
| `WebhookDispatcher` | 假 `HttpMessageHandler` + 虚拟时钟：断言退避、按 URL 隔离、队列上限、flush |
| DTO 契约 | 固化若干示例 snapshot，断言 schema（字段存在性/可空性）——守住下游依赖的契约 |

**集成/手动验证**：构建 → 装进 plugins → 跑 HDT → 开一局**单人酒馆**（无计时器）；浏览器开 `http://localhost:5273/state` 看 JSON；本地起 webhook echo（十几行 Python/Node）观察 `ShopPhaseStart`/`CombatPhaseStart`/`ShopChanged`/选择事件在正确阶段触发。配合 HDT **Debug Window**（Options → Tracker → Settings → Debug）核对插件所见与 HDT 所见一致。

**测试项目位置**：随插件项目新增 `BgsDataBridge.Tests/BgsDataBridge.Tests.csproj`（xUnit/NUnit，加进 sln）。

---

## 9. 交付范围与边界

| 在范围内（本 spec） | 不在范围内（后续独立项目） |
|---|---|
| `BgsDataBridge` 插件：HTTP `GET` 拉取 + webhook 推送 + 配置/日志 | 真正的 Web 面板产品（UI/交互/历史） |
| 数据契约 `bgs-state/v1`、`bgs-event/v1` | LLM 编排服务（prompt、调 Claude、展示建议） |
| **最小参考查看器**：插件 `GET /` 提供轮询 `/state` 渲染当前面板的单页 HTML，兼作验证 + Web 面板种子 | 商店/阵容的"写"控制端点（v1 只读） |
| 单测 + 手动验证清单 | 自动化对局级集成测试（需真 HS 客户端，不现实） |

---

## 10. 已知风险与缓解

| 风险 | 缓解 |
|---|---|
| 跨线程读 `Core.Game` 偶发"集合已修改" | Projector 快照+克隆+重试+`partial` 标志 |
| `OpponentBoardStateWatcher.Change` 是否可被插件订阅未确认 | 轮询 `GetOpponentBoardState()` 作基线；spike 后再优化 |
| HDT 升级后内部 API 变更破坏插件 | 无版本门禁是固有问题；DTO `schema` 字段隔离下游；插件版本号显式 |
| 商店内容仅"商店在屏"可读 | 契约中 `shop` 显式可空；文档化 |
| 其它 7 人实时状态拿不到 | 契约中不出现该字段；明确告知下游 |
| 插件异常搞挂 HDT | 全路径 try/catch + 异常计数 + HDT `MaxExceptions` 兜底 |
| Webhook 下游挂掉导致内存爆 | 队列上限 + 按事件丢弃策略 |

---

## 11. 待实现时确认的开放项

1. `OpponentBoardStateWatcher.Change` 事件对插件的可订阅性（spike）。
2. `HeroPick`/`TrinketPick` 的精确进入时机：依赖 `ChoicesWatcher` 还是模式/状态对象变化——以单机酒馆实测为准。
3. 双排（Duos）下 `lastOpponent` 快照可能 stale（HDT 自身在 Duos 对手英雄未重解析时跳过快照）——契约中标注该字段在 Duos 下可能为空。
4. 插件项目放进本仓库 sln 还是独立仓库——本 spec 默认放进本仓库 sln；若希望独立分发，调整引用方式为 DLL 引用。
5. 商店 `frozen`（冻结）状态的可读性：确认对应 `GameTag` 可靠；若不可靠，该字段恒为 `null`（契约已允许）。
