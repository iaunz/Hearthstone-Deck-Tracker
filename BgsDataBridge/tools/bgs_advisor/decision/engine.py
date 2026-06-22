"""决策引擎编排:启发式 → prompt(hints) → advisor(建议唯一来源)。纯逻辑,注入 advisor 可测。

'异步'由调用方(Flask 后台线程)保证;引擎本身同步。advisor 契约:始终返回一个 Advice
(status=ok 或 error);LLM 失败由 advisor 内部捕获并返回 error Advice(无降级)。"""
from typing import Protocol
from .advice import Advice
from .heuristics import run_tactics, DEFAULT_TACTICS


class StrategyAdvisor(Protocol):
    def advise(self, snapshot: dict, dtype: str, findings: list) -> Advice: ...


class DecisionEngine:
    def __init__(self, advisor, tactics=None):
        self._advisor = advisor
        self._tactics = tactics if tactics is not None else DEFAULT_TACTICS

    def decide(self, snapshot, dtype, trigger="manual", snapshotRef=None) -> Advice:
        findings = run_tactics(snapshot, dtype, self._tactics)
        advice = self._advisor.advise(snapshot, dtype, findings)
        advice.trigger = trigger
        if snapshotRef is not None:
            advice.snapshotRef = snapshotRef
        return advice
