# BgsDataBridge —— 棋盘/手牌/升本 变动事件 设计文档

- **日期**：2026-06-21
- **状态**：已通过设计评审，待实现
- **目标项目**：Hearthstone-Deck-Tracker-v2（fork，`v2` 分支）
- **范围**：在现有 `bgs-event/v1` 事件总线之上**新增 3 个状态型事件**——`BoardChanged`（玩家棋盘变动）、`HandChanged`（玩家手牌变动）、`TavernUpgraded`（升酒馆等级）。沿用现有 `ShopChanged` 的"10Hz 轮询 → 指纹 diff → 去抖 → 发完整新状态"范式，不改传输层。`bgs-state/v1` 仅做**加字段**（`player.hand`），保持向后兼容。

---

## 1. 背景与目标

当前插件只在**相位边界**（`MatchStart`/`ShopPhaseStart`/`CombatPhaseStart`/`MatchEnd`）携带玩家棋盘快照，购物阶段内的棋盘/手牌/升本操作**不产生任何事件**。下游（Web 面板 / LLM 决策）只能在回合开始/开打时看到棋盘，看不到中间过程。

本次新增 3 个事件，让消费方能近乎实时地观测：

1. **玩家棋盘变动**——买/卖/打出/挪动位置/出现 buff（圣盾、剧毒、攻血变化）。
2. **玩家手牌变动**——买到未打出、打出后手牌变化、战棋法术入手。
3. **升酒馆等级**——玩家花钱升本的瞬间。

### 设计决策（已与用户确认锁定）

| # | 决策点 | 结论 |
|---|---|---|
| 1 | 事件风格 | **状态型**（同 `ShopChanged`：一变就发完整新状态，插件不推断动作语义）。语义动作型（`MinionBought`/`Sold`/`Played`/`Repositioned`）**推迟到后续 spec**。 |
| 2 | 冻结（freeze） | **本轮舍弃**。`shop.frozen` 保持 `null`（数据源硬约束，见 §3）。 |
| 3 | 棋盘检测粒度 | **全量指纹**：有序 `(cardId, zonePosition, attack, health, keywords)`，覆盖增删/挪位/属性 buff。手牌用同粒度但**忽略顺序**（组合）。 |

两个实现细节按推荐默认值定：手牌包含**随从 + 战棋法术**（法术无攻血 → `null`）；`TavernUpgraded` 载荷为最小 `{from, to}`（turn 已在信封 `match` 中）。

---

## 2. 可行性调研结论（HDT / HearthDb 一手探查）

`lib/` 仅含 DLL（无源码），结论来自全树调用点枚举 + HDT 自身用法对照：

| 调研问题 | 结论 | 一手来源 |
|---|---|---|
| 玩家棋盘实体 | ✅ `g.Entities` 过滤 `IsControlledBy(pid) && IsInPlay && IsMinion`，已在 `HdtGameSource.Capture()` 读取 | `BgsDataBridge/Projector/HdtGameSource.cs:90-93` |
| 玩家手牌实体 | ✅ `Entity.IsInHand`（`Zone.HAND`）；战棋中手牌存放"买到未打出"的随从/法术，可读 | `Hearthstone Deck Tracker/Hearthstone/Entities/Entity.cs:115` |
| 酒馆等级 | ✅ `PLAYER_TECH_LEVEL` tag（现有 `ReadTechLevel`），单调递增、仅购物相变化 | `HdtGameSource.cs:273`；`GameV2.cs:496-502` |
| 等级变化的 GameEvents 钩子 | ❌ `GameEvents` 无 tier/tavern 事件；HDT 内部 `HandleBattlegroundsPlayerTechLevel` 不对插件暴露 → 只能**轮询 diff** | `Hearthstone Deck Tracker/API/GameEvents.cs`（无匹配项）；`GameEventHandler.cs:2322` |
| 商店冻结状态 | ❌ 全树无 `GameTag`、HearthMirror 不暴露。唯一可观测是"跨回合 offers 完全一致"的**推断**——本轮不做 | 全树 grep `SHOP_FROZEN`/`FREEZE` 无商店相关命中；spec `2026-06-17-...md:222,442` |
| 现成可订阅 watcher | ⚠️ `OpponentBoardStateWatcher` 仅商店（已在用）；`BaconWatcher` 只跟踪游戏模式选择；**无**棋盘/手牌/tier watcher → 必须 OnUpdate 轮询 | `HearthWatcher/BaconWatcher.cs:44-49` |

