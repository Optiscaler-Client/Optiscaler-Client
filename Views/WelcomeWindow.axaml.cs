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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using OptiscalerClient.Helpers;

namespace OptiscalerClient.Views
{
    public partial class WelcomeWindow : Window
    {
        public WelcomeWindow(Window owner)
        {
            InitializeComponent();
            DialogDimHelper.Register(this);

            // Flicker-free startup: start invisible, show after positioning
            this.Opacity = 0;

            var scaling = owner.DesktopScaling;
            double dialogW = 540 * scaling;
            double dialogH = 560 * scaling;
            var x = owner.Position.X + (owner.Bounds.Width * scaling - dialogW) / 2;
            var y = owner.Position.Y + (owner.Bounds.Height * scaling - dialogH) / 2;
            this.Position = new PixelPoint((int)Math.Max(0, x), (int)Math.Max(0, y));

            var titleBar = this.FindControl<Border>("TitleBar");
            if (titleBar != null)
                titleBar.PointerPressed += (s, e) => this.BeginMoveDrag(e);

            var versionLabel = this.FindControl<TextBlock>("TxtVersionDisplay");
            if (versionLabel != null)
                versionLabel.Text = $"v{App.AppVersion}";

            this.Opened += (s, e) =>
            {
                this.Opacity = 1;
                var rootPanel = this.FindControl<Panel>("RootPanel");
                if (rootPanel != null)
                {
                    AnimationHelper.SetupPanelTransition(rootPanel);
                    rootPanel.Opacity = 1;
                }
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private bool _isAnimatingClose = false;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => _ = CloseAnimated();

        private void BtnOpenChangelog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://github.com/Optiscaler-Client/Optiscaler-Client/releases",
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private async Task CloseAnimated()
        {
            if (_isAnimatingClose) return;
            _isAnimatingClose = true;
            DialogDimHelper.HideDimNow(this);
            var rootPanel = this.FindControl<Panel>("RootPanel");
            if (rootPanel != null) rootPanel.Opacity = 0;
            await Task.Delay(220);
            Close();
        }
    }
}
