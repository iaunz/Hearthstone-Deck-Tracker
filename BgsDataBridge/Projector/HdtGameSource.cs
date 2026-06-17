using System;
using System.Collections.Generic;
using System.Linq;
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

                var player = g.Player;
                v.PlayerName = player.Name;

                var board = Safe(() => player.Board.Where(x => x.IsMinion).Select(x => x.Clone()).ToList());
                v.PlayerBoard = board ?? new List<Entity>();

                v.Hero = Safe(() => player.Hero?.Clone());

                v.HeroPower = Safe(() => g.Entities.Values.FirstOrDefault(
                    x => x.IsHeroPower && x.IsControlledBy(player.Id))?.Clone());

                var trinkets = Safe(() => player.Trinkets.Select(x => x.Clone()).ToList());
                v.Trinkets = trinkets ?? new List<Entity>();

                var qr = Safe(() => player.QuestRewards.FirstOrDefault());
                if(qr != null)
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
            }
            catch
            {
                // Integration layer: any single failure is non-fatal; return the partial view.
            }
            return v;
        }

        static string DerivePhase(GameV2 g)
        {
            if(g.IsInMenu)
                return "None";
            // Hero-pick is not done yet -> still in the picking phase.
            if(g.IsBattlegroundsHeroPickingDone == false)
                return "HeroPick";
            if(g.IsBattlegroundsCombatPhase)
                return "Combat";
            return "Shop";
        }

        ShopView CaptureShop()
        {
            var g = Hearthstone_Deck_Tracker.API.Core.Game;
            if(!g.IsBattlegroundsMatch || g.IsBattlegroundsCombatPhase)
                return null;
            var obs = HearthMirror.Reflection.Client.GetOpponentBoardState();
            if(obs?.BoardCards == null)
                return null;
            var sv = new ShopView { Tier = 0, Frozen = null };
            foreach(var bc in obs.BoardCards)
            {
                var e = new Entity { CardId = bc.CardId };
                sv.Offers.Add(e);
            }
            return sv;
        }

        LastOpponentView CaptureLastOpponent()
        {
            var g = Hearthstone_Deck_Tracker.API.Core.Game;
            var oppHero = g.Opponent.Hero;
            if(oppHero == null)
                return null;
            var snap = g.GetBattlegroundsBoardStateFor(oppHero.Id);
            if(snap?.Entities == null)
                return null;
            var lo = new LastOpponentView { Turn = snap.Turn, Hero = new Entity { CardId = oppHero.CardId } };
            foreach(var e in snap.Entities)
                lo.Board.Add(e.Clone());
            return lo;
        }

        List<LobbyPlayerView> CaptureLobby()
        {
            var li = Hearthstone_Deck_Tracker.API.Core.Game.MetaData.BattlegroundsLobbyInfo;
            if(li?.Players == null)
                return null;
            var list = new List<LobbyPlayerView>();
            foreach(var p in li.Players)
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
    }
}
