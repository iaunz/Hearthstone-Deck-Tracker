using System;
using System.Collections.Generic;
using BgsDataBridge.Dtos;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Projector
{
    public class GameStateProjector
    {
        public BgsSnapshot Project(GameStateView v, bool includeText)
        {
            var snap = new BgsSnapshot
            {
                Locale = "enUS", // HdtGameSource 可覆写为 Config.Instance 选定语言
                CapturedAt = DateTimeOffset.UtcNow.ToString("o"),
                InMatch = v.InMatch,
                Partial = v.Partial,
                Match = new BgsMatch
                {
                    GameType = v.IsBattlegrounds ? (v.IsDuos ? "BattlegroundsDuos" : "BattlegroundsSolo") : "Other",
                    IsBattlegrounds = v.IsBattlegrounds, IsDuos = v.IsDuos, Spectator = v.Spectator,
                    Phase = v.Phase ?? "None", Turn = v.Turn, GameUuid = v.GameUuid,
                    Rating = (v.Mmr.HasValue || v.DuosMmr.HasValue) ? new BgsRating { Mmr = v.Mmr, DuosMmr = v.DuosMmr } : null,
                    // 畸变:cardId+name+text(text 始终输出,描述规则改动,对 AI 决策关键)。
                    Anomaly = v.Anomaly != null ? ToCard(v.Anomaly, true) : null
                },
                AvailableRaces = v.AvailableRaces ?? new List<string>(),
                Player = ProjectPlayer(v, includeText),
                Shop = ProjectShop(v.Shop, includeText),
                LastOpponent = v.LastOpponent != null ? new BgsLastOpponent { Turn = v.LastOpponent.Turn,
                    Hero = v.LastOpponent.Hero != null ? new BgsCardRef { CardId = v.LastOpponent.Hero.CardId, Name = NameOf(v.LastOpponent.Hero) } : null,
                    Board = Minions(v.LastOpponent.Board, includeText) } : null,
                Lobby = v.Lobby != null && v.Lobby.Count > 0 ? new BgsLobby { Players = LobbyOf(v.Lobby) } : null
            };
            return snap;
        }

        private BgsPlayer ProjectPlayer(GameStateView v, bool includeText)
        {
            var p = new BgsPlayer { Name = v.PlayerName, Tier = v.Tier };
            if (v.Hero != null) p.Hero = ToHero(v.Hero);
            if (v.HeroPower != null) p.HeroPower = ToCard(v.HeroPower, true); // §4.1: heroPower text always emitted regardless of includeText
            // #5: trinkets arrive zone-position-sorted from HdtGameSource, so the
            // 1-based positional slot reflects board order (1 = lesser, 2 = greater).
            for(int i = 0; i < v.Trinkets.Count; i++)
                p.Trinkets.Add(ToTrinket(v.Trinkets[i], (i + 1).ToString()));
            if (v.QuestReward != null) p.QuestReward = ToQuestReward(v);
            foreach (var e in v.PlayerBoard) p.Board.Add(ToMinion(e, includeText));
            foreach (var e in v.PlayerHand) p.Hand.Add(ToMinion(e, includeText));
            return p;
        }

        public static BgsMinion ToMinion(Entity e, bool includeText)
            => new BgsMinion { CardId = e.CardId, Attack = e.Attack, Health = e.Health,
                Keywords = KeywordMap.From(e), Text = includeText ? TextOf(e) : null };

        // #1: shop offers are bare Entities (CardId only, no live tags) built
        // from HearthMirror BoardCards. Their stats/keywords come from the
        // HearthDb CARD DEFINITION (base printed values), not entity tags —
        // which is why this is a separate mapper from ToMinion (board minions
        // carry live buffs in tags). Name is populated here because consumers
        // read shop offers by card identity; Card never throws on lookup.
        static BgsMinion ToShopOffer(Entity e, bool includeText)
            => new BgsMinion { CardId = e.CardId, Name = NameOf(e),
                Attack = e.Card.Attack, Health = e.Card.Health,
                Keywords = KeywordMap.FromCard(e.Card),
                Text = includeText ? TextOf(e) : null };

        static BgsHero ToHero(Entity e) => new BgsHero { CardId = e.CardId, Name = NameOf(e),
            Health = e.Health, Armor = Tag(e, GameTag.ARMOR) };

        static BgsCardRef ToCard(Entity e, bool withText) => new BgsCardRef { CardId = e.CardId,
            Name = NameOf(e), Text = withText ? TextOf(e) : null };

        static BgsTrinket ToTrinket(Entity e, string slot) => new BgsTrinket
            { CardId = e.CardId, Name = NameOf(e), Text = TextOf(e), Slot = slot };

        static BgsQuestReward ToQuestReward(GameStateView v) => new BgsQuestReward
        { CardId = v.QuestReward.CardId, Name = NameOf(v.QuestReward), Text = TextOf(v.QuestReward),
          Progress = v.QuestProgress, Total = v.QuestTotal };

        List<BgsMinion> Minions(List<Entity> es, bool includeText)
        {
            var list = new List<BgsMinion>(es.Count);
            foreach (var e in es) list.Add(ToMinion(e, includeText));
            return list;
        }

        public List<BgsMinion> ProjectZone(List<Entity> es, bool includeText) => Minions(es, includeText);

        // #1: shop offers use the card-definition mapper (ToShopOffer), NOT the
        // board's live-tag mapper (Minions/ToMinion).
        List<BgsMinion> ShopOffers(List<Entity> es, bool includeText)
        {
            var list = new List<BgsMinion>(es.Count);
            foreach (var e in es) list.Add(ToShopOffer(e, includeText));
            return list;
        }

        public BgsShop ProjectShop(ShopView shop, bool includeText)
            => shop != null ? new BgsShop { Available = true, Tier = shop.Tier,
                Frozen = shop.Frozen, Offers = ShopOffers(shop.Offers, includeText) } : null;
        static List<BgsLobbyPlayer> LobbyOf(List<LobbyPlayerView> src)
        {
            var list = new List<BgsLobbyPlayer>(src.Count);
            foreach (var p in src) list.Add(new BgsLobbyPlayer { Name = p.Name, HeroCardId = p.HeroCardId, AccountId = p.AccountId });
            return list;
        }

        // ---- HDT 卡牌文本解析（属性名以 HDT 源为准）----
        static string NameOf(Entity e)
        { try { return e.Card?.Name; } catch { return null; } }
        static string TextOf(Entity e)
        { try { return e.Card?.Text; } catch { return null; } }
        static int? Tag(Entity e, GameTag t) { var x = e.GetTag(t); return x > 0 ? x : (int?)null; }
    }
}
