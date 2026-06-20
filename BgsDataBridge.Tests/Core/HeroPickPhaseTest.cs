using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class HeroPickPhaseTest
    {
        const int BeginMulligan = 5; // Step.BEGIN_MULLIGAN 的实际值无关；验证比较语义

        [TestMethod]
        public void Active_When_Bgs_NotMenu_Step_AtOrBefore_Mulligan()
        {
            Assert.IsTrue(HeroPickPhase.IsActive(true, false, BeginMulligan, BeginMulligan));
            Assert.IsTrue(HeroPickPhase.IsActive(true, false, 0, BeginMulligan));
        }

        [TestMethod]
        public void Inactive_When_Step_Past_Mulligan()
        {
            // 问题 #2 场景：STEP 已越过 BEGIN_MULLIGAN → 不应再触发 HeroPick
            Assert.IsFalse(HeroPickPhase.IsActive(true, false, BeginMulligan + 1, BeginMulligan));
        }

        [TestMethod]
        public void Inactive_When_Not_Bgs()
        {
            Assert.IsFalse(HeroPickPhase.IsActive(false, false, 0, BeginMulligan));
        }

        [TestMethod]
        public void Inactive_When_InMenu()
        {
            Assert.IsFalse(HeroPickPhase.IsActive(true, true, 0, BeginMulligan));
        }

        [TestMethod]
        public void Inactive_When_Step_Missing()
        {
            // GameEntity tag 缺失 → 调用方传 int.MaxValue → 永不触发
            Assert.IsFalse(HeroPickPhase.IsActive(true, false, int.MaxValue, BeginMulligan));
        }
    }
}
