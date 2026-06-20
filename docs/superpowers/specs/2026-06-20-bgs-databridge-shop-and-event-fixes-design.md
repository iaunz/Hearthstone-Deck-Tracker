# BgsDataBridge —— 商店数据与事件修复 设计文档

- **日期**：2026-06-20
- **状态**：已通过设计评审，待实现
- **目标项目**：Hearthstone-Deck-Tracker-v2（fork，`v2` 分支）
- **范围**：针对一段真实对局 webhook 日志（`bgs-events.log`，事件 #27–#48，覆盖 turn 7→12）暴露的 4 类问题，做定点修复。不改 HTTP/webhook 传输层，不改 DTO JSON 契约（`bgs-state/v1` / `bgs-event/v1`）。

---

## 1. 背景与问题（来自日志分析）

运行日志显示插件核心管道（HTTP/webhook 投递、相位状态机、棋盘/英雄/对手/大厅捕获）健康，整局无一次 `partial:true`。但暴露出 4 类问题：

| # | 问题 | 日志证据 | 严重度 |
|---|---|---|---|
| 1 | **商店 offers 几乎永远拿不到；`tier` 恒为 0、`frozen` 恒为 null** | 6 个回合里只有 turn 11 的 `ShopPhaseStart`（#42）读到 7 个商品；其余 5 个 `ShopPhaseStart` 全是 `offers: []`；**所有** `ShopChanged` 的 offers 全空。`player.tier`/`shop.tier` 全程 `0`（玩家 turn 12 显然已是 5/6 本） | 🔴 主要 |
| 2 | **误触发 `HeroPick`** | turn 10→11 之间冒出一条 `HeroPick · turn 0 · 空 payload`（#41），英雄选择本只该开局触发一次 | 🟡 噪声 |
| 3 | **空载荷 `ShopChanged`** | 紧跟 `CombatPhaseStart` 之后 0.3s 出现 `data: {}` 的 `ShopChanged`（#30、#37） | 🟡 噪声 |
| 4 | **`MatchEnd` 快照缺 `player.hero`** | #48 的 `player` 无 `hero` 字段（其余快照都有） | 🟢 轻微 |

---

## 2. 可行性调研结论（HDT / HearthMirror 一手探查）

`lib/` 仅含 DLL（无源码），HearthMirror 为远程 ZIP 构建时下载。下表结论来自全树调用点枚举 + HDT 自身用法对照：

| 调研问题 | 结论 | 一手来源 |
|---|---|---|
| 玩家当前酒馆等级（tech level 1–6） | ✅ `Hero.GetTag(GameTag.PLAYER_TECH_LEVEL)`，HDT 自家 BobsBuddy 就这么读 | `Hearthstone Deck Tracker/BobsBuddy/BobsBuddyInvoker.cs:428`；tag 分发 `LogReader/Handlers/TagChangeActions.cs:145` |
| 商店随从读取器 | ⚠️ `GetOpponentBoardState()` 已是正确/标准读取器（HDT 随从钉子覆盖层用同一个）。**无**更好的方法。问题在"何时捕获" | `Hearthstone Deck Tracker/Hearthstone/Watchers.cs:346-353,406,539` |
| 商店实时变更事件 | ✅ `Watchers.OpponentBoardStateWatcher.Change` 公开可订阅，内存变更即触发（发牌/刷新/购买） | `HearthWatcher/OpponentBoardStateWatcher.cs:24`；启动于 `SceneHandler.cs:100` |
| 商店冻结状态 | ❌ 全树（HDT / HearthWatcher / HearthMirror 调用点）无任何暴露。需上游 HearthMirror 新版本 | 无 |
| 英雄选择稳定信号 | ✅ `GameEntity.GetTag(GameTag.STEP) <= (int)Step.BEGIN_MULLIGAN` 是 HDT 覆盖层用的硬门（游戏实体 tag，单调、常驻）；现用的 `IsBattlegroundsHeroPickingDone`（= 玩家实体 `MULLIGAN_STATE==DONE`）在玩家实体瞬时缺失时会误读 false | `GameEventHandler.cs:2080`；`GameV2.cs:234-245` |

---

## 3. 修复设计

### 3.1 酒馆等级（修问题 #1 的 tier 部分）

新增 `HdtGameSource.ReadTechLevel(GameV2 g) → int`：`SafeValue(() => g.Player.Hero?.GetTag(GameTag.PLAYER_TECH_LEVEL) ?? 0)`。

