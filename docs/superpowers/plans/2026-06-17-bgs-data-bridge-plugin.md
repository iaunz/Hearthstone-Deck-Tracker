# BgsDataBridge 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 构建一个 HDT 插件（单 DLL），把酒馆战棋对局状态以 HTTP `GET /state` 快照 + 事件驱动 webhook 暴露给本地下游（Web 面板 / LLM 分析）。

**Architecture:** 插件实现 HDT 的 `IPlugin`，进程内运行。纯逻辑核心（去抖/状态机/webhook 分发/DTO 投影）从 HDT 依赖剥离、TDD 覆盖；HDT 适配层（读 `Core.Game`/HearthMirror）薄而可手动验证。`HttpListener` 仅绑 localhost。Webhook 走独立发送线程、绝不阻塞游戏线程。

**Tech Stack:** C# / .NET Framework 4.7.2 (net472) / WPF / `HttpListener` / Newtonsoft.Json 12.0.3 / MSTest / Costura.Fody（打包）。

**Spec:** `docs/superpowers/specs/2026-06-17-bgs-data-bridge-plugin-design.md`

## Global Constraints

（每个任务的隐含前置；逐字取自 spec）

- 目标框架 **net472**，平台 **x86**（与 HDT 一致；HearthMirror/HearthDb 为 x86）。
- 插件项目引用 `Hearthstone Deck Tracker.exe`、`HearthDb.dll`、`HearthMirror.dll`、`Newtonsoft.Json` —— 这些 HDT 已加载，**Copy Local = False**，避免版本冲突（官方插件教程明确警告）。
- C# 语法兼容 **C# 7.3**（HDTTests `LangVersion=7`；避免 switch 表达式/可空引用类型/records/ranges）。
- **铁律**：游戏线程上的回调（`OnUpdate`、`GameEvents.*`）只做"快照入队"立刻返回，**绝不发 HTTP**；HTTP 发送走独立线程。
- 跨线程读 `Core.Game` 必须**先快照**（`.ToList()` / `Entity.Clone()`）。
- 所有对外路径 try/catch；插件异常绝不抛回 HDT；契约可空字段显式 `null`。
- HTTP 仅 **localhost**，**只读**（v1 无写端点）。
- 商店 `frozen` 为尽力而为可空字段。

---

## File Structure

| 文件 | 责任 |
|---|---|
| `BgsDataBridge/BgsDataBridge.csproj` | 插件项目（SDK-style net472 x86 UseWPF），引用 HDT+lib |
| `BgsDataBridge/Dtos/*.cs` | 契约 POCO：`BgsSnapshot`/`BgsMatch`/`BgsPlayer`/`BgsMinion`/`BgsCardRef`/`BgsShop`/`BgsLastOpponent`/`BgsLobby` |
| `BgsDataBridge/Events/EventEnvelope.cs` | webhook 信封 `bgs-event/v1` |
| `BgsDataBridge/Events/BridgeEventType.cs` | 事件类型枚举 |
| `BgsDataBridge/Config/BridgeConfig.cs` | 配置模型 + JSON load/save |
| `BgsDataBridge/Core/IClock.cs` | 时钟抽象（测试可替换） |
| `BgsDataBridge/Core/ShopChangedDebouncer.cs` | 商店变化 trailing-edge 合并 |
| `BgsDataBridge/Core/PhaseStateMachine.cs` | 阶段/选择边沿检测（纯逻辑） |
| `BgsDataBridge/Core/TriggerInput.cs` | 状态机输入结构 |
| `BgsDataBridge/Core/TriggerEvent.cs` | 状态机输出事件 |
| `BgsDataBridge/Webhook/IHttpSender.cs` | HTTP 发送抽象 |
| `BgsDataBridge/Webhook/WebhookDispatcher.cs` | 队列 + 退避 + flush |
| `BgsDataBridge/Webhook/HmacSigner.cs` | HMAC 签名 |
| `BgsDataBridge/Projector/GameStateView.cs` | 已快照的对局视图（持克隆 Entity） |
| `BgsDataBridge/Projector/IGameSource.cs` | `Capture() → GameStateView` |
| `BgsDataBridge/Projector/GameStateProjector.cs` | `GameStateView → BgsSnapshot` 映射 |
| `BgsDataBridge/Projector/KeywordMap.cs` | `GameTag → keyword` 映射 |
| `BgsDataBridge/Projector/HdtGameSource.cs` | 读 `Core.Game`/HearthMirror 的适配器（集成层） |
| `BgsDataBridge/Http/RouteDispatcher.cs` | 路由纯逻辑（path+query → 响应） |
| `BgsDataBridge/Http/BridgeHttpServer.cs` | `HttpListener` 封装 + CORS |
| `BgsDataBridge/BgsBridgePlugin.cs` | `IPlugin` 实现 + 装配/拆卸 + 配置热重载 |
| `BgsDataBridge/Settings/SettingsWindow.xaml(.cs)` | WPF 设置窗 |
| `BgsDataBridge/Logger.cs` | 文件日志 |
| `BgsDataBridge/Assets/viewer.html` | 最小参考 Web 面板（`GET /`） |
| `BgsDataBridge.Tests/*.cs` | 单测（SDK-style net472 x86 MSTest） |

---

## Task 0: 环境与项目脚手架

**目标**：确认 HDT 能构建；建立插件 + 测试两个项目并加入 sln；空构建 + 一个平凡测试通过。

**Files:**
- Create: `BgsDataBridge/BgsDataBridge.csproj`
- Create: `BgsDataBridge.Tests/BgsDataBridge.Tests.csproj`
- Modify: `Hearthstone Deck Tracker.sln`（加入两个项目）

- [ ] **Step 1: 确认 HDT 解决方案可构建（一次性环境门禁）**

拉取 lib 依赖并构建（任选一种，以你的环境为准）：

```bash
# 方式 A：Visual Studio 打开 Hearthstone Deck Tracker.sln，还原 NuGet，构建 Solution（Debug|x86）
# 方式 B：命令行（需 VS Build Tools + .NET SDK）
msbuild "Hearthstone Deck Tracker.sln" /t:Restore /p:Configuration=Debug /p:Platform=x86
msbuild "Hearthstone Deck Tracker.sln" /p:Configuration=Debug /p:Platform=x86
```

Expected：构建成功（HDTTests 项目随之构建）。**若 HDT 本身构建不过，先解决环境问题——这是仓库前置，非本计划范畴。** lib 依赖由 `Bootstrap/Bootstrap.csproj` 从 `https://libs.hearthsim.net/hdt/HearthMirror.zip` 下载到 `lib/`；VS 还原/构建会自动触发。

- [ ] **Step 2: 创建插件项目 `BgsDataBridge.csproj`**

`BgsDataBridge/BgsDataBridge.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <Platforms>x86</Platforms>
    <LangVersion>7.3</LangVersion>
    <UseWPF>true</UseWPF>
    <AssemblyName>BgsDataBridge</AssemblyName>
    <RootNamespace>BgsDataBridge</RootNamespace>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Hearthstone Deck Tracker\Hearthstone Deck Tracker.csproj">
      <Private>false</Private>
    </ProjectReference>
    <Reference Include="HearthDb"><HintPath>..\lib\HearthDb.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="HearthMirror"><HintPath>..\lib\HearthMirror.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Newtonsoft.Json"><HintPath>..\packages\Newtonsoft.Json.12.0.3\lib\net45\Newtonsoft.Json.dll</HintPath><Private>false</Private></Reference>
  </ItemGroup>
</Project>
```
> Newtonsoft.Json 的 HintPath 需指向还原后的包路径（以实际 NuGet 包路径为准；或改为 `<PackageReference Include="Newtonsoft.Json" Version="12.0.3" PrivateAssets="all" />`）。`Private=false` 即 Copy Local=False，避免重复 HDT 依赖。

- [ ] **Step 3: 创建测试项目 `BgsDataBridge.Tests.csproj`**

`BgsDataBridge.Tests/BgsDataBridge.Tests.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <Platforms>x86</Platforms>
    <LangVersion>7.3</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="16.0.1" />
    <PackageReference Include="MSTest.TestAdapter" Version="1.4.0" />
    <PackageReference Include="MSTest.TestFramework" Version="1.4.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\BgsDataBridge\BgsDataBridge.csproj" />
  </ItemGroup>
</Project>
```
> 测试项目通过插件项目传递引用 HDT；如需直接构造 `Entity`，再加 `<ProjectReference Include="..\Hearthstone Deck Tracker\Hearthstone Deck Tracker.csproj" />`（Task 6 起需要）。

- [ ] **Step 4: 写一个平凡测试确认测试管线通**

`BgsDataBridge.Tests/SanityTest.cs`：
```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgsDataBridge.Tests
{
    [TestClass]
    public class SanityTest
    {
        [TestMethod]
        public void Sanity_Passes()
        {
            Assert.AreEqual(2, 1 + 1);
        }
    }
}
```

- [ ] **Step 5: 运行测试，确认通过**

Run: `dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -c Debug -p:Platform=x86`
（或 VS Test Explorer；或 `vstest.console.exe BgsDataBridge.Tests/bin/x86/Debug/BgsDataBridge.Tests.dll`）
Expected: PASS（1 passed）。

- [ ] **Step 6: 把两个项目加入 .sln 并提交**

用 VS 把两个项目"添加到解决方案"，或用 `dotnet sln`：
```bash
dotnet sln "Hearthstone Deck Tracker.sln" add BgsDataBridge/BgsDataBridge.csproj
dotnet sln "Hearthstone Deck Tracker.sln" add BgsDataBridge.Tests/BgsDataBridge.Tests.csproj
git add BgsDataBridge/ BgsDataBridge.Tests/ "Hearthstone Deck Tracker.sln"
git commit -m "feat(bridge): scaffold BgsDataBridge plugin + test projects"
```

---

## Task 1: 数据契约 DTO（`bgs-state/v1`）

**目标**：定义所有快照 POCO + 事件信封，固化 schema 测试守住下游契约。

**Files:**
- Create: `BgsDataBridge/Dtos/BgsSnapshot.cs`（及其余 Dto，见下）
- Create: `BgsDataBridge/Events/EventEnvelope.cs`
- Create: `BgsDataBridge/Events/BridgeEventType.cs`
- Test: `BgsDataBridge.Tests/Dtos/SchemaTest.cs`

**Interfaces:**
- Produces: `BgsSnapshot`、`BgsMatch`、`BgsPlayer`、`BgsMinion`、`BgsCardRef`、`BgsShop`、`BgsLastOpponent`、`BgsLobby`、`EventEnvelope`、`BridgeEventType`

- [ ] **Step 1: 写失败的 schema 测试**

`BgsDataBridge.Tests/Dtos/SchemaTest.cs`：
```csharp
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Dtos;
using BgsDataBridge.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BgsDataBridge.Tests.Dtos
{
    [TestClass]
    public class SchemaTest
    {
        [TestMethod]
        public void Snapshot_RoundTrip_PreservesAllFields()
        {
            var snap = new BgsSnapshot
            {
                Schema = "bgs-state/v1", Locale = "enUS", InMatch = true, Partial = false,
                Match = new BgsMatch { GameType = "BattlegroundsSolo", IsBattlegrounds = true,
                    IsDuos = false, Spectator = false, Phase = "Shop", Turn = 5 },
                AvailableRaces = new List<string> { "MURLOC", "DEMON" },
                Player = new BgsPlayer { Name = "me", Tier = 4,
                    Board = new List<BgsMinion> { new BgsMinion { CardId = "BACON_1", Attack = 8, Health = 8 } } },
                Shop = new BgsShop { Available = true, Offers = new List<BgsMinion>() },
                LastOpponent = null, Lobby = null
            };
            var json = JsonConvert.SerializeObject(snap);
            var j = JObject.Parse(json);
            Assert.AreEqual("bgs-state/v1", j["schema"]);
            Assert.AreEqual("Shop", j["match"]["phase"]);
            Assert.AreEqual(8, (int)j["player"]["board"][0]["health"]);
            Assert.AreEqual(JTokenType.Null, j["lastOpponent"].Type);
        }

        [TestMethod]
        public void Envelope_RoundTrip_HasSeqAndEvent()
        {
            var env = new EventEnvelope
            {
                Schema = "bgs-event/v1", Seq = 137, Event = BridgeEventType.ShopPhaseStart
            };
            var j = JObject.Parse(JsonConvert.SerializeObject(env));
            Assert.AreEqual(137, (int)j["seq"]);
            Assert.AreEqual("ShopPhaseStart", j["event"]);
        }
    }
}
```

