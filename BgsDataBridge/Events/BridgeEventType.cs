using Newtonsoft.Json;
using System.Runtime.Serialization;

namespace BgsDataBridge.Events
{
    [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
    public enum BridgeEventType
    {
        [EnumMember(Value = "MatchStart")] MatchStart,
        [EnumMember(Value = "MatchEnd")] MatchEnd,
        [EnumMember(Value = "HeroPick")] HeroPick,
        [EnumMember(Value = "TrinketPick")] TrinketPick,
        [EnumMember(Value = "ShopPhaseStart")] ShopPhaseStart,
        [EnumMember(Value = "CombatPhaseStart")] CombatPhaseStart,
        [EnumMember(Value = "ShopChanged")] ShopChanged
    }
}
