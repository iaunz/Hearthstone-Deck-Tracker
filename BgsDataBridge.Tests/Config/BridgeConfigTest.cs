using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Config;

namespace BgsDataBridge.Tests.Config
{
    [TestClass]
    public class BridgeConfigTest
    {
        [TestMethod]
        public void Load_ParsesWebhooksAndToggles()
        {
            var json = @"{""enabled"":true,""port"":5273,
              ""webhooks"":[{""url"":""http://localhost:8000/bgs"",""events"":[""*""]}],
              ""shopChangedQuietMs"":400}";
            var cfg = BridgeConfig.Load(json);
            Assert.IsTrue(cfg.Enabled);
            Assert.AreEqual(5273, cfg.Port);
            Assert.AreEqual(1, cfg.Webhooks.Count);
            Assert.AreEqual("http://localhost:8000/bgs", cfg.Webhooks[0].Url);
            Assert.AreEqual(400, cfg.ShopChangedQuietMs);
        }

        [TestMethod]
        public void Defaults_WhenEmpty()
        {
            var cfg = BridgeConfig.Load("{}");
            Assert.IsTrue(cfg.Enabled);
            Assert.AreEqual(5273, cfg.Port);
            Assert.AreEqual(400, cfg.ShopChangedQuietMs);
            Assert.AreEqual(0, cfg.Webhooks.Count);
        }

        [TestMethod]
        public void RoundTrip_PreservesValues()
        {
            var cfg = BridgeConfig.Load(@"{""port"":6000,""webhooks"":[{""url"":""u"",""events"":[""ShopPhaseStart""]}],""shopChangedQuietMs"":250}");
            var again = BridgeConfig.Load(cfg.ToJson());
            Assert.AreEqual(6000, again.Port);
            Assert.AreEqual("ShopPhaseStart", again.Webhooks[0].Events[0]);
            Assert.AreEqual(250, again.ShopChangedQuietMs);
        }
    }
}