**结论**：棋盘/手牌/升本全部走"游戏线程 10Hz 轮询 + 指纹 diff"，与 `ShopChanged` 同构，无需新线程、无需新 watcher。

---

## 3. 新增事件（`bgs-event/v1` 加枚举，向后兼容）

`BridgeEventType` 新增 3 个成员（`Events/BridgeEventType.cs`）：

```csharp
[EnumMember(Value = "BoardChanged")] BoardChanged,
[EnumMember(Value = "HandChanged")] HandChanged,
[EnumMember(Value = "TavernUpgraded")] TavernUpgraded,
```

载荷（`data` 字段）：

| 事件 | `data` | 触发 |
|---|---|---|
| `BoardChanged` | `{ board: [BgsMinion], turn, phase }` | 棋盘指纹变化（去抖后） |
| `HandChanged` | `{ hand: [BgsMinion], turn, phase }` | 手牌指纹变化（去抖后） |
| `TavernUpgraded` | `{ from, to, turn, phase }` | `ReadTechLevel` 上升沿（边沿触发，**不去抖**） |

`board`/`hand` 元素复用现有 `BgsMinion`（`zonePosition`/`cardId`/`attack`/`health`/`keywords`/`text` 等）。手牌里的战棋法术 `attack`/`health` 为 `null`。

### 3.1 信封示例

`BoardChanged`：

```json
{
  "seq": 42,
  "event": "BoardChanged",
  "at": "2026-06-21T08:51:10.1234567+00:00",
  "match": { "gameType": "BattlegroundsSolo", "isBattlegrounds": true, "isDuos": false, "spectator": false, "turn": 3 },
  "data": {
    "board": [
      { "cardId": "BG26_529", "attack": 1, "health": 1, "keywords": [] },
      { "cardId": "BG29_611", "attack": 1, "health": 1, "keywords": ["DIVINE_SHIELD"] }
    ],
    "turn": 3,
    "phase": "Shop"
  }
}
```

`TavernUpgraded`：

```json
{
  "seq": 43, "event": "TavernUpgraded", "at": "...",
  "match": { "...": "...", "turn": 4 },
  "data": { "from": 2, "to": 3, "turn": 4, "phase": "Shop" }
}
```

---

## 4. DTO 加字段（`bgs-state/v1`，向后兼容）

`Dtos/BgsSnapshot.cs` 的 `BgsPlayer` 新增手牌（与 `board` 对称）：

```csharp
public class BgsPlayer
{
    // ... 现有字段 ...
    [JsonProperty("board")] public List<BgsMinion> Board { get; set; } = new List<BgsMinion>();
    [JsonProperty("hand")] public List<BgsMinion> Hand { get; set; } = new List<BgsMinion>();
}
```

- 这样全量快照（`MatchStart`/`ShopPhaseStart`/`CombatPhaseStart`/`MatchEnd`）也带 `player.hand`，与 `HandChanged` 载荷一致。
- 加字段属 JSON 向后兼容（宽松消费方忽略未知键；严格消费方需感知）。schema 字符串**保持 `bgs-state/v1`**，不升版本（详见 §11 风险）。

**附带修**：`HdtGameSource.Capture()` 现在对棋盘**未按 `ZONE_POSITION` 排序**（仅 trinkets 排了）。本轮顺带修正——棋盘按 `ZONE_POSITION` 升序，保证指纹的位置稳定性与快照可读性（战棋棋盘顺序对战斗有意义）。

---

## 5. 检测逻辑（全部门控在**购物相**）

### 5.1 购物相门控

`inShop = g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase && !g.IsInMenu`（与现有 `ShopChanged` 守卫同源，`BgsBridgePlugin.cs:183`）。

**战斗相不轮询棋盘/手牌/tier**：战斗中棋盘攻血随伤害持续变化，轮询会洪泛；战斗相的权威棋盘是 `CombatPhaseStart` 的快照。

### 5.2 一次快照，三处 diff

每次 `OnUpdate`（游戏线程 10Hz）做**一次** `g.Entities` 快照（复用 `CaptureEntitiesSnapshot` 的"物化 + 一次重试"模式），从中派生棋盘、手牌、tier，避免反复枚举：