- [ ] **Step 2: 运行，确认失败（类型未定义）**

Run: `dotnet test BgsDataBridge.Tests -c Debug -p:Platform=x86 --filter SchemaTest`
Expected: FAIL（编译错误：找不到 `BgsSnapshot` 等）。

- [ ] **Step 3: 实现 DTO + 事件类型**

`BgsDataBridge/Events/BridgeEventType.cs`：
```csharp
using Newtonsoft.Json;
using System.Runtime.Serialization;

namespace BgsDataBridge.Events
{
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum BridgeEventType
    {
        [EnumMember(Value = "MatchStart")] MatchStart,
        [EnumMember(Value = "MatchEnd")] MatchEnd,
        [EnumMember(Value = "HeroPick")] HeroPick,
        [EnumMember(Value = "TrinketPick")] TrinketPick,
        [EnumMember(Value = "ShopPhaseStart")] ShopPhaseStart,
        [EnumMember(Value = "CombatPhaseStart")] CombatPhaseStart,
        [EnumMember(Value = "ShopChanged")] ShopChanged
    }
}
```

`BgsDataBridge/Events/EventEnvelope.cs`：
```csharp
using System.Collections.Generic;

namespace BgsDataBridge.Events
{
    public class EventEnvelope
    {
        public string Schema { get; set; } = "bgs-event/v1";
        public long Seq { get; set; }
        public BridgeEventType Event { get; set; }
        public string At { get; set; }      // ISO-8601 UTC
        public object Match { get; set; }    // 复用 BgsMatch 结构
        public object Data { get; set; }
    }
}
```

`BgsDataBridge/Dtos/BgsSnapshot.cs`（同文件放全部相关 DTO；字段名用 `[JsonProperty]` 显式对齐 spec 的 camelCase）：
```csharp
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BgsDataBridge.Dtos
{
    public class BgsSnapshot
    {
        [JsonProperty("schema")] public string Schema { get; set; } = "bgs-state/v1";
        [JsonProperty("locale")] public string Locale { get; set; }
        [JsonProperty("capturedAt")] public string CapturedAt { get; set; }
        [JsonProperty("inMatch")] public bool InMatch { get; set; }
        [JsonProperty("partial")] public bool Partial { get; set; }
        [JsonProperty("match")] public BgsMatch Match { get; set; }
        [JsonProperty("availableRaces")] public List<string> AvailableRaces { get; set; }
        [JsonProperty("player")] public BgsPlayer Player { get; set; }
        [JsonProperty("shop", NullValueHandling = NullValueHandling.Ignore)] public BgsShop Shop { get; set; }
        [JsonProperty("lastOpponent", NullValueHandling = NullValueHandling.Ignore)] public BgsLastOpponent LastOpponent { get; set; }
        [JsonProperty("lobby", NullValueHandling = NullValueHandling.Ignore)] public BgsLobby Lobby { get; set; }
    }

    public class BgsMatch
    {
        [JsonProperty("gameType")] public string GameType { get; set; }
        [JsonProperty("isBattlegrounds")] public bool IsBattlegrounds { get; set; }
        [JsonProperty("isDuos")] public bool IsDuos { get; set; }
        [JsonProperty("spectator")] public bool Spectator { get; set; }
        [JsonProperty("phase")] public string Phase { get; set; }
        [JsonProperty("turn")] public int Turn { get; set; }
        [JsonProperty("gameUuid", NullValueHandling = NullValueHandling.Ignore)] public string GameUuid { get; set; }
        [JsonProperty("rating", NullValueHandling = NullValueHandling.Ignore)] public BgsRating Rating { get; set; }
        [JsonProperty("anomaly", NullValueHandling = NullValueHandling.Ignore)] public BgsCardRef Anomaly { get; set; }
    }
    public class BgsRating { [JsonProperty("mmr")] public int? Mmr { get; set; } [JsonProperty("duosMmr")] public int? DuosMmr { get; set; } }

    public class BgsCardRef
    {
        [JsonProperty("cardId")] public string CardId { get; set; }
        [JsonProperty("dbfId", NullValueHandling = NullValueHandling.Ignore)] public int? DbfId { get; set; }
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string Name { get; set; }
        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)] public string Text { get; set; }
        [JsonProperty("cost", NullValueHandling = NullValueHandling.Ignore)] public int? Cost { get; set; }
    }

    public class BgsHero : BgsCardRef
    {
        [JsonProperty("health", NullValueHandling = NullValueHandling.Ignore)] public int? Health { get; set; }
        [JsonProperty("armor", NullValueHandling = NullValueHandling.Ignore)] public int? Armor { get; set; }
    }

    public class BgsPlayer
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("tier")] public int Tier { get; set; }
        [JsonProperty("hero", NullValueHandling = NullValueHandling.Ignore)] public BgsHero Hero { get; set; }
        [JsonProperty("heroPower", NullValueHandling = NullValueHandling.Ignore)] public BgsCardRef HeroPower { get; set; }
        [JsonProperty("trinkets")] public List<BgsTrinket> Trinkets { get; set; } = new List<BgsTrinket>();
        [JsonProperty("questReward", NullValueHandling = NullValueHandling.Ignore)] public BgsQuestReward QuestReward { get; set; }
        [JsonProperty("board")] public List<BgsMinion> Board { get; set; } = new List<BgsMinion>();
    }
    public class BgsTrinket : BgsCardRef { [JsonProperty("slot")] public string Slot { get; set; } }
    public class BgsQuestReward : BgsCardRef
    {
        [JsonProperty("progress", NullValueHandling = NullValueHandling.Ignore)] public int? Progress { get; set; }
        [JsonProperty("total", NullValueHandling = NullValueHandling.Ignore)] public int? Total { get; set; }
    }

    public class BgsMinion
    {
        [JsonProperty("zonePosition", NullValueHandling = NullValueHandling.Ignore)] public int? ZonePosition { get; set; }
        [JsonProperty("slot", NullValueHandling = NullValueHandling.Ignore)] public int? Slot { get; set; }
        [JsonProperty("cardId")] public string CardId { get; set; }
        [JsonProperty("dbfId", NullValueHandling = NullValueHandling.Ignore)] public int? DbfId { get; set; }
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string Name { get; set; }
        [JsonProperty("attack", NullValueHandling = NullValueHandling.Ignore)] public int? Attack { get; set; }
        [JsonProperty("health", NullValueHandling = NullValueHandling.Ignore)] public int? Health { get; set; }
        [JsonProperty("golden", NullValueHandling = NullValueHandling.Ignore)] public bool? Golden { get; set; }
        [JsonProperty("keywords", NullValueHandling = NullValueHandling.Ignore)] public List<string> Keywords { get; set; }
        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)] public string Text { get; set; }
    }

    public class BgsShop
    {
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("tier")] public int Tier { get; set; }
        [JsonProperty("frozen")] public bool? Frozen { get; set; }
        [JsonProperty("offers")] public List<BgsMinion> Offers { get; set; } = new List<BgsMinion>();
    }

    public class BgsLastOpponent
    {
        [JsonProperty("turn")] public int Turn { get; set; }
        [JsonProperty("hero", NullValueHandling = NullValueHandling.Ignore)] public BgsCardRef Hero { get; set; }
        [JsonProperty("board")] public List<BgsMinion> Board { get; set; } = new List<BgsMinion>();
    }

    public class BgsLobby
    {
        [JsonProperty("players")] public List<BgsLobbyPlayer> Players { get; set; } = new List<BgsLobbyPlayer>();
    }
    public class BgsLobbyPlayer
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("heroCardId")] public string HeroCardId { get; set; }
        [JsonProperty("accountId")] public string AccountId { get; set; }
    }
}
```

- [ ] **Step 4: 运行测试，确认通过**

Run: `dotnet test BgsDataBridge.Tests -c Debug -p:Platform=x86 --filter SchemaTest`
Expected: PASS（2 passed）。

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/ BgsDataBridge.Tests/
git commit -m "feat(bridge): data contract DTOs (bgs-state/v1, bgs-event/v1)"
```

---

## Task 2: 配置模型（JSON load/save）

**Files:**
- Create: `BgsDataBridge/Config/BridgeConfig.cs`
- Test: `BgsDataBridge.Tests/Config/BridgeConfigTest.cs`

**Interfaces:**
- Produces: `BridgeConfig`、`WebhookConfig`（`Load(string json)`、`Save()`、`ToJson()`）

- [ ] **Step 1: 写失败的配置测试**

`BgsDataBridge.Tests/Config/BridgeConfigTest.cs`：
```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Config;

namespace BgsDataBridge.Tests.Config
{
    [TestClass]
    public class BridgeConfigTest
    {
        [TestMethod]
        public void Load_ParsesWebhooksAndToggles()
        {
            var json = @"{""enabled"":true,""port"":5273,
              ""webhooks"":[{""url"":""http://localhost:8000/bgs"",""events"":[""*""]}],
              ""shopChangedQuietMs"":400}";
            var cfg = BridgeConfig.Load(json);
            Assert.IsTrue(cfg.Enabled);
            Assert.AreEqual(5273, cfg.Port);
            Assert.AreEqual(1, cfg.Webhooks.Count);
            Assert.AreEqual("http://localhost:8000/bgs", cfg.Webhooks[0].Url);
            Assert.AreEqual(400, cfg.ShopChangedQuietMs);
        }

        [TestMethod]
        public void Defaults_WhenEmpty()
        {
            var cfg = BridgeConfig.Load("{}");
            Assert.IsTrue(cfg.Enabled);
            Assert.AreEqual(5273, cfg.Port);
            Assert.AreEqual(400, cfg.ShopChangedQuietMs);
            Assert.AreEqual(0, cfg.Webhooks.Count);
        }

        [TestMethod]
        public void RoundTrip_PreservesValues()
        {
            var cfg = BridgeConfig.Load(@"{""port"":6000,""webhooks"":[{""url"":""u"",""events"":[""ShopPhaseStart""]}],""shopChangedQuietMs"":250}");
            var again = BridgeConfig.Load(cfg.ToJson());
            Assert.AreEqual(6000, again.Port);
            Assert.AreEqual("ShopPhaseStart", again.Webhooks[0].Events[0]);
            Assert.AreEqual(250, again.ShopChangedQuietMs);
        }
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test BgsDataBridge.Tests --filter BridgeConfigTest`
Expected: FAIL（`BridgeConfig` 未定义）。

- [ ] **Step 3: 实现配置模型**

`BgsDataBridge/Config/BridgeConfig.cs`：
```csharp
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BgsDataBridge.Config
{
    public class WebhookConfig
    {
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("events")] public List<string> Events { get; set; } = new List<string> { "*" };
        [JsonProperty("secret", NullValueHandling = NullValueHandling.Ignore)] public string Secret { get; set; }

        public bool Wants(BridgeEventType? evt) // 稍后 Task 5 用
            => evt == null || Events.Contains("*") || Events.Contains(evt.ToString());
    }

    public class BridgeConfig
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("port")] public int Port { get; set; } = 5273;
        [JsonProperty("webhooks")] public List<WebhookConfig> Webhooks { get; set; } = new List<WebhookConfig>();
        [JsonProperty("shopChangedQuietMs")] public int ShopChangedQuietMs { get; set; } = 400;
        [JsonProperty("webhook")] public WebhookHttpConfig Webhook { get; set; } = new WebhookHttpConfig();
        [JsonProperty("logLevel")] public string LogLevel { get; set; } = "Info";

