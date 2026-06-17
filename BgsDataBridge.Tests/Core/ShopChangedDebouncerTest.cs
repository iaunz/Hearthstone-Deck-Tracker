using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class ShopChangedDebouncerTest
    {
        class FakeClock : IClock { public long Now; public long NowMs => Now; }

        [TestMethod]
        public void Emits_After_Quiet_Period()
        {
            var clock = new FakeClock();
            var db = new ShopChangedDebouncer(quietMs: 400, clock);
            var emitted = new List<string>();
            db.OnEmit += s => emitted.Add(s);

            db.Update("A", clock.NowMs);   // t=0
            Assert.AreEqual(0, emitted.Count);
            clock.Now = 300; db.Tick();     // 还在窗口内
            Assert.AreEqual(0, emitted.Count);
            clock.Now = 401; db.Tick();     // 静默满
            Assert.AreEqual(1, emitted.Count);
            Assert.AreEqual("A", emitted[0]);
        }

        [TestMethod]
        public void Coalesces_Rapid_Changes_To_Final()
        {
            var clock = new FakeClock();
            var db = new ShopChangedDebouncer(400, clock);
            var emitted = new List<string>();
            db.OnEmit += s => emitted.Add(s);

            db.Update("A", 0);
            clock.Now = 100; db.Update("B", clock.Now); db.Tick();
            clock.Now = 200; db.Update("C", clock.Now); db.Tick();
            clock.Now = 601; db.Tick();     // 距最近变化 401ms
            Assert.AreEqual(1, emitted.Count);
            Assert.AreEqual("C", emitted[0]);  // 只发最终态
        }

        [TestMethod]
        public void Flush_Emits_Pending_Immediately()
        {
            var clock = new FakeClock();
            var db = new ShopChangedDebouncer(400, clock);
            var emitted = new List<string>();
            db.OnEmit += s => emitted.Add(s);
            db.Update("X", 0);
            db.Flush();                      // 立即 flush（阶段切换/卸载）
            Assert.AreEqual(1, emitted.Count);
            Assert.AreEqual("X", emitted[0]);
        }

        [TestMethod]
        public void No_Emit_When_No_Change()
        {
            var clock = new FakeClock();
            var db = new ShopChangedDebouncer(400, clock);
            var n = 0;
            db.OnEmit += s => n++;
            clock.Now = 5000; db.Tick();
            Assert.AreEqual(0, n);
        }
    }
}
