using System.Collections.Generic;
using System.Linq;
using BgsDataBridge.Projector;           // KeywordMap
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Core
{
    // 棋盘/手牌稳定指纹：游戏线程 10Hz 轮询 diff 用。纯函数、只读已 Clone 的 Entity。
    // 棋盘：有序，含 zonePosition（挪位即变）+ atk/hp/keywords（buff 即变）—— spec §5.3 全量粒度。
    // 手牌：无序（位置无战术意义），不含 zonePosition。
    public static class ZoneFingerprint
    {
        public static string Board(List<Entity> orderedBoard)
            => orderedBoard == null ? "" : string.Join("|", orderedBoard.Select(BoardBeast));

        public static string Hand(List<Entity> hand)
        {
            if (hand == null) return "";
            var parts = hand.Select(HandBeast).OrderBy(p => p).ToList();
            return string.Join("|", parts);
        }

        static string BoardBeast(Entity e)
        {
            if (e == null) return "::0:0:";
            return $"{e.CardId ?? ""}:{e.GetTag(GameTag.ZONE_POSITION)}:{e.Attack}:{e.Health}:{string.Join(";", KeywordMap.From(e))}";
        }

        static string HandBeast(Entity e)
        {
            if (e == null) return ":0:0:";
            return $"{e.CardId ?? ""}:{e.Attack}:{e.Health}:{string.Join(";", KeywordMap.From(e))}";
        }
    }
}
