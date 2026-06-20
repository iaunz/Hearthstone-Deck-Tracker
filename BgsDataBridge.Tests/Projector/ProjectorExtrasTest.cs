using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using BgsDataBridge.Projector;

namespace BgsDataBridge.Tests.Projector
{
    [TestClass]
    public class ProjectorExtrasTest
    {
        static Entity Mn(string id, int a, int h) { var e = new Entity { CardId = id }; e.SetTag(GameTag.ATK, a); e.SetTag(GameTag.HEALTH, h); return e; }

        [TestMethod]
        public void Shop_And_LastOpponent_And_Lobby_Projected()
        {
            var view = new GameStateView
            {
                InMatch = true, IsBattlegrounds = true, Phase = "Shop", Turn = 4,
                Shop = new ShopView { Tier = 4, Frozen = null, Offers = new List<Entity> { Mn("BACON_9", 3, 3) } },
                LastOpponent = new LastOpponentView { Turn = 3, Hero = new Entity { CardId = "HERO_X" },
                    Board = new List<Entity> { Mn("BACON_8", 8, 8) } },
                Lobby = new List<LobbyPlayerView> { new LobbyPlayerView { Name = "p1", HeroCardId = "HERO_A" } }
            };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.IsNotNull(snap.Shop);
            Assert.AreEqual("BACON_9", snap.Shop.Offers[0].CardId);
            Assert.AreEqual(3, snap.LastOpponent.Turn);
            Assert.AreEqual("HERO_X", snap.LastOpponent.Hero.CardId);
            Assert.AreEqual(8, snap.LastOpponent.Board[0].Health);
            Assert.AreEqual("p1", snap.Lobby.Players[0].Name);
        }

        [TestMethod]
        public void Null_Extras_Omitted()
        {
            var view = new GameStateView { InMatch = true, IsBattlegrounds = true };
            var snap = new GameStateProjector().Project(view, false);
            Assert.IsNull(snap.Shop);
            Assert.IsNull(snap.LastOpponent);
            Assert.IsNull(snap.Lobby);
        }

        // #1: production shop offers are BARE Entities — only CardId, no
        // ATK/HEALTH tags (built from HearthMirror BoardCards in
        // HdtGameSource.CaptureShop and BgsBridgePlugin's shop path). The shop
        // mapper must therefore read base stats from the HearthDb CARD
        // DEFINITION (e.Card.Attack/Health), not from entity tags (which are
        // always 0 on a bare entity). BG minions resolve via Cards.All
        // (BattlegroundsDb.cs also reads bacon cards from Cards.All.Values).
        [TestMethod]
        public void Shop_Offer_Stats_Read_From_Card_Definition_Not_Live_Tags()
        {
            var id = "BG26_529";
            var expected = new Card(id);
            Assert.IsTrue(expected.Attack > 0 && expected.Health > 0,
                "test setup: cardId must resolve in HearthDb (attack=" + expected.Attack + ", health=" + expected.Health + ")");

            // bare entity — no tags, mirrors the production shop-offer shape
            var offer = new Entity { CardId = id };
            var view = new GameStateView
            {
                InMatch = true, IsBattlegrounds = true, Phase = "Shop", Turn = 2,
                Shop = new ShopView { Tier = 1, Offers = new List<Entity> { offer } }
            };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.IsNotNull(snap.Shop);
            Assert.AreEqual(expected.Attack, snap.Shop.Offers[0].Attack);
            Assert.AreEqual(expected.Health, snap.Shop.Offers[0].Health);
            Assert.AreEqual(expected.Name, snap.Shop.Offers[0].Name);
        }

        // #1 regression guard: board minions carry live buffs in entity tags, so
        // they MUST keep reading e.Attack/e.Health (tags), NOT the card
        // definition. Shop offers and board minions are mapped by different
        // paths precisely because their stat sources differ.
        [TestMethod]
        public void Board_Minion_Stats_Still_Read_From_Live_Tags()
        {
            var buffed = new Entity { CardId = "BG26_529" };
            buffed.SetTag(GameTag.ATK, 99);
            buffed.SetTag(GameTag.HEALTH, 88);
            var view = new GameStateView
            {
                InMatch = true, IsBattlegrounds = true, Phase = "Combat", Turn = 5,
                PlayerBoard = new List<Entity> { buffed }
            };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.AreEqual(99, snap.Player.Board[0].Attack);
            Assert.AreEqual(88, snap.Player.Board[0].Health);
        }
    }
}
