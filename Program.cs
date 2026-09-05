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
using System.Reflection;
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

        // Reads the config already loaded (and cached) by RenderingSafetyNet above, so this is
        // just a dictionary lookup, not another disk read.
        int renderFpsLimit = new ComponentManagementService().Config.RenderFpsLimit;

        BuildAvaloniaApp(forceSoftwareRendering, renderFpsLimit)
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp(bool forceSoftwareRendering = false, int renderFpsLimit = 60)
    {
        var appBuilder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .AfterSetup(_ => TryCapRenderFps(renderFpsLimit));

        if (forceSoftwareRendering && OperatingSystem.IsWindows())
        {
            appBuilder = appBuilder.With(new Win32PlatformOptions
            {
                RenderingMode = new[] { Win32RenderingMode.Software }
            });
        }

        return appBuilder;
    }

    /// <summary>
    /// Caps Avalonia's compositor render loop (see AppConfiguration.RenderFpsLimit). There's no
    /// officially supported public API for this in Avalonia 11 — AvaloniaLocator/DefaultRenderTimer
    /// are marked [PrivateApi] (runtime-public, hidden from the compile-time ref assembly), so this
    /// goes through reflection. Fails soft: if a future Avalonia version renames/removes these, the
    /// render loop just stays uncapped instead of crashing the app.
    /// </summary>
    private static void TryCapRenderFps(int fps)
    {
        if (fps <= 0) return;
        try
        {
            var avaloniaBase = Assembly.Load("Avalonia.Base");
            var locatorType = avaloniaBase.GetType("Avalonia.AvaloniaLocator")!;
            var timerType = avaloniaBase.GetType("Avalonia.Rendering.DefaultRenderTimer")!;
            var iTimerType = avaloniaBase.GetType("Avalonia.Rendering.IRenderTimer")!;

            var currentMutable = locatorType.GetProperty("CurrentMutable", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var bind = locatorType.GetMethod("Bind")!.MakeGenericMethod(iTimerType);
            var registrationHelper = bind.Invoke(currentMutable, null)!;
            var toConstant = registrationHelper.GetType().GetMethod("ToConstant")!.MakeGenericMethod(timerType);
            var timerInstance = Activator.CreateInstance(timerType, fps);
            toConstant.Invoke(registrationHelper, new[] { timerInstance });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RenderFpsCap] Failed to apply (Avalonia internals may have changed): {ex.Message}");
        }
    }
}
