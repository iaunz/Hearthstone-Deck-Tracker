using System;
using System.IO;
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
using Hearthstone_Deck_Tracker.API;
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
        private ShopChangedDebouncer<string> _shopDeb;
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
            _shopDeb = new ShopChangedDebouncer<string>(_cfg.ShopChangedQuietMs, _clock);
            // ShopChangedDebouncer.OnEmit fires once per settled shop state.
            // We re-capture the shop-only view at emit time (cheap; one
            // GetOpponentBoardState read) and build the C1 webhook data as a
            // proper JSON OBJECT (anonymous {shop, turn, phase}) rather than
            // a double-encoded JSON string.
            _shopDeb.OnEmit += payload => Emit(BridgeEventType.ShopChanged, ShopData());

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

                // Shop debouncer: poll the shop only while in a BGs shopping phase.
                // I3: CaptureShopOnly() skips lobby/races/rating/board/lastOpponent
                // (the heavy reads) — only GetOpponentBoardState + turn/phase.
                // The debouncer's payload string is a cheap fingerprint for
                // change detection; the actual webhook DTO is rebuilt at emit
                // time (see ShopData) so C1 gets a clean projected BgsShop.
                if (g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase && !g.IsInMenu)
                {
                    var fp = ShopFingerprint();
                    if (fp != null) _shopDeb.Update(fp, _clock.NowMs);
                }
                _shopDeb.Tick();
            }
            catch (Exception ex) { Logger.Error("OnUpdate: " + ex.Message); }
        }

        void OnGameStart()
        {
            // M9: a new match re-arms the MatchEnd guard.
            _matchEnded = false;
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

        // Cheap fingerprint for ShopChangedDebouncer's change detection. The
        // debouncer only compares string equality, so a joined cardId list +
        // tier is enough to detect "shop contents changed". Returns null when
        // no shop is currently on screen (debouncer treats null as "no update").
        string ShopFingerprint()
        {
            try
            {
                var v = _source.CaptureShopOnly();
                if (v?.Shop?.Offers == null || v.Shop.Offers.Count == 0) return null;
                var ids = new System.Text.StringBuilder();
                ids.Append(v.Turn).Append(':').Append(v.Shop.Tier).Append(':');
                foreach (var e in v.Shop.Offers) ids.Append(e.CardId ?? "").Append(',');
                return ids.ToString();
            }
            catch { return null; }
        }

        // C1 + I3: build the ShopChanged webhook data as a proper JSON OBJECT
        // {shop, turn, phase} where shop is a *projected* BgsShop DTO (clean
        // cardId/attack/health/keywords), NOT the raw ShopView (which holds
        // Entity objects with tag dicts). CaptureShopOnly() reads only the
        // shop slice at emit time; Project(_, false) maps it to BgsShop.
        object ShopData()
        {
            try
            {
                var v = _source.CaptureShopOnly();
                if (v?.Shop == null) return new {};
                // Minimal view for projection: only Shop + InMatch + IsBattlegrounds
                // matter for snap.Shop. includeText=false keeps the payload small.
                var view = new GameStateView
                {
                    Shop = v.Shop,
                    InMatch = true,
                    IsBattlegrounds = true,
                    Turn = v.Turn,
                    Phase = v.Phase
                };
                var snap = _projector.Project(view, false);
                return new { shop = snap.Shop, turn = v.Turn, phase = v.Phase };
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
            try { _shopDeb?.Flush(); } catch (Exception ex) { Logger.Error("shop flush: " + ex.Message); }
            try { _webhook?.Stop(3000); } catch (Exception ex) { Logger.Error("webhook stop: " + ex.Message); }
            try { _webhook?.Dispose(); } catch { /* Dispose may double-throw after Stop; ignore */ }
            try { _http?.Stop(); } catch (Exception ex) { Logger.Error("http stop: " + ex.Message); }

            // NO GameEvents.OnGameStart.Clear() here — see class remarks.
            // HDT removes our handlers automatically when this plugin is disabled.

            _http = null;
            _webhook = null;
            _shopDeb = null;
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
