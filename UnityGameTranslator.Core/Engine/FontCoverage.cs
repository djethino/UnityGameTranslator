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
        /// <summary>One line of the translation drawn with a font: what it brought, and when it was last drawn.</summary>
        internal sealed class Line
        {
            public string Source;     // the source line it translates, null when unknown
            public string Text;       // the translated text as last drawn
            public HashSet<int> Brings = new HashSet<int>();
            public long Order;        // when it was last recorded: the latest is the one a row names
        }

        // Per font, the lines it drew, by identity: the source line, else the text itself. 🔴 Per line
        // and not one set per font (2026-10-04): a line corrected in the editor takes back what it
        // alone brought when it is drawn again — with one set, the notice stayed until the game was
        // restarted — and a font with a fallback can say how many lines lack characters, and which.
        private readonly Dictionary<string, Dictionary<string, Line>> _lines = new Dictionary<string, Dictionary<string, Line>>(StringComparer.Ordinal);
        // Per font, how many of its lines bring each character: needed while one does.
        private readonly Dictionary<string, Dictionary<int, int>> _needed = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        private long _order;
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
            if (!_lines.TryGetValue(font, out var lines)) _lines[font] = lines = new Dictionary<string, Line>(StringComparer.Ordinal);
            if (!_needed.TryGetValue(font, out var needed)) _needed[font] = needed = new Dictionary<int, int>();

            var brings = new HashSet<int>();
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
                brings.Add(cp);
            }

            bool changed = false;
            if (source != null && lines.TryGetValue(source, out var line))
            {
                // The same line drawn with another text (corrected, retranslated): its new characters
                // replace its old ones, and its old text is read again if it ever comes back.
                seen.Remove(line.Text.GetHashCode());
                foreach (int cp in line.Brings)
                    if (!brings.Contains(cp) && --needed[cp] == 0) { needed.Remove(cp); changed = true; }
                foreach (int cp in brings)
                    if (!line.Brings.Contains(cp)) changed |= Need(needed, cp);
                line.Text = text;
                line.Brings = brings;
                line.Order = ++_order;
            }
            else if (source != null || brings.Count > 0)
            {
                // A line known by its source is kept even bringing nothing: its next text replaces it.
                // A text with no source cannot be replaced, and one bringing nothing is not kept.
                lines[source ?? text] = new Line { Source = source, Text = text, Brings = brings, Order = ++_order };
                foreach (int cp in brings) changed |= Need(needed, cp);
            }
            if (changed) Version++;
            return changed;
        }

        private static bool Need(Dictionary<int, int> needed, int cp)
        {
            needed.TryGetValue(cp, out int count);
            needed[cp] = count + 1;
            return count == 0;
        }

        /// <summary>
        /// The lines drawn with this font that bring one of <paramref name="missing"/>, the latest
        /// first — what a font with a fallback says under its row: how many, and the last one seen.
        /// </summary>
        internal List<Line> LinesBringing(string font, ICollection<int> missing)
        {
            var found = new List<Line>();
            if (font == null || missing == null || missing.Count == 0 || !_lines.TryGetValue(font, out var lines)) return found;
            foreach (var line in lines.Values)
                foreach (int cp in line.Brings)
                    if (missing.Contains(cp)) { found.Add(line); break; }
            found.Sort((a, b) => b.Order.CompareTo(a.Order));
            return found;
        }

        /// <summary>
        /// How many characters of a line a font's row shows (user, 2026-10-04: "on limite à un nombre
        /// de caractères") — enough to recognise the line in the editor, short enough for one row.
        /// </summary>
        internal const int ExcerptLength = 40;

        /// <summary>
        /// The part of a line around the characters <paramref name="missing"/> holds (user,
        /// 2026-10-04): "...some normal text, the broken characters, some normal text...". The broken
        /// characters come first: the text around them gets only what they leave of
        /// <paramref name="length"/>, shared between both sides — a line made of broken characters
        /// shows only those. Cut on whole characters, never between a letter and its marks.
        /// </summary>
        internal static string Excerpt(string text, ICollection<int> missing, int length = ExcerptLength)
        {
            if (string.IsNullOrEmpty(text) || length <= 0) return text ?? "";
            var cps = new List<int>(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    cps.Add(char.ConvertToUtf32(text[i], text[i + 1]));
                    i++;
                }
                else cps.Add(text[i]);
            }
            int n = cps.Count, first = -1, last = -1;
            if (missing != null)
                for (int i = 0; i < n; i++)
                    if (missing.Contains(cps[i])) { if (first < 0) first = i; last = i; }

            int start, end;
            if (first < 0) { start = 0; end = Math.Min(n, length); }
            else if (last - first + 1 >= length) { start = first; end = first + length; }
            else
            {
                int room = length - (last - first + 1);
                start = first - room / 2;
                end = last + 1 + (room - room / 2);
                if (start < 0) { end -= start; start = 0; }
                if (end > n) { start = Math.Max(0, start - (end - n)); end = n; }
            }
            while (start > 0 && IsMark(cps[start])) start--;
            while (end < n && IsMark(cps[end])) end++;

            var excerpt = new System.Text.StringBuilder();
            for (int i = start; i < end; i++)
            {
                if (cps[i] > 0xFFFF) excerpt.Append(char.ConvertFromUtf32(cps[i]));
                else excerpt.Append((char)cps[i]);   // a lone surrogate stays as written
            }
            string middle = excerpt.ToString();
            if (start > 0) middle = "..." + middle.TrimStart();
            if (end < n) middle = middle.TrimEnd() + "...";
            return middle;
        }

        private static bool IsMark(int cp)
        {
            var category = TextShaping.UnicodeInfo.CategoryOf(cp);
            return category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark
                || category == UnicodeCategory.EnclosingMark;
        }

        /// <summary>Whether the source line holds this codepoint (a surrogate pair read as one).</summary>
        private static bool SourceHas(string source, int cp)
        {
            if (cp <= 0xFFFF) return source.IndexOf((char)cp) >= 0;
            return source.IndexOf(char.ConvertFromUtf32(cp), StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Whether one of these characters is written in the script of ISO 15924 code
        /// <paramref name="isoScript"/> (the catalogue's script of a language, Languages.ScriptOf) —
        /// by Unicode's script of the character. Only then does a notice say "Arabic characters": a
        /// bullet or a stray Japanese word missing from a font is not the language missing
        /// (2026-10-03). A language written in several scripts at once (Japanese: "Jpan") matches
        /// none, and the notice stays general — never wrong, only less precise.
        /// </summary>
        internal static bool AnyWrittenIn(IEnumerable<int> characters, string isoScript)
        {
            if (characters == null || string.IsNullOrEmpty(isoScript)) return false;
            foreach (int cp in characters)
            {
                string tag = TextShaping.ShapingTables.ScriptTags[TextShaping.ShapingCommon.ScriptOf(cp)];
                if (tag != null && string.Equals(tag.TrimEnd(), isoScript, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
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
                foreach (var f in _decorated) if (!_needed.ContainsKey(f) && !_unshaped.Contains(f)) yield return f;
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
            foreach (int cp in needed.Keys)
                if (covers(cp) == false) missing.Add(cp);
            missing.Sort();
            return missing;
        }

        // Fonts whose game texts underline, strike through or highlight (DecoratesText).
        private readonly HashSet<string> _decorated = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// A game text drawn with this font — the source line of a translation, or the game's own
        /// text — carries a decoration TMP draws with the font's "_" (DecoratesText). Judged on the
        /// source: what the developer wanted shown (user, 2026-10-04). True when that is new for the font.
        /// </summary>
        internal bool NoteDecorated(string font, string text)
        {
            if (string.IsNullOrEmpty(font) || !DecoratesText(text) || !_decorated.Add(font)) return false;
            Version++;
            return true;
        }

        internal bool IsDecorated(string font) => font != null && _decorated.Contains(font);

        /// <summary>
        /// Whether a text asks TextMesh Pro for an underline, a strikethrough or a highlight —
        /// &lt;u&gt;, &lt;s&gt;, &lt;mark&gt;, with or without a value — the three things TMP draws
        /// with the "_" of the component's own font, and only of that font (TMP_Text
        /// .GetUnderlineSpecialCharacter, read in TMP 1.4 to 3.0 and Unity 6's).
        /// </summary>
        internal static bool DecoratesText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = text.IndexOf('<'); i >= 0; i = text.IndexOf('<', i + 1))
            {
                int name = i + 1;
                if (Tag(text, name, "u") || Tag(text, name, "s") || Tag(text, name, "mark")) return true;
            }
            return false;
        }

        // The tag name at this position, followed by its end, a value or an attribute.
        private static bool Tag(string text, int at, string name)
        {
            if (at + name.Length > text.Length || string.Compare(text, at, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0) return false;
            int after = at + name.Length;
            if (after >= text.Length) return false;
            char next = text[after];
            return next == '>' || next == '=' || next == ' ';
        }

        /// <summary>Forget everything — another translation is loaded.</summary>
        internal void Clear()
        {
            _needed.Clear();
            _lines.Clear();
            _seen.Clear();
            _unshaped.Clear();
            _decorated.Clear();
            Version++;
        }
    }
}