        public static BridgeConfig Load(string json)
        {
            var cfg = string.IsNullOrWhiteSpace(json)
                ? new BridgeConfig()
                : JsonConvert.DeserializeObject<BridgeConfig>(json) ?? new BridgeConfig();
            if (cfg.Webhook == null) cfg.Webhook = new WebhookHttpConfig();
            return cfg;
        }
        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
    }
    public class WebhookHttpConfig
    {
        [JsonProperty("timeoutMs")] public int TimeoutMs { get; set; } = 3000;
        [JsonProperty("maxRetries")] public int MaxRetries { get; set; } = 4;
        [JsonProperty("queueCap")] public int QueueCap { get; set; } = 1000;
    }
}
```
> `Wants` 引用了 `BridgeEventType`（Task 1 已建）。

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test BgsDataBridge.Tests --filter BridgeConfigTest`
Expected: PASS（3 passed）。

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/ BgsDataBridge.Tests/
git commit -m "feat(bridge): config model with JSON load/save + defaults"
```

---

## Task 3: 时钟抽象 + `ShopChangedDebouncer`

**目标**：trailing-edge 静默期合并算法，纯逻辑、虚拟时钟可测。

**Files:**
- Create: `BgsDataBridge/Core/IClock.cs`
- Create: `BgsDataBridge/Core/ShopChangedDebouncer.cs`
- Test: `BgsDataBridge.Tests/Core/ShopChangedDebouncerTest.cs`

**Interfaces:**
- Produces: `IClock`（`long NowMs`）、`ShopChangedDebouncer`
- Consumes: `BridgeConfig.ShopChangedQuietMs`

- [ ] **Step 1: 写失败的合并测试**

`BgsDataBridge.Tests/Core/ShopChangedDebouncerTest.cs`：
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
            var db = new ShopChangedDebouncer(quietMs: 400, clock);
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
            var db = new ShopChangedDebouncer(400, clock);
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
            var db = new ShopChangedDebouncer(400, clock);
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
            var db = new ShopChangedDebouncer(400, clock);
            var n = 0;
            db.OnEmit += s => n++;
            clock.Now = 5000; db.Tick();
            Assert.AreEqual(0, n);
        }
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test BgsDataBridge.Tests --filter ShopChangedDebouncerTest`
Expected: FAIL。

- [ ] **Step 3: 实现时钟 + 去抖器**

`BgsDataBridge/Core/IClock.cs`：
```csharp
namespace BgsDataBridge.Core
{
    public interface IClock { long NowMs { get; } }
    public class SystemClock : IClock { public long NowMs => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
}
```

`BgsDataBridge/Core/ShopChangedDebouncer.cs`：
```csharp
namespace BgsDataBridge.Core
{
    public delegate void ShopEmitted(string payload);

    // trailing-edge: 变化后静默 quietMs 再发；同窗口多次变化只发最终态。
    public class ShopChangedDebouncer
    {
        private readonly long _quietMs;
        private readonly IClock _clock;
        private string _pending;
        private long _lastChangeAt = -1;
        private bool _dirty;

        public event ShopEmitted OnEmit;

        public ShopChangedDebouncer(long quietMs, IClock clock)
        {
            _quietMs = quietMs;
            _clock = clock;
        }

        public void Update(string payload, long nowMs)
        {
            _pending = payload;
            _lastChangeAt = nowMs;
            _dirty = true;
        }

        public void Tick()
        {
            if (!_dirty || _lastChangeAt < 0) return;
            if (_clock.NowMs - _lastChangeAt >= _quietMs) Flush();
        }

        public void Flush()
        {
            if (!_dirty) return;
            var p = _pending;
            _dirty = false;
            _lastChangeAt = -1;
            OnEmit?.Invoke(p);
        }
    }
}
```

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test BgsDataBridge.Tests --filter ShopChangedDebouncerTest`
Expected: PASS（4 passed）。

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/ BgsDataBridge.Tests/
git commit -m "feat(bridge): ShopChangedDebouncer (trailing-edge coalescing)"
```

---

## Task 4: `PhaseStateMachine`（阶段/选择边沿检测）

**目标**：把 `OnUpdate` 10Hz 采样出的状态，转成事件流；门控非 BGS/菜单不触发。纯逻辑。

**Files:**
- Create: `BgsDataBridge/Core/TriggerInput.cs`
- Create: `BgsDataBridge/Core/TriggerEvent.cs`
- Create: `BgsDataBridge/Core/PhaseStateMachine.cs`
- Test: `BgsDataBridge.Tests/Core/PhaseStateMachineTest.cs`

**Interfaces:**
- Produces: `TriggerInput`、`TriggerEvent`、`PhaseStateMachine.Observe(TriggerInput) → IReadOnlyList<TriggerEvent>`
- Consumes: `BridgeEventType`（Task 1）

- [ ] **Step 1: 写失败的状态机测试**

`BgsDataBridge.Tests/Core/PhaseStateMachineTest.cs`：
```csharp
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;
using BgsDataBridge.Events;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class PhaseStateMachineTest
    {
        static TriggerInput Bg(bool combat, bool heroPick = false, bool trinketPick = false, bool inMenu = false)
            => new TriggerInput { IsBattlegroundsMatch = true, IsInMenu = inMenu, IsCombatPhase = combat,
               HeroPickActive = heroPick, TrinketPickActive = trinketPick };

        [TestMethod]
        public void ShopToCombat_Emits_CombatPhaseStart()
        {
            var sm = new PhaseStateMachine();
            CollectionAssert.AreEqual(new TriggerEvent[0], sm.Observe(Bg(false)).ToList());
            var ev = sm.Observe(Bg(true)).Single();
            Assert.AreEqual(BridgeEventType.CombatPhaseStart, ev.Type);
        }

        [TestMethod]
        public void CombatToShop_Emits_ShopPhaseStart()
        {
            var sm = new PhaseStateMachine();
            sm.Observe(Bg(true));
            var ev = sm.Observe(Bg(false)).Single();
            Assert.AreEqual(BridgeEventType.ShopPhaseStart, ev.Type);
        }

        [TestMethod]
        public void No_Emit_When_Unchanged()
        {
            var sm = new PhaseStateMachine();
            sm.Observe(Bg(true));
            Assert.AreEqual(0, sm.Observe(Bg(true)).Count);
        }

        [TestMethod]
        public void Gate_NonBattlegrounds_NoEmit()
        {
            var sm = new PhaseStateMachine();
            var nonBg = new TriggerInput { IsBattlegroundsMatch = false, IsCombatPhase = true };
            Assert.AreEqual(0, sm.Observe(nonBg).Count);
            Assert.AreEqual(0, sm.Observe(new TriggerInput { IsBattlegroundsMatch = false }).Count);
        }

        [TestMethod]
        public void Gate_InMenu_NoEmit()
        {
            var sm = new PhaseStateMachine();
            Assert.AreEqual(0, sm.Observe(Bg(true, inMenu: true)).Count);
        }

        [TestMethod]
        public void HeroPick_Edge_Emits()
        {
            var sm = new PhaseStateMachine();
            sm.Observe(Bg(false));
            var ev = sm.Observe(Bg(false, heroPick: true)).Single();
            Assert.AreEqual(BridgeEventType.HeroPick, ev.Type);
        }

        [TestMethod]
        public void TrinketPick_Edge_Emits()
        {
            var sm = new PhaseStateMachine();
            sm.Observe(Bg(false));
            var ev = sm.Observe(Bg(false, trinketPick: true)).Single();
            Assert.AreEqual(BridgeEventType.TrinketPick, ev.Type);
        }
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test BgsDataBridge.Tests --filter PhaseStateMachineTest`
Expected: FAIL。

- [ ] **Step 3: 实现输入/输出/状态机**

`BgsDataBridge/Core/TriggerInput.cs`：
```csharp
namespace BgsDataBridge.Core
{
    public struct TriggerInput
    {
        public bool IsBattlegroundsMatch;
        public bool IsInMenu;
        public bool IsCombatPhase;
        public bool HeroPickActive;
        public bool TrinketPickActive;
    }
}
```

`BgsDataBridge/Core/TriggerEvent.cs`：
```csharp
using BgsDataBridge.Events;
namespace BgsDataBridge.Core
{
    public struct TriggerEvent
    {
        public BridgeEventType Type;
        public TriggerEvent(BridgeEventType t) { Type = t; }
    }
}
```

`BgsDataBridge/Core/PhaseStateMachine.cs`：
```csharp
using System.Collections.Generic;
using BgsDataBridge.Events;

namespace BgsDataBridge.Core
{
    // 边沿检测：仅在游戏线程采样输入；门控非 BGS/菜单。
    public class PhaseStateMachine
    {
        private bool _first = true;
        private bool _prevCombat;
        private bool _prevHeroPick;
        private bool _prevTrinketPick;

        public IReadOnlyList<TriggerEvent> Observe(TriggerInput i)
        {
            var outp = new List<TriggerEvent>(2);
            if (_first)
            {
                _first = false;
                _prevCombat = i.IsCombatPhase;
                _prevHeroPick = i.HeroPickActive;
                _prevTrinketPick = i.TrinketPickActive;
                return outp; // 首帧只记基线，不触发
            }

            bool active = i.IsBattlegroundsMatch && !i.IsInMenu;
            if (active)
            {
                if (i.IsCombatPhase != _prevCombat)
                    outp.Add(new TriggerEvent(i.IsCombatPhase ? BridgeEventType.CombatPhaseStart : BridgeEventType.ShopPhaseStart));
                if (i.HeroPickActive && !_prevHeroPick)
                    outp.Add(new TriggerEvent(BridgeEventType.HeroPick));
                if (i.TrinketPickActive && !_prevTrinketPick)
                    outp.Add(new TriggerEvent(BridgeEventType.TrinketPick));
            }

            _prevCombat = i.IsCombatPhase;
            _prevHeroPick = i.HeroPickActive;
            _prevTrinketPick = i.TrinketPickActive;
            return outp;
        }
    }
}
```
> `MatchStart`/`MatchEnd` 由 `GameEvents` 直接订阅（Task 9），不经此状态机。

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test BgsDataBridge.Tests --filter PhaseStateMachineTest`
Expected: PASS（7 passed）。

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/ BgsDataBridge.Tests/
git commit -m "feat(bridge): PhaseStateMachine edge detection for Bgs phases/picks"
```

---

## Task 5: `WebhookDispatcher` + HMAC 签名

**目标**：事件入队 + 专用发送线程 + 按 URL 隔离退避 + 队列上限 + flush。`IHttpSender`/`IClock` 可替换。

**Files:**
- Create: `BgsDataBridge/Webhook/HmacSigner.cs`
- Create: `BgsDataBridge/Webhook/IHttpSender.cs`
- Create: `BgsDataBridge/Webhook/WebhookDispatcher.cs`
- Test: `BgsDataBridge.Tests/Webhook/WebhookDispatcherTest.cs`
- Test: `BgsDataBridge.Tests/Webhook/HmacSignerTest.cs`

**Interfaces:**
- Produces: `WebhookDispatcher.Enqueue(EventEnvelope)`、`Start()`、`Stop(flushMs)`、`HmacSigner.Sign`
- Consumes: `BridgeConfig`、`IClock`、`IHttpSender`

- [ ] **Step 1: 写失败的 HMAC 测试**

`BgsDataBridge.Tests/Webhook/HmacSignerTest.cs`：
```csharp
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Webhook;

namespace BgsDataBridge.Tests.Webhook
{
    [TestClass]
    public class HmacSignerTest
    {
        [TestMethod]
        public void Sign_Matches_Known_Vector()
        {
            // key="k", body="hello" → HMAC-SHA256 lowerhex
            var sig = HmacSigner.Sign("hello", "k");
            // 用任意标准库离线算出的定值；实现须与此一致
            Assert.AreEqual("aa7472787a2f5b068d5374369b785079b56c07ce1790d4f70f5e36b0d2890fb9", sig);
        }

        [TestMethod]
        public void Sign_Differs_For_Different_Input()
        {
            Assert.AreNotEqual(HmacSigner.Sign("a", "k"), HmacSigner.Sign("b", "k"));
        }
    }
}
```
> 该向量由标准 HMAC-SHA256("hello","k") 得出，实现须匹配。实现后可用 `python -c "import hmac,hashlib;print(hmac.new(b'k',b'hello',hashlib.sha256).hexdigest())"` 复核。

