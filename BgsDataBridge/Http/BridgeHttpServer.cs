using System;
using System.Net;
using System.Threading;
using BgsDataBridge.Config;

namespace BgsDataBridge.Http
{
    public class BridgeHttpServer
    {
        private readonly BridgeConfig _cfg;
        private readonly RouteDispatcher _dispatcher;
        private HttpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        public int ActualPort { get; private set; }

        public BridgeHttpServer(BridgeConfig cfg, RouteDispatcher dispatcher) { _cfg = cfg; _dispatcher = dispatcher; }

        public int Start()
        {
            for (int port = _cfg.Port; port < _cfg.Port + 10; port++)
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://localhost:" + port + "/");
                try { l.Start(); _listener = l; ActualPort = port; break; }
                catch { /* 端口占用，试下一个 */ }
            }
            if (_listener == null) throw new InvalidOperationException("no free port");
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "BgsBridge.Http" };
            _thread.Start();
            return ActualPort;
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            _thread?.Join(1000);
        }

        void Run()
        {
            while (_running)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { if (_running) continue; else break; }
                try { Handle(ctx); }
                catch { /* 单请求失败不影响服务 */ }
            }
        }

        void Handle(HttpListenerContext ctx)
        {
            // CORS + OPTIONS 预检
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, OPTIONS";
            if (string.Equals(ctx.Request.HttpMethod, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            { ctx.Response.StatusCode = 204; ctx.Response.Close(); return; }

            var resp = _dispatcher.Dispatch(ctx.Request.Url.AbsolutePath, ctx.Request.Url.Query.TrimStart('?'));
            var bytes = System.Text.Encoding.UTF8.GetBytes(resp.Body ?? "");
            ctx.Response.ContentType = resp.ContentType;
            ctx.Response.StatusCode = resp.Status;
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.Close();
        }
    }
}
