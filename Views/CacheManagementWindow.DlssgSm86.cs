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
using Avalonia.Layout;
using Avalonia.Media;
using OptiscalerClient.Services;

namespace OptiscalerClient.Views
{
    /// <summary>"DLSS FG (RTX 20/30)" page: cached dlssg_for_sm86 builds (one card per mod version and
    /// runtime build) with their size and a Delete button. List + delete only — builds are downloaded
    /// by the Manage Game window on install, and verified again before every use.</summary>
    public partial class CacheManagementWindow
    {
        private readonly DlssgSm86PackageService _dlssgSm86Packages = new();

        /// <summary>Windows only (the mod is), and only with Experimental Features on, like the Manage
        /// Game section that downloads these.</summary>
        private bool IsDlssgSm86CacheSectionAvailable() =>
            PlatformServiceFactory.CreateDlssgSm86Service() != null && _componentService.Config.ShowExperimentalFeatures;

        private void RenderDlssgSm86(StackPanel content)
        {
            var cached = _dlssgSm86Packages.ListCached();
            long total = 0;
            foreach (var c in cached) total += c.SizeBytes;
            content.Children.Add(CreateTotalSizeBadge(total));

            if (cached.Count == 0)
            {
                content.Children.Add(MakeEmptyLabel(
                    Application.Current?.FindResource("TxtDlssgSm86CacheEmpty") as string ?? "No DLSS FG for RTX 20/30 builds cached."));
                return;
            }

            var runtimeFormat = Application.Current?.FindResource("TxtDlssgSm86CacheItem") as string ?? "v{0} · runtime {1}";
            foreach (var entry in cached)
            {
                var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*, Auto, Auto"), VerticalAlignment = VerticalAlignment.Center };

                var title = new TextBlock
                {
                    Text = string.Format(runtimeFormat, entry.ModVersion, entry.BuildId),
                    FontWeight = FontWeight.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = this.FindResource("BrTextPrimary") as IBrush ?? Brushes.White
                };
                grid.Children.Add(title);

                var size = CreateSizeBadge(entry.SizeBytes);
                Grid.SetColumn(size, 1);
                grid.Children.Add(size);

                var btnDelete = new Button
                {
                    Content = Application.Current?.FindResource("TxtDeletePlain") as string ?? "Delete",
                    Padding = new Thickness(12, 4),
                    FontSize = 11,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                btnDelete.Classes.Add("BtnSecondary");
                var (modVersion, buildId) = (entry.ModVersion, entry.BuildId);
                btnDelete.Click += (_, _) =>
                {
                    _dlssgSm86Packages.DeleteCached(modVersion, buildId);
                    ShowSection("dlssgsm86");
                };
                Grid.SetColumn(btnDelete, 2);
                grid.Children.Add(btnDelete);

                content.Children.Add(new Border
                {
                    Background = this.FindResource("BrBgCard") as IBrush ?? Brushes.Transparent,
                    BorderBrush = this.FindResource("BrBorderSubtle") as IBrush ?? Brushes.DimGray,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 10),
                    Child = grid
                });
            }
        }
    }
}