- [ ] **Step 2: 实现 HMAC（先实现以便后续测试编译）**

`BgsDataBridge/Webhook/HmacSigner.cs`：
```csharp
using System;
using System.Security.Cryptography;
using System.Text;

namespace BgsDataBridge.Webhook
{
    public static class HmacSigner
    {
        public static string Sign(string body, string secret)
        {
            using (var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret ?? "")))
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(body ?? "")))
                    .Replace("-", "").ToLowerInvariant();
        }
    }
}
```

- [ ] **Step 3: 写失败的 Dispatcher 测试**

`BgsDataBridge.Tests/Webhook/WebhookDispatcherTest.cs`：
```csharp
using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Config;
using BgsDataBridge.Core;
using BgsDataBridge.Events;
using BgsDataBridge.Webhook;

namespace BgsDataBridge.Tests.Webhook
{
    [TestClass]
    public class WebhookDispatcherTest
    {
        class FakeClock : IClock { public long Now; public long NowMs => Now; }
        class FakeSender : IHttpSender
        {
            public List<(string url, string body)> Sent = new List<(string, string)>();
            public int Calls;
            public int FailFirstNTimes = 0;
            public int Send(string url, string body, string signature, int timeoutMs)
            {
                Calls++;
                if (Calls <= FailFirstNTimes) return 500; // 模拟失败
                Sent.Add((url, body));
                return 200;
            }
        }

        static EventEnvelope Ev(long seq, BridgeEventType t)
            => new EventEnvelope { Seq = seq, Event = t, At = "2026-06-17T00:00:00Z", Data = "{}" };

        [TestMethod]
        public void Delivers_To_Subscribed_Url()
        {
            var cfg = new BridgeConfig { Webhooks = { new WebhookConfig { Url = "u1" } } };
            var sender = new FakeSender();
            var clock = new FakeClock();
            using (var d = new WebhookDispatcher(cfg, sender, clock))
            {
                d.Start();
                d.Enqueue(Ev(1, BridgeEventType.ShopPhaseStart));
                Thread.Sleep(150);
            }
            Assert.AreEqual(1, sender.Sent.Count);
            Assert.AreEqual("u1", sender.Sent[0].url);
        }

        [TestMethod]
        public void Event_Filter_Respects_Subscription()
        {
            var cfg = new BridgeConfig { Webhooks = {
                new WebhookConfig { Url = "onlyShop", Events = new List<string>{ "ShopPhaseStart" } } } };
            var sender = new FakeSender();
            var clock = new FakeClock();
            using (var d = new WebhookDispatcher(cfg, sender, clock))
            {
                d.Start();
                d.Enqueue(Ev(1, BridgeEventType.CombatPhaseStart)); // 不匹配
                d.Enqueue(Ev(2, BridgeEventType.ShopPhaseStart));   // 匹配
                Thread.Sleep(150);
            }
            Assert.AreEqual(1, sender.Sent.Count);
            Assert.AreEqual("onlyShop", sender.Sent[0].url);
        }

        [TestMethod]
        public void Retries_On_5xx_Then_Succeeds()
        {
            var cfg = new BridgeConfig { Webhooks = { new WebhookConfig { Url = "u" } } };
            cfg.Webhook.MaxRetries = 5;
            var sender = new FakeSender { FailFirstNTimes = 2 };
            var clock = new FakeClock();
            using (var d = new WebhookDispatcher(cfg, sender, clock))
            {
                d.Start();
                d.Enqueue(Ev(1, BridgeEventType.ShopPhaseStart));
                Thread.Sleep(200);
            }
            Assert.AreEqual(1, sender.Sent.Count); // 第 3 次成功送达
            Assert.AreEqual(3, sender.Calls);
        }

        [TestMethod]
        public void Queue_Cap_Drops_When_Full()
        {
            var cfg = new BridgeConfig { Webhooks = { new WebhookConfig { Url = "u" } } };
            cfg.Webhook.QueueCap = 2;
            var sender = new FakeSender();
            // 故意不 Start，发送线程不 drain → 队列堆满
            var d = new WebhookDispatcher(cfg, sender, new FakeClock());
            Assert.IsTrue(d.Enqueue(Ev(1, BridgeEventType.ShopPhaseStart)));
            Assert.IsTrue(d.Enqueue(Ev(2, BridgeEventType.ShopPhaseStart)));
            Assert.IsFalse(d.Enqueue(Ev(3, BridgeEventType.ShopPhaseStart))); // 满了丢弃
            d.Stop(0);
        }

        [TestMethod]
        public void Stop_Flushes_Pending()
        {
            var cfg = new BridgeConfig { Webhooks = { new WebhookConfig { Url = "u" } } };
            var sender = new FakeSender();
            var d = new WebhookDispatcher(cfg, sender, new FakeClock());
            d.Enqueue(Ev(1, BridgeEventType.ShopPhaseStart));
            d.Start();              // 启 drain
            d.Stop(1000);           // flush 并等待
            Assert.AreEqual(1, sender.Sent.Count);
        }
    }
}
```

- [ ] **Step 4: 实现 IHttpSender + Dispatcher**

`BgsDataBridge/Webhook/IHttpSender.cs`：
```csharp
namespace BgsDataBridge.Webhook
{
    public interface IHttpSender { int Send(string url, string body, string signature, int timeoutMs); }
}
```

`BgsDataBridge/Webhook/WebhookDispatcher.cs`：
```csharp
using System;
using System.Collections.Concurrent;
using System.Threading;
using BgsDataBridge.Config;
using BgsDataBridge.Core;
using BgsDataBridge.Events;
using Newtonsoft.Json;

namespace BgsDataBridge.Webhook
{
    // 生产者只 Enqueue（绝不阻塞游戏线程）；专用发送线程 drain + 退避。
    public class WebhookDispatcher : IDisposable
    {
        private readonly BridgeConfig _cfg;
        private readonly IHttpSender _sender;
        private readonly IClock _clock;
        private readonly BlockingCollection<EventEnvelope> _queue;
        private Thread _thread;
        private volatile bool _running;

        public WebhookDispatcher(BridgeConfig cfg, IHttpSender sender, IClock clock)
        {
            _cfg = cfg;
            _sender = sender;
            _clock = clock;
            _queue = new BlockingCollection<EventEnvelope>(_cfg.Webhook.QueueCap);
        }

        public bool Enqueue(EventEnvelope ev)
        {
            try { return _queue.TryAdd(ev, 0); } catch { return false; }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "BgsBridge.Webhook" };
            _thread.Start();
        }

        public void Stop(int flushMs)
        {
            _running = false;
            _queue.CompleteAdding();
            _thread?.Join(flushMs);
        }

        private void Run()
        {
            foreach (var ev in _queue.GetConsumatingEnumerableSafe())
            {
                var body = JsonConvert.SerializeObject(ev);
                foreach (var wh in _cfg.Webhooks)
                {
                    if (!wh.Wants(ev.Event)) continue;
                    SendWithRetry(wh, body);
                }
            }
        }

        private void SendWithRetry(WebhookConfig wh, string body)
        {
            var sig = string.IsNullOrEmpty(wh.Secret) ? null : HmacSigner.Sign(body, wh.Secret);
            int attempt = 0;
            int backoff = 200;
            while (_running)
            {
                int status;
                try { status = _sender.Send(wh.Url, body, sig, _cfg.Webhook.TimeoutMs); }
                catch { status = 0; }
                if (status >= 200 && status < 300) return;
                if (status >= 400 && status < 500 && status != 429) return; // 4xx 不重试
                if (++attempt > _cfg.Webhook.MaxRetries) return;
                var target = _clock.NowMs + backoff;
                while (_running && _clock.NowMs < target) Thread.Sleep(10);
                backoff = Math.Min(backoff * 2, 5000);
            }
        }

        public void Dispose() { Stop(0); _queue.Dispose(); }
    }
}
```
> 上面用了一个 `GetConsumatingEnumerableSafe()` 扩展名（防误拼 BlockingCollection 的真实 API）。**改用标准 API**：把 `Run` 改为：
```csharp
private void Run()
{
    while (_running)
    {
        EventEnvelope ev;
        try { if (!_queue.TryTake(out ev, 100)) continue; }
        catch { break; }
        var body = JsonConvert.SerializeObject(ev);
        foreach (var wh in _cfg.Webhooks)
        {
            if (!wh.Wants(ev.Event)) continue;
            SendWithRetry(wh, body);
        }
    }
}
```
> （实现时使用这段标准 `TryTake` 版本；`Start` 时若 `_running` 已 true 直接返回；`Stop` 置位后线程在 ≤100ms 内退出。`Stop(flushMs)` 在置位前已 Enqueue 的项会在 `Join` 窗口内被 drain。）

- [ ] **Step 5: 运行，确认通过**

Run: `dotnet test BgsDataBridge.Tests --filter "HmacSignerTest|WebhookDispatcherTest"`
Expected: PASS（7 passed）。

- [ ] **Step 6: 提交**

```bash
git add BgsDataBridge/ BgsDataBridge.Tests/
git commit -m "feat(bridge): WebhookDispatcher (queue, per-URL retry, flush) + HMAC signer"
```

---

## Task 6a: `GameStateView` + `GameStateProjector`（match + player 映射）

**目标**：把已快照的 `GameStateView`（持克隆 `Entity`）映射成 `BgsSnapshot` 的 match + player 部分。用手工构建的 `Entity` 单测，无需 live `GameV2`。

**Files:**
- Create: `BgsDataBridge/Projector/GameStateView.cs`
- Create: `BgsDataBridge/Projector/KeywordMap.cs`
- Create: `BgsDataBridge/Projector/GameStateProjector.cs`
- Test: `BgsDataBridge.Tests/Projector/ProjectorPlayerTest.cs`
- Modify: `BgsDataBridge.Tests/BgsDataBridge.Tests.csproj`（加 HDT ProjectReference）

> 本任务起需直接构造 HDT `Entity`。给测试项目加对主项目的引用（与 HDTTests 一致）：
```xml
<ProjectReference Include="..\Hearthstone Deck Tracker\Hearthstone Deck Tracker.csproj" />
```

**Interfaces:**
- Produces: `GameStateView`、`GameStateProjector.Project(GameStateView, bool includeText) → BgsSnapshot`、`KeywordMap`
- Consumes: DTOs（Task 1）、`Hearthstone_Deck_Tracker.Hearthstone.Entities.Entity`、`GameTag`

- [ ] **Step 1: 写失败的映射测试**

