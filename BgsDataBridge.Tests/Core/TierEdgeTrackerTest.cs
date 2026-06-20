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
    }
}
