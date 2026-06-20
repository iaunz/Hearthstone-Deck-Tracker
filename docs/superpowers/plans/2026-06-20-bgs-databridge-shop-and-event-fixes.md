# BgsDataBridge 商店数据与事件修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复 `bgs-events.log` 暴露的 4 类问题——商店 offers 几乎拿不到 / `tier` 恒 0、赛中误触发 `HeroPick`、空载荷 `ShopChanged`、`MatchEnd` 缺 `player.hero`——使商店 offers 通过事件驱动可靠投递、酒馆等级正确反映、事件流无噪声。

**Architecture:** 商店改为订阅 HDT 自带 `Watchers.OpponentBoardStateWatcher.Change`（watcher 线程只移交快照引用），全部判定与去抖仍在游戏线程 `OnUpdate` 完成；去抖器泛型化为 `ShopChangedDebouncer<T>` 并发出 pending 快照（不再重新捕获）。tier 用 `PLAYER_TECH_LEVEL` tag；HeroPick 用游戏实体 `STEP` 硬门。两个新的纯函数（`HeroPickPhase`/`ShopFeedPolicy`）单测覆盖。

**Tech Stack:** .NET Framework 4.7.2 / WPF / **x86** / C# 7.3；MSTest（v2）；Newtonsoft.Json；Costura 单 DLL。

## Global Constraints

- **构建器**：经典 MSBuild（**不可用 `dotnet build`**，主工程 ResGen 目标不兼容 SDK MSBuild）：
  - 构建：`"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" <工程或sln> -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo`
  - 测试：先 MSBuild 构建 Tests 工程，再 `dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj --no-build -p:Platform=x86`
- **框架/平台/语言**：net472、x86、C# 7.3（**禁用** `namespace Foo;` 文件作用域语法、`is not` 模式、target-typed `new()`）。
- **插件依赖铁律**：所有 HDT 运行时依赖（HearthDb/HearthMirror/HearthWatcher/HDT 主工程）一律 `<Private>false</Private>`（Copy Local=False），避免与 HDT 已加载程序集冲突。
- **线程铁律**：游戏线程回调（`OnUpdate`/`GameEvents.*`）只"快照入队"立刻返回，绝不发 HTTP；跨线程读 `Core.Game` 必先克隆。watcher 回调跑在 watcher 后台线程，只允许赋值一个 volatile 字段。
- **测试边界**：纯逻辑（Core）走 TDD；HDT 集成层（HdtGameSource 读 `Core.Game`/HearthMirror、插件生命周期）无单测，靠构建通过 + 运行时验证。
- **C# 7.3 注意**：所有新代码用块状 `namespace {}`、显式类型、`default(T)` 而非 `default`。
- **每个 task 结尾必须提交一次**（conventional commits，scope 用 `bgs`）。
- **每个 task 结尾必须构建通过**（绿）。后续 task 依赖前序 task 产出的类型/方法签名（见各 task 的 Interfaces 块）。

---

## File Structure

| 文件 | 责任 | task |
|---|---|---|
| `BgsDataBridge/Core/ShopChangedDebouncer.cs` | 泛型 trailing-edge 去抖器；存 pending、发 pending、Reset | 1 |
| `BgsDataBridge/Core/HeroPickPhase.cs`（新） | 纯函数：是否处于英雄选择相位（STEP 硬门） | 2 |
| `BgsDataBridge/Core/ShopFeedPolicy.cs`（新） | 纯函数：是否把商店采样喂去抖器 | 3 |
| `BgsDataBridge/Projector/HdtGameSource.cs` | `ReadTechLevel`、`Capture()` 赋 `v.Tier`、`CaptureShop()` 赋 `sv.Tier`、hero 兜底、`DerivePhase` 用 HeroPickPhase、删 `CaptureShopOnly` | 4, 5, 6, 7 |
| `BgsDataBridge/Projector/ShopSnapshot.cs`（新） | 去抖器 pending 载荷 DTO | 7 |
| `BgsDataBridge/Projector/GameStateProjector.cs` | 抽出 `ProjectShop` 复用 | 7 |
| `BgsDataBridge/BgsBridgePlugin.cs` | 订阅/解绑 watcher、`OnShopBoardChange`、重写 `OnUpdate` 商店段、`OnEmit` 用 ShopSnapshot、删 `ShopFingerprint`/旧 `ShopData`、`HeroPickActive` 用 HeroPickPhase、Reset | 1, 5, 7 |
| `BgsDataBridge/BgsDataBridge.csproj` | 加 HearthWatcher 引用 | 7 |
| `BgsDataBridge.Tests/Core/ShopChangedDebouncerTest.cs` | 适配泛型 + Reset | 1 |
| `BgsDataBridge.Tests/Core/HeroPickPhaseTest.cs`（新） | HeroPickPhase 单测 | 2 |
| `BgsDataBridge.Tests/Core/ShopFeedPolicyTest.cs`（新） | ShopFeedPolicy 单测 | 3 |

---

### Task 1: 泛型化 `ShopChangedDebouncer<T>` 并加 `Reset`