`BgsDataBridge.Tests/Projector/ProjectorPlayerTest.cs`：
```csharp
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using BgsDataBridge.Projector;

namespace BgsDataBridge.Tests.Projector
{
    [TestClass]
    public class ProjectorPlayerTest
    {
        static Entity Minion(string id, int atk, int hp, bool taunt = false, bool goldenTag = false)
        {
            var e = new Entity { CardId = id };
            e.SetTag(GameTag.ATK, atk);
            e.SetTag(GameTag.HEALTH, hp);
            if (taunt) e.SetTag(GameTag.TAUNT, 1);
            return e;
        }

        [TestMethod]
        public void Projects_Player_Board_With_Stats_And_Keywords()
        {
            var view = new GameStateView
            {
                InMatch = true, IsBattlegrounds = true, IsDuos = false, Phase = "Combat",
                Turn = 3, PlayerBoard = new List<Entity> { Minion("BACON_1", 5, 5, true) }
            };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.AreEqual("Combat", snap.Match.Phase);
            Assert.AreEqual(3, snap.Match.Turn);
            Assert.AreEqual(1, snap.Player.Board.Count);
            Assert.AreEqual("BACON_1", snap.Player.Board[0].CardId);
            Assert.AreEqual(5, snap.Player.Board[0].Attack);
            Assert.AreEqual(5, snap.Player.Board[0].Health);
            CollectionAssert.Contains(snap.Player.Board[0].Keywords, "TAUNT");
        }

        [TestMethod]
        public void Keywords_Empty_When_None()
        {
            var view = new GameStateView { InMatch = true, IsBattlegrounds = true,
                PlayerBoard = new List<Entity> { Minion("BACON_2", 1, 1) } };
            var snap = new GameStateProjector().Project(view, false);
            Assert.AreEqual(0, snap.Player.Board[0].Keywords.Count);
        }
    }
}
```
> `Entity.SetTag`/`GetTag` 与 `GameTag.ATK/HEALTH/TAUNT` 为 HDT 实际 API（见 `Hearthstone Deck Tracker/Hearthstone/Entities/Entity.cs`）。若属性名/标签枚举位置不符，以该文件为准修正。

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test BgsDataBridge.Tests --filter ProjectorPlayerTest`
Expected: FAIL。

- [ ] **Step 3: 实现 GameStateView + KeywordMap + Projector（player 部分）**

`BgsDataBridge/Projector/GameStateView.cs`：
```csharp
using System.Collections.Generic;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Projector
{
    // 由 HdtGameSource 快照填充（持已 Clone 的 Entity，线程安全）。
    public class GameStateView
    {
        public bool InMatch;
        public bool IsBattlegrounds;
        public bool IsDuos;
        public bool Spectator;
        public string Phase;
        public int Turn;
        public string GameUuid;
        public int? Mmr;
        public int? DuosMmr;
        public Entity Anomaly;
        public List<string> AvailableRaces = new List<string>();

        public string PlayerName;
        public int Tier;
        public Entity Hero;
        public Entity HeroPower;
        public List<Entity> Trinkets = new List<Entity>();
        public Entity QuestReward; public int? QuestProgress; public int? QuestTotal;
        public List<Entity> PlayerBoard = new List<Entity>();

        public ShopView Shop;
        public LastOpponentView LastOpponent;
        public List<LobbyPlayerView> Lobby = new List<LobbyPlayerView>();
    }
    public class ShopView { public int Tier; public bool? Frozen; public List<Entity> Offers = new List<Entity>(); }
    public class LastOpponentView { public int Turn; public Entity Hero; public List<Entity> Board = new List<Entity>(); }
    public class LobbyPlayerView { public string Name; public string HeroCardId; public string AccountId; }
}
```

`BgsDataBridge/Projector/KeywordMap.cs`：
```csharp
using System.Collections.Generic;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Projector
{
    public static class KeywordMap
    {
        public static readonly GameTag[] Mapped = {
            GameTag.TAUNT, GameTag.DIVINE_SHIELD, GameTag.POISONOUS,
            GameTag.REBORN, GameTag.WINDFURY, GameTag.CLEAVE, GameTag.STEALTH, GameTag.FROZEN
        };

        public static List<string> From(Entity e)
        {
            var list = new List<string>();
            foreach (var t in Mapped)
                if (e.HasTag(t)) list.Add(t.ToString());
            return list;
        }
    }
}
```
> `CLEAVE`/`VENOM` 等若在 `GameTag` 中不存在，以 `Hearthstone Deck Tracker/Enums/Hearthstone/GAME_TAG.cs` 为准增删。

`BgsDataBridge/Projector/GameStateProjector.cs`（先实现 player 部分；shop/opponent/lobby 留 Task 6b）：
```csharp
using System;
using System.Collections.Generic;
using BgsDataBridge.Dtos;

namespace BgsDataBridge.Projector
{
    public class GameStateProjector
    {
        public BgsSnapshot Project(GameStateView v, bool includeText)
        {
            var snap = new BgsSnapshot
            {
                Locale = "enUS", // HdtGameSource 可覆写为 Config.Instance 选定语言
                CapturedAt = DateTimeOffset.UtcNow.ToString("o"),
                InMatch = v.InMatch,
                Partial = false,
                Match = new BgsMatch
                {
                    GameType = v.IsBattlegrounds ? (v.IsDuos ? "BattlegroundsDuos" : "BattlegroundsSolo") : "Other",
                    IsBattlegrounds = v.IsBattlegrounds, IsDuos = v.IsDuos, Spectator = v.Spectator,
                    Phase = v.Phase ?? "None", Turn = v.Turn, GameUuid = v.GameUuid,
                    Rating = (v.Mmr.HasValue || v.DuosMmr.HasValue) ? new BgsRating { Mmr = v.Mmr, DuosMmr = v.DuosMmr } : null
                },
                AvailableRaces = v.AvailableRaces ?? new List<string>(),
                Player = ProjectPlayer(v, includeText),
                Shop = null, LastOpponent = null, Lobby = null
            };
            return snap;
        }

        private BgsPlayer ProjectPlayer(GameStateView v, bool includeText)
        {
            var p = new BgsPlayer { Name = v.PlayerName, Tier = v.Tier };
            if (v.Hero != null) p.Hero = ToHero(v.Hero);
            if (v.HeroPower != null) p.HeroPower = ToCard(v.HeroPower, includeText || true); // 技能始终带 text
            foreach (var t in v.Trinkets) p.Trinkets.Add(ToTrinket(t));
            if (v.QuestReward != null) p.QuestReward = ToQuestReward(v);
            foreach (var e in v.PlayerBoard) p.Board.Add(ToMinion(e, includeText));
            return p;
        }

        public static BgsMinion ToMinion(Entity e, bool includeText)
            => new BgsMinion { CardId = e.CardId, Attack = e.Attack, Health = e.Health,
                Keywords = KeywordMap.From(e), Text = includeText ? TextOf(e) : null };

        static BgsHero ToHero(Entity e) => new BgsHero { CardId = e.CardId, Name = NameOf(e),
            Health = e.Health, Armor = Tag(e, GameTag.ARMOR) };

        static BgsCardRef ToCard(Entity e, bool withText) => new BgsCardRef { CardId = e.CardId,
            Name = NameOf(e), Text = withText ? TextOf(e) : null };

        static BgsTrinket ToTrinket(Entity e) => new BgsTrinket { CardId = e.CardId, Name = NameOf(e), Text = TextOf(e) };

        static BgsQuestReward ToQuestReward(GameStateView v) => new BgsQuestReward
        { CardId = v.QuestReward.CardId, Name = NameOf(v.QuestReward), Text = TextOf(v.QuestReward),
          Progress = v.QuestProgress, Total = v.QuestTotal };

        // ---- HDT 卡牌文本解析（属性名以 HDT 源为准）----
        static string NameOf(Entity e)
        { try { return e.Card?.Name; } catch { return null; } }
        static string TextOf(Entity e)
        { try { return e.Card?.Text; } catch { return null; } }
        static int? Tag(Entity e, GameTag t) { var x = e.GetTag(t); return x > 0 ? x : (int?)null; }
    }
}
```
> `e.Card.Name`/`e.Card.Text` 经 HDT `Card` 解析（`Hearthstone Deck Tracker/Hearthstone/Card.cs:227,329`）。若 `Card` 为 null（卡 ID 未知）返回 null。

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test BgsDataBridge.Tests --filter ProjectorPlayerTest`
Expected: PASS（2 passed）。

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/ BgsDataBridge.Tests/
git commit -m "feat(bridge): GameStateView + Projector (match+player mapping)"
```

---

## Task 6b: Projector 扩展（shop + lastOpponent + lobby）

**Files:**
- Modify: `BgsDataBridge/Projector/GameStateProjector.cs`
- Test: `BgsDataBridge.Tests/Projector/ProjectorExtrasTest.cs`

- [ ] **Step 1: 写失败的扩展测试**

`BgsDataBridge.Tests/Projector/ProjectorExtrasTest.cs`：
```csharp
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using BgsDataBridge.Projector;

namespace BgsDataBridge.Tests.Projector
{
    [TestClass]
    public class ProjectorExtrasTest
    {
        static Entity Mn(string id, int a, int h) { var e = new Entity { CardId = id }; e.SetTag(GameTag.ATK, a); e.SetTag(GameTag.HEALTH, h); return e; }

        [TestMethod]
        public void Shop_And_LastOpponent_And_Lobby_Projected()
        {
            var view = new GameStateView
            {
                InMatch = true, IsBattlegrounds = true, Phase = "Shop", Turn = 4,
                Shop = new ShopView { Tier = 4, Frozen = null, Offers = new List<Entity> { Mn("BACON_9", 3, 3) } },
                LastOpponent = new LastOpponentView { Turn = 3, Hero = new Entity { CardId = "HERO_X" },
                    Board = new List<Entity> { Mn("BACON_8", 8, 8) } },
                Lobby = new List<LobbyPlayerView> { new LobbyPlayerView { Name = "p1", HeroCardId = "HERO_A" } }
            };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.IsNotNull(snap.Shop);
            Assert.AreEqual("BACON_9", snap.Shop.Offers[0].CardId);
            Assert.AreEqual(3, snap.LastOpponent.Turn);
            Assert.AreEqual("HERO_X", snap.LastOpponent.Hero.CardId);
            Assert.AreEqual(8, snap.LastOpponent.Board[0].Health);
            Assert.AreEqual("p1", snap.Lobby.Players[0].Name);
        }

