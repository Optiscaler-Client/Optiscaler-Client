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
using System;
using OptiscalerClient.Services;

namespace OptiscalerClient;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Plain file I/O, safe to run before Avalonia initializes — see RenderingSafetyNet for
        // why this can't be done via a normal exception handler instead.
        bool forceSoftwareRendering = OperatingSystem.IsWindows() && RenderingSafetyNet.ShouldForceSoftwareRendering();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RenderingSafetyNet.MarkCleanShutdown();

        BuildAvaloniaApp(forceSoftwareRendering)
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp(bool forceSoftwareRendering = false)
    {
        var appBuilder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        if (forceSoftwareRendering && OperatingSystem.IsWindows())
        {
            appBuilder = appBuilder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software }
            });
        }

        return appBuilder;
    }
}
