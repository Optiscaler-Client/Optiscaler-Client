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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace OptiscalerClient.Helpers
{
    /// <summary>
    /// AMD renamed the FSR4 INT8 mod DLL between releases (amd_fidelityfx_upscaler_dx12.dll -> amdxcffx64.dll).
    /// Centralizes the known names so every consumer recognizes either one.
    /// </summary>
    public static class Fsr4Int8DllHelper
    {
        public const string LegacyFileName = "amd_fidelityfx_upscaler_dx12.dll";
        public const string CurrentFileName = "amdxcffx64.dll";

        /// <summary>
        /// Additional per-effect DLLs shipped by FidelityFX SDK 2.0+ split packages (the monolithic
        /// amd_fidelityfx_dx12.dll was broken up by effect type). Accepted alongside the main
        /// upscaler DLL names wherever an FSR 4 package is recognized/extracted/swapped.
        /// </summary>
        public const string RadianceCacheFileName = "amd_fidelityfx_radiancecache_dx12.dll";
        public const string LoaderFileName = "amd_fidelityfx_loader_dx12.dll";
        public const string FrameGenerationFileName = "amd_fidelityfx_framegeneration_dx12.dll";
        public const string DenoiserFileName = "amd_fidelityfx_denoiser_dx12.dll";

        /// <summary>
        /// Custom amdxc64.dll needed by OptiScaler's LoadCustomAmdxc64OnRdna2 to work around AMD's
        /// driver-side GPU whitelist for the "current" build (amdxcffx64.dll) on RDNA2. Distinct from
        /// CurrentFileName despite the similar name — this one goes in OptiDllPath (".\OptiScaler\"),
        /// not next to the game's .exe, and is only picked up when an Extras release actually ships it.
        /// </summary>
        public const string CustomRdna2FileName = "amdxc64.dll";

        public static readonly string[] KnownFileNames =
        {
            LegacyFileName, CurrentFileName,
            RadianceCacheFileName, LoaderFileName, FrameGenerationFileName, DenoiserFileName
        };

        /// <summary>Short human label for the FidelityFX effect a known filename belongs to, used
        /// wherever the user picks which packaged file(s) to swap/copy (e.g. Fsr4SwapSelectionWindow).</summary>
        public static string GetEffectDisplayName(string fileName)
        {
            if (string.Equals(fileName, LegacyFileName, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, CurrentFileName, System.StringComparison.OrdinalIgnoreCase))
                return "Upscaler";
            if (string.Equals(fileName, RadianceCacheFileName, System.StringComparison.OrdinalIgnoreCase)) return "Radiance Cache";
            if (string.Equals(fileName, LoaderFileName, System.StringComparison.OrdinalIgnoreCase)) return "Loader";
            if (string.Equals(fileName, FrameGenerationFileName, System.StringComparison.OrdinalIgnoreCase)) return "Frame Generation";
            if (string.Equals(fileName, DenoiserFileName, System.StringComparison.OrdinalIgnoreCase)) return "Denoiser";
            return fileName;
        }

        /// <summary>
        /// Stable (non-localized, never renamed) identifiers for the 5 logical FSR4 file "slots" a
        /// user can pre-configure defaults for in Fsr4SwapOptionsWindow — persisted as-is in
        /// AppConfiguration.Fsr4SwapDefaultFileKeys, so unlike GetEffectDisplayName's output this
        /// must never change once shipped. "Upscaler" covers both LegacyFileName and CurrentFileName.
        /// </summary>
        public static readonly string[] LogicalFileKeys = { "Upscaler", "Loader", "FrameGeneration", "Denoiser", "RadianceCache" };

        public static string GetLogicalKey(string fileName)
        {
            if (string.Equals(fileName, LegacyFileName, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, CurrentFileName, System.StringComparison.OrdinalIgnoreCase))
                return "Upscaler";
            if (string.Equals(fileName, LoaderFileName, System.StringComparison.OrdinalIgnoreCase)) return "Loader";
            if (string.Equals(fileName, FrameGenerationFileName, System.StringComparison.OrdinalIgnoreCase)) return "FrameGeneration";
            if (string.Equals(fileName, DenoiserFileName, System.StringComparison.OrdinalIgnoreCase)) return "Denoiser";
            if (string.Equals(fileName, RadianceCacheFileName, System.StringComparison.OrdinalIgnoreCase)) return "RadianceCache";
            return fileName;
        }

        /// <summary>Display label for a LogicalFileKeys entry (as opposed to GetEffectDisplayName,
        /// which takes an actual filename).</summary>
        public static string GetLogicalKeyDisplayName(string key) => key switch
        {
            "FrameGeneration" => "Frame Generation",
            "RadianceCache" => "Radiance Cache",
            _ => key
        };

        /// <summary>
        /// Narrows swap candidates down to the ones the user pre-configured as defaults (Fsr4SwapOptionsWindow's
        /// "Choose default values" mode). An empty/unset key set means "never configured" and is treated
        /// as "all" so a fresh install (or a config predating this feature) keeps swapping everything.
        /// </summary>
        public static List<(string TargetPath, string SourceContentPath)> FilterCandidatesByDefaultKeys(
            List<(string TargetPath, string SourceContentPath)> candidates, IEnumerable<string> allowedKeys)
        {
            var allowed = new HashSet<string>(allowedKeys, System.StringComparer.OrdinalIgnoreCase);
            if (allowed.Count == 0) return candidates;
            return candidates.Where(c => allowed.Contains(GetLogicalKey(Path.GetFileName(c.SourceContentPath)))).ToList();
        }

        public static bool IsKnownFileName(string fileName)
        {
            foreach (var name in KnownFileNames)
            {
                if (string.Equals(fileName, name, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>Returns the full path to whichever known DLL name exists in the directory, or null.</summary>
        public static string? FindIn(string directory)
        {
            foreach (var name in KnownFileNames)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        public static bool ExistsIn(string directory) => FindIn(directory) != null;

        /// <summary>Formats GitHub tags for display without changing their cache/config identity.</summary>
        public static string FormatVersionLabel(string version)
        {
            var normalized = version.Replace('_', ' ').Trim();
            return Regex.Replace(normalized, @"(?i)\bfsr\s*(?=\d)", "FSR ");
        }

        private static readonly Regex FourPlusSegmentVersion = new(@"\d+(?:\.\d+){3,}", RegexOptions.Compiled);

        /// <summary>AMD build tags carry a 4th (or later) build-number segment, e.g. "4.1.1.2740" — too
        /// noisy to show by default, so it's dropped down to 3 segments ("4.1.1").</summary>
        private static string TruncateToThreeSegments(string version)
        {
            var match = FourPlusSegmentVersion.Match(version);
            if (!match.Success) return version;
            var truncated = string.Join(".", match.Value.Split('.').Take(3));
            return version[..match.Index] + truncated + version[(match.Index + match.Length)..];
        }

        /// <summary>
        /// Formats <paramref name="version"/> truncated to 3 segments, unless another version in
        /// <paramref name="siblings"/> truncates to the same value — then the full version (with its
        /// build number) is kept so the two stay distinguishable in a list.
        /// </summary>
        public static string FormatDisplayLabel(string version, IEnumerable<string> siblings)
        {
            var truncated = TruncateToThreeSegments(version);
            bool collides = siblings.Any(other =>
                !string.Equals(other, version, System.StringComparison.OrdinalIgnoreCase) &&
                string.Equals(TruncateToThreeSegments(other), truncated, System.StringComparison.OrdinalIgnoreCase));
            return FormatVersionLabel(collides ? version : truncated);
        }

        /// <summary>
        /// The 3 filenames the DLL-swap feature will look for/replace directly in a game's root
        /// folder: both names of the main FSR4 INT8 DLL, plus the RDNA2 companion. Deliberately a
        /// separate list from KnownFileNames — that one is used elsewhere to detect the Extras DLL
        /// as installed *through OptiScaler*, and must not start matching CustomRdna2FileName (which
        /// normally lives in ".\OptiScaler\", not the game root, and has a different source — see
        /// ComponentManagementService.GetCachedCustomAmdxc64Path vs. DownloadExtrasDllAsync).
        /// </summary>
        public static readonly string[] SwapTargetFileNames =
        {
            LegacyFileName, CurrentFileName,
            RadianceCacheFileName, LoaderFileName, FrameGenerationFileName, DenoiserFileName,
            CustomRdna2FileName
        };

        /// <summary>Returns the full path to whichever swap-target name exists directly in the game's root, or null.</summary>
        public static string? FindSwapTargetIn(string gameDir, bool includeRdna2Companion = true)
        {
            var names = includeRdna2Companion ? SwapTargetFileNames : KnownFileNames;
            foreach (var name in names)
            {
                var path = Path.Combine(gameDir, name);
                if (File.Exists(path)) return path;
            }
            return null;
        }

        /// <summary>
        /// Determines, for a package's recognized files, exactly which (targetPath, sourceContentPath)
        /// pairs a swap should write. The two "Upscaler" names (legacy/current) are alternate names for
        /// the same conceptual file and collapse into a single candidate: if either already exists in
        /// gameDir, that exact name is kept as the target (so a game hardcoded to load one specific
        /// filename keeps working) — otherwise the package's own filename is used. Every other known
        /// file (Loader/FrameGeneration/Denoiser/RadianceCache) has one canonical name, so it maps
        /// 1:1 to gameDir + that name. Shared by ManageGameWindow's interactive swap and
        /// BulkInstallWindow's batch swap (which applies every candidate without prompting).
        /// </summary>
        public static List<(string TargetPath, string SourceContentPath)> BuildSwapCandidates(
            string gameDir, string cacheDir, IEnumerable<string> packagedFiles)
        {
            var files = packagedFiles as IList<string> ?? packagedFiles.ToList();
            var candidates = new List<(string, string)>();

            var upscalerNames = new[] { LegacyFileName, CurrentFileName };
            var packagedUpscaler = files.FirstOrDefault(f => upscalerNames.Contains(f, System.StringComparer.OrdinalIgnoreCase));
            if (packagedUpscaler != null)
            {
                var existingUpscaler = FindIn(gameDir);
                var targetName = existingUpscaler != null ? Path.GetFileName(existingUpscaler) : packagedUpscaler;
                candidates.Add((Path.Combine(gameDir, targetName), Path.Combine(cacheDir, packagedUpscaler)));
            }

            foreach (var extra in new[] { RadianceCacheFileName, LoaderFileName, FrameGenerationFileName, DenoiserFileName })
            {
                if (files.Contains(extra, System.StringComparer.OrdinalIgnoreCase))
                    candidates.Add((Path.Combine(gameDir, extra), Path.Combine(cacheDir, extra)));
            }

            return candidates;
        }
    }
}
