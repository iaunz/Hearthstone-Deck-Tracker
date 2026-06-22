"""确定性战术启发式:产 TacticFinding 提示,喂进 LLM prompt(不对用户可见,无降级)。纯逻辑。"""
from dataclasses import dataclass


@dataclass
class TacticFinding:
    code: str
    severity: str          # info | warn | critical
    message: str


class Tactic:
    code: str = ""
    def evaluate(self, snapshot: dict, dtype: str):  # -> TacticFinding | None
        raise NotImplementedError


def _names(items):
    return [m.get("name") or m.get("cardId") for m in (items or []) if m]


class GoldenOpportunity(Tactic):
    code = "golden-opportunity"
    def evaluate(self, snapshot, dtype):
        player = snapshot.get("player") or {}
        names = _names(player.get("board")) + _names(player.get("hand")) + \
                _names((snapshot.get("shop") or {}).get("offers"))
        seen = {}
        for n in names:
            if n is None:
                continue
            seen[n] = seen.get(n, 0) + 1
            if seen[n] == 2:
                return TacticFinding(self.code, "warn", f"可合成金色: {n}")
        return None


class TauntPositioning(Tactic):
    """Positioning 决策:嘲讽随从应靠左(第一个承伤)。"""
    code = "taunt-positioning"
    def evaluate(self, snapshot, dtype):
        if dtype != "Positioning":
            return None
        board = (snapshot.get("player") or {}).get("board") or []
        for i, m in enumerate(board):
            if "TAUNT" in (m.get("keywords") or []) and i != 0:
                name = m.get("name") or m.get("cardId") or "嘲讽随从"
                return TacticFinding(self.code, "warn", f"{name} 是嘲讽但未在最左侧")
        return None


class LevelUpWindow(Tactic):
    """tier < 5 时提示存在升本空间(启发式提示,具体由 LLM 判断时机)。"""
    code = "level-up-window"
    def evaluate(self, snapshot, dtype):
        if dtype not in ("ShopPhase", "Positioning"):
            return None
        tier = (snapshot.get("player") or {}).get("tier")
        if isinstance(tier, int) and 1 <= tier <= 4:
            return TacticFinding(self.code, "info", f"当前酒馆 {tier} 级,可考虑升本窗口")
        return None


DEFAULT_TACTICS = [GoldenOpportunity(), TauntPositioning(), LevelUpWindow()]


def run_tactics(snapshot: dict, dtype: str, tactics=None):
    tactics = tactics if tactics is not None else DEFAULT_TACTICS
    out = []
    for t in tactics:
        f = t.evaluate(snapshot, dtype)
        if f is not None:
            out.append(f)
    return out
