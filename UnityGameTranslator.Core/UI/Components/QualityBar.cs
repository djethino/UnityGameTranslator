using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// What a translation is MADE OF — human, validated, AI, kept as is, and captured but not
    /// dealt with yet.
    ///
    /// Deliberately not a progress bar: a game's total line count is unknowable (text is captured
    /// as it is met, and branching games are never fully walked), so there is no denominator to
    /// measure progress against. The denominator is everything that WAS captured, and the labels
    /// never say "progress".
    ///
    /// S (kept as is) is part of that denominator and owns a segment. It was met, read, and
    /// settled by a decision — leaving a fictional language untouched is an answer, not an
    /// omission — so it belongs with the work done, never in the grey. What it must NOT do is
    /// share the grey or fall outside the total: the first would count care as missing work, the
    /// second would make the bar describe only part of the file while claiming to describe it all.
    ///
    /// M (mod UI) is technical noise and appears nowhere.
    ///
    /// Same five segments, same colours and same denominator as the website's x-progress-bar, so
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
        private LayoutElement _keptLayout;
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
            // container, not as one more (dark) category.
            UIStyles.SetBackground(_root, UIStyles.ViewportBackground);

            // Order matters: everything settled first, what remains to do last. The grey always
            // ends the bar, so its length reads as the work left without doing any arithmetic.
            _humanLayout = CreateSegment("HumanBar", UIStyles.StatusSuccess, height);
            _validatedLayout = CreateSegment("ValidatedBar", UIStyles.StatusInfo, height);
            _aiLayout = CreateSegment("AiBar", UIStyles.StatusWarning, height);
            _keptLayout = CreateSegment("KeptBar", UIStyles.StatusKept, height);
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
        /// Set the five shares. Returns false when there is nothing to show (nothing captured at
        /// all) — callers use it to hide the row rather than display an empty bar.
        /// </summary>
        public bool SetCounts(int human, int validated, int ai, int kept, int capture)
        {
            int total = human + validated + ai + kept + capture;
            if (_root == null) return total > 0;

            // Proportions, not pixels: the layout divides the width by these weights, so the bar
            // stays right whatever the panel size.
            if (_humanLayout != null) _humanLayout.flexibleWidth = human;
            if (_validatedLayout != null) _validatedLayout.flexibleWidth = validated;
            if (_aiLayout != null) _aiLayout.flexibleWidth = ai;
            if (_keptLayout != null) _keptLayout.flexibleWidth = kept;
            if (_captureLayout != null) _captureLayout.flexibleWidth = capture;

            return total > 0;
        }

        /// <summary>Show or hide the bar.</summary>
        public void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        /// <summary>
        /// How many entries share one line of the colour key.
        ///
        /// Three fit across a card when there are only three — the whole key on one line, which
        /// is what a file with nothing kept and nothing left captured deserves. Add a fourth
        /// ("Kept as is", the longest of them all) and the line overflows, so they pair up.
        /// </summary>
        private static int EntriesPerLine(int entries)
        {
            return entries <= 3 ? 3 : 2;
        }

        /// <summary>
        /// The colour key with each share as a whole percent. Rounding is absorbed by the last
        /// entry so the percentages always read 100.
        ///
        /// Two mechanisms, and BOTH are needed. Line breaks are placed explicitly, so the number
        /// of lines is known in advance and the row can be measured for them. And every space
        /// INSIDE an entry is non-breaking, so that if a line still runs past the available
        /// width — a narrower panel, a longer translation of "Kept as is" — Unity can only break
        /// between entries. Left to itself it breaks at whatever space runs out first, which is
        /// how a swatch ended one line while the word it labels started the next, and how "Kept
        /// as is" got cut after "as". A key whose squares have drifted from their words is not
        /// a key any more.
        /// </summary>
        public static string BuildLegend(int human, int validated, int ai, int kept, int capture)
        {
            int total = human + validated + ai + kept + capture;
            if (total <= 0) return string.Empty;

            int humanPct = Mathf.RoundToInt(human * 100f / total);
            int validatedPct = Mathf.RoundToInt(validated * 100f / total);
            int aiPct = Mathf.RoundToInt(ai * 100f / total);
            int keptPct = Mathf.RoundToInt(kept * 100f / total);
            int capturePct = 100 - humanPct - validatedPct - aiPct - keptPct;

            var entries = new List<string>
            {
                Entry(UIStyles.StatusSuccess, "Human", humanPct),
                Entry(UIStyles.StatusInfo, "Validated", validatedPct),
                Entry(UIStyles.StatusWarning, "AI", aiPct),
            };

            // The last two are mentioned only when there are some: a permanent "Captured 0%" is
            // noise, and each absence is itself the information.
            if (kept > 0) entries.Add(Entry(UIStyles.StatusKept, "Kept as is", keptPct));
            if (capture > 0) entries.Add(Entry(UIStyles.StatusNeutral, "Captured", capturePct));

            int perLine = EntriesPerLine(entries.Count);

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < entries.Count; i++)
            {
                // The separator between entries is the ONLY ordinary space in the whole string,
                // and therefore the only place Unity is able to break a line by itself.
                if (i > 0) sb.Append(i % perLine == 0 ? "\n" : "   ");
                sb.Append(entries[i]);
            }

            return sb.ToString();
        }

        /// <summary>
        /// One colour key entry: its swatch, its name, its share — held together by non-breaking
        /// spaces so the three can never end up on different lines. The name is translated first:
        /// a language whose word for "Kept as is" is three words long must be protected too.
        /// </summary>
        private static string Entry(Color color, string label, int percent)
        {
            string entry = Swatch(color) + " " + TranslatorCore.TranslateOwnUIDynamic(label) + $" {percent}%";

            // Escaped, never the literal character: a non-breaking space is invisible in
            // an editor, and the first well-meaning cleanup would quietly turn it back
            // into an ordinary one, taking the protection with it.
            return entry.Replace(' ', ' ');
        }

        /// <summary>
        /// How many lines <see cref="BuildLegend"/> will produce for these counts. Callers size
        /// their row with it — a row measured for one line simply clips the rest.
        /// </summary>
        public static int LegendLineCount(int kept, int capture)
        {
            int entries = 3 + (kept > 0 ? 1 : 0) + (capture > 0 ? 1 : 0);
            int perLine = EntriesPerLine(entries);

            return (entries + perLine - 1) / perLine;
        }

        private static string Swatch(Color color)
        {
            return $"<color=#{ColorUtility.ToHtmlStringRGB(color)}>■</color>";
        }

        /// <summary>
        /// "Kept as is: 312", or null when there are none. Same wording as the website's
        /// progress.skipped, and count-last so no language needs a plural form.
        ///
        /// For the places that show the bar without a colour key (the download cards): the purple
        /// segment alone says nothing. Wording stays factual — we cannot read the author's intent
        /// (an S can also mean "I will deal with it later"), so it states what happened to the
        /// line, never why.
        /// </summary>
        public static string KeptLabel(int kept)
        {
            if (kept <= 0) return null;
            return TranslatorCore.TranslateOwnUIDynamic("Kept as is") + $": {kept}";
        }
    }
}
