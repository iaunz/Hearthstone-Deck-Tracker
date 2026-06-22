import os, sys, tempfile, unittest
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
import config


class TestConfig(unittest.TestCase):
    def test_load_missing_file_returns_defaults(self):
        with tempfile.TemporaryDirectory() as d:
            cfg = config.load(os.path.join(d, "nope.json"))
            self.assertEqual(cfg["model"], config.DEFAULTS["model"])
            self.assertEqual(cfg["apiKey"], "")

    def test_save_then_load_roundtrip(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "c.json")
            config.save(p, {"apiKey": "sk-secret", "model": "claude-sonnet-4-6"})
            cfg = config.load(p)
            self.assertEqual(cfg["apiKey"], "sk-secret")

    def test_mask_hides_key(self):
        cfg = {"apiKey": "sk-secret", "model": "m", "baseUrl": "u",
               "autoTriggerPicks": True, "timeoutMs": 30000}
        m = config.mask(cfg)
        self.assertNotIn("sk-secret", str(m))
        self.assertTrue(m["hasApiKey"])
        self.assertNotIn("apiKey", m)

    def test_load_merges_defaults(self):
        with tempfile.TemporaryDirectory() as d:
            p = os.path.join(d, "c.json")
            config.save(p, {"apiKey": "x"})
            cfg = config.load(p)
            self.assertEqual(cfg["apiKey"], "x")
            self.assertEqual(cfg["timeoutMs"], config.DEFAULTS["timeoutMs"])


if __name__ == "__main__":
    unittest.main()
