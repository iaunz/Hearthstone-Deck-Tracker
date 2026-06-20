using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using BgsDataBridge.Projector;

namespace BgsDataBridge.Tests.Projector
{
    [TestClass]
    public class ProjectorTrinketTest
    {
        // #5: HDT exposes no reliable lesser/greater tag on trinket entities in
        // v1 (only a boolean BACON_TRINKET; BACON_FIRST/SECOND_TRINKET_DATABASE_ID
        // live on the game entity and are unused/unverified across HDT). We
        // assign a POSITIONAL slot by board order — HdtGameSource sorts trinkets
        // by ZONE_POSITION before projection, so slot "1" is the first (lesser)
        // and "2" the second (greater) trinket. Strictly better than the previous
        // always-null slot: consumers at least learn the ordering.
        [TestMethod]
        public void Trinkets_Get_Positional_Slot_By_Order()
        {
            var view = new GameStateView
            {
                InMatch = true, IsBattlegrounds = true, Phase = "Shop", Turn = 7,
                Trinkets = new List<Entity>
                {
                    new Entity { CardId = "BG30_MagicItem_705" },
                    new Entity { CardId = "BG30_MagicItem_993" }
                }
            };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.AreEqual(2, snap.Player.Trinkets.Count);
            Assert.AreEqual("BG30_MagicItem_705", snap.Player.Trinkets[0].CardId);
            Assert.AreEqual("1", snap.Player.Trinkets[0].Slot);
            Assert.AreEqual("2", snap.Player.Trinkets[1].Slot);
        }

        [TestMethod]
        public void No_Trinkets_Yields_Empty_List()
        {
            var view = new GameStateView { InMatch = true, IsBattlegrounds = true };
            var snap = new GameStateProjector().Project(view, includeText: false);
            Assert.AreEqual(0, snap.Player.Trinkets.Count);
        }
    }
}
