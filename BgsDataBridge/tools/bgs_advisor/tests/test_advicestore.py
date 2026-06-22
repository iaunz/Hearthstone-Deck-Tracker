import os, sys, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
from advicestore import AdviceStore
from decision.advice import Advice


def advice(dtype, status="ok"):
    return Advice(decisionType=dtype, trigger="manual", snapshotRef={"seq": 1}, status=status)


class TestAdviceStore(unittest.TestCase):
    def test_put_stamps_monotonic_ts(self):
        s = AdviceStore()
        a = advice("ShopPhase")
        s.put(a)
        self.assertGreater(a.ts, 0)

    def test_latest_per_decisiontype(self):
        s = AdviceStore()
        s.put(advice("ShopPhase"))
        s.put(advice("Positioning"))
        self.assertEqual(s.latest("ShopPhase").decisionType, "ShopPhase")
        self.assertEqual(s.latest("Positioning").decisionType, "Positioning")

    def test_put_replaces_latest_for_same_type(self):
        s = AdviceStore()
        first = advice("ShopPhase"); s.put(first)
        second = advice("ShopPhase"); s.put(second)
        self.assertIs(s.latest("ShopPhase"), second)
        self.assertGreater(second.ts, first.ts)

    def test_since_returns_newer_only(self):
        s = AdviceStore()
        a = advice("ShopPhase"); s.put(a)
        b = advice("Positioning"); s.put(b)
        got = s.since(a.ts)
        self.assertEqual([x.decisionType for x in got], ["Positioning"])

    def test_latest_none_default(self):
        self.assertIsNone(AdviceStore().latest())


if __name__ == "__main__":
    unittest.main()
