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
            return Compile(pattern).Matches(path);
        }

        /// <summary>
        /// A pattern taken apart once — its segments split, a name holding a <c>*</c> turned into
        /// its expression — for a caller that asks it of many paths (the exclusions and the font
        /// rules keep one per pattern). The same grammar and the same answers as
        /// <see cref="Matches(string, string)"/>, which is this, used once.
        ///
        /// 🔴 **Why** (2026-10-22): a name with a <c>*</c> built its expression again at every
        /// comparison, for every pattern, for every text the decision was asked about.
        /// </summary>
        public static Compiled Compile(string pattern) => new Compiled(pattern ?? "");

        public sealed class Compiled
        {
            private readonly string _pattern;
            private readonly bool _plain;
            private readonly string[] _parts;
            private readonly Regex[] _names;

            internal Compiled(string pattern)
            {
                _pattern = pattern;
                // Exact path exclusions implicitly exclude all children:
                // Pattern "Canvas/Panel" should match "Canvas/Panel", "Canvas/Panel/Child", "Canvas/Panel/Child/Text"
                // Only apply this when pattern has no wildcards (pure path exclusion)
                _plain = !pattern.Contains("*");
                if (_plain) return;

                // Wildcard pattern matching
                // ** = any number of path segments (including zero)
                // * = any single path segment name
                _parts = pattern.Split('/');
                _names = new Regex[_parts.Length];
                for (int i = 0; i < _parts.Length; i++)
                {
                    string part = _parts[i];
                    // A name holding * as a wildcard (e.g., "Chat*" matches "ChatWindow")
                    if (part != "**" && part != "*" && part.Contains("*"))
                        _names[i] = new Regex("^" + Regex.Escape(part).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
                }
            }

            public bool Matches(string path)
            {
                if (_pattern.Length == 0 || path == null) return false;
                if (_plain)
                {
                    if (string.Equals(path, _pattern, StringComparison.OrdinalIgnoreCase))
                        return true;
                    // Check if path is a child of the excluded path
                    return path.Length > _pattern.Length && path[_pattern.Length] == '/' &&
                           path.StartsWith(_pattern, StringComparison.OrdinalIgnoreCase);
                }
                return MatchFrom(path.Split('/'), 0, 0);
            }

            private bool MatchFrom(string[] path, int pathIdx, int patternIdx)
            {
                // Base cases
                if (patternIdx >= _parts.Length)
                    return pathIdx >= path.Length;

                string patternPart = _parts[patternIdx];

                if (patternPart == "**")
                {
                    // ** matches zero or more path segments
                    // Try matching rest of pattern at every remaining position
                    for (int i = pathIdx; i <= path.Length; i++)
                    {
                        if (MatchFrom(path, i, patternIdx + 1))
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
                    return MatchFrom(path, pathIdx + 1, patternIdx + 1);
                }

                var name = _names[patternIdx];
                if (name != null)
                {
                    if (!name.IsMatch(pathPart))
                        return false;
                }
                else
                {
                    // Exact match (case-insensitive)
                    if (!string.Equals(pathPart, patternPart, StringComparison.OrdinalIgnoreCase))
                        return false;
                }

                return MatchFrom(path, pathIdx + 1, patternIdx + 1);
            }
        }

        /// <summary>
        /// How precisely a pattern names a place — what decides, between two font rules matching the
        /// same text, which one wins (user, 2026-10-22: « les autres textes utilisent les règles
        /// globales en fonction de la profondeur »). Greater is more precise; compare with
        /// <see cref="ComparePrecision"/>, in this order:
        /// <list type="number">
        /// <item>how many levels it names outright — <c>doc/page 1/Prestation</c> before <c>doc/page 1/**</c> before <c>doc/**</c>;</item>
        /// <item>then anchored at the root before floating (<c>**/…</c>), for the same names;</item>
        /// <item>then the fewer wildcards — so an exact path is the most precise of its depth.</item>
        /// </list>
        /// A name with a <c>*</c> inside (<c>Chat*</c>) is a wildcard, not a named level.
        /// </summary>
        public static int ComparePrecision(string a, string b)
        {
            Measure(a ?? "", out int namedA, out bool anchoredA, out int wildA);
            Measure(b ?? "", out int namedB, out bool anchoredB, out int wildB);
            if (namedA != namedB) return namedA.CompareTo(namedB);
            if (anchoredA != anchoredB) return anchoredA ? 1 : -1;
            return wildB.CompareTo(wildA);
        }

        private static void Measure(string pattern, out int named, out bool anchored, out int wild)
        {
            named = 0; wild = 0;
            var parts = pattern.Split('/');
            anchored = parts.Length > 0 && parts[0] != "**" && parts[0] != "*";
            foreach (var part in parts)
            {
                if (part.Length == 0) continue;
                if (part.Contains("*")) wild++;
                else named++;
            }
        }
    }
}
