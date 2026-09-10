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
using System.Text.RegularExpressions;

namespace OptiscalerClient.Helpers
{
    /// <summary>
    /// Unified version comparer that accurately sorts standard SemVer releases,
    /// prereleases (alpha, pre, beta, rc), build-numbered releases, and nightly builds (nightly-YYYYMMDD).
    /// Returns &gt; 0 if x is newer than y, &lt; 0 if x is older than y, and 0 if equal.
    /// Used with OrderByDescending(v =&gt; v, VersionComparer.Instance) for newest-to-oldest ordering.
    /// </summary>
    public class VersionComparer : IComparer<string>
    {
        public static readonly VersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;

            // "rolling" or "latest" keyword always ranks highest
            bool xRolling = string.Equals(x, "rolling", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "latest", StringComparison.OrdinalIgnoreCase);
            bool yRolling = string.Equals(y, "rolling", StringComparison.OrdinalIgnoreCase) || string.Equals(y, "latest", StringComparison.OrdinalIgnoreCase);
            if (xRolling != yRolling) return xRolling ? 1 : -1;
            if (xRolling && yRolling) return 0;

            bool xNightly = IsNightly(x);
            bool yNightly = IsNightly(y);
            if (xNightly && yNightly)
            {
                return CompareNightly(x, y);
            }
            if (xNightly != yNightly)
            {
                return CompareNightlyVsStandard(x, y);
            }

            return CompareStandard(x, y);
        }

        public static bool IsNightly(string v)
        {
            return v.Contains("nightly", StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareNightly(string x, string y)
        {
            // Extract 8-digit date YYYYMMDD if present (e.g. nightly-20260910)
            var matchX = Regex.Match(x, @"\d{8}");
            var matchY = Regex.Match(y, @"\d{8}");

            if (matchX.Success && matchY.Success)
            {
                if (long.TryParse(matchX.Value, out long dateX) && long.TryParse(matchY.Value, out long dateY))
                {
                    int dateComp = dateX.CompareTo(dateY);
                    if (dateComp != 0) return dateComp;
                }
            }
            else if (matchX.Success)
            {
                // x has a date (modern nightly), y does not (legacy tag like 0.7-old_nightly or nightly)
                return 1;
            }
            else if (matchY.Success)
            {
                return -1;
            }

            // If neither has an 8-digit date, compare numeric version parts (e.g. 0.7 vs unversioned)
            var verX = ExtractVersionComponents(x);
            var verY = ExtractVersionComponents(y);
            int comp = CompareNumericParts(verX.Numbers, verY.Numbers);
            if (comp != 0) return comp;

            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static int CompareNightlyVsStandard(string x, string y)
        {
            var verX = ExtractVersionComponents(x);
            var verY = ExtractVersionComponents(y);
            int comp = CompareNumericParts(verX.Numbers, verY.Numbers);
            if (comp != 0) return comp;
            return IsNightly(x) ? -1 : 1;
        }

        private static int CompareStandard(string x, string y)
        {
            var parsedX = ExtractVersionComponents(x);
            var parsedY = ExtractVersionComponents(y);

            // Compare major.minor.patch.build numeric segments
            int numComp = CompareNumericParts(parsedX.Numbers, parsedY.Numbers);
            if (numComp != 0) return numComp;

            // Numeric parts are identical (e.g. 0.9.5 vs 0.9.5-pre4 vs 0.9.5-beta1)
            bool xHasSuffix = !string.IsNullOrEmpty(parsedX.Suffix);
            bool yHasSuffix = !string.IsNullOrEmpty(parsedY.Suffix);

            if (!xHasSuffix && yHasSuffix) return 1;  // Pure stable 0.9.5 > 0.9.5-pre4
            if (xHasSuffix && !yHasSuffix) return -1; // 0.9.5-pre4 < Pure stable 0.9.5
            if (xHasSuffix && yHasSuffix)
            {
                int stageX = GetPrereleaseStageRank(parsedX.Suffix);
                int stageY = GetPrereleaseStageRank(parsedY.Suffix);
                if (stageX != stageY) return stageX.CompareTo(stageY);

                // Same prerelease stage: compare trailing numeric index (e.g. pre4 vs pre3, pre66 vs pre9)
                int numX = ExtractTrailingNumber(parsedX.Suffix);
                int numY = ExtractTrailingNumber(parsedY.Suffix);
                if (numX != numY) return numX.CompareTo(numY);

                return string.Compare(parsedX.Suffix, parsedY.Suffix, StringComparison.OrdinalIgnoreCase);
            }

            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static (List<int> Numbers, string Suffix) ExtractVersionComponents(string v)
        {
            var trimmed = v.Trim().TrimStart('v', 'V', '.');
            int splitIdx = -1;
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '-' || c == '+')
                {
                    splitIdx = i;
                    break;
                }
                if (char.IsLetter(c))
                {
                    splitIdx = i;
                    break;
                }
            }

            string numPart = splitIdx >= 0 ? trimmed.Substring(0, splitIdx).TrimEnd('.') : trimmed;
            string suffix = splitIdx >= 0 ? trimmed.Substring(splitIdx).TrimStart('-', '+') : string.Empty;

            var numbers = new List<int>();
            if (!string.IsNullOrEmpty(numPart))
            {
                var segments = numPart.Split('.');
                foreach (var seg in segments)
                {
                    if (int.TryParse(seg, out int n))
                    {
                        numbers.Add(n);
                    }
                    else
                    {
                        var m = Regex.Match(seg, @"^\d+");
                        if (m.Success && int.TryParse(m.Value, out int mn))
                            numbers.Add(mn);
                        else
                            numbers.Add(0);
                    }
                }
            }

            return (numbers, suffix);
        }

        private static int CompareNumericParts(List<int> a, List<int> b)
        {
            int maxLen = Math.Max(a.Count, b.Count);
            for (int i = 0; i < maxLen; i++)
            {
                int valA = i < a.Count ? a[i] : 0;
                int valB = i < b.Count ? b[i] : 0;
                if (valA != valB) return valA.CompareTo(valB);
            }
            return 0;
        }

        private static int GetPrereleaseStageRank(string suffix)
        {
            var lower = suffix.ToLowerInvariant();
            if (lower.Contains("rc")) return 4;
            if (lower.Contains("beta")) return 3;
            if (lower.Contains("pre")) return 2;
            if (lower.Contains("alpha")) return 1;
            return 0;
        }

        private static int ExtractTrailingNumber(string s)
        {
            var match = Regex.Match(s, @"\d+");
            if (match.Success && int.TryParse(match.Value, out int val))
                return val;
            return 0;
        }
    }
}
