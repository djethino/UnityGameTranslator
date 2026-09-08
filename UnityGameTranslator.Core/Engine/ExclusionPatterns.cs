using System;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Whether a path in the game's hierarchy is covered by an exclusion somebody wrote.
    ///
    /// 🔴 **Pure by contract**, like Engine/TextNormalization: two strings in, an answer out. No
    /// Unity, no state, no clock. Linked by tests/UnityGameTranslator.Core.Checks, which cannot
    /// reference the Core — so a `using UnityEngine` here stops that project compiling.
    ///
    /// ⚠ **One matcher for every framework, and that is the point.** uGUI, TextMeshPro, NGUI and
    /// the rest differ in how a path is BUILT and in nothing else; a second set of rules would make
    /// one written pattern mean two things depending on what the label happened to be made of.
    ///
    /// ⚠ **And for both features that name a path.** Exclusions use it, and so do font rules
    /// through their `path:` prefix — `Canvas/HUD/**` means the same thing whether somebody wrote
    /// it to leave a text alone or to give it another font. Two matchers would be two grammars for
    /// one syntax, and nothing on screen would say which one a pattern had been read by.
    ///
    /// ⚠ **What a pattern means is a promise to whoever wrote it.** An exclusion that silently
    /// stops matching is text going to a model that somebody asked to be left alone — a proper
    /// noun, a brand, a line they translate by hand. It had no case of its own until 2026-09-08.
    ///
    /// Moved out of TranslatorCore that day (step 6 of analyse/plan-prealables-couches.md),
    /// verbatim: same code, same comments, same answers.
    /// </summary>
    public static class ExclusionPatterns
    {
        /// <summary>
        /// Match a path against an exclusion pattern.
        /// Patterns: "Canvas/Chat/**" matches any child, "**/PlayerName" matches at any depth.
        /// An exact path also matches all children (excluding "Canvas/Panel" excludes "Canvas/Panel/Text").
        /// </summary>
        public static bool Matches(string path, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            // Exact path exclusions implicitly exclude all children:
            // Pattern "Canvas/Panel" should match "Canvas/Panel", "Canvas/Panel/Child", "Canvas/Panel/Child/Text"
            // Only apply this when pattern has no wildcards (pure path exclusion)
            if (!pattern.Contains("*"))
            {
                if (string.Equals(path, pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                // Check if path is a child of the excluded path
                if (path.Length > pattern.Length && path[pattern.Length] == '/' &&
                    path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
                    return true;
                return false;
            }

            // Wildcard pattern matching
            // ** = any number of path segments (including zero)
            // * = any single path segment name

            // Split both into segments
            var pathParts = path.Split('/');
            var patternParts = pattern.Split('/');

            return MatchPatternRecursive(pathParts, 0, patternParts, 0);
        }

        private static bool MatchPatternRecursive(string[] path, int pathIdx, string[] pattern, int patternIdx)
        {
            // Base cases
            if (patternIdx >= pattern.Length)
                return pathIdx >= path.Length;

            string patternPart = pattern[patternIdx];

            if (patternPart == "**")
            {
                // ** matches zero or more path segments
                // Try matching rest of pattern at every remaining position
                for (int i = pathIdx; i <= path.Length; i++)
                {
                    if (MatchPatternRecursive(path, i, pattern, patternIdx + 1))
                        return true;
                }
                return false;
            }

            if (pathIdx >= path.Length)
                return false;

            string pathPart = path[pathIdx];

            if (patternPart == "*")
            {
                // * matches exactly one segment (any name)
                return MatchPatternRecursive(path, pathIdx + 1, pattern, patternIdx + 1);
            }

            // Check if pattern part contains * as wildcard within the name
            if (patternPart.Contains("*"))
            {
                // Convert to simple wildcard matching (e.g., "Chat*" matches "ChatWindow")
                string regexPattern = "^" + Regex.Escape(patternPart).Replace("\\*", ".*") + "$";
                if (!Regex.IsMatch(pathPart, regexPattern, RegexOptions.IgnoreCase))
                    return false;
            }
            else
            {
                // Exact match (case-insensitive)
                if (!string.Equals(pathPart, patternPart, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return MatchPatternRecursive(path, pathIdx + 1, pattern, patternIdx + 1);
        }
    }
}
