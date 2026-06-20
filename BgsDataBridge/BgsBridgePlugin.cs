using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Controls;
using BgsDataBridge.Config;
using BgsDataBridge.Core;
using BgsDataBridge.Events;
using BgsDataBridge.Http;
using BgsDataBridge.Projector;
using BgsDataBridge.Settings;
using BgsDataBridge.Webhook;
using HearthDb.Enums;
using HearthMirror.Objects;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Hearthstone_Deck_Tracker.Plugins;

namespace BgsDataBridge
{
    /// <summary>
    /// HDT plugin entry point. Assembles every prior component:
    /// <see cref="BridgeHttpServer"/> + <see cref="RouteDispatcher"/> +
    /// <see cref="WebhookDispatcher"/> on <see cref="OnLoad"/>; runs
    /// <see cref="PhaseStateMachine.Observe"/> + <see cref="ShopChangedDebouncer"/>
    /// on <see cref="OnUpdate"/> (10Hz, game thread, non-blocking);
    /// tears it all down on <see cref="OnUnload"/>.
    ///
    /// Lifecycle invariants (enforced so <see cref="ReloadConfig"/>'s
    /// OnUnload-then-OnLoad hot reload is safe):
    ///   - OnLoad is idempotent: if already loaded, it OnUnload()s first.
    ///   - OnUnload is idempotent: every tear-down step is null-guarded and
    ///     wrapped, so OnUnload on an unloaded plugin is a no-op.
    ///   - We do NOT call GameEvents.OnGameStart.Clear() (or any .Clear()).
    ///     HDT's ActionList attributes handlers to the calling plugin via
    ///     stack-trace and removes a disabled plugin's handlers on the next
    ///     fire (see Hearthstone Deck Tracker/API/ActionList.cs). Clear() would
    ///     wipe every other plugin's subscriptions too.
    /// Verified by compilation + manual HDT load at Task 12; no unit tests
    /// cover the plugin lifecycle.
    /// </summary>
    public class BgsBridgePlugin : IPlugin
    {
        private string _dir;
        private BridgeConfig _cfg;
        private BridgeHttpServer _http;
        private WebhookDispatcher _webhook;
        private readonly PhaseStateMachine _sm = new PhaseStateMachine();
        private ShopChangedDebouncer<ShopSnapshot> _shopDeb;
        private ShopChangedDebouncer<ZoneSnapshot> _boardDeb;
        private ShopChangedDebouncer<ZoneSnapshot> _handDeb;
        private readonly TierEdgeTracker _tierEdge = new TierEdgeTracker();
        private string _lastBoardFp;
        private string _lastHandFp;
        // watcher 后台线程写、游戏线程读；单一 volatile 字段，持有最近一次商店快照引用。
        // NOTE: OpponentBoardStateWatcher 按 EntityId+Hovered+MousedOverSlot 去重后才 fire
        // （见 HearthWatcher/EventArgs/OpponentBoardArgs.Equals），故纯悬停变更也会移交一个新
        // list 引用；但我们的指纹只看 turn:tier:cardIds、忽略 hover → 不会触发多余 Update。
        private volatile System.Collections.Generic.List<BoardCard> _lastShopCards;
        private string _lastShopFp;
        private SystemClock _clock;
        private HttpSender _sender;
        private HdtGameSource _source;
        private long _seq;
        private bool _loaded;
        // M9: MatchEnd historically fired twice per match — once from
        // OnGameEnd (GameEvents.OnGameEnd) and once more from whichever of
        // OnGameWon/Lost/Tied fires (all three were wired to OnGameEnd in
        // OnLoad). The _matchEnded guard makes the second emit within the
        // same match a no-op; it is reset in OnGameStart.
        private bool _matchEnded;
        // #2: HDT's ActionList exposes Add() only — no Remove/Clear — so a
        // ReloadConfig (OnUnload+OnLoad) or disable→enable cycle leaves STALE
        // OnGameStart/OnGameEnd handlers subscribed alongside the fresh ones.
        // The _matchEnded guard masks the duplicate OnGameEnd, but MatchStart
        // had no such guard and double-fired (observed in the receiver log).
        // _matchStarted is the symmetric guard: only the first OnGameStart per
        // match emits MatchStart; reset in OnGameEnd (and OnLoad).
        private bool _matchStarted;
        private readonly GameStateProjector _projector = new GameStateProjector();

