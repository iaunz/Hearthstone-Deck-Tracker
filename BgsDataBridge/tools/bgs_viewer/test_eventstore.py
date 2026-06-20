import threading
import unittest
from eventstore import EventStore


def ev(seq, **kw):
    return {"seq": seq, "type": kw.get("type", "ShopChanged"),
            "at": None, "match": None, "data": {}, "source": "test"}


class TestEventStore(unittest.TestCase):
    def test_append_and_all_sorted_by_seq(self):
        s = EventStore()
        s.append(ev(3)); s.append(ev(1)); s.append(ev(2))
        self.assertEqual([e["seq"] for e in s.all()], [1, 2, 3])

    def test_dedup_by_seq(self):
        s = EventStore()
        self.assertTrue(s.append(ev(2)))
        self.assertFalse(s.append(ev(2)))
        self.assertEqual(len(s.all()), 1)

    def test_since_returns_strictly_after(self):
        s = EventStore()
        for i in (1, 2, 3, 4):
            s.append(ev(i))
        self.assertEqual([e["seq"] for e in s.since(2)], [3, 4])

    def test_since_none_returns_all(self):
        s = EventStore()
        s.append(ev(1)); s.append(ev(2))
        self.assertEqual(len(s.since(None)), 2)

    def test_last_seq(self):
        s = EventStore()
        self.assertIsNone(s.last_seq())
        s.append(ev(5)); s.append(ev(7))
        self.assertEqual(s.last_seq(), 7)

    def test_concurrent_append_no_loss(self):
        s = EventStore()
        def worker(start):
            for i in range(start, start + 100):
                s.append(ev(i))
        threads = [threading.Thread(target=worker, args=(b * 100,))
                   for b in range(4)]
        for t in threads: t.start()
        for t in threads: t.join()
        self.assertEqual(len(s.all()), 400)


if __name__ == "__main__":
    unittest.main()
