import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.heuristics import run_tactics, GoldenOpportunity, TauntPositioning, LevelUpWindow


def minion(name, keywords=None):
    return {"name": name, "cardId": name, "attack": 1, "health": 1, "keywords": keywords or []}


class TestHeuristics(unittest.TestCase):
    def test_golden_opportunity_two_same_name(self):
        snap = {"player": {"board": [minion("雷鳞海妖")], "hand": [minion("雷鳞海妖")]}}
        findings = GoldenOpportunity().evaluate(snap, "ShopPhase")
        self.assertIsNotNone(findings)
        self.assertIn("雷鳞海妖", findings.message)

    def test_golden_opportunity_none(self):
        snap = {"player": {"board": [minion("A")], "hand": [minion("B")]}}
        self.assertIsNone(GoldenOpportunity().evaluate(snap, "ShopPhase"))

    def test_taunt_positioning_only_for_positioning_dtype(self):
        # TAUNT minion at index 1 (not leftmost)
        snap = {"player": {"board": [minion("A"), minion("B", ["TAUNT"])]}}
        self.assertIsNone(TauntPositioning().evaluate(snap, "ShopPhase"))  # wrong dtype
        f = TauntPositioning().evaluate(snap, "Positioning")
        self.assertIsNotNone(f)

    def test_taunt_positioning_already_leftmost(self):
        snap = {"player": {"board": [minion("A", ["TAUNT"]), minion("B")]}}
        self.assertIsNone(TauntPositioning().evaluate(snap, "Positioning"))

    def test_level_up_window_high_tier(self):
        # tier 5+ → no level-up window (already near top)
        snap = {"player": {"tier": 5}}
        self.assertIsNone(LevelUpWindow().evaluate(snap, "ShopPhase"))
        snap2 = {"player": {"tier": 3}}
        f = LevelUpWindow().evaluate(snap2, "ShopPhase")
        self.assertIsNotNone(f)

    def test_run_tactics_collects(self):
        snap = {"player": {"tier": 3, "board": [minion("X")], "hand": [minion("X")]}}
        codes = {f.code for f in run_tactics(snap, "ShopPhase")}
        self.assertIn("golden-opportunity", codes)
        self.assertIn("level-up-window", codes)


if __name__ == "__main__":
    unittest.main()
