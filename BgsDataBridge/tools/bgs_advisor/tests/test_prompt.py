import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from decision.prompt import encode_prompt
from decision.heuristics import TacticFinding


def snap():
    return {
        "match": {"turn": 5, "phase": "Shop", "anomaly": {"name": "畸变X", "text": "随从少花1金币"}},
        "availableRaces": ["MURLOC", "BEAST"],
        "player": {"tier": 3, "hero": {"name": "英雄A", "health": 40},
                   "board": [{"name": "雷鳞海妖", "attack": 3, "health": 2, "keywords": ["TAUNT"]}],
                   "hand": [{"name": "鱼人斥候", "attack": 1, "health": 1}],
                   "heroPower": {"name": "技能A", "text": "战吼:..."},
                   "trinkets": [], "questReward": None},
        "shop": {"available": True, "tier": 3, "offers": [{"name": "雷鳞海妖", "attack": 3, "health": 2}]},
    }


class TestPrompt(unittest.TestCase):
    def test_system_message_sets_role_and_json_contract(self):
        system, _ = encode_prompt(snap(), "ShopPhase", [])
        self.assertIn("酒馆战棋", system)
        self.assertIn("JSON", system)

    def test_user_message_contains_state_fields(self):
        _, user = encode_prompt(snap(), "ShopPhase", [])
        self.assertIn("雷鳞海妖", user)
        self.assertIn("3/2", user)            # attack/health compact form
        self.assertIn("TAUNT", user)
        self.assertIn("ShopPhase", user)
        self.assertIn("畸变X", user)           # anomaly surfaces
        self.assertIn("MURLOC", user)         # races surface

    def test_findings_baked_into_user_message(self):
        findings = [TacticFinding("golden-opportunity", "warn", "可合成金色: 雷鳞海妖")]
        _, user = encode_prompt(snap(), "ShopPhase", findings)
        self.assertIn("可合成金色", user)


if __name__ == "__main__":
    unittest.main()
