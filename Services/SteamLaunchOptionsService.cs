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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using OptiscalerClient.Views;

namespace OptiscalerClient.Services
{
    /// <summary>
    /// Reads and writes a Steam game's Launch Options (Properties → General) on Linux, so the Linux
    /// NR fork's launch wrapper ("'…/launch.sh' %command%") is added and removed automatically instead
    /// of the user pasting it by hand. Steam keeps them per account in
    /// userdata/&lt;id&gt;/config/localconfig.vdf under apps/&lt;appid&gt;/LaunchOptions, loads that file at
    /// startup and rewrites it on exit — so it may only be written while Steam is closed.
    ///
    /// Whatever the user already has there is preserved: only the fork's own wrapper token is
    /// inserted (right before %command%) or removed.
    /// </summary>
    public static class SteamLaunchOptionsService
    {
        private static readonly string[] SteamRoots =
        {
            ".local/share/Steam", ".steam/steam", ".var/app/com.valvesoftware.Steam/.local/share/Steam",
        };

        // A launch wrapper written by the Linux NR fork (guentra's or bulacha3's, original or lmxxf
        // backend): its path always goes through a "dlssnr-linux" folder and ends in launch.sh.
        private static readonly Regex ManagedLauncher = new(
            "(?:'[^']*dlssnr-linux[^']*launch\\.sh'|\"[^\"]*dlssnr-linux[^\"]*launch\\.sh\"|[^\\s'\"]*dlssnr-linux[^\\s'\"]*launch\\.sh)\\s*",
            RegexOptions.IgnoreCase);

        /// <summary>Steam loads localconfig.vdf at startup and rewrites it on exit, so an edit made
        /// while it runs is lost.</summary>
        public static bool IsSteamRunning()
        {
            try { return Process.GetProcessesByName("steam").Length > 0; }
            catch (Exception) { return false; }
        }

        /// <summary>Adds <paramref name="launcher"/> (already shell-quoted) to the game's launch
        /// options, keeping everything already there. False when no Steam config for this game could
        /// be found or written (the caller then shows the line to paste by hand). Steam must be
        /// closed.</summary>
        public static bool TryAddLauncher(string appId, string launcher, string? optiScalerDll, out string newOptions)
            => TryUpdate(appId, current => AddLauncher(current, launcher, optiScalerDll), out newOptions);

        /// <summary>Removes the fork's wrapper from the game's launch options, keeping the rest.
        /// Steam must be closed.</summary>
        public static bool TryRemoveLauncher(string appId, out string newOptions)
            => TryUpdate(appId, RemoveLauncher, out newOptions);