        public string Name => "BgsDataBridge";
        public string Description => "Exposes Battlegrounds state over HTTP + webhooks for local consumers.";
        public string ButtonText => "Open status";
        public string Author => "BgsDataBridge";
        public Version Version => new Version(0, 1, 0, 0);
        public MenuItem MenuItem { get; private set; }

        public void OnLoad()
        {
            // Idempotent: a second OnLoad (e.g. from ReloadConfig) tears down first.
            if (_loaded) OnUnload();

            // Config.AppDataPath lives in namespace Hearthstone_Deck_Tracker
            // (class Hearthstone_Deck_Tracker.Config), NOT Hearthstone_Deck_Tracker.Utility.
            // Verified: Hearthstone Deck Tracker/Config.cs:17,23.
            _dir = Path.Combine(Hearthstone_Deck_Tracker.Config.AppDataPath, "Plugins", "BgsDataBridge");
            Logger.Init(_dir);
            LoadConfig();

            _clock = new SystemClock();
            _source = new HdtGameSource();
            // M9: re-arm MatchEnd guard on (re)load so a mid-match reload
            // doesn't permanently suppress the next MatchEnd.
            _matchEnded = false;
            // #2: re-arm MatchStart guard on (re)load for the same reason.
            _matchStarted = false;
            _shopDeb = new ShopChangedDebouncer<ShopSnapshot>(_cfg.ShopChangedQuietMs, _clock);
            // ShopChangedDebouncer.OnEmit hands us the ShopSnapshot captured at
            // Update() time. ShopData(snap) projects its ShopView to BgsShop and
            // uses snap.Turn/snap.Phase — NO re-capture at emit time. (The old
            // design re-captured via GetOpponentBoardState at emit, by which point
            // the shop was often empty/combat — the root cause of empty offers.)
            _shopDeb.OnEmit += snap => Emit(BridgeEventType.ShopChanged, ShopData(snap));
            _boardDeb = new ShopChangedDebouncer<ZoneSnapshot>(_cfg.ShopChangedQuietMs, _clock);
            _handDeb = new ShopChangedDebouncer<ZoneSnapshot>(_cfg.ShopChangedQuietMs, _clock);
            _boardDeb.OnEmit += snap => Emit(BridgeEventType.BoardChanged, BoardData(snap));
            _handDeb.OnEmit += snap => Emit(BridgeEventType.HandChanged, HandData(snap));

            // 商店事件驱动：watcher 在商店发牌/刷新/购买时触发 Change。
            // 与 GameEvents 不同，Watchers.*.Change 是普通 C# 事件，不随插件禁用自动解绑，
            // 故 OnUnload 必须手动 -=（否则禁用→启用会重复订阅、重复触发）。
            Hearthstone_Deck_Tracker.Hearthstone.Watchers.OpponentBoardStateWatcher.Change += OnShopBoardChange;

            var routes = new RouteDispatcher(_source, _projector);
            _http = new BridgeHttpServer(_cfg, routes);
            try { var port = _http.Start(); Logger.Info("HTTP on localhost:" + port); }
            catch (Exception ex) { Logger.Error("HTTP start failed: " + ex.Message); }

            _sender = new HttpSender();
            _webhook = new WebhookDispatcher(_cfg, _sender, _clock);
            _webhook.Start();

            // Match start/end: GameEvents are non-generic ActionList, so .Add(Action).
            // We rely on HDT's stack-trace plugin attribution + auto-removal on
            // disable (ActionList.cs) instead of unsubscribing in OnUnload.
            GameEvents.OnGameStart.Add(OnGameStart);
            GameEvents.OnGameEnd.Add(OnGameEnd);
            GameEvents.OnGameWon.Add(OnGameEnd);
            GameEvents.OnGameLost.Add(OnGameEnd);

            MenuItem = BuildMenu();
            _loaded = true;
            Logger.Info("loaded");
        }

