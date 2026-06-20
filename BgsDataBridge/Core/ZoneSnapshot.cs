using System.Collections.Generic;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Core
{
    // 去抖器 pending 载荷：Update 时刻冻结的 zone 实体（已 Clone）+ turn/phase。
    // emit 时不再重捕获（复刻 ShopChanged 修复 offers 丢失的同款铁律）。
    public class ZoneSnapshot
    {
        public List<Entity> Zone;
        public int Turn;
        public string Phase;
    }
}