- `Capture()`：在取到 `v.Hero` 后置 `v.Tier = ReadTechLevel(g)` → 修好 `player.tier`（projector 已把 `v.Tier` 透传到 `BgsPlayer.Tier`，此前只是从没赋值）。
- `CaptureShop()`：`sv.Tier = ReadTechLevel(g)` → 修好 `shop.tier`。

> 说明：`shop.tier` 语义即"当前酒馆等级"（商店只出到你当前本数），与 `player.tier` 同源同值。

### 3.2 商店冻结（问题 #1 的 frozen 部分）

确认无 API。保持 `Frozen = null`，在 `CaptureShop()` 旁加注释 + 本 spec 记录。不阻塞发布。

### 3.3 商店 offers —— 事件驱动（修问题 #1 的 offers 部分 + #3）

**核心思路**：商店读取器没错，错在"捕获时机"。改用 HDT 自带的 `OpponentBoardStateWatcher.Change` 事件拿"最新商店快照"，但**所有判定/发送逻辑仍跑在游戏线程**，watcher 回调只做一次快照移交。

**订阅**（`OnLoad`）：

```csharp
Watchers.OpponentBoardStateWatcher.Change += OnShopBoardChange;
```

**watcher 回调**（watcher 后台线程，极简）：

```csharp
volatile List<BoardCard> _lastShopCards;
void OnShopBoardChange(object sender, OpponentBoardArgs args)
{
    _lastShopCards = args.BoardCards;   // 仅存引用；BoardCards 每次 fire 都是新不可变 list
}
```

- 零 `Core.Game` 访问、零逻辑、零枚举 → 完全规避并发修改风险，符合 CLAUDE.md"铁律"。
- 跨线程仅此一个 volatile 字段，写者 watcher 线程、读者游戏线程。

**`OnUpdate`（游戏线程 10Hz）承担全部判定与发送**：

1. 取 `_lastShopCards`（volatile 读）→ 映射 CardId → `ShopView { Tier = ReadTechLevel(g), Offers = [...] }`。
2. 纯函数门控 `ShopFeedPolicy.ShouldFeed(offerCount, inShopPhase)`：仅当 `inShopPhase && offerCount > 0` 才继续（**空商店不发** → 自然消除问题 #3 的空载荷噪声；战斗相跳过）。其中 `inShopPhase = g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase && !g.IsInMenu`（与现状 OnUpdate 守卫同源）。
3. 指纹 = `$"{turn}:{tier}:{cardIds}"`；与 `_lastShopFp` 相同则跳过（忽略纯悬停变更——`BoardCard` 含 `Hovered`/`MousedOverSlot`，但指纹只看 CardId）。
4. 变更则 `_shopDeb.Update(new ShopSnapshot { Shop = sv, Turn = turn, Phase = "Shop" }, _clock.NowMs)`。turn/phase/tier **此刻**（游戏线程）捕获，随快照一起 pending。
5. `_shopDeb.Tick()`。
6. **Shop→Combat 切换时**：`_shopDeb.Reset()` + `_lastShopCards = null` + `_lastShopFp = null`（丢弃未发完的 pending，杜绝战斗开始时的陈旧/空发）。

**去抖器发送"pending 快照"，不再"emit 时重新捕获"**——这是 offers 丢失的真正修复点。`OnEmit(ShopSnapshot snap)` → 投影成 `BgsShop` → 构造 `{ shop, turn, phase }` webhook data → 入队 `WebhookDispatcher`（TryAdd，非阻塞，与现状一致）。

**删除**旧的重捕获链路：`CaptureShopOnly()`、`ShopFingerprint()`、旧 `ShopData()`。

**生命周期**：`OnUnload` 必须 `Watchers.OpponentBoardStateWatcher.Change -= OnShopBoardChange;`。与 `GameEvents.*` 不同（HDT 靠栈归因自动解绑禁用插件的处理函数），`Watchers.*.Change` 是普通 C# 事件，不自动解绑——不解绑则禁用→启用会重复订阅、重复触发。

> `ShopPhaseStart` 自身的 `shop` 字段保持现状（边沿触发瞬间商店内存可能未填充，仍可能空）。**权威 offers 由紧随其后的 `ShopChanged` 投递**（发牌后 watcher 触发 → 去抖窗口内发出真实内容）。契约不变，spec 记录此语义。