- **棋盘**：`IsControlledBy(pid) && IsInPlay && IsMinion`，按 `ZONE_POSITION` 升序，逐个 `Clone()`。
- **手牌**：`IsControlledBy(pid) && IsInHand` 且 `(IsMinion || IsBattlegroundsSpell)`，`Clone()`（顺序无关，下文指纹内部排序）。
- **tier**：`HdtGameSource.ReadTechLevel(g)`。

> 该热路径捕获**不调用 HearthMirror**（商店仍走现有 watcher 驱动的 `_lastShopCards` 链路），仅枚举已物化的实体列表 + clone 少量实体（棋盘 ≤7、手牌 ≤~6），10Hz 成本可忽略。

### 5.3 指纹（纯函数，可单测）

新增 `Core/ZoneFingerprint.cs`：

```csharp
public static class ZoneFingerprint
{
    // 棋盘：有序（调用方已按 zonePosition 排序）。
    // cardId|zonePos|atk|hp|kw1;kw2  —— 关键词按字典序，null cardId 记作空串。
    public static string Board(List<Entity> orderedBoard);

    // 手牌：无序——内部按指纹分量排序后 join，顺序无关。
    public static string Hand(List<Entity> hand);
}
```

- 棋盘指纹含 `zonePosition` → **挪动位置**即变化（满足"调整棋盘"）。
- 含 `attack`/`health`/`keywords` → 圣盾/剧毒/攻血 buff 即变化（满足"全量"粒度）。
- 手牌忽略顺序（手牌内位置无战术意义）。

### 5.4 去抖（棋盘/手牌）

复用现有泛型 `ShopChangedDebouncer<T>`（`Core/ShopChangedDebouncer.cs`），各实例化一个：

- `_boardDeb = new ShopChangedDebouncer<ZoneSnapshot>(cfg.ShopChangedQuietMs, clock);`
- `_handDeb = new ShopChangedDebouncer<ZoneSnapshot>(cfg.ShopChangedQuietMs, clock);`

**复用现有 `ShopChangedQuietMs` 配置**——不新增配置项（棋盘/手牌与商店同属"购物相操作 settle 窗口"语义，一个窗口即可）。trailing-edge：变更静默 `quietMs` 后发最终态；同窗口多次操作只发一次。

> `ZoneSnapshot` 为简单 POCO：`{ List<Entity> Zone, int Turn, string Phase }`（在 `OnUpdate` 捕获时刻冻结 turn/phase，随 pending 走，**emit 时不重捕获**——复刻 `ShopChanged` 修复 offers 丢失的同款铁律）。

`OnEmit` 投影：`ZoneSnapshot` → `List<BgsMinion>`（复用 `GameStateProjector.ToMinion`，含 live tags）→ 构造 `{ board|hand, turn, phase }` → `Emit(...)`。

### 5.5 升本（tier）——边沿触发，不去抖

新增 `Core/TierEdgeTracker.cs`（纯逻辑，可单测）：

```csharp
public class TierEdgeTracker
{
    int _last = -1;
    // 返回 (from, to) 当且仅当 tier 严格上升；否则返回 null。首帧建立基线不触发。
    public (int from, int to)? Observe(int tier);
    public void Reset();  // OnGameStart 调用
}
```

- 仅严格上升才发（战棋 tier 不可能降）；相等/首帧/下降（异常）不发。
- **不去抖**：升本是一次明确动作，慢且稀有，边沿即发，避免延迟。
- `_tierEdge.Reset()` 在 `OnGameStart`（与现有 `_source.ResetMatchCache()` 同处）调用。

### 5.6 相位/对局切换时的 Reset

镜像现有商店 reset（`BgsBridgePlugin.cs:198-203`）：

- **Shop→Combat**（`!inShop` 分支）：`_boardDeb.Reset()` / `_handDeb.Reset()` + 清棋盘/手牌指纹缓存 → 丢弃未发完的 pending，杜绝战斗开始的陈旧/空发。
- **OnGameStart**：`_tierEdge.Reset()`（新对局 tier 从 0/1 起）；棋盘/手牌指纹缓存清空。

---

## 6. 线程安全（CLAUDE.md 铁律，不变）

- `OnUpdate` 跑在游戏线程；**读 `g.Entities` 前必须物化快照 + `Clone()`**（复用 `CaptureEntitiesSnapshot`）。
- watcher 回调（商店）仍只做"移交引用"，零 `Core.Game` 访问。
- emit 链路只入队 `WebhookDispatcher`（`TryAdd`，非阻塞），与现状一致。
- 棋盘/手牌/tier 的 pending 载荷都是**不可变**（已 clone 的 `Entity` 列表 + 快照时刻的 turn/phase）——`GameStateView` 之后全是不可变 DTO 的边界不变。

