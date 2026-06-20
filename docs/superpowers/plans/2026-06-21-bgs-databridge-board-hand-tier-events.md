# BgsDataBridge 棋盘/手牌/升本 变动事件 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 BgsDataBridge 插件新增 3 个状态型事件 `BoardChanged` / `HandChanged` / `TavernUpgraded`，沿用现有 `ShopChanged` 的 10Hz 轮询+指纹 diff+去抖范式。

**Architecture:** 游戏线程 `OnUpdate`（10Hz）里对玩家棋盘/手牌/酒馆等级做指纹 diff——棋盘/手牌变化喂入复用的 `ShopChangedDebouncer<ZoneSnapshot>`（trailing-edge 去抖），等级上升经 `TierEdgeTracker` 边沿触发。所有检测门控在购物相；跨线程只持已 `Clone()` 的不可变实体。纯逻辑（指纹/抽取/等级边沿）走 TDD 单测；HDT 集成层（`HdtGameSource`/`BgsBridgePlugin`）靠编译+运行时验证（与现有 `Capture()` 同策略）。

**Tech Stack:** C# 7.3 / .NET Framework 4.7.2 / WPF / x86 / MSTest。插件 Costura 单 DLL。

## Global Constraints

- 目标框架 **net472**、平台 **x86**、语言 **C# 7.3**。
- **必须用经典 VS MSBuild**（主工程/插件用自定义 ResGen，`dotnet build` 不行）；测试先 MSBuild 再 `dotnet test --no-build`。
- 插件对 HDT 依赖（`HearthDb`/`HearthMirror`/`HearthstoneDeckTracker`）保持 `<Private>false</Private>`。
- 线程安全铁律：游戏线程回调里只"快照+Clone 入队"，绝不直接发 HTTP；跨线程读 `Core.Game` 必先物化+`Clone()`。
- `bgs-state/v1` / `bgs-event/v1` schema 字符串**不升版本**（仅加字段/加枚举，向后兼容）。
- 遵循 DRY/YAGNI/TDD；每个任务结束提交一次（conventional commits，`feat(bgs-databridge):` / `test(bgs-databridge):`，末尾带 `Co-Authored-By: Claude <noreply@anthropic.com>`）。

### 构建与测试命令（全计划统一用这两条）

```bash
# 构建+跑测试（纯逻辑任务）
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo && dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj --no-build -p:Platform=x86
```

```bash
# 构建插件（集成层任务，确认整体编译通过）
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge/BgsDataBridge.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
```

### 设计依据

完整设计见 `docs/superpowers/specs/2026-06-21-bgs-databridge-board-hand-tier-events-design.md`。关键决策：状态型事件（非语义动作型）；冻结本轮舍弃；棋盘指纹全量（位置+属性+关键词）；手牌含随从+战棋法术、忽略顺序；等级边沿不去抖。

---

## File Structure

**新建（纯逻辑，TDD）：**
- `BgsDataBridge/Core/ZoneFingerprint.cs` — 棋盘/手牌稳定指纹（纯静态）。
- `BgsDataBridge/Core/TierEdgeTracker.cs` — 酒馆等级上升沿（纯逻辑）。
- `BgsDataBridge/Core/ZoneExtractor.cs` — 从实体快照过滤+排序出棋盘/手牌（纯静态，Clone 后返回）。
- `BgsDataBridge/Core/ZoneSnapshot.cs` — 去抖器 pending 载荷 POCO（`{ List<Entity> Zone; int Turn; string Phase }`）。
- `BgsDataBridge.Tests/Core/ZoneFingerprintTest.cs`
- `BgsDataBridge.Tests/Core/TierEdgeTrackerTest.cs`
- `BgsDataBridge.Tests/Core/ZoneExtractorTest.cs`

**修改：**
- `BgsDataBridge/Events/BridgeEventType.cs` — +3 枚举成员。
- `BgsDataBridge/Dtos/BgsSnapshot.cs` — `BgsPlayer.Hand` 字段。
- `BgsDataBridge/Projector/GameStateView.cs` — `+PlayerHand` 字段 + `PlayerZonesView` 类型。
- `BgsDataBridge/Projector/HdtGameSource.cs` — `Capture()` 捕手牌+棋盘排序；新增 `CapturePlayerZones()` 热路径方法。
- `BgsDataBridge/Projector/GameStateProjector.cs` — 投影 `player.hand`；公开 `ProjectZone(...)`。
- `BgsDataBridge/BgsBridgePlugin.cs` — `OnLoad`/`OnUpdate`/`OnGameStart`/`OnUnload` 接线。

---

## Task 1: ZoneFingerprint（纯逻辑，TDD）

**Files:**
- Create: `BgsDataBridge/Core/ZoneFingerprint.cs`
- Test: `BgsDataBridge.Tests/Core/ZoneFingerprintTest.cs`

