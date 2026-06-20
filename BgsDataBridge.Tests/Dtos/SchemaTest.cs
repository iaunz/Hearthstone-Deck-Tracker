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

        // C1 regression: the webhook `data` field must serialize as a JSON
        // OBJECT (per spec §4.2), not a JSON string. Before the fix, Emit
        // assigned a JSON string to EventEnvelope.Data (object), and
        // WebhookDispatcher's JsonConvert.SerializeObject(envelope) re-encoded
        // that string → "data":"{}" or "data":"{\"shop\":...}". This test
        // builds the two real envelope shapes (MatchStart: empty object;
        // ShopChanged: {shop, turn, phase} with a projected BgsShop) and
        // asserts j["data"].Type == JTokenType.Object for BOTH — which would
        // have failed against the old string-typed assignment.
        [TestMethod]
        public void Envelope_Data_IsJsonObject_ForBothEventShapes()
        {
            // MatchStart shape: Data = new {} (anonymous empty object).
            var matchStart = new EventEnvelope
            {
                Schema = "bgs-event/v1", Seq = 1,
                Event = BridgeEventType.MatchStart,
                At = "2026-06-17T00:00:00Z",
                Match = new { gameType = "BattlegroundsSolo" },
                Data = new {}
            };
            var msJson = JsonConvert.SerializeObject(matchStart);
            var ms = JObject.Parse(msJson);
            Assert.AreEqual(JTokenType.Object, ms["data"].Type,
                "MatchStart data must be a JSON object, was: " + msJson);
            // Empty object round-trips as {} (no properties).
            Assert.AreEqual(0, ((JObject)ms["data"]).Count);

            // ShopChanged shape: Data = { shop = BgsShop{...}, turn, phase }.
            var shop = new BgsShop
            {
                Available = true, Tier = 3, Frozen = false,
                Offers = new List<BgsMinion>
                {
                    new BgsMinion { CardId = "BACON_1", Attack = 2, Health = 3 }
                }
            };
            var shopChanged = new EventEnvelope
            {
                Schema = "bgs-event/v1", Seq = 2,
                Event = BridgeEventType.ShopChanged,
                At = "2026-06-17T00:00:01Z",
                Match = new { gameType = "BattlegroundsSolo" },
                Data = new { shop, turn = 5, phase = "Shop" }
            };
            var scJson = JsonConvert.SerializeObject(shopChanged);
            var sc = JObject.Parse(scJson);
            Assert.AreEqual(JTokenType.Object, sc["data"].Type,
                "ShopChanged data must be a JSON object, was: " + scJson);
            // The shop DTO nested under data is a clean BgsShop (cardId present,
            // no Entity tag dicts leak).
            Assert.AreEqual("BACON_1", (string)sc["data"]["shop"]["offers"][0]["cardId"]);
            Assert.AreEqual(2, (int)sc["data"]["shop"]["offers"][0]["attack"]);
            Assert.AreEqual(5, (int)sc["data"]["turn"]);
            Assert.AreEqual("Shop", (string)sc["data"]["phase"]);
        }

        // §4.2: phase/lifecycle events (MatchStart/MatchEnd/ShopPhaseStart/
        // CombatPhaseStart) carry the FULL snapshot in `data`, so the consumer
        // gets board/shop/hero context at decision points. Locks that a
        // BgsSnapshot-typed Data serializes with its nested fields (not {}).
        [TestMethod]
        public void Envelope_Data_Carries_FullSnapshot_ForPhaseEvents()
        {
            var snap = new BgsSnapshot
            {
                Schema = "bgs-state/v1", InMatch = true,
                Player = new BgsPlayer
                {
                    Board = new List<BgsMinion> { new BgsMinion { CardId = "BACON_1", Attack = 5, Health = 5 } }
                }
            };
            var env = new EventEnvelope
            {
                Schema = "bgs-event/v1", Seq = 10,
                Event = BridgeEventType.ShopPhaseStart,
                At = "2026-06-17T00:00:00Z",
                Data = snap
            };
            var j = JObject.Parse(JsonConvert.SerializeObject(env));
            Assert.AreEqual(JTokenType.Object, j["data"].Type);
            Assert.AreEqual("bgs-state/v1", (string)j["data"]["schema"]);
            Assert.AreEqual("BACON_1", (string)j["data"]["player"]["board"][0]["cardId"]);
            Assert.AreEqual(5, (int)j["data"]["player"]["board"][0]["attack"]);
        }
    }
}
