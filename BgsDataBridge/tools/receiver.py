#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
BgsDataBridge webhook 接收器 —— 把插件推送的事件以清晰文本打印到控制台。

用法:
  python receiver.py                       # 监听 :8000, 文本日志写 bgs-events.log
  python receiver.py --port 9000           # 自定义端口
  python receiver.py --host 127.0.0.1      # 自定义主机
  python receiver.py --secret MY_SECRET    # 校验 X-BgsBridge-Signature (HMAC-SHA256 hex)
  python receiver.py --raw                 # 额外打印原始 JSON body
  python receiver.py --no-log              # 不写文本日志文件
  python receiver.py --log events.log      # 自定义文本日志路径
  python receiver.py --jsonlog events.jsonl  # 额外写 NDJSON 机器日志 (每行一事件)

然后在 HDT 插件设置窗里把 webhook URL 设为 http://localhost:8000/ (或对应端口)。

依赖: 仅 Python 3 标准库 (http.server / hmac / hashlib / json / argparse)。
终止: Ctrl+C。
"""
import argparse
import hashlib
import hmac
import json
import os
import re
import sys
from datetime import datetime
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

# Windows 控制台默认代码页可能不是 UTF-8, 强制 stdout/stderr 用 UTF-8,
# 保证中文与制表符 (─ 等) 在任何终端都正确显示。
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
except Exception:
    pass  # 旧 Python 或重定向时忽略

# ── 终端颜色 (非 TTY 时自动关闭) ──────────────────────────────────────────
_USE_COLOR = sys.stdout.isatty()

def c(text, code):
    if not _USE_COLOR:
        return text
    return f"\033[{code}m{text}\033[0m"

def bold(t):   return c(t, "1")
def dim(t):    return c(t, "2")
def cyan(t):   return c(t, "36")
def yellow(t): return c(t, "33")
def green(t):  return c(t, "32")
def red(t):    return c(t, "31")
def magenta(t):return c(t, "35")

SEP = "─" * 64
# 用于把写进日志文件的文本里的 ANSI 颜色码剥掉 (日志要纯文本)
ANSI_RE = re.compile(r"\033\[[0-9;]*m")

# ── 渲染 ────────────────────────────────────────────────────────────────
def render_minion(m, idx=None):
    """渲染一个随从/商店卡: [idx] name  atk/hp  [keywords]"""
    prefix = f"[{idx}] " if idx is not None else ""
    name = m.get("name") or m.get("cardId") or "?"
    atk = m.get("attack")
    hp = m.get("health")
    stats = f"{yellow(str(atk))}/{green(str(hp))}" if atk is not None and hp is not None else ""
    kws = m.get("keywords") or []
    kw = "  " + " ".join(dim(k) for k in kws) if kws else ""
    golden = " " + magenta("(金)") if m.get("golden") else ""
    return f"    {prefix}{name:<22} {stats:<8}{kw}{golden}"

def render_board(board, title="player board"):
    if not board:
        return f"  {dim('── ' + title + ': (空) ──')}"
    lines = [f"  {dim('── ' + title + ' ──')}"]
    for i, m in enumerate(board, 1):
        lines.append(render_minion(m, i))
    return "\n".join(lines)

def render_shop(shop):
    if not shop:
        return f"  {dim('── shop: (无) ──')}"
    offers = shop.get("offers") or []
    tier = shop.get("tier")
    frozen = shop.get("frozen")
    meta = []
    if tier: meta.append(f"tier {tier}")
    if frozen is not None: meta.append("frozen" if frozen else "")
    meta_s = " · ".join(x for x in meta if x)
    head = "── shop"
    if meta_s: head += f" ({meta_s})"
    if not offers:
        return f"  {dim(head + ': (无 offers) ──')}"
    lines = [f"  {dim(head + ' ──')}"]
    for i, m in enumerate(offers, 1):
        lines.append(render_minion(m, i))
    return "\n".join(lines)

def render_match(match):
    """对局元信息一行。"""
    if not match:
        return dim("  match: (无)")
    gt = match.get("gameType", "?")
    turn = match.get("turn")
    phase = match.get("phase")
    isbg = match.get("isBattlegrounds")
    isduos = match.get("isDuos")
    rating = match.get("rating") or {}
    mmr = rating.get("mmr")
    parts = [bold(gt)]
    if isduos: parts.append(dim("Duos"))
    if turn is not None: parts.append(f"turn {turn}")
    if phase: parts.append(f"phase {cyan(phase)}")
    if mmr is not None: parts.append(f"mmr {mmr}")
    return "  " + " · ".join(str(p) for p in parts)

def render_data(data, raw=False):
    """根据 data 形状渲染人类友好摘要 + 可选原始 JSON。"""
    out = []
    if data in (None, {}, ""):
        out.append(f"  {dim('data: (空 payload)')}")
        return "\n".join(out)

    # ShopChanged 形状: { shop, turn, phase }
    if isinstance(data, dict) and "shop" in data:
        out.append(render_shop(data.get("shop")))
        extra = []
        if data.get("turn") is not None: extra.append(f"turn {data['turn']}")
        if data.get("phase"): extra.append(f"phase {data['phase']}")
        if extra: out.append(f"  {dim(' · '.join(extra))}")

    # 完整 snapshot 形状 (BgsSnapshot): { player: { board }, shop, lastOpponent, ... }
    if isinstance(data, dict) and "player" in data:
        player = data.get("player") or {}
        if player.get("board"):
            out.append(render_board(player.get("board"), "your board"))
        if player.get("hero"):
            h = player["hero"]
            out.append(f"  {dim('hero')}: {h.get('name') or h.get('cardId')} {h.get('health','-')}hp"
                       + (f" +{h['armor']}" if h.get("armor") else ""))
        if player.get("heroPower"):
            hp_ = player["heroPower"]
            out.append(f"  {dim('heroPower')}: {hp_.get('name') or hp_.get('cardId')}")
        if player.get("trinkets"):
            for t in player["trinkets"]:
                out.append(f"  {dim('trinket')}[{t.get('slot','?')}]: {t.get('name') or t.get('cardId')}")
        if data.get("shop"):
            out.append(render_shop(data.get("shop")))
        if data.get("lastOpponent"):
            lo = data["lastOpponent"]
            out.append(render_board(lo.get("board"), f"lastOpponent (turn {lo.get('turn','?')})"))

    # 始终附上完整 data 的 pretty JSON
    out.append(f"  {dim('── data (json) ──')}")
    pretty = json.dumps(data, ensure_ascii=False, indent=2)
    out.append("\n".join("    " + ln for ln in pretty.splitlines()))
    return "\n".join(out)

# ── HTTP handler ─────────────────────────────────────────────────────────
class Handler(BaseHTTPRequestHandler):
    server_version = "BgsReceiver/1.0"

    def _common_log(self):
        # 静默默认的 stderr 访问日志, 自己打更清晰的
        return

    def log_message(self, *args):
        pass  # 不打印默认访问日志

    def do_GET(self):
        body = ("BgsDataBridge webhook 接收器运行中。\n"
                "在插件设置窗把 webhook URL 设为 "
                f"http://{self.server.server_name}:{self.server.server_port}/\n").encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/plain; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0) or 0)
        raw = self.rfile.read(length) if length else b""

        # 签名校验
        secret = self.server.opts.get("secret")
        sig = self.headers.get("X-BgsBridge-Signature")
        sig_ok = None
        if secret:
            expected = hmac.new(secret.encode("utf-8"), raw, hashlib.sha256).hexdigest()
            sig_ok = (sig is not None) and hmac.compare_digest(sig, expected)

        # 先回 200, 让插件不重试
        self.send_response(200)
        self.send_header("Content-Length", "0")
        self.end_headers()

        self._print_event(raw, sig_ok, sig)

    def _print_event(self, raw, sig_ok, sig):
        now = datetime.now().strftime("%H:%M:%S.%f")[:-3]
        L = ["", c(SEP, "90")]
        ts = dim(now)
        if sig_ok is True:
            ts += "  " + green("✓ sig")
        elif sig_ok is False:
            ts += "  " + red(f"✗ sig mismatch (got {sig})")
        L.append(f"{ts}  ← {bold(self.path)}")

        try:
            env = json.loads(raw.decode("utf-8")) if raw else {}
        except Exception as e:
            L.append(red(f"  解析 JSON 失败: {e}"))
            L.append(f"  raw: {raw!r}")
            self._emit(L, env=None, raw=raw)
            return

        seq = env.get("seq")
        event = env.get("event")
        at = env.get("at")
        head = f"  #{seq}  {bold(cyan(event or '?'))}"
        if at:
            head += f"   {dim(at)}"
        L.append(head)

        L.append(render_match(env.get("match")))

        L.append(render_data(env.get("data")))

        if self.server.opts.get("raw"):
            L.append(f"  {dim('── raw body ──')}")
            try:
                txt = json.dumps(env, ensure_ascii=False, indent=2)
            except Exception:
                txt = raw.decode("utf-8", "replace")
            L.append("\n".join("    " + ln for ln in txt.splitlines()))

        self._emit(L, env=env, raw=raw)

    def _emit(self, lines, env, raw):
        """把一次事件分发到: 控制台(带色) + 文本日志(去色) + NDJSON 日志。"""
        text = "\n".join(lines)
        # 控制台 (带颜色)
        print(text)
        sys.stdout.flush()

        opts = self.server.opts
        # 人类可读文本日志 (剥掉 ANSI 颜色码)
        hlog = opts.get("log")
        if hlog:
            try:
                with open(hlog, "a", encoding="utf-8") as f:
                    f.write(ANSI_RE.sub("", text) + "\n")
            except Exception as e:
                sys.stderr.write(f"[文本日志写入失败: {e}]\n")
        # 机器可读 NDJSON 日志 (每个事件一行 JSON, 便于后续分析/回放)
        jlog = opts.get("jsonlog")
        if jlog and env is not None:
            try:
                with open(jlog, "a", encoding="utf-8") as f:
                    f.write(json.dumps(env, ensure_ascii=False) + "\n")
            except Exception as e:
                sys.stderr.write(f"[NDJSON 日志写入失败: {e}]\n")


def main():
    ap = argparse.ArgumentParser(description="BgsDataBridge webhook 接收器")
    ap.add_argument("--host", default="0.0.0.0", help="监听主机 (默认 0.0.0.0)")
    ap.add_argument("--port", type=int, default=8000, help="监听端口 (默认 8000)")
    ap.add_argument("--secret", default=None, help="校验 X-BgsBridge-Signature 的共享密钥")
    ap.add_argument("--raw", action="store_true", help="额外打印完整原始 JSON body")
    ap.add_argument("--log", default="bgs-events.log",
                    help="人类可读文本日志路径 (默认 bgs-events.log; 用 --no-log 关闭)")
    ap.add_argument("--no-log", action="store_true", help="不写人类可读文本日志文件")
    ap.add_argument("--jsonlog", default=None,
                    help="机器可读 NDJSON 日志路径 (每行一个事件 JSON); 默认不写")
    args = ap.parse_args()

    srv = ThreadingHTTPServer((args.host, args.port), Handler)
    log_path = None if args.no_log else os.path.abspath(args.log)
    jsonlog_path = os.path.abspath(args.jsonlog) if args.jsonlog else None
    srv.opts = {"secret": args.secret, "raw": args.raw, "log": log_path, "jsonlog": jsonlog_path}

    print(bold("BgsDataBridge webhook 接收器"))
    print(f"  监听: http://{args.host}:{args.port}/")
    if args.secret:
        print(f"  签名校验: {green('开启')}")
    if args.raw:
        print(f"  原始 JSON: {dim('开启')}")
    if log_path:
        print(f"  文本日志: {log_path}")
    else:
        print(f"  文本日志: {dim('关闭 (--no-log)')}")
    if jsonlog_path:
        print(f"  NDJSON 日志: {jsonlog_path}")
    print(f"  {dim('在插件设置窗把 webhook URL 指向这里, Ctrl+C 终止')}")
    print()
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\n" + dim("已停止"))
        srv.shutdown()


if __name__ == "__main__":
    main()
