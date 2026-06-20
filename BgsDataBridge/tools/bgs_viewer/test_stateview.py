import unittest
from stateview import reconstruct_state, compute_progression


def full(turn, hp=30, board=None, tier=2, shop_tier=None):
    return {"type": "CombatPhaseStart",
            "data": {"schema": "bgs-state/v1",
                     "match": {"turn": turn},
                     "player": {"tier": tier, "hero": {"health": hp},
                                "board": board or []},
                     "shop": ({"tier": shop_tier} if shop_tier is not None else None)}}


def shop(tier):
    return {"type": "ShopChanged", "data": {"shop": {"tier": tier},
            "turn": 9, "phase": "Shop"}}


def empty():
    return {"type": "HeroPick", "data": {}}


class TestReconstruct(unittest.TestCase):
    def test_full_replaces_view(self):
        v = reconstruct_state([full(1, hp=30), full(2, hp=25)])
        self.assertEqual(v["player"]["hero"]["health"], 25)
        self.assertEqual(v["match"]["turn"], 2)

    def test_shop_patches_only_shop(self):
        v = reconstruct_state([full(1, hp=30), shop(5)])
        # board/hero from full snapshot retained
        self.assertEqual(v["player"]["hero"]["health"], 30)
        # shop replaced by ShopChanged
        self.assertEqual(v["shop"]["tier"], 5)

    def test_empty_leaves_view(self):
        v = reconstruct_state([full(1, hp=30), empty()])
        self.assertEqual(v["player"]["hero"]["health"], 30)

    def test_empty_before_any_full(self):
        self.assertEqual(reconstruct_state([empty()]), {})


class TestProgression(unittest.TestCase):
    def test_samples_combat_only(self):
        events = [
            full(1, hp=30, board=[{"attack": 3}], tier=1),
            shop(2),  # not sampled
            full(2, hp=25, board=[{"attack": 3}, {"attack": 4}], tier=2),
            full(3, hp=18, board=[{"attack": 10}], tier=3),
        ]
        prog = compute_progression(events)
        self.assertEqual([p["turn"] for p in prog], [1, 2, 3])
        self.assertEqual(prog[1]["boardAtk"], 7)
        self.assertEqual(prog[2]["heroHp"], 18)
        self.assertEqual(prog[2]["tier"], 3)

    def test_no_combat_returns_empty(self):
        self.assertEqual(compute_progression([shop(1), empty()]), [])


if __name__ == "__main__":
    unittest.main()
