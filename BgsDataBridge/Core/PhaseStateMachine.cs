using System.Collections.Generic;
using BgsDataBridge.Events;

namespace BgsDataBridge.Core
{
    // 边沿检测：仅在游戏线程采样输入；门控非 BGS/菜单。
    public class PhaseStateMachine
    {
        private bool _first = true;
        private bool _prevCombat;
        private bool _prevHeroPick;
        private bool _prevTrinketPick;

        public IReadOnlyList<TriggerEvent> Observe(TriggerInput i)
        {
            var outp = new List<TriggerEvent>(2);
            if (_first)
            {
                _first = false;
                _prevCombat = i.IsCombatPhase;
                _prevHeroPick = i.HeroPickActive;
                _prevTrinketPick = i.TrinketPickActive;
                return outp; // 首帧只记基线，不触发
            }

            bool active = i.IsBattlegroundsMatch && !i.IsInMenu;
            if (active)
            {
                if (i.IsCombatPhase != _prevCombat)
                    outp.Add(new TriggerEvent(i.IsCombatPhase ? BridgeEventType.CombatPhaseStart : BridgeEventType.ShopPhaseStart));
                if (i.HeroPickActive && !_prevHeroPick)
                    outp.Add(new TriggerEvent(BridgeEventType.HeroPick));
                if (i.TrinketPickActive && !_prevTrinketPick)
                    outp.Add(new TriggerEvent(BridgeEventType.TrinketPick));
            }

            _prevCombat = i.IsCombatPhase;
            _prevHeroPick = i.HeroPickActive;
            _prevTrinketPick = i.TrinketPickActive;
            return outp;
        }
    }
}