把去抖器从 `string` 载荷改为泛型 `T`，新增 `Reset()`（Shop→Combat 切换时丢弃 pending）。同步把 plugin 的 `_shopDeb` 字段/构造改为 `<string>`（行为不变；Task 7 再切到 `<ShopSnapshot>`）。

**Files:**
- Modify: `BgsDataBridge/Core/ShopChangedDebouncer.cs`（整体重写）
- Modify: `BgsDataBridge/BgsBridgePlugin.cs:45`（字段类型）、`BgsBridgePlugin.cs:83`（构造调用）
- Test: `BgsDataBridge.Tests/Core/ShopChangedDebouncerTest.cs`（适配 + 新增 Reset 用例）

**Interfaces:**
- Produces: `public class ShopChangedDebouncer<T>` with `ShopChangedDebouncer(long quietMs, IClock clock)`、`event Action<T> OnEmit`、`void Update(T payload, long nowMs)`、`void Tick()`、`void Flush()`、`void Reset()`。
- Note: 删除旧的 `ShopEmitted` 委托类型，改用 `Action<T>`。

- [ ] **Step 1: 更新测试文件（适配泛型 + 新增 Reset 用例）**

整体替换 `BgsDataBridge.Tests/Core/ShopChangedDebouncerTest.cs`：

```csharp
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class ShopChangedDebouncerTest
    {
        class FakeClock : IClock { public long Now; public long NowMs => Now; }

        [TestMethod]
        public void Emits_After_Quiet_Period()
        {
            var clock = new FakeClock();
            var db = new ShopChangedDebouncer<string>(quietMs: 400, clock);
            var emitted = new List<string>();
            db.OnEmit += s => emitted.Add(s);

            db.Update("A", clock.NowMs);   // t=0
            Assert.AreEqual(0, emitted.Count);
            clock.Now = 300; db.Tick();     // 还在窗口内
            Assert.AreEqual(0, emitted.Count);
            clock.Now = 401; db.Tick();     // 静默满
            Assert.AreEqual(1, emitted.Count);
            Assert.AreEqual("A", emitted[0]);
        }

        [TestMethod]
        public void Coalesces_Rapid_Changes_To_Final()
        {
            var clock = new FakeClock();
            var db = new ShopChangedDebouncer<string>(400, clock);
            var emitted = new List<string>();
            db.OnEmit += s => emitted.Add(s);

            db.Update("A", 0);
            clock.Now = 100; db.Update("B", clock.Now); db.Tick();
            clock.Now = 200; db.Update("C", clock.Now); db.Tick();
            clock.Now = 601; db.Tick();     // 距最近变化 401ms
            Assert.AreEqual(1, emitted.Count);
            Assert.AreEqual("C", emitted[0]);  // 只发最终态
        }

        [TestMethod]
        public void Flush_Emits_Pending_Immediately()
        {
            var clock = new FakeClock();
            var db = new ShopChangedDebouncer<string>(400, clock);
            var emitted = new List<string>();
            db.OnEmit += s => emitted.Add(s);
            db.Update("X", 0);
            db.Flush();                      // 立即 flush（阶段切换/卸载）
            Assert.AreEqual(1, emitted.Count);
            Assert.AreEqual("X", emitted[0]);
        }

        [TestMethod]
        public void No_Emit_When_No_Change()
        {
            var clock = new FakeClock();
            var db = new ShopChangedDebouncer<string>(400, clock);
            var n = 0;
            db.OnEmit += s => n++;
            clock.Now = 5000; db.Tick();
            Assert.AreEqual(0, n);
        }

        [TestMethod]
        public void Reset_Clears_Pending_And_Prevents_Emit()
        {
            var clock = new FakeClock();
            var db = new ShopChangedDebouncer<string>(400, clock);
            var n = 0;
            db.OnEmit += s => n++;
            db.Update("A", 0);
            db.Reset();                      // Shop→Combat：丢弃 pending
            clock.Now = 9999; db.Tick();     // 即使静默足够久
            Assert.AreEqual(0, n);           // 也不发
        }
    }
}
```

- [ ] **Step 2: 跑测试，确认失败（旧类非泛型、无 Reset → 编译错误）**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
```
Expected: 编译失败（`ShopChangedDebouncer` 需要 1 个类型参数 / `Reset` 未定义）。

- [ ] **Step 3: 重写去抖器为泛型 + Reset**

整体替换 `BgsDataBridge/Core/ShopChangedDebouncer.cs`：

```csharp
namespace BgsDataBridge.Core
{
    // trailing-edge: 变更后静默 quietMs 再发；同窗口多次变化只发最终态（pending）。
    // 泛型 T 为 pending 载荷（plugin 用 ShopSnapshot）。Core 不依赖 Projector。
    public class ShopChangedDebouncer<T>
    {
        private readonly long _quietMs;
        private readonly IClock _clock;
        private T _pending;
        private long _lastChangeAt = -1;
        private bool _dirty;

        public event System.Action<T> OnEmit;

        public ShopChangedDebouncer(long quietMs, IClock clock)
        {
            _quietMs = quietMs;
            _clock = clock;
        }

