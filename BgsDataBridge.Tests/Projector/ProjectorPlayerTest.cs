using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using BgsDataBridge.Projector;

namespace BgsDataBridge.Tests.Projector
{
    [TestClass]
    public class ProjectorPlayerTest
    {
        static Entity Minion(string id, int atk, int hp, bool taunt = false, bool goldenTag = false)
        {
            var e = new Entity { CardId = id };
            e.SetTag(GameTag.ATK, atk);
            e.SetTag(GameTag.HEALTH, hp);
            if (taunt) e.SetTag(GameTag.TAUNT, 1);
            return e;
        }

        [TestMethod]
        public void Projects_Player_Board_With_Stats_And_Keywords()
        {
            var view = new GameStateView
            {
                InMatch = true, IsBattlegrounds = true, IsDuos = false, Phase = "Combat",
                Turn = 3, PlayerBoard = new List<Entity> { Minion("BACON_1", 5, 5, true) }
            };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.AreEqual("Combat", snap.Match.Phase);
            Assert.AreEqual(3, snap.Match.Turn);
            Assert.AreEqual(1, snap.Player.Board.Count);
            Assert.AreEqual("BACON_1", snap.Player.Board[0].CardId);
            Assert.AreEqual(5, snap.Player.Board[0].Attack);
            Assert.AreEqual(5, snap.Player.Board[0].Health);
            CollectionAssert.Contains(snap.Player.Board[0].Keywords, "TAUNT");
        }

        [TestMethod]
        public void Keywords_Empty_When_None()
        {
            var view = new GameStateView { InMatch = true, IsBattlegrounds = true,
                PlayerBoard = new List<Entity> { Minion("BACON_2", 1, 1) } };
            var snap = new GameStateProjector().Project(view, false);
            Assert.AreEqual(0, snap.Player.Board[0].Keywords.Count);
        }

        [TestMethod]
        public void Keywords_Includes_Venomous_When_Tag_Set()
        {
            var e = Minion("BACON_3", 3, 3);
            e.SetTag(GameTag.VENOMOUS, 1);
            var view = new GameStateView { InMatch = true, IsBattlegrounds = true,
                PlayerBoard = new List<Entity> { e } };
            var snap = new GameStateProjector().Project(view, false);
            CollectionAssert.Contains(snap.Player.Board[0].Keywords, "VENOMOUS");
        }

        [TestMethod]
        public void Projects_Player_Hand_From_View()
        {
            var view = new GameStateView
            {
                InMatch = true, IsBattlegrounds = true,
                PlayerBoard = new List<Entity>(),
                PlayerHand = new List<Entity> { Minion("BACON_H", 2, 2) }
            };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.AreEqual(1, snap.Player.Hand.Count);
            Assert.AreEqual("BACON_H", snap.Player.Hand[0].CardId);
            Assert.AreEqual(2, snap.Player.Hand[0].Attack);
        }

        [TestMethod]
        public void ProjectZone_Maps_Entities_To_Minions()
        {
            var zone = new List<Entity> { Minion("Z1", 3, 4, true) };
            var minions = new GameStateProjector().ProjectZone(zone, includeText: false);
            Assert.AreEqual(1, minions.Count);
            Assert.AreEqual("Z1", minions[0].CardId);
            CollectionAssert.Contains(minions[0].Keywords, "TAUNT");
        }
    }
}
