using System.Collections.Generic;
using System.Linq;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Core
{
    // 从一次实体快照中抽取玩家棋盘/手牌（纯函数，可单测）。
    // 棋盘按 ZONE_POSITION 升序（修 Capture 未排序问题，保证指纹位置稳定 + 快照可读）。
    // 手牌含随从 + 战棋法术，顺序无关（指纹内部再排序）。
    // 返回的实体均 Clone()——可安全跨 tick 持有（去抖 pending 载荷）。
    public static class ZoneExtractor
    {
        public static List<Entity> Board(IList<Entity> entities, int pid)
            => entities.Where(x => x.IsControlledBy(pid) && x.IsInPlay && x.IsMinion)
                       .OrderBy(x => x.GetTag(GameTag.ZONE_POSITION))
                       .Select(x => x.Clone())
                       .ToList();

        public static List<Entity> Hand(IList<Entity> entities, int pid)
            => entities.Where(x => x.IsControlledBy(pid) && x.IsInHand
                                  && (x.IsMinion || x.IsBattlegroundsSpell))
                       .Select(x => x.Clone())
                       .ToList();
    }
}
