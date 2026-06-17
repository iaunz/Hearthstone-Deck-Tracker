using System.Collections.Generic;
using Newtonsoft.Json;

namespace BgsDataBridge.Events
{
    public class EventEnvelope
    {
        // [JsonProperty] camelCase names align with the bgs-event/v1 contract
        // (design spec §4.2). The brief's source block omitted these attributes;
        // without them Newtonsoft emits PascalCase ("Seq"/"Event") and the
        // schema test (which asserts j["seq"]/j["event"]) fails.
        [JsonProperty("schema")] public string Schema { get; set; } = "bgs-event/v1";
        [JsonProperty("seq")] public long Seq { get; set; }
        [JsonProperty("event")] public BridgeEventType Event { get; set; }
        [JsonProperty("at")] public string At { get; set; }      // ISO-8601 UTC
        [JsonProperty("match")] public object Match { get; set; }    // 复用 BgsMatch 结构
        [JsonProperty("data")] public object Data { get; set; }
    }
}
