using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Http;
using BgsDataBridge.Projector;

namespace BgsDataBridge.Tests.Http
{
    [TestClass]
    public class RouteDispatcherTest
    {
        class FakeSource : IGameSource
        {
            public GameStateView Capture() => new GameStateView { InMatch = true, IsBattlegrounds = true, Phase = "Shop", Turn = 2 };
        }

        [TestMethod]
        public void Health_Returns_200()
        {
            var d = new RouteDispatcher(new FakeSource(), new GameStateProjector());
            var r = d.Dispatch("/health", "");
            Assert.AreEqual(200, r.Status);
        }

        [TestMethod]
        public void State_Returns_200_Json()
        {
            var d = new RouteDispatcher(new FakeSource(), new GameStateProjector());
            var r = d.Dispatch("/state", "");
            Assert.AreEqual(200, r.Status);
            StringAssert.Contains(r.Body, "\"schema\":\"bgs-state/v1\"");
        }

        [TestMethod]
        public void Unknown_Returns_404()
        {
            var d = new RouteDispatcher(new FakeSource(), new GameStateProjector());
            var r = d.Dispatch("/nope", "");
            Assert.AreEqual(404, r.Status);
        }

        [TestMethod]
        public void Pretty_Query_Indents()
        {
            var d = new RouteDispatcher(new FakeSource(), new GameStateProjector());
            var compact = d.Dispatch("/state", "").Body;
            var pretty = d.Dispatch("/state", "pretty=1").Body;
            Assert.IsTrue(pretty.Length > compact.Length); // 缩进后更长
        }
    }
}
