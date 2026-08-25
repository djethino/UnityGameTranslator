using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// Keeps a button's LABEL in step with whether it can be pressed.
    ///
    /// 🔴 **Unity greys a button's own image and leaves every child alone.** A Selectable tints its
    /// targetGraphic through its ColorBlock — that is the background — and a label is a child, so
    /// it kept full-strength white on a control nobody could press. A disabled button was therefore
    /// told apart from a live one only by a slightly different grey behind it, which is how it came
    /// to be confused with an ordinary button sitting on another surface. Text carries far more
    /// weight than a background shade.
    ///
    /// ⚠ **Why a registry ticked once a frame, and not a MonoBehaviour.** The natural shape is a
    /// component beside ButtonLabelFitter, which does exactly this job for width. That component
    /// lives in UniverseLib, which is built ONCE PER RUNTIME and can carry the `#if CPP`
    /// constructor and the IL2CPP type registration a MonoBehaviour needs. This assembly is built
    /// ONCE for both, so it cannot: an injected type would work on Mono and fail on IL2CPP, which
    /// is the failure mode this project pays for most often.
    ///
    /// ⚠ **And it was not worth converting ninety-one call sites for.** There are 91 places that
    /// set `interactable` across twelve files; rewriting them all would be a mass edit of exactly
    /// the kind this project has already paid for, and would leave every button written afterwards
    /// to remember a rule. Registering at the moment a button is themed covers what exists and what
    /// comes next, in one place.
    /// </summary>
    public static class ButtonStates
    {
        private static readonly List<Selectable> _watched = new List<Selectable>();
        private static readonly List<Text> _labels = new List<Text>();
        private static readonly List<bool> _wasInteractable = new List<bool>();

        /// <summary>
        /// Starts following a button. Called where the mod makes a control look like its own, so a
        /// button is watched by the same act that gives it its colours.
        /// </summary>
        /// <summary>
        /// Buttons whose label colour SAYS something, and which must therefore be left alone.
        ///
        /// 🔴 **This registry owns the label colour of what it watches**, so anything already using
        /// that colour to carry meaning would have its meaning overwritten. Found by looking, not
        /// assumed: HotkeyCapture paints its key accent when a key is set and muted when none is,
        /// TabBar and TranslationParametersPanel mark the selected tab, VoteButtons turns an arrow
        /// white when it is the one you cast, MergePanel marks a chosen side.
        ///
        /// ⚠ Excluded by NAME rather than by a flag on the call, deliberately: a flag would have to
        /// be remembered at each of those sites, and forgetting it looks like nothing until the
        /// meaning quietly disappears. The list is short, it is here, and it is checked.
        /// </summary>
        private static readonly string[] _labelSpeaks =
        {
            "KeyButton", "Tab_", "IdentifyBtn", "UpButton", "DownButton",
        };

        public static void Watch(Selectable selectable)
        {
            if (selectable == null) return;

            foreach (var reserved in _labelSpeaks)
                if (selectable.name.IndexOf(reserved, System.StringComparison.Ordinal) >= 0) return;

            // Inactive children included: a panel is built before it is shown, so the label is
            // routinely under an inactive root at this point. ⚠ NOT GetComponentInChildren<Text>(true)
            // — that overload throws on IL2CPP and killed CreatePanels; see FindInChildrenSafe.
            var label = UIHelpers.FindInChildrenSafe<Text>(selectable.transform);
            if (label == null) return;

            for (int i = 0; i < _watched.Count; i++)
                if (ReferenceEquals(_watched[i], selectable)) return;

            _watched.Add(selectable);
            _labels.Add(label);

            // 🔴 **Painted here, on the spot, and the state recorded as it really is.** This used to
            // record the OPPOSITE, so that the first tick would find a difference and paint. It
            // works for a button that is still live at that first tick — and fails silently for one
            // that has already been switched off, which is the ordinary case: a panel is built, then
            // refreshed in the same breath. Stored `false`, disabled `false`, the tick reads "nothing
            // changed" and the label keeps its full-strength white FOR EVER. Every button born
            // greyed — the three lineage choices, Upload with nothing to send — was in that state.
            //
            // ⚠ A tick that only repaints on a change still needs its first value to be true, not a
            // trick that assumes what the next frame will hold.
            Paint(label, selectable.interactable);
            _wasInteractable.Add(selectable.interactable);
        }

        // ⚠ ButtonLabelDead, not TextMuted. Measured against the disabled fill, TextMuted scores
        // 4.50 — comfortable reading, which is what made a dead button look like a live one on
        // another surface. See UIStyles.ButtonLabelDead.
        private static void Paint(Text label, bool live)
        {
            label.color = live ? UIStyles.TextPrimary : UIStyles.ButtonLabelDead;
        }

        /// <summary>
        /// One pass. Repaints only the buttons whose state actually moved.
        ///
        /// ⚠ Cheap by construction, because it runs inside the tick that also carries the scene
        /// scan: one bool comparison per watched button, and a colour written only on the frame a
        /// button changes — which is rare. Destroyed buttons are dropped as they are met, so a
        /// panel rebuilt a hundred times does not grow this list for ever.
        /// </summary>
        public static void Tick()
        {
            for (int i = _watched.Count - 1; i >= 0; i--)
            {
                var selectable = _watched[i];
                var label = _labels[i];

                if (selectable == null || label == null)
                {
                    _watched.RemoveAt(i);
                    _labels.RemoveAt(i);
                    _wasInteractable.RemoveAt(i);
                    continue;
                }

                bool live = selectable.interactable;
                if (live == _wasInteractable[i]) continue;

                _wasInteractable[i] = live;
                Paint(label, live);
            }
        }

        /// <summary>Forgets everything — the UI was torn down and rebuilt.</summary>
        public static void Clear()
        {
            _watched.Clear();
            _labels.Clear();
            _wasInteractable.Clear();
        }
    }
}