        public void OnUpdate()
        {
            if (!_loaded) return;
            // Runs on the HDT game thread at ~10Hz. Keep it strictly non-blocking:
            // PhaseStateMachine.Observe is in-memory edge detection; the shop
            // capture reads HearthMirror (fast, bounded); the debouncer only
            // enqueues into the webhook BlockingCollection (TryAdd, 0 timeout).
            // Any failure is swallowed so we never stall the game thread.
            try
            {
                var g = Hearthstone_Deck_Tracker.API.Core.Game;
                // #2: safety net — if a match was abandoned without a clean
                // OnGameEnd (e.g. HS force-quit), _matchStarted would stay true
                // and silently suppress the NEXT match's MatchStart. Re-arm it
                // whenever we observe the menu. OnGameEnd remains the normal path.
                if (_matchStarted && g.IsInMenu)
                    _matchStarted = false;
                var input = new TriggerInput
                {
                    IsBattlegroundsMatch = g.IsBattlegroundsMatch,
                    IsInMenu = g.IsInMenu,
                    IsCombatPhase = g.IsBattlegroundsCombatPhase,
                    HeroPickActive = HeroPickPhase.IsActive(g.IsBattlegroundsMatch, g.IsInMenu,
                        g.GameEntity?.GetTag(GameTag.STEP) ?? int.MaxValue,
                        (int)Step.BEGIN_MULLIGAN),
                    // TrinketPickActive deferred (spec §11 open item #2): real
                    // detection would hook ChoicesWatcher; v1 leaves it false.
                    TrinketPickActive = false
                };
                foreach (var ev in _sm.Observe(input))
                {
                    // ShopChanged is owned by the debouncer; ignore any SM emission.
                    if (ev.Type == BridgeEventType.ShopChanged) continue;
                    // §4.2: ShopPhaseStart/CombatPhaseStart carry the full snapshot
                    // (the buy/sell/level + combat-prediction decision points).
                    // HeroPick/TrinketPick offered-choices deferred (need adapter capture).
                    object data = (ev.Type == BridgeEventType.ShopPhaseStart
                                   || ev.Type == BridgeEventType.CombatPhaseStart)
                        ? SnapshotData() : null;
                    Emit(ev.Type, data);
                }

                // 商店/棋盘/手牌/升本 事件驱动：均在游戏线程判定 + 去抖。
                bool inShop = g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase && !g.IsInMenu;
                int turn = g.GetTurnNumber();

                if(inShop)
                {
                    // —— 商店（保持原逻辑）——
                    var cards = _lastShopCards;
                    if(cards != null && ShopFeedPolicy.ShouldFeed(cards.Count, true))
                    {
                        var sv = new ShopView { Tier = HdtGameSource.ReadTechLevel(g), Frozen = null };
                        foreach(var bc in cards) sv.Offers.Add(new Entity { CardId = bc.CardId });
                        var shopFp = turn + ":" + sv.Tier + ":" + string.Join(",", cards.Select(c => c.CardId ?? ""));
                        if(shopFp != _lastShopFp)
                        {
                            _lastShopFp = shopFp;
                            _shopDeb.Update(new ShopSnapshot { Shop = sv, Turn = turn, Phase = "Shop" }, _clock.NowMs);
                        }
                    }

                    // —— 棋盘 / 手牌（全量指纹 diff，首帧 seed 不发，避免与 ShopPhaseStart 重复）——
                    var zones = _source.CapturePlayerZones();
                    var boardFp = ZoneFingerprint.Board(zones.Board);
                    if(_lastBoardFp == null) _lastBoardFp = boardFp;                       // seed
                    else if(boardFp != _lastBoardFp)
                    {
                        _lastBoardFp = boardFp;
                        _boardDeb.Update(new ZoneSnapshot { Zone = zones.Board, Turn = turn, Phase = "Shop" }, _clock.NowMs);
                    }
                    var handFp = ZoneFingerprint.Hand(zones.Hand);
                    if(_lastHandFp == null) _lastHandFp = handFp;                           // seed
                    else if(handFp != _lastHandFp)
                    {
                        _lastHandFp = handFp;
                        _handDeb.Update(new ZoneSnapshot { Zone = zones.Hand, Turn = turn, Phase = "Shop" }, _clock.NowMs);
                    }

                    // —— 升本（边沿，不去抖；tier 单调递增）——
                    var up = _tierEdge.Observe(zones.Tier);
                    if(up.HasValue)
                        Emit(BridgeEventType.TavernUpgraded, new { from = up.Value.Item1, to = up.Value.Item2, turn = turn, phase = "Shop" });
                }
                else
                {
                    // 非购物相：丢弃所有 pending + 指纹，杜绝战斗/菜单时的陈旧发。
                    if(_lastShopFp != null) { _shopDeb.Reset(); _lastShopFp = null; _lastShopCards = null; }
                    if(_lastBoardFp != null) { _boardDeb.Reset(); _lastBoardFp = null; }
                    if(_lastHandFp != null) { _handDeb.Reset(); _lastHandFp = null; }
                }

                _shopDeb.Tick();
                _boardDeb?.Tick();
                _handDeb?.Tick();
            }
            catch (Exception ex) { Logger.Error("OnUpdate: " + ex.Message); }
        }