**Interfaces:**
- Consumes: `Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity`（`CardId`/`GetTag`/`Attack`/`Health`）、`BgsDataBridge.Projector.KeywordMap.From(Entity)`、`HearthDb.Enums.GameTag`。
- Produces: `public static string ZoneFingerprint.Board(List<Entity> orderedBoard)`、`public static string ZoneFingerprint.Hand(List<Entity> hand)`。

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge.Tests/Core/ZoneFingerprintTest.cs`:

```csharp
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class ZoneFingerprintTest
    {
        static Entity M(string id, int zonePos, int atk, int hp)
        {
            var e = new Entity { CardId = id };
            e.SetTag(GameTag.ZONE_POSITION, zonePos);
            e.SetTag(GameTag.ATK, atk);
            e.SetTag(GameTag.HEALTH, hp);
            return e;
        }

        [TestMethod]
        public void Board_Stable_When_Unchanged()
        {
            var b = new List<Entity> { M("A", 1, 2, 3) };
            Assert.AreEqual(ZoneFingerprint.Board(b), ZoneFingerprint.Board(new List<Entity> { M("A", 1, 2, 3) }));
        }

        [TestMethod]
        public void Board_Changes_On_Reposition()
        {
            var a = new List<Entity> { M("A", 1, 2, 2), M("B", 2, 3, 3) };
            var b = new List<Entity> { M("A", 2, 2, 2), M("B", 1, 3, 3) };  // 交换位置
            Assert.AreNotEqual(ZoneFingerprint.Board(a), ZoneFingerprint.Board(b));
        }

        [TestMethod]
        public void Board_Changes_On_Buff()
        {
            var before = new List<Entity> { M("A", 1, 2, 2) };
            var after  = new List<Entity> { M("A", 1, 5, 5) };  // 攻血 buff
            Assert.AreNotEqual(ZoneFingerprint.Board(before), ZoneFingerprint.Board(after));
        }

        [TestMethod]
        public void Board_Changes_On_Keyword()
        {
            var plain = M("A", 1, 2, 2);
            var shielded = M("A", 1, 2, 2);
            shielded.SetTag(GameTag.DIVINE_SHIELD, 1);
            Assert.AreNotEqual(
                ZoneFingerprint.Board(new List<Entity> { plain }),
                ZoneFingerprint.Board(new List<Entity> { shielded }));
        }

        [TestMethod]
        public void Board_Changes_On_Add_Remove()
        {
            var one = new List<Entity> { M("A", 1, 1, 1) };
            var two = new List<Entity> { M("A", 1, 1, 1), M("B", 2, 1, 1) };
            Assert.AreNotEqual(ZoneFingerprint.Board(one), ZoneFingerprint.Board(two));
        }

        [TestMethod]
        public void Hand_Order_And_ZonePos_Invariant()
        {
            // 手牌忽略顺序与 zonePosition
            var a = new List<Entity> { M("A", 1, 1, 1), M("B", 2, 2, 2) };
            var b = new List<Entity> { M("B", 2, 2, 2), M("A", 1, 1, 1) };
            Assert.AreEqual(ZoneFingerprint.Hand(a), ZoneFingerprint.Hand(b));
        }

        [TestMethod]
        public void Hand_Changes_On_Composition()
        {
            var a = new List<Entity> { M("A", 1, 1, 1) };
            var b = new List<Entity> { M("A", 1, 1, 1), M("B", 1, 1, 1) };
            Assert.AreNotEqual(ZoneFingerprint.Hand(a), ZoneFingerprint.Hand(b));
        }

        [TestMethod]
        public void Null_Returns_Empty()
        {
            Assert.AreEqual("", ZoneFingerprint.Board(null));
            Assert.AreEqual("", ZoneFingerprint.Hand(null));
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run the build+test command (see Global Constraints).
Expected: FAIL — compile error `The name 'ZoneFingerprint' does not exist`（类尚未创建）。

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/Core/ZoneFingerprint.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using BgsDataBridge.Projector;           // KeywordMap
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Core
{
    // 棋盘/手牌稳定指纹：游戏线程 10Hz 轮询 diff 用。纯函数、只读已 Clone 的 Entity。
    // 棋盘：有序，含 zonePosition（挪位即变）+ atk/hp/keywords（buff 即变）—— spec §5.3 全量粒度。
    // 手牌：无序（位置无战术意义），不含 zonePosition。
    public static class ZoneFingerprint
    {
        public static string Board(List<Entity> orderedBoard)
            => orderedBoard == null ? "" : string.Join("|", orderedBoard.Select(BoardBeast));

        public static string Hand(List<Entity> hand)
        {
            if (hand == null) return "";
            var parts = hand.Select(HandBeast).OrderBy(p => p).ToList();
            return string.Join("|", parts);
        }

        static string BoardBeast(Entity e)
        {
            if (e == null) return "::0:0:";
            return $"{e.CardId ?? ""}:{e.GetTag(GameTag.ZONE_POSITION)}:{e.Attack}:{e.Health}:{string.Join(";", KeywordMap.From(e))}";
        }

        static string HandBeast(Entity e)
        {
            if (e == null) return ":0:0:";
            return $"{e.CardId ?? ""}:{e.Attack}:{e.Health}:{string.Join(";", KeywordMap.From(e))}";
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run the build+test command.
Expected: PASS — 8 个测试全绿。

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/Core/ZoneFingerprint.cs BgsDataBridge.Tests/Core/ZoneFingerprintTest.cs
git commit -m "feat(bgs-databridge): add ZoneFingerprint for board/hand diff" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 2: TierEdgeTracker（纯逻辑，TDD）

**Files:**
- Create: `BgsDataBridge/Core/TierEdgeTracker.cs`
- Test: `BgsDataBridge.Tests/Core/TierEdgeTrackerTest.cs`

**Interfaces:**
- Consumes: 无（纯 int 逻辑）。
- Produces: `public (int from, int to)? TierEdgeTracker.Observe(int tier)`、`public void TierEdgeTracker.Reset()`。

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge.Tests/Core/TierEdgeTrackerTest.cs`:

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class TierEdgeTrackerTest
    {
        [TestMethod]
        public void First_Observe_Baselines_No_Emit()
        {
            Assert.IsNull(new TierEdgeTracker().Observe(3));
        }

        [TestMethod]
        public void Strict_Increase_Emits_From_To()
        {
            var t = new TierEdgeTracker();
            t.Observe(2);
            var r = t.Observe(3);
            Assert.IsNotNull(r);
            Assert.AreEqual(2, r.Value.Item1);
            Assert.AreEqual(3, r.Value.Item2);
        }

        [TestMethod]
        public void Equal_Does_Not_Emit()
        {
            var t = new TierEdgeTracker();
            t.Observe(3);
            Assert.IsNull(t.Observe(3));
        }

        [TestMethod]
        public void Decrease_Does_Not_Emit()
        {
            var t = new TierEdgeTracker();
            t.Observe(4);
            Assert.IsNull(t.Observe(3));   // 异常下降不触发，仅更新基线
        }

        [TestMethod]
        public void Reset_Rebaselines()
        {
            var t = new TierEdgeTracker();
            t.Observe(5);
            t.Reset();
            Assert.IsNull(t.Observe(1));   // reset 后首帧建基线
            var r = t.Observe(2);
            Assert.AreEqual(1, r.Value.Item1);
        }

        [TestMethod]
        public void Repeated_Increments_Each_Emit()
        {
            var t = new TierEdgeTracker();
            t.Observe(1);
            Assert.AreEqual(2, t.Observe(2).Value.Item2);
            Assert.AreEqual(3, t.Observe(3).Value.Item2);
        }
    }
}
```

> 注：C# 7.3 命名元组字段在测试里用 `.Item1`/`.Item2` 访问最稳（`r.Value.from` 也合法，但 MSTest 断言里用 Item 名避免任何歧义）。

- [ ] **Step 2: Run test to verify it fails**

Run the build+test command.
Expected: FAIL — compile error `The name 'TierEdgeTracker' does not exist`。

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/Core/TierEdgeTracker.cs`:

```csharp
namespace BgsDataBridge.Core
{
    // 酒馆等级上升沿检测。战棋 tier 单调递增（不可能降）。
    // 首帧建基线不触发；仅严格上升返回 (from,to)。纯逻辑、可单测。
    public class TierEdgeTracker
    {
        int _last = -1;   // -1 = 未建基线

        public (int from, int to)? Observe(int tier)
        {
            if (_last < 0)
            {
                _last = tier;
                return null;
            }
            if (tier > _last)
            {
                var r = (_last, tier);
                _last = tier;
                return r;
            }
            _last = tier;
            return null;
        }

        public void Reset() => _last = -1;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run the build+test command.
Expected: PASS — 6 个测试全绿。

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/Core/TierEdgeTracker.cs BgsDataBridge.Tests/Core/TierEdgeTrackerTest.cs
git commit -m "feat(bgs-databridge): add TierEdgeTracker for tavern-up edge detection" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 3: ZoneExtractor（纯逻辑，TDD）

**Files:**
- Create: `BgsDataBridge/Core/ZoneExtractor.cs`
- Test: `BgsDataBridge.Tests/Core/ZoneExtractorTest.cs`

**Interfaces:**
- Consumes: `Entity`（`IsControlledBy`/`IsInPlay`/`IsInHand`/`IsMinion`/`IsBattlegroundsSpell`/`GetTag`/`Clone`）、`GameTag.ZONE_POSITION`。
- Produces: `public static List<Entity> ZoneExtractor.Board(IList<Entity> entities, int pid)`、`public static List<Entity> ZoneExtractor.Hand(IList<Entity> entities, int pid)`。返回的实体均已 `Clone()`（线程安全，可跨 tick 持有）。

- [ ] **Step 1: Write the failing test**

Create `BgsDataBridge.Tests/Core/ZoneExtractorTest.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class ZoneExtractorTest
    {
        const int Me = 1;
        const int Foe = 2;

        static Entity Card(string id, int controller, Zone zone, CardType type, int zonePos = 0)
        {
            var e = new Entity { CardId = id };
            e.SetTag(GameTag.CONTROLLER, controller);
            e.SetTag(GameTag.ZONE, (int)zone);
            e.SetTag(GameTag.CARDTYPE, (int)type);
            e.SetTag(GameTag.ZONE_POSITION, zonePos);
            return e;
        }

        [TestMethod]
        public void Board_Filters_To_Player_Minions_In_Play_Sorted_By_ZonePos()
        {
            var entities = new List<Entity>
            {
                Card("B", Me, Zone.PLAY, CardType.MINION, zonePos: 2),
                Card("A", Me, Zone.PLAY, CardType.MINION, zonePos: 1),
                Card("FOE", Foe, Zone.PLAY, CardType.MINION, zonePos: 1),   // 对手：排除
                Card("HAND", Me, Zone.HAND, CardType.MINION, zonePos: 1),   // 手牌：排除
                Card("HERO", Me, Zone.PLAY, CardType.HERO, zonePos: 0)      // 英雄：排除
            };
            var board = ZoneExtractor.Board(entities, Me);
            CollectionAssert.AreEqual(new[] { "A", "B" }, board.Select(x => x.CardId).ToList());
        }

        [TestMethod]
        public void Hand_Includes_Minions_And_BgsSpells()
        {
            var entities = new List<Entity>
            {
                Card("MIN", Me, Zone.HAND, CardType.MINION),
                Card("SPL", Me, Zone.HAND, CardType.BATTLEGROUND_SPELL),
                Card("PLAY", Me, Zone.PLAY, CardType.MINION)   // 棋盘：排除
            };
            var hand = ZoneExtractor.Hand(entities, Me);
            CollectionAssert.AreEquivalent(new[] { "MIN", "SPL" }, hand.Select(x => x.CardId).ToList());
        }

        [TestMethod]
        public void Returns_Clones_Not_Live_References()
        {
            var live = Card("A", Me, Zone.PLAY, CardType.MINION, zonePos: 1);
            var board = ZoneExtractor.Board(new List<Entity> { live }, Me);
            Assert.AreNotSame(live, board[0]);                 // 是克隆
            Assert.AreEqual("A", board[0].CardId);             // 内容保留
            live.SetTag(GameTag.ATK, 99);                       // 改原对象
            Assert.AreEqual(0, board[0].Attack);                // 克隆不受影响
        }

        [TestMethod]
        public void Empty_When_No_Match()
        {
            Assert.AreEqual(0, ZoneExtractor.Board(new List<Entity> { Card("X", Foe, Zone.PLAY, CardType.MINION) }, Me).Count);
            Assert.AreEqual(0, ZoneExtractor.Hand(new List<Entity>(), Me).Count);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run the build+test command.
Expected: FAIL — compile error `The name 'ZoneExtractor' does not exist`。

- [ ] **Step 3: Write minimal implementation**

Create `BgsDataBridge/Core/ZoneExtractor.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Core
{
    // 从一次实体快照中抽取玩家棋盘/手牌（纯函数，可单测）。
    // 棋盘按 ZONE_POSITION 升序（修 Capture 未排序问题，保证指纹位置稳定 + 快照可读）。
    // 手牌含随从 + 战棋法术，顺序无关（指纹内部再排序）。
    // 返回的实体均 Clone()——可安全跨 tick 持有（去抖 pending 载荷）。
    public static class ZoneExtractor
    {
        public static List<Entity> Board(IList<Entity> entities, int pid)
            => entities.Where(x => x.IsControlledBy(pid) && x.IsInPlay && x.IsMinion)
                       .OrderBy(x => x.GetTag(GameTag.ZONE_POSITION))
                       .Select(x => x.Clone())
                       .ToList();

        public static List<Entity> Hand(IList<Entity> entities, int pid)
            => entities.Where(x => x.IsControlledBy(pid) && x.IsInHand
                                  && (x.IsMinion || x.IsBattlegroundsSpell))
                       .Select(x => x.Clone())
                       .ToList();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run the build+test command.
Expected: PASS — 4 个测试全绿。

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/Core/ZoneExtractor.cs BgsDataBridge.Tests/Core/ZoneExtractorTest.cs
git commit -m "feat(bgs-databridge): add ZoneExtractor (board sort + hand filter, cloned)" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 4: player.hand 投影管线（DTO + View + Projector，TDD）

把 `player.hand` 从 `GameStateView` 一路透传到 `BgsSnapshot`，并暴露 `ProjectZone` 给后续 emit 链路用。

**Files:**
- Modify: `BgsDataBridge/Dtos/BgsSnapshot.cs`（`BgsPlayer` 加 `Hand`）
- Modify: `BgsDataBridge/Projector/GameStateView.cs`（加 `PlayerHand` 字段）
- Modify: `BgsDataBridge/Projector/GameStateProjector.cs`（`ProjectPlayer` 投影 hand；公开 `ProjectZone`）
- Test: `BgsDataBridge.Tests/Projector/ProjectorPlayerTest.cs`（加测试方法）

**Interfaces:**
- Consumes: Task 1 的 `ZoneFingerprint` 不依赖本任务；本任务仅扩展既有 DTO/View/Projector。
- Produces: `BgsPlayer.Hand`（`List<BgsMinion>`）、`GameStateView.PlayerHand`（`List<Entity>`）、`public List<BgsMinion> GameStateProjector.ProjectZone(List<Entity>, bool includeText)`。

- [ ] **Step 1: Write the failing test**

在 `BgsDataBridge.Tests/Projector/ProjectorPlayerTest.cs` 末尾（类内、最后一个 `[TestMethod]` 之后）追加：

```csharp
        [TestMethod]
        public void Projects_Player_Hand_From_View()
        {
            var view = new GameStateView
            {
                InMatch = true, IsBattlegrounds = true,
                PlayerBoard = new List<Entity>(),
                PlayerHand = new List<Entity> { Minion("BACON_H", 2, 2) }
            };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.AreEqual(1, snap.Player.Hand.Count);
            Assert.AreEqual("BACON_H", snap.Player.Hand[0].CardId);
            Assert.AreEqual(2, snap.Player.Hand[0].Attack);
        }

        [TestMethod]
        public void ProjectZone_Maps_Entities_To_Minions()
        {
            var zone = new List<Entity> { Minion("Z1", 3, 4, true) };
            var minions = new GameStateProjector().ProjectZone(zone, includeText: false);
            Assert.AreEqual(1, minions.Count);
            Assert.AreEqual("Z1", minions[0].CardId);
            CollectionAssert.Contains(minions[0].Keywords, "TAUNT");
        }
```

> `Minion(...)` helper 已存在于该测试类顶部（`ProjectorPlayerTest.cs:12-19`），直接复用。

- [ ] **Step 2: Run test to verify it fails**

Run the build+test command.
Expected: FAIL — compile error：`'GameStateView' does not contain a definition for 'PlayerHand'`（及 `'BgsPlayer' does not contain 'Hand'` / `'GameStateProjector' does not contain 'ProjectZone'`）。

- [ ] **Step 3: Write minimal implementation**

3a. `BgsDataBridge/Dtos/BgsSnapshot.cs`：在 `BgsPlayer` 类的 `Board` 属性之后加 `Hand`：

```csharp
        [JsonProperty("board")] public List<BgsMinion> Board { get; set; } = new List<BgsMinion>();
        [JsonProperty("hand")] public List<BgsMinion> Hand { get; set; } = new List<BgsMinion>();
```

3b. `BgsDataBridge/Projector/GameStateView.cs`：在 `public List<Entity> PlayerBoard = new List<Entity>();` 之后加：

```csharp
        public List<Entity> PlayerHand = new List<Entity>();
```

3c. `BgsDataBridge/Projector/GameStateProjector.cs`：在 `ProjectPlayer` 内、`foreach (var e in v.PlayerBoard) p.Board.Add(ToMinion(e, includeText));` 之后加：

```csharp
            foreach (var e in v.PlayerHand) p.Hand.Add(ToMinion(e, includeText));
```

并在 `Minions(...)` 方法附近新增公开方法（供 emit 链路把 pending `ZoneSnapshot.Zone` 投影成 `List<BgsMinion>`）：

```csharp
        public List<BgsMinion> ProjectZone(List<Entity> es, bool includeText)
        {
            var list = new List<BgsMinion>(es.Count);
            foreach (var e in es) list.Add(ToMinion(e, includeText));
            return list;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run the build+test command.
Expected: PASS — 新增 2 个测试全绿，既有测试不受影响（`PlayerHand` 默认空 list，老用例不设它时 hand 为空，不影响断言）。

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/Dtos/BgsSnapshot.cs BgsDataBridge/Projector/GameStateView.cs BgsDataBridge/Projector/GameStateProjector.cs BgsDataBridge.Tests/Projector/ProjectorPlayerTest.cs
git commit -m "feat(bgs-databridge): project player.hand through snapshot + ProjectZone" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 5: HdtGameSource 捕手牌 + 棋盘排序 + 热路径 CapturePlayerZones（编译+运行时验证）

`Capture()` 现在对棋盘未按 `ZONE_POSITION` 排序（仅 trinkets 排了）。本任务用 Task 3 的 `ZoneExtractor` 统一棋盘抽取（含排序）+ 新增手牌捕获，并加一个**不调 HearthMirror**的热路径方法供 `OnUpdate` 每 tick 调用。

**Files:**
- Modify: `BgsDataBridge/Projector/GameStateView.cs`（加 `PlayerZonesView` 类型）
- Modify: `BgsDataBridge/Projector/HdtGameSource.cs`（`Capture()` 用 `ZoneExtractor`；新增 `CapturePlayerZones()`）

**Interfaces:**
- Consumes: Task 3 `ZoneExtractor.Board/Hand`、既有 `CaptureEntitiesSnapshot`/`ReadTechLevel`。
- Produces: `public PlayerZonesView HdtGameSource.CapturePlayerZones()`，其中 `PlayerZonesView { List<Entity> Board; List<Entity> Hand; int Tier }`（实体均已 Clone）。

> 无单测——本层读 `Core.Game`（与既有 `Capture()` 同策略，靠编译 + 运行时验证）。棋盘排序/手牌过滤的正确性已由 Task 3 的 `ZoneExtractor` 单测覆盖。

- [ ] **Step 1: Add the PlayerZonesView type**

在 `BgsDataBridge/Projector/GameStateView.cs` 末尾（`LobbyPlayerView` 类之后）追加：

```csharp
    // 热路径（OnUpdate 每 tick）返回值：玩家棋盘+手牌+tier，实体均已 Clone。
    // 不调 HearthMirror（商店仍走 watcher 链路）；仅供 BoardChanged/HandChanged/TavernUpgraded 检测。
    public class PlayerZonesView
    {
        public List<Entity> Board = new List<Entity>();
        public List<Entity> Hand = new List<Entity>();
        public int Tier;
    }
```

并在该文件顶部确保有 `using Hearthstone_Deck_Tracker.Hearthstone.Entities;`（已存在，`PlayerBoard` 即用 `Entity`）。

- [ ] **Step 2: Wire ZoneExtractor into Capture() and add CapturePlayerZones()**

2a. 在 `BgsDataBridge/Projector/HdtGameSource.cs` 顶部 using 区加：

```csharp
using BgsDataBridge.Core;
```

2b. 替换 `Capture()` 里棋盘抽取的那段（当前 `HdtGameSource.cs:90-93`）：

```csharp
                var board = Safe(() => entities
                    .Where(x => x.IsControlledBy(pid) && x.IsInPlay && x.IsMinion)
                    .Select(x => x.Clone()).ToList());
                v.PlayerBoard = board ?? new List<Entity>();
```

替换为（用 `ZoneExtractor.Board`，含排序 + Clone）：

```csharp
                v.PlayerBoard = Safe(() => ZoneExtractor.Board(entities, pid)) ?? new List<Entity>();
```

2c. 紧接 `v.PlayerBoard = ...` 之后、`// #3: prefer the real hero ...` 注释之前，加手牌捕获：

```csharp
                v.PlayerHand = Safe(() => ZoneExtractor.Hand(entities, pid)) ?? new List<Entity>();
```

2d. 在 `Capture()` 方法之后、`CaptureEntitiesSnapshot` 之前，新增热路径方法：

```csharp
        /// <summary>
        /// 热路径（OnUpdate 10Hz）：一次实体快照 → 玩家棋盘+手牌+tier。
        /// 不调 HearthMirror（商店走 watcher 链路）；实体均 Clone，可跨 tick 持有。
        /// 与 Capture() 共享 ZoneExtractor，但跳过 shop/lobby/lastOpponent 等重读取。
        /// </summary>
        public PlayerZonesView CapturePlayerZones()
        {
            var v = new PlayerZonesView();
            try
            {
                var g = Hearthstone_Deck_Tracker.API.Core.Game;
                var entities = CaptureEntitiesSnapshot(g);
                int? playerId = null;
                try { playerId = g.Player.Id; } catch { }
                int pid = playerId ?? -1;
                v.Board = ZoneExtractor.Board(entities, pid);
                v.Hand = ZoneExtractor.Hand(entities, pid);
                v.Tier = ReadTechLevel(g);
            }
            catch { /* 保持空 view；OnUpdate 的调用方容错 */ }
            return v;
        }
```

- [ ] **Step 3: Build the plugin to verify it compiles**

Run the plugin build command (see Global Constraints).
Expected: 构建成功，无错误（`ZoneExtractor`/`PlayerZonesView`/`PlayerHand` 均已就位）。

- [ ] **Step 4: Run the full test suite to verify no regressions**

Run the build+test command (see Global Constraints).
Expected: PASS — 全部既有测试仍绿（`PlayerHand` 默认空，老 `Capture()`-无关测试不受影响）。

- [ ] **Step 5: Commit**

```bash
git add BgsDataBridge/Projector/GameStateView.cs BgsDataBridge/Projector/HdtGameSource.cs
git commit -m "feat(bgs-databridge): capture player.hand + sort board; add CapturePlayerZones" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## Task 6: 事件枚举 + BgsBridgePlugin 接线（编译+运行时验证）

把前 5 个任务连起来：加 3 个事件枚举；`OnUpdate` 里对棋盘/手牌做指纹 diff（seed 首帧、去抖）、对 tier 做边沿；相位/对局切换时 reset。

**Files:**
- Create: `BgsDataBridge/Core/ZoneSnapshot.cs`
- Modify: `BgsDataBridge/Events/BridgeEventType.cs`（+3 枚举）
- Modify: `BgsDataBridge/BgsBridgePlugin.cs`（字段/OnLoad/OnUpdate/OnGameStart/OnUnload）

**Interfaces:**
- Consumes: Task 1 `ZoneFingerprint`、Task 2 `TierEdgeTracker`、Task 4 `GameStateProjector.ProjectZone`、Task 5 `HdtGameSource.CapturePlayerZones`、既有 `ShopChangedDebouncer<T>`/`SystemClock`/`Emit`。
- Produces: 3 个新事件经 webhook 投递。

> 无单测——本层是 HDT 集成（靠编译 + 运行时验证）。指纹/边沿/去抖机制本身已由 Task 1/2 及既有 `ShopChangedDebouncerTest` 覆盖。

- [ ] **Step 1: Add the ZoneSnapshot payload type**

Create `BgsDataBridge/Core/ZoneSnapshot.cs`:

```csharp
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Core
{
    // 去抖器 pending 载荷：Update 时刻冻结的 zone 实体（已 Clone）+ turn/phase。
    // emit 时不再重捕获（复刻 ShopChanged 修复 offers 丢失的同款铁律）。
    public class ZoneSnapshot
    {
        public List<Entity> Zone;
        public int Turn;
        public string Phase;
    }
}
```

- [ ] **Step 2: Add the 3 event enum members**

`BgsDataBridge/Events/BridgeEventType.cs`：在 `ShopChanged` 之后加 3 个成员（保持 `[EnumMember]` + 末尾无逗号语义）：

```csharp
        [EnumMember(Value = "ShopChanged")] ShopChanged,
        [EnumMember(Value = "BoardChanged")] BoardChanged,
        [EnumMember(Value = "HandChanged")] HandChanged,
        [EnumMember(Value = "TavernUpgraded")] TavernUpgraded
```

即把原最后一行 `[EnumMember(Value = "ShopChanged")] ShopChanged` 后补逗号，再追加 3 行。

- [ ] **Step 3: Add plugin fields**

`BgsDataBridge/BgsBridgePlugin.cs`：在现有 `_shopDeb` 字段（约 `BgsBridgePlugin.cs:49`）附近加：

```csharp
        private ShopChangedDebouncer<ZoneSnapshot> _boardDeb;
        private ShopChangedDebouncer<ZoneSnapshot> _handDeb;
        private readonly TierEdgeTracker _tierEdge = new TierEdgeTracker();
        private string _lastBoardFp;
        private string _lastHandFp;
```

- [ ] **Step 4: Wire debouncers in OnLoad**

在 `OnLoad()` 里 `_shopDeb.OnEmit += ...` 那行（约 `BgsBridgePlugin.cs:109`）之后加：

```csharp
            _boardDeb = new ShopChangedDebouncer<ZoneSnapshot>(_cfg.ShopChangedQuietMs, _clock);
            _handDeb = new ShopChangedDebouncer<ZoneSnapshot>(_cfg.ShopChangedQuietMs, _clock);
            _boardDeb.OnEmit += snap => Emit(BridgeEventType.BoardChanged, BoardData(snap));
            _handDeb.OnEmit += snap => Emit(BridgeEventType.HandChanged, HandData(snap));
```

- [ ] **Step 5: Add BoardData / HandData helpers**

在 `BgsBridgePlugin.cs` 既有 `ShopData(ShopSnapshot snap)` 方法（约 `:284-293`）之后加两个镜像方法：

```csharp
        // 由 pending ZoneSnapshot 构造 BoardChanged/HandChanged webhook data：
        // 投影 zone → List<BgsMinion>（复用 ToMinion，含 live tags）。emit 时不重捕获。
        object BoardData(ZoneSnapshot snap)
        {
            try
            {
                if(snap?.Zone == null) return new {};
                return new { board = _projector.ProjectZone(snap.Zone, false), turn = snap.Turn, phase = snap.Phase };
            }
            catch { return new {}; }
        }

        object HandData(ZoneSnapshot snap)
        {
            try
            {
                if(snap?.Zone == null) return new {};
                return new { hand = _projector.ProjectZone(snap.Zone, false), turn = snap.Turn, phase = snap.Phase };
            }
            catch { return new {}; }
        }
```

- [ ] **Step 6: Replace the OnUpdate shop block with shop + board + hand + tier**

把 `OnUpdate()` 里这段（约 `BgsBridgePlugin.cs:180-204`，从 `// 商店事件驱动：...` 注释到 `_shopDeb.Tick();`）：

```csharp
                // 商店事件驱动：从 watcher 移交的最新快照（_lastShopCards）在游戏线程
                // 做全部判定与去抖。仅在购物相、非空时喂（空商店不发 → 无空载荷噪声）；
                // Shop→Combat 切换时 Reset（丢弃未发完的 pending，避免战斗开始时的陈旧发）。
                bool inShop = g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase && !g.IsInMenu;
                var cards = _lastShopCards;
                if(inShop && cards != null && ShopFeedPolicy.ShouldFeed(cards.Count, true))
                {
                    var sv = new ShopView { Tier = HdtGameSource.ReadTechLevel(g), Frozen = null };
                    foreach(var bc in cards) sv.Offers.Add(new Entity { CardId = bc.CardId ?? "" });
                    var fp = g.GetTurnNumber() + ":" + sv.Tier + ":"
                           + string.Join(",", cards.Select(c => c.CardId ?? ""));
                    if(fp != _lastShopFp)
                    {
                        _lastShopFp = fp;
                        _shopDeb.Update(new ShopSnapshot { Shop = sv, Turn = g.GetTurnNumber(), Phase = "Shop" },
                                        _clock.NowMs);
                    }
                }
                else if(!inShop && _lastShopFp != null)
                {
                    _shopDeb.Reset();
                    _lastShopFp = null;
                    _lastShopCards = null;
                }
                _shopDeb.Tick();
```

整体替换为：

```csharp
                // 商店/棋盘/手牌/升本 事件驱动：均在游戏线程判定 + 去抖。
                bool inShop = g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase && !g.IsInMenu;
                int turn = g.GetTurnNumber();

                if(inShop)
                {
                    // —— 商店（保持原逻辑）——
                    var cards = _lastShopCards;
                    if(cards != null && ShopFeedPolicy.ShouldFeed(cards.Count, true))
                    {
                        var sv = new ShopView { Tier = HdtGameSource.ReadTechLevel(g), Frozen = null };
                        foreach(var bc in cards) sv.Offers.Add(new Entity { CardId = bc.CardId ?? "" });
                        var shopFp = turn + ":" + sv.Tier + ":" + string.Join(",", cards.Select(c => c.CardId ?? ""));
                        if(shopFp != _lastShopFp)
                        {
                            _lastShopFp = shopFp;
                            _shopDeb.Update(new ShopSnapshot { Shop = sv, Turn = turn, Phase = "Shop" }, _clock.NowMs);
                        }
                    }

                    // —— 棋盘 / 手牌（全量指纹 diff，首帧 seed 不发，避免与 ShopPhaseStart 重复）——
                    var zones = _source.CapturePlayerZones();
                    var boardFp = ZoneFingerprint.Board(zones.Board);
                    if(_lastBoardFp == null) _lastBoardFp = boardFp;                       // seed
                    else if(boardFp != _lastBoardFp)
                    {
                        _lastBoardFp = boardFp;
                        _boardDeb.Update(new ZoneSnapshot { Zone = zones.Board, Turn = turn, Phase = "Shop" }, _clock.NowMs);
                    }
                    var handFp = ZoneFingerprint.Hand(zones.Hand);
                    if(_lastHandFp == null) _lastHandFp = handFp;                           // seed
                    else if(handFp != _lastHandFp)
                    {
                        _lastHandFp = handFp;
                        _handDeb.Update(new ZoneSnapshot { Zone = zones.Hand, Turn = turn, Phase = "Shop" }, _clock.NowMs);
                    }

                    // —— 升本（边沿，不去抖；tier 单调递增）——
                    var up = _tierEdge.Observe(zones.Tier);
                    if(up.HasValue)
                        Emit(BridgeEventType.TavernUpgraded, new { from = up.Value.Item1, to = up.Value.Item2, turn = turn, phase = "Shop" });
                }
                else
                {
                    // 非购物相：丢弃所有 pending + 指纹，杜绝战斗/菜单时的陈旧发。
                    if(_lastShopFp != null) { _shopDeb.Reset(); _lastShopFp = null; _lastShopCards = null; }
                    if(_lastBoardFp != null) { _boardDeb.Reset(); _lastBoardFp = null; }
                    if(_lastHandFp != null) { _handDeb.Reset(); _lastHandFp = null; }
                }

                _shopDeb.Tick();
                _boardDeb?.Tick();
                _handDeb?.Tick();
```

> 注：tier 的 `Observe` 只在 inShop 调用——tier 仅购物相变化，购物相内连续观测保持 `_last` 连续；非购物相不观测，下次进购物相 `_last` 仍是上次值，连续性正确。

- [ ] **Step 7: Reset tier on OnGameStart**

`OnGameStart()`：在 `_source?.ResetMatchCache();`（约 `BgsBridgePlugin.cs:225`）之后加：

```csharp
            _tierEdge.Reset();
```

- [ ] **Step 8: Flush new debouncers in OnUnload**

`OnUnload()`：在 `_shopDeb?.Flush();`（约 `BgsBridgePlugin.cs:343`）之后加：

```csharp
            try { _boardDeb?.Flush(); } catch (Exception ex) { Logger.Error("board flush: " + ex.Message); }
            try { _handDeb?.Flush(); } catch (Exception ex) { Logger.Error("hand flush: " + ex.Message); }
```

并在 `OnUnload` 末尾的置空区（`_shopDeb = null;` 附近）加：

```csharp
            _boardDeb = null;
            _handDeb = null;
```

- [ ] **Step 9: Build the plugin to verify it compiles**

Run the plugin build command.
Expected: 构建成功，无错误。

- [ ] **Step 10: Run the full test suite**

Run the build+test command.
Expected: PASS — 全部测试仍绿（新事件枚举不影响既有 `PhaseStateMachineTest` 等断言）。

- [ ] **Step 11: Commit**

```bash
git add BgsDataBridge/Core/ZoneSnapshot.cs BgsDataBridge/Events/BridgeEventType.cs BgsDataBridge/BgsBridgePlugin.cs
git commit -m "feat(bgs-databridge): emit BoardChanged/HandChanged/TavernUpgraded events" -m "Co-Authored-By: Claude <noreply@anthropic.com>"
```

- [ ] **Step 12: Runtime verification (manual)**

1. 部署：拷贝 `BgsDataBridge/bin/x86/Debug/BgsDataBridge.dll`（或 Release）到 `%APPDATA%\HearthstoneDeckTracker\Plugins\`，重启 HDT，启用插件。
2. 跑 `python BgsDataBridge/tools/receiver.py`（默认 `http://localhost:8000/`）。
3. 开一局酒馆战棋，验证 webhook 出现：
   - 买随从 → `HandChanged`（入手）随后 `BoardChanged`（打出）；商店 offers 少一个 → `ShopChanged`。
   - 挪动棋盘随从位置 → `BoardChanged`。
   - 一个随从获得圣盾/攻血 buff → `BoardChanged`。
   - 花钱升本 → `TavernUpgraded` `{from, to}`。
   - 战斗相：无 `BoardChanged`/`HandChanged` 洪泛（只有 `CombatPhaseStart`）。
4. 查 `%APPDATA%\HearthstoneDeckTracker\Plugins\BgsDataBridge\log.txt` 无 `OnUpdate`/`Emit` 异常。

---

## Self-Review

**1. Spec coverage**（逐节对照 spec）：
- §3 三个新事件 + 载荷 → Task 6（枚举 + BoardData/HandData/TavernUpgraded emit）。✓
- §4 `player.hand` + 棋盘排序 → Task 4（DTO/View/Projector hand）+ Task 5（Capture 用 ZoneExtractor 排序）。✓
- §5.1 购物相门控 → Task 6 OnUpdate `inShop` 分支。✓
- §5.2 一次快照三处 diff → Task 6 `CapturePlayerZones()` 一次返回 board/hand/tier。✓
- §5.3 指纹（棋盘有序含 zonePos+atk+hp+kw；手牌无序无 zonePos）→ Task 1（BoardBeast/HandBeast）。✓
- §5.4 复用 `ShopChangedDebouncer` + `ShopChangedQuietMs` → Task 6 OnLoad 实例化。✓
- §5.5 tier 边沿不去抖 → Task 2 + Task 6 直接 Emit。✓
- §5.6 相位/对局 reset → Task 6 OnUpdate `else` 分支 + OnGameStart `_tierEdge.Reset()`。✓
- §6 线程安全（Clone、emit 不重捕获）→ Task 3 Clone、Task 5 CapturePlayerZones Clone、Task 6 BoardData/HandData 用 pending 不重捕获。✓
- §7 组件清单逐项 → 各 Task 覆盖。✓
- §8 测试（指纹/边沿/去抖/门控）→ Task 1/2/3 单测 + 既有 `ShopChangedDebouncerTest`（去抖）+ Task 6 运行时验证。✓
- 决策 1（状态型）→ 全计划。决策 2（冻结舍弃）→ 不涉及 frozen。决策 3（全量指纹）→ Task 1。✓

**2. Placeholder scan**：无 TBD/TODO；每步含完整代码与确切命令。

**3. Type consistency**：
- `ZoneFingerprint.Board/Hand`（Task 1）↔ Task 6 调用一致。
- `TierEdgeTracker.Observe` 返回 `(int from,int to)?`，Task 6 用 `.Item1/.Item2` 访问（与 Task 2 测试一致）。✓
- `ZoneExtractor.Board/Hand(IList<Entity>,int)`（Task 3）↔ Task 5 `Capture`/`CapturePlayerZones` 调用一致。✓
- `PlayerZonesView.{Board,Hand,Tier}`（Task 5）↔ Task 6 `zones.Board/Hand/Tier` 一致。✓
- `ZoneSnapshot.{Zone,Turn,Phase}`（Task 6 Step1）↔ BoardData/HandData `snap.Zone/Turn/Phase` 一致。✓
- `ProjectZone(List<Entity>,bool)`（Task 4）↔ Task 5/6 调用一致。✓
- 枚举 `BoardChanged/HandChanged/TavernUpgraded`（Task 6 Step2）↔ Emit 调用一致。✓

无遗漏、无类型漂移。
