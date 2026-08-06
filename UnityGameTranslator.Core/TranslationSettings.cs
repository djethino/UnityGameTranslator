using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The settings sections that travel inside translations.json alongside the
    /// lines: fonts, font rules, image replacements, exclusions, variables and
    /// game settings.
    ///
    /// Section names match the website's Translation::SETTINGS_SECTIONS so both
    /// sides name the same things in the same order.
    /// </summary>
    public static class SettingsSection
    {
        public const string Fonts = "fonts";
        public const string FontRules = "font_rules";
        public const string Images = "images";
        public const string Exclusions = "exclusions";
        public const string Variables = "variables";
        public const string GameSettings = "game_settings";

        public static readonly string[] All =
        {
            Fonts, FontRules, Images, Exclusions, Variables, GameSettings
        };

        /// <summary>The key this section uses inside translations.json.</summary>
        public static string JsonKey(string section)
        {
            switch (section)
            {
                case Fonts: return "_fonts";
                case FontRules: return "_font_overrides";
                case Images: return "_image_replacements";
                case Exclusions: return "_exclusions";
                case Variables: return "_variables";
                case GameSettings: return "_settings";
                default: return null;
            }
        }

        /// <summary>Short label shown to the player.</summary>
        public static string DisplayName(string section)
        {
            switch (section)
            {
                case Fonts: return "Fonts";
                case FontRules: return "Font rules";
                case Images: return "Image replacements";
                case Exclusions: return "Exclusions";
                case Variables: return "Variables";
                case GameSettings: return "Game settings";
                default: return section;
            }
        }

        /// <summary>
        /// One line explaining what the player loses or gains by replacing this
        /// section. Shown next to each checkbox — a section name alone does not
        /// let anyone decide.
        /// </summary>
        public static string Description(string section)
        {
            switch (section)
            {
                case Fonts: return "Which fonts are translated, their fallback and their size";
                case FontRules: return "Font substitutions applied by pattern";
                case Images: return "Images swapped in-game (the PNG files stay on your disk)";
                case Exclusions: return "Text left in the game's original language";
                case Variables: return "Game values inserted into translated sentences";
                case GameSettings: return "Per-game options such as typewriter detection";
                default: return string.Empty;
            }
        }
    }

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

    /// <summary>What happened to one settings section since the last sync.</summary>
    public enum SettingsSectionState
    {
        /// <summary>Both sides hold the same thing. Nothing to decide.</summary>
        Same,

        /// <summary>Only the incoming side moved. Take it, silently.</summary>
        TheirsChanged,

        /// <summary>Only we moved. Keep ours, silently.</summary>
        OursChanged,

        /// <summary>Both moved since the shared baseline. Only the player can decide.</summary>
        BothChanged,

        /// <summary>
        /// They differ and there is no shared baseline to attribute the change
        /// to. Indistinguishable from BothChanged in practice — ask.
        /// </summary>
        Unknown,
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
            get { return State == SettingsSectionState.BothChanged || State == SettingsSectionState.Unknown; }
        }

        public string DisplayName { get { return SettingsSection.DisplayName(Section); } }
        public string Description { get { return SettingsSection.Description(Section); } }
    }

    /// <summary>
    /// What should happen to each settings section when incoming content is
    /// about to replace ours.
    ///
    /// The point is to ask RARELY. A section only reaches the player when both
    /// sides moved since the last common state (or when there is no common
    /// state to compare against). Everything else is decided here: an untouched
    /// section takes the incoming value, and a section only we changed keeps
    /// ours. Without this, every download would either ask about six sections or
    /// silently overwrite them — which is what it did before.
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
            foreach (var section in SettingsSection.All)
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

        private static SettingsSectionState Classify(
            TranslationSettings ours, TranslationSettings theirs, TranslationSettings ancestor, string section)
        {
            if (ours.SameSectionAs(theirs, section))
            {
                return SettingsSectionState.Same;
            }

            if (ancestor == null)
            {
                return SettingsSectionState.Unknown;
            }

            bool weMoved = !ours.SameSectionAs(ancestor, section);
            bool theyMoved = !theirs.SameSectionAs(ancestor, section);

            if (weMoved && theyMoved) return SettingsSectionState.BothChanged;
            if (theyMoved) return SettingsSectionState.TheirsChanged;
            if (weMoved) return SettingsSectionState.OursChanged;

            // They differ from each other yet neither differs from the ancestor:
            // impossible unless the comparison is inconsistent. Ask rather than
            // pick a side on a contradiction.
            return SettingsSectionState.Unknown;
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
                foreach (var section in SettingsSection.All)
                {
                    var token = file[SettingsSection.JsonKey(section)];
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
            foreach (var section in SettingsSection.All)
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

            if (section == SettingsSection.Fonts)
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
            return SettingsSection.All.Where(s => !SameSectionAs(other, s)).ToList();
        }

        /// <summary>
        /// Write these sections into a file object, removing the ones this
        /// snapshot does not have. Used to store settings alongside an ancestor.
        /// </summary>
        public void WriteInto(JObject target)
        {
            if (target == null) return;

            foreach (var section in SettingsSection.All)
            {
                string key = SettingsSection.JsonKey(section);
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
                if (Array.IndexOf(SettingsSection.All, section) < 0)
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

            if (section == SettingsSection.Fonts)
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
            bool orderMatters = section == SettingsSection.FontRules;

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