---

## 7. 组件改动清单

| 文件 | 改动 |
|---|---|
| `Events/BridgeEventType.cs` | +3 枚举成员（`BoardChanged`/`HandChanged`/`TavernUpgraded`） |
| `Dtos/BgsSnapshot.cs` | `BgsPlayer.Hand` 加字段 |
| `Projector/GameStateView.cs` | +`PlayerHand` 字段 |
| `Projector/HdtGameSource.cs` | `Capture()`：捕手牌 + 棋盘按 `ZONE_POSITION` 排序；新增聚焦热路径方法 `CapturePlayerZones()`（一次实体快照 → 棋盘+手牌+tier，**不调 HearthMirror**） |
| `Projector/GameStateProjector.cs` | `ProjectPlayer` 投影 `player.hand`（复用 `ToMinion`）；新增公开 `ProjectZone(List<Entity>, includeText)` 给 emit 链路用 |
| `Core/ZoneFingerprint.cs` | **新文件**，纯函数 `Board(...)`/`Hand(...)` |
| `Core/TierEdgeTracker.cs` | **新文件**，纯逻辑 `Observe/Reset` |
| `BgsDataBridge.Tests/` | 新增：指纹测试、tier 边沿测试、去抖 emit 测试、购物相门控测试 |
| `BgsBridgePlugin.cs` | `OnLoad`：建 `_boardDeb`/`_handDeb`/`_tierEdge` + 接 `OnEmit`；`OnUpdate`：§5 的 diff/去抖/边沿/reset 逻辑；`OnGameStart`：`_tierEdge.Reset()`；`OnUnload`：flush 两个新去抖器 |

> 不改：HTTP 层、`WebhookDispatcher`、`RouteDispatcher`、`ShopChanged` 现有链路、`bgs-state/v1` 版本字符串。

---

## 8. 测试策略

**单元测试（纯逻辑，全覆盖）**——`BgsDataBridge.Tests/`：

- `ZoneFingerprint`：无变化→指纹相等；增/删/挪位/属性 buff/关键词出现→指纹不等；手牌顺序无关（重排→指纹相等）。
- `TierEdgeTracker`：上升→返回 `(from,to)`；相等/首帧/下降→返回 null；`Reset` 后重建基线。
- 去抖 emit（复用现有 `ShopChangedDebouncer` 测试范式 + 假时钟）：trailing-edge 合并、静默窗口内多次变更只发最终态。
- 购物相门控：构造 `inShop=false`（战斗/菜单）时不喂棋盘/手牌。

**运行时验证（无单测，同 `HdtGameSource` 现状）**——编译 + HDT 手动加载，用 `tools/receiver.py` 观察一次真实对局的 webhook：确认买/卖/打出/挪位/升本分别产生对应事件、战斗相无洪泛、相位切换无陈旧发。

---

## 9. 不在本轮范围（推迟）

- **语义动作事件**（`MinionBought`/`Sold`/`Played`/`Repositioned`/`Rerolled`）——后续 spec（消费方需要"动作语义"时再做，需状态 diff 推断）。
- **商店冻结**（`shop.frozen`）——上游 HearthMirror 未暴露，保持 `null`。
- **战斗相棋盘实时变动**——权威快照仍是 `CombatPhaseStart`。
- **`bgs-state/v1` → v2**——本轮仅加字段，不升版本。

---

## 10. 风险与对策

| 风险 | 对策 |
|---|---|
| 10Hz 枚举 `g.Entities` 增加热路径成本 | 已物化快照 + 仅 clone 少数实体；HDT 自身枚举频率远高于此，可忽略 |
| 购物相棋盘/手牌频繁变动 → 事件洪泛 | trailing-edge 去抖（复用 `ShopChangedQuietMs`），同窗口多次操作只发最终态 |
| `player.hand` 加字段破坏严格 schema 消费方 | 向后兼容（加字段不删不改）；文档记录。如确需严格契约可后续升 v2 |
| tier 在异常对局中瞬时跳变导致误发 | `TierEdgeTracker` 仅严格上升才发；首帧/下降不发 |
| 棋盘排序改动影响现有快照消费方 | `ZONE_POSITION` 升序即视觉左→右顺序，是更正确的行为；风险低 |
