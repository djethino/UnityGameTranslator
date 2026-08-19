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

        /// <summary>
        /// The five segments in drawing order, kept so the ends can be rounded.
        ///
        /// The site and the Manager both draw this bar as a pill, and a pill made of five abutting
        /// pieces has to round the OUTER edges of the first and last visible ones — not each piece,
        /// which would notch the bar at every junction. Which pieces those are changes with the
        /// counts, so it is settled in SetCounts rather than here.
        /// </summary>
        private readonly List<Image> _segments = new List<Image>();
        private readonly List<LayoutElement> _segmentLayouts = new List<LayoutElement>();
        private int _height = DefaultHeight;

        /// <summary>The bar container. Null until CreateUI has run.</summary>
        public GameObject Root => _root;

        /// <summary>
        /// Build the bar inside <paramref name="parent"/>. It takes all available width and the
        /// requested height, and nothing else.
        /// </summary>
        public void CreateUI(GameObject parent, int height = DefaultHeight)
        {
            _height = height;

            // NO track behind the segments, and this is why the colour is passed HERE rather than
            // cleared afterwards. CreateHorizontalGroup always fits an Image, and when no colour
            // is given it uses UniverseLib's default background AND its default padding — which
            // is what drew a dark slab a little taller than the bar and held the segments away
            // from its edges. UIStyles.Transparent rather than Color.clear on purpose: the
            // factory treats a fully-zero colour as "no colour given" and puts the defaults back.
            //
            // The bar has no unfilled remainder to show anyway: the five shares always add up to
            // everything that was captured, and what is left to do is the grey segment, not an
            // empty gutter.
            _root = UIFactory.CreateHorizontalGroup(parent, "QualityBar", false, false, true, true,
                0, default, UIStyles.Transparent);
            UIFactory.SetLayoutElement(_root, minHeight: height, preferredHeight: height,
                flexibleWidth: 9999, flexibleHeight: 0);

            // Order matters: everything settled first, what remains to do last. The grey always
            // ends the bar, so its length reads as the work left without doing any arithmetic.
            //
            // ⚠ The Quality* keys, NOT the Status* ones. This bar used to borrow "it went well" for
            // the human share and "careful" for the AI share, which is how the same file came out
            // amber here and orange on the website — a measure that reads differently depending on
            // where you look at it is not a measure. See UIStyles for the full account.
            _humanLayout = CreateSegment("HumanBar", UIStyles.QualityHuman, height);
            _validatedLayout = CreateSegment("ValidatedBar", UIStyles.QualityValidated, height);
            _aiLayout = CreateSegment("AiBar", UIStyles.QualityAi, height);
            _keptLayout = CreateSegment("KeptBar", UIStyles.QualityKept, height);
            _captureLayout = CreateSegment("CaptureBar", UIStyles.QualityCapture, height);
        }

        private LayoutElement CreateSegment(string name, Color color, int height)
        {
            var obj = UIFactory.CreateUIObject(name, _root);
            var image = obj.AddComponent<Image>();
            image.color = color;
            _segments.Add(image);
            // Full height, not height - 2: the missing pixels used to sit at one edge, which is
            // what made the bar look misaligned with what was behind it. Sized only by their
            // share (flexibleWidth), set in SetCounts.
            var layout = UIFactory.SetLayoutElement(obj, minHeight: height, flexibleWidth: 0);
            _segmentLayouts.Add(layout);
            return layout;
        }

        /// <summary>
        /// Round the two ends of the bar, and only those.
        ///
        /// `rounded-full` on the site, <c>CornerRadius = height / 2</c> in the Manager. Here the bar
        /// is five abutting pieces, so the radius goes on the outer edge of the first visible piece
        /// and of the last — the ones in between stay square or the bar would be notched at every
        /// junction. A single visible share gets both ends, i.e. a full pill.
        ///
        /// Zero-width segments are skipped: a share of nothing still exists in the layout, and
        /// rounding it would put the curve somewhere invisible while the real end stayed square.
        /// </summary>
        private void RoundEnds()
        {
            int first = -1, last = -1;
            for (int i = 0; i < _segmentLayouts.Count; i++)
            {
                if (_segmentLayouts[i] == null || _segmentLayouts[i].flexibleWidth <= 0f) continue;
                if (first < 0) first = i;
                last = i;
            }

            int radius = Mathf.Max(1, _height / 2);

            for (int i = 0; i < _segments.Count; i++)
            {
                if (_segments[i] == null) continue;

                Corners corners = Corners.None;
                if (i == first) corners |= Corners.Left;
                if (i == last) corners |= Corners.Right;

                // A middle piece is left exactly as it was: square, and sharing no texture change
                // with its neighbours.
                if (corners == Corners.None)
                {
                    _segments[i].sprite = null;
                    continue;
                }

                UIFactory.SetShape(_segments[i], UIShapes.Rounded(radius, corners));
            }
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

            // Which segments show has just changed, so which ones carry the curve has too.
            RoundEnds();

            return total > 0;
        }

        /// <summary>Show or hide the bar.</summary>
        public void SetVisible(bool visible)
        {
            if (_root != null) _root.SetActive(visible);
        }

        /// <summary>
        /// The colour key with each share as a whole percent. Rounding is absorbed by the last
        /// entry so the percentages always read 100.
        ///
        /// NO line break is placed here, and that is the point. The panel is resizable, so any
        /// rule of the form "N entries per line" is a guess about a width that changes underneath
        /// it: it forces a break where there was room, and fails to prevent one where there was
        /// not.
        ///
        /// Instead each entry is welded together with non-breaking spaces, and the ONLY ordinary
        /// space in the whole string is the one between two entries. Unity then wraps wherever it
        /// needs to — which can now only be between entries, so every line begins with a swatch
        /// followed by the words that belong to it. Widen the panel and the key gathers onto one
        /// line by itself; narrow it and it lays itself out again.
        ///
        /// Left to its own devices Unity breaks at whatever space runs out first, which is how a
        /// swatch ended one line while its label started the next, and how "Kept as is" was cut
        /// after "as". A key whose squares have drifted from their words is not a key any more.
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

            // Same keys as the bar itself, or the key would name colours the bar does not show.
            var entries = new List<string>
            {
                Entry(UIStyles.QualityHuman, "Human", humanPct),
                Entry(UIStyles.QualityValidated, "Validated", validatedPct),
                Entry(UIStyles.QualityAi, "AI", aiPct),
            };

            // The last two are mentioned only when there are some: a permanent "Captured 0%" is
            // noise, and each absence is itself the information.
            if (kept > 0) entries.Add(Entry(UIStyles.QualityKept, "Kept as is", keptPct));
            if (capture > 0) entries.Add(Entry(UIStyles.QualityCapture, "Captured", capturePct));

            // Three ordinary spaces: enough to separate two entries, and the only place in the
            // string where a line is allowed to break.
            return string.Join("   ", entries.ToArray());
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
            return entry.Replace(' ', '\u00A0');
        }

        private static string Swatch(Color color)
        {
            // ⚠ UIStyles.Hex, never ColorUtility.ToHtmlStringRGB — the latter is stripped by some
            // games and killed the whole panel pass there. See the note on UIStyles.Hex.
            return $"<color=#{UIStyles.Hex(color)}>■</color>";
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
