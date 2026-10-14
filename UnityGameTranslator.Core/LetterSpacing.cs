using System;
using System.Collections.Generic;
using System.Reflection;
using UnityGameTranslator.Common;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// 🔴 **No letter spacing in a translation whose letters are shaped together** (CLDR's
    /// shapingReq "yes": Arabic, Devanagari, Bengali, Tamil, Khmer… — the table the shapers read,
    /// ShapingCommon.HoldsShapedLetters).
    ///
    /// A game spaces the letters of some of its texts for their look (Silksong's titles in Trajan:
    /// a character spacing of 10). Latin takes it; a script whose letters join does not: the top bar
    /// of a Hindi word came apart between its letters (2026-10-14, the inventory's titles), as
    /// Arabic's joins would. The rule of the web's typography too (CSS: a cursive script is not
    /// letter-spaced). So while a component shows a TRANSLATION into such a language, its letter
    /// spacing is 0; it gets the game's back when it shows the game's own text again.
    ///
    /// ⚠ Decided by the translation's own letters, the way the shapers decide (one table, one
    /// rule), never by its language: a translation that holds only Latin ("OK", a name) keeps the
    /// game's spacing, and a language the catalogue does not know is judged like any other.
    /// Only a translation: the game's own text is left as the game spaces it.
    /// ⚠ The spacing is the game's to write too: found other than what the mod left, it is the
    /// game's new value (the rule of the alignment, RtlPresenter.MirrorAlignment).
    /// Engines: TextMesh Pro (characterSpacing, the old one included), NGUI (spacingX), UI Toolkit
    /// (the inline style's letterSpacing). UI.Text and TextMesh have none.
    /// </summary>
    internal static class LetterSpacing
    {
        private sealed class Kept
        {
            public object Original, Written;
        }

        private static readonly Dictionary<long, Kept> _kept = ForgetByNumber.Tracked(new Dictionary<long, Kept>());
        private const BindingFlags Pub = BindingFlags.Public | BindingFlags.Instance;

        /// <summary>
        /// A translation whose letters are shaped together: its logical text (Arabic presented in
        /// its joined forms is read back from what was presented) holds such a letter.
        /// </summary>
        private static bool ShapedTranslation(string value)
        {
            string logical = TranslatorCore.TryGetPresentedLogical(value);
            if (logical == null && !TranslatorCore.IsAlreadyTargetText(value)) return false;
            return ShapingCommon.HoldsShapedLetters(logical ?? value);
        }

        /// <summary>A text about to be shown by a game component (RtlPresenter.Present): its letter spacing for it.</summary>
        internal static void Present(object instance, long compId, string value, bool ownUi)
        {
            if (ownUi || compId == -1 || instance == null || string.IsNullOrEmpty(value)) return;
            bool translation = ShapedTranslation(value);
            if (!translation && !_kept.ContainsKey(compId)) return;
            try
            {
                if (!SpacingOf(instance, out object owner, out PropertyInfo spacing)) return;
                object current = spacing.GetValue(owner, null);
                if (translation)
                {
                    object zero = ZeroOf(spacing.PropertyType);
                    if (zero == null) return;
                    if (!_kept.TryGetValue(compId, out var kept))
                    {
                        // Nothing to take away: the component is left as it is, and not followed.
                        if (IsZero(instance, current)) return;
                        _kept[compId] = kept = new Kept { Original = current };
                    }
                    // Not what the mod left there: the game wrote it since, and it is the game's now.
                    else if (!Equals(current, kept.Written)) kept.Original = current;
                    if (!Equals(current, zero)) spacing.SetValue(owner, zero, null);
                    kept.Written = spacing.GetValue(owner, null);
                    if (DiagnosticOnce.First("LetterSpacing.taken", instance.GetType().Name))
                        TranslatorCore.LogDebug($"[LetterSpacing] {instance.GetType().Name}: the game's letter spacing ({kept.Original}) taken out of a translation whose letters are shaped together — given back with the game's own text");
                }
                else if (_kept.TryGetValue(compId, out var kept))
                {
                    _kept.Remove(compId);
                    // What the game wrote since the mod's own write is the game's: nothing to give back.
                    if (Equals(current, kept.Written)) spacing.SetValue(owner, kept.Original, null);
                }
            }
            // The text then keeps the spacing it had: its joined letters may come apart — said.
            catch (Exception ex) { Faults.Say("LetterSpacing.Present", ex.InnerException ?? ex, instance.GetType().Name); }
        }

        /// <summary>Where a component keeps its letter spacing: TMP's characterSpacing, NGUI's spacingX, UI Toolkit's inline letterSpacing.</summary>
        private static bool SpacingOf(object instance, out object owner, out PropertyInfo spacing)
        {
            owner = instance;
            var type = instance.GetType();
            spacing = type.GetProperty("characterSpacing", Pub) ?? type.GetProperty("spacingX", Pub);
            if (spacing == null && UIToolkitSupport.IsTextElementInstance(instance))
            {
                owner = type.GetProperty("style", Pub)?.GetValue(instance, null);
                spacing = owner?.GetType().GetProperty("letterSpacing", Pub);
            }
            return owner != null && spacing != null && spacing.CanRead && spacing.CanWrite;
        }

        /// <summary>No spacing, in the property's own type: 0, or UI Toolkit's length of 0.</summary>
        private static object ZeroOf(Type type)
        {
            if (type == typeof(float)) return 0f;
            if (type == typeof(int)) return 0;
            var fromFloat = type.GetConstructor(new[] { typeof(float) });
            return fromFloat?.Invoke(new object[] { 0f });
        }

        /// <summary>
        /// No spacing to take away. UI Toolkit: what the element is drawn with (its resolved style),
        /// a style sheet's spacing being no inline value.
        /// </summary>
        private static bool IsZero(object instance, object current)
        {
            if (current is float f) return Math.Abs(f) < 0.0001f;
            if (current is int i) return i == 0;
            object resolved = instance.GetType().GetProperty("resolvedStyle", Pub)?.GetValue(instance, null);
            object drawn = resolved?.GetType().GetProperty("letterSpacing", Pub)?.GetValue(resolved, null);
            return drawn is float r && Math.Abs(r) < 0.0001f;
        }
    }
}
