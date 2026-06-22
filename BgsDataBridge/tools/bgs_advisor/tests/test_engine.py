import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.engine import DecisionEngine
from decision.advice import Advice, Action


class FakeAdvisor:
    """Records what the engine passed; returns a canned Advice."""
    def __init__(self, status="ok"):
        self.status = status
        self.seen = None
    def advise(self, snapshot, dtype, findings):
        self.seen = (dtype, [f.code for f in findings])
        # spec §error-contract: status=error → actions=[] (no fallback). FakeAdvisor
        # honors the same contract a real advisor (Task 8) must.
        if self.status == "error":
            return Advice(decisionType=dtype, trigger="manual", snapshotRef={}, status="error",
                          actions=[], rationale=None,
                          llm={"model": "fake", "latencyMs": 1, "tokensIn": 0, "tokensOut": 0, "error": "boom"})
        return Advice(decisionType=dtype, trigger="manual", snapshotRef={}, status=self.status,
                      actions=[Action(kind="LEVEL_UP")], rationale="ok",
                      llm={"model": "fake", "latencyMs": 1, "tokensIn": 0, "tokensOut": 0, "error": None})


def snap():
    return {"player": {"tier": 3, "board": [{"name": "X", "keywords": []}],
                       "hand": [{"name": "X", "keywords": []}]}}


class TestEngine(unittest.TestCase):
    def test_decide_runs_tactics_and_calls_advisor(self):
        adv = FakeAdvisor()
        eng = DecisionEngine(adv)
        out = eng.decide(snap(), "ShopPhase", trigger="manual", snapshotRef={"seq": 9})
        self.assertEqual(out.status, "ok")
        self.assertEqual(out.snapshotRef, {"seq": 9})
        self.assertEqual(out.trigger, "manual")
        # advisor received the golden-opportunity finding
        self.assertIn("golden-opportunity", adv.seen[1])

    def test_decide_propagates_advisor_error_unchanged(self):
        eng = DecisionEngine(FakeAdvisor(status="error"))
        out = eng.decide(snap(), "ShopPhase")
        self.assertEqual(out.status, "error")
        self.assertEqual(out.actions, [])


if __name__ == "__main__":
    unittest.main()
