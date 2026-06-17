using System.Collections.Generic;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Projector
{
    public static class KeywordMap
    {
        // NOTE: cleave has NO GameTag in this build; HDT derives it from a
        // card-ID allowlist (MinionFactory.cardIDsWithCleave, consumed at
        // BobsBuddyUtils.cs:44). Do NOT add a CLEAVE tag here. Re-add a tag
        // only if a future lib/HearthDb.dll surfaces one. VENOMOUS and
        // POISONOUS are both real BG keywords (HDT: BobsBuddyUtils.cs:46,
        // BattlegroundsUtils.cs:118).
        public static readonly GameTag[] Mapped = {
            GameTag.TAUNT, GameTag.DIVINE_SHIELD, GameTag.POISONOUS, GameTag.VENOMOUS,
            GameTag.REBORN, GameTag.WINDFURY, GameTag.STEALTH, GameTag.FROZEN
        };

        public static List<string> From(Entity e)
        {
            var list = new List<string>();
            foreach (var t in Mapped)
                if (e.HasTag(t)) list.Add(t.ToString());
            return list;
        }
    }
}
