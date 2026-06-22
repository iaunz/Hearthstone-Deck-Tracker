"""bgs-advice/v1 契约:类型化的行动建议。纯逻辑,无 IO。"""
from dataclasses import dataclass, field, asdict

SCHEMA = "bgs-advice/v1"
STATUSES = ("thinking", "ok", "error")


@dataclass
class Action:
    kind: str
    cardId: str | None = None
    name: str | None = None
    index: int | None = None
    note: str | None = None

    def to_dict(self) -> dict:
        return asdict(self)

    @staticmethod
    def from_dict(d: dict) -> "Action":
        return Action(kind=d["kind"], cardId=d.get("cardId"), name=d.get("name"),
                      index=d.get("index"), note=d.get("note"))


@dataclass
class Advice:
    decisionType: str
    trigger: str
    snapshotRef: dict
    status: str = "thinking"
    actions: list = field(default_factory=list)   # list[Action]
    rationale: str | None = None
    llm: dict = field(default_factory=dict)       # {model,latencyMs,tokensIn,tokensOut,error}
    ts: int = 0                                    # AdviceStore 赋的 monotonic 序号
    schema: str = SCHEMA

    def __post_init__(self):
        if self.status not in STATUSES:
            raise ValueError(f"invalid status {self.status!r}; must be one of {STATUSES}")

    def to_dict(self) -> dict:
        return {
            "schema": self.schema,
            "decisionType": self.decisionType,
            "trigger": self.trigger,
            "snapshotRef": self.snapshotRef,
            "status": self.status,
            "actions": [a.to_dict() for a in self.actions],
            "rationale": self.rationale,
            "llm": self.llm,
        }

    @staticmethod
    def from_dict(d: dict) -> "Advice":
        return Advice(
            schema=d.get("schema", SCHEMA),
            decisionType=d["decisionType"],
            trigger=d["trigger"],
            snapshotRef=d.get("snapshotRef", {}),
            status=d.get("status", "thinking"),
            actions=[Action.from_dict(a) for a in d.get("actions", [])],
            rationale=d.get("rationale"),
            llm=d.get("llm", {}),
        )
