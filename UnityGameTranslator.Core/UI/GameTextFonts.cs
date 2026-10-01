using System;
using System.Collections.Generic;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// The fonts the mod's windows draw the GAME's text with (ModWindowText): the source text font
    /// and the target text font (config source_text_font / target_text_font), each its own — the
    /// interface keeps the interface font (user, 2026-10-01; analyse/polices-fenetres-du-mod.md).
    /// A side with no font of its own draws in the window's font (UniversalUI.DefaultFont).
    ///
    /// ⚠ Where the runtime cannot make a font (IL2CPP builds that stripped CreateDynamicFontFromOSFont)
    /// the window has ONE font object: a side's font can only join the chain of names that object
    /// draws from (TranslatorUIManager.ApplyInterfaceFont), so it supplies the characters the
    /// interface font lacks — a Latin source text stays in the interface font there, and two
    /// derived copies (source AND target needing shaping) share the private-use range. Said in the
    /// log when it applies.
    /// Main thread.
    /// </summary>
    internal static class GameTextFonts
    {
        // Font objects made for a side's font, by its reference as chosen (FontManager.LoadUIFont
        // resolves a font needing shaping to its derived copy's family).
        private static readonly Dictionary<string, UnityEngine.Font> _made = new Dictionary<string, UnityEngine.Font>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _cannotMake = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _chainSaid;

        /// <summary>A side's own font when one is chosen (and present), else null — the window's then.</summary>
        private static string OwnFontOf(GameTextSide side)
        {
            string font = TranslatorCore.WindowFontFor(side);
            return string.Equals(font ?? "", TranslatorCore.WindowFont ?? "", StringComparison.OrdinalIgnoreCase) ? null : font;
        }

        /// <summary>The font object a side draws with: its own when one is chosen and can be made, the window's otherwise.</summary>
        internal static UnityEngine.Font FontFor(GameTextSide side)
        {
            string own = OwnFontOf(side);
            if (own == null || _cannotMake.Contains(own)) return UniversalUI.DefaultFont;
            if (_made.TryGetValue(own, out var made) && made != null) return made;
            made = FontManager.LoadUIFont(own);
            if (made == null)
            {
                _cannotMake.Add(own);
                return UniversalUI.DefaultFont;
            }
            _made[own] = made;
            return made;
        }

        /// <summary>Gives one text of the window its side's font.</summary>
        internal static void Put(UnityEngine.UI.Text text, GameTextSide side)
        {
            if (text == null) return;
            var font = FontFor(side);
            if (font == null || text.font == font) return;
            text.font = font;
            if (!string.IsNullOrEmpty(text.text))
            {
                try { font.RequestCharactersInTexture(text.text, text.fontSize, text.fontStyle); }
                catch (Exception ex) { Faults.Say("GameTextFonts.Put", ex, font.name); }
            }
            text.SetAllDirty();
        }

        /// <summary>
        /// Every text of the window showing the game's text, given its side's font — after a font
        /// changed — and written again: shaping depends on the font (its derived copy, or none), so a
        /// text shaped for the previous font is presented again from its logical text (RtlPresenter
        /// knows what each was shaped for), and a font that cannot shape it is said (the window
        /// notice). A field is redrawn by its own update, from what it holds.
        /// </summary>
        internal static void PutAll()
        {
            foreach (var piece in ModWindowText.All())
            {
                Put(piece.Text, piece.Side);
                if (piece.Field is UnityEngine.UI.InputField field) field.ForceLabelUpdate();
                else if (!string.IsNullOrEmpty(piece.Text.text)) piece.Text.text = piece.Text.text;
            }
        }

        /// <summary>
        /// The side fonts this runtime could not make into font objects, in chain order (target, then
        /// source): what the window's single font must also draw from. Empty wherever fonts can be made.
        /// </summary>
        internal static List<string> Unmade()
        {
            var names = new List<string>();
            foreach (var side in new[] { GameTextSide.Target, GameTextSide.Source })
            {
                string own = OwnFontOf(side);
                if (own == null) continue;
                FontFor(side);   // tries to make it once
                if (_cannotMake.Contains(own) && !names.Contains(own)) names.Add(own);
            }
            if (names.Count > 0 && !_chainSaid)
            {
                _chainSaid = true;
                TranslatorCore.LogInfo("[UIManager] This runtime cannot make fonts: the source/target text fonts join the window font's chain — "
                    + "they draw only the characters the interface font lacks");
            }
            return names;
        }

        /// <summary>
        /// A side font whose derived copy was rewritten: its font object is made again at the next use.
        /// False when it had none (never made, or this runtime cannot make it).
        /// </summary>
        internal static bool Forget(string fontName)
        {
            if (string.IsNullOrEmpty(fontName)) return false;
            var stale = new List<string>();
            foreach (var reference in _made.Keys)
                if (string.Equals(UnityGameTranslator.Common.FontReferences.Name(reference), fontName, StringComparison.OrdinalIgnoreCase))
                    stale.Add(reference);
            foreach (var reference in stale) _made.Remove(reference);
            return stale.Count > 0;
        }
    }
}
