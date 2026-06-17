using System.Collections.Generic;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Config;
using BgsDataBridge.Core;
using BgsDataBridge.Events;
using BgsDataBridge.Webhook;

namespace BgsDataBridge.Tests.Webhook
{
    [TestClass]
    public class WebhookDispatcherTest
    {
        class FakeClock : IClock { public long Now; public long NowMs => Now; }
        // Wall-clock IClock used only by the retry test so that BackoffWait's
        // real-time loop actually elapses (FakeClock.Now is controllable and
        // never advances on its own, which the other four dispatcher tests
        // rely on because they do not exercise backoff).
        class WallClock : IClock { public long NowMs => System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
        class FakeSender : IHttpSender
        {
            public List<(string url, string body)> Sent = new List<(string, string)>();
            public int Calls;
            public int FailFirstNTimes = 0;
            public int Send(string url, string body, string signature, int timeoutMs)
            {
                Calls++;
                if (Calls <= FailFirstNTimes) return 500; // 模拟失败
                Sent.Add((url, body));
                return 200;
            }
        }

        static EventEnvelope Ev(long seq, BridgeEventType t)
            => new EventEnvelope { Seq = seq, Event = t, At = "2026-06-17T00:00:00Z", Data = "{}" };

        [TestMethod]
        public void Delivers_To_Subscribed_Url()
        {
            var cfg = new BridgeConfig { Webhooks = { new WebhookConfig { Url = "u1" } } };
            var sender = new FakeSender();
            var clock = new FakeClock();
            using (var d = new WebhookDispatcher(cfg, sender, clock))
            {
                d.Start();
                d.Enqueue(Ev(1, BridgeEventType.ShopPhaseStart));
                Thread.Sleep(150);
            }
            Assert.AreEqual(1, sender.Sent.Count);
            Assert.AreEqual("u1", sender.Sent[0].url);
        }

        [TestMethod]
        public void Event_Filter_Respects_Subscription()
        {
            var cfg = new BridgeConfig { Webhooks = {
                new WebhookConfig { Url = "onlyShop", Events = new List<string>{ "ShopPhaseStart" } } } };
            var sender = new FakeSender();
            var clock = new FakeClock();
            using (var d = new WebhookDispatcher(cfg, sender, clock))
            {
                d.Start();
                d.Enqueue(Ev(1, BridgeEventType.CombatPhaseStart)); // 不匹配
                d.Enqueue(Ev(2, BridgeEventType.ShopPhaseStart));   // 匹配
                Thread.Sleep(150);
            }
            Assert.AreEqual(1, sender.Sent.Count);
            Assert.AreEqual("onlyShop", sender.Sent[0].url);
        }

        [TestMethod]
        public void Retries_On_5xx_Then_Succeeds()
        {
            var cfg = new BridgeConfig { Webhooks = { new WebhookConfig { Url = "u" } } };
            cfg.Webhook.MaxRetries = 5;
            var sender = new FakeSender { FailFirstNTimes = 2 };
            // WallClock advances from real wall time so BackoffWait actually
            // elapses the 200ms + 400ms backoffs (~0.6s total).
            var clock = new WallClock();
            using (var d = new WebhookDispatcher(cfg, sender, clock))
            {
                d.Start();
                d.Enqueue(Ev(1, BridgeEventType.ShopPhaseStart));
                Thread.Sleep(1000);
            }
            Assert.AreEqual(1, sender.Sent.Count); // 第 3 次成功送达
            Assert.AreEqual(3, sender.Calls);
        }

        [TestMethod]
        public void Queue_Cap_Drops_When_Full()
        {
            var cfg = new BridgeConfig { Webhooks = { new WebhookConfig { Url = "u" } } };
            cfg.Webhook.QueueCap = 2;
            var sender = new FakeSender();
            // 故意不 Start，发送线程不 drain → 队列堆满
            var d = new WebhookDispatcher(cfg, sender, new FakeClock());
            Assert.IsTrue(d.Enqueue(Ev(1, BridgeEventType.ShopPhaseStart)));
            Assert.IsTrue(d.Enqueue(Ev(2, BridgeEventType.ShopPhaseStart)));
            Assert.IsFalse(d.Enqueue(Ev(3, BridgeEventType.ShopPhaseStart))); // 满了丢弃
            d.Stop(0);
        }

        [TestMethod]
        public void Stop_Flushes_Pending()
        {
            var cfg = new BridgeConfig { Webhooks = { new WebhookConfig { Url = "u" } } };
            var sender = new FakeSender();
            var d = new WebhookDispatcher(cfg, sender, new FakeClock());
            d.Enqueue(Ev(1, BridgeEventType.ShopPhaseStart));
            d.Start();              // 启 drain
            d.Stop(1000);           // flush 并等待
            Assert.AreEqual(1, sender.Sent.Count);
        }
    }
}
