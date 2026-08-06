using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// What a translation is MADE OF — human, validated, AI, and captured-but-not-yet-translated.
    ///
    /// Deliberately not a progress bar: a game's total line count is unknowable (text is captured
    /// as it is met, and branching games are never fully walked), so there is no denominator to
    /// measure progress against. The denominator here is what was captured, nothing more, and the
    /// labels never say "progress".
    ///
    /// M (mod UI) and S (marked as not-to-translate) are excluded from BOTH the segments and the
    /// denominator. Leaving S in would make the author who carefully marked 300 untranslatable
    /// lines look LESS complete than the one who ran the AI over everything — the opposite of the
    /// signal we want. S is worth showing, but on its own, as a fact (see SkippedLabel).
    ///
    /// Same four segments, same colours and same denominator as the website's x-progress-bar, so
    /// a given translation looks identical in the game and in the browser.
    /// </summary>
    public class QualityBar
    {
        /// <summary>Height used inside cards, where the bar is the row's subject.</summary>
        public const int DefaultHeight = 10;

        /// <summary>Height used in dense lists, where the bar rides under a text line.</summary>
        public const int CompactHeight = 6;

        private GameObject _root;
        private LayoutElement _humanLayout;
        private LayoutElement _validatedLayout;
        private LayoutElement _aiLayout;
        private LayoutElement _captureLayout;

        /// <summary>The bar container. Null until CreateUI has run.</summary>
        public GameObject Root => _root;

        /// <summary>
        /// Build the bar inside <paramref name="parent"/>. It takes all available width and the
        /// requested height, and nothing else.
        /// </summary>
        public void CreateUI(GameObject parent, int height = DefaultHeight)
        {
            _root = UIFactory.CreateHorizontalGroup(parent, "QualityBar", false, false, true, true, 0);
            UIFactory.SetLayoutElement(_root, minHeight: height, preferredHeight: height,
                flexibleWidth: 9999, flexibleHeight: 0);
            // The empty bar is the viewport colour: a bar with no data must read as an empty
            // container, not as a fifth (dark) category.
            UIStyles.SetBackground(_root, UIStyles.ViewportBackground);

            _humanLayout = CreateSegment("HumanBar", UIStyles.StatusSuccess, height);
            _validatedLayout = CreateSegment("ValidatedBar", UIStyles.StatusInfo, height);
            _aiLayout = CreateSegment("AiBar", UIStyles.StatusWarning, height);
            _captureLayout = CreateSegment("CaptureBar", UIStyles.StatusNeutral, height);
        }

        private LayoutElement CreateSegment(string name, Color color, int height)
        {
            var obj = UIFactory.CreateUIObject(name, _root);
            var image = obj.AddComponent<Image>();
            image.color = color;
            // Segments are sized only by their share (flexibleWidth), set in SetCounts.
            return UIFactory.SetLayoutElement(obj, minHeight: height - 2, flexibleWidth: 0);
        }

        /// <summary>
        /// Set the four shares. Returns false when there is nothing to show (no captured line at
        /// all) — callers use it to hide the row rather than display an empty bar.
        /// </summary>
        public bool SetCounts(int human, int validated, int ai, int capture)
        {
            int total = human + validated + ai + capture;
            if (_root == null) return total > 0;

            // Proportions, not pixels: the layout divides the width by these weights, so the bar
            // stays right whatever the panel size.
            if (_humanLayout != null) _humanLayout.flexibleWidth = human;
            if (_validatedLayout != null) _validatedLayout.flexibleWidth = validated;
            if (_aiLayout != null) _aiLayout.flexibleWidth = ai;
            if (_captureLayout != null) _captureLayout.flexibleWidth = capture;

            return total > 0;
        }

        /// <summary>Show or hide the bar.</summary>
        public void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        /// <summary>
        /// The colour key with each share as a whole percent. Rounding is absorbed by the last
        /// visible entry so the percentages always read 100.
        /// </summary>
        public static string BuildLegend(int human, int validated, int ai, int capture)
        {
            int total = human + validated + ai + capture;
            if (total <= 0) return string.Empty;

            int humanPct = Mathf.RoundToInt(human * 100f / total);
            int validatedPct = Mathf.RoundToInt(validated * 100f / total);
            int aiPct = Mathf.RoundToInt(ai * 100f / total);
            int capturePct = 100 - humanPct - validatedPct - aiPct;

            string legend =
                Swatch(UIStyles.StatusSuccess) + " " + TranslatorCore.TranslateOwnUIDynamic("Human") + $" {humanPct}%   " +
                Swatch(UIStyles.StatusInfo) + " " + TranslatorCore.TranslateOwnUIDynamic("Validated") + $" {validatedPct}%   " +
                Swatch(UIStyles.StatusWarning) + " " + TranslatorCore.TranslateOwnUIDynamic("AI") + $" {aiPct}%";

            // Only mentioned when there is some: a permanent "Captured 0%" is noise, and its
            // absence is itself the information (everything captured has been translated).
            if (capture > 0)
            {
                legend += "   " + Swatch(UIStyles.StatusNeutral) + " " +
                          TranslatorCore.TranslateOwnUIDynamic("Captured") + $" {capturePct}%";
            }

            return legend;
        }

        private static string Swatch(Color color)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>■</color>";
        }

        /// <summary>
        /// "Marked as not to translate: 312", or null when there are none. Same wording as the
        /// website's progress.skipped_marked, and count-last so no language needs a plural form.
        ///
        /// Kept out of the bar on purpose: marking a line as untranslatable (a fictional language
        /// that must stay untouched, a proper noun) is a human decision and a sign of care, but it
        /// is not a translation. Wording stays factual — we cannot read the author's intent, and
        /// "deliberately excluded" would claim something we do not know.
        /// </summary>
        public static string SkippedLabel(int skipped)
        {
            if (skipped <= 0) return null;
            return TranslatorCore.TranslateOwnUIDynamic("Marked as not to translate") + $": {skipped}";
        }
    }
}
