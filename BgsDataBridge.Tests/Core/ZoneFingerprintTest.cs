using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using BgsDataBridge.Core;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class ZoneFingerprintTest
    {
        static Entity M(string id, int zonePos, int atk, int hp)
        {
            var e = new Entity { CardId = id };
            e.SetTag(GameTag.ZONE_POSITION, zonePos);
            e.SetTag(GameTag.ATK, atk);
            e.SetTag(GameTag.HEALTH, hp);
            return e;
        }

        [TestMethod]
        public void Board_Stable_When_Unchanged()
        {
            var b = new List<Entity> { M("A", 1, 2, 3) };
            Assert.AreEqual(ZoneFingerprint.Board(b), ZoneFingerprint.Board(new List<Entity> { M("A", 1, 2, 3) }));
        }

        [TestMethod]
        public void Board_Changes_On_Reposition()
        {
            var a = new List<Entity> { M("A", 1, 2, 2), M("B", 2, 3, 3) };
            var b = new List<Entity> { M("A", 2, 2, 2), M("B", 1, 3, 3) };  // 交换位置
            Assert.AreNotEqual(ZoneFingerprint.Board(a), ZoneFingerprint.Board(b));
        }

        [TestMethod]
        public void Board_Changes_On_Buff()
        {
            var before = new List<Entity> { M("A", 1, 2, 2) };
            var after  = new List<Entity> { M("A", 1, 5, 5) };  // 攻血 buff
            Assert.AreNotEqual(ZoneFingerprint.Board(before), ZoneFingerprint.Board(after));
        }

        [TestMethod]
        public void Board_Changes_On_Keyword()
        {
            var plain = M("A", 1, 2, 2);
            var shielded = M("A", 1, 2, 2);
            shielded.SetTag(GameTag.DIVINE_SHIELD, 1);
            Assert.AreNotEqual(
                ZoneFingerprint.Board(new List<Entity> { plain }),
                ZoneFingerprint.Board(new List<Entity> { shielded }));
        }

        [TestMethod]
        public void Board_Changes_On_Add_Remove()
        {
            var one = new List<Entity> { M("A", 1, 1, 1) };
            var two = new List<Entity> { M("A", 1, 1, 1), M("B", 2, 1, 1) };
            Assert.AreNotEqual(ZoneFingerprint.Board(one), ZoneFingerprint.Board(two));
        }

        [TestMethod]
        public void Hand_Order_And_ZonePos_Invariant()
        {
            // 手牌忽略顺序与 zonePosition
            var a = new List<Entity> { M("A", 1, 1, 1), M("B", 2, 2, 2) };
            var b = new List<Entity> { M("B", 2, 2, 2), M("A", 1, 1, 1) };
            Assert.AreEqual(ZoneFingerprint.Hand(a), ZoneFingerprint.Hand(b));
        }

        [TestMethod]
        public void Hand_Changes_On_Composition()
        {
            var a = new List<Entity> { M("A", 1, 1, 1) };
            var b = new List<Entity> { M("A", 1, 1, 1), M("B", 1, 1, 1) };
            Assert.AreNotEqual(ZoneFingerprint.Hand(a), ZoneFingerprint.Hand(b));
        }

        [TestMethod]
        public void Null_Returns_Empty()
        {
            Assert.AreEqual("", ZoneFingerprint.Board(null));
            Assert.AreEqual("", ZoneFingerprint.Hand(null));
        }
    }
}
