using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using BgsDataBridge.Config;
using BgsDataBridge.Core;
using BgsDataBridge.Events;
using Newtonsoft.Json;

namespace BgsDataBridge.Webhook
{
    // 生产者只 Enqueue（绝不阻塞游戏线程）；专用发送线程 drain + 退避。
    public class WebhookDispatcher : IDisposable
    {
        private readonly BridgeConfig _cfg;
        private readonly IHttpSender _sender;
        private readonly IClock _clock;
        private readonly BlockingCollection<EventEnvelope> _queue;
        private readonly List<WebhookConfig> _webhooks;
        private Thread _thread;
        private volatile bool _running;

        public WebhookDispatcher(BridgeConfig cfg, IHttpSender sender, IClock clock)
        {
            _cfg = cfg;
            _sender = sender;
            _clock = clock;
            _queue = new BlockingCollection<EventEnvelope>(_cfg.Webhook.QueueCap);
            // Null-coalesce guard (Task 2 review M1): a config with
            // "webhooks":null would NRE the foreach in Run().
            _webhooks = _cfg.Webhooks ?? new List<WebhookConfig>();
        }

        public bool Enqueue(EventEnvelope ev)
        {
            try { return _queue.TryAdd(ev, 0); } catch { return false; }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "BgsBridge.Webhook" };
            _thread.Start();
        }

        public void Stop(int flushMs)
        {
            _running = false;
            _queue.CompleteAdding();
            _thread?.Join(flushMs);
        }

        // TryTake polling loop (brief's SECOND, corrected Run() — the
        // GetConsumatingEnumerableSafe() version referenced a non-existent API).
        // After _running flips to false (Stop), we drain any items enqueued
        // before Stop so the flush contract holds within the Join window.
        private void Run()
        {
            while (_running)
            {
                EventEnvelope ev;
                try { if (!_queue.TryTake(out ev, 100)) continue; }
                catch { break; }
                Process(ev);
            }
            // Flush drain: items enqueued before Stop must still be delivered.
            EventEnvelope tail;
            while (_queue.TryTake(out tail, 0)) Process(tail);
        }

        private void Process(EventEnvelope ev)
        {
            var body = JsonConvert.SerializeObject(ev);
            foreach (var wh in _webhooks)
            {
                if (!wh.Wants(ev.Event)) continue;
                SendWithRetry(wh, body);
            }
        }

        private void SendWithRetry(WebhookConfig wh, string body)
        {
            var sig = string.IsNullOrEmpty(wh.Secret) ? null : HmacSigner.Sign(body, wh.Secret);
            int attempt = 0;
            int backoff = 200;
            // do/while: always attempt at least one send even after Stop has
            // flipped _running (so the flush drain still delivers), then gate
            // only *retries* on _running.
            bool keepGoing;
            do
            {
                int status;
                try { status = _sender.Send(wh.Url, body, sig, _cfg.Webhook.TimeoutMs); }
                catch { status = 0; }
                if (status >= 200 && status < 300) return;
                if (status >= 400 && status < 500 && status != 429) return; // 4xx 不重试
                if (++attempt > _cfg.Webhook.MaxRetries) return;
                BackoffWait(backoff);
                backoff = Math.Min(backoff * 2, 5000);
                keepGoing = _running;
            } while (keepGoing);
        }

        // Wait roughly `backoff` ms via _clock.NowMs (testable seam). With
        // the real SystemClock the deadline is reached naturally as real
        // time passes. Production backoff always elapses the full interval
        // modulo _running; tests that exercise backoff supply a wall-clock
        // IClock (see WallClock in WebhookDispatcherTest) rather than the
        // controllable FakeClock used by the non-backoff tests.
        private void BackoffWait(int backoff)
        {
            var target = _clock.NowMs + backoff;
            while (_running && _clock.NowMs < target)
                Thread.Sleep(10);
        }

        public void Dispose() { Stop(0); _queue.Dispose(); }
    }
}
