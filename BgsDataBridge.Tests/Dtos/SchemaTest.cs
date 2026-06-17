using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Dtos;
using BgsDataBridge.Events;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BgsDataBridge.Tests.Dtos
{
    [TestClass]
    public class SchemaTest
    {
        [TestMethod]
        public void Snapshot_RoundTrip_PreservesAllFields()
        {
            var snap = new BgsSnapshot
            {
                Schema = "bgs-state/v1", Locale = "enUS", InMatch = true, Partial = false,
                Match = new BgsMatch { GameType = "BattlegroundsSolo", IsBattlegrounds = true,
                    IsDuos = false, Spectator = false, Phase = "Shop", Turn = 5 },
                AvailableRaces = new List<string> { "MURLOC", "DEMON" },
                Player = new BgsPlayer { Name = "me", Tier = 4,
                    Board = new List<BgsMinion> { new BgsMinion { CardId = "BACON_1", Attack = 8, Health = 8 } } },
                Shop = new BgsShop { Available = true, Offers = new List<BgsMinion>() },
                LastOpponent = null, Lobby = null
            };
            var json = JsonConvert.SerializeObject(snap);
            var j = JObject.Parse(json);
            Assert.AreEqual("bgs-state/v1", j["schema"]);
            Assert.AreEqual("Shop", j["match"]["phase"]);
            Assert.AreEqual(8, (int)j["player"]["board"][0]["health"]);
            Assert.AreEqual(JTokenType.Null, j["lastOpponent"].Type);
            // questReward and board[].text must be present-as-null (not omitted)
            // per the design contract — distinguishes "known empty" from "unknown".
            Assert.AreEqual(JTokenType.Null, j["player"]["questReward"].Type);
            Assert.AreEqual(JTokenType.Null, j["player"]["board"][0]["text"].Type);
        }

        [TestMethod]
        public void Envelope_RoundTrip_HasSeqAndEvent()
        {
            var env = new EventEnvelope
            {
                Schema = "bgs-event/v1", Seq = 137, Event = BridgeEventType.ShopPhaseStart
            };
            var j = JObject.Parse(JsonConvert.SerializeObject(env));
            Assert.AreEqual(137, (int)j["seq"]);
            Assert.AreEqual("ShopPhaseStart", j["event"]);
        }
    }
}