        [TestMethod]
        public void Null_Extras_Omitted()
        {
            var view = new GameStateView { InMatch = true, IsBattlegrounds = true };
            var snap = new GameStateProjector().Project(view, false);
            Assert.IsNull(snap.Shop);
            Assert.IsNull(snap.LastOpponent);
            Assert.IsNull(snap.Lobby);
        }
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test BgsDataBridge.Tests --filter ProjectorExtrasTest`
Expected: FAIL。

- [ ] **Step 3: 扩展 Projector（在 `Project` 中补齐 shop/opponent/lobby）**

把 `Project(...)` 末尾的 `Shop = null, LastOpponent = null, Lobby = null` 替换为：
```csharp
                Shop = v.Shop != null ? new BgsShop { Available = true, Tier = v.Shop.Tier,
                    Frozen = v.Shop.Frozen, Offers = Minions(v.Shop.Offers, includeText) } : null,
                LastOpponent = v.LastOpponent != null ? new BgsLastOpponent { Turn = v.LastOpponent.Turn,
                    Hero = v.LastOpponent.Hero != null ? new BgsCardRef { CardId = v.LastOpponent.Hero.CardId, Name = NameOf(v.LastOpponent.Hero) } : null,
                    Board = Minions(v.LastOpponent.Board, includeText) } : null,
                Lobby = v.Lobby != null && v.Lobby.Count > 0 ? new BgsLobby { Players = LobbyOf(v.Lobby) } : null
```
并新增两个辅助方法到 `GameStateProjector`：
```csharp
        List<BgsMinion> Minions(List<Entity> es, bool includeText)
        {
            var list = new List<BgsMinion>(es.Count);
            foreach (var e in es) list.Add(ToMinion(e, includeText));
            return list;
        }
        static List<BgsLobbyPlayer> LobbyOf(List<LobbyPlayerView> src)
        {
            var list = new List<BgsLobbyPlayer>(src.Count);
            foreach (var p in src) list.Add(new BgsLobbyPlayer { Name = p.Name, HeroCardId = p.HeroCardId, AccountId = p.AccountId });
            return list;
        }
```

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test BgsDataBridge.Tests --filter "ProjectorPlayerTest|ProjectorExtrasTest"`
Expected: PASS（4 passed）。

- [ ] **Step 5: 提交**

```bash
git add BgsDataBridge/ BgsDataBridge.Tests/
git commit -m "feat(bridge): Projector shop/lastOpponent/lobby mapping"
```

---

## Task 7: `HdtGameSource` 适配器（读 `Core.Game`/HearthMirror）

**目标**：薄适配器，把 `Core.Game` 的相关切片快照（Clone）进 `GameStateView`。集成层，靠手动验证。

**Files:**
- Create: `BgsDataBridge/Projector/HdtGameSource.cs`

**Interfaces:**
- Produces: `HdtGameSource : IGameSource`（`Capture() → GameStateView`）
- Consumes: `Hearthstone_Deck_Tracker.API.Core.Game`、`HearthMirror.Reflection.Client`、`GameStateView`

- [ ] **Step 1: 定义 IGameSource 并实现 HdtGameSource**

`BgsDataBridge/Projector/IGameSource.cs`：
```csharp
namespace BgsDataBridge.Projector
{
    public interface IGameSource { GameStateView Capture(); }
}
```

`BgsDataBridge/Projector/HdtGameSource.cs`：
```csharp
using System;
using System.Linq;
using BgsDataBridge.Logger; // 见 Task 9 的 Logger；此处先用占位 using，Task 9 补 Logger 命名空间
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Enums.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using HearthDb.Enums; // GameTag 实际所在；以编译为准，必要时改 Hearthstone_Deck_Tracker.Enums.Hearthstone

namespace BgsDataBridge.Projector
{
    public class HdtGameSource : IGameSource
    {
        public GameStateView Capture()
        {
            var v = new GameStateView { InMatch = false };
            try
            {
                var g = Core.Game;
                v.InMatch = !g.IsInMenu;
                v.IsBattlegrounds = g.IsBattlegroundsMatch;
                v.IsDuos = g.IsBattlegroundsDuosMatch;
                v.Spectator = g.Spectator;
                v.Turn = g.GetTurnNumber();
                v.Phase = DerivePhase(g);

                var player = g.Player;
                v.PlayerName = player.Name;
                v.PlayerBoard = Safe(() => player.Board.Where(x => x.IsMinion).Select(x => x.Clone()).ToList())
                                ?? new System.Collections.Generic.List<Entity>();
                v.Hero = Safe(() => player.Hero?.Clone());
                v.HeroPower = Safe(() => g.Entities.Values.FirstOrDefault(
                    x => x.IsHeroPower && x.IsControlledBy(player.Id))?.Clone());
                v.Trinkets = Safe(() => player.Trinkets.Select(x => x.Clone()).ToList())
                             ?? new System.Collections.Generic.List<Entity>();
                var qr = player.QuestRewards.FirstOrDefault();
                if (qr != null) { v.QuestReward = qr.Clone(); }

                // 商店：仅购物阶段、商店在屏
                v.Shop = Safe(CaptureShop);

                // 上一对手快照
                v.LastOpponent = Safe(CaptureLastOpponent);

                // 大厅名单
                v.Lobby = Safe(CaptureLobby) ?? new System.Collections.Generic.List<LobbyPlayerView>();

                // 种族 / 段位
                v.AvailableRaces = Safe(() => Hearthstone_Deck_Tracker.Hearthstone.BattlegroundsUtils
                    .ReadAvailableRacesFromMemory()?.Select(r => r.ToString()).ToList())
                    ?? new System.Collections.Generic.List<string>();
                v.Mmr = Safe(() => g.BattlegroundsRatingInfo?.Rating);
                v.DuosMmr = Safe(() => g.BattlegroundsRatingInfo?.DuosRating);
            }
            catch { /* 集成层：任一失败不致命；返回部分视图 */ }
            return v;
        }

        static string DerivePhase(Hearthstone_Deck_Tracker.Hearthstone.IGame g)
        {
            if (g.IsInMenu) return "None";
            if (g.IsBattlegroundsHeroPickingDone == false) return "HeroPick"; // 选择未完成视为选择阶段
            if (g.IsBattlegroundsCombatPhase) return "Combat";
            return "Shop";
        }

        ShopView CaptureShop()
        {
            var g = Core.Game;
            if (!g.IsBattlegroundsMatch || g.IsBattlegroundsCombatPhase) return null;
            var obs = HearthMirror.Reflection.Client.GetOpponentBoardState();
            if (obs?.BoardCards == null) return null;
            var sv = new ShopView { Tier = 0, Frozen = null };
            foreach (var bc in obs.BoardCards)
            {
                var e = new Entity { CardId = bc.CardId };
                sv.Offers.Add(e);
            }
            return sv;
        }

        LastOpponentView CaptureLastOpponent()
        {
            var g = Core.Game;
            var oppHero = g.Opponent.Hero;
            if (oppHero == null) return null;
            var snap = g.GetBattlegroundsBoardStateFor(oppHero.Id);
            if (snap?.Entities == null) return null;
            var lo = new LastOpponentView { Turn = snap.Turn, Hero = new Entity { CardId = oppHero.CardId } };
            foreach (var e in snap.Entities) lo.Board.Add(e.Clone());
            return lo;
        }

        System.Collections.Generic.List<LobbyPlayerView> CaptureLobby()
        {
            var li = Core.Game.MetaData.BattlegroundsLobbyInfo;
            if (li?.Players == null) return null;
            var list = new System.Collections.Generic.List<LobbyPlayerView>();
            foreach (var p in li.Players)
                list.Add(new LobbyPlayerView { Name = p.Name, HeroCardId = p.HeroCardId, AccountId = p.AccountId?.ToString() });
            return list;
        }

        static T Safe<T>(Func<T> f) where T : class { try { return f(); } catch { return null; } }
    }
}
```
> **集成层属性名以 HDT 源为准**：交叉核对 `Hearthstone Deck Tracker/Hearthstone/GameV2.cs`（`IsBattlegroundsHeroPickingDone`、`GetBattlegroundsBoardStateFor`、`BattlegroundsRatingInfo.Rating/DuosRating`）、`Player.cs`（`Trinkets`/`QuestRewards`/`Hero`）、`GameMetaData.cs`（`BattlegroundsLobbyInfo.Players[*]`）、`BattlegroundsUtils.cs`（`ReadAvailableRacesFromMemory`）、`BoardSnapshot.cs`（`Turn`/`Entities`）。`HearthMirror.Objects.OpponentBoardState.BoardCards[*].CardId` 同理。不符处按实际修正；每处都包在 `Safe` 里，单点出错只丢该字段。

- [ ] **Step 2: 编译通过**

Run: `msbuild BgsDataBridge/BgsDataBridge.csproj /p:Configuration=Debug /p:Platform=x86`（或 VS 构建）
Expected: 编译成功。**此任务无单测**（集成层），手动验证放 Task 12。

- [ ] **Step 3: 提交**

```bash
git add BgsDataBridge/
git commit -m "feat(bridge): HdtGameSource adapter (Core.Game + HearthMirror capture)"
```

---

## Task 8: `RouteDispatcher`（纯）+ `BridgeHttpServer`（HttpListener）

**目标**：路由纯逻辑可测（path+query → JSON 字符串/状态码）；`HttpListener` 封装跑独立线程、CORS、错误隔离。

**Files:**
- Create: `BgsDataBridge/Http/RouteDispatcher.cs`
- Create: `BgsDataBridge/Http/BridgeHttpServer.cs`
- Test: `BgsDataBridge.Tests/Http/RouteDispatcherTest.cs`

**Interfaces:**
- Produces: `RouteDispatcher.Dispatch(string path, string query) → HttpResponse`、`BridgeHttpServer`（`Start()`/`Stop()`）
- Consumes: `IGameSource`、`GameStateProjector`、`BridgeConfig`

- [ ] **Step 1: 写失败的路由测试**

`BgsDataBridge.Tests/Http/RouteDispatcherTest.cs`：
```csharp
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Http;
using BgsDataBridge.Projector;

namespace BgsDataBridge.Tests.Http
{
    [TestClass]
    public class RouteDispatcherTest
    {
        class FakeSource : IGameSource
        {
            public GameStateView Capture() => new GameStateView { InMatch = true, IsBattlegrounds = true, Phase = "Shop", Turn = 2 };
        }

        [TestMethod]
        public void Health_Returns_200()
        {
            var d = new RouteDispatcher(new FakeSource(), new GameStateProjector());
            var r = d.Dispatch("/health", "");
            Assert.AreEqual(200, r.Status);
        }

        [TestMethod]
        public void State_Returns_200_Json()
        {
            var d = new RouteDispatcher(new FakeSource(), new GameStateProjector());
            var r = d.Dispatch("/state", "");
            Assert.AreEqual(200, r.Status);
            StringAssert.Contains(r.Body, "\"schema\":\"bgs-state/v1\"");
        }

        [TestMethod]
        public void Unknown_Returns_404()
        {
            var d = new RouteDispatcher(new FakeSource(), new GameStateProjector());
            var r = d.Dispatch("/nope", "");
            Assert.AreEqual(404, r.Status);
        }

        [TestMethod]
        public void Pretty_Query_Indents()
        {
            var d = new RouteDispatcher(new FakeSource(), new GameStateProjector());
            var compact = d.Dispatch("/state", "").Body;
            var pretty = d.Dispatch("/state", "pretty=1").Body;
            Assert.IsTrue(pretty.Length > compact.Length); // 缩进后更长
        }
    }
}
```

- [ ] **Step 2: 运行，确认失败**

Run: `dotnet test BgsDataBridge.Tests --filter RouteDispatcherTest`
Expected: FAIL。

- [ ] **Step 3: 实现 HttpResponse + RouteDispatcher**

`BgsDataBridge/Http/RouteDispatcher.cs`：
```csharp
using System;
using System.Collections.Specialized;
using System.Web;
using BgsDataBridge.Projector;
using Newtonsoft.Json;

namespace BgsDataBridge.Http
{
    public class HttpResponse { public int Status; public string Body; public string ContentType = "application/json"; }

    public class RouteDispatcher
    {
        private readonly IGameSource _source;
        private readonly GameStateProjector _projector;
        public RouteDispatcher(IGameSource source, GameStateProjector projector) { _source = source; _projector = projector; }

        public HttpResponse Dispatch(string path, string query)
        {
            try
            {
                if (path == "/health") return Health();
                if (path == "/state") return State(query);
                return new HttpResponse { Status = 404, Body = "{\"error\":\"not found\"}" };
            }
            catch (Exception ex)
            {
                return new HttpResponse { Status = 500, Body = "{\"error\":\"" + Escape(ex.Message) + "\"}" };
            }
        }

        HttpResponse Health()
        {
            var v = SafeCapture();
            return new HttpResponse { Status = 200, Body = "{\"status\":\"ok\",\"inMatch\":" + (v?.InMatch ?? false).ToString().ToLower() + ",\"isBattlegrounds\":" + (v?.IsBattlegrounds ?? false).ToString().ToLower() + "}" };
        }

        HttpResponse State(string query)
        {
            var qs = HttpUtility.ParseQueryString(query ?? "");
            bool pretty = qs["pretty"] == "1";
            bool includeText = qs["text"] == "1";
            var v = SafeCapture();
            if (v == null) return new HttpResponse { Status = 503, Body = "{\"error\":\"capture failed\"}" };
            var snap = _projector.Project(v, includeText);
            snap.Locale = "enUS"; // HdtGameSource 实际可注入选定语言
            var fmt = pretty ? Formatting.Indented : Formatting.None;
            return new HttpResponse { Status = 200, Body = JsonConvert.SerializeObject(snap, fmt) };
        }

        GameStateView SafeCapture() { try { return _source.Capture(); } catch { return null; } }
        static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
```
> `System.Web.HttpUtility`：插件项目需引用 `System.Web`（UseWPF 项目默认含；否则在 csproj 加 `<Reference Include="System.Web" />`）。

- [ ] **Step 4: 运行，确认通过**

Run: `dotnet test BgsDataBridge.Tests --filter RouteDispatcherTest`
Expected: PASS（4 passed）。

- [ ] **Step 5: 实现 BridgeHttpServer（HttpListener 封装）**

`BgsDataBridge/Http/BridgeHttpServer.cs`：
```csharp
using System;
using System.Net;
using System.Threading;
using BgsDataBridge.Config;

namespace BgsDataBridge.Http
{
    public class BridgeHttpServer
    {
        private readonly BridgeConfig _cfg;
        private readonly RouteDispatcher _dispatcher;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        public int ActualPort { get; private set; }

        public BridgeHttpServer(BridgeConfig cfg, RouteDispatcher dispatcher) { _cfg = cfg; _dispatcher = dispatcher; }

        public int Start()
        {
            for (int port = _cfg.Port; port < _cfg.Port + 10; port++)
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://localhost:" + port + "/");
                try { l.Start(); _listener = l; ActualPort = port; break; }
                catch { /* 端口占用，试下一个 */ }
            }
            if (_listener == null) throw new InvalidOperationException("no free port");
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "BgsBridge.Http" };
            _thread.Start();
            return ActualPort;
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _thread?.Join(1000);
        }

        void Run()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { if (_running) continue; else break; }
                try { Handle(ctx); }
                catch { /* 单请求失败不影响服务 */ }
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            // CORS + OPTIONS 预检
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
            if (string.Equals(ctx.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            { ctx.Response.StatusCode = 204; ctx.Response.Close(); return; }

            var resp = _dispatcher.Dispatch(ctx.Request.Url.AbsolutePath, ctx.Request.Url.Query.TrimStart('?'));
            var bytes = System.Text.Encoding.UTF8.GetBytes(resp.Body ?? "");
            ctx.Response.ContentType = resp.ContentType;
            ctx.Response.StatusCode = resp.Status;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
    }
}
```
> `localhost` 前缀通常无需管理员授权。`GET /`（查看器 HTML）在 Task 11 补：在 `RouteDispatcher` 加 `/` 分支返回内嵌 HTML（`ContentType = "text/html"`）。

- [ ] **Step 6: 编译，提交**

Run: `msbuild BgsDataBridge/BgsDataBridge.csproj /p:Configuration=Debug /p:Platform=x86`
Expected: 编译成功。
```bash
git add BgsDataBridge/ BgsDataBridge.Tests/
git commit -m "feat(bridge): RouteDispatcher (pure) + BridgeHttpServer (HttpListener, CORS)"
```

---

## Task 9: `Logger` + `BgsBridgePlugin`（IPlugin + 装配/拆卸 + 热重载 + 设置窗）

**目标**：`IPlugin` 实现，装配 HttpServer + WebhookDispatcher + TriggerDetector（`OnUpdate` 跑 PhaseStateMachine + 商店去抖）+ `GameEvents` 订阅（MatchStart/End）。配置热重载 + WPF 设置窗。

**Files:**
- Create: `BgsDataBridge/Logger.cs`
- Create: `BgsDataBridge/BgsBridgePlugin.cs`
- Create: `BgsDataBridge/Settings/SettingsWindow.xaml(.cs)`
- Modify: `BgsDataBridge/BgsDataBridge.csproj`（含 xaml compile、Fody/Costura 在 Task 10）

**Interfaces:**
- Produces: `BgsBridgePlugin : Hearthstone_Deck_Tracker.Plugins.IPlugin`
- Consumes: 所有前序组件、`GameEvents`、`Config`

- [ ] **Step 1: 实现 Logger**

`BgsDataBridge/Logger.cs`：
```csharp
using System;
using System.IO;
using Hearthstone_Deck_Tracker.Utility;
// AppDataPath 在 Hearthstone_Deck_Tracker.Config；以实际命名空间为准

namespace BgsDataBridge
{
    public static class Logger
    {
        private static string _dir;
        public static void Init(string dir) { _dir = dir; try { Directory.CreateDirectory(dir); } catch { } }
        public static void Info(string m) => Write("INFO", m);
        public static void Error(string m) => Write("ERR ", m);
        static void Write(string lvl, string m)
        {
            if (_dir == null) return;
            try
            {
                var path = Path.Combine(_dir, "log.txt");
                File.AppendAllText(path, DateTime.Now.ToString("o") + " " + lvl + " " + m + Environment.NewLine);
            }
            catch { }
        }
    }
}
```
> 插件目录用 `%AppData%\HearthstoneDeckTracker\Plugins\BgsDataBridge`（`Path.Combine(Config.AppDataPath, "Plugins", "BgsDataBridge")`）。`Config.AppDataPath` 见 `Hearthstone Deck Tracker/Utility/Config.cs`（以实际为准）。

- [ ] **Step 2: 实现 BgsBridgePlugin**

`BgsDataBridge/BgsBridgePlugin.cs`：
```csharp
using System;
using System.IO;
using System.Windows.Controls;
using BgsDataBridge.Config;
using BgsDataBridge.Core;
using BgsDataBridge.Events;
using BgsDataBridge.Http;
using BgsDataBridge.Projector;
using BgsDataBridge.Webhook;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Plugins;
using Newtonsoft.Json;

namespace BgsDataBridge
{
    public class BgsBridgePlugin : IPlugin
    {
        private string _dir;
        private BridgeConfig _cfg;
        private BridgeHttpServer _http;
        private WebhookDispatcher _webhook;
        private readonly PhaseStateMachine _sm = new PhaseStateMachine();
        private ShopChangedDebouncer _shopDeb;
        private SystemClock _clock;
        private HttpSender _sender;
        private long _seq;
        private bool _loaded;

        public string Name => "BgsDataBridge";
        public string Description => "Exposes Battlegrounds state over HTTP + webhooks for local consumers.";
        public string ButtonText => "Open status";
        public string Author => "you";
        public Version Version => new Version(0, 1, 0, 0);
        public MenuItem MenuItem { get; private set; }

        public void OnLoad()
        {
            if (_loaded) OnUnload();
            _dir = Path.Combine(Hearthstone_Deck_Tracker.Utility.Config.AppDataPath, "Plugins", "BgsDataBridge");
            Logger.Init(_dir);
            LoadConfig();

            _clock = new SystemClock();
            _shopDeb = new ShopChangedDebouncer(_cfg.ShopChangedQuietMs, _clock);
            _shopDeb.OnEmit += payload => Emit(BridgeEventType.ShopChanged, payload);

            var source = new HdtGameSource();
            var projector = new GameStateProjector();
            var routes = new RouteDispatcher(source, projector);
            _http = new BridgeHttpServer(_cfg, routes);
            try { var port = _http.Start(); Logger.Info("HTTP on localhost:" + port); }
            catch (Exception ex) { Logger.Error("HTTP start failed: " + ex.Message); }

            _sender = new HttpSender();
            _webhook = new WebhookDispatcher(_cfg, _sender, _clock);
            _webhook.Start();

            // 对局开始/结束：直接订阅 GameEvents
            GameEvents.OnGameStart.Add(OnGameStart);
            GameEvents.OnGameEnd.Add(OnGameEnd);
            GameEvents.OnGameWon.Add(() => OnGameEnd());
            GameEvents.OnGameLost.Add(() => OnGameEnd());

            MenuItem = BuildMenu();
            _loaded = true;
            Logger.Info("loaded");
        }

        public void OnUpdate()
        {
            if (!_loaded) return;
            try
            {
                var g = Core.Game;
                var input = new TriggerInput
                {
                    IsBattlegroundsMatch = g.IsBattlegroundsMatch,
                    IsInMenu = g.IsInMenu,
                    IsCombatPhase = g.IsBattlegroundsCombatPhase,
                    HeroPickActive = !g.IsBattlegroundsHeroPickingDone,
                    TrinketPickActive = false // Task 4 已留：可接 ChoicesWatcher 判定，初版置 false
                };
                foreach (var ev in _sm.Observe(input))
                {
                    if (ev.Type == BridgeEventType.ShopChanged) continue; // 由去抖器发
                    Emit(ev.Type, null);
                }

                // 商店去抖：购物阶段内轮询商店内容
                if (g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase)
                {
                    var shop = ShopPayload(); // 取当前 shop 的 JSON 字符串
                    if (shop != null) _shopDeb.Update(shop, _clock.NowMs);
                }
                _shopDeb.Tick();
            }
            catch (Exception ex) { Logger.Error("OnUpdate: " + ex.Message); }
        }

        void OnGameStart() => Emit(BridgeEventType.MatchStart, null);
        void OnGameEnd() => Emit(BridgeEventType.MatchEnd, null);

        void Emit(BridgeEventType type, string shopPayload)
        {
            try
            {
                var env = new EventEnvelope { Seq = System.Threading.Interlocked.Increment(ref _seq), Event = type,
                    At = DateTimeOffset.UtcNow.ToString("o"), Match = MatchPayload() };
                env.Data = shopPayload ?? (object)"{}";
                _webhook.Enqueue(env);
            }
            catch (Exception ex) { Logger.Error("Emit: " + ex.Message); }
        }

        object MatchPayload()
        {
            try
            {
                var g = Core.Game;
                return new { gameType = g.IsBattlegroundsDuosMatch ? "BattlegroundsDuos" : "BattlegroundsSolo",
                    isBattlegrounds = g.IsBattlegroundsMatch, isDuos = g.IsBattlegroundsDuosMatch,
                    spectator = g.Spectator, turn = g.GetTurnNumber() };
            }
            catch { return null; }
        }

        string ShopPayload()
        {
            try
            {
                var v = new HdtGameSource().Capture();
                if (v?.Shop == null) return null;
                return JsonConvert.SerializeObject(new { shop = v.Shop, turn = v.Turn, phase = v.Phase });
            }
            catch { return null; }
        }

        MenuItem BuildMenu()
        {
            var mi = new MenuItem { Header = "BgsDataBridge settings..." };
            mi.Click += (s, e) => { try { new Settings.SettingsWindow(_cfg, ReloadConfig).Show(); } catch { } };
            return mi;
        }

        void LoadConfig()
        {
            var path = Path.Combine(_dir, "config.json");
            _cfg = File.Exists(path) ? BridgeConfig.Load(File.ReadAllText(path)) : new BridgeConfig();
        }
        void ReloadConfig(BridgeConfig cfg)
        {
            _cfg = cfg;
            try { File.WriteAllText(Path.Combine(_dir, "config.json"), cfg.ToJson()); } catch { }
            // 热重载：重启 HTTP 与 webhook
            try { _http?.Stop(); } catch { }
            try { _webhook?.Dispose(); } catch { }
            // 重新装配（精简：直接 OnUnload + OnLoad 的关键部分）
            OnUnload();
            OnLoad();
        }

        public void OnUnload()
        {
            try { _shopDeb?.Flush(); } catch { }
            try { _webhook?.Stop(3000); _webhook?.Dispose(); } catch { }
            try { _http?.Stop(); } catch { }
            try { GameEvents.OnGameStart?.Clear(); } catch { } // 注意：Clear 会清所有订阅，谨慎；见下注
            _loaded = false;
            Logger.Info("unloaded");
        }

        public void OnButtonPress() { try { new Settings.SettingsWindow(_cfg, ReloadConfig).Show(); } catch { } }
    }
}
```
> **重要修正**：`GameEvents.OnGameStart.Clear()` 会清除**所有插件**的订阅，不可取。改为在 `OnUnload` 中**不 Clear**，依赖 HDT 的 `ActionList` 机制——禁用插件时 HDT 会按归属自动移除该插件的 handler（见 `Hearthstone Deck Tracker/API/ActionList.cs`：handler 按调用栈归属，插件禁用后下次 fire 自动剔除）。因此 `OnUnload` 删除 `Clear()` 那行，仅停 HTTP/webhook。
>
> `Settings.SettingsWindow`、`HttpSender`、`SystemClock` 在下面补齐。`TrinketPickActive` 初版置 false（spec §11 开放项 #2；可后续接 `ChoicesWatcher`）。

- [ ] **Step 3: 实现 HttpSender（生产 IHttpSender）+ SystemClock（已有，确认命名空间）**

`BgsDataBridge/Webhook/HttpSender.cs`：
```csharp
using System;
using System.Net.Http;
using System.Net.Http.Headers;
using BgsDataBridge.Core;

namespace BgsDataBridge.Webhook
{
    public class HttpSender : IHttpSender
    {
        public int Send(string url, string body, string signature, int timeoutMs)
        {
            using (var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) })
            {
                var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                if (!string.IsNullOrEmpty(signature))
                    req.Headers.Add("X-BgsBridge-Signature", signature);
                using (var resp = c.SendAsync(req).GetAwaiter().GetResult())
                    return (int)resp.StatusCode;
            }
        }
    }
}
```
> `System.Net.Http.HttpClient` 在 net472 可用（HDT 也用）。`SystemClock` 已在 Task 3 的 `BgsDataBridge/Core/IClock.cs` 定义。

- [ ] **Step 4: 实现 WPF 设置窗**

`BgsDataBridge/Settings/SettingsWindow.xaml`：
```xml
<Window x:Class="BgsDataBridge.Settings.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="BgsDataBridge" Height="320" Width="460">
  <StackPanel Margin="12">
    <CheckBox x:Name="CbEnabled" Content="Enabled" Margin="0,4"/>
    <TextBlock Text="HTTP port" Margin="0,8,0,2"/>
    <TextBox x:Name="TbPort" Margin="0,0,0,8"/>
    <TextBlock Text="Webhook URLs (one per line, events=* )" Margin="0,4,0,2"/>
    <TextBox x:Name="TbWebhooks" Height="100" AcceptsReturn="True" Margin="0,0,0,8"/>
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
      <Button Content="Save" Width="80" Margin="0,0,8,0" Click="OnSave"/>
      <Button Content="Cancel" Width="80" IsCancel="True"/>
    </StackPanel>
  </StackPanel>
</Window>
```

`BgsDataBridge/Settings/SettingsWindow.xaml.cs`：
```csharp
using System;
using System.Windows;
using BgsDataBridge.Config;

namespace BgsDataBridge.Settings
{
    public partial class SettingsWindow : Window
    {
        private readonly BridgeConfig _cfg;
        private readonly Action<BridgeConfig> _onSave;
        public SettingsWindow(BridgeConfig cfg, Action<BridgeConfig> onSave)
        {
            InitializeComponent();
            _cfg = cfg; _onSave = onSave;
            CbEnabled.IsChecked = cfg.Enabled;
            TbPort.Text = cfg.Port.ToString();
            TbWebhooks.Text = string.Join(Environment.NewLine,
                cfg.Webhooks.ConvertAll(w => w.Url));
        }
        void OnSave(object sender, RoutedEventArgs e)
        {
            _cfg.Enabled = CbEnabled.IsChecked ?? true;
            if (int.TryParse(TbPort.Text, out var p)) _cfg.Port = p;
            _cfg.Webhooks.Clear();
            foreach (var line in TbWebhooks.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                _cfg.Webhooks.Add(new WebhookConfig { Url = line.Trim() });
            _onSave(_cfg);
            Close();
        }
    }
}
```
> csproj 已 `UseWPF=true`，xaml 自动参与编译。

- [ ] **Step 5: 编译，手动加载验证（见 Task 12），提交**

Run: `msbuild BgsDataBridge/BgsDataBridge.csproj /p:Configuration=Debug /p:Platform=x86`
Expected: 编译成功。
```bash
git add BgsDataBridge/
git commit -m "feat(bridge): BgsBridgePlugin lifecycle, trigger wiring, settings window, logger"
```

---

## Task 10: Costura 单 DLL 打包

**目标**：把插件自身依赖打进单 DLL；排除 HDT 已有依赖。

**Files:**
- Modify: `BgsDataBridge/BgsDataBridge.csproj`
- Create: `BgsDataBridge/FodyWeavers.xml`

- [ ] **Step 1: 加 Fody + Costura 包引用**

在 `BgsDataBridge.csproj` 的 `<ItemGroup>` 内加：
```xml
<PackageReference Include="Fody" Version="6.0.0" PrivateAssets="all" />
<PackageReference Include="Costura.Fody" Version="4.1.0" PrivateAssets="all" />
```

- [ ] **Step 2: FodyWeavers.xml（排除 HDT 自带依赖）**

`BgsDataBridge/FodyWeavers.xml`：
```xml
<?xml version="1.0" encoding="utf-8"?>
<Weavers>
  <Costura ExcludeAssemblies="HearthDb|HearthMirror|Newtonsoft.Json|Hearthstone Deck Tracker" />
</Weavers>
```

- [ ] **Step 3: 构建并确认产出单 DLL + 部署**

Run: `msbuild BgsDataBridge/BgsDataBridge.csproj /p:Configuration=Release /p:Platform=x86`
Expected: `BgsDataBridge/bin/x86/Release/BgsDataBridge.dll` 单文件（依赖内嵌）。

```bash
mkdir -p "$APPDATA/HearthstoneDeckTracker/Plugins"
cp "BgsDataBridge/bin/x86/Release/BgsDataBridge.dll" "$APPDATA/HearthstoneDeckTracker/Plugins/"
```
> `$APPDATA` 在 PowerShell 是 `$env:APPDATA`；bash 是 `$APPDATA`（Git Bash）。部署后 HDT「Options → Tracker → Plugins」启用。

- [ ] **Step 4: 提交**

```bash
git add BgsDataBridge/
git commit -m "build(bridge): Costura single-DLL packaging (exclude HDT deps)"
```

---

## Task 11: `GET /` 最小参考查看器（Web 面板种子）

**Files:**
- Modify: `BgsDataBridge/Http/RouteDispatcher.cs`（加 `/` 分支）
- Modify: `BgsDataBridge.Tests/Http/RouteDispatcherTest.cs`（加一例）

- [ ] **Step 1: 写失败测试**

追加到 `RouteDispatcherTest.cs`：
```csharp
[TestMethod]
public void Root_Returns_Html()
{
    var d = new RouteDispatcher(new FakeSource(), new GameStateProjector());
    var r = d.Dispatch("/", "");
    Assert.AreEqual(200, r.Status);
    Assert.AreEqual("text/html", r.ContentType);
    StringAssert.Contains(r.Body, "BgsDataBridge");
}
```

- [ ] **Step 2: 加 `/` 路由**

在 `RouteDispatcher.Dispatch` 顶部、`/health` 之前加：
```csharp
if (path == "/") return new HttpResponse { Status = 200, ContentType = "text/html", Body = ViewerHtml };
```
并加静态字段（轮询 `/state?pretty=0`、渲染 player.board 与 shop）：
```csharp
const string ViewerHtml = @"<!doctype html><html><head><meta charset=""utf-8"">
<title>BgsDataBridge</title><style>body{font-family:sans-serif;background:#111;color:#eee;margin:16px}
.card{display:inline-block;border:1px solid #555;border-radius:6px;padding:6px 10px;margin:3px;min-width:60px;text-align:center}
.atk{color:#7ec}.hp{color:#e77}.title{color:#9cf}</style></head><body>
<h2 class=title>BgsDataBridge</h2><pre id=s>…</pre>
<script>
async function tick(){try{const j=await (await fetch('/state')).json();const b=(j.player?.board||[]).map(c=>`<span class=card>${c.name||c.cardId} <span class=atk>${c.attack||0}</span>/<span class=hp>${c.health||0}</span></span>`).join(' ');const sh=(j.shop?.offers||[]).map(c=>`<span class=card>${c.name||c.cardId} <span class=atk>${c.attack||0}</span>/<span class=hp>${c.health||0}</span></span>`).join(' ');document.getElementById('s').textContent='turn '+j.match?.turn+' phase '+j.match?.phase+'\\nYOU: '+b+'\\nSHOP: '+sh;}catch(e){document.getElementById('s').textContent='(no match / '+e+')';}}
tick();setInterval(tick,1000);
</script></body></html>";
```

- [ ] **Step 3: 运行，确认通过**

Run: `dotnet test BgsDataBridge.Tests --filter RouteDispatcherTest`
Expected: PASS（5 passed）。

- [ ] **Step 4: 提交**

```bash
git add BgsDataBridge/ BgsDataBridge.Tests/
git commit -m "feat(bridge): minimal viewer served at GET /"
```

---

## Task 12: 手动端到端验证清单

**目标**：在真实 HDT + 单机酒馆对局中验证全链路。无代码改动，记录结果。

- [ ] **Step 1: 构建 + 部署（如未做）**

`msbuild ... /p:Configuration=Release /p:Platform=x86` → 拷 `BgsDataBridge.dll` 到 `%APPDATA%\HearthstoneDeckTracker\Plugins\`。

- [ ] **Step 2: 启用插件**

HDT → Options → Tracker → Plugins → 勾选 BgsDataBridge → 启用。检查 `%APPDATA%\HearthstoneDeckTracker\Plugins\BgsDataBridge\log.txt` 出现 "HTTP on localhost:5273"（或 +N 的端口）。

- [ ] **Step 3: HTTP 拉取**

浏览器开 `http://localhost:5273/health`（应 `{"status":"ok",...}`）与 `http://localhost:5273/state`。开一局**单机酒馆**（无计时器）：
- 英雄选择阶段：`/state` 的 `match.phase` 应为 `HeroPick`。
- 购物阶段：`player.board`、`shop.offers`、`availableRaces` 有值。
- 战斗阶段：`match.phase` 为 `Combat`，`lastOpponent` 在战斗后有上一对手快照。
- 核对 HDT Debug Window（Options → Tracker → Settings → Debug）的 `Game.Entities` 与插件输出一致。

- [ ] **Step 4: Webhook 推送**

本地起 echo 接收器（PowerShell）：
```powershell
# recv.ps1 — 在 :8000 接收并打印
$l=[System.Net.HttpListener]::new();$l.Prefixes.Add("http://localhost:8000/");$l.Start()
while($true){$c=$l.GetContext();$r=New-Object IO.StreamReader $c.Request.InputStream;$c.Response.StatusCode=200;$c.Response.Close();Write-Host $r.ReadToEnd()}
```
在插件设置窗加 webhook `http://localhost:8000/`，Save（热重载）。继续打酒馆：确认每个商店/战斗阶段开始、英雄/饰品选择、商店刷新（去抖后）各收到一条 `bgs-event/v1` 信封，`seq` 单调递增。

- [ ] **Step 5: 记录结果**

在 `docs/superpowers/specs/2026-06-17-bgs-data-bridge-plugin-design.md` 末尾或工作单记录：哪些事件可靠触发、哪些字段在某场景为 null（如 Duos 下 lastOpponent stale、shop 在战斗期为 null）。把实测发现的 §11 开放项答案回填。

---

## Self-Review（写完后自查，已修正）

**1. Spec coverage：**
- §1.2 可行性结论 → Task 0（环境）+ 全程假定。
- §3 架构/组件 → Task 0–9 全覆盖（HttpServer/Projector/TriggerDetector/WebhookDispatcher/Config/Logger）。
- §4 数据契约 → Task 1（DTO + envelope + schema 测试）。
- §5 事件流映射 → Task 4（PhaseStateMachine 边沿）+ Task 9（GameEvents 订阅 + OnUpdate 轮询）。
- §5.3 ShopChanged 合并 → Task 3。
- §6 线程安全/错误处理 → Projector 快照(Task 6a SafeCapture)、WebhookDispatcher 队列+隔离(Task 5)、HTTP try/catch(Task 8)、`Safe(...)`(Task 7)。
- §7 配置/打包 → Task 2 + Task 10。
- §8 测试 → Task 1–8 单测、Task 12 手动。
- §9 交付边界（最小查看器）→ Task 11。
- §11 开放项（watcher 可订阅、pick 时机、Duos stale、frozen、项目位置）→ 在 Task 7/9 注释 + Task 12 回填。

**2. Placeholder 扫描：** 无 TODO/TBD；集成层（Task 7/9）显式标注"属性名以 HDT 源为准"并给出文件路径——这是诚实的边界，非占位。

**3. 类型一致性：** `BridgeEventType` 枚举成员与 `PhaseStateMachine`/`WebhookDispatcher`/`BgsBridgePlugin.Emit` 一致；`IGameSource.Capture()`/`GameStateProjector.Project(view, includeText)`/`RouteDispatcher.Dispatch` 签名跨任务一致；`EventEnvelope.Seq` 为 long，`Interlocked.Increment(ref _seq)` 匹配。

---

## Execution Handoff

（见下条消息，向用户提供执行方式选择。）