        // watcher 后台线程回调：仅移交最新商店快照引用。零 Core.Game 访问、零逻辑。
        // BoardCards 每次 fire 都是新 list（HearthMirror 返回新对象），跨线程持有安全。
        void OnShopBoardChange(object sender, HearthWatcher.EventArgs.OpponentBoardArgs args)
        {
            _lastShopCards = args.BoardCards;
        }

        void OnGameStart()
        {
            // #2: dedup duplicate subscription (see _matchStarted remarks).
            if (_matchStarted) return;
            _matchStarted = true;
            // M9: a new match re-arms the MatchEnd guard.
            _matchEnded = false;
            // #3: clear the per-match hero/tier cache so the previous match's
            // hero cannot leak into this match's hero-pick snapshot.
            _source?.ResetMatchCache();
            _tierEdge.Reset();
            // §4.2: MatchStart carries the full snapshot (LLM decision context).
            Emit(BridgeEventType.MatchStart, SnapshotData());
        }

        void OnGameEnd()
        {
            // M9: GameEvents.OnGameEnd AND OnGameWon/Lost/Tied are all wired
            // here, so without a guard MatchEnd fires twice. The first call
            // emits + sets the guard; subsequent calls within the same match
            // are no-ops. OnGameStart resets it.
            if (_matchEnded) return;
            _matchEnded = true;
            // #2: re-arm the MatchStart guard for the next match.
            _matchStarted = false;
            Emit(BridgeEventType.MatchEnd, SnapshotData());
        }

        void Emit(BridgeEventType type, object data)
        {
            try
            {
                var env = new EventEnvelope
                {
                    Seq = Interlocked.Increment(ref _seq),
                    Event = type,
                    At = DateTimeOffset.UtcNow.ToString("o"),
                    Match = MatchPayload(),
                    // C1: Data is an object (anonymous or DTO). For non-shop
                    // events pass new {} → serializes as "data":{} (object,
                    // not the double-encoded "data":"{}" string the old
                    // string-typed assignment produced). For ShopChanged pass
                    // {shop, turn, phase} where shop is a projected BgsShop.
                    Data = data ?? new {}
                };
                _webhook.Enqueue(env);
            }
            catch (Exception ex) { Logger.Error("Emit: " + ex.Message); }
        }

        object MatchPayload()
        {
            try
            {
                var g = Hearthstone_Deck_Tracker.API.Core.Game;
                return new
                {
                    gameType = g.IsBattlegroundsDuosMatch ? "BattlegroundsDuos" : "BattlegroundsSolo",
                    isBattlegrounds = g.IsBattlegroundsMatch,
                    isDuos = g.IsBattlegroundsDuosMatch,
                    spectator = g.Spectator,
                    turn = g.GetTurnNumber()
                };
            }
            catch { return null; }
        }

        // 由 pending ShopSnapshot 构造 ShopChanged webhook data：投影 shop → BgsShop，
        // 直接用快照里的 turn/phase。不再 CaptureShopOnly 重捕获（那是 offers 丢失的根因）。
        object ShopData(ShopSnapshot snap)
        {
            try
            {
                if(snap?.Shop == null) return new {};
                var shop = _projector.ProjectShop(snap.Shop, false);
                return new { shop = shop, turn = snap.Turn, phase = snap.Phase };
            }
            catch { return new {}; }
        }