### 3.4 HeroPick 硬门（修问题 #2）

新增纯函数 `Core/HeroPickPhase.cs`：

```csharp
static bool IsActive(bool isBattlegrounds, bool isInMenu, int step, int beginMulliganStep)
    => isBattlegrounds && !isInMenu && step <= beginMulliganStep;
```

- `step` 取自 `g.GameEntity?.GetTag(GameTag.STEP)`（游戏实体标量 tag，常驻、单调）；缺失 → `int.MaxValue` → 永不触发。
- `beginMulliganStep = (int)Step.BEGIN_MULLIGAN`。
- **不**做 audit 建议的"待选英雄枚举"——YAGNI：`STEP` 硬门已足以消除日志里的误触发，且避免在游戏线程 10Hz 枚举 `PlayerEntities`（并发修改竞争）。
- `HdtGameSource.DerivePhase`：把 `g.IsBattlegroundsHeroPickingDone == false ⇒ "HeroPick"` 替换为 `HeroPickPhase.IsActive(...)`，与事件侧一致。
- `BgsBridgePlugin.OnUpdate` 的 `TriggerInput.HeroPickActive` 同样改用 `HeroPickPhase.IsActive(...)`。

### 3.5 空 `ShopChanged`（问题 #3）

由 3.3 的 `ShopFeedPolicy.ShouldFeed(offerCount>0)` 自然解决——空商店不入队去抖器，永不发出空载荷。不另立修复。

### 3.6 MatchEnd 缺 hero（问题 #4）

`Capture()` 的英雄查找加兜底：若 `entities.FirstOrDefault(IsControlledBy(pid) && IsInPlay && IsHero)` 为空（对局结束清场），回退到 `entities.FirstOrDefault(IsControlledBy(pid) && IsHero)`（去掉 `IsInPlay`）。玩家只控制一个英雄实体，回退安全。仍为空则照旧省略字段。best-effort，仅构建+运行时验证。

---

## 4. 架构决策

| 决策 | 理由 |
|---|---|
| watcher 回调只做一次快照移交；判定/发送全在游戏线程 | 单向 volatile 字段，无需锁、无 `Core.Game` 跨线程读、去抖器无需改线程安全模型。严格符合"铁律" |
| 去抖器泛型化为 `ShopChangedDebouncer<T>` | `Core` 不得依赖 `Projector`；plugin 用 `ShopChangedDebouncer<ShopSnapshot>` |
| 两个新的纯函数缝 `HeroPickPhase` / `ShopFeedPolicy` | 把"何时触发"的判定逻辑从 HDT 集成层剥离出来单测覆盖；触达 `Core.Game` 的读取仍属集成层（构建+运行时验证，见 CLAUDE.md） |
| `STEP` 硬门而非待选英雄枚举 | 用最小、无竞争的信号修掉误触发；枚举的额外鲁棒性不值得 10Hz 枚举成本 |
| 手动解绑 watcher 事件 | 普通 C# 事件无栈归因自动解绑 |

### 去抖器签名变更

```csharp
public class ShopChangedDebouncer<T>
{
    public ShopChangedDebouncer(long quietMs, IClock clock);
    public event Action<T> OnEmit;
    public void Update(T payload, long nowMs);   // 存最近 pending（非"重新捕获"）
    public void Tick();
    public void Flush();                          // 阶段切换/卸载时立即发
    public void Reset();                          // 新增：清 pending（Shop→Combat）
}
```

现有 `ShopChangedDebouncerTest` 用 `<string>` 即可（行为不变：发"最近一次 Update 的值"），仅需加 `<string>` 泛型实参与新增 `Reset` 用例。

---

## 5. 测试策略（遵循 CLAUDE.md：纯逻辑 TDD，集成层靠构建+运行时）

**单元测试（TDD，纯逻辑）**

- `ShopChangedDebouncer<T>`：现有 4 例适配泛型签名；新增 `Reset_Clears_Pending`。
- `HeroPickPhaseTest`（新）：`STEP <= BEGIN_MULLIGAN` 且 BGS 且非菜单 → true；`STEP > BEGIN_MULLIGAN`（问题 #2 场景）→ false；非 BGS / 菜单 → false；step 缺失（`int.MaxValue`）→ false。
- `ShopFeedPolicyTest`（新）：`offerCount > 0 && inShopPhase` → true；空商店 / 非购物相 → false。

