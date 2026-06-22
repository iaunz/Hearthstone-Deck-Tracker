#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""BgsAdvisor —— 酒馆战棋 AI 军师(BgsViewer 超集):实时 webhook / 复盘 + 云端 LLM 建议。"""
import argparse
import json
import os
import sys
import threading

try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
except Exception:
    pass

from flask import Flask, request, jsonify, render_template

# 复用 BgsViewer 已构建的纯逻辑模块(兄弟目录)
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "bgs_viewer"))
from eventstore import EventStore          # noqa: E402
from logparser import parse                # noqa: E402
from stateview import reconstruct_state    # noqa: E402

sys.path.insert(0, HERE)
from decision.engine import DecisionEngine                       # noqa: E402
from decision.llm_strategy import LlmStrategyAdvisor             # noqa: E402
from advicestore import AdviceStore                              # noqa: E402
import config as advisor_config                                  # noqa: E402

app = Flask(__name__, template_folder=os.path.join(HERE, "templates"),
            static_folder=os.path.join(HERE, "static"))
store = EventStore()
advice_store = AdviceStore()
CONFIG_PATH = os.path.join(os.path.expanduser("~"), ".bgs_advisor_config.json")
RUNTIME = {"mode": "live", "engine": None, "cfg": None}


def _env_to_event(env, source):
    return {"seq": env.get("seq"), "type": env.get("event"), "at": env.get("at"),
            "match": env.get("match"), "data": env.get("data") or {}, "source": source}


def _make_engine():
    cfg = advisor_config.load(CONFIG_PATH)
    advisor = LlmStrategyAdvisor(cfg["baseUrl"], cfg["apiKey"], cfg["model"], cfg["timeoutMs"])
    return DecisionEngine(advisor), cfg


def _snapshot_ref(view, seq):
    match = (view or {}).get("match") or {}
    return {"seq": seq, "turn": match.get("turn"), "capturedAt": (view or {}).get("capturedAt")}


def _trigger_async(dtype, trigger, seq):
    """后台线程跑引擎(不阻塞 Flask)。结果入 AdviceStore。"""
    def run():
        try:
            events = store.all()
            # 定位触发点位:实时取末尾;复盘按 seq 精确匹配
            if seq is not None:
                idx = next((i for i, e in enumerate(events) if e.get("seq") == seq), None)
                n = (idx + 1) if idx is not None else len(events)
            else:
                n = len(events)
            view = reconstruct_state(events[:n]) or {}
            engine, _ = RUNTIME.get("engine") or (None, None)
            if engine is None:
                engine, _ = _make_engine()
                RUNTIME["engine"] = (engine, None)
            advice = engine.decide(view, dtype, trigger=trigger, snapshotRef=_snapshot_ref(view, seq))
            advice_store.put(advice)
        except Exception as e:
            from decision.advice import Advice
            advice_store.put(Advice(decisionType=dtype, trigger=trigger,
                                    snapshotRef={"seq": seq}, status="error",
                                    llm={"error": repr(e)}))
    t = threading.Thread(target=run, daemon=True)
    t.start()


@app.route("/")
def index():
    return render_template("dashboard.html", mode=RUNTIME["mode"])


@app.route("/events", methods=["POST"])
def post_events():
    if RUNTIME["mode"] != "live":
        return jsonify({"error": "webhook disabled in replay mode"}), 404
    raw = request.get_data(cache=True)
    try:
        env = json.loads(raw.decode("utf-8")) if raw else {}
    except Exception:
        return jsonify({"error": "bad json"}), 400
    event = _env_to_event(env, "webhook")
    store.append(event)
    # Picks 自动触发
    cfg = RUNTIME.get("cfg") or advisor_config.load(CONFIG_PATH)
    RUNTIME["cfg"] = cfg
    etype = event.get("type")
    if cfg.get("autoTriggerPicks", True) and etype in ("HeroPick", "TrinketPick"):
        dtype = "HeroPick" if etype == "HeroPick" else "TrinketPick"
        _trigger_async(dtype, f"auto:{etype}", event.get("seq"))
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


@app.route("/api/mode")
def api_mode():
    return jsonify({"mode": RUNTIME["mode"], "lastIndex": len(store.all())})


@app.route("/api/advise", methods=["POST"])
def api_advise():
    body = request.get_json(silent=True) or {}
    dtype = body.get("decisionType")
    if dtype not in ("ShopPhase", "Positioning", "HeroPick", "TrinketPick"):
        return jsonify({"error": "bad decisionType"}), 400
    seq = body.get("atSeq")
    # 立即占位
    from decision.advice import Advice
    placeholder = Advice(decisionType=dtype, trigger="manual",
                         snapshotRef={"seq": seq}, status="thinking")
    advice_store.put(placeholder)
    _trigger_async(dtype, "manual", seq)
    return jsonify({"ok": True, "decisionType": dtype}), 202


@app.route("/api/advice")
def api_advice():
    dtype = request.args.get("decisionType")
    since_ts = request.args.get("sinceTs", type=int)
    if since_ts is not None:
        return jsonify([a.to_dict() for a in advice_store.since(since_ts)])
    a = advice_store.latest(dtype)
    return jsonify(a.to_dict() if a else None)


@app.route("/api/config", methods=["GET", "POST"])
def api_config():
    if request.method == "GET":
        cfg = RUNTIME.get("cfg") or advisor_config.load(CONFIG_PATH)
        RUNTIME["cfg"] = cfg
        return jsonify(advisor_config.mask(cfg))
    body = request.get_json(silent=True) or {}
    cfg = advisor_config.load(CONFIG_PATH)
    for k in ("baseUrl", "model", "autoTriggerPicks", "timeoutMs"):
        if k in body:
            cfg[k] = body[k]
    if body.get("apiKey"):  # 空串不改(前端无法读回原文,不主动清空)
        cfg["apiKey"] = body["apiKey"]
    advisor_config.save(CONFIG_PATH, cfg)
    RUNTIME["cfg"] = cfg
    RUNTIME["engine"] = None  # 强制下次用新配置重建引擎
    return jsonify(advisor_config.mask(cfg))


def main():
    ap = argparse.ArgumentParser(description="BgsAdvisor 酒馆战棋 AI 军师")
    ap.add_argument("--port", type=int, default=5001)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--replay", default=None)
    ap.add_argument("--config", default=None)
    args = ap.parse_args()

    global CONFIG_PATH
    if args.config:
        CONFIG_PATH = args.config
    cfg = advisor_config.load(CONFIG_PATH)
    RUNTIME["cfg"] = cfg
    RUNTIME["engine"] = None  # 懒构造(首次 advise 时建,用最新 cfg)

    print("BgsAdvisor 酒馆战棋 AI 军师")
    if args.replay:
        RUNTIME["mode"] = "replay"
        result = parse(args.replay)
        for e in result.events:
            store.append(e)
        print(f"  回放: 载入 {len(result.events)} 事件, 跳过 {result.skipped} 行")
    else:
        print(f"  实时: webhook 目标 http://localhost:{args.port}/events")
    print(f"  API key: {'已配置' if cfg.get('apiKey') else '未配置(请在设置区填)'}")
    print(f"  打开: http://localhost:{args.port}/")
    app.run(host=args.host, port=args.port, debug=False)


if __name__ == "__main__":
    main()
