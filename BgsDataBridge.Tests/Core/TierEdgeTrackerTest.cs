using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class TierEdgeTrackerTest
    {
        [TestMethod]
        public void First_Observe_Baselines_No_Emit()
        {
            Assert.IsNull(new TierEdgeTracker().Observe(3));
        }

        [TestMethod]
        public void Strict_Increase_Emits_From_To()
        {
            var t = new TierEdgeTracker();
            t.Observe(2);
            var r = t.Observe(3);
            Assert.IsNotNull(r);
            Assert.AreEqual(2, r.Value.Item1);
            Assert.AreEqual(3, r.Value.Item2);
        }

        [TestMethod]
        public void Equal_Does_Not_Emit()
        {
            var t = new TierEdgeTracker();
            t.Observe(3);
            Assert.IsNull(t.Observe(3));
        }

        [TestMethod]
        public void Decrease_Does_Not_Emit()
        {
            var t = new TierEdgeTracker();
            t.Observe(4);
            Assert.IsNull(t.Observe(3));   // 异常下降不触发，仅更新基线
        }

        [TestMethod]
        public void Reset_Rebaselines()
        {
            var t = new TierEdgeTracker();
            t.Observe(5);
            t.Reset();
            Assert.IsNull(t.Observe(1));   // reset 后首帧建基线
            var r = t.Observe(2);
            Assert.AreEqual(1, r.Value.Item1);
        }

        [TestMethod]
        public void Repeated_Increments_Each_Emit()
        {
            var t = new TierEdgeTracker();
            t.Observe(1);
            Assert.AreEqual(2, t.Observe(2).Value.Item2);
            Assert.AreEqual(3, t.Observe(3).Value.Item2);
        }

        [TestMethod]
        public void Zero_Or_Negative_Tier_Treated_As_Unknown_No_Emit()
        {
            // 回归（runtime log #3）：开局 ReadTechLevel 在英雄 PLAYER_TECH_LEVEL
            // 建立前返回 0。tier<=0 应视为"未知"——不建基线、不触发，避免 spurious
            // {0→1}。首个真实 tier(1) 才建基线，首次触发应是 {1→2}。
            var t = new TierEdgeTracker();
            Assert.IsNull(t.Observe(0));       // 0 忽略，不动基线
            Assert.IsNull(t.Observe(0));       // 仍 0，仍忽略
            Assert.IsNull(t.Observe(1));       // 首个真实 tier 建基线，不触发
            var r = t.Observe(2);
            Assert.IsNotNull(r);
            Assert.AreEqual(1, r.Value.Item1);  // from=1（不是 0）
            Assert.AreEqual(2, r.Value.Item2);
        }
    }
}
