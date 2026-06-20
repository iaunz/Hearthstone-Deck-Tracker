#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BgsViewer —— 酒馆战棋对局可视化仪表盘（实时 webhook + 日志回放）。"""
import argparse
import json
import os
import sys

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
except Exception:
    pass

from flask import Flask, request, jsonify, render_template

from eventstore import EventStore
from logparser import parse
from stateview import reconstruct_state, compute_progression

HERE = os.path.dirname(os.path.abspath(__file__))
app = Flask(__name__, template_folder=os.path.join(HERE, "templates"),
            static_folder=os.path.join(HERE, "static"))
store = EventStore()
CONFIG = {"mode": "live", "stateUrl": "http://localhost:5273",
          "statePoll": True, "jsonlog": None}


def _env_to_event(env, source):
    return {
        "seq": env.get("seq"),
        "type": env.get("event"),
        "at": env.get("at"),
        "match": env.get("match"),
        "data": env.get("data") or {},
        "source": source,
    }


def _write_jsonlog(event):
    path = CONFIG.get("jsonlog")
    if not path:
        return
    try:
        with open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps(event, ensure_ascii=False) + "\n")
    except Exception as e:
        sys.stderr.write(f"[jsonlog 写入失败: {e}]\n")


@app.route("/")
def index():
    return render_template("dashboard.html", mode=CONFIG["mode"],
                           state_url=CONFIG["stateUrl"],
                           state_poll=("true" if CONFIG["statePoll"] else "false"))


@app.route("/events", methods=["POST"])
def post_events():
    if CONFIG["mode"] != "live":
        return jsonify({"error": "webhook disabled in replay mode"}), 404
    raw = request.get_data(cache=True)
    try:
        env = json.loads(raw.decode("utf-8")) if raw else {}
    except Exception:
        return jsonify({"error": "bad json"}), 400
    event = _env_to_event(env, "webhook")
    store.append(event)
    _write_jsonlog(event)
    return jsonify({"ok": True}), 200


@app.route("/api/events")
def api_events():
    since = request.args.get("since", type=int)
    return jsonify(store.since(since))


@app.route("/api/view")
def api_view():
    n = request.args.get("n", type=int)
    events = store.all()
    if n is None or n > len(events):
        n = len(events)
    if n < 0:
        n = 0
    return jsonify(reconstruct_state(events[:n]))


@app.route("/api/progression")
def api_progression():
    return jsonify(compute_progression(store.all()))


@app.route("/api/mode")
def api_mode():
    return jsonify({"mode": CONFIG["mode"], "stateUrl": CONFIG["stateUrl"],
                    "statePoll": CONFIG["statePoll"],
                    "lastIndex": len(store.all())})


def main():
    ap = argparse.ArgumentParser(description="BgsViewer 对局可视化仪表盘")
    ap.add_argument("--port", type=int, default=5000)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--state-url", default="http://localhost:5273")
    ap.add_argument("--no-state-poll", action="store_true")
    ap.add_argument("--replay", default=None, help=".log 或 .jsonl 文件路径")
    ap.add_argument("--jsonlog", default=None, help="实时模式下另写 NDJSON")
    args = ap.parse_args()

    print("BgsViewer 对局可视化仪表盘")
    if args.replay:
        CONFIG["mode"] = "replay"
        CONFIG["statePoll"] = False
        result = parse(args.replay)
        for e in result.events:
            store.append(e)
        print(f"  回放: 载入 {len(result.events)} 事件, 跳过 {result.skipped} 行")
    else:
        CONFIG["stateUrl"] = args.state_url
        CONFIG["statePoll"] = not args.no_state_poll
        CONFIG["jsonlog"] = os.path.abspath(args.jsonlog) if args.jsonlog else None
        print(f"  实时: webhook 目标设为 http://localhost:{args.port}/events")
        print(f"        (与 receiver.py 并存; 插件支持多 webhook URL)")
    if CONFIG["statePoll"]:
        print(f"  /state 轮询: {CONFIG['stateUrl']}/state")
    print(f"  打开: http://localhost:{args.port}/")
    app.run(host=args.host, port=args.port, debug=False)


if __name__ == "__main__":
    main()
