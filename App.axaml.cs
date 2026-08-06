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
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;

namespace OptiscalerClient
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);

            // Ensure trace and console output go to Terminal
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(Console.Out));

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                try
                {
                    var crashLogPath = System.IO.Path.Combine(Services.AppPaths.GetAppDataRoot(), "crash.log");
                    System.IO.File.WriteAllText(crashLogPath, args.ExceptionObject.ToString());
                }
                catch
                {
                    // Last-resort handler — nothing we can do if writing fails
                }
            };
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow = new Views.MainWindow();
            }

            base.OnFrameworkInitializationCompleted();
        }

        public static string AppVersion { get; } =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

        public static string CurrentLanguage = "en";

        public static void ChangeLanguage(string langCode)
        {
            try
            {
                var uri = new Uri($"avares://OptiscalerClient/Languages/Strings.{langCode}.axaml");
                var include = new ResourceInclude(new Uri("avares://OptiscalerClient/App.axaml"))
                {
                    Source = uri
                };

                var dictionaries = Application.Current?.Resources.MergedDictionaries;
                if (dictionaries != null)
                {
                    // In App.axaml, we only have one MergedDictionary at index 0 which is Strings.*.axaml
                    if (dictionaries.Count > 0)
                    {
                        var oldDict = dictionaries[0];
                        dictionaries.Remove(oldDict);
                    }
                    dictionaries.Insert(0, include);
                }
                CurrentLanguage = langCode;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[App] Failed to change language to '{langCode}': {ex.Message}");
            }
        }
    }
}