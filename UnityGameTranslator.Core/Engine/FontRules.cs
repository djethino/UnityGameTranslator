using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Per-font settings for translation control and fallback fonts.
    /// Stored in translations.json as _font_overrides.
    /// Rules are evaluated in order — first match wins.
    /// </summary>
    public class FontOverrideRule
    {
        /// <summary>
        /// Pattern to match. Prefixes: "path:" (hierarchy glob), "font:" (font name), "text:" (content, regex if /.../).
        /// Without prefix: tries path first, then text substring.
        /// </summary>
        public string match { get; set; }

        // ── Runtime state, never part of the file ────────────────────────────────────────────
        // Internal fields, so no serializer writes them and the parser does not read them: a rule
        // object is rebuilt from the file on every load and by the Fonts tab on every edit, which
        // is exactly when this state should start over.

        /// <summary>The compiled <c>text:/…/</c> pattern, once TextRegexCompiled is set; null if invalid.</summary>
        internal System.Text.RegularExpressions.Regex TextRegex;
        internal bool TextRegexCompiled;

        /// <summary>
        /// Left out for the rest of this run — an invalid pattern, or one that ran past
        /// TextRule.Budget. Distinct from <see cref="enabled"/> on purpose: that one is the author's
        /// choice and is saved; this is a verdict on one session and never is.
        /// </summary>
        internal bool SwitchedOffThisSession;

        /// <summary>
        /// Replacement font name. Null = keep current font (only override size).
        /// </summary>
        public string replacement { get; set; }

        /// <summary>
        /// Size multiplier override. 0 = don't override (use global setting).
        /// Example: 1.0 = original size, 1.5 = 150%, 0.7 = 70%.
        /// </summary>
        public float size_multiplier { get; set; } = 0f;

        /// <summary>
        /// Whether this rule is active.
        /// </summary>
        public bool enabled { get; set; } = true;

        /// <summary>
        /// User comment for identifying the rule purpose.
        /// </summary>
        public string comment { get; set; }

        /// <summary>
        /// RTL alignment behaviour for the matched components: null = inherit the font's
        /// setting, "mirror" or "keep". Exists because one game mixes both needs (a description
        /// pane that mirrors fine next to buttons whose boxes were built for one side —
        /// user-arbitrated on the bench).
        /// </summary>
        public string rtl_alignment { get; set; }
    }

    /// <summary>
    /// Which font rule applies to a given label, and what has already been decided for each target.
    ///
    /// 🔴 **Why this is stateful, like the exclusions.** The answer is taken once per target and
    /// kept, because it is asked on every text write. And there is a second memory that matters
    /// more: a rule whose pattern is invalid, or which ran past its time budget, is switched off
    /// **for the rest of the run** — a verdict, said once, that must not be written back to the
    /// file. Both are sequences, and neither can be seen from a single question.
    ///
    /// ⚠ **The time budget is not a nicety.** A <c>text:/…/</c> pattern arrives in a downloaded
    /// translation and runs on the main thread against every label of every frame. One built to
    /// backtrack froze the game outright, and an invalid one was swallowed in silence. So the two
    /// verdicts are announced, once each, naming the rule and where to fix it.
    ///
    /// ⚠ **Pure by contract, and the warnings are an injected sink** rather than a call into the
    /// mod's log. That is what lets the checks read what was said, and how often — the "once" is
    /// half the rule, and a rule announcing itself on every frame is its own defect.
    ///
    /// ⚠ The pattern grammar for <c>path:</c> is <see cref="ExclusionPatterns"/>, shared with the
    /// exclusions: one syntax, one reader. The budget and the compilation live in
    /// <see cref="TextRule"/>, shared with everything else that runs a downloaded pattern.
    ///
    /// Cut out of TranslatorCore on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md).
    /// </summary>
    public sealed class FontRules
    {
        private readonly List<FontOverrideRule> _rules = new List<FontOverrideRule>();

        /// <summary>
        /// ⚠ Keyed by long, like every other per-target map: uGUI passes an instance id, UI Toolkit
        /// passes an id from beyond the int range. Holds nulls too — "nothing matches this one" is
        /// an answer worth keeping, or every ordinary label pays the whole list on every write.
        /// </summary>
        private readonly Dictionary<long, FontOverrideRule> _decided = new Dictionary<long, FontOverrideRule>();

        /// <summary>
        /// Where the two per-session verdicts go: an invalid pattern, and one that ran too long.
        ///
        /// ⚠ Left null in a check that does not care; never called more than once per rule either
        /// way, which is the part worth checking.
        /// </summary>
        public Action<string> Warn { get; set; }

        /// <summary>The rules, in the order they are evaluated. Read-only for the UI.</summary>
        public IReadOnlyList<FontOverrideRule> Rules => _rules;

        /// <summary>True when any rule exists at all. Asked before a path is built.</summary>
        public bool Any => _rules.Count > 0;

        /// <summary>
        /// The first rule that matches this target, or null when none does — remembered either way.
        ///
        /// ⚠ First match wins, so the order in the file is a decision its author made.
        /// </summary>
        public FontOverrideRule Find(long targetId, string path, string fontName, string text)
        {
            if (_decided.TryGetValue(targetId, out var cached))
                return cached;

            FontOverrideRule matched = null;
            for (int i = 0; i < _rules.Count; i++)
            {
                var rule = _rules[i];
                if (!rule.enabled || rule.SwitchedOffThisSession) continue;
                if (Matches(rule, path, fontName, text))
                {
                    matched = rule;
                    break; // First match wins
                }
            }

            _decided[targetId] = matched;
            return matched;
        }

        /// <summary>
        /// Whether one rule matches this context, with nothing remembered.
        ///
        /// ⚠ Not const-pure: a <c>text:/…/</c> rule compiles its pattern on first use and may
        /// switch ITSELF off for the run. That is the point — the verdict belongs to the rule, not
        /// to the target that happened to trigger it.
        /// </summary>
        public bool Matches(FontOverrideRule rule, string path, string fontName, string text)
        {
            string match = rule.match;
            if (string.IsNullOrEmpty(match)) return false;

            // Prefix-based matching
            if (match.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = match.Substring(5);
                return !string.IsNullOrEmpty(path) && ExclusionPatterns.Matches(path, pattern);
            }
            if (match.StartsWith("font:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = match.Substring(5);
                return !string.IsNullOrEmpty(fontName) &&
                       string.Equals(fontName, pattern, StringComparison.OrdinalIgnoreCase);
            }
            if (match.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
            {
                string pattern = match.Substring(5);
                if (string.IsNullOrEmpty(text)) return false;
                // Regex if wrapped in /.../
                if (pattern.StartsWith("/") && pattern.EndsWith("/") && pattern.Length > 2)
                {
                    // 🔴 Compiled once per rule, matched under TextRule's budget. This used to call
                    // Regex.IsMatch on every component with no limit: a pattern from a downloaded
                    // file built to backtrack froze the game on the main thread, and an invalid one
                    // was swallowed without a word. Both are now said once, naming the rule, and the
                    // rule is left out for the rest of the session — never written back to the file:
                    // `enabled` belongs to the author, this is a verdict on one run.
                    if (!rule.TextRegexCompiled)
                    {
                        rule.TextRegexCompiled = true;
                        rule.TextRegex = TextRule.Compile(pattern.Substring(1, pattern.Length - 2));
                        if (rule.TextRegex == null)
                        {
                            rule.SwitchedOffThisSession = true;
                            Warn?.Invoke($"[FontOverride] Rule \"{match}\" is not a valid pattern; ignored. Check it in the Fonts tab.");
                        }
                    }
                    if (rule.TextRegex == null) return false;

                    switch (TextRule.Match(rule.TextRegex, text))
                    {
                        case TextRule.Outcome.Matched:
                            return true;
                        case TextRule.Outcome.TimedOut:
                            rule.SwitchedOffThisSession = true;
                            Warn?.Invoke($"[FontOverride] Rule \"{match}\" took more than {TextRule.Budget.TotalSeconds:0} s on one text and is switched off until the next launch. Check the pattern in the Fonts tab.");
                            return false;
                        default:
                            return false;
                    }
                }
                return text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            // No prefix: try path first, then text substring
            if (!string.IsNullOrEmpty(path) && ExclusionPatterns.Matches(path, match))
                return true;
            if (!string.IsNullOrEmpty(text) && text.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            return false;
        }

        /// <summary>
        /// Replace every rule at once — an edit in the Fonts tab, or what a translation file says.
        ///
        /// ⚠ Drops the memory too: an answer decided against rules that no longer exist is an
        /// answer to a question nobody is asking any more. Null is an empty list, not a refusal.
        /// </summary>
        public void Load(IEnumerable<FontOverrideRule> rules)
        {
            _rules.Clear();
            if (rules != null) _rules.AddRange(rules);
            ForgetAll();
        }

        /// <summary>
        /// Forget one target. See <see cref="ExclusionRules.Forget"/> for why this exists: a
        /// UI Toolkit element is recycled, and a strong map keyed by id would grow for ever.
        /// </summary>
        public void Forget(long targetId)
        {
            _decided.Remove(targetId);
        }

        /// <summary>Forget every answer — a scene change, or the rules having moved.</summary>
        public void ForgetAll()
        {
            _decided.Clear();
        }
    }
}
