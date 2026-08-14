using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;
using UniverseLib.UI.Panels;
using UnityGameTranslator.Core.UI.Components;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Base class for all translator panels.
    /// Provides consistent sizing and centering behavior similar to the old IMGUI windows.
    /// Uses UIStyles for centralized theming.
    /// </summary>
    public abstract class TranslatorPanelBase : PanelBase
    {
        #region Style Helpers (delegates to UIStyles)

        /// <summary>
        /// Helper to set background color on a UI group/box.
        /// </summary>
        protected static void SetBackgroundColor(GameObject obj, Color color)
        {
            UIStyles.SetBackground(obj, color);
        }

        /// <summary>
        /// Creates a styled card container with proper padding
        /// </summary>
        protected GameObject CreateCard(GameObject parent, string name, int minHeight = 0)
        {
            return UIStyles.CreateCard(parent, name, minHeight);
        }

        /// <summary>
        /// Creates a styled section box
        /// </summary>
        protected GameObject CreateSection(GameObject parent, string name, int minHeight = 0)
        {
            return UIStyles.CreateSection(parent, name, minHeight);
        }

        /// <summary>
        /// Creates a flexible spacer for vertical centering
        /// </summary>
        protected GameObject CreateFlexSpacer(GameObject parent, string name = "Spacer")
        {
            return UIStyles.CreateFlexSpacer(parent, name);
        }

        /// <summary>
        /// Creates a styled title label
        /// </summary>
        protected Text CreateTitle(GameObject parent, string name, string text)
        {
            return UIStyles.CreateTitle(parent, name, text);
        }

        /// <summary>
        /// A title with the three-position scope switch to its left: which copy this panel writes
        /// to — the published translation, both, or the file in this game.
        ///
        /// ⚠ **On EVERY screen that shows translation lines, without exception.** One missing is
        /// worse than never having had it: the glance stops being a habit, and an absent badge
        /// starts to mean something it does not. That is why this sits on the panel base rather
        /// than being pasted into the panels that happened to be edited.
        ///
        /// ⚠ The positions, their order and their words come from
        /// <see cref="UnityGameTranslator.Common.EditScope"/> — the same answers the manager and
        /// the website draw their own control from. Somebody who learns it in a browser must not
        /// have to relearn it here.
        /// </summary>
        protected Text CreateScopedTitle(GameObject parent, string name, string text, EditSide side)
        {
            // ⚠ Alignment and padding stated, not left to the default. Without them the row packs
            // its children into the top-left corner and flush against the edge — the strip and the
            // title read as stuck in the corner of the bar rather than sitting on its line. The
            // same omission was fixed on the main panel's section title; it was here too.
            var row = UIFactory.CreateHorizontalGroup(parent, name + "Row", false, false, true, true, 4,
                                                      new Vector4(3, 3, UIStyles.SectionPadding,
                                                                  UIStyles.SectionPadding),
                                                      default, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightMedium,
                                       flexibleWidth: 9999, flexibleHeight: 0);
            _scopeRow = row.GetComponent<RectTransform>();

            // 🔴 **ONE box holding the three cells AND the separator.** They were four siblings of
            // the row, counted together in the arithmetic and laid out separately by uGUI — so the
            // reserved width and the drawn width were never quite the same number, and every
            // correction moved the discrepancy somewhere else. As one box the strip has a single
            // width, which can be READ rather than added up, and the mirror can copy it exactly.
            var box = UIFactory.CreateHorizontalGroup(row, name + "Strip", false, false, true, true, 4,
                                                      Vector4.zero, default, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(box, minHeight: UIStyles.RowHeightSmall,
                                       flexibleWidth: 0, flexibleHeight: 0);
            UIStyles.ClearRowBackground(box);
            _scopeStrip = box.GetComponent<RectTransform>();

            // 🔴 **Something is ALWAYS lit.** The chosen side was lit only when it happened to be
            // available, so on any screen where it is not — the published side with nothing of
            // yours published, which is most people — all three positions came out grey and the
            // control said nothing at all. EditScope.Default answers what a screen actually falls
            // back to, and it falls towards the local side, never towards publishing.
            var sides = ScopeSides(side);
            var lit = EditScope.Default(sides, side);

            // 🔴 **The strip takes what is left AFTER the title.** It gives up its words rather
            // than push the title onto a second line — which is what it was doing. Measured before
            // anything is built, so the form is chosen once instead of being noticed after a
            // layout pass and rebuilt, which flickers and can oscillate.
            //
            // ⚠ **These numbers ARE the cell built below — they were not, and that was the defect.**
            // The cell padding went from 5 to 8 a side to give the marks room, and this estimate
            // was left at the old figure: the strip believed it needed eighteen pixels less than it
            // did, so it kept its words at widths where they wrapped onto a second line beside
            // their own icon. An estimate that drifts from the thing it estimates is worse than no
            // estimate, because it is confidently wrong.

            _scopeTitleWidth = text.Length * UIStyles.FontSizeSectionTitle * 0.62f;
            _scopeLit = lit;
            _scopeWords.Clear();
            _scopeOrder.Clear();
            _scopeWordWidths.Clear();
            _scopeCells.Clear();

            // Built at the floor so the strip only grows into room it certainly has; the first
            // refresh, once everything exists and can be measured, decides for real.
            _scopeTier = StripTier.Mini;

            foreach (var standing in sides)
            {
                bool selected = standing.Side == lit && standing.Available;
                var colour = selected ? UIStyles.TextPrimary : UIStyles.TextMuted;

                // ⚠ Each position is a mark AND a word here, where a button elsewhere carries the
                // marks alone. That is the whole arrangement: the full form teaches the pictures
                // beside the words, the small one relies on having been taught.
                // Padding given explicitly: left at its default, the group would take the library's
                // roomy one and a row of three cells would no longer fit beside a title.
                // ⚠ **The strip announces, the title is the subject.** It first took a third of the
                // row — cells 88 wide, marks as tall as their container — so a badge that only says
                // where a save lands was competing with the name of the panel. It is now the
                // smallest thing on the line: fixed marks, tight cells, no stretch. Somebody
                // reading the title should notice it without being stopped by it.
                // ⚠ Room on every side. At 5 and 1 the picture sat flush against its own cell edge
                // — a chip whose contents touch its border reads as clipped rather than as placed,
                // and three of them in a row read as a smudge.
                var cell = UIFactory.CreateHorizontalGroup(box, name + standing.Side + "Cell",
                                                           false, false, true, true, 5,
                                                           new Vector4(3, 3, 8, 8), default,
                                                           TextAnchor.MiddleLeft);
                // ⚠ minWidth as well as no flex: without a minimum a cell is squeezed below what it
                // holds the moment the row is tight, and its contents spill out of it.
                UIFactory.SetLayoutElement(cell, minWidth: Mathf.CeilToInt(ScopeMarkCell),
                                           minHeight: UIStyles.RowHeightSmall,
                                           flexibleWidth: 0, flexibleHeight: 0);
                UIStyles.SetBackground(cell,
                    selected ? UIStyles.ItemBackgroundSelected : UIStyles.ItemBackground);

                AddScopeMark(cell, name + standing.Side + "Mark",
                             EditScope.Mark(standing.Side), colour);

                // ⚠ Always CREATED, shown or hidden according to the tier. Creating them only when
                // they fit would mean rebuilding the row to get them back on a resize — and the
                // row holds the Text this method returns, which callers keep.
                var chip = UIFactory.CreateLabel(cell, name + standing.Side, EditScope.Name(standing.Side),
                                                 TextAnchor.MiddleLeft, supportRichText: false);
                chip.fontSize = UIStyles.FontSizeHint;
                chip.color = colour;
                chip.fontStyle = selected ? FontStyle.Bold : FontStyle.Normal;

                // 🔴 **NEVER WRAPS, and this is the fix the last three attempts were working
                // around.** A label allowed to wrap has a width that depends on the room it was
                // given — which is the very thing being decided from it. Measuring one is measuring
                // the answer one is looking for, one frame late, and it latches: it wraps, reports
                // half its width, is granted half, stays wrapped. Forbidding the wrap makes every
                // reading honest and the whole loop disappears.
                //
                // ⚠ These are three short fixed words. Wrapping them was never wanted anywhere —
                // it was only ever the default nobody turned off.
                chip.horizontalOverflow = HorizontalWrapMode.Overflow;
                chip.verticalOverflow = VerticalWrapMode.Overflow;

                // ⚠ flexibleWidth 0, and it is the whole fix: at 9999 each of the three cells
                // claimed an equal share of the row and the title got what was left.
                UIFactory.SetLayoutElement(chip.gameObject, minHeight: UIStyles.RowHeightSmall,
                                           flexibleWidth: 0);

                // Never translated: these are the product's own words, identical in three places,
                // and a translation of the mod's interface must not make them diverge.
                RegisterExcluded(chip);

                _scopeWords.Add(chip);
                _scopeOrder.Add(standing.Side);
                _scopeCells.Add(cell);

                // The stand-in until the real thing can be read. Every word is created ACTIVE, so
                // the measurement below sees all three before any of them is hidden.
                _scopeWordWidths.Add(EditScope.Name(standing.Side).Length * UIStyles.FontSizeHint * 0.62f);
            }

            MeasureScopeWords();

            // 🔴 **The same rule the buttons carry**, and for the same reason: it turns three
            // pictures and a word into one control with two parts. One ecosystem — a player who
            // reads it on a button must meet it here unchanged.
            //
            // ⚠ It belongs to the STRIP, not to the title: it stays on the icons' side and never
            // travels with the title as it drifts to centre. What separates two things has to sit
            // where the boundary is, not where one of them happens to be.
            var rule = UIFactory.CreateUIObject(name + "Rule", box);
            UIFactory.SetLayoutElement(rule, minWidth: 1 + 2 * 7, preferredWidth: 1 + 2 * 7,
                                       minHeight: 12, preferredHeight: 12,
                                       flexibleWidth: 0, flexibleHeight: 0);

            var ruleLine = UIFactory.CreateUIObject(name + "RuleLine", rule);
            var ruleImage = ruleLine.AddComponent<Image>();
            ruleImage.color = UIStyles.BorderSubtle;
            ruleImage.raycastTarget = false;

            // The line is a child of a transparent slot: painted on the slot itself it would fill
            // its whole width and come out as a grey block, which is exactly what happened once.
            var ruleRect = ruleLine.GetComponent<RectTransform>();
            ruleRect.anchorMin = new Vector2(0.5f, 0.5f);
            ruleRect.anchorMax = new Vector2(0.5f, 0.5f);
            ruleRect.pivot = new Vector2(0.5f, 0.5f);
            ruleRect.sizeDelta = new Vector2(1f, 12f);
            ruleRect.anchoredPosition = Vector2.zero;

            // ⚠ A RectMask2D was tried here and made things worse: the title came out cut at both
            // ends, a fragment floating in the middle of the row. Masking hides a symptom whose
            // cause is that the title is not being given the room it needs — and the cause was the
            // strip being counted in one place and laid out in another.
            var title = CreateTitle(row, name, text);
            UIFactory.SetLayoutElement(title.gameObject, minWidth: 0, flexibleWidth: 9999);

            // 🔴 Same rule, same reason. A title that may wrap reports the width of one of its
            // lines, so the strip reads "the title needs little" exactly when the title is
            // suffering — and takes the room that would have fixed it.
            //
            // ⚠ It CLIPS instead of wrapping when a window is genuinely too narrow, which is the
            // behaviour asked for: a window title belongs on one line, and a clipped one still
            // says which window it is.
            title.horizontalOverflow = HorizontalWrapMode.Overflow;
            title.verticalOverflow = VerticalWrapMode.Overflow;

            // 🔴 **A TITLE BAR, so the title is centred on the WINDOW.** Left to itself it centres
            // on what the strip has not taken, which pushes it right by half the strip — the more
            // the strip says, the further off-centre the title of the window sits.
            //
            // ⚠ And here, unlike on a button, the marks genuinely belong on the LEFT. A button is
            // an inline group that travels with its label; a title bar has a leading slot, a title,
            // and a slot mirroring the first so the middle really is the middle. The empty right
            // slot is not padding — it is what buys the centring, exactly as it does in every
            // window title bar and every navigation bar.
            _scopeMirror = UIFactory.CreateUIObject(name + "Mirror", row);
            UIFactory.SetLayoutElement(_scopeMirror, flexibleWidth: 0, flexibleHeight: 0);

            // Kept so the width can be measured rather than guessed, from the first resize on.
            _scopeTitle = title;

            // ⚠ Forces the first real measurement now that the title exists: the tier chosen a few
            // lines above was based on the estimate, and the estimate is the thing that was wrong.
            _scopeLastWidth = -1f;
            RefreshScopeStrip();

            ApplyScopeTier();
            return title;
        }

        // ── The scope strip, and what it needs to follow a resize ────────────────────────────
        //
        // 🔴 **The words are HIDDEN, never destroyed and rebuilt.** Rebuilding the row would drop
        // the Text this method returns — panels keep it (`_titleLabel = CreateScopedTitle(...)`) —
        // and every caller would be left holding a destroyed object. Toggling costs nothing, the
        // layout group recomputes the widths itself, and there is no flicker because nothing is
        // created.
        private readonly List<Text> _scopeWords = new List<Text>();
        private readonly List<EditSide> _scopeOrder = new List<EditSide>();

        /// <summary>Each word's real width, read while it was on screen. Never a guess after that.</summary>
        private readonly List<float> _scopeWordWidths = new List<float>();

        /// <summary>
        /// The cells holding a mark and its word.
        ///
        /// ⚠ Kept because a cell is the LAST compressible thing in this row: pinning the word and
        /// leaving its cell free lets the layout squeeze the cell and crush the word inside it.
        /// </summary>
        private readonly List<GameObject> _scopeCells = new List<GameObject>();
        private EditSide _scopeLit;
        private StripTier _scopeTier = StripTier.Mini;
        private float _scopeFull, _scopeMedium, _scopeMini, _scopeTitleWidth;

        /// <summary>The width the strip was last asked about, so an unchanged frame costs nothing.</summary>
        private float _scopeLastWidth = -1f;

        /// <summary>The empty slot on the right that makes the title's centre the window's.</summary>
        private GameObject _scopeMirror;

        /// <summary>
        /// The box holding the three cells and the separator — the strip, as one thing.
        ///
        /// ⚠ Its width is READ, never added up. The pieces were siblings of the row and the
        /// arithmetic that reserved room for them was a separate reconstruction of what uGUI would
        /// lay out; the two disagreed, and every fix moved the disagreement rather than removing it.
        /// </summary>
        private RectTransform _scopeStrip;

        /// <summary>
        /// The row the strip and the title share — the thing whose width actually matters.
        ///
        /// ⚠ Not the panel's. This row sits inside a card inside a section, each with its own
        /// padding, so it is dozens of pixels narrower than the window; reasoning from the window
        /// granted the strip room that did not exist.
        /// </summary>
        private RectTransform _scopeRow;

        /// <summary>
        /// The title itself, kept to MEASURE it rather than guess at it.
        ///
        /// 🔴 A section title is bold and a size up, and the per-character factor used everywhere
        /// else models neither. Under-guessing its width is not a cosmetic error: the strip reserves
        /// what it believes the title needs and bids for the rest, so a title that needs more than
        /// it was granted wraps while the strip is still holding its full form — which is exactly
        /// the ladder failing to have any rungs.
        /// </summary>
        private Text _scopeTitle;

        /// <summary>
        /// Re-measures the room the title leaves and changes form only if the answer moved.
        ///
        /// ⚠ Called on every resize, including the programmatic ones. The dead band inside
        /// <see cref="ScopeStrip.Fits"/> is what keeps a size resting on a threshold from flipping
        /// back and forth — without it this method would be the thing doing the flapping.
        /// </summary>
        /// <summary>
        /// The size of a cell holding a mark and nothing else, as the cell is actually built.
        ///
        /// ⚠ Kept beside the code that builds it. The pair drifted apart once — the padding was
        /// raised for looks and this was not — and the strip then reasoned about a cell that no
        /// longer existed.
        /// </summary>
        private const float ScopeCellPad = 8f * 2f;        // left + right, as built
        private const float ScopeIconWord = 5f;             // the cell's spacing, icon to word
        private const float ScopeMarkCell = 11f + ScopeCellPad;
        private const float ScopeRule = 1f + 2f * 7f;       // the separator and its two gaps
        private const float ScopeRowGaps = 4f * 4f;         // three cells, the rule, the title

        /// <summary>
        /// Over-estimating drops a tier a few pixels early, which nobody notices; under-estimating
        /// wraps a word, which is what got reported. So the guess leans one way on purpose.
        /// </summary>
        private const float ScopeSafety = 12f;

        /// <summary>
        /// Reads the words' real width from the words themselves, and rebuilds the three tier
        /// widths from it.
        ///
        /// 🔴 **Because the interface font is a SETTING.** `ui_font` can be changed while a panel
        /// is open, and a per-character factor models one font — the wrong one for a condensed, a
        /// wide or a CJK face. Every width this mechanism reasons about was a guess against metrics
        /// nobody promised.
        ///
        /// ⚠ **A hidden label cannot be measured**, so a width is only ever overwritten while its
        /// word is on screen; otherwise the last good reading stands. That is also why all three
        /// are measured at build, while none is hidden yet: a panel that opened straight into Mini
        /// would otherwise have no word visible, nothing to measure, and no way of ever learning it
        /// could grow.
        /// </summary>
        /// <summary>
        /// The width this label needs on ONE line, or nothing if it cannot be known right now.
        ///
        /// 🔴 **preferredWidth lies about a label that has already wrapped.** It reports the width
        /// of the longest generated line, so a label broken in two claims to need HALF of what it
        /// really does. Believing it closes a loop: the label wraps, says it needs less, is granted
        /// less, and stays wrapped for ever with empty space beside it. That is what shipped, and
        /// both the title and the words did it.
        ///
        /// So a reading counts only while the label is on ONE line. Wrapped, the last good value
        /// stands — which is exactly what lets it be given room back and recover.
        /// </summary>
        private static float SingleLineWidth(Text label)
        {
            if (label == null || !label.gameObject.activeInHierarchy) return 0f;

            // ⚠ The one-line test stays, as a belt: these labels are now set to Overflow so they
            // cannot wrap, but a reading taken before anything was generated is still not an
            // answer, and must not overwrite a good one.
            var generator = label.cachedTextGenerator;
            if (generator == null || generator.lineCount > 1) return 0f;

            return label.preferredWidth > 1f ? label.preferredWidth : 0f;
        }

        private void MeasureScopeWords()
        {
            for (int i = 0; i < _scopeWords.Count && i < _scopeWordWidths.Count; i++)
            {
                float measured = SingleLineWidth(_scopeWords[i]);
                if (measured > 0f) _scopeWordWidths[i] = measured;
            }

            float words = 0f, chosen = 0f;
            for (int i = 0; i < _scopeWordWidths.Count && i < _scopeOrder.Count; i++)
            {
                words += _scopeWordWidths[i] + ScopeIconWord;
                if (_scopeOrder[i] == _scopeLit) chosen = _scopeWordWidths[i] + ScopeIconWord;
            }

            float spacing = ScopeRowGaps + ScopeRule + ScopeSafety;
            _scopeFull = 3f * ScopeMarkCell + words + spacing;
            _scopeMedium = 3f * ScopeMarkCell + chosen + spacing;
            _scopeMini = 3f * ScopeMarkCell + spacing;
        }

        /// <summary>
        /// Forget every measurement and take them again — after the interface font changes.
        ///
        /// ⚠ Without this a font change leaves the strip reasoning with the OLD font's metrics
        /// until somebody happens to resize the window. It is the one failure of this mechanism
        /// that cannot be found by testing the layout, because testing the layout never changes
        /// the font.
        /// </summary>
        public void InvalidateScopeStrip()
        {
            if (_scopeWords.Count == 0) return;

            _scopeLastWidth = -1f;
            RefreshScopeStrip();
        }

        public void RefreshScopeStrip()
        {
            if (_scopeWords.Count == 0) return;

            // 🔴 **The ROW's width, not the panel's.** This row lives inside a card inside a
            // section, each with its own padding, so it is far narrower than the window around it —
            // by fifty pixels or more. Measuring the panel told the strip it had room it did not
            // have, which is why it kept a tier that no longer fitted and simply overflowed instead
            // of dropping to the next one.
            //
            // ⚠ No circularity: the row is stretched by its parent and its width does not depend on
            // which tier the strip is in, so this is an input, not the answer being sought.
            float width = _scopeRow != null && _scopeRow.rect.width > 1f
                ? _scopeRow.rect.width
                : (Rect != null && Rect.rect.width > 1f ? Rect.rect.width : PanelWidth);

            // Read FIRST, so a frame where nothing moved leaves on a comparison. This runs every
            // frame for every open panel, which is what makes it follow a drag whatever caused it.
            if (Mathf.Abs(width - _scopeLastWidth) < 0.5f) return;

            MeasureScopeWords();

            // ⚠ Nothing moved, nothing to ask. This runs on EVERY FRAME through the mod's single
            // tick, so the cheap test comes before the arithmetic and before anything else.
            // ⚠ Same rule as the words, and for the same reason: a title already broken in two
            // reports the width of one of its lines, so believing it would grant it even less and
            // keep it broken. Only a one-line reading counts.
            float titleWidth = SingleLineWidth(_scopeTitle);
            if (titleWidth > 0f) _scopeTitleWidth = titleWidth;

            if (Mathf.Abs(width - _scopeLastWidth) < 0.5f) return;
            _scopeLastWidth = width;

            // ⚠ The title's width is taken out FIRST, so the strip only ever bids for what the
            // title does not need. That is what makes a widening window un-wrap the title before it
            // gives the strip its words back.
            float available = width - 2f * UIStyles.SectionPadding - _scopeTitleWidth;

            var tier = ScopeStrip.Fits(available, _scopeFull, _scopeMedium, _scopeMini, _scopeTier);
            _scopeTier = tier;

            // ⚠ Applied on every width change, not only when the tier moves: the mirror depends on
            // the room, and the room changes with every pixel. Skipping it here is what left a
            // stale mirror squeezing the title.
            ApplyScopeTier();
        }

        private void ApplyScopeTier()
        {
            for (int i = 0; i < _scopeWords.Count && i < _scopeOrder.Count; i++)
            {
                var word = _scopeWords[i];
                if (word == null) continue;

                bool shown = ScopeStrip.ShowsWords(_scopeTier, _scopeOrder[i] == _scopeLit);
                word.gameObject.SetActive(shown);

                float wordWidth = i < _scopeWordWidths.Count ? _scopeWordWidths[i] : 0f;

                // 🔴 **The cell is the LAST compressible thing in this row.** Pinning the word and
                // leaving its cell free changes nothing: the layout squeezes the cell instead and
                // crushes the word inside it, which is the shrinking that survived every previous
                // fix. A cell's minimum is what it actually holds — mark alone, or mark and word.
                int cellNeeds = Mathf.CeilToInt(ScopeMarkCell + (shown ? ScopeIconWord + wordWidth : 0f));
                if (i < _scopeCells.Count && _scopeCells[i] != null)
                {
                    UIFactory.SetLayoutElement(_scopeCells[i], minWidth: cellNeeds,
                                               preferredWidth: cellNeeds,
                                               minHeight: UIStyles.RowHeightSmall,
                                               flexibleWidth: 0, flexibleHeight: 0);
                }

                if (!shown) continue;

                // 🔴 **A word is RIGID: its minimum is its width.** Left without one, the layout
                // squeezes a cell below what it needs as soon as the row is tight — so the tags
                // visibly shrank before any tier changed, and once the text stopped wrapping it
                // simply spilled out of its own cell instead.
                //
                // ⚠ Rigid here means the TITLE absorbs a shortage, which is the order that was
                // asked for: the strip gives up whole words, never letters, and the title is the
                // one thing allowed to clip — and only once the strip has nothing left to drop.
                int needed = Mathf.CeilToInt(wordWidth);
                if (needed > 0)
                {
                    UIFactory.SetLayoutElement(word.gameObject, minWidth: needed,
                                               preferredWidth: needed,
                                               minHeight: UIStyles.RowHeightSmall,
                                               flexibleWidth: 0, flexibleHeight: 0);
                }
            }

            if (_scopeMirror == null) return;

            // ⚠ READ from the box, and only reconstructed while it has not been laid out yet. The
            // mirror exists to match the strip exactly; matching it to a number that merely tries
            // to predict it is how the title ended up off-centre by a few pixels at every tier.
            float strip = _scopeStrip != null && _scopeStrip.rect.width > 1f
                ? _scopeStrip.rect.width
                : (_scopeTier == StripTier.Full ? _scopeFull
                   : _scopeTier == StripTier.Medium ? _scopeMedium : _scopeMini);

            float width = Rect != null && Rect.rect.width > 1f ? Rect.rect.width : PanelWidth;
            float room = width - 2f * UIStyles.SectionPadding - strip;

            // 🔴 **The mirror only exists when there is room to spare.** It buys one thing — a title
            // centred on the window rather than on the leftovers — and it is worth nothing at all
            // if paying for it costs the title a line. Given a minimum width it kept its place while
            // everything around it wrapped, which is how a strip of empty space ended up beside a
            // title broken in two.
            //
            // ⚠ **The order of priority, and it is the whole rule**: the title reads first, the
            // strip says as much as it can second, and only what is left over is spent on symmetry.
            // Growing a window therefore un-wraps the title, then feeds the strip's words, and only
            // then centres.
            //
            // 🔴 **Continuous, not a threshold — so there is nothing to jump across.** This was a
            // yes-or-no test, and crossing it moved the title by half the strip in one step. A dead
            // band would only have stopped that jump from repeating, not from happening. Giving the
            // surplus away a pixel at a time instead means the title DRIFTS to the centre as the
            // window widens, and the question of hysteresis never arises: a measure with no
            // threshold cannot sit on one.
            //
            // Everything the title does not need, up to the width that balances the strip.
            int mirrored = Mathf.RoundToInt(Mathf.Clamp(room - _scopeTitleWidth, 0f, strip));

            UIFactory.SetLayoutElement(_scopeMirror, minWidth: 0, preferredWidth: mirrored,
                                       flexibleWidth: 0, flexibleHeight: 0);
        }

        /// <summary>
        /// One of the switch's three pictures, beside its word.
        ///
        /// ⚠ Silent when the mark cannot be built — an unreadable texture on a game that refuses
        /// one must not take the label with it. The words alone still say everything; the pictures
        /// are what makes the control recognisable elsewhere, not what makes it legible here.
        /// </summary>
        private static void AddScopeMark(GameObject parent, string name, string mark, Color colour)
        {
            var sprite = Icons.Get(mark);
            if (sprite == null) return;

            // ⚠ A FIXED square, not a share of the row. Given only a minimum, the mark took the
            // full height of its cell and a matching width — three of those beside a title is most
            // of the line gone. preferredWidth/Height pin it; flexible 0 stops it growing.
            var holder = UIFactory.CreateUIObject(name, parent);
            UIFactory.SetLayoutElement(holder, minWidth: 11, minHeight: 11,
                                       preferredWidth: 11, preferredHeight: 11,
                                       flexibleWidth: 0, flexibleHeight: 0);

            var image = holder.AddComponent<Image>();
            image.sprite = sprite;
            image.color = colour;
            image.preserveAspect = true;
            image.raycastTarget = false;
        }

        /// <summary>
        /// What is reachable from inside a running game: the file here always, the published
        /// version once signed in and leading its lineage.
        /// </summary>
        private static SideStanding[] ScopeSides(EditSide side)
        {
            bool signedIn = !string.IsNullOrEmpty(TranslatorCore.Config?.api_token);

            return EditScope.Sides(
                hasLocalFile: true,
                // A game IS the machine — that is the whole difference from a browser on its own.
                canReachMachine: true,
                signedIn: signedIn,
                // The mod knows it owns the lineage when the server said so at the last check.
                publishedByThisAccount: signedIn && TranslatorCore.ServerState is { IsOwner: true },
                publishedBySomebodyElse: TranslatorCore.ServerState is { IsOwner: false });
        }

        /// <summary>
        /// Creates a styled description label
        /// </summary>
        protected Text CreateDescription(GameObject parent, string name, string text)
        {
            return UIStyles.CreateDescription(parent, name, text);
        }

        /// <summary>
        /// Creates a navigation button row
        /// </summary>
        protected GameObject CreateButtonRow(GameObject parent, string name = "ButtonRow")
        {
            return UIStyles.CreateButtonRow(parent, name);
        }

        /// <summary>
        /// Creates a primary styled button
        /// </summary>
        protected ButtonRef CreatePrimaryButton(GameObject parent, string name, string text, int minWidth = 130)
        {
            return UIStyles.CreatePrimaryButton(parent, name, text, minWidth);
        }

        /// <summary>
        /// Creates a secondary styled button
        /// </summary>
        protected ButtonRef CreateSecondaryButton(GameObject parent, string name, string text, int minWidth = 110)
        {
            return UIStyles.CreateSecondaryButton(parent, name, text, minWidth);
        }

        /// <summary>
        /// Creates a scrollable panel layout with fixed footer buttons.
        /// This is the recommended way to build panel content - content scrolls if needed,
        /// while buttons stay fixed at the bottom.
        /// </summary>
        protected GameObject CreateScrollablePanelLayout(out GameObject scrollContent, out GameObject buttonRow, int cardWidth = 420)
        {
            // Register panel root EARLY for hierarchy-based own UI detection
            // This must happen before any child components are created
            EnsurePanelRootRegistered();
            var scrollObj = UIStyles.CreateScrollablePanelLayout(ContentRoot, out scrollContent, out buttonRow, cardWidth);

            if (HasFlexibleContent)
            {
                ConfigureScrollContentForFlex(scrollContent);
            }

            return scrollObj;
        }

        /// <summary>
        /// Creates a contextual help bar pinned between the scroll area and the footer
        /// buttons. Hovering any control described via helpZone.Describe(...) shows its
        /// explanation there. The zone translates its own label as it writes it (the code
        /// owns that Text), so there is nothing to register here.
        /// </summary>
        protected Components.HelpZone CreateHelpZone(GameObject buttonRow, string defaultText = "")
        {
            var helpZone = new Components.HelpZone();
            var parent = buttonRow != null ? buttonRow.transform.parent.gameObject : ContentRoot;
            helpZone.CreateUI(parent, defaultText);

            // Pin the bar just above the footer buttons
            if (buttonRow != null && helpZone.Root != null)
                helpZone.Root.transform.SetSiblingIndex(buttonRow.transform.GetSiblingIndex());

            return helpZone;
        }

        /// <summary>
        /// Creates a fixed (non-scrolling) header container pinned between the title bar
        /// and the scroll area — the top mirror of the HelpZone/footer pattern. Put panel
        /// titles and tab buttons here so only the actual content scrolls. As a direct
        /// child of ContentRoot it is automatically counted as chrome by
        /// MeasureChromeHeight.
        /// </summary>
        protected GameObject CreateFixedHeader(string name = "FixedHeader")
        {
            var header = UIFactory.CreateVerticalGroup(ContentRoot, name, false, false, true, true,
                UIStyles.ElementSpacing, default, Color.clear);
            UIFactory.SetLayoutElement(header, flexibleWidth: 9999, flexibleHeight: 0);

            // Pin right above the scroll view
            var scroll = ContentRoot.transform.Find("PanelScroll");
            if (scroll != null)
                header.transform.SetSiblingIndex(scroll.GetSiblingIndex());

            return header;
        }

        /// <summary>
        /// Wires the scroll content so its flexibleHeight children grow with the panel while
        /// the global panel scroll still kicks in when the content overflows.
        ///
        /// Default layout uses ContentSizeFitter in PreferredSize mode, which sets
        /// scrollContent.height = sum(children.preferredHeight) and IGNORES flexibleHeight.
        /// Result: enlarging the panel just adds empty space at the bottom — flexible
        /// children (TextEdit list, ConflictScroll, etc.) never see the extra room.
        ///
        /// Fix: attach a UniverseLib.UI.Widgets.FillViewportHeight component that pushes
        /// LayoutElement.preferredHeight = viewport.rect.height each frame, so
        /// LayoutUtility.GetPreferredHeight returns max(viewport_height, children_height):
        ///   - small content → preferredHeight = viewport → flex children expand
        ///   - large content → preferredHeight = children → global scroll still works
        ///
        /// Also enable childForceExpandHeight on the VLG so the extra room actually
        /// reaches the flexible child instead of staying in unused vertical space.
        /// </summary>
        private void ConfigureScrollContentForFlex(GameObject scrollContent)
        {
            if (scrollContent == null) return;

            UniverseLib.UI.UIFactory.AttachFillViewportHeight(scrollContent);

            var vlg = scrollContent.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
            {
                vlg.childForceExpandHeight = true;
                // Static elements stack from the top; the flexible child takes the trailing space.
                vlg.childAlignment = TextAnchor.UpperCenter;
            }
        }

        /// <summary>
        /// Ensures this panel's root is registered for hierarchy-based own UI detection.
        /// Call this before creating any child components if not using CreateScrollablePanelLayout.
        /// </summary>
        protected void EnsurePanelRootRegistered()
        {
            if (UIRoot != null && !_panelRootRegistered)
            {
                TranslatorCore.RegisterPanelRoot(UIRoot);
                _panelRootRegistered = true;
            }
        }

        private bool _panelRootRegistered = false;

        /// <summary>
        /// Creates an adaptive card that sizes to its content (no fixed minHeight).
        /// Use inside scrollContent from CreateScrollablePanelLayout.
        /// </summary>
        /// <param name="stretchVertically">If true, card expands to fill available vertical space (for tab content)</param>
        protected GameObject CreateAdaptiveCard(GameObject parent, string name, int width = 420, bool stretchVertically = false)
        {
            return UIStyles.CreateAdaptiveCard(parent, name, width, stretchVertically);
        }

        /// <summary>
        /// Creates a styled info label (secondary color, normal font size).
        /// </summary>
        protected Text CreateInfoLabel(GameObject parent, string name, string text)
        {
            return UIStyles.CreateInfoLabel(parent, name, text);
        }

        /// <summary>
        /// Creates a small styled label (muted color, small font).
        /// </summary>
        protected Text CreateSmallLabel(GameObject parent, string name, string text)
        {
            return UIStyles.CreateSmallLabel(parent, name, text);
        }

        /// <summary>
        /// Creates a centered status label for displaying status messages.
        /// </summary>
        protected Text CreateStatusLabel(GameObject parent, string name)
        {
            return UIStyles.CreateStatusLabel(parent, name);
        }

        /// <summary>
        /// Creates a styled input field with proper background and sizing.
        /// </summary>
        protected InputFieldRef CreateStyledInputField(
            GameObject parent, string name, string placeholder, int minHeight = 0)
        {
            return UIStyles.CreateStyledInputField(parent, name, placeholder, minHeight);
        }

        /// <summary>
        /// Creates a list item row with consistent styling.
        /// </summary>
        protected GameObject CreateListItem(GameObject parent, string name, int minHeight = 0)
        {
            return UIStyles.CreateListItem(parent, name, minHeight);
        }

        #endregion

        #region Own UI Registration Helpers

        /// <summary>
        /// Registers a Text component as excluded from translation.
        /// Use for: mod title, language codes, config values, technical labels.
        /// </summary>
        protected void RegisterExcluded(Text text)
        {
            TranslatorCore.RegisterExcluded(text);
        }

        /// <summary>
        /// Registers a Text component for UI-specific translation.
        /// Use for: buttons, labels, descriptions that should be translated with the UI prompt.
        /// </summary>
        protected void RegisterUIText(Text text)
        {
            TranslatorCore.RegisterUIText(text);
        }

        /// <summary>
        /// Write a label the CODE owns (status lines, state buttons, counters), translated at the
        /// moment it is written. Such labels are RegisterExcluded — letting the async pipeline also
        /// write them would put two writers on one Text and leave it stuck or inconsistent — so the
        /// translation has to happen here instead.
        ///
        /// Pass the label as the component so the worker can drop the translation in once it lands
        /// (a cache miss returns English now and queues it for next time). Numbers inside the text
        /// are turned into placeholders by the pipeline, so "Apply (1)" and "Apply (7)" share one
        /// cache entry — but any OTHER data (a username, a language, a game name) must be kept OUT
        /// of the string and concatenated by the caller, or the cache fills with one entry per value.
        /// </summary>
        protected static void SetDynamicText(Text label, string english)
        {
            if (label == null) return;
            label.text = TranslatorCore.TranslateOwnUIDynamic(english, label);
        }

        /// <summary>
        /// Translate a FRAGMENT that the caller then concatenates with data
        /// (<c>Tr("Connected as") + " @" + user</c>).
        ///
        /// Deliberately does NOT register the label: on a cache miss the worker writes the finished
        /// translation straight into the component it was given, which for a composed label would
        /// replace the whole line with just this fragment — dropping the username. Without a
        /// component the translation only lands in the cache, and the next refresh renders the
        /// complete line correctly.
        /// </summary>
        protected static string Tr(string english)
        {
            return TranslatorCore.TranslateOwnUIDynamic(english);
        }

        /// <summary>
        /// Registers a TMPro text component as excluded from translation.
        /// Use for: mod title, language codes, config values, technical labels.
        /// </summary>
        protected void RegisterExcluded(TMPro.TMP_Text text)
        {
            TranslatorCore.RegisterExcluded(text);
        }

        /// <summary>
        /// Registers a TMPro text component for UI-specific translation.
        /// Use for: buttons, labels, descriptions that should be translated with the UI prompt.
        /// </summary>
        protected void RegisterUIText(TMPro.TMP_Text text)
        {
            TranslatorCore.RegisterUIText(text);
        }

        #endregion

        /// <summary>
        /// Desired width of the panel in pixels.
        /// </summary>
        public abstract int PanelWidth { get; }

        /// <summary>
        /// Desired height of the panel in pixels.
        /// </summary>
        public abstract int PanelHeight { get; }

        /// <summary>
        /// Whether this panel should show the backdrop when active.
        /// Override to false for panels like StatusOverlay that shouldn't dim the screen.
        /// </summary>
        protected virtual bool UseBackdrop => true;

        /// <summary>
        /// Minimum panel height for resize constraints.
        /// Override to set a different minimum per panel.
        /// </summary>
        protected virtual int MinPanelHeight => MinHeight;

        /// <summary>
        /// Whether this panel should use dynamic content-based sizing.
        /// Override to false for fixed-size panels like StatusOverlay.
        /// </summary>
        protected virtual bool UseDynamicSizing => true;

        /// <summary>
        /// Whether this panel should persist window preferences (position, size).
        /// Override to false for temporary panels like dialogs or wizards.
        /// </summary>
        protected virtual bool PersistWindowPreferences => true;

        /// <summary>
        /// Whether this panel uses center anchors for positioning.
        /// Override to false for panels like StatusOverlay that use corner anchors.
        /// </summary>
        protected virtual bool UsesCenterAnchors => true;

        /// <summary>
        /// Whether this panel contains scrollable content that should be allowed to grow
        /// beyond its measured min height (i.e. it has a ScrollView with flexibleHeight).
        /// When true, MaxHeight is bumped up to the screen height so the user can drag the
        /// bottom edge down to give the inner scroll list more room.
        /// Override to true for panels with long lists (Inspector text edit, merge conflicts,
        /// parameters tabs, etc.).
        /// </summary>
        protected virtual bool HasFlexibleContent => false;

        /// <summary>
        /// A height the panel should stay able to show even when the current content is
        /// shorter. Tabbed panels report their tallest tab here so the window keeps one size
        /// across tabs. It sizes the PANEL — the containers inside stay free to be exactly as
        /// tall as what they hold.
        /// </summary>
        protected virtual float ContentHeightFloor => _tallestTabContentHeight;

        /// <summary>
        /// THE RULE FOR EVERY SCROLLING LIST IN A PANEL, written here because getting it wrong
        /// is invisible until someone opens the window on a small screen.
        ///
        /// A list declares three heights: a minimum (the smallest box worth showing), a
        /// PREFERRED height equal to that minimum, and a flexible height so it grows into
        /// whatever room is left.
        ///
        /// The preferred one is the trap. This panel sizes its scrolling area to
        /// max(viewport, sum of the children's preferred heights), so a list that asks for twice
        /// what it needs pushes that sum past the viewport before a single entry exists — a
        /// scrollbar nobody can justify, and boxes standing half empty at the size they demanded.
        /// Declaring no preferred height at all is the mirror mistake: the list is then weighed
        /// at its minimum in that sum, so whatever sits below it is never budgeted for and gets
        /// pushed out of view once the list expands for real.
        /// </summary>
        protected const string ScrollingListHeightRule =
            "minHeight = preferredHeight = smallest useful box, flexibleHeight = 9999";

        private float _tallestTabContentHeight;
        private bool _tabHeightMeasured;

        /// <summary>
        /// Keeps a tabbed panel from changing size when the visitor switches tabs, by measuring
        /// the tallest tab once and holding the window to it.
        ///
        /// Call from SetActive when the panel becomes visible: layouts have no measurable size
        /// until then, which is why this waits a few frames before reading anything.
        ///
        /// Lived in three panels as three copies of the same coroutine, and all three wrote the
        /// measurement as a minHeight on the tab CONTAINER. That says something quite different
        /// from what was meant: it makes every short tab as tall as the tallest one, so its
        /// content stretches, nothing inside stays anchored, and the panel scrolls to show
        /// emptiness that belongs to another tab. The measurement sizes the panel; the
        /// containers are left alone.
        /// </summary>
        protected void KeepPanelHeightAcrossTabs(TabBar tabBar)
        {
            if (tabBar == null) return;
            UniverseLib.RuntimeHelper.StartCoroutine(MeasureTallestTab(tabBar));
        }

        private System.Collections.IEnumerator MeasureTallestTab(TabBar tabBar)
        {
            // Layouts are not calculated until the panel has been visible for a few frames
            yield return null;
            yield return null;
            yield return null;

            if (tabBar == null || tabBar.ContentContainer == null) yield break;

            float tallest = tabBar.MeasureMaxContentHeight();
            if (tallest <= 0) yield break;

            // The LARGEST of everything measured so far, so a panel with nested tab bars — the
            // font settings sit behind their own row of tabs — is held by whichever of them
            // needs the most room. Switching any of them then leaves the window where it was.
            if (tallest <= _tallestTabContentHeight) yield break;

            _tallestTabContentHeight = tallest;
            _tabHeightMeasured = true;
            RecalculateSize();
        }

        // Track if we've shown the backdrop for this panel
        private bool _backdropShown = false;

        // Track previous position/size for change detection
        private Vector2 _lastSavedPosition;
        private Vector2 _lastSavedSize;
        private bool _hasLastSavedValues;

        // Track if initial sizing is complete (don't save during construction)
        private bool _initialSizingComplete;

        // Track if we need to do sizing on first show (deferred from init to when panel is visible)
        private bool _needsFirstShowSizing = true;

        // Track the dynamically calculated size (to preserve across SetDefaultSizeAndPosition calls)
        private Vector2 _dynamicSize;
        private bool _hasDynamicSize;

        // Content measurement cache
        private float _measuredContentHeight;
        private bool _contentMeasured;

        // Flag to ignore OnPanelResized during programmatic resizes
        private bool _isProgrammaticResize;

        // True once the panel wears a size the USER picked (restored from preferences or set by
        // dragging). Dynamic sizing must not overwrite it. Cleared by ResetWindowPreferences.
        private bool _userChoseSize;

        // Every constructed panel, so "Reset Window Positions" can act on the
        // LIVE windows immediately instead of only clearing the saved config
        private static readonly List<TranslatorPanelBase> _livePanels = new List<TranslatorPanelBase>();

        /// <summary>
        /// Re-center and re-size every live panel to its defaults, right now.
        /// Called by the Options "Reset Window Positions" button after it
        /// clears the persisted preferences — the visible windows must move
        /// at runtime, not on the next game launch.
        /// </summary>
        public static void ResetAllLiveWindows()
        {
            _livePanels.RemoveAll(p => p == null || p.Rect == null);
            foreach (var panel in _livePanels)
            {
                try { panel.ResetWindowToDefaults(); }
                catch (Exception e) { TranslatorCore.LogWarning($"[Panels] Reset failed for {panel.Name}: {e.Message}"); }
            }
        }

        /// <summary>
        /// Back to the default placement: centered, user resize dropped
        /// (the dynamically computed size is content-driven, it is kept),
        /// tracking refreshed so the reset state is not re-saved as a move.
        /// </summary>
        private void ResetWindowToDefaults()
        {
            if (!UsesCenterAnchors) return;

            // Hand the panel back to dynamic sizing, otherwise the reset would keep the very
            // size the user asked to drop.
            _userChoseSize = false;

            // And throw away the size that came WITH it. "Back to default" means the default is
            // computed again, not that an old measurement is replayed — and this button reaches
            // every window at once, including those never opened in this session, whose layouts
            // had no measurable size when they were last measured. Replaying that gave a window
            // squashed to nothing the next time it was opened.
            //
            // A hidden panel is not measured here: it has no size to measure while hidden.
            // Clearing the flags is enough — the show path measures it properly.
            _contentMeasured = false;
            if (UIRoot != null && UIRoot.activeInHierarchy)
            {
                CalculateAndApplyOptimalSize();
            }
            else
            {
                _hasDynamicSize = false;
            }

            // SetDefaultSizeAndPosition restores anchors/pivot (user resizes
            // shift anchors), re-applies the default or dynamic size, centers
            // the panel and clamps it back on screen
            SetDefaultSizeAndPosition();

            _lastSavedPosition = Rect.anchoredPosition;
            _lastSavedSize = new Vector2(Rect.rect.width, Rect.rect.height);
            _hasLastSavedValues = true;
        }

        /// <summary>
        /// Updates the dragger's resize cache without triggering the user resize save.
        /// Use this for programmatic size changes.
        /// </summary>
        private void UpdateDraggerCache()
        {
            if (Dragger != null)
            {
                // Scoped, not one-shot: OnEndResize raises OnFinishResize synchronously, so the
                // handler sees the flag while it runs. Clearing it here rather than in the handler
                // matters because this also runs BEFORE LateConstructUI subscribes — with nobody
                // listening the flag would stay armed and swallow the user's next real resize,
                // which then never got saved and reverted to the dynamic size on reopen.
                _isProgrammaticResize = true;
                try { Dragger.OnEndResize(); }
                finally { _isProgrammaticResize = false; }
            }
        }

        // Use center anchor
        public override Vector2 DefaultAnchorMin => new(0.5f, 0.5f);
        public override Vector2 DefaultAnchorMax => new(0.5f, 0.5f);
        public override bool CanDragAndResize => true;

        /// <summary>
        /// Maximum height for resize - based on content height.
        /// This is checked by UniverseLib during resize to prevent extending beyond content.
        /// For panels with HasFlexibleContent = true, we cap at screen height instead so
        /// the user can drag the bottom edge down to give the scroll list more visible rows.
        /// </summary>
        public override int MaxHeight
        {
            get
            {
                if (!UseDynamicSizing) return int.MaxValue;
                if (!_contentMeasured) MeasureContentHeight();
                int measured = Mathf.RoundToInt(_measuredContentHeight);
                if (HasFlexibleContent)
                {
                    // Allow the user to extend the panel up to the screen height minus a small
                    // safety margin so the bottom resize handle stays grabbable.
                    int screenCap = Mathf.Max(measured, Screen.height - 60);
                    return screenCap;
                }
                return measured;
            }
        }

        /// <summary>
        /// Clamp the panel inside the screen so the title bar (top of the panel) can
        /// always be grabbed for dragging. Without this the UniverseLib default lets pos.y
        /// reach halfH, which with our (0.5, 0.5) pivot puts the title bar entirely above
        /// the screen edge and makes the panel impossible to move again.
        /// Non-centered panels (StatusOverlay) keep the original UniverseLib behavior.
        /// </summary>
        public override void EnsureValidPosition()
        {
            if (!UsesCenterAnchors)
            {
                base.EnsureValidPosition();
                return;
            }

            Vector3 pos = Rect.localPosition;
            // localPosition is in canvas-local pixels, not screen pixels — the CanvasScaler
            // (referenceResolution 1920x1080, Expand mode) means Screen.width/height differs
            // from the canvas logical size in fullscreen ≠ 1080p, in windowed mode, or on HiDPI.
            // The canvas root rect is the right source of truth for clamping localPosition.
            var rootRect = Owner.RootRect.rect;
            float halfW = rootRect.width * 0.5f;
            float halfH = rootRect.height * 0.5f;
            float halfPanelW = Rect.rect.width * 0.5f;
            float halfPanelH = Rect.rect.height * 0.5f;
            // Minimum reachable slice of the panel on the side/bottom edges —
            // overflowing there is allowed (and useful), as long as enough of
            // the panel stays grabbable
            const float GrabMargin = 50f;

            // X: leave at least GrabMargin px of the panel inside the screen on each side.
            pos.x = Mathf.Clamp(pos.x, -halfW + GrabMargin - halfPanelW, halfW - GrabMargin + halfPanelW);

            // Y, top edge: the panel top may touch the screen edge exactly (no dead band)
            // but never pass above it — the title bar is the only way to move the panel.
            // Y, bottom edge: keep at least GrabMargin px of the panel's top (where the
            // title bar lives) above the bottom edge; the rest may overflow below.
            pos.y = Mathf.Clamp(pos.y, -halfH + GrabMargin - halfPanelH, halfH - halfPanelH);

            Rect.localPosition = pos;
        }

        protected TranslatorPanelBase(UIBase owner) : base(owner)
        {
            _livePanels.Add(this);
        }

        /// <summary>
        /// Override ConstructUI to use construction mode.
        /// This ensures all text created during panel construction is skipped from translation,
        /// preventing race conditions where texts are queued before we can register them.
        /// </summary>
        public override void ConstructUI()
        {
            // Enter construction mode BEFORE any UI is created
            // This makes ShouldSkipTranslation return true for all components during construction
            TranslatorCore.EnterConstructionMode();

            try
            {
                // Call base which creates title bar, content root, etc.
                base.ConstructUI();

                // Title bar and close button text are created by base.ConstructUI()
                // Register them as excluded so they're never translated even after construction mode ends
                // Note: We iterate manually to avoid IL2CPP issues with generic GetComponentsInChildren<T>
                if (TitleBar != null)
                {
                    ExcludeAllTextComponents(TitleBar.transform);
                }
            }
            finally
            {
                // Always exit construction mode, even if an exception occurs
                TranslatorCore.ExitConstructionMode();
            }
        }

        /// <summary>
        /// Recursively excludes all Text components from translation.
        /// Uses manual iteration to avoid IL2CPP issues with generic GetComponentsInChildren.
        /// </summary>
        private void ExcludeAllTextComponents(Transform parent)
        {
            if (parent == null) return;

            // Check this object for Text component
            var text = parent.GetComponent<UnityEngine.UI.Text>();
            if (text != null)
                TranslatorCore.RegisterExcluded(text);

            // Recursively check all children
            for (int i = 0; i < parent.childCount; i++)
            {
                ExcludeAllTextComponents(parent.GetChild(i));
            }
        }

        public override void SetActive(bool active)
        {
            // Handle backdrop
            if (UseBackdrop)
            {
                if (active && !_backdropShown)
                {
                    UIStyles.ShowBackdrop(Owner);
                    _backdropShown = true;
                }
                else if (!active && _backdropShown)
                {
                    UIStyles.HideBackdrop();
                    _backdropShown = false;
                }
            }

            base.SetActive(active);

            // Dynamic sizing on FIRST SHOW - this is when Unity's layout is actually calculated
            if (active && _needsFirstShowSizing && UseDynamicSizing)
            {
                _needsFirstShowSizing = false;
                UniverseLib.RuntimeHelper.StartCoroutine(DelayedFirstShowSizing());
            }
            else if (active && _initialSizingComplete)
            {
                // Every reopen re-clamps: covers resolution changes that
                // happened while the panel was hidden
                EnsureValidPosition();
            }
        }

        /// <summary>
        /// Calculates and applies optimal size on first show, when Unity's layout is properly calculated.
        /// </summary>
        private System.Collections.IEnumerator DelayedFirstShowSizing()
        {
            // Wait for Unity's layout system to fully calculate (panel is now active)
            yield return null;
            yield return null;

            // Force layout rebuild now that panel is visible
            if (ContentRoot != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());
            }

            yield return null;

            // Now measure and apply optimal size
            _contentMeasured = false;
            CalculateAndApplyOptimalSize();
            EnsureValidPosition();

            // Update tracking values
            _lastSavedPosition = Rect.anchoredPosition;
            _lastSavedSize = new Vector2(Rect.rect.width, Rect.rect.height);
            _hasLastSavedValues = true;
            _initialSizingComplete = true;

            UpdateDraggerCache();
        }

        #region Dynamic Sizing

        /// <summary>
        /// Override to properly center the panel.
        /// Preserves dynamically calculated size if already set.
        /// </summary>
        public override void SetDefaultSizeAndPosition()
        {
            // Set anchors to center (for center-anchored panels)
            if (UsesCenterAnchors)
            {
                Rect.anchorMin = new Vector2(0.5f, 0.5f);
                Rect.anchorMax = new Vector2(0.5f, 0.5f);
                Rect.pivot = new Vector2(0.5f, 0.5f);
            }

            // Use dynamically calculated size if available, otherwise use declared size
            var screenDim = new Vector2(Screen.width, Screen.height);
            if (_hasDynamicSize)
            {
                // Preserve dynamic size (already calculated)
                Rect.sizeDelta = _dynamicSize;
            }
            else
            {
                // Initial size before dynamic sizing runs
                Rect.sizeDelta = new Vector2(
                    Mathf.Min(PanelWidth, UIStyles.CalculateMaxPanelWidth(screenDim.x)),
                    Mathf.Min(PanelHeight, UIStyles.CalculateMaxPanelHeight(screenDim.y))
                );
            }

            if (UsesCenterAnchors)
            {
                Rect.anchoredPosition = Vector2.zero;
            }

            EnsureValidPosition();

            UpdateDraggerCache();
        }

        protected override void LateConstructUI()
        {
            // Ensure panel root is registered (backup in case CreateScrollablePanelLayout wasn't used)
            EnsurePanelRootRegistered();

            // Apply anchors based on panel type
            if (UsesCenterAnchors)
            {
                // Center-anchored panels (most panels)
                Rect.anchorMin = new Vector2(0.5f, 0.5f);
                Rect.anchorMax = new Vector2(0.5f, 0.5f);
                Rect.pivot = new Vector2(0.5f, 0.5f);
            }
            // For non-center panels (e.g., StatusOverlay), keep the anchors set in SetDefaultSizeAndPosition()

            // Hook into dragger events for persistence
            if (Dragger != null)
            {
                Dragger.OnFinishResize += OnPanelResized;
                if (PersistWindowPreferences)
                {
                    Dragger.OnFinishDrag += OnPanelDragged;
                }
            }

            // For non-center-anchored panels, skip preference loading (they have fixed positions)
            if (!UsesCenterAnchors)
            {
                _needsFirstShowSizing = false; // Non-centered panels don't use dynamic sizing
                _initialSizingComplete = true;
                return;
            }

            // Try to load saved preferences
            // Position and size are now independent:
            // - Position is only applied if hasPosition is true (user moved the panel)
            // - Size is only applied if userResized is true (user resized the panel)
            WindowPreference pref = null;
            var prefs = TranslatorCore.Config.window_preferences;
            bool hasPreference = PersistWindowPreferences && prefs.panels.TryGetValue(Name, out pref);

            var screenDim = new Vector2(Screen.width, Screen.height);
            float widthRatio = prefs.screenWidth > 0 ? screenDim.x / prefs.screenWidth : 1f;
            float heightRatio = prefs.screenHeight > 0 ? screenDim.y / prefs.screenHeight : 1f;
            bool resolutionChanged = Math.Abs(widthRatio - 1) > 0.1f || Math.Abs(heightRatio - 1) > 0.1f;

            // Handle position (independent of size). The restored SIZE matters
            // here: a user-resized panel is often taller/wider than the
            // declared defaults, and judging bounds with the default size let
            // saved positions land with the title bar above the screen.
            bool sizeRestored = hasPreference && pref.userResized && pref.width > 0 && pref.height > 0 && !resolutionChanged;
            if (hasPreference && pref.hasPosition)
            {
                float newX = resolutionChanged ? pref.x * widthRatio : pref.x;
                float newY = resolutionChanged ? pref.y * heightRatio : pref.y;
                float halfWidth = (sizeRestored ? pref.width : PanelWidth) / 2f;
                float halfHeight = (sizeRestored ? pref.height : PanelHeight) / 2f;

                // Check if saved position would be out of bounds
                bool positionOutOfBounds = Math.Abs(newX) + halfWidth > screenDim.x / 2f ||
                                           Math.Abs(newY) + halfHeight > screenDim.y / 2f;

                if (!positionOutOfBounds)
                {
                    Rect.anchoredPosition = new Vector2(newX, newY);
                }
                else
                {
                    // Position out of bounds - reset to center
                    Rect.anchoredPosition = Vector2.zero;
                }
            }
            else
            {
                // No saved position - use auto (center)
                Rect.anchoredPosition = Vector2.zero;
            }

            // Handle size (independent of position)
            if (sizeRestored)
            {
                // User manually resized - apply saved size and skip dynamic sizing
                Rect.sizeDelta = new Vector2(pref.width, pref.height);
                _userChoseSize = true;
                _needsFirstShowSizing = false;
                _initialSizingComplete = true;
                // Dynamic sizing (and its clamp) will NOT run for this panel:
                // enforce the reachable-title-bar guarantee here
                EnsureValidPosition();
                UpdateDraggerCache();
            }
            // Otherwise, keep _needsFirstShowSizing = true to calculate size dynamically

            // Initialize tracking values
            _lastSavedPosition = Rect.anchoredPosition;
            _lastSavedSize = new Vector2(Rect.rect.width, Rect.rect.height);
            _hasLastSavedValues = true;

            // For non-dynamic sizing panels, set the fixed size now
            if (!UseDynamicSizing)
            {
                Rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
                _needsFirstShowSizing = false;
                _initialSizingComplete = true;
                EnsureValidPosition();
                UpdateDraggerCache();
            }
        }

        /// <summary>
        /// Called when user finishes resizing the panel.
        /// Max/min are now enforced during resize by UniverseLib via MaxHeight property.
        /// This just invalidates content cache and saves preferences if size actually changed.
        /// </summary>
        private void OnPanelResized()
        {
            // Invalidate content measurement cache since size changed
            _contentMeasured = false;

            // Kept as a backstop although the tick already does this every frame: a panel that is
            // not registered as interactive is never ticked, and one float comparison is a cheaper
            // insurance than finding out later which panel that was.
            RefreshScopeStrip();

            // Ignore programmatic resizes (dynamic sizing, etc.) — the flag is cleared by the
            // scope that set it, never here (see UpdateDraggerCache).
            if (_isProgrammaticResize)
                return;

            // Growing a center-pivot panel pushes its top edge UP: a resize
            // can move the title bar above the screen — clamp it back
            EnsureValidPosition();

            // Don't save during initial construction - only after user interaction
            if (!_initialSizingComplete) return;

            // Save size only (not position) - position is saved separately when dragging
            if (PersistWindowPreferences && HasSizeChanged())
            {
                _userChoseSize = true;
                SaveWindowPreference(savePosition: false, saveSize: true);
            }
        }

        /// <summary>
        /// Called when user finishes dragging the panel.
        /// Saves position only (not size) to preserve auto-sizing.
        /// </summary>
        private void OnPanelDragged()
        {
            // Clamp BEFORE saving: the persisted position must never be one
            // where the title bar cannot be grabbed back
            EnsureValidPosition();

            // Don't save during initial construction - only after user interaction
            if (!_initialSizingComplete) return;

            if (HasPositionChanged())
            {
                SaveWindowPreference(savePosition: true, saveSize: false);
            }
        }

        /// <summary>
        /// Checks if the panel position has changed since last save.
        /// </summary>
        private bool HasPositionChanged()
        {
            if (!_hasLastSavedValues) return true;
            const float tolerance = 1f;
            return Math.Abs(Rect.anchoredPosition.x - _lastSavedPosition.x) > tolerance ||
                   Math.Abs(Rect.anchoredPosition.y - _lastSavedPosition.y) > tolerance;
        }

        /// <summary>
        /// Checks if the panel size has changed since last save.
        /// Uses Rect.rect (actual rendered size) because UniverseLib resizes via anchors, not sizeDelta.
        /// </summary>
        private bool HasSizeChanged()
        {
            if (!_hasLastSavedValues) return true;
            const float tolerance = 1f;
            // Use rect.width/height instead of sizeDelta because UniverseLib changes anchors when resizing
            return Math.Abs(Rect.rect.width - _lastSavedSize.x) > tolerance ||
                   Math.Abs(Rect.rect.height - _lastSavedSize.y) > tolerance;
        }

        /// <summary>
        /// Measures the preferred content height using layout system.
        /// Recursively measures child elements to handle nested layouts.
        /// </summary>
        protected float MeasureContentHeight()
        {
            if (ContentRoot == null)
            {
                return PanelHeight;
            }

            // Force complete layout rebuild first
            LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRoot.GetComponent<RectTransform>());

            float contentHeight = 0;

            // Find the scrollContent (has ContentSizeFitter) - this is where our cards are
            var sizeFitter = ContentRoot.GetComponentInChildren<ContentSizeFitter>();
            if (sizeFitter != null)
            {
                var scrollContent = sizeFitter.gameObject;
                var scrollContentRect = scrollContent.GetComponent<RectTransform>();

                // FillViewportHeight (flex panels) writes the CURRENT viewport height
                // into the content's LayoutElement.preferredHeight. Measuring with it
                // active returns max(current viewport, children): the measure could
                // never go below the panel's current size, so flex panels never shrank
                // back and MaxHeight was inflated. Neutralize during the measure.
                var contentLayoutElement = scrollContent.GetComponent<LayoutElement>();
                float savedPreferredHeight = contentLayoutElement != null ? contentLayoutElement.preferredHeight : -1f;
                if (contentLayoutElement != null)
                    contentLayoutElement.preferredHeight = -1f;

                try
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(scrollContentRect);

                    // Method 1: Use Unity's preferred height (most accurate when layout is calculated)
                    float unityPreferredHeight = LayoutUtility.GetPreferredHeight(scrollContentRect);

                    // Method 2: Measure recursively (fallback when Unity returns 0)
                    float childrenHeight = MeasureChildrenRecursive(scrollContent.transform);

                    // Add spacing between direct children (from VerticalLayoutGroup)
                    var layoutGroup = scrollContent.GetComponent<VerticalLayoutGroup>();
                    if (layoutGroup != null)
                    {
                        // Use helper for IL2CPP compatibility (foreach on Transform doesn't work in IL2CPP)
                        int activeChildren = UIHelpers.CountActiveChildren(scrollContent.transform);
                        if (activeChildren > 1)
                        {
                            childrenHeight += layoutGroup.spacing * (activeChildren - 1);
                        }
                        // Add padding
                        childrenHeight += layoutGroup.padding.top + layoutGroup.padding.bottom;
                    }

                    // Use the MAXIMUM of both methods - Unity's calculation is more accurate when available
                    contentHeight = Mathf.Max(unityPreferredHeight, childrenHeight);

                    // A panel may want to stay big enough for something that is not on screen
                    // right now — a tabbed panel keeps the size of its tallest tab so switching
                    // tabs does not make the window jump. That floor belongs HERE, to the panel,
                    // and never to the tab container: forcing the container to the tallest tab
                    // makes every other tab inherit a height it does not need, which leaves dead
                    // space under its content and a scrollbar for something nobody can see.
                    contentHeight = Mathf.Max(contentHeight, ContentHeightFloor);
                }
                finally
                {
                    if (contentLayoutElement != null)
                        contentLayoutElement.preferredHeight = savedPreferredHeight;
                }
            }
            else
            {
                // Fallback: measure ContentRoot directly (for panels without scroll)
                contentHeight = LayoutUtility.GetPreferredHeight(ContentRoot.GetComponent<RectTransform>());
                if (contentHeight <= 0)
                {
                    contentHeight = MeasureChildrenRecursive(ContentRoot.transform);
                }
            }

            // Measure chrome dynamically instead of hardcoding
            float chromeHeight = MeasureChromeHeight();

            _measuredContentHeight = contentHeight + chromeHeight;
            _contentMeasured = true;

            return _measuredContentHeight;
        }

        /// <summary>
        /// Measures the chrome height: every fixed (non-scrolling) direct child of
        /// ContentRoot — title bar, footer buttons, help zone, any future bar — plus
        /// the layout group's padding and spacing. Children are discovered dynamically:
        /// the only one excluded is the scroll view itself, whose content is measured
        /// separately by MeasureContentHeight. Hardcoded chrome names here are exactly
        /// what caused the HelpZone regression (panels sized 45px too short → the
        /// auto-hide scrollbar correctly showed up in windows that "had room").
        /// </summary>
        private float MeasureChromeHeight()
        {
            if (ContentRoot == null)
            {
                return UIStyles.PanelPadding * 2;
            }

            float chromeHeight = 0;
            int activeChildren = 0;

            // Manual iteration for IL2CPP compatibility (foreach on Transform doesn't work)
            var root = ContentRoot.transform;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (!child.gameObject.activeSelf) continue;
                activeChildren++;

                // The scroll view is the elastic part, not chrome
                if (child.GetComponent<ScrollRect>() != null) continue;

                var childRect = child.GetComponent<RectTransform>();
                if (childRect == null) continue;

                LayoutRebuilder.ForceRebuildLayoutImmediate(childRect);
                float childHeight = LayoutUtility.GetPreferredHeight(childRect);
                if (childHeight <= 0)
                {
                    var layoutElement = child.GetComponent<LayoutElement>();
                    childHeight = layoutElement != null && layoutElement.minHeight > 0
                        ? layoutElement.minHeight
                        : childRect.rect.height;
                }
                if (childHeight > 0)
                    chromeHeight += childHeight;
            }

            // Layout group padding + spacing between direct children
            var contentVlg = ContentRoot.GetComponent<VerticalLayoutGroup>();
            if (contentVlg != null)
            {
                chromeHeight += contentVlg.padding.top + contentVlg.padding.bottom;
                if (activeChildren > 1)
                {
                    chromeHeight += contentVlg.spacing * (activeChildren - 1);
                }
            }
            else
            {
                // Fallback to UIStyles constants: padding + assume 2 gaps
                chromeHeight += UIStyles.PanelPadding * 2;
                chromeHeight += UIStyles.ElementSpacing * 2;
            }

            return chromeHeight;
        }

        /// <summary>
        /// Recursively measures children heights, going up to maxDepth levels deep.
        /// Uses manual iteration for IL2CPP compatibility (foreach on Transform doesn't work in IL2CPP).
        /// </summary>
        private float MeasureChildrenRecursive(Transform parent, int depth = 0, int maxDepth = 10)
        {
            if (depth > maxDepth) return 0;

            float totalHeight = 0;
            int childCount = 0;

            // Manual iteration for IL2CPP compatibility
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (!child.gameObject.activeSelf) continue;

                var childRect = child.GetComponent<RectTransform>();
                if (childRect == null) continue;

                LayoutRebuilder.ForceRebuildLayoutImmediate(childRect);

                // Try to get preferred height first
                float childHeight = LayoutUtility.GetPreferredHeight(childRect);

                // If preferred is 0, check LayoutElement.minHeight
                if (childHeight <= 0)
                {
                    var layoutElement = child.GetComponent<LayoutElement>();
                    if (layoutElement != null && layoutElement.minHeight > 0)
                    {
                        childHeight = layoutElement.minHeight;
                    }
                }

                // If still 0, try rect height
                if (childHeight <= 0)
                {
                    childHeight = childRect.rect.height;
                }

                // If still 0 and has children, measure children recursively
                if (childHeight <= 0 && child.childCount > 0)
                {
                    childHeight = MeasureChildrenRecursive(child, depth + 1, maxDepth);

                    // Add layout group padding/spacing if present
                    var vlg = child.GetComponent<VerticalLayoutGroup>();
                    if (vlg != null)
                    {
                        childHeight += vlg.padding.top + vlg.padding.bottom;
                    }
                }

                if (childHeight > 0)
                {
                    totalHeight += childHeight;
                    childCount++;
                }
            }

            // Add spacing between children
            var parentVlg = parent.GetComponent<VerticalLayoutGroup>();
            if (parentVlg != null && childCount > 1)
            {
                totalHeight += parentVlg.spacing * (childCount - 1);
            }

            return totalHeight;
        }

        /// <summary>
        /// Gets the current maximum content height (what the panel could expand to).
        /// Used to enforce resize limits.
        /// </summary>
        protected float GetMaxContentHeight()
        {
            if (!_contentMeasured)
                MeasureContentHeight();
            return _measuredContentHeight;
        }

        /// <summary>
        /// Call this when panel content changes dynamically to recalculate size.
        /// Waits for layout to update before measuring.
        /// </summary>
        protected void RecalculateSize()
        {
            if (!UseDynamicSizing) return;
            UniverseLib.RuntimeHelper.StartCoroutine(DelayedRecalculateSize());
        }

        private System.Collections.IEnumerator DelayedRecalculateSize()
        {
            // Force immediate layout rebuild on ContentRoot
            if (ContentRoot != null)
            {
                var contentRect = ContentRoot.GetComponent<RectTransform>();
                if (contentRect != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
                }
            }

            // Wait 2 frames for Unity to fully recalculate layouts after SetActive changes
            yield return null;
            yield return null;

            // Force rebuild again after frames have passed
            if (ContentRoot != null)
            {
                var contentRect = ContentRoot.GetComponent<RectTransform>();
                if (contentRect != null)
                {
                    LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
                }
            }

            _contentMeasured = false; // Force re-measurement
            CalculateAndApplyOptimalSize();
        }

        /// <summary>
        /// Calculates and applies optimal panel size based on content and screen.
        /// </summary>
        protected void CalculateAndApplyOptimalSize()
        {
            if (!UseDynamicSizing)
            {
                Rect.sizeDelta = new Vector2(PanelWidth, PanelHeight);
                UpdateDraggerCache();
                return;
            }

            // A size the user set by hand wins over any later recalculation. Content changes
            // (opening a tab, a list filling in) call RecalculateSize, and this method always
            // resets the width back to the declared PanelWidth — so a hand-widened window
            // silently snapped back as soon as the content moved. Overflow is not lost: the
            // panel body scrolls.
            if (_userChoseSize)
            {
                UpdateDraggerCache();
                return;
            }

            var screenDim = new Vector2(Screen.width, Screen.height);

            // Measure content
            float contentHeight = MeasureContentHeight();

            // Calculate optimal height: min(content, screen-bounded)
            int optimalHeight = UIStyles.CalculateOptimalPanelHeight(
                contentHeight,
                screenDim.y,
                MinPanelHeight
            );

            // Width stays at declared PanelWidth (or screen-bounded if too wide)
            int optimalWidth = Mathf.Min(PanelWidth, UIStyles.CalculateMaxPanelWidth(screenDim.x));

            // Store and apply dynamic size
            _dynamicSize = new Vector2(optimalWidth, optimalHeight);
            _hasDynamicSize = true;
            Rect.sizeDelta = _dynamicSize;

            // Update resize cache so cursor appears correctly
            UpdateDraggerCache();
        }

        #endregion

        #region Window Preference Persistence

        /// <summary>
        /// Saves window preferences to config.
        /// Position and size are saved independently.
        /// </summary>
        /// <param name="savePosition">If true, saves current position and sets hasPosition flag</param>
        /// <param name="saveSize">If true, saves current size and sets userResized flag</param>
        protected void SaveWindowPreference(bool savePosition, bool saveSize)
        {
            if (!PersistWindowPreferences) return;
            if (!savePosition && !saveSize) return;

            var screenDim = new Vector2(Screen.width, Screen.height);
            var prefs = TranslatorCore.Config.window_preferences;

            // Get or create preference
            if (!prefs.panels.TryGetValue(Name, out var pref))
            {
                pref = new WindowPreference();
            }

            // Save position if requested
            if (savePosition)
            {
                pref.x = Rect.anchoredPosition.x;
                pref.y = Rect.anchoredPosition.y;
                pref.hasPosition = true;
                _lastSavedPosition = Rect.anchoredPosition;
            }

            // Save size if requested
            if (saveSize)
            {
                pref.width = Rect.rect.width;
                pref.height = Rect.rect.height;
                pref.userResized = true;
                _lastSavedSize = new Vector2(Rect.rect.width, Rect.rect.height);
            }

            prefs.panels[Name] = pref;

            // Update global screen dimensions
            prefs.screenWidth = Mathf.RoundToInt(screenDim.x);
            prefs.screenHeight = Mathf.RoundToInt(screenDim.y);

            _hasLastSavedValues = true;

            // Save config (debounced by TranslatorCore)
            TranslatorCore.SaveConfig();
        }

        #endregion
    }
}
