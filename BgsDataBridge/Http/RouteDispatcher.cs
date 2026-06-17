using System;
using System.Collections.Specialized;
using System.Web;
using BgsDataBridge.Projector;
using Newtonsoft.Json;

namespace BgsDataBridge.Http
{
    public class HttpResponse { public int Status; public string Body; public string ContentType = "application/json"; }

    public class RouteDispatcher
    {
        private readonly IGameSource _source;
        private readonly GameStateProjector _projector;
        public RouteDispatcher(IGameSource source, GameStateProjector projector) { _source = source; _projector = projector; }

        public HttpResponse Dispatch(string path, string query)
        {
            try
            {
                if (path == "/health") return Health();
                if (path == "/state") return State(query);
                return new HttpResponse { Status = 404, Body = "{\"error\":\"not found\"}" };
            }
            catch (Exception ex)
            {
                return new HttpResponse { Status = 500, Body = "{\"error\":\"" + Escape(ex.Message) + "\"}" };
            }
        }

        HttpResponse Health()
        {
            var v = SafeCapture();
            return new HttpResponse { Status = 200, Body = "{\"status\":\"ok\",\"inMatch\":" + (v?.InMatch ?? false).ToString().ToLower() + ",\"isBattlegrounds\":" + (v?.IsBattlegrounds ?? false).ToString().ToLower() + "}" };
        }

        HttpResponse State(string query)
        {
            var qs = HttpUtility.ParseQueryString(query ?? "");
            bool pretty = qs["pretty"] == "1";
            bool includeText = qs["text"] == "1";
            var v = SafeCapture();
            if (v == null) return new HttpResponse { Status = 503, Body = "{\"error\":\"capture failed\"}" };
            var snap = _projector.Project(v, includeText);
            snap.Locale = "enUS"; // HdtGameSource 实际可注入选定语言
            var fmt = pretty ? Formatting.Indented : Formatting.None;
            return new HttpResponse { Status = 200, Body = JsonConvert.SerializeObject(snap, fmt) };
        }

        GameStateView SafeCapture() { try { return _source.Capture(); } catch { return null; } }
        static string Escape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