        public void Update(T payload, long nowMs)
        {
            _pending = payload;
            _lastChangeAt = nowMs;
            _dirty = true;
        }

        public void Tick()
        {
            if(!_dirty || _lastChangeAt < 0) return;
            if(_clock.NowMs - _lastChangeAt >= _quietMs) Flush();
        }

        public void Flush()
        {
            if(!_dirty) return;
            var p = _pending;
            _dirty = false;
            _lastChangeAt = -1;
            OnEmit?.Invoke(p);
        }

        // 丢弃未发出的 pending（Shop→Combat 切换时调用，避免战斗开始时的陈旧/空发）。
        public void Reset()
        {
            _pending = default(T);
            _dirty = false;
            _lastChangeAt = -1;
        }
    }
}
```

- [ ] **Step 4: 把 plugin 的 `_shopDeb` 改为 `<string>`（保持构建绿）**

在 `BgsDataBridge/BgsBridgePlugin.cs`：

字段声明（约 line 45）：
```csharp
        private ShopChangedDebouncer<string> _shopDeb;
```

构造（约 line 83，`OnLoad` 内）：
```csharp
            _shopDeb = new ShopChangedDebouncer<string>(_cfg.ShopChangedQuietMs, _clock);
```

（`OnEmit += payload => ...`、`Update(fp, ...)`、`Flush()` 无需改动——`payload`/`fp` 仍是 `string`。）

- [ ] **Step 5: 跑测试，确认全绿**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj --no-build -p:Platform=x86
```
Expected: 全部通过（含新 `Reset_Clears_Pending_And_Prevents_Emit`）。

- [ ] **Step 6: 构建 plugin 工程，确认绿**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge/BgsDataBridge.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
```
Expected: 构建成功。

- [ ] **Step 7: 提交**

```bash
git add BgsDataBridge/Core/ShopChangedDebouncer.cs BgsDataBridge/BgsBridgePlugin.cs BgsDataBridge.Tests/Core/ShopChangedDebouncerTest.cs
git commit -m "refactor(bgs): genericize ShopChangedDebouncer<T> and add Reset

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 2: 纯函数 `HeroPickPhase.IsActive`

用游戏实体 `STEP` tag 做硬门，替代易误触发的 `!IsBattlegroundsHeroPickingDone`。

**Files:**
- Create: `BgsDataBridge/Core/HeroPickPhase.cs`
- Test: `BgsDataBridge.Tests/Core/HeroPickPhaseTest.cs`

**Interfaces:**
- Produces: `public static class HeroPickPhase { public static bool IsActive(bool isBattlegrounds, bool isInMenu, int step, int beginMulliganStep); }`。`step` 缺失时调用方应传 `int.MaxValue`。

- [ ] **Step 1: 写失败测试**

