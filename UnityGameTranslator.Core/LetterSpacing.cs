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
    /// ⚠ A font asset's own spacings (bold, normal) are not the component's: TextMesh Pro takes
    /// them from the asset that draws each letter, and the mod's fonts of such scripts carry none
    /// (CustomFontLoader.UnspaceShapedLetters).
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
            if (TranslatorCore.DebugMode && ShapingCommon.HoldsShapedLetters(value) && DiagnosticOnce.First("LetterSpacing.seen", compId.ToString()))
                TranslatorCore.LogDebug($"[LetterSpacing] comp={compId} seen: {WhatSpaces(instance, value)} ; translation of ours {translation}");
            if (!translation && !_kept.ContainsKey(compId))
            {
                // Letters that must not be spaced, on a component that may space them, not taken for
                // a translation of ours: left as the game wrote it — said once per text, with why.
                if (TranslatorCore.DebugMode && ShapingCommon.HoldsShapedLetters(value) && DiagnosticOnce.First("LetterSpacing.notOurs", value))
                    TranslatorCore.LogDebug($"[LetterSpacing] comp={compId} {instance.GetType().Name}: '{Clip(value)}' holds shaped letters but is no translation of ours (presented {TranslatorCore.TryGetPresentedLogical(value) != null}, already target {TranslatorCore.IsAlreadyTargetText(value)}) — its spacing left to the game");
                return;
            }
            try
            {
                if (!SpacingOf(instance, out object owner, out PropertyInfo spacing)) return;
                object current = spacing.GetValue(owner, null);
                if (translation)
                {
                    object none = ZeroOf(spacing.PropertyType);
                    if (none == null) return;
                    if (!_kept.TryGetValue(compId, out var kept))
                    {
                        // Nothing to take away: the component is left as it is, and not followed.
                        if (IsZero(instance, current)) return;
                        _kept[compId] = kept = new Kept { Original = current };
                    }
                    // Not what the mod left there: the game wrote it since, and it is the game's now.
                    else if (!Equals(current, kept.Written)) kept.Original = current;
                    if (!Equals(current, none)) spacing.SetValue(owner, none, null);
                    kept.Written = spacing.GetValue(owner, null);
                    if (TranslatorCore.DebugMode && DiagnosticOnce.First("LetterSpacing.taken", compId.ToString()))
                        TranslatorCore.LogDebug($"[LetterSpacing] comp={compId} {instance.GetType().Name}: '{Clip(value)}' — the game's letter spacing ({kept.Original}) set to {kept.Written} — given back with the game's own text");
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

        private static string Clip(string text) => text.Length > 40 ? text.Substring(0, 40) + "…" : text;

        /// <summary>
        /// For the debug log: everything that sets how far apart a component draws its letters —
        /// the text it showed before this one (tags included), its spacings, its style, its font and that font's own.
        /// </summary>
        private static string WhatSpaces(object instance, string value)
        {
            var type = instance.GetType();
            object Read(object on, string name)
            {
                if (on == null) return null;
                var t = on.GetType();
                try { return t.GetProperty(name, Pub)?.GetValue(on, null) ?? t.GetField(name, Pub)?.GetValue(on); }
                catch (Exception ex) { return "unreadable " + (ex.InnerException ?? ex).GetType().Name; }
            }
            object font = Read(instance, "font");
            string written = Read(instance, "text") as string ?? "";
            var sb = new System.Text.StringBuilder();
            sb.Append(type.Name).Append(" '").Append(Clip(value)).Append("' (it showed '").Append(Clip(Escaped(written))).Append("')");
            foreach (var name in new[] { "characterSpacing", "wordSpacing", "fontStyle", "fontSize", "enableKerning", "richText", "isRightToLeftText", "monoSpacing" })
            {
                object v = Read(instance, name);
                if (v != null) sb.Append(' ').Append(name).Append('=').Append(v);
            }
            sb.Append(" font '").Append(font is UnityEngine.Object o ? o.name : "none").Append('\'');
            foreach (var name in new[] { "normalSpacingOffset", "boldSpacing", "normalStyle", "boldStyle" })
            {
                object v = Read(font, name);
                if (v != null) sb.Append(' ').Append(name).Append('=').Append(v);
            }
            return sb.ToString();
        }

        /// <summary>A text for one log line: its line breaks shown as \n.</summary>
        private static string Escaped(string text) => text.Replace("\n", "\\n").Replace("\r", "\\r");

        /// <summary>A TextMesh Pro font asset's own letter spacing (normalSpacingOffset), for the log; 0 for anything else.</summary>
        internal static float FontSpacing(object font)
        {
            if (font == null) return 0f;
            var type = font.GetType();
            object value = type.GetProperty("normalSpacingOffset", Pub)?.GetValue(font, null) ?? type.GetField("normalSpacingOffset", Pub)?.GetValue(font);
            return value is float f ? f : 0f;
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