**集成层（构建通过 + 运行时验证，无单测）**

- `HdtGameSource.ReadTechLevel` / tier 透传 / hero 兜底 / `STEP` 读取。
- watcher 订阅 + `_lastShopCards` 移交 + `OnUpdate` 判定链。
- 生命周期（订阅/解绑幂等；禁用→启用不重复触发）。

**运行时验证**：部署 DLL，开一局 BG，确认（a）`ShopChanged` 携带真实 offers、（b）`player.tier`/`shop.tier` 随升本变化、（c）比赛中段不再有 `HeroPick`、（d）无空载荷 `ShopChanged`。

---

## 6. 不在范围内

- **商店冻结状态**（无 API，待上游 HearthMirror）。
- **`TrinketPick` 事件**（spec §11 既有 deferred 项，需要 `ChoicesWatcher` 适配；本次不动）。
- **`ShopPhaseStart` 边沿商店填充竞争**（权威 offers 已由 `ShopChanged` 覆盖；不再为边沿快照单独延时捕获——YAGNI）。
- 任何 LLM/下游逻辑（插件只做数据管道）。

---

## 7. 变更文件清单

**插件源码**

- `BgsDataBridge/Projector/HdtGameSource.cs` — 新增 `ReadTechLevel`；`Capture()` 赋 `v.Tier` + hero 兜底；`CaptureShop()` 赋 `sv.Tier`；`DerivePhase` 改用 `HeroPickPhase`；删除 `CaptureShopOnly`。
- `BgsDataBridge/BgsBridgePlugin.cs` — 订阅/解绑 `OpponentBoardStateWatcher.Change`；`OnShopBoardChange`（仅移交快照）；重写 `OnUpdate` 商店段（读 `_lastShopCards` → `ShopFeedPolicy` → 指纹 → `Update(ShopSnapshot)`）；`TriggerInput.HeroPickActive` 改用 `HeroPickPhase`；删除 `ShopFingerprint`/旧 `ShopData`；`OnEmit` 由 `ShopSnapshot` 构造 webhook data；新增 Shop→Combat 的 `Reset`。
- `BgsDataBridge/Core/ShopChangedDebouncer.cs` — 泛型化为 `<T>`；新增 `Reset`。
- `BgsDataBridge/Core/HeroPickPhase.cs` — 新（纯函数）。
- `BgsDataBridge/Core/ShopFeedPolicy.cs` — 新（纯函数）。
- 新增内部小 DTO `ShopSnapshot { ShopView Shop; int Turn; string Phase; }`，放 `Projector/`（紧邻 `ShopView`）。去抖器 `ShopChangedDebouncer<T>` 中 `T` 对 Core 不透明，故 Core 不引用 `Projector`，无分层倒置；由 plugin（同时引 Core 与 Projector）实例化 `ShopChangedDebouncer<ShopSnapshot>`。

**测试**

- `BgsDataBridge.Tests/Core/ShopChangedDebouncerTest.cs` — 适配 `<string>` + 新增 `Reset` 用例。
- `BgsDataBridge.Tests/Core/HeroPickPhaseTest.cs` — 新。
- `BgsDataBridge.Tests/Core/ShopFeedPolicyTest.cs` — 新。

---

## 8. 验证记录（API 一手来源，供实现对照）

| 用途 | HDT 来源 |
|---|---|
| 酒馆等级 tag | `BobsBuddy/BobsBuddyInvoker.cs:428` (`playerGameHero.GetTag(GameTag.PLAYER_TECH_LEVEL)`) |
| 商店读取器 | `Hearthstone/Watchers.cs:539` (`GetOpponentBoardState()` → `OpponentBoardState { List<BoardCard> BoardCards, int MousedOverSlot }`) |
| 商店变更事件 | `Hearthstone/Watchers.cs:406` (`public static OpponentBoardStateWatcher OpponentBoardStateWatcher`)；`HearthWatcher/OpponentBoardStateWatcher.cs:24` (`Change` 事件) |
| STEP 硬门 | `GameEventHandler.cs:2080` (`GameEntity.GetTag(STEP) > (int)Step.BEGIN_MULLIGAN ⇒ 已过英雄选择`) |
| 旧信号定义 | `Hearthstone/GameV2.cs:234-245` (`IsBattlegroundsHeroPickingDone` = 玩家实体 `MULLIGAN_STATE==DONE`) |
