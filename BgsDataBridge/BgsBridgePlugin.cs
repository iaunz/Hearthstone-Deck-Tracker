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
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Plugins;
using Newtonsoft.Json;

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
        private ShopChangedDebouncer _shopDeb;
        private SystemClock _clock;
        private HttpSender _sender;
        private HdtGameSource _source;
        private long _seq;
        private bool _loaded;

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
            _shopDeb = new ShopChangedDebouncer(_cfg.ShopChangedQuietMs, _clock);
            // ShopChangedDebouncer.OnEmit is a ShopEmitted(string) event; the
            // emitted payload is already a JSON string, passed straight through.
            _shopDeb.OnEmit += payload => Emit(BridgeEventType.ShopChanged, payload);

            var projector = new GameStateProjector();
            var routes = new RouteDispatcher(_source, projector);
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
                    HeroPickActive = !g.IsBattlegroundsHeroPickingDone,
                    // TrinketPickActive deferred (spec §11 open item #2): real
                    // detection would hook ChoicesWatcher; v1 leaves it false.
                    TrinketPickActive = false
                };
                foreach (var ev in _sm.Observe(input))
                {
                    // ShopChanged is owned by the debouncer; ignore any SM emission.
                    if (ev.Type == BridgeEventType.ShopChanged) continue;
                    Emit(ev.Type, null);
                }

                // Shop debouncer: poll the shop only while in a BGs shopping phase.
                if (g.IsBattlegroundsMatch && !g.IsBattlegroundsCombatPhase && !g.IsInMenu)
                {
                    var shop = ShopPayload();
                    if (shop != null) _shopDeb.Update(shop, _clock.NowMs);
                }
                _shopDeb.Tick();
            }
            catch (Exception ex) { Logger.Error("OnUpdate: " + ex.Message); }
        }

        void OnGameStart() => Emit(BridgeEventType.MatchStart, null);
        void OnGameEnd() => Emit(BridgeEventType.MatchEnd, null);

        void Emit(BridgeEventType type, string dataPayload)
        {
            try
            {
                var env = new EventEnvelope
                {
                    Seq = Interlocked.Increment(ref _seq),
                    Event = type,
                    At = DateTimeOffset.UtcNow.ToString("o"),
                    Match = MatchPayload(),
                    Data = dataPayload ?? "{}"
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

        string ShopPayload()
        {
            try
            {
                var v = _source.Capture();
                if (v?.Shop == null) return null;
                return JsonConvert.SerializeObject(new { shop = v.Shop, turn = v.Turn, phase = v.Phase });
            }
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
