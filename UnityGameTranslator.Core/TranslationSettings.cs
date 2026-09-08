using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    // The six sections themselves — their names, their order, the key each one uses in the file,
    // what they are called on screen and what becomes of one when both sides have moved — live in
    // Common.SettingsSections. They were here, in C#, while the website held the same list in PHP,
    // with a comment asking whoever touched one to remember the other.
    //
    // What stays here is everything that needs the DOCUMENT: reading a section out of a JObject,
    // comparing two of them, counting what they hold. The socle has no way to hold JSON, on
    // purpose — so the comparing is done here and its answers are handed to Common.SettingsSections
    // .Classify, which is the rule.

    /// <summary>
    /// The online settings a translation can be put back to, and how far it has drifted from
    /// them. Built by TranslatorCore.GetOnlineSettingsReference.
    /// </summary>
    public class SettingsReference
    {
        /// <summary>The settings held by the online version.</summary>
        public TranslationSettings Settings { get; private set; }

        /// <summary>Who they belong to, in the player's words ("@alice's version").</summary>
        public string Label { get; private set; }

        /// <summary>Sections where we no longer match, in display order.</summary>
        public List<string> DifferingSections { get; private set; }

        public bool HasDifferences { get { return DifferingSections.Count > 0; } }

        public static SettingsReference Build(TranslationSettings reference, string label, TranslationSettings ours)
        {
            return new SettingsReference
            {
                Settings = reference,
                Label = label,
                DifferingSections = (ours ?? TranslationSettings.Empty()).SectionsDifferingFrom(reference),
            };
        }
    }

    /// <summary>One section's situation, ready to be shown in a list.</summary>
    public class SettingsSectionPlan
    {
        public string Section { get; set; }
        public SettingsSectionState State { get; set; }
        public int OursCount { get; set; }
        public int TheirsCount { get; set; }

        /// <summary>Does this one need the player? Everything else decides itself.</summary>
        public bool NeedsDecision
        {
            get { return SettingsSections.NeedsDecision(State); }
        }

        public string DisplayName { get { return SettingsSections.Name(Section); } }
        public string Description { get { return SettingsSections.Description(Section); } }
    }

    /// <summary>
    /// What should happen to each settings section when incoming content is
    /// about to replace ours: the three documents compared, section by section,
    /// and each comparison handed to the socle to be read.
    ///
    /// ⚠ The RULE — what "both moved" and "no baseline" mean, and which of the
    /// five answers has to reach the player — is
    /// <see cref="SettingsSections.Classify"/>. What is here is the comparing,
    /// which needs a JSON document the socle cannot hold.
    /// </summary>
    public class SettingsSyncPlan
    {
        public List<SettingsSectionPlan> Sections { get; private set; }

        /// <summary>Sections the player has to arbitrate.</summary>
        public List<SettingsSectionPlan> Decisions
        {
            get { return Sections.Where(s => s.NeedsDecision).ToList(); }
        }

        /// <summary>Sections to take from the incoming side without asking.</summary>
        public List<string> AutoAccepted
        {
            get
            {
                return Sections
                    .Where(s => s.State == SettingsSectionState.TheirsChanged)
                    .Select(s => s.Section)
                    .ToList();
            }
        }

        public bool NeedsPlayer { get { return Decisions.Count > 0; } }

        private SettingsSyncPlan(List<SettingsSectionPlan> sections)
        {
            Sections = sections;
        }

        /// <summary>
        /// Compare what we hold, what is arriving, and what both agreed on last
        /// time (null when that is unknown).
        /// </summary>
        public static SettingsSyncPlan Build(TranslationSettings ours, TranslationSettings theirs, TranslationSettings ancestor)
        {
            ours = ours ?? TranslationSettings.Empty();
            theirs = theirs ?? TranslationSettings.Empty();

            var plans = new List<SettingsSectionPlan>();
            foreach (var section in SettingsSections.All)
            {
                var plan = new SettingsSectionPlan
                {
                    Section = section,
                    OursCount = ours.CountOf(section),
                    TheirsCount = theirs.CountOf(section),
                    State = Classify(ours, theirs, ancestor, section),
                };
                plans.Add(plan);
            }

            return new SettingsSyncPlan(plans);
        }

        /// <summary>
        /// The three comparisons this program can make, handed to the socle to be read.
        ///
        /// ⚠ The ancestor's comparisons are only asked when there IS one; with none, what they
        /// would have answered is not merely unknown, it is unanswerable, and Classify is told so
        /// rather than being given a made-up false.
        /// </summary>
        private static SettingsSectionState Classify(
            TranslationSettings ours, TranslationSettings theirs, TranslationSettings ancestor, string section)
        {
            bool hasAncestor = ancestor != null;

            return SettingsSections.Classify(
                oursMatchesTheirs: ours.SameSectionAs(theirs, section),
                hasAncestor: hasAncestor,
                oursMatchesAncestor: hasAncestor && ours.SameSectionAs(ancestor, section),
                theirsMatchesAncestor: hasAncestor && theirs.SameSectionAs(ancestor, section));
        }
    }

    /// <summary>
    /// An immutable snapshot of the six settings sections, from a file or from
    /// what the mod currently holds in memory.
    ///
    /// Exists so settings can be COMPARED and replaced section by section.
    /// Before it, downloading replaced them wholesale and merging ignored them
    /// entirely, both without asking — see
    /// analyse/metadata-visibility-and-sync.md.
    /// </summary>
    public class TranslationSettings
    {
        // section name -> raw token, or null when the section is absent.
        // Absent and empty mean the same thing: the mod only writes a section
        // when it has content.
        private readonly Dictionary<string, JToken> _sections;

        private TranslationSettings(Dictionary<string, JToken> sections)
        {
            _sections = sections;
        }

        public static TranslationSettings Empty()
        {
            return new TranslationSettings(new Dictionary<string, JToken>());
        }

        /// <summary>Read the sections out of a parsed translations.json.</summary>
        public static TranslationSettings FromFile(JObject file)
        {
            var sections = new Dictionary<string, JToken>();
            if (file != null)
            {
                foreach (var section in SettingsSections.All)
                {
                    var token = file[SettingsSections.JsonKey(section)];
                    if (!IsEmptyToken(token))
                    {
                        sections[section] = token.DeepClone();
                    }
                }
            }

            return new TranslationSettings(sections);
        }

        public static TranslationSettings FromJsonText(string json)
        {
            try
            {
                return FromFile(JObject.Parse(json ?? string.Empty));
            }
            catch (Exception)
            {
                // A file we cannot read carries no settings we can honour;
                // callers treat this as "nothing to offer", never as an error
                return Empty();
            }
        }

        /// <summary>What the mod holds right now, in the same shape as a file.</summary>
        public static TranslationSettings FromCurrentState()
        {
            var sections = new Dictionary<string, JToken>();
            foreach (var section in SettingsSections.All)
            {
                var token = TranslatorCore.BuildSettingsSection(section);
                if (!IsEmptyToken(token))
                {
                    sections[section] = token;
                }
            }

            return new TranslationSettings(sections);
        }

        public JToken Section(string section)
        {
            JToken token;
            return _sections.TryGetValue(section, out token) ? token : null;
        }

        public bool HasAny()
        {
            return _sections.Count > 0;
        }

        /// <summary>
        /// How many entries this section holds. For fonts this counts only the
        /// DELIBERATE ones: FontManager records every font it meets in-game, so
        /// the raw entry count measures how much of the game a player walked
        /// through, not what anyone configured.
        /// </summary>
        public int CountOf(string section)
        {
            var token = Section(section);
            if (token == null) return 0;

            if (section == SettingsSections.Fonts)
            {
                return DeliberateFonts(token as JObject).Count;
            }

            var array = token as JArray;
            if (array != null) return array.Count;

            var obj = token as JObject;
            return obj != null ? obj.Properties().Count() : 0;
        }

        /// <summary>Does this section hold exactly the same thing on both sides?</summary>
        public bool SameSectionAs(TranslationSettings other, string section)
        {
            var mine = Canonical(section, Section(section));
            var theirs = Canonical(section, other == null ? null : other.Section(section));

            return string.Equals(mine, theirs, StringComparison.Ordinal);
        }

        /// <summary>Sections that differ, in display order.</summary>
        public List<string> SectionsDifferingFrom(TranslationSettings other)
        {
            return SettingsSections.All.Where(s => !SameSectionAs(other, s)).ToList();
        }

        /// <summary>
        /// Write these sections into a file object, removing the ones this
        /// snapshot does not have. Used to store settings alongside an ancestor.
        /// </summary>
        public void WriteInto(JObject target)
        {
            if (target == null) return;

            foreach (var section in SettingsSections.All)
            {
                string key = SettingsSections.JsonKey(section);
                var token = Section(section);
                if (token == null)
                {
                    target.Remove(key);
                }
                else
                {
                    target[key] = token.DeepClone();
                }
            }
        }

        /// <summary>
        /// Apply the given sections to the running mod, replacing what it holds.
        /// Only the listed sections are touched; everything else is left alone.
        /// The caller saves — this never writes to disk.
        /// </summary>
        public void ApplySections(IEnumerable<string> sections)
        {
            if (sections == null) return;

            var applied = new List<string>();
            foreach (var section in sections)
            {
                if (Array.IndexOf(SettingsSections.All, section) < 0)
                {
                    TranslatorCore.LogWarning($"[Settings] Ignoring unknown section '{section}'");
                    continue;
                }

                TranslatorCore.ApplySettingsSection(section, Section(section));
                applied.Add(section);
            }

            if (applied.Count > 0)
            {
                TranslatorCore.AfterSettingsSectionsChanged(applied);
            }
        }

        // ── Comparison helpers ───────────────────────────────────────────────

        /// <summary>
        /// A stable text form of a section, so equality does not depend on key
        /// order or on the order of entries whose order carries no meaning.
        /// </summary>
        private static string Canonical(string section, JToken token)
        {
            if (IsEmptyToken(token)) return string.Empty;

            if (section == SettingsSections.Fonts)
            {
                // Compare deliberate settings only (see CountOf)
                var deliberate = new JObject();
                foreach (var kvp in DeliberateFonts(token as JObject))
                {
                    deliberate[kvp.Key] = kvp.Value;
                }
                return Sort(deliberate).ToString(Newtonsoft.Json.Formatting.None);
            }

            // Font rules are matched first-wins, so their order IS the setting
            // and must not be sorted away. Every other list is a set.
            bool orderMatters = section == SettingsSections.FontRules;

            return Sort(token, sortArrays: !orderMatters).ToString(Newtonsoft.Json.Formatting.None);
        }

        private static JToken Sort(JToken token, bool sortArrays = false)
        {
            var obj = token as JObject;
            if (obj != null)
            {
                var sorted = new JObject();
                foreach (var prop in obj.Properties().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    sorted[prop.Name] = Sort(prop.Value, sortArrays);
                }
                return sorted;
            }

            var array = token as JArray;
            if (array != null)
            {
                var items = array.Select(i => Sort(i, sortArrays));
                if (sortArrays)
                {
                    items = items.OrderBy(i => i.ToString(Newtonsoft.Json.Formatting.None), StringComparer.Ordinal);
                }
                return new JArray(items);
            }

            return token;
        }

        /// <summary>
        /// Fonts carrying an actual setting. Mirrors the website's
        /// Translation::isDeliberateFontSetting — the two must agree, otherwise
        /// the site and the mod would report different numbers for one file.
        /// </summary>
        private static Dictionary<string, JToken> DeliberateFonts(JObject fonts)
        {
            var result = new Dictionary<string, JToken>();
            if (fonts == null) return result;

            foreach (var prop in fonts.Properties())
            {
                var settings = prop.Value as JObject;
                if (settings == null) continue;

                if (IsDeliberate(settings))
                {
                    result[prop.Name] = prop.Value;
                }
            }

            return result;
        }

        /// <summary>
        /// Did a HUMAN configure this font? Mirrors Translation::isDeliberateFontSetting on the
        /// website — the two must give the same answer for the same file, or the site and the mod
        /// report different numbers for it.
        ///
        /// The subtlety is the size. "scale" is the MATERIALIZED product (automatic design-scale ×
        /// deliberate percent) and "scale_auto" is switched on by the mod itself the first time it
        /// meets a TMP font, so neither says anything about intent — reading them as deliberate
        /// counted every font ever seen in game as configured, which is exactly what this filter
        /// exists to avoid. Only "size_percent" records a choice.
        /// </summary>
        private static bool IsDeliberate(JObject settings)
        {
            bool disabled = settings["enabled"] != null && settings["enabled"].Type == JTokenType.Boolean
                            && settings["enabled"].Value<bool>() == false;
            bool hasFallback = settings["fallback"] != null
                               && settings["fallback"].Type != JTokenType.Null
                               && !string.IsNullOrEmpty(settings["fallback"].Value<string>());
            if (disabled || hasFallback) return true;

            var sizePercent = settings["size_percent"];
            if (sizePercent != null && sizePercent.Type != JTokenType.Null)
            {
                return Math.Abs(sizePercent.Value<float>() - 1f) > 0.001f;
            }

            // Older files predate the split: "scale" then held the deliberate percent, but only
            // when the automatic scaling was off — otherwise it is polluted by it.
            var scaleAuto = settings["scale_auto"];
            if (scaleAuto != null && scaleAuto.Type == JTokenType.Boolean && scaleAuto.Value<bool>())
            {
                return false;
            }

            var scale = settings["scale"];
            if (scale == null || scale.Type == JTokenType.Null) return false;

            return Math.Abs(scale.Value<float>() - 1f) > 0.001f;
        }

        private static bool IsEmptyToken(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return true;

            var array = token as JArray;
            if (array != null) return array.Count == 0;

            var obj = token as JObject;
            if (obj != null) return !obj.Properties().Any();

            return false;
        }
    }
}
