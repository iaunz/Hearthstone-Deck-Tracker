"""云端 LLM 策略器(Anthropic Messages API)。建议的唯一来源;失败 → error Advice(无降级)。

'异步'由调用方在后台线程跑 decide() 保证;此处用同步 httpx.Client。
_call_llm 被 isolate 以便单测 monkeypatch(不打真网络)。"""
import json
import time

import httpx

from .advice import Advice, Action
from .prompt import encode_prompt


class LlmStrategyAdvisor:
    def __init__(self, baseUrl, apiKey, model, timeoutMs=30000):
        self._baseUrl = baseUrl.rstrip("/")
        self._apiKey = apiKey
        self._model = model
        self._timeoutMs = timeoutMs

    def advise(self, snapshot, dtype, findings):
        system, user = encode_prompt(snapshot, dtype, findings)
        t0 = time.monotonic()
        actions = rationale = None
        error = None
        # spec §10:JSON 不合规重试 1 次(强化"只返回 JSON"提示);网络/未知错误不重试。
        for attempt in range(2):
            prompt = user if attempt == 0 else user + "\n\n务必只返回 JSON 对象,不要任何其它文字。"
            try:
                raw = self._call_llm(system, prompt)
                actions, rationale = self._extract(raw)
                error = None
                break
            except _LLMError as e:
                error = str(e)        # 解析失败 → 进入下一次尝试
            except Exception as e:
                error = str(e)        # 网络/未知 → 不重试
                break
        status = "ok" if error is None else "error"
        if status == "error":
            actions, rationale = [], None
        return Advice(
            decisionType=dtype, trigger="manual", snapshotRef={},
            status=status, actions=actions or [], rationale=rationale,
            llm={"model": self._model,
                 "latencyMs": int((time.monotonic() - t0) * 1000),
                 "tokensIn": None, "tokensOut": None, "error": error},
        )

    def _extract(self, raw: str):
        # 容忍模型在 JSON 前后夹带文字:抽取第一个 {...} 块。
        s = raw.strip()
        i, j = s.find("{"), s.rfind("}")
        if i < 0 or j < 0 or j < i:
            raise _LLMError("LLM 未返回 JSON")
        obj = json.loads(s[i:j + 1])
        actions = [Action(kind=a["kind"], cardId=a.get("cardId"), name=a.get("name"),
                          index=a.get("index"), note=a.get("note"))
                   for a in obj.get("actions", [])]
        return actions, obj.get("rationale")

    # ---- IO 边界:测试中 monkeypatch _call_llm ----
    def _call_llm(self, system: str, user: str) -> str:
        url = f"{self._baseUrl}/v1/messages"
        headers = {"x-api-key": self._apiKey,
                   "anthropic-version": "2023-06-01",
                   "content-type": "application/json"}
        body = {"model": self._model, "max_tokens": 1024,
                "system": system,
                "messages": [{"role": "user", "content": user}]}
        with httpx.Client(timeout=self._timeoutMs / 1000) as c:
            r = c.post(url, headers=headers, json=body)
            r.raise_for_status()
            data = r.json()
        for block in data.get("content", []):
            if block.get("type") == "text":
                return block.get("text", "")
        raise _LLMError("LLM 响应无 text block")


class _LLMError(Exception):
    pass
