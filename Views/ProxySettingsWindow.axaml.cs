// OptiScaler Client - A frontend for managing OptiScaler installations
// Copyright (C) 2026 Agustín Montaña (Agustinm28)
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using OptiscalerClient.Helpers;
using OptiscalerClient.Models;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views
{
    public partial class ProxySettingsWindow : Window, IGamepadInputHost
    {
        private readonly ComponentManagementService _componentService;
        private bool _isLoading;
        private GamepadDialogNavigationHelper? _gamepadHelper;

        GamepadHelperBase? IGamepadInputHost.GamepadHelper => _gamepadHelper;

        public ProxySettingsWindow()
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentService = new ComponentManagementService();
        }

        public ProxySettingsWindow(Window owner, ComponentManagementService componentService)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);
            _componentService = componentService;

            this.Opacity = 0;

            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null)
                titleBar.PointerPressed += (s, e) => this.BeginMoveDrag(e);

            this.Opened += (s, e) =>
            {
                this.Opacity = 1;
                var rootPanel = this.FindControl<Panel>("RootPanel");
                if (rootPanel != null)
                {
                    AnimationHelper.SetupPanelTransition(rootPanel);
                    rootPanel.Opacity = 1;
                }

                if (_gamepadHelper == null)
                {
                    _gamepadHelper = new GamepadDialogNavigationHelper(this, null);
                }

                if (owner is IGamepadInputHost host)
                    host.GamepadHelper?.SuspendInput();
            };

            this.Closed += (s, e) =>
            {
                if (owner is IGamepadInputHost closedHost)
                    closedHost.GamepadHelper?.ResumeInput();

                _gamepadHelper?.Dispose();
                _gamepadHelper = null;
            };

            LoadCurrentSettings();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void LoadCurrentSettings()
        {
            var net = _componentService.Config.Network;
            _isLoading = true;

            var tgl = this.FindControl<ToggleSwitch>("TglUseSystemProxy");
            if (tgl != null) tgl.IsChecked = net.UseSystemProxy;

            var cmb = this.FindControl<ComboBox>("CmbProxyType");
            if (cmb != null)
            {
                for (int i = 0; i < cmb.Items.Count; i++)
                {
                    if ((cmb.Items[i] as ComboBoxItem)?.Tag?.ToString() == net.ProxyType)
                    { cmb.SelectedIndex = i; break; }
                }
                if (cmb.SelectedIndex < 0) cmb.SelectedIndex = 0;
            }

            var txtHost = this.FindControl<TextBox>("TxtProxyHost");
            if (txtHost != null) txtHost.Text = net.ProxyHost ?? string.Empty;

            var txtPort = this.FindControl<TextBox>("TxtProxyPort");
            if (txtPort != null) txtPort.Text = net.ProxyPort?.ToString() ?? string.Empty;

            var chk = this.FindControl<CheckBox>("ChkProxyRequiresAuth");
            if (chk != null) chk.IsChecked = net.ProxyRequiresAuth;

            var txtUser = this.FindControl<TextBox>("TxtProxyUsername");
            if (txtUser != null) txtUser.Text = net.ProxyUsername ?? string.Empty;

            var txtPass = this.FindControl<TextBox>("TxtProxyPassword");
            if (txtPass != null) txtPass.Text = net.ProxyPassword ?? string.Empty;

            _isLoading = false;
            UpdateExplicitProxyVisibility(!net.UseSystemProxy);
            UpdateCredentialsVisibility(net.ProxyRequiresAuth);
        }

        private void UpdateExplicitProxyVisibility(bool enabled)
        {
            var pnl = this.FindControl<StackPanel>("PnlExplicitProxy");
            if (pnl != null) pnl.IsEnabled = enabled;
        }

        private void UpdateCredentialsVisibility(bool enabled)
        {
            var pnl = this.FindControl<StackPanel>("PnlCredentials");
            if (pnl != null) pnl.IsEnabled = enabled;
        }

        private void SaveAndReconfigure()
        {
            _componentService.SaveConfiguration();
            NetworkService.Configure(_componentService.Config.Network);
        }

        // ── Event handlers ────────────────────────────────────────────────────────

        private void TglUseSystemProxy_IsCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is not ToggleSwitch tgl) return;
            _componentService.Config.Network.UseSystemProxy = tgl.IsChecked ?? true;
            UpdateExplicitProxyVisibility(!(tgl.IsChecked ?? true));
            SaveAndReconfigure();
        }

        private void CmbProxyType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is not ComboBox cmb) return;
            var tag = (cmb.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "HTTPS";
            _componentService.Config.Network.ProxyType = tag;
            SaveAndReconfigure();
        }

        private void TxtProxyHost_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            var value = (tb.Text ?? string.Empty).Trim();
            _componentService.Config.Network.ProxyHost = string.IsNullOrEmpty(value) ? null : value;
            SaveAndReconfigure();
        }

        private void TxtProxyPort_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            var text = (tb.Text ?? string.Empty).Trim();
            _componentService.Config.Network.ProxyPort = int.TryParse(text, out var port) ? port : null;
            SaveAndReconfigure();
        }

        private void ChkProxyRequiresAuth_IsCheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_isLoading) return;
            if (sender is not CheckBox chk) return;
            _componentService.Config.Network.ProxyRequiresAuth = chk.IsChecked ?? false;
            UpdateCredentialsVisibility(chk.IsChecked ?? false);
            SaveAndReconfigure();
        }

        private void TxtProxyUsername_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            var value = (tb.Text ?? string.Empty).Trim();
            _componentService.Config.Network.ProxyUsername = string.IsNullOrEmpty(value) ? null : value;
            SaveAndReconfigure();
        }

        private void TxtProxyPassword_LostFocus(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox tb) return;
            var value = (tb.Text ?? string.Empty).Trim();
            _componentService.Config.Network.ProxyPassword = string.IsNullOrEmpty(value) ? null : value;
            SaveAndReconfigure();
        }

        private async void BtnTestProxyConnection_Click(object sender, RoutedEventArgs e)
        {
            var resultTxt = this.FindControl<TextBlock>("TxtProxyTestResult");
            if (resultTxt != null)
                resultTxt.Text = GetResourceString("TxtNetworkTestTesting", "Testing…");

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await NetworkService.GetHttpClient()
                    .GetAsync("https://api.github.com/", cts.Token);

                if (resultTxt != null)
                    resultTxt.Text = response.IsSuccessStatusCode
                        ? $"{GetResourceString("TxtNetworkTestOk", "✓ Connected")} (HTTP {(int)response.StatusCode})"
                        : $"{GetResourceString("TxtNetworkTestFail", "✗ Failed")} (HTTP {(int)response.StatusCode})";
            }
            catch (Exception ex)
            {
                if (resultTxt != null)
                    resultTxt.Text = $"{GetResourceString("TxtNetworkTestFail", "✗ Failed")}: {ex.Message}";
            }
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private string GetResourceString(string key, string fallback)
        {
            if (Avalonia.Application.Current?.TryFindResource(key, out var res) == true && res is string s)
                return s;
            return fallback;
        }
    }
}
