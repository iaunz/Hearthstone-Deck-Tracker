using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HearthDb.Enums;
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
    }
}
