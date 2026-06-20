import json
import os
import tempfile
import unittest
from logparser import parse, parse_ndjson, parse_text_log


def write_tmp(text, suffix):
    fd, path = tempfile.mkstemp(suffix=suffix, text=True)
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        f.write(text)
    return path


class TestNdjson(unittest.TestCase):
    def test_parses_envelopes_and_counts_bad(self):
        a = {"schema": "bgs-event/v1", "seq": 1, "event": "MatchStart",
             "at": "2026-06-20T10:00:00+00:00", "match": {"turn": 0},
             "data": {"schema": "bgs-state/v1", "player": {"name": "x"}}}
        b = {"schema": "bgs-event/v1", "seq": 2, "event": "ShopChanged",
             "at": "2026-06-20T10:00:01+00:00", "data": {"shop": {"tier": 1}}}
        text = json.dumps(a, ensure_ascii=False) + "\n" + \
               "NOT JSON\n" + json.dumps(b, ensure_ascii=False) + "\n\n"
        path = write_tmp(text, ".jsonl")
        res = parse_ndjson(path)
        self.assertEqual([e["seq"] for e in res.events], [1, 2])
        self.assertEqual(res.skipped, 1)
        self.assertEqual(res.events[0]["type"], "MatchStart")
        self.assertEqual(res.events[1]["data"], {"shop": {"tier": 1}})
        self.assertEqual(res.events[0]["source"], "log-jsonl")


TEXT_LOG = """
────────────────────────────────────────────────────────────────
10:00:00.000  ← /
  #1  MatchStart   2026-06-20T10:00:00+00:00
  BattlegroundsSolo · turn 0
  ── data (json) ──
    {
      "schema": "bgs-state/v1",
      "match": {"turn": 0},
      "player": {"name": "iaun"}
    }

────────────────────────────────────────────────────────────────
10:00:01.000  ← /
  #2  ShopChanged   2026-06-20T10:00:01+00:00
  BattlegroundsSolo · turn 1
  ── shop (tier 1) ──
    [1] Risen Rider  2/1
  ── data (json) ──
    {
      "shop": {"tier": 1, "offers": []},
      "turn": 1,
      "phase": "Shop"
    }

────────────────────────────────────────────────────────────────
10:00:02.000  ← /
  #3  HeroPick   2026-06-20T10:00:02+00:00
  BattlegroundsSolo · turn 0
  data: (空 payload)
"""


class TestTextLog(unittest.TestCase):
    def test_parses_full_shop_and_empty(self):
        path = write_tmp(TEXT_LOG, ".log")
        res = parse_text_log(path)
        self.assertEqual([e["seq"] for e in res.events], [1, 2, 3])
        self.assertEqual(res.events[0]["type"], "MatchStart")
        # full snapshot data extracted
        self.assertEqual(res.events[0]["data"]["schema"], "bgs-state/v1")
        self.assertEqual(res.events[0]["data"]["player"]["name"], "iaun")
        # shop-only data extracted (human shop block ignored)
        self.assertEqual(res.events[1]["data"]["shop"]["tier"], 1)
        self.assertEqual(res.events[1]["data"]["turn"], 1)
        # empty payload -> {}
        self.assertEqual(res.events[2]["data"], {})
        self.assertEqual(res.events[0]["source"], "log-text")

    def test_parse_dispatches_by_extension(self):
        path = write_tmp(TEXT_LOG, ".log")
        self.assertEqual(len(parse(path).events), 3)


if __name__ == "__main__":
    unittest.main()
