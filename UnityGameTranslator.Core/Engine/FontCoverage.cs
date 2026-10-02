using System;
using System.Collections.Generic;
using System.Globalization;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Which characters the translation has written with each game font — the question behind
    /// "this font cannot display the translation" (user, 2026-09-30: said for ANY font and any
    /// language, never from a list of scripts). The characters come from the texts actually drawn
    /// with that font, never from the whole file: a font that only draws scores is asked about
    /// digits. Whether the font that draws them HAS them is asked by the caller
    /// (<see cref="Missing"/>), who knows the fonts; this only keeps the account.
    ///
    /// Only visible characters count: spaces, controls, joiners and marks of format, variation
    /// selectors and private-use codepoints (the mod's own shaped glyphs) draw nothing a font must have.
    ///
    /// PURE by contract — linked into Core.Checks. Main thread (the text hooks).
    /// </summary>
    internal sealed class FontCoverage
    {
        private readonly Dictionary<string, HashSet<int>> _needed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        // Hashes of the texts already read per font: a text drawn again costs one lookup. A collision
        // skips one text, whose characters any other text of the translation almost always carries.
        private readonly Dictionary<string, HashSet<int>> _seen = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        // Fonts that drew a text needing shaping (conjuncts, joined letters) they could not shape:
        // every character there, and the text still wrong on screen.
        private readonly HashSet<string> _unshaped = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>A text drawn with this font needed shaping the font could not give it. True when that is new.</summary>
        internal bool NoteUnshaped(string font)
        {
            if (string.IsNullOrEmpty(font) || !_unshaped.Add(font)) return false;
            Version++;
            return true;
        }

        internal bool IsUnshaped(string font) => font != null && _unshaped.Contains(font);

        /// <summary>The fonts' setup changed: whether each can shape is learned again from the texts it draws next.</summary>
        internal void ForgetUnshaped()
        {
            if (_unshaped.Count == 0) return;
            _unshaped.Clear();
            Version++;
        }

        /// <summary>Bumped when a font needs a character it did not before: the answers are due again.</summary>
        internal int Version { get; private set; }

        /// <summary>Whether this text was already read for this font (then there is nothing to do).</summary>
        internal bool Seen(string font, string text)
            => font != null && text != null && _seen.TryGetValue(font, out var set) && set.Contains(text.GetHashCode());

        /// <summary>
        /// Account for one text drawn with a font. Returns true when the font now needs a character
        /// it did not.
        /// </summary>
        /// <param name="source">
        /// 🔴 The source line this text translates, when known: only the characters the translation
        /// BRINGS are counted — those its own source did not have (user's decision, 2026-10-02).
        /// A language name or a developer's placeholder kept as written, a Latin name left in an
        /// Arabic sentence, came from the game's own text: counted, they reported "Arabic
        /// characters missing" for Thai or Korean letters the notice was never about. Per line, not
        /// per translation: a game offering Arabic in its menu holds "العربية" among its sources,
        /// and a translation-wide rule would never have judged Arabic again.
        /// </param>
        internal bool Record(string font, string text, string source = null)
        {
            if (string.IsNullOrEmpty(font) || string.IsNullOrEmpty(text)) return false;
            if (!_seen.TryGetValue(font, out var seen)) _seen[font] = seen = new HashSet<int>();
            if (!seen.Add(text.GetHashCode())) return false;
            if (!_needed.TryGetValue(font, out var needed)) _needed[font] = needed = new HashSet<int>();
            bool added = false;
            for (int i = 0; i < text.Length; i++)
            {
                int cp = text[i];
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    cp = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++;
                }
                if (!Counts(cp)) continue;
                if (source != null && SourceHas(source, cp)) continue;
                if (needed.Add(cp)) added = true;
            }
            if (added) Version++;
            return added;
        }

        /// <summary>Whether the source line holds this codepoint (a surrogate pair read as one).</summary>
        private static bool SourceHas(string source, int cp)
        {
            if (cp <= 0xFFFF) return source.IndexOf((char)cp) >= 0;
            return source.IndexOf(char.ConvertFromUtf32(cp), StringComparison.Ordinal) >= 0;
        }

        /// <summary>A character a font must draw for the text to show.</summary>
        internal static bool Counts(int cp)
        {
            if (cp < 0x21 || (cp >= 0x7F && cp <= 0xA0)) return false;                 // controls and spaces
            if (cp >= 0xE000 && cp <= 0xF8FF) return false;                            // private use (our shaped glyphs)
            if (cp >= 0xF0000) return false;                                           // supplementary private use
            if (cp >= 0xFE00 && cp <= 0xFE0F) return false;                            // variation selectors
            if (cp >= 0xE0100 && cp <= 0xE01EF) return false;
            if (cp >= 0xD800 && cp <= 0xDFFF) return false;                            // a lone surrogate
            var category = TextShaping.UnicodeInfo.CategoryOf(cp);
            return category != UnicodeCategory.Format && category != UnicodeCategory.SpaceSeparator
                && category != UnicodeCategory.LineSeparator && category != UnicodeCategory.ParagraphSeparator
                && category != UnicodeCategory.Control;
        }

        /// <summary>The fonts that have drawn a text of the translation.</summary>
        internal IEnumerable<string> Fonts
        {
            get
            {
                foreach (var f in _needed.Keys) yield return f;
                foreach (var f in _unshaped) if (!_needed.ContainsKey(f)) yield return f;
            }
        }

        /// <summary>
        /// The characters this font drew that <paramref name="covers"/> says the font cannot show —
        /// false only: null ("cannot tell") is never counted, a message must be provable. In
        /// codepoint order.
        /// </summary>
        internal List<int> Missing(string font, Func<int, bool?> covers)
        {
            var missing = new List<int>();
            if (font == null || covers == null || !_needed.TryGetValue(font, out var needed)) return missing;
            foreach (int cp in needed)
                if (covers(cp) == false) missing.Add(cp);
            missing.Sort();
            return missing;
        }

        /// <summary>Forget everything — another translation is loaded.</summary>
        internal void Clear()
        {
            _needed.Clear();
            _seen.Clear();
            _unshaped.Clear();
            Version++;
        }
    }
}
