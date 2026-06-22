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
- 纯逻辑模块(`advice`/`heuristics`/`prompt`/`engine`/`advicestore`/`config`/`llm_strategy`)全覆盖 `unittest`:`python tests/test_*.py`。

## 非目标

- 不做自动执行(ToS)。
- 不接 BobsBuddy(引擎接口已预留,v2)。
- Stage 2(金币 / picks 可选项)属另一份插件 spec。
