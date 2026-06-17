using System;
using System.Net.Http;
using System.Text;
using BgsDataBridge.Core;

namespace BgsDataBridge.Webhook
{
    /// <summary>
    /// Production <see cref="IHttpSender"/> backed by
    /// <see cref="System.Net.Http.HttpClient"/>. Synchronous (Send runs on the
    /// dedicated webhook dispatcher thread, never the game thread). Returns the
    /// HTTP status code; throws propagate to <see cref="WebhookDispatcher"/>
    /// which maps them to status 0 (no retry budget change beyond the 4xx rule).
    /// </summary>
    public class HttpSender : IHttpSender
    {
        public int Send(string url, string body, string signature, int timeoutMs)
        {
            using (var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) })
            {
                var content = new StringContent(body, Encoding.UTF8, "application/json");
                var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                if (!string.IsNullOrEmpty(signature))
                    req.Headers.Add("X-BgsBridge-Signature", signature);
                using (var resp = c.SendAsync(req).GetAwaiter().GetResult())
                    return (int)resp.StatusCode;
            }
        }
    }
}
