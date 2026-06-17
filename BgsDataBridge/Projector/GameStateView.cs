using System.Collections.Generic;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;

namespace BgsDataBridge.Projector
{
    // 由 HdtGameSource 快照填充（持已 Clone 的 Entity，线程安全）。
    public class GameStateView
    {
        public bool InMatch;
        // I2: set true by HdtGameSource.Capture()'s outer catch when any read
        // threw and the view is a best-effort partial. Projector propagates it
        // to BgsSnapshot.Partial so consumers can distrust the snapshot.
        public bool Partial;
        public bool IsBattlegrounds;
        public bool IsDuos;
        public bool Spectator;
        public string Phase;
        public int Turn;
        public string GameUuid;
        public int? Mmr;
        public int? DuosMmr;
        public Entity Anomaly;
        public List<string> AvailableRaces = new List<string>();

        public string PlayerName;
        public int Tier;
        public Entity Hero;
        public Entity HeroPower;
        public List<Entity> Trinkets = new List<Entity>();
        public Entity QuestReward; public int? QuestProgress; public int? QuestTotal;
        public List<Entity> PlayerBoard = new List<Entity>();

        public ShopView Shop;
        public LastOpponentView LastOpponent;
        public List<LobbyPlayerView> Lobby = new List<LobbyPlayerView>();
    }
    public class ShopView { public int Tier; public bool? Frozen; public List<Entity> Offers = new List<Entity>(); }
    public class LastOpponentView { public int Turn; public Entity Hero; public List<Entity> Board = new List<Entity>(); }
    public class LobbyPlayerView { public string Name; public string HeroCardId; public string AccountId; }
}