创建 `BgsDataBridge.Tests/Core/HeroPickPhaseTest.cs`：

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class HeroPickPhaseTest
    {
        const int BeginMulligan = 5; // Step.BEGIN_MULLIGAN 的实际值无关；验证比较语义

        [TestMethod]
        public void Active_When_Bgs_NotMenu_Step_AtOrBefore_Mulligan()
        {
            Assert.IsTrue(HeroPickPhase.IsActive(true, false, BeginMulligan, BeginMulligan));
            Assert.IsTrue(HeroPickPhase.IsActive(true, false, 0, BeginMulligan));
        }

        [TestMethod]
        public void Inactive_When_Step_Past_Mulligan()
        {
            // 问题 #2 场景：STEP 已越过 BEGIN_MULLIGAN → 不应再触发 HeroPick
            Assert.IsFalse(HeroPickPhase.IsActive(true, false, BeginMulligan + 1, BeginMulligan));
        }

        [TestMethod]
        public void Inactive_When_Not_Bgs()
        {
            Assert.IsFalse(HeroPickPhase.IsActive(false, false, 0, BeginMulligan));
        }

        [TestMethod]
        public void Inactive_When_InMenu()
        {
            Assert.IsFalse(HeroPickPhase.IsActive(true, true, 0, BeginMulligan));
        }

        [TestMethod]
        public void Inactive_When_Step_Missing()
        {
            // GameEntity tag 缺失 → 调用方传 int.MaxValue → 永不触发
            Assert.IsFalse(HeroPickPhase.IsActive(true, false, int.MaxValue, BeginMulligan));
        }
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj --no-build -p:Platform=x86 --filter "FullyQualifiedName~HeroPickPhaseTest"
```
Expected: FAIL（`HeroPickPhase` 未定义）。

- [ ] **Step 3: 实现**

创建 `BgsDataBridge/Core/HeroPickPhase.cs`：

```csharp
namespace BgsDataBridge.Core
{
    // 纯函数：判断当前是否处于"英雄选择"相位。用游戏实体 STEP tag 做硬门
    // （单调、常驻），避免旧的 IsBattlegroundsHeroPickingDone（依赖玩家实体
    // MULLIGAN_STATE）在玩家实体瞬时缺失时误读 false 而触发赛中 HeroPick。
    // step 缺失时调用方应传 int.MaxValue（永不触发）。
    public static class HeroPickPhase
    {
        public static bool IsActive(bool isBattlegrounds, bool isInMenu, int step, int beginMulliganStep)
            => isBattlegrounds && !isInMenu && step <= beginMulliganStep;
    }
}
```

- [ ] **Step 4: 跑测试，确认全绿**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj --no-build -p:Platform=x86 --filter "FullyQualifiedName~HeroPickPhaseTest"
```
Expected: 5/5 通过。

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/Core/HeroPickPhase.cs BgsDataBridge.Tests/Core/HeroPickPhaseTest.cs
git commit -m "feat(bgs): add HeroPickPhase STEP-gate predicate

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 3: 纯函数 `ShopFeedPolicy.ShouldFeed`

封装"是否把一次商店采样喂去抖器"：空商店不喂（消除空载荷噪声）、非购物相不喂。

**Files:**
- Create: `BgsDataBridge/Core/ShopFeedPolicy.cs`
- Test: `BgsDataBridge.Tests/Core/ShopFeedPolicyTest.cs`

**Interfaces:**
- Produces: `public static class ShopFeedPolicy { public static bool ShouldFeed(int offerCount, bool inShopPhase); }`。

- [ ] **Step 1: 写失败测试**

创建 `BgsDataBridge.Tests/Core/ShopFeedPolicyTest.cs`：

```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class ShopFeedPolicyTest
    {
        [TestMethod]
        public void Feeds_When_NonEmpty_Shop_In_Shop_Phase()
        {
            Assert.IsTrue(ShopFeedPolicy.ShouldFeed(1, true));
            Assert.IsTrue(ShopFeedPolicy.ShouldFeed(6, true));
        }

        [TestMethod]
        public void Does_Not_Feed_When_Empty()
        {
            // 问题 #3：空商店（已买空）不喂 → 不产生空载荷 ShopChanged
            Assert.IsFalse(ShopFeedPolicy.ShouldFeed(0, true));
        }

        [TestMethod]
        public void Does_Not_Feed_When_Not_Shop_Phase()
        {
            Assert.IsFalse(ShopFeedPolicy.ShouldFeed(6, false));
        }
    }
}
```

- [ ] **Step 2: 跑测试，确认失败**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj --no-build -p:Platform=x86 --filter "FullyQualifiedName~ShopFeedPolicyTest"
```
Expected: FAIL（`ShopFeedPolicy` 未定义）。

- [ ] **Step 3: 实现**

创建 `BgsDataBridge/Core/ShopFeedPolicy.cs`：

```csharp
namespace BgsDataBridge.Core
{
    // 纯函数：决定是否把一次商店采样喂给去抖器。空商店（已买空）无决策价值
    // 且会产生空载荷噪声；非购物相（战斗/菜单）不喂。
    public static class ShopFeedPolicy
    {
        public static bool ShouldFeed(int offerCount, bool inShopPhase)
            => inShopPhase && offerCount > 0;
    }
}
```

- [ ] **Step 4: 跑测试，确认全绿**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj --no-build -p:Platform=x86 --filter "FullyQualifiedName~ShopFeedPolicyTest"
```
Expected: 3/3 通过。

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/Core/ShopFeedPolicy.cs BgsDataBridge.Tests/Core/ShopFeedPolicyTest.cs
git commit -m "feat(bgs): add ShopFeedPolicy non-empty shop gate

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 4: 酒馆等级（`player.tier` + `shop.tier`）

新增 `HdtGameSource.ReadTechLevel`（BobsBuddy 同款 `PLAYER_TECH_LEVEL` tag），在 `Capture()` 赋 `v.Tier`、在 `CaptureShop()` 赋 `sv.Tier`。集成层，无单测。

**Files:**
- Modify: `BgsDataBridge/Projector/HdtGameSource.cs`（加 using + `ReadTechLevel` + `Capture()`/`CaptureShop()` 两处赋值）

**Interfaces:**
- Produces: `public static int HdtGameSource.ReadTechLevel(GameV2 g)`（Task 7 复用）。
- 效果：`GameStateView.Tier`（→ `BgsPlayer.Tier`）与 `ShopView.Tier`（→ `BgsShop.Tier`）反映真实酒馆等级。

- [ ] **Step 1: 加 using 并实现 `ReadTechLevel`**

在 `BgsDataBridge/Projector/HdtGameSource.cs` 顶部 using 区加：
```csharp
using HearthDb.Enums;
```

在类内（`Safe`/`SafeValue` 附近）加：
```csharp
        // 酒馆等级（tech level 1-6）：取玩家英雄实体的 PLAYER_TECH_LEVEL tag
        // （HDT BobsBuddy 同款读法，BobsBuddyInvoker.cs:428）。g.Player.Hero 与
        // g.Opponent.Hero 对称（后者已在本类 CaptureLastOpponent 使用）。
        public static int ReadTechLevel(GameV2 g)
            => SafeValue(() => g.Player.Hero?.GetTag(GameTag.PLAYER_TECH_LEVEL) ?? 0) ?? 0;
```

- [ ] **Step 2: `Capture()` 赋 `v.Tier`**

在 `Capture()` 中，紧跟 `v.Turn = g.GetTurnNumber();` / `v.Phase = DerivePhase(g);` 之后加：
```csharp
                v.Tier = ReadTechLevel(g);
```

- [ ] **Step 3: `CaptureShop()` 赋 `sv.Tier`**

把 `CaptureShop()` 内：
```csharp
            var sv = new ShopView { Tier = 0, Frozen = null };
```
改为：
```csharp
            // Tier = 玩家当前酒馆等级；Frozen：HearthMirror 暂未暴露商店冻结状态，保持 null（spec §3.2）。
            var sv = new ShopView { Tier = ReadTechLevel(g), Frozen = null };
```

- [ ] **Step 4: 构建，确认绿**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge/BgsDataBridge.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
```
Expected: 构建成功。

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/Projector/HdtGameSource.cs
git commit -m "fix(bgs): populate tavern tier via PLAYER_TECH_LEVEL (player + shop)

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 5: 接入 `HeroPickPhase`（`DerivePhase` + `TriggerInput`）

把两处 `!g.IsBattlegroundsHeroPickingDone` 替换为 `HeroPickPhase.IsActive(...)`，消除赛中误触发。集成层。

**Files:**
- Modify: `BgsDataBridge/Projector/HdtGameSource.cs`（using + `DerivePhase`）
- Modify: `BgsDataBridge/BgsBridgePlugin.cs`（using + `OnUpdate` 的 `TriggerInput.HeroPickActive`）

**Interfaces:**
- Consumes: `HeroPickPhase.IsActive(bool,bool,int,int)`（Task 2）。
- 需要 HDT API：`g.GameEntity?.GetTag(GameTag.STEP)`、`(int)Step.BEGIN_MULLIGAN`（均 `HearthDb.Enums`）。

- [ ] **Step 1: `HdtGameSource.DerivePhase` 改用 `HeroPickPhase`**

在 `BgsDataBridge/Projector/HdtGameSource.cs` using 区加（若 Task 4 未覆盖）：
```csharp
using BgsDataBridge.Core;
```
（`HearthDb.Enums` 已由 Task 4 加入。）

把 `DerivePhase` 中的：
```csharp
            // Hero-pick is not done yet -> still in the picking phase.
            if (g.IsBattlegroundsHeroPickingDone == false)
                return "HeroPick";
```
替换为：
```csharp
            // 英雄选择相位：用 STEP 硬门（游戏实体 tag，单调），避免玩家实体瞬时
            // 缺失导致旧的 IsBattlegroundsHeroPickingDone 误判（问题 #2）。
            if (HeroPickPhase.IsActive(g.IsBattlegroundsMatch, g.IsInMenu,
                    g.GameEntity?.GetTag(GameTag.STEP) ?? int.MaxValue,
                    (int)Step.BEGIN_MULLIGAN))
                return "HeroPick";
```

- [ ] **Step 2: plugin `OnUpdate` 的 `HeroPickActive` 改用 `HeroPickPhase`**

在 `BgsDataBridge/BgsBridgePlugin.cs` using 区加：
```csharp
using HearthDb.Enums;
```
（`BgsDataBridge.Core` 已 import。）

把 `OnUpdate` 中 `TriggerInput` 的：
```csharp
                    HeroPickActive = !g.IsBattlegroundsHeroPickingDone,
```
替换为：
```csharp
                    HeroPickActive = HeroPickPhase.IsActive(g.IsBattlegroundsMatch, g.IsInMenu,
                        g.GameEntity?.GetTag(GameTag.STEP) ?? int.MaxValue,
                        (int)Step.BEGIN_MULLIGAN),
```

- [ ] **Step 3: 构建，确认绿**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge/BgsDataBridge.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
```
Expected: 构建成功。

- [ ] **Step 4: 提交**

```bash
git add BgsDataBridge/Projector/HdtGameSource.cs BgsDataBridge/BgsBridgePlugin.cs
git commit -m "fix(bgs): gate HeroPick on STEP tag to stop mid-game false triggers

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 6: `MatchEnd` 缺 `player.hero` 的兜底

`Capture()` 英雄查找：在 PLAY 区找不到时（对局结束清场），回退到任意我方英雄实体。集成层，best-effort。

**Files:**
- Modify: `BgsDataBridge/Projector/HdtGameSource.cs`（`Capture()` 英雄查找）

**Interfaces:**
- 无新接口；仅改 `v.Hero` 取值逻辑。

- [ ] **Step 1: 加 hero 兜底**

把 `Capture()` 中：
```csharp
                v.Hero = Safe(() => entities
                    .FirstOrDefault(x => x.IsControlledBy(pid) && x.IsInPlay && x.IsHero)?.Clone());
```
替换为：
```csharp
                v.Hero = Safe(() => entities
                        .FirstOrDefault(x => x.IsControlledBy(pid) && x.IsInPlay && x.IsHero)?.Clone())
                    // 问题 #4：对局结束清场时英雄可能已离开 PLAY 区；回退到任意我方
                    // 英雄实体（玩家只控制一个英雄，安全）。仍为空则照旧省略字段。
                    ?? Safe(() => entities
                        .FirstOrDefault(x => x.IsControlledBy(pid) && x.IsHero)?.Clone());
```

- [ ] **Step 2: 构建，确认绿**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge/BgsDataBridge.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
```
Expected: 构建成功。

- [ ] **Step 3: 提交**

```bash
git add BgsDataBridge/Projector/HdtGameSource.cs
git commit -m "fix(bgs): fallback hero entity when not in PLAY at match end

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

### Task 7: 事件驱动商店（订阅 watcher，重写 `OnUpdate` 商店段，删除旧重捕获链路）

把商店来源从"10Hz 自调 `GetOpponentBoardState` + emit 时重捕获"改为"watcher 移交 `_lastShopCards` + 游戏线程喂去抖器 + 发 pending 快照"。删 `CaptureShopOnly`/`ShopFingerprint`/旧 `ShopData`。`OnEmit` 由 `ShopSnapshot` 构造 webhook data。

**Files:**
- Modify: `BgsDataBridge/BgsDataBridge.csproj`（加 HearthWatcher 引用）
- Create: `BgsDataBridge/Projector/ShopSnapshot.cs`
- Modify: `BgsDataBridge/Projector/GameStateProjector.cs`（抽出 `ProjectShop`）
- Modify: `BgsDataBridge/Projector/HdtGameSource.cs`（删 `CaptureShopOnly`）
- Modify: `BgsDataBridge/BgsBridgePlugin.cs`（using + 字段 + 订阅/解绑 + `OnShopBoardChange` + 重写 `OnUpdate` 商店段 + `OnEmit`/`ShopData` 改写 + 删 `ShopFingerprint` + `_shopDeb` 改 `<ShopSnapshot>`）

**Interfaces:**
- Consumes: `ShopChangedDebouncer<T>`（Task 1）、`ShopFeedPolicy.ShouldFeed`（Task 3）、`public static int HdtGameSource.ReadTechLevel(GameV2)`（Task 4）。
- Produces: `public class ShopSnapshot { public ShopView Shop; public int Turn; public string Phase; }`、`public BgsShop GameStateProjector.ProjectShop(ShopView shop, bool includeText)`。
- 关键 HDT/HearthWatcher 类型：`Hearthstone_Deck_Tracker.Hearthstone.Watchers.OpponentBoardStateWatcher`（事件 `Change`，委托 `OpponentBoardStateEventHandler(object, OpponentBoardArgs)`）、`HearthWatcher.EventArgs.OpponentBoardArgs`（属性 `List<BoardCard> BoardCards`）、`HearthMirror.Objects.BoardCard`（属性 `CardId`）。

- [ ] **Step 1: 给 plugin 工程加 HearthWatcher 引用**

在 `BgsDataBridge/BgsDataBridge.csproj` 的 `<ItemGroup>`（含其它 Reference/ProjectReference）里加：
```xml
    <ProjectReference Include="..\HearthWatcher\HearthWatcher.csproj">
      <Private>false</Private>
    </ProjectReference>
```
（`<Private>false</Private>`：编译期可见、运行期由 HDT 提供、Costura 不内嵌——与其它 HDT 依赖一致。HearthWatcher 已被 HDT 主工程引用，故 DLL 在 HDT 运行时已加载。）

- [ ] **Step 2: 创建 `ShopSnapshot` DTO**

创建 `BgsDataBridge/Projector/ShopSnapshot.cs`：
```csharp
namespace BgsDataBridge.Projector
{
    // 商店去抖器的 pending 载荷：随 Update 一起捕获的（shop 快照 + 当时 turn/phase）。
    // emit 时直接用它构造 webhook，不再"重新捕获"（offers 丢失的真正修复点）。
    public class ShopSnapshot
    {
        public ShopView Shop;
        public int Turn;
        public string Phase;
    }
}
```

- [ ] **Step 3: 抽出 `GameStateProjector.ProjectShop` 供复用**

在 `BgsDataBridge/Projector/GameStateProjector.cs`：

把 `Project()` 中的：
```csharp
                Shop = v.Shop != null ? new BgsShop { Available = true, Tier = v.Shop.Tier,
                    Frozen = v.Shop.Frozen, Offers = Minions(v.Shop.Offers, includeText) } : null,
```
改为：
```csharp
                Shop = ProjectShop(v.Shop, includeText),
```

在类内（`Minions` 附近）加 public 方法：
```csharp
        public BgsShop ProjectShop(ShopView shop, bool includeText)
            => shop != null ? new BgsShop { Available = true, Tier = shop.Tier,
                Frozen = shop.Frozen, Offers = Minions(shop.Offers, includeText) } : null;
```

- [ ] **Step 4: 删除 `HdtGameSource.CaptureShopOnly`**

在 `BgsDataBridge/Projector/HdtGameSource.cs` 删除整个 `CaptureShopOnly()` 方法（含其上方 `<summary>` 注释块）。该方法不再有任何调用方（Step 9 会删掉 plugin 里仅有的两个引用）。

- [ ] **Step 5: plugin 加 using 与字段**

在 `BgsDataBridge/BgsBridgePlugin.cs` using 区加：
```csharp
using System.Linq;
using HearthMirror.Objects;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
```
（`BgsDataBridge.Projector` 已 import，故 `ShopSnapshot`/`ShopView` 可见；`HearthDb.Enums` 已由 Task 5 加入。）

在类字段区（`_shopDeb` 附近）加：
```csharp
        // watcher 后台线程写、游戏线程读；单一 volatile 字段，持有最近一次商店快照引用。
        private volatile System.Collections.Generic.List<BoardCard> _lastShopCards;
        private string _lastShopFp;
```

把 `_shopDeb` 字段类型与构造改为 `<ShopSnapshot>`（Task 1 设过 `<string>`，现替换）：
```csharp
        private ShopChangedDebouncer<ShopSnapshot> _shopDeb;
```
```csharp
            _shopDeb = new ShopChangedDebouncer<ShopSnapshot>(_cfg.ShopChangedQuietMs, _clock);
```

- [ ] **Step 6: 加 `OnShopBoardChange` 与订阅/解绑**

在 `OnLoad`（`_shopDeb.OnEmit += ...` 之后、`MenuItem = BuildMenu();` 之前）加订阅：
```csharp
            // 商店事件驱动：watcher 在商店发牌/刷新/购买时触发 Change。
            // 与 GameEvents 不同，Watchers.*.Change 是普通 C# 事件，不随插件禁用自动解绑，
            // 故 OnUnload 必须手动 -=（否则禁用→启用会重复订阅、重复触发）。
            Hearthstone_Deck_Tracker.Hearthstone.Watchers.OpponentBoardStateWatcher.Change += OnShopBoardChange;
```

在 `OnUnload`（`try { _shopDeb?.Flush(); }` 之前）加解绑：
```csharp
            try { Hearthstone_Deck_Tracker.Hearthstone.Watchers.OpponentBoardStateWatcher.Change -= OnShopBoardChange; } catch { /* 已解绑 */ }
```

在类内加方法（与 `OnGameStart`/`OnGameEnd` 同区）：
```csharp
        // watcher 后台线程回调：仅移交最新商店快照引用。零 Core.Game 访问、零逻辑。
        // BoardCards 每次 fire 都是新 list（HearthMirror 返回新对象），跨线程持有安全。
        void OnShopBoardChange(object sender, HearthWatcher.EventArgs.OpponentBoardArgs args)
        {
            _lastShopCards = args.BoardCards;
        }
```

- [ ] **Step 7: 改写 `OnEmit` lambda 与 `ShopData`**

把 `OnLoad` 中：
```csharp
            _shopDeb.OnEmit += payload => Emit(BridgeEventType.ShopChanged, ShopData());
```
改为：
```csharp
            _shopDeb.OnEmit += snap => Emit(BridgeEventType.ShopChanged, ShopData(snap));
```

把整个旧 `ShopData()`（无参、重捕获版本）替换为接收 `ShopSnapshot`、不再重新捕获的版本：
```csharp
        // 由 pending ShopSnapshot 构造 ShopChanged webhook data：投影 shop → BgsShop，
        // 直接用快照里的 turn/phase。不再 CaptureShopOnly 重捕获（那是 offers 丢失的根因）。
        object ShopData(ShopSnapshot snap)
        {
            try
            {
                if(snap?.Shop == null) return new {};
                var shop = _projector.ProjectShop(snap.Shop, false);
                return new { shop = shop, turn = snap.Turn, phase = snap.Phase };
            }
            catch { return new {}; }
        }
```

- [ ] **Step 8: 删除 `ShopFingerprint`**

删除整个 `ShopFingerprint()` 方法（含其上方注释）——被 Step 9 的新逻辑取代，且 `CaptureShopOnly` 已删。

- [ ] **Step 9: 重写 `OnUpdate` 商店段**

把 `OnUpdate` 中（`foreach (var ev in _sm.Observe(input)) {...}` 之后、`catch` 之前）的旧商店轮询段：
```csharp
                // Shop debouncer: poll the shop only while in a BGs shopping phase.
                // I3: CaptureShopOnly() skips lobby/races/rating/board/lastOpponent
                // (the heavy reads) — only GetOpponentBoardState + turn/phase.
                // The debouncer's payload string is a cheap fingerprint for
                // change detection; the actual webhook DTO is rebuilt at emit
                // time (see ShopData) so C1 gets a clean projected BgsShop.
                if (g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase && !g.IsInMenu)
                {
                    var fp = ShopFingerprint();
                    if (fp != null) _shopDeb.Update(fp, _clock.NowMs);
                }
                _shopDeb.Tick();
```
替换为：
```csharp
                // 商店事件驱动：从 watcher 移交的最新快照（_lastShopCards）在游戏线程
                // 做全部判定与去抖。仅在购物相、非空时喂（空商店不发 → 无空载荷噪声）；
                // Shop→Combat 切换时 Reset（丢弃未发完的 pending，避免战斗开始时的陈旧发）。
                bool inShop = g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase && !g.IsInMenu;
                var cards = _lastShopCards;
                if(inShop && cards != null && ShopFeedPolicy.ShouldFeed(cards.Count, true))
                {
                    var sv = new ShopView { Tier = HdtGameSource.ReadTechLevel(g), Frozen = null };
                    foreach(var bc in cards) sv.Offers.Add(new Entity { CardId = bc.CardId });
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

- [ ] **Step 10: 构建 plugin，确认绿**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge/BgsDataBridge.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
```
Expected: 构建成功。（若报 `OpponentBoardArgs`/`BoardCard`/`Watchers` 找不到，确认 using：`OnShopBoardChange` 用全限定 `HearthWatcher.EventArgs.OpponentBoardArgs`、字段用 `HearthMirror.Objects.BoardCard`（已 `using HearthMirror.Objects;`）、订阅用全限定 `Hearthstone_Deck_Tracker.Hearthstone.Watchers.OpponentBoardStateWatcher`。）

- [ ] **Step 11: 跑全部测试，确认仍绿（未触及纯逻辑行为）**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj --no-build -p:Platform=x86
```
Expected: 全部通过。

- [ ] **Step 12: 打 Release 单 DLL**

Run:
```bash
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge/BgsDataBridge.csproj -p:Configuration=Release -p:Platform=x86 -v:minimal -nologo
```
Expected: 产出 `BgsDataBridge/bin/x86/Release/BgsDataBridge.dll`（Costura 单 DLL）。

- [ ] **Step 13: 提交**

```bash
git add BgsDataBridge/BgsDataBridge.csproj BgsDataBridge/Projector/ShopSnapshot.cs BgsDataBridge/Projector/GameStateProjector.cs BgsDataBridge/Projector/HdtGameSource.cs BgsDataBridge/BgsBridgePlugin.cs
git commit -m "fix(bgs): event-driven shop via OpponentBoardStateWatcher.Change

Shop offers now come from the watcher's latest snapshot (handed off via a
single volatile field), with all gating/debounce on the game thread and
the debouncer emitting the pending snapshot instead of re-capturing at
emit time. Suppresses empty shops and resets on Shop->Combat.

Co-Authored-By: Claude <noreply@anthropic.com>"
```

---

## 部署与运行时验证（Task 7 之后）

1. 把 `BgsDataBridge/bin/x86/Release/BgsDataBridge.dll` 拷到 `%APPDATA%\HearthstoneDeckTracker\Plugins\`，**重启 HDT**，Options → Tracker → Plugins → 启用 `BgsDataBridge`。
2. 开一局酒馆战棋，跑接收器（`tools/receiver.py`），确认：
   - `ShopChanged` 携带**真实 offers**（发牌/刷新后短时间内）。
   - `player.tier` / `shop.tier` 随升本变化（不再恒 0）。
   - 比赛中段**不再**出现 `HeroPick`（仅开局 turn 0 一次）。
   - **无**空载荷 `ShopChanged`（`data:{}`）。
   - `MatchEnd` 的 `player.hero` 尽量存在（best-effort）。
3. 插件日志：`%APPDATA%\HearthstoneDeckTracker\Plugins\BgsDataBridge\log.txt` 应无 `OnUpdate:`/`Emit:` 错误。

---

## Self-Review（写完后自检）

- **Spec 覆盖**：tier（#1 部分）→ Task 4；frozen 文档化 → Task 4 注释；offers 事件驱动（#1）+ 空 `ShopChanged`（#3）→ Task 7；HeroPick（#2）→ Task 2+5；hero 兜底（#4）→ Task 6。✅ 全覆盖。
- **类型一致性**：`ShopChangedDebouncer<T>.Update(T,long)` / `Reset()` / `event Action<T> OnEmit`（Task 1 定义，Task 7 以 `<ShopSnapshot>` 用）；`HeroPickPhase.IsActive(bool,bool,int,int)`（Task 2 定义，Task 5 用）；`ShopFeedPolicy.ShouldFeed(int,bool)`（Task 3 定义，Task 7 用）；`public static HdtGameSource.ReadTechLevel(GameV2)`（Task 4 定义，Task 7 用）；`ShopSnapshot{Shop,Turn,Phase}`（Task 7 Step 2 定义，Step 7/9 用）；`GameStateProjector.ProjectShop(ShopView,bool)`（Task 7 Step 3 定义，Step 7 用）。✅ 签名一致。
- **占位符扫描**：无 TBD/TODO；每个代码步骤含完整代码；构建/测试命令含期望输出。✅
- **每 task 构建绿**：Task 1 同步把 plugin `_shopDeb` 改 `<string>` 保持绿；Task 7 删除 `CaptureShopOnly` 与删除其唯一调用方（`ShopFingerprint`/旧 `ShopData`）在同 task 内完成，中间态可编译。✅
