import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.advice import Advice, Action, STATUSES


class TestAdvice(unittest.TestCase):
    def test_action_roundtrip(self):
        a = Action(kind="BUY", cardId="CS3_014", name="雷鳞海妖", note="核心")
        d = a.to_dict()
        self.assertEqual(d["kind"], "BUY")
        self.assertEqual(d["cardId"], "CS3_014")
        self.assertIsNone(d["index"])  # None keys still round-trip
        self.assertEqual(Action.from_dict(d), a)

    def test_advice_roundtrip_ok(self):
        adv = Advice(
            decisionType="ShopPhase", trigger="manual",
            snapshotRef={"seq": 7, "turn": 5, "capturedAt": "2026-06-22T00:00:00Z"},
            status="ok",
            actions=[Action(kind="LEVEL_UP"), Action(kind="SELL", cardId="X1", name="鱼人")],
            rationale="升本窗口已开",
            llm={"model": "claude-sonnet-4-6", "latencyMs": 1200, "tokensIn": 900,
                 "tokensOut": 40, "error": None},
        )
        d = adv.to_dict()
        self.assertEqual(d["schema"], "bgs-advice/v1")
        self.assertEqual(d["status"], "ok")
        self.assertEqual(len(d["actions"]), 2)
        back = Advice.from_dict(d)
        self.assertEqual(back.decisionType, "ShopPhase")
        self.assertEqual(back.actions[1].name, "鱼人")
        self.assertEqual(back.llm["model"], "claude-sonnet-4-6")

    def test_advice_error_status_no_actions(self):
        adv = Advice(decisionType="ShopPhase", trigger="manual",
                     snapshotRef={"seq": 1}, status="error", actions=[],
                     rationale=None, llm={"error": "timeout"})
        self.assertEqual(adv.to_dict()["status"], "error")

    def test_invalid_status_rejected(self):
        with self.assertRaises(ValueError):
            Advice(decisionType="ShopPhase", trigger="manual",
                   snapshotRef={}, status="bogus")


if __name__ == "__main__":
    unittest.main()
