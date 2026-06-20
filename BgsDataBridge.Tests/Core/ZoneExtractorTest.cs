using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class ZoneExtractorTest
    {
        const int Me = 1;
        const int Foe = 2;

        static Entity Card(string id, int controller, Zone zone, CardType type, int zonePos = 0)
        {
            var e = new Entity { CardId = id };
            e.SetTag(GameTag.CONTROLLER, controller);
            e.SetTag(GameTag.ZONE, (int)zone);
            e.SetTag(GameTag.CARDTYPE, (int)type);
            e.SetTag(GameTag.ZONE_POSITION, zonePos);
            return e;
        }

        [TestMethod]
        public void Board_Filters_To_Player_Minions_In_Play_Sorted_By_ZonePos()
        {
            var entities = new List<Entity>
            {
                Card("B", Me, Zone.PLAY, CardType.MINION, zonePos: 2),
                Card("A", Me, Zone.PLAY, CardType.MINION, zonePos: 1),
                Card("FOE", Foe, Zone.PLAY, CardType.MINION, zonePos: 1),   // 对手：排除
                Card("HAND", Me, Zone.HAND, CardType.MINION, zonePos: 1),   // 手牌：排除
                Card("HERO", Me, Zone.PLAY, CardType.HERO, zonePos: 0)      // 英雄：排除
            };
            var board = ZoneExtractor.Board(entities, Me);
            CollectionAssert.AreEqual(new[] { "A", "B" }, board.Select(x => x.CardId).ToList());
        }

        [TestMethod]
        public void Hand_Includes_Minions_And_BgsSpells()
        {
            var entities = new List<Entity>
            {
                Card("MIN", Me, Zone.HAND, CardType.MINION),
                Card("SPL", Me, Zone.HAND, CardType.BATTLEGROUND_SPELL),
                Card("PLAY", Me, Zone.PLAY, CardType.MINION)   // 棋盘：排除
            };
            var hand = ZoneExtractor.Hand(entities, Me);
            CollectionAssert.AreEquivalent(new[] { "MIN", "SPL" }, hand.Select(x => x.CardId).ToList());
        }

        [TestMethod]
        public void Returns_Clones_Not_Live_References()
        {
            var live = Card("A", Me, Zone.PLAY, CardType.MINION, zonePos: 1);
            var board = ZoneExtractor.Board(new List<Entity> { live }, Me);
            Assert.AreNotSame(live, board[0]);                 // 是克隆
            Assert.AreEqual("A", board[0].CardId);             // 内容保留
            live.SetTag(GameTag.ATK, 99);                       // 改原对象
            Assert.AreEqual(0, board[0].Attack);                // 克隆不受影响
        }

        [TestMethod]
        public void Empty_When_No_Match()
        {
            Assert.AreEqual(0, ZoneExtractor.Board(new List<Entity> { Card("X", Foe, Zone.PLAY, CardType.MINION) }, Me).Count);
            Assert.AreEqual(0, ZoneExtractor.Hand(new List<Entity>(), Me).Count);
        }
    }
}
