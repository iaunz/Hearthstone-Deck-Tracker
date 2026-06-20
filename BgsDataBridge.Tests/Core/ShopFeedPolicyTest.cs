using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class ShopFeedPolicyTest
    {
        [TestMethod]
        public void Feeds_When_NonEmpty_Shop_In_Shop_Phase()
        {
            Assert.IsTrue(ShopFeedPolicy.ShouldFeed(1, true));
            Assert.IsTrue(ShopFeedPolicy.ShouldFeed(6, true));
        }

        [TestMethod]
        public void Does_Not_Feed_When_Empty()
        {
            // 问题 #3：空商店（已买空）不喂 → 不产生空载荷 ShopChanged
            Assert.IsFalse(ShopFeedPolicy.ShouldFeed(0, true));
        }

        [TestMethod]
        public void Does_Not_Feed_When_Not_Shop_Phase()
        {
            Assert.IsFalse(ShopFeedPolicy.ShouldFeed(6, false));
        }
    }
}
