# CLAUDE.md

## 这是什么

本仓库是 **Hearthstone Deck Tracker (HDT)** 的一个 fork（`v2` 分支）。HDT 是 Windows 上的炉石传说自动牌组追踪器/管理器（.NET Framework 4.7.2 / WPF / **x86**）：实时 overlay、手牌/牌库/抽牌概率追踪、对局统计、牌组管理，以及对**酒馆战棋（Battlegrounds）**的一整套支持（己方阵容、对手快照、英雄/饰品/任务选择、商店、BobsBuddy 战斗模拟等）。

仓库里还包含我们新加的一个 HDT 插件 **`BgsDataBridge/`**：把酒馆战棋对局状态通过 **HTTP `GET /state`** 与**事件 webhook** 暴露给本地下游消费方（Web 面板 / AI/LLM 分析）。**这是我们当前主要开发的部分。** 设计 spec 与实现计划见 `docs/superpowers/specs/` 和 `docs/superpowers/plans/`。

## 目录结构（重点）

- `Hearthstone Deck Tracker/` — HDT 主程序源码（WPF；namespace `Hearthstone_Deck_Tracker`）。运行时游戏状态在静态 `Core.Game`（`GameV2` 单例）；插件契约 `Plugins/IPlugin.cs`；事件总线 `API/GameEvents.cs`、`API/DeckManagerEvents.cs`、`API/LogEvents.cs`（均为 `ActionList`，按调用栈归属插件、随禁用自动解绑）。
- `HearthWatcher/` — 炉石日志读取库（主程序引用）。游戏状态由后台日志线程 ~10Hz 写入 `GameV2.Entities`，**非线程安全、无锁**。
- `BgsDataBridge/` — **我们的插件**（详见下方"BgsDataBridge 架构"）。
- `BgsDataBridge.Tests/` — 插件单元测试（MSTest，纯逻辑核心全覆盖）。
- `BgsDataBridge/tools/receiver.py` — webhook 接收器开发工具（清晰文本 + 可选 NDJSON 日志）。
- `lib/` — 构建时由 `Bootstrap/` 从 hearthsim 下载的依赖（`HearthDb`/`HearthMirror`/`HSReplay`/`BobsBuddy`），已 gitignore，**本地缺失属正常**，构建时自动拉取。
- `Bootstrap/` — 首次构建联网下载 lib 依赖 + clone `HDT-Localization`。
- `Directory.Build.props`（仓库根）— 给所有 net472 工程加 `Microsoft.NETFramework.ReferenceAssemblies`，使构建不依赖系统装的 .NET Framework SDK。

## 构建与测试（关键 —— 踩过坑，必读）

HDT 主工程用了自定义 `ResGen.exe` 目标，**`dotnet build` 无法构建主工程**（报 "ResGen.exe not supported on .NET Core MSBuild"）。**必须用经典 Visual Studio MSBuild**：

```bash
# 构建（主工程 / 插件 / solution 一律用这条）
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" <工程或sln> -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
```

```bash
# 跑测试（先构建，再加 --no-build —— 否则 dotnet 会用 SDK MSBuild 重建并触发 ResGen）
"C:/Program Files (x86)/Microsoft Visual Studio/2022/BuildTools/MSBuild/Current/Bin/MSBuild.exe" BgsDataBridge.Tests/BgsDataBridge.Tests.csproj -p:Configuration=Debug -p:Platform=x86 -v:minimal -nologo
dotnet test BgsDataBridge.Tests/BgsDataBridge.Tests.csproj --no-build -p:Platform=x86
```

要点：
- 目标框架 **net472**、平台 **x86**、语言 **C# 7.3**（HearthMirror/HearthDb 为 x86，务必 `-p:Platform=x86`）。
- 插件工程的 HDT 依赖 `HearthDb`/`HearthMirror`/`HearthstoneDeckTracker` 一律 `<Private>false</Private>`（Copy Local=False），避免与 HDT 运行时已加载的同名程序集冲突。
- 首次构建需联网（Bootstrap 下载 lib + clone 本地化仓库）。
- 如缺 `lib/*.dll`：跑一次上面的 MSBuild solution/Bootstrap 构建即可自动补齐。

## BgsDataBridge 架构（一句话）

`IPlugin`（进程内、全信任、无沙箱）→ `OnLoad` 启动 `BridgeHttpServer`（`HttpListener`，仅 localhost）+ `WebhookDispatcher`（独立后台线程发送，按 URL 隔离退避）→ `OnUpdate`（10Hz）跑 `PhaseStateMachine` 边沿检测 + `ShopChangedDebouncer` → `HdtGameSource.Capture()` 从 `Core.Game` + HearthMirror **快照+克隆** 出 `GameStateView` → `GameStateProjector` 映射成纯 POCO `BgsSnapshot`（JSON 契约 `bgs-state/v1` / `bgs-event/v1`）。

**铁律**：游戏线程上的回调（`OnUpdate`、`GameEvents.*`）只"快照入队"立刻返回，**绝不发 HTTP**；跨线程读 `Core.Game` 必先克隆（`Entity.Clone()`/`.ToList()`）。详见 spec §6.1。`GameStateView` 是唯一的线程安全边界——`Projector` 之后全是不可变 DTO。

产物为 Costura 单 DLL：`BgsDataBridge/bin/x86/Release/BgsDataBridge.dll`。

## 部署插件

把 `BgsDataBridge.dll` 拷到 `%APPDATA%\HearthstoneDeckTracker\Plugins\` → **重启 HDT** → Options → Tracker → Plugins → 启用 `BgsDataBridge`。webhook URL 默认指向本地接收器（如 `http://localhost:8000/`），HTTP 状态在 `http://localhost:5273/state`。

## 常用任务速查

- 改插件纯逻辑（DTO/去抖/状态机/webhook 分发/路由）→ 有单元测试，TDD。
- 改 HDT 集成层（`HdtGameSource` 读 `Core.Game`/HearthMirror）→ 无单测，靠构建通过 + 运行时验证；属性名以 HDT 源码为准（每处用 `Safe(...)` 包裹）。
- 运行时调试：插件日志在 `%APPDATA%\HearthstoneDeckTracker\Plugins\BgsDataBridge\log.txt`；HDT 自带 Debug Window（Options → Tracker → Settings → Debug）可看 `Game.Entities` 实时状态。
- 完整设计决策与已知 v1 边界（如其它 7 人实时状态不可获取）见 `docs/superpowers/specs/2026-06-17-bgs-data-bridge-plugin-design.md`。