        private static bool TryUpdate(string appId, Func<string, string> change, out string newOptions)
        {
            newOptions = "";
            var files = FindLocalConfigs(appId);
            if (files.Count == 0)
            {
                DebugWindow.Log($"[SteamLaunchOptions] No localconfig.vdf found for app {appId}.");
                return false;
            }

            var written = false;
            foreach (var file in files)
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var current = GetLaunchOptions(text, appId) ?? "";
                    var updated = change(current);
                    newOptions = updated;
                    if (updated == current) { written = true; continue; }

                    var newText = SetLaunchOptions(text, appId, updated);
                    if (newText == null) continue;

                    var backup = file + ".optiscaler-client.bak";
                    if (!File.Exists(backup)) File.Copy(file, backup);
                    File.WriteAllText(file, newText, new UTF8Encoding(false));
                    DebugWindow.Log($"[SteamLaunchOptions] {file}: app {appId} launch options '{current}' -> '{updated}'.");
                    written = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    DebugWindow.Log($"[SteamLaunchOptions] Could not update {file}: {ex.Message}");
                }
            }
            return written;
        }

        /// <summary>Every account's localconfig.vdf that already lists <paramref name="appId"/>;
        /// if none does, the most recently used one.</summary>
        private static List<string> FindLocalConfigs(string appId)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var all = new List<string>();
            foreach (var root in SteamRoots)
            {
                var userdata = Path.Combine(home, root, "userdata");
                if (!Directory.Exists(userdata)) continue;
                try
                {
                    foreach (var dir in Directory.GetDirectories(userdata))
                    {
                        var file = Path.Combine(dir, "config", "localconfig.vdf");
                        if (File.Exists(file) && !all.Contains(Path.GetFullPath(file))) all.Add(Path.GetFullPath(file));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            var withApp = all.Where(f =>
            {
                try { return FindAppBlock(Tokenize(File.ReadAllText(f)), appId) != null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            }).ToList();
            if (withApp.Count > 0) return withApp;
            return all.OrderByDescending(File.GetLastWriteTimeUtc).Take(1).ToList();
        }

        // ── Launch options text ─────────────────────────────────────────────────────

        /// <summary>The fork's wrapper token (as quoted in <paramref name="launchOptions"/>), or null.</summary>
        public static string? ExtractLauncher(string? launchOptions)
        {
            if (string.IsNullOrEmpty(launchOptions)) return null;
            var m = ManagedLauncher.Match(launchOptions);
            return m.Success ? m.Value.Trim() : null;
        }

        // Environment added in front of the wrapper when OptiScaler runs alongside the mod: the
        // swapchain queue mode the fork's OptiScaler route needs, and a native override for
        // OptiScaler's proxy DLL. Any WINEDLLOVERRIDES the user already had is merged into ours
        // (ours is appended last) so it isn't shadowed, and split back out on removal.
        private const string SwapchainQueueVar = "DLSSNR_SWAPCHAIN_QUEUE=1";
        private static readonly Regex ManagedEnvBlock = new(
            "DLSSNR_SWAPCHAIN_QUEUE=1\\s+WINEDLLOVERRIDES=\"([^\"]*)\"\\s+(?=['\"][^'\"]*dlssnr-linux|[^\\s'\"]*dlssnr-linux)",
            RegexOptions.IgnoreCase);
        private static readonly Regex UserDllOverrides = new(
            "(?<=^|\\s)WINEDLLOVERRIDES=(\"[^\"]*\"|'[^']*'|\\S*)\\s*");

        /// <summary>Inserts the wrapper right before %command% (so env vars and tools like
        /// gamemoderun keep running first), replacing any wrapper from an earlier install.
        /// Game arguments without %command% ("-dx12") stay after it, where Steam would put them.
        /// With <paramref name="optiScalerDll"/> (OptiScaler installed alongside the mod) the
        /// swapchain queue variable and a native override for that DLL go right before it.</summary>
        internal static string AddLauncher(string current, string launcher, string? optiScalerDll = null)
        {
            var rest = RemoveLauncher(current);
            var token = launcher;
            if (!string.IsNullOrEmpty(optiScalerDll))
            {
                var dll = Path.GetFileNameWithoutExtension(optiScalerDll);
                var overrides = "";
                var existing = UserDllOverrides.Match(rest);
                if (existing.Success)
                {
                    overrides = existing.Groups[1].Value.Trim('"', '\'');
                    rest = Collapse(rest.Remove(existing.Index, existing.Length));
                }
                overrides = string.IsNullOrEmpty(overrides) ? $"{dll}=n,b" : $"{overrides};{dll}=n,b";
                token = $"{SwapchainQueueVar} WINEDLLOVERRIDES=\"{overrides}\" {launcher}";
            }

            if (rest.Length == 0) return $"{token} %command%";
            var idx = rest.IndexOf("%command%", StringComparison.Ordinal);
            if (idx < 0) return $"{token} %command% {rest}";
            return rest.Substring(0, idx) + token + " " + rest.Substring(idx);
        }

        /// <summary>Removes the fork's wrapper (and the environment added with it), keeping
        /// everything else — a WINEDLLOVERRIDES the user had before goes back to its own value. A
        /// bare "%command%" left behind means nothing else was there, so it becomes empty.</summary>
        internal static string RemoveLauncher(string current)
        {
            var result = ManagedEnvBlock.Replace(current, m =>
            {
                var entries = m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
                if (entries.Count > 0) entries.RemoveAt(entries.Count - 1); // ours is always last
                return entries.Count > 0 ? $"WINEDLLOVERRIDES=\"{string.Join(';', entries)}\" " : "";
            });
            result = Collapse(ManagedLauncher.Replace(result, ""));
            return result == "%command%" ? "" : result;
        }

        private static string Collapse(string text) => Regex.Replace(text, "[ \\t]{2,}", " ").Trim();

        // ── localconfig.vdf ─────────────────────────────────────────────────────────

        private enum TokenKind { String, Open, Close }
        private sealed record Token(TokenKind Kind, string Value, int Start, int End);

        private static List<Token> Tokenize(string text)
        {
            var tokens = new List<Token>();
            int i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (c == '{') { tokens.Add(new Token(TokenKind.Open, "{", i, i + 1)); i++; continue; }
                if (c == '}') { tokens.Add(new Token(TokenKind.Close, "}", i, i + 1)); i++; continue; }
                if (c == '"')
                {
                    var start = i++;
                    var sb = new StringBuilder();
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1]); i += 2; continue; }
                        sb.Append(text[i++]);
                    }
                    i++; // closing quote
                    tokens.Add(new Token(TokenKind.String, sb.ToString(), start, Math.Min(i, text.Length)));
                    continue;
                }
                // Unquoted token (not written by Steam, tolerated).
                var s = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}') i++;
                tokens.Add(new Token(TokenKind.String, text.Substring(s, i - s), s, i));
            }
            return tokens;
        }

        /// <summary>Index of the "{" and its matching "}" of the child block named
        /// <paramref name="key"/> directly inside the block spanning tokens (open, close).</summary>
        private static (int Open, int Close)? FindChild(List<Token> t, int open, int close, string key)
        {
            int i = open + 1;
            while (i < close)
            {
                if (t[i].Kind == TokenKind.String && i + 1 < close && t[i + 1].Kind == TokenKind.Open)
                {
                    var end = MatchingClose(t, i + 1);
                    if (string.Equals(t[i].Value, key, StringComparison.OrdinalIgnoreCase)) return (i + 1, end);
                    i = end + 1;
                    continue;
                }
                i += t[i].Kind == TokenKind.String && i + 1 < close && t[i + 1].Kind == TokenKind.String ? 2 : 1;
            }
            return null;
        }

        private static int MatchingClose(List<Token> t, int open)
        {
            int depth = 0;
            for (int i = open; i < t.Count; i++)
            {
                if (t[i].Kind == TokenKind.Open) depth++;
                else if (t[i].Kind == TokenKind.Close && --depth == 0) return i;
            }
            return t.Count - 1;
        }

        private static (int Open, int Close)? FindAppsBlock(List<Token> t)
        {
            if (t.Count < 2 || t[1].Kind != TokenKind.Open) return null;
            (int, int)? block = (1, MatchingClose(t, 1));
            foreach (var key in new[] { "Software", "Valve", "Steam", "apps" })
            {
                block = FindChild(t, block.Value.Item1, block.Value.Item2, key);
                if (block == null) return null;
            }
            return block;
        }

        private static (int Open, int Close)? FindAppBlock(List<Token> t, string appId)
        {
            var apps = FindAppsBlock(t);
            return apps == null ? null : FindChild(t, apps.Value.Open, apps.Value.Close, appId);
        }

        /// <summary>Index of the value token of <paramref name="key"/> directly inside a block.</summary>
        private static int FindValue(List<Token> t, int open, int close, string key)
        {
            int i = open + 1;
            while (i < close)
            {
                if (t[i].Kind == TokenKind.String && i + 1 < close && t[i + 1].Kind == TokenKind.Open)
                {
                    i = MatchingClose(t, i + 1) + 1;
                    continue;
                }
                if (t[i].Kind == TokenKind.String && i + 1 < close && t[i + 1].Kind == TokenKind.String)
                {
                    if (string.Equals(t[i].Value, key, StringComparison.OrdinalIgnoreCase)) return i + 1;
                    i += 2;
                    continue;
                }
                i++;
            }
            return -1;
        }

        internal static string? GetLaunchOptions(string vdf, string appId)
        {
            var t = Tokenize(vdf);
            var app = FindAppBlock(t, appId);
            if (app == null) return null;
            var v = FindValue(t, app.Value.Open, app.Value.Close, "LaunchOptions");
            return v < 0 ? null : t[v].Value;
        }

        /// <summary>The same file with the game's LaunchOptions set to <paramref name="value"/>
        /// (added to the game's block, or a new block, when missing). Null when the file has no
        /// apps section to add it to.</summary>
        internal static string? SetLaunchOptions(string vdf, string appId, string value)
        {
            var t = Tokenize(vdf);
            var quoted = "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

            var app = FindAppBlock(t, appId);
            if (app != null)
            {
                var v = FindValue(t, app.Value.Open, app.Value.Close, "LaunchOptions");
                if (v >= 0)
                    return vdf.Substring(0, t[v].Start) + quoted + vdf.Substring(t[v].End);

                var indent = IndentOf(vdf, t[app.Value.Open].Start) + "\t";
                var insertAt = t[app.Value.Open].End;
                return vdf.Insert(insertAt, $"\n{indent}\"LaunchOptions\"\t\t{quoted}");
            }

            var apps = FindAppsBlock(t);
            if (apps == null) return null;
            var appsIndent = IndentOf(vdf, t[apps.Value.Open].Start) + "\t";
            var block = $"\n{appsIndent}\"{appId}\"\n{appsIndent}{{\n{appsIndent}\t\"LaunchOptions\"\t\t{quoted}\n{appsIndent}}}";
            return vdf.Insert(t[apps.Value.Open].End, block);
        }

        private static string IndentOf(string text, int index)
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
            var sb = new StringBuilder();
            for (int i = lineStart; i < text.Length && (text[i] == '\t' || text[i] == ' '); i++) sb.Append(text[i]);
            return sb.ToString();
        }
    }
}
