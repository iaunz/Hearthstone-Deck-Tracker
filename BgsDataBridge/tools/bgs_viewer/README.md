# BgsViewer · 酒馆战棋对局可视化仪表盘

把 BgsDataBridge 插件的事件流（实时 webhook 或日志回放）可视化为
"当前状态 + 回合时间轴 + 进度曲线"，展示 bgs-state/v1 契约内的全部字段。

## 依赖
- Python 3.8+
- `pip install flask`
- 浏览器查看页面时需联网加载 Chart.js（CDN）

## 实时模式
1. `python bgs_viewer.py --port 5000`
2. HDT → Options → Tracker → Plugins → BgsDataBridge 设置里，把 webhook URL
   设为 `http://localhost:5000/events`（可与 receiver.py 并存，插件支持多 URL）。
3. 浏览器打开 `http://localhost:5000/`。

默认每 1.5s 由浏览器直连插件 `http://localhost:5273/state` 取最新快照（插件已开
CORS）。用 `--no-state-poll` 关闭，仅靠 webhook。

## 回放模式
`python bgs_viewer.py --replay <bgs-events-*.log | *.jsonl>`
拖动条/▶/步进/速度可逐事件浏览，回合 chips 可点跳转。

## 可选
- `--jsonlog out.jsonl` 实时模式下把收到的事件另写 NDJSON（可替代 receiver.py 日志）。
- `--state-url http://host:port` 覆盖插件 /state 地址。
- `--host 0.0.0.0` 对外暴露（默认仅 127.0.0.1）。

## 字段说明
顶栏 + 三列 + 详情抽屉合起来覆盖契约内所有插件实际填充字段。
死字段（dbfId、minion.golden、shop.frozen、zonePosition、cost、match.anomaly、
match.gameUuid）插件从不填充，不展示。金标按 cardId 以 `_G` 结尾推断。

## 测试
`cd BgsDataBridge/tools/bgs_viewer && python -m unittest`
