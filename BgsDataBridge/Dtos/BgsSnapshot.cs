using System.Collections.Generic;
using Newtonsoft.Json;

namespace BgsDataBridge.Dtos
{
    public class BgsSnapshot
    {
        [JsonProperty("schema")] public string Schema { get; set; } = "bgs-state/v1";
        [JsonProperty("locale")] public string Locale { get; set; }
        [JsonProperty("capturedAt")] public string CapturedAt { get; set; }
        [JsonProperty("inMatch")] public bool InMatch { get; set; }
        [JsonProperty("partial")] public bool Partial { get; set; }
        [JsonProperty("match")] public BgsMatch Match { get; set; }
        [JsonProperty("availableRaces")] public List<string> AvailableRaces { get; set; }
        [JsonProperty("player")] public BgsPlayer Player { get; set; }
        // shop/lastOpponent/lobby: brief used NullValueHandling.Ignore, but the
        // design spec (§4.1) emits them explicitly as `null` when absent, and the
        // schema test asserts j["lastOpponent"].Type == JTokenType.Null (a missing
        // key would make the indexer return C# null and throw NRE on .Type).
        // Keep them always-emitted so consumers can distinguish "field absent
        // because unknown" from "field known to be empty this snapshot".
        [JsonProperty("shop")] public BgsShop Shop { get; set; }
        [JsonProperty("lastOpponent")] public BgsLastOpponent LastOpponent { get; set; }
        [JsonProperty("lobby")] public BgsLobby Lobby { get; set; }
    }

    public class BgsMatch
    {
        [JsonProperty("gameType")] public string GameType { get; set; }
        [JsonProperty("isBattlegrounds")] public bool IsBattlegrounds { get; set; }
        [JsonProperty("isDuos")] public bool IsDuos { get; set; }
        [JsonProperty("spectator")] public bool Spectator { get; set; }
        [JsonProperty("phase")] public string Phase { get; set; }
        [JsonProperty("turn")] public int Turn { get; set; }
        [JsonProperty("gameUuid", NullValueHandling = NullValueHandling.Ignore)] public string GameUuid { get; set; }
        [JsonProperty("rating", NullValueHandling = NullValueHandling.Ignore)] public BgsRating Rating { get; set; }
        [JsonProperty("anomaly", NullValueHandling = NullValueHandling.Ignore)] public BgsCardRef Anomaly { get; set; }
    }
    public class BgsRating { [JsonProperty("mmr")] public int? Mmr { get; set; } [JsonProperty("duosMmr")] public int? DuosMmr { get; set; } }

    public class BgsCardRef
    {
        [JsonProperty("cardId")] public string CardId { get; set; }
        [JsonProperty("dbfId", NullValueHandling = NullValueHandling.Ignore)] public int? DbfId { get; set; }
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string Name { get; set; }
        [JsonProperty("text", NullValueHandling = NullValueHandling.Ignore)] public string Text { get; set; }
        [JsonProperty("cost", NullValueHandling = NullValueHandling.Ignore)] public int? Cost { get; set; }
    }

    public class BgsHero : BgsCardRef
    {
        [JsonProperty("health", NullValueHandling = NullValueHandling.Ignore)] public int? Health { get; set; }
        [JsonProperty("armor", NullValueHandling = NullValueHandling.Ignore)] public int? Armor { get; set; }
    }

    public class BgsPlayer
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("tier")] public int Tier { get; set; }
        [JsonProperty("hero", NullValueHandling = NullValueHandling.Ignore)] public BgsHero Hero { get; set; }
        [JsonProperty("heroPower", NullValueHandling = NullValueHandling.Ignore)] public BgsCardRef HeroPower { get; set; }
        [JsonProperty("trinkets")] public List<BgsTrinket> Trinkets { get; set; } = new List<BgsTrinket>();
        [JsonProperty("questReward")] public BgsQuestReward QuestReward { get; set; }
        [JsonProperty("board")] public List<BgsMinion> Board { get; set; } = new List<BgsMinion>();
    }
    public class BgsTrinket : BgsCardRef { [JsonProperty("slot")] public string Slot { get; set; } }
    public class BgsQuestReward : BgsCardRef
    {
        [JsonProperty("progress", NullValueHandling = NullValueHandling.Ignore)] public int? Progress { get; set; }
        [JsonProperty("total", NullValueHandling = NullValueHandling.Ignore)] public int? Total { get; set; }
    }

    public class BgsMinion
    {
        [JsonProperty("zonePosition", NullValueHandling = NullValueHandling.Ignore)] public int? ZonePosition { get; set; }
        [JsonProperty("slot", NullValueHandling = NullValueHandling.Ignore)] public int? Slot { get; set; }
        [JsonProperty("cardId")] public string CardId { get; set; }
        [JsonProperty("dbfId", NullValueHandling = NullValueHandling.Ignore)] public int? DbfId { get; set; }
        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string Name { get; set; }
        [JsonProperty("attack", NullValueHandling = NullValueHandling.Ignore)] public int? Attack { get; set; }
        [JsonProperty("health", NullValueHandling = NullValueHandling.Ignore)] public int? Health { get; set; }
        [JsonProperty("golden", NullValueHandling = NullValueHandling.Ignore)] public bool? Golden { get; set; }
        [JsonProperty("keywords", NullValueHandling = NullValueHandling.Ignore)] public List<string> Keywords { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
    }

    public class BgsShop
    {
        [JsonProperty("available")] public bool Available { get; set; }
        [JsonProperty("tier")] public int Tier { get; set; }
        [JsonProperty("frozen")] public bool? Frozen { get; set; }
        [JsonProperty("offers")] public List<BgsMinion> Offers { get; set; } = new List<BgsMinion>();
    }

    public class BgsLastOpponent
    {
        [JsonProperty("turn")] public int Turn { get; set; }
        [JsonProperty("hero", NullValueHandling = NullValueHandling.Ignore)] public BgsCardRef Hero { get; set; }
        [JsonProperty("board")] public List<BgsMinion> Board { get; set; } = new List<BgsMinion>();
    }

    public class BgsLobby
    {
        [JsonProperty("players")] public List<BgsLobbyPlayer> Players { get; set; } = new List<BgsLobbyPlayer>();
    }
    public class BgsLobbyPlayer
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("heroCardId")] public string HeroCardId { get; set; }
        [JsonProperty("accountId")] public string AccountId { get; set; }
    }
}
