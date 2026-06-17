using System;
using System.Windows;
using BgsDataBridge.Config;

namespace BgsDataBridge.Settings
{
    /// <summary>
    /// WPF settings window for the plugin. Edits a working copy of the
    /// <see cref="BridgeConfig"/> in place; on Save, persists it and hands it
    /// back to <c>BgsBridgePlugin.ReloadConfig</c> for a hot reload of HTTP +
    /// webhook dispatcher. Verified by compilation (UseWPF=true in csproj);
    /// runtime behavior is exercised manually at Task 12.
    /// </summary>
    public partial class SettingsWindow : Window
    {
        private readonly BridgeConfig _cfg;
        private readonly Action<BridgeConfig> _onSave;

        public SettingsWindow(BridgeConfig cfg, Action<BridgeConfig> onSave)
        {
            InitializeComponent();
            _cfg = cfg;
            _onSave = onSave;
            CbEnabled.IsChecked = cfg.Enabled;
            TbPort.Text = cfg.Port.ToString();
            TbWebhooks.Text = string.Join(Environment.NewLine,
                cfg.Webhooks.ConvertAll(w => w.Url ?? ""));
        }

        void OnSave(object sender, RoutedEventArgs e)
        {
            _cfg.Enabled = CbEnabled.IsChecked ?? true;
            if (int.TryParse(TbPort.Text, out var p)) _cfg.Port = p;

            _cfg.Webhooks.Clear();
            var lines = TbWebhooks.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var url = line.Trim();
                if (url.Length > 0)
                    _cfg.Webhooks.Add(new WebhookConfig { Url = url });
            }

            try { _onSave(_cfg); }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Reload failed: " + ex.Message, "BgsDataBridge",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Close();
        }
    }
}
