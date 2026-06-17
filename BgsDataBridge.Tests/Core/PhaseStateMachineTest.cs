using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using BgsDataBridge.Core;
using BgsDataBridge.Events;

namespace BgsDataBridge.Tests.Core
{
    [TestClass]
    public class PhaseStateMachineTest
    {
        static TriggerInput Bg(bool combat, bool heroPick = false, bool trinketPick = false, bool inMenu = false)
            => new TriggerInput { IsBattlegroundsMatch = true, IsInMenu = inMenu, IsCombatPhase = combat,
               HeroPickActive = heroPick, TrinketPickActive = trinketPick };

        [TestMethod]
        public void ShopToCombat_Emits_CombatPhaseStart()
        {
            var sm = new PhaseStateMachine();
            CollectionAssert.AreEqual(new TriggerEvent[0], sm.Observe(Bg(false)).ToList());
            var ev = sm.Observe(Bg(true)).Single();
            Assert.AreEqual(BridgeEventType.CombatPhaseStart, ev.Type);
        }

        [TestMethod]
        public void CombatToShop_Emits_ShopPhaseStart()
        {
            var sm = new PhaseStateMachine();
            sm.Observe(Bg(true));
            var ev = sm.Observe(Bg(false)).Single();
            Assert.AreEqual(BridgeEventType.ShopPhaseStart, ev.Type);
        }

        [TestMethod]
        public void No_Emit_When_Unchanged()
        {
            var sm = new PhaseStateMachine();
            sm.Observe(Bg(true));
            Assert.AreEqual(0, sm.Observe(Bg(true)).Count);
        }

        [TestMethod]
        public void Gate_NonBattlegrounds_NoEmit()
        {
            var sm = new PhaseStateMachine();
            var nonBg = new TriggerInput { IsBattlegroundsMatch = false, IsCombatPhase = true };
            Assert.AreEqual(0, sm.Observe(nonBg).Count);
            Assert.AreEqual(0, sm.Observe(new TriggerInput { IsBattlegroundsMatch = false }).Count);
        }

        [TestMethod]
        public void Gate_InMenu_NoEmit()
        {
            var sm = new PhaseStateMachine();
            Assert.AreEqual(0, sm.Observe(Bg(true, inMenu: true)).Count);
        }

        [TestMethod]
        public void HeroPick_Edge_Emits()
        {
            var sm = new PhaseStateMachine();
            sm.Observe(Bg(false));
            var ev = sm.Observe(Bg(false, heroPick: true)).Single();
            Assert.AreEqual(BridgeEventType.HeroPick, ev.Type);
        }

        [TestMethod]
        public void TrinketPick_Edge_Emits()
        {
            var sm = new PhaseStateMachine();
            sm.Observe(Bg(false));
            var ev = sm.Observe(Bg(false, trinketPick: true)).Single();
            Assert.AreEqual(BridgeEventType.TrinketPick, ev.Type);
        }
    }
}
