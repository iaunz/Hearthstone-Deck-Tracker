using System.Collections.Generic;
using BgsDataBridge.Events;
using Newtonsoft.Json;

namespace BgsDataBridge.Config
{
    public class WebhookConfig
    {
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("events", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Events { get; set; } = new List<string> { "*" };
        [JsonProperty("secret", NullValueHandling = NullValueHandling.Ignore)] public string Secret { get; set; }

        public bool Wants(BridgeEventType? evt) // 稍后 Task 5 用
            => evt == null || Events.Contains("*") || Events.Contains(evt.ToString());
    }

    public class BridgeConfig
    {
        [JsonProperty("enabled")] public bool Enabled { get; set; } = true;
        [JsonProperty("port")] public int Port { get; set; } = 5273;
        [JsonProperty("webhooks")] public List<WebhookConfig> Webhooks { get; set; } = new List<WebhookConfig>();
        [JsonProperty("shopChangedQuietMs")] public int ShopChangedQuietMs { get; set; } = 400;
        [JsonProperty("webhook")] public WebhookHttpConfig Webhook { get; set; } = new WebhookHttpConfig();
        [JsonProperty("logLevel")] public string LogLevel { get; set; } = "Info";

        public static BridgeConfig Load(string json)
        {
            var cfg = string.IsNullOrWhiteSpace(json)
                ? new BridgeConfig()
                : JsonConvert.DeserializeObject<BridgeConfig>(json) ?? new BridgeConfig();
            if (cfg.Webhook == null) cfg.Webhook = new WebhookHttpConfig();
            // M1: belt-and-suspenders null-guard for Webhooks. The
            // WebhookDispatcher ctor already coalesces this, but a config
            // file with "webhooks":null should not surface a null collection
            // on the loaded model either.
            if (cfg.Webhooks == null) cfg.Webhooks = new List<WebhookConfig>();
            return cfg;
        }
        public string ToJson() => JsonConvert.SerializeObject(this, Formatting.Indented);
    }
    public class WebhookHttpConfig
    {
        [JsonProperty("timeoutMs")] public int TimeoutMs { get; set; } = 3000;
        [JsonProperty("maxRetries")] public int MaxRetries { get; set; } = 4;
        [JsonProperty("queueCap")] public int QueueCap { get; set; } = 1000;
    }
}
