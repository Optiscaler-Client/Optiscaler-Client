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
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OptiscalerClient.Helpers
{
    /// <summary>
    /// Normalizes and fuzzy-matches game titles against a curated list (a wiki table, a game map,
    /// etc.) by name alone. Extracted from CompatibilityListService, which pioneered this exact
    /// normalization/tokenization/Jaccard-similarity approach against the OptiScaler wiki's
    /// Compatibility List — every edge case here (diacritics, camelCase, edition suffixes, curly
    /// apostrophes) was tuned against real reported mismatches, see the comments on each constant.
    /// RenodxModsService reuses this unchanged rather than re-deriving the same tuning independently.
    /// </summary>
    public static class GameNameMatcher
    {
        // Minimum Jaccard token-overlap score to accept a fuzzy match, and the minimum lead the
        // best candidate must have over the second-best one. Both tuned against real near-miss
        // pairs: "Resident Evil 9 Requiem" vs "Resident Evil Requiem" scores ~0.75 and is accepted;
        // "Grand Theft Auto V" vs "Grand Theft Auto IV" scores exactly 0.6 and must be rejected —
        // hence 0.7, not a rounder-looking 0.6 or 0.65.
        public const double DefaultMinScore = 0.7;
        public const double DefaultMinMargin = 0.15;

        // Words too common to be meaningful for similarity scoring.
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "of", "and"
        };

        // Edition/re-release suffixes stripped before tokenizing, so e.g. "Silent Hill 2 Remake"
        // and "Silent Hill 2" tokenize the same way. "Demo" is deliberately NOT in this list — a
        // demo is often its own separate curated entry with different notes, so treating it as
        // noise could match a game to the wrong (full-release) entry.
        private static readonly string[] EditionSuffixes =
        {
            "Deluxe", "Ultimate", "Gold", "GOTY", "Complete", "Enhanced",
            "Remastered", "Remake", "Definitive", "Standard", "Digital"
        };

        // Straight, curly, and grave apostrophe/quote variants a title's possessive might use
        // (e.g. "Marvel's Spider-Man 2" vs a locally typed "Marvels Spider-Man 2"). Removed
        // entirely rather than treated as a word boundary, so "Marvel's" and "Marvels" reduce to
        // the same "marvels" instead of the apostrophe'd side leaving a spurious extra "s" token.
        private static readonly char[] ApostropheChars = { '\'', '’', '‘', '`', '´' };

        public static HashSet<string> Tokenize(string name)
        {
            var cleaned = name;
            foreach (var suffix in EditionSuffixes)
                cleaned = Regex.Replace(cleaned, $@"\b{suffix}\b\s*(Edition)?", "", RegexOptions.IgnoreCase);

            cleaned = StripApostrophes(cleaned);
            cleaned = SplitCamelCase(cleaned);
            cleaned = RemoveDiacritics(cleaned);

            return Regex.Matches(cleaned, @"[\p{L}\p{Nd}]+")
                .Select(m => m.Value.ToLowerInvariant())
                .Where(w => !StopWords.Contains(w))
                .ToHashSet();
        }

        public static double JaccardSimilarity(HashSet<string> a, HashSet<string> b)
        {
            if (a.Count == 0 || b.Count == 0) return 0;
            int intersection = a.Count(b.Contains);
            int union = a.Count + b.Count - intersection;
            return union == 0 ? 0 : (double)intersection / union;
        }

        /// <summary>
        /// Normalizes a title for exact-match lookup (lowercase, accents/apostrophes/camelCase
        /// folded away, dashes turned to spaces). Two titles that only differ by these superficial
        /// variations normalize to the same string.
        /// </summary>
        public static string Normalize(string name)
        {
            var normalized = name.Trim().Replace("™", "").Replace("®", "").Replace("©", "");
            normalized = StripApostrophes(normalized);
            normalized = SplitCamelCase(normalized);
            normalized = RemoveDiacritics(normalized);
            // ASCII hyphen-minus plus the Unicode hyphen/dash variants a title might use instead
            // (e.g. "Spider‐Man") — turned into a space so a hyphenated name and an unpunctuated/
            // spaced one normalize to the same word sequence.
            normalized = Regex.Replace(normalized, "[-‐‑‒–—―−]", " ");
            normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
            return normalized.ToLowerInvariant();
        }

        /// <summary>
        /// Finds the best fuzzy match for <paramref name="query"/> among already-tokenized
        /// candidates (tokenize once per candidate list rebuild, not once per call — see how
        /// CompatibilityListService/RenodxModsService cache their tokenized lists). Requires both a
        /// minimum absolute score and a minimum lead over the runner-up, so two near-identical
        /// candidates (e.g. two similarly-named games) don't produce a confident wrong match.
        /// </summary>
        public static bool TryFuzzyMatch<T>(string query, List<(T Item, HashSet<string> Tokens)> candidates,
            out T? match, double minScore = DefaultMinScore, double minMargin = DefaultMinMargin)
        {
            match = default;
            var queryTokens = Tokenize(query);
            if (queryTokens.Count == 0) return false;

            T? best = default;
            HashSet<string>? bestTokens = null;
            double bestScore = 0;
            double secondBestScore = 0;
            bool found = false;

            foreach (var (item, tokens) in candidates)
            {
                var score = JaccardSimilarity(queryTokens, tokens);
                if (score > bestScore)
                {
                    secondBestScore = bestScore;
                    bestScore = score;
                    best = item;
                    bestTokens = tokens;
                    found = true;
                }
                else if (score > secondBestScore)
                {
                    secondBestScore = score;
                }
            }

            if (!found || (bestScore - secondBestScore) < minMargin)
                return false;

            // A subtitle-heavy title (e.g. "Death Stranding 2: On The Beach" vs the wiki's plain
            // "Death Stranding 2") can dilute Jaccard below minScore even though the shorter name
            // is entirely contained in the longer one — every one of its tokens is present, just
            // alongside extra words Jaccard counts as "not shared". Accept that case on perfect
            // containment alone, gated to >=2 shared tokens so a single common word (already rare
            // since stop words are stripped) can't false-positive against an unrelated longer title.
            int smallerCount = Math.Min(queryTokens.Count, bestTokens!.Count);
            int intersection = queryTokens.Count(bestTokens.Contains);
            bool isFullContainment = smallerCount >= 2 && intersection == smallerCount;

            if (bestScore < minScore && !isFullContainment)
                return false;

            match = best;
            return true;
        }

        /// <summary>
        /// Strips accents/diacritics (e.g. "Ragnarök" -> "Ragnarok") via Unicode NFD decomposition
        /// + removing combining marks, so names that only differ by accented characters still
        /// match. True distinct letters that merely look similar (e.g. "ø", "æ") don't decompose
        /// this way and are intentionally left alone rather than guessed at.
        /// </summary>
        private static string RemoveDiacritics(string text)
        {
            var decomposed = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    sb.Append(c);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string StripApostrophes(string text)
        {
            foreach (var ch in ApostropheChars)
                text = text.Replace(ch.ToString(), "");
            return text;
        }

        /// <summary>
        /// Inserts a space at each lowercase/digit -> uppercase transition (e.g. "SpiderMan" ->
        /// "Spider Man"), so a locally concatenated camelCase title tokenizes the same as a
        /// hyphenated or spaced form of the same words. All-caps runs (acronyms like "NBA2K") have
        /// no such transition and are left alone rather than guessed at.
        /// </summary>
        private static string SplitCamelCase(string text)
        {
            return Regex.Replace(text, @"(?<=[\p{Ll}\p{Nd}])(?=\p{Lu})", " ");
        }
    }
}
