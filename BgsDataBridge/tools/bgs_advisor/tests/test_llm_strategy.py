import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.llm_strategy import LlmStrategyAdvisor


def snap():
    return {"match": {"turn": 5}, "player": {"tier": 3, "board": [], "hand": []}, "shop": {"offers": []}}


class TestLlmStrategy(unittest.TestCase):
    def test_advice_ok_parses_llm_json(self):
        adv = LlmStrategyAdvisor("https://x", "k", "m")
        adv._call_llm = lambda system, user: '{"rationale":"升本","actions":[{"kind":"LEVEL_UP"}]}'
        out = adv.advise(snap(), "ShopPhase", [])
        self.assertEqual(out.status, "ok")
        self.assertEqual(out.actions[0].kind, "LEVEL_UP")
        self.assertEqual(out.rationale, "升本")
        self.assertEqual(out.llm["model"], "m")
        self.assertIsNone(out.llm["error"])

    def test_advice_error_when_call_raises(self):
        adv = LlmStrategyAdvisor("https://x", "k", "m")
        def boom(s, u): raise RuntimeError("network down")
        adv._call_llm = boom
        out = adv.advise(snap(), "ShopPhase", [])
        self.assertEqual(out.status, "error")
        self.assertEqual(out.actions, [])
        self.assertIn("network down", out.llm["error"])

    def test_advice_error_when_json_unparseable_after_retry(self):
        adv = LlmStrategyAdvisor("https://x", "k", "m")
        adv._call_llm = lambda s, u: "not json at all"
        out = adv.advise(snap(), "ShopPhase", [])
        self.assertEqual(out.status, "error")

    def test_advice_error_when_action_missing_kind_after_retry(self):
        adv = LlmStrategyAdvisor("https://x", "k", "m")
        adv._call_llm = lambda s, u: '{"rationale":"x","actions":[{"cardId":"ABC"}]}'
        out = adv.advise(snap(), "ShopPhase", [])
        self.assertEqual(out.status, "error")
        self.assertEqual(out.actions, [])
        self.assertIn("missing required field", out.llm["error"])


if __name__ == "__main__":
    unittest.main()
