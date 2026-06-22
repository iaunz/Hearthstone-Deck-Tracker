"""军师配置加载/保存 + API key 打码(前端只看 hasApiKey)。"""
import json
import os

DEFAULTS = {
    "baseUrl": "https://api.anthropic.com",
    "apiKey": "",
    "model": "claude-sonnet-4-6",
    "autoTriggerPicks": True,
    "timeoutMs": 30000,
}


def load(path: str) -> dict:
    cfg = dict(DEFAULTS)
    if path and os.path.exists(path):
        try:
            with open(path, "r", encoding="utf-8") as f:
                cfg.update(json.load(f))
        except Exception:
            pass  # 损坏 → 回退默认(对齐 spec §10)
    return cfg


def save(path: str, cfg: dict) -> None:
    merged = dict(DEFAULTS)
    merged.update(cfg)
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(merged, f, ensure_ascii=False, indent=2)


def mask(cfg: dict) -> dict:
    """供前端读取:永不暴露原始 key。"""
    out = {k: v for k, v in cfg.items() if k != "apiKey"}
    out["hasApiKey"] = bool(cfg.get("apiKey"))
    return out
