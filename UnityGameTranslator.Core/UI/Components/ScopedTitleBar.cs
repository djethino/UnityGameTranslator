using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// A title with the three-position scope switch to its left: which copy this panel writes
    /// to — the published translation, both, or the file in this game.
    ///
    /// ⚠ **On EVERY screen that shows translation lines, without exception.** One missing is
    /// worse than never having had it: the glance stops being a habit, and an absent badge
    /// starts to mean something it does not. That is why the panel base hands it out
    /// (<c>ScopedTitle</c>) rather than each panel pasting its own.
    ///
    /// ⚠ The positions, their order and their words come from
    /// <see cref="UnityGameTranslator.Common.EditScope"/> — the same answers the manager and
    /// the website draw their own control from. Somebody who learns it in a browser must not
    /// have to relearn it here.
    ///
    /// Lived on <c>TranslatorPanelBase</c> until 2026-09-08 (step 4d of
    /// analyse/plan-prealables-couches.md): six hundred lines of rendering, state and resize
    /// arithmetic that had nothing to do with being a panel. Moved here word for word; the base
    /// keeps one field and two forwards.
    /// </summary>
    public sealed class ScopedTitleBar
    {
        // ── The scope strip, and what it needs to follow a resize ────────────────────────────
        //
        // 🔴 **The words are HIDDEN, never destroyed and rebuilt.** Rebuilding the row would drop
        // the Text this control returns — panels keep it (`_titleLabel = ScopedTitle(...)`) —
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

        // Kept so the strip can be lit again — see Relight. The mark is half of what "lit" looks
        // like, so re-colouring the word alone would leave the picture saying the opposite.
        private readonly List<Image> _scopeMarks = new List<Image>();

        // What the panel asked for, kept because the ANSWER depends on facts that arrive later.
        private EditSide _scopeAsked;

        // What was last painted: the lit side, and which sides were available. Compared rather than
        // repainted, because Refresh runs on every frame of every visible panel.
        private string _scopePainted;
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
        /// The window's width, asked of the panel when the row cannot answer for itself — before
        /// its first layout, and for the mirror's room. The panel knows its rect and its declared
        /// width; this control only needs the number.
        /// </summary>
        private readonly Func<float> _windowWidth;

        private LabelHandle _title;

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
        // Two gaps in the row now that the cells and the rule live in one box: box to title, and
        // title to mirror. It counted four, from when they were four siblings.
        private const float ScopeRowGaps = 2f * 4f;

        /// <summary>
        /// Over-estimating drops a tier a few pixels early, which nobody notices; under-estimating
        /// wraps a word, which is what got reported. So the guess leans one way on purpose.
        /// </summary>
        /// <remarks>
        /// ⚠ Went 12 → 28 → 6. It was raised while the tier arithmetic was the only thing keeping
        /// the title off the separator, and had to be generous for that. It is not any more: the
        /// title now has a minimum width the layout cannot take from it, so overlap is impossible
        /// whatever this number says. All it decides is how early a tier is given up — and at 28
        /// it gave one up with plenty of room still to the right of the title.
        ///
        /// What is left is the rounding of a reading taken one frame before the layout it
        /// describes. A few pixels, which is all it should ever have been.
        /// </remarks>
        private const float ScopeSafety = 6f;

        private ScopedTitleBar(Func<float> windowWidth)
        {
            _windowWidth = windowWidth;
        }

        /// <summary>The title, as a label a panel may hold and — under a Dynamic policy — rewrite.</summary>
        public LabelHandle Title => _title;

        /// <summary>
        /// Build the row: strip, rule, title, mirror.
        /// </summary>
        /// <param name="windowWidth">The panel's current width, or its declared one before layout.</param>
        /// <param name="policy">
        /// UiText for a title fixed at construction; Dynamic (or Excluded) for one the code
        /// rewrites afterwards (a mode announced in the title itself) — <see cref="LabelHandle.Say"/>
        /// throws on a UiText label, so a title that changes must not be created as one.
        /// </param>
        public static ScopedTitleBar Create(Host parent, string name, string text, EditSide side,
                                            Func<float> windowWidth, TextPolicy policy = TextPolicy.UiText)
        {
            // ⚠ Alignment and padding stated, not left to the default. Without them the row packs
            // its children into the top-left corner and flush against the edge — the strip and the
            // title read as stuck in the corner of the bar rather than sitting on its line. The
            // same omission was fixed on the main panel's section title; it was here too.
            var row = UIFactory.CreateHorizontalGroup(parent.Object, name + "Row", false, false, true, true, 4,
                                                      new Vector4(3, 3, UIStyles.SectionPadding,
                                                                  UIStyles.SectionPadding),
                                                      default, TextAnchor.MiddleLeft);
            UIFactory.SetLayoutElement(row, minHeight: UIStyles.RowHeightMedium,
                                       flexibleWidth: 9999, flexibleHeight: 0);

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

            // 🔴 **Something is ALWAYS lit.** The chosen side was lit only when it happened to be
            // available, so on any screen where it is not — the published side with nothing of
            // yours published, which is most people — all three positions came out grey and the
            // control said nothing at all. EditScope.Default answers what a screen actually falls
            // back to, and it falls towards the local side, never towards publishing.
            var sides = ScopeSides(side);
            var lit = EditScope.Default(sides, side);

            var bar = new ScopedTitleBar(windowWidth);
            bar._scopeRow = row.GetComponent<RectTransform>();
            bar._scopeStrip = box.GetComponent<RectTransform>();

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

            bar._scopeTitleWidth = text.Length * UIStyles.FontSizeSectionTitle * 0.62f;
            bar._scopeLit = lit;
            bar._scopeAsked = side;
            bar._scopePainted = PaintKey(sides, lit);

            // Built at the floor so the strip only grows into room it certainly has; the first
            // refresh, once everything exists and can be measured, decides for real.
            bar._scopeTier = StripTier.Mini;

            foreach (var standing in sides)
            {
                bool selected = standing.Side == lit && standing.Available;
                var colour = selected ? UIStyles.TextPrimary : UIStyles.TextMuted;

                // ⚠ The MARK takes the same colour here as it does on a button, while the word
                // keeps the text ramp. The two sizes of this control are only one control if the
                // picture is identical — including what "lit" looks like — and the small form has
                // nothing but the picture to say it with.
                var markColour = selected ? UIStyles.MarkLit : UIStyles.TextMuted;

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

                var markImage = AddScopeMark(cell, name + standing.Side + "Mark",
                                             EditScope.Mark(standing.Side), markColour);

                // ⚠ Always CREATED, shown or hidden according to the tier. Creating them only when
                // they fit would mean rebuilding the row to get them back on a resize — and the
                // row holds the Text this control returns, which callers keep.
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
                TranslatorCore.RegisterExcluded(chip);

                bar._scopeWords.Add(chip);
                bar._scopeOrder.Add(standing.Side);
                bar._scopeCells.Add(cell);
                bar._scopeMarks.Add(markImage);

                // The stand-in until the real thing can be read. Every word is created ACTIVE, so
                // the measurement below sees all three before any of them is hidden.
                bar._scopeWordWidths.Add(EditScope.Name(standing.Side).Length * UIStyles.FontSizeHint * 0.62f);
            }

            bar.MeasureScopeWords();

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
            var title = UIStyles.CreateTitle(row, name, text);
            UIFactory.SetLayoutElement(title.gameObject, minWidth: 0, flexibleWidth: 9999);

            // Registered here, under the policy the panel asked for: the panel holds a handle and
            // may not name the registration itself.
            if (policy == TextPolicy.UiText) TranslatorCore.RegisterUIText(title);
            else TranslatorCore.RegisterExcluded(title);
            bar._scopeTitle = title;
            bar._title = new LabelHandle(title, policy);

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
            bar._scopeMirror = UIFactory.CreateUIObject(name + "Mirror", row);
            UIFactory.SetLayoutElement(bar._scopeMirror, flexibleWidth: 0, flexibleHeight: 0);

            // ⚠ Forces the first real measurement now that the title exists: the tier chosen a few
            // lines above was based on the estimate, and the estimate is the thing that was wrong.
            bar._scopeLastWidth = -1f;
            bar.Refresh();

            bar.ApplyScopeTier();
            return bar;
        }

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
        private void MeasureScopeWords()
        {
            for (int i = 0; i < _scopeWords.Count && i < _scopeWordWidths.Count; i++)
            {
                float measured = SingleLineWidth(_scopeWords[i]);
                if (measured > 0f) _scopeWordWidths[i] = measured;
            }

            // 🔴 **Anchored on what the strip ACTUALLY measures, with only the deltas computed.**
            // The three candidate widths were reconstructions of what uGUI would lay out — cells,
            // paddings, spacings, the rule, all added up by hand — and they came out short, so a
            // tier was kept until the title was already touching the strip. What is added or taken
            // away between tiers is nothing but WORDS, and every word's width is measured.
            float shown = 0f, all = 0f, chosen = 0f;
            for (int i = 0; i < _scopeWordWidths.Count && i < _scopeOrder.Count; i++)
            {
                float piece = _scopeWordWidths[i] + ScopeIconWord;

                all += piece;
                if (_scopeOrder[i] == _scopeLit) chosen = piece;
                if (ScopeStrip.ShowsWords(_scopeTier, _scopeOrder[i] == _scopeLit)) shown += piece;
            }

            // The strip as it stands, minus the words it is currently showing: the bare form, from
            // which the other two are one addition away.
            float stripNow = _scopeStrip != null && _scopeStrip.rect.width > 1f
                ? _scopeStrip.rect.width
                : 3f * ScopeMarkCell + ScopeRule + shown;

            float bare = stripNow - shown;

            _scopeMini = bare + ScopeRowGaps + ScopeSafety;
            _scopeMedium = bare + chosen + ScopeRowGaps + ScopeSafety;
            _scopeFull = bare + all + ScopeRowGaps + ScopeSafety;
        }

        /// <summary>
        /// Forget every measurement and take them again — after the interface font changes.
        ///
        /// ⚠ Without this a font change leaves the strip reasoning with the OLD font's metrics
        /// until somebody happens to resize the window. It is the one failure of this mechanism
        /// that cannot be found by testing the layout, because testing the layout never changes
        /// the font.
        /// </summary>
        public void Invalidate()
        {
            if (_scopeWords.Count == 0) return;

            _scopeLastWidth = -1f;
            Refresh();
        }

        /// <summary>
        /// Re-measures the room the title leaves and changes form only if the answer moved.
        ///
        /// ⚠ Called on every resize, including the programmatic ones. The dead band inside
        /// <see cref="ScopeStrip.Fits"/> is what keeps a size resting on a threshold from flipping
        /// back and forth — without it this method would be the thing doing the flapping.
        /// </summary>
        public void Refresh()
        {
            if (_scopeWords.Count == 0) return;

            Relight();

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
                : _windowWidth();

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

            // 🔴 **The title cannot be given less than it needs — a constraint, not a calculation.**
            // Every previous attempt tuned the arithmetic so the tier would drop before the title
            // ran out of room, and a few pixels of error always came back as the title drawn over
            // the separator. With a minimum equal to its own measured width the layout can no
            // longer squeeze it at all: if the row is genuinely too small, the row overflows to the
            // RIGHT and the title is cut by the panel edge — never drawn across the strip.
            //
            // ⚠ This is what makes the tier arithmetic merely a nicety instead of a guarantee. It
            // was carrying a promise it could not keep.
            if (_scopeTitle != null && _scopeTitleWidth > 1f)
            {
                UIFactory.SetLayoutElement(_scopeTitle.gameObject,
                                           minWidth: Mathf.CeilToInt(_scopeTitleWidth),
                                           flexibleWidth: 9999);
            }

            if (_scopeMirror == null) return;

            // ⚠ READ from the box, and only reconstructed while it has not been laid out yet. The
            // mirror exists to match the strip exactly; matching it to a number that merely tries
            // to predict it is how the title ended up off-centre by a few pixels at every tier.
            float strip = _scopeStrip != null && _scopeStrip.rect.width > 1f
                ? _scopeStrip.rect.width
                : (_scopeTier == StripTier.Full ? _scopeFull
                   : _scopeTier == StripTier.Medium ? _scopeMedium : _scopeMini);

            float width = _windowWidth();
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
        private static Image AddScopeMark(GameObject parent, string name, string mark, Color colour)
        {
            var sprite = Icons.Get(mark);
            if (sprite == null) return null;

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

            // Handed back so the strip can re-colour it when the lit side changes — see Relight.
            return image;
        }

        /// <summary>
        /// What is reachable from inside a running game: the file here always, the published
        /// version once signed in and leading its lineage.
        /// </summary>
        /// <summary>
        /// Light the strip from what is known NOW.
        ///
        /// 🔴 **It was decided once, at construction — before anything was known.** Panels are
        /// built when the mod starts, and <see cref="ScopeSides"/> asks whether this account has
        /// published this lineage: at that moment <c>ServerState</c> is null, so the published side
        /// reads as unavailable, <c>Both</c> with it, and <see cref="EditScope.Default"/> falls back
        /// to <c>Local</c> — deliberately, since falling towards publishing would be worse. The
        /// answer arrives seconds later from check-uuid and nothing ever asked again.
        ///
        /// So the upload screen sat under a title saying "Update Translation" with the LOCAL mark
        /// lit, and the button that opened it carried a different mark from the window it opened.
        /// Same family as the sync watch and the interface file: settled once, never re-derived.
        ///
        /// ⚠ **Compared before painting**, because Refresh runs every frame on every visible panel.
        /// The key is the lit side plus which sides are available — the two things that decide what
        /// this looks like.
        /// </summary>
        /// <summary>
        /// Change which side this screen is about — for a window that serves two acts.
        ///
        /// ⚠ Only the ASKED side moves; whether it can be lit is still availability's answer, and
        /// Relight repaints on the next frame like any other change.
        /// </summary>
        public void Ask(EditSide side)
        {
            _scopeAsked = side;
            Relight();
        }

        private void Relight()
        {
            var sides = ScopeSides(_scopeAsked);
            var lit = EditScope.Default(sides, _scopeAsked);

            string key = PaintKey(sides, lit);
            if (key == _scopePainted) return;

            _scopePainted = key;
            _scopeLit = lit;

            for (int i = 0; i < sides.Length && i < _scopeCells.Count; i++)
            {
                bool selected = sides[i].Side == lit && sides[i].Available;

                UIStyles.SetBackground(_scopeCells[i],
                    selected ? UIStyles.ItemBackgroundSelected : UIStyles.ItemBackground);

                if (i < _scopeWords.Count && _scopeWords[i] != null)
                {
                    _scopeWords[i].color = selected ? UIStyles.TextPrimary : UIStyles.TextMuted;
                    _scopeWords[i].fontStyle = selected ? FontStyle.Bold : FontStyle.Normal;
                }

                if (i < _scopeMarks.Count && _scopeMarks[i] != null)
                    _scopeMarks[i].color = selected ? UIStyles.MarkLit : UIStyles.TextMuted;
            }
        }

        /// <summary>What the strip looks like, in one comparable value.</summary>
        private static string PaintKey(SideStanding[] sides, EditSide lit)
        {
            var key = new System.Text.StringBuilder().Append((int)lit);
            foreach (var side in sides) key.Append(side.Available ? '1' : '0');
            return key.ToString();
        }

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
    }
}
