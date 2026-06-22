using System;
using System.Collections.Generic;
using System.Linq;
using BgsDataBridge.Core;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Hearthstone;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Projector
{
    /// <summary>
    /// Integration-layer adapter: snapshots the relevant slices of the live
    /// HDT game state (<see cref="Hearthstone_Deck_Tracker.API.Core.Game"/> + HearthMirror memory) into a
    /// <see cref="GameStateView"/>. Every HDT read is wrapped in
    /// <see cref="Safe{T}"/> so a single failing read only nulls that field,
    /// not the whole capture. All <see cref="Entity"/> instances stored on the
    /// returned view are Clone()d so the projector never mutates live state.
    /// This is the ONLY place downstream of the GameStateView seam that touches
    /// Hearthstone_Deck_Tracker.API.Core.Game; it is therefore verified by compilation + manual runtime, not
    /// unit tests.
    /// </summary>
    public class HdtGameSource : IGameSource
    {
        // #3: at match end the player's real hero leaves PLAY and only a
        // placeholder (TB_BaconShop_HERO_PH) remains in the entity list; the
        // PLAYER_TECH_LEVEL tag is gone too, so tier reads 0. Cache the last
        // good (in-PLAY, non-placeholder) hero + tier seen during the match and
        // fall back to them so MatchEnd's snapshot keeps the real hero/tier.
        // ResetMatchCache() is called by the plugin on OnGameStart so a previous
        // match's hero cannot leak into the next match's hero-pick snapshot.
        private Entity _lastGoodHero;
        private int _lastGoodTier;
        // TB_BaconShop_HERO_PH is the post-match placeholder hero entity (name
        // "BaconPHhero"); the old fallback grabbed it because it is
        // IsControlledBy(pid) && IsHero. Treat it as "not a real hero".
        const string PlaceholderHeroCardId = "TB_BaconShop_HERO_PH";

        public void ResetMatchCache()
        {
            _lastGoodHero = null;
            _lastGoodTier = 0;
        }

        public GameStateView Capture()
        {
            var v = new GameStateView { InMatch = false };
            try
            {
                var g = Hearthstone_Deck_Tracker.API.Core.Game;
                v.InMatch = !g.IsInMenu;
                v.IsBattlegrounds = g.IsBattlegroundsMatch;
                v.IsDuos = g.IsBattlegroundsDuosMatch;
                v.Spectator = g.Spectator;
                v.Turn = g.GetTurnNumber();
                v.Phase = DerivePhase(g);

                // #3: cache tier; fall back to the cached value when live reads 0
                // mid-match (match-end teardown), so the final snapshot keeps it.
                var tier = ReadTechLevel(g);
                if (tier > 0) _lastGoodTier = tier;
                else if (v.InMatch && _lastGoodTier > 0) tier = _lastGoodTier;
                v.Tier = tier;

                // I1: take ONE snapshot of the live Entities dictionary up
                // front. Player.Board / Player.Trinkets / Player.QuestRewards
                // / Player.Hero / g.Entities.Values are all lazy IEnumerable
                // over _game.Entities.Values (a Dictionary mutated ~10Hz by
                // the log thread). Enumerating them live throws
                // InvalidOperationException on a concurrent mutation, which
                // the old outer catch turned into a near-empty capture.
                // CaptureEntitiesSnapshot retries once on that race.
                var entities = CaptureEntitiesSnapshot(g);
                // Player/Opponent.Id are scalar (cheap, non-enumerating), so
                // safe to read directly. Wrap in try/catch defensively in
                // case the underlying state is mid-teardown.
                int? playerId = null;
                try { playerId = g.Player.Id; } catch { }
                var playerName = Safe(() => g.Player.Name);
                // Coalesce to -1 (no entity is ever controlled by id -1) so
                // the LINQ filters below stay null-safe and compile against
                // IsControlledBy(int).
                int pid = playerId ?? -1;

                v.PlayerName = playerName;

                // I1: derive every per-player collection from the SNAPSHOT
                // list filtered by controller+zone+kind — never via the live
                // player.Board / player.Trinkets enumerables.
                v.PlayerBoard = Safe(() => ZoneExtractor.Board(entities, pid)) ?? new List<Entity>();
                v.PlayerHand = Safe(() => ZoneExtractor.Hand(entities, pid)) ?? new List<Entity>();

                // #3: prefer the real hero in PLAY (and cache it). At match end
                // the hero leaves PLAY and the old fallback grabbed the placeholder
                // (TB_BaconShop_HERO_PH); instead fall back to the cached hero so
                // MatchEnd keeps the real identity. Cache empty → original
                // any-controlled-hero fallback (covers very early capture).
                var liveHeroInPlay = Safe(() => entities
                    .FirstOrDefault(x => x.IsControlledBy(pid) && x.IsInPlay && x.IsHero)?.Clone());
                Entity hero;
                if(liveHeroInPlay != null && !IsPlaceholderHero(liveHeroInPlay))
                {
                    hero = liveHeroInPlay;
                    _lastGoodHero = hero.Clone();
                }
                else if(_lastGoodHero != null)
                {
                    hero = _lastGoodHero.Clone();
                }
                else
                {
                    hero = liveHeroInPlay
                        ?? Safe(() => entities.FirstOrDefault(x => x.IsControlledBy(pid) && x.IsHero)?.Clone());
                }
                v.Hero = hero;

                v.HeroPower = Safe(() => entities
                    .FirstOrDefault(x => x.IsHeroPower && x.IsControlledBy(pid))?.Clone());

                // #5: sort trinkets by ZONE_POSITION so the Projector's positional
                // slot (1, 2, ...) maps to board order (1 = lesser, 2 = greater).
                var trinkets = Safe(() => entities
                    .Where(x => x.IsControlledBy(pid) && x.IsInPlay && x.IsBattlegroundsTrinket)
                    .OrderBy(x => x.GetTag(GameTag.ZONE_POSITION))
                    .Select(x => x.Clone()).ToList());
                v.Trinkets = trinkets ?? new List<Entity>();

                var qr = Safe(() => entities
                    .FirstOrDefault(x => x.IsControlledBy(pid) && x.IsInPlay && x.IsBgsQuestReward));
                if (qr != null)
                    v.QuestReward = qr.Clone();

                // Shop: only outside the combat phase, when the shop is on screen.
                v.Shop = Safe(CaptureShop);

                // Previous opponent board snapshot (last combat).
                v.LastOpponent = Safe(CaptureLastOpponent);

                // Lobby roster.
                var lobby = Safe(CaptureLobby);
                v.Lobby = lobby ?? new List<LobbyPlayerView>();

                // Available minion races (public API; the underlying memory read is private).
                var races = Safe(() => BattlegroundsUtils.GetAvailableRaces()
                    ?.Select(r => r.ToString()).ToList());
                v.AvailableRaces = races ?? new List<string>();

                v.Mmr = SafeValue(() => g.BattlegroundsRatingInfo?.Rating);
                v.DuosMmr = SafeValue(() => g.BattlegroundsRatingInfo?.DuosRating);

                // 畸变(anomaly):读游戏实体的 BACON_GLOBAL_ANOMALY_DBID tag,转 cardId。
                // DTO/View 字段已存在,此处补捕获(此前为死字段)。全决策类型受益。
                var anomalyDbf = SafeValue(() => BattlegroundsUtils.GetBattlegroundsAnomalyDbfId(g.GameEntity));
                if (anomalyDbf.HasValue)
                {
                    var anomalyCardId = Safe(() => Database.GetCardFromDbfId(anomalyDbf.Value, false)?.Id);
                    if (!string.IsNullOrEmpty(anomalyCardId))
                        v.Anomaly = new Entity { CardId = anomalyCardId };
                }
            }
            catch
            {
                // I2: do NOT reset v to empty — keep whatever fields were
                // captured so far, and flag the snapshot as Partial so
                // consumers (HTTP /state, webhook) can distrust it.
                v.Partial = true;
            }
            return v;
        }

        /// <summary>
        /// 热路径（OnUpdate 10Hz）：一次实体快照 → 玩家棋盘+手牌+tier。
        /// 不调 HearthMirror（商店走 watcher 链路）；实体均 Clone，可跨 tick 持有。
        /// 与 Capture() 共享 ZoneExtractor，但跳过 shop/lobby/lastOpponent 等重读取。
        /// </summary>
        public PlayerZonesView CapturePlayerZones()
        {
            var v = new PlayerZonesView();
            try
            {
                var g = Hearthstone_Deck_Tracker.API.Core.Game;
                var entities = CaptureEntitiesSnapshot(g);
                int? playerId = null;
                try { playerId = g.Player.Id; } catch { }
                int pid = playerId ?? -1;
                v.Board = ZoneExtractor.Board(entities, pid);
                v.Hand = ZoneExtractor.Hand(entities, pid);
                v.Tier = ReadTechLevel(g);
            }
            catch { /* 保持空 view；OnUpdate 的调用方容错 */ }
            return v;
        }

        /// <summary>
        /// I1: snapshot the live Entities dictionary into a local list with
        /// one retry on the concurrent-mutation race. The retry is bounded
        /// (single re-attempt); persistent failure is allowed to propagate to
        /// Capture()'s outer catch (which flags Partial rather than reset).
        /// </summary>
        static List<Entity> CaptureEntitiesSnapshot(GameV2 g)
        {
            try { return g.Entities.Values.ToList(); }
            catch (InvalidOperationException)
            {
                // One retry — the log thread mutates ~10Hz, so a second
                // snapshot taken immediately after usually wins the race.
                return g.Entities.Values.ToList();
            }
        }

        static string DerivePhase(GameV2 g)
        {
            // M5: constructed / mercenary / other matches would otherwise fall
            // through to the HeroPick/Combat/Shop ladder and report "Shop" or
            // "HeroPick" for non-BGs modes. Short-circuit to a sane phase.
            if (!g.IsBattlegroundsMatch)
                return g.IsInMenu ? "None" : "Other";
            if (g.IsInMenu)
                return "None";
            // 英雄选择相位：用 STEP 硬门（游戏实体 tag，单调），避免玩家实体瞬时
            // 缺失导致旧的 IsBattlegroundsHeroPickingDone 误判（问题 #2）。
            if (HeroPickPhase.IsActive(g.IsBattlegroundsMatch, g.IsInMenu,
                    g.GameEntity?.GetTag(GameTag.STEP) ?? int.MaxValue,
                    (int)Step.BEGIN_MULLIGAN))
                return "HeroPick";
            if (g.IsBattlegroundsCombatPhase)
                return "Combat";
            return "Shop";
        }

        ShopView CaptureShop()
        {
            var g = Hearthstone_Deck_Tracker.API.Core.Game;
            if (!g.IsBattlegroundsMatch || g.IsBattlegroundsCombatPhase)
                return null;
            var obs = HearthMirror.Reflection.Client.GetOpponentBoardState();
            // #4: an empty BoardCards list means the shop frame is up but cards
            // haven't been dealt yet (typical at the instant of the Combat→Shop
            // edge that drives ShopPhaseStart). Returning null yields an honest
            // shop:null ("unknown / not yet dealt") instead of a misleading
            // offers:[] ("shop is empty"). A ShopChanged event follows once the
            // watcher sees the dealt cards.
            if (obs?.BoardCards == null || obs.BoardCards.Count == 0)
                return null;
            // Tier = 玩家当前酒馆等级；Frozen：HearthMirror 暂未暴露商店冻结状态，保持 null（spec §3.2）。
            var sv = new ShopView { Tier = ReadTechLevel(g), Frozen = null };
            foreach (var bc in obs.BoardCards)
            {
                var e = new Entity { CardId = bc.CardId };
                sv.Offers.Add(e);
            }
            return sv;
        }

        static bool IsPlaceholderHero(Entity e) => e != null && e.CardId == PlaceholderHeroCardId;

        LastOpponentView CaptureLastOpponent()
        {
            var g = Hearthstone_Deck_Tracker.API.Core.Game;
            var oppHero = g.Opponent.Hero;
            if (oppHero == null)
                return null;
            var snap = g.GetBattlegroundsBoardStateFor(oppHero.Id);
            if (snap?.Entities == null)
                return null;
            var lo = new LastOpponentView { Turn = snap.Turn, Hero = new Entity { CardId = oppHero.CardId } };
            foreach (var e in snap.Entities)
                lo.Board.Add(e.Clone());
            return lo;
        }

        List<LobbyPlayerView> CaptureLobby()
        {
            var li = Hearthstone_Deck_Tracker.API.Core.Game.MetaData.BattlegroundsLobbyInfo;
            if (li?.Players == null)
                return null;
            var list = new List<LobbyPlayerView>();
            foreach (var p in li.Players)
                list.Add(new LobbyPlayerView
                {
                    Name = p.Name,
                    HeroCardId = p.HeroCardId,
                    // Deterministic encoding from Hi/Lo (HearthMirror AccountId has no ToString override).
                    AccountId = p.AccountId != null ? $"{p.AccountId.Hi}_{p.AccountId.Lo}" : null
                });
            return list;
        }

        static T Safe<T>(Func<T> f) where T : class
        {
            try { return f(); }
            catch { return null; }
        }

        static T? SafeValue<T>(Func<T?> f) where T : struct
        {
            try { return f(); }
            catch { return null; }
        }

        // 酒馆等级（tech level 1-6）：取玩家英雄实体的 PLAYER_TECH_LEVEL tag
        // （HDT BobsBuddy 同款读法，BobsBuddyInvoker.cs:428）。g.Player.Hero 与
        // g.Opponent.Hero 对称（后者已在本类 CaptureLastOpponent 使用）。
        public static int ReadTechLevel(GameV2 g)
            => SafeValue(() => g.Player.Hero?.GetTag(GameTag.PLAYER_TECH_LEVEL)) ?? 0;

    }
}