        // 由 pending ZoneSnapshot 构造 BoardChanged/HandChanged webhook data：
        // 投影 zone → List<BgsMinion>（复用 ToMinion，含 live tags）。emit 时不重捕获。
        object BoardData(ZoneSnapshot snap)
        {
            try
            {
                if(snap?.Zone == null) return new {};
                return new { board = _projector.ProjectZone(snap.Zone, false), turn = snap.Turn, phase = snap.Phase };
            }
            catch { return new {}; }
        }

        object HandData(ZoneSnapshot snap)
        {
            try
            {
                if(snap?.Zone == null) return new {};
                return new { hand = _projector.ProjectZone(snap.Zone, false), turn = snap.Turn, phase = snap.Phase };
            }
            catch { return new {}; }
        }

        // §4.2: MatchStart/MatchEnd/ShopPhaseStart/CombatPhaseStart carry the
        // full snapshot so the downstream consumer (LLM) gets complete decision
        // context (board + shop + hero/heroPower/trinkets/quest + lastOpponent).
        // Safe on the game thread: Capture() clones entities (I1 fix) and these
        // events are edge-triggered (once per turn/game), not at 10Hz.
        // includeText=false → minions carry keywords only (heroPower/trinket/
        // quest text always included by the projector); keeps the payload lean.
        // Returns null on failure; Emit falls back to {} (data ?? new {}).
        object SnapshotData()
        {
            try { return _projector.Project(_source.Capture(), false); }
            catch { return null; }
        }

        MenuItem BuildMenu()
        {
            var mi = new MenuItem { Header = "BgsDataBridge settings..." };
            mi.Click += (s, e) =>
            {
                try { new SettingsWindow(_cfg, ReloadConfig).Show(); }
                catch (Exception ex) { Logger.Error("settings window: " + ex.Message); }
            };
            return mi;
        }

        void LoadConfig()
        {
            var path = Path.Combine(_dir, "config.json");
            _cfg = File.Exists(path) ? BridgeConfig.Load(File.ReadAllText(path)) : new BridgeConfig();
        }

        // Hot-reload: persist the edited config, then bounce the plugin.
        // OnUnload + OnLoad are both idempotent (see class remarks), so this
        // restarts HTTP (new port) and the webhook dispatcher cleanly.
        void ReloadConfig(BridgeConfig cfg)
        {
            _cfg = cfg;
            try { File.WriteAllText(Path.Combine(_dir, "config.json"), cfg.ToJson()); }
            catch (Exception ex) { Logger.Error("config save: " + ex.Message); }
            OnUnload();
            OnLoad();
        }

        public void OnUnload()
        {
            // Idempotent + balanced: every step is null-guarded and wrapped so
            // re-entry (ReloadConfig, double-disable) is a safe no-op.
            try { Hearthstone_Deck_Tracker.Hearthstone.Watchers.OpponentBoardStateWatcher.Change -= OnShopBoardChange; } catch { /* 已解绑 */ }
            try { _shopDeb?.Flush(); } catch (Exception ex) { Logger.Error("shop flush: " + ex.Message); }
            try { _boardDeb?.Flush(); } catch (Exception ex) { Logger.Error("board flush: " + ex.Message); }
            try { _handDeb?.Flush(); } catch (Exception ex) { Logger.Error("hand flush: " + ex.Message); }
            try { _webhook?.Stop(3000); } catch (Exception ex) { Logger.Error("webhook stop: " + ex.Message); }
            try { _webhook?.Dispose(); } catch { /* Dispose may double-throw after Stop; ignore */ }
            try { _http?.Stop(); } catch (Exception ex) { Logger.Error("http stop: " + ex.Message); }

            // NO GameEvents.OnGameStart.Clear() here — see class remarks.
            // HDT removes our handlers automatically when this plugin is disabled.

            _http = null;
            _webhook = null;
            _shopDeb = null;
            _boardDeb = null;
            _handDeb = null;
            _sender = null;
            _source = null;
            _loaded = false;
            Logger.Info("unloaded");
        }

        public void OnButtonPress()
        {
            try { new SettingsWindow(_cfg ?? new BridgeConfig(), ReloadConfig).Show(); }
            catch (Exception ex) { Logger.Error("button press: " + ex.Message); }
        }
    }
}
