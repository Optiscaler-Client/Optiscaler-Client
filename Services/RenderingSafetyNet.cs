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
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// Crash-safety net for GPU-driver rendering crashes that bypass normal .NET exception
    /// handling entirely (a raw CoreCLR "internal error in the .NET Runtime" FailFast, observed
    /// on some AMD GPU + old Windows 10 + very recent Adrenalin driver combinations). That class
    /// of crash never reaches AppDomain.UnhandledException, so it can't be caught directly — it's
    /// detected indirectly instead: a marker flag is set at every startup and cleared only on a
    /// clean shutdown (AppDomain.ProcessExit still fires for normal exits and handled exceptions).
    /// Finding the marker still set at the next startup means the previous run never got there.
    ///
    /// Two dirty shutdowns in a row (not one, to avoid misfiring on a single Task Manager kill or
    /// power loss) switch Avalonia's Windows rendering backend to pure software for all future
    /// launches, until the user explicitly opts back into hardware rendering from Settings. This
    /// applies whenever the user hasn't explicitly forced software rendering themselves — there's
    /// deliberately no "hardware, but never self-heal" option: a rendering preference that can get
    /// permanently stuck in the very crash loop it exists to escape defeats its own purpose.
    /// </summary>
    public static class RenderingSafetyNet
    {
        private const int UncleanShutdownsBeforeFallback = 2;

        /// <summary>
        /// Call once at the very start of Main(), before the Avalonia AppBuilder is configured.
        /// Updates the crash-streak bookkeeping and returns true if software rendering should be
        /// forced for this session.
        /// </summary>
        public static bool ShouldForceSoftwareRendering()
        {
            try
            {
                var componentService = new ComponentManagementService();
                var config = componentService.Config;

                if (config.RunInProgress)
                {
                    config.UncleanShutdownStreak++;
                    DebugWindow.Log($"[RenderingSafetyNet] Previous run did not shut down cleanly (streak={config.UncleanShutdownStreak}).");
                }

                config.RunInProgress = true;

                // Self-heal a stale/inconsistent state: earlier builds only set the flag without
                // persisting "software" into the preference itself, which left the Settings
                // dropdown showing "Hardware" even though the flag (and the notice next to it)
                // said otherwise. Reconciling here means any such leftover config fixes itself on
                // the very next launch, no manual edit required.
                if (config.ForcedSoftwareRenderingActive &&
                    !string.Equals(config.RenderingModePreference, "software", StringComparison.OrdinalIgnoreCase))
                {
                    config.RenderingModePreference = "software";
                }

                bool forceSoftware = string.Equals(config.RenderingModePreference, "software", StringComparison.OrdinalIgnoreCase);

                // Sticky once tripped — a clean exit resets the streak (see MarkCleanShutdown) but
                // must NOT silently move back to hardware rendering on its own, since that would
                // re-expose the user to the same crash on some future launch. Persisting the trip
                // straight into RenderingModePreference (not just a separate flag) keeps the
                // Settings dropdown honest about what's actually running, instead of still showing
                // "Hardware" while the app is quietly rendering in software underneath it.
                if (!forceSoftware && config.UncleanShutdownStreak >= UncleanShutdownsBeforeFallback)
                {
                    forceSoftware = true;
                    config.RenderingModePreference = "software";
                    config.ForcedSoftwareRenderingActive = true;
                    DebugWindow.Log("[RenderingSafetyNet] Auto-switching to software rendering after repeated crashes.");
                }

                componentService.SaveConfiguration();
                return forceSoftware;
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[RenderingSafetyNet] Failed to evaluate rendering safety net: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Call from AppDomain.ProcessExit. Clears the dirty-shutdown marker and resets the streak
        /// so a single clean run stops counting past crashes against future launches.
        /// </summary>
        public static void MarkCleanShutdown()
        {
            try
            {
                var componentService = new ComponentManagementService();
                componentService.Config.RunInProgress = false;
                componentService.Config.UncleanShutdownStreak = 0;
                componentService.SaveConfiguration();
            }
            catch (Exception ex)
            {
                DebugWindow.Log($"[RenderingSafetyNet] Failed to mark clean shutdown: {ex.Message}");
            }
        }
    }
}
