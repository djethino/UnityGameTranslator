using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Gives the scripts written without spaces between words — Thai, Lao, Khmer, Myanmar — the
    /// word boundaries a text engine needs to wrap a line. None of the engines this mod writes
    /// into finds those boundaries itself (UI Toolkit's Advanced Text Generator excepted): a
    /// Thai paragraph wraps wherever the box ends, in the middle of a word. The boundaries are
    /// found with a dictionary and marked with U+200B ZERO WIDTH SPACE, which every engine
    /// treats as a break opportunity without drawing anything.
    ///
    /// The dictionaries are ICU's own (Unicode license, see Resources/Dictionaries/LICENSE),
    /// embedded compressed and inflated on first use, one script at a time. Segmentation is
    /// the least-words path: over a run of same-script characters, each position keeps the
    /// cheapest way to reach it — a dictionary word costs one, an unknown character more, so a
    /// known word is preferred to spelling it out. Unknown stretches are kept whole rather
    /// than broken letter by letter, and a break is never placed inside a grapheme (before a
    /// combining mark) nor after a vowel that is written before its consonant (Thai เ แ โ ใ ไ,
    /// stored in visual order — see IndicTables.VisualOrderLeft).
    ///
    /// ⚠ Runs on the LOGICAL text and before any other presentation stage: the words in the
    /// dictionary are spelt in storage order, and a Myanmar text reordered first would match
    /// nothing. Pure by contract apart from reading its own embedded resources: no Unity, no
    /// state beyond the inflated dictionaries.
    /// </summary>
    internal static class WordBreaker
    {
        internal const char ZeroWidthSpace = '​';

        private sealed class Script
        {
            public int First, Last;
            public string Resource;
        }

        // Unicode blocks → ICU dictionary. Myanmar's extension blocks belong to its dictionary.
        private static readonly Script[] Scripts =
        {
            new Script { First = 0x0E01, Last = 0x0E5B, Resource = "thaidict" },
            new Script { First = 0x0E81, Last = 0x0EDF, Resource = "laodict" },
            new Script { First = 0x1000, Last = 0x109F, Resource = "burmesedict" },
            new Script { First = 0x1780, Last = 0x17F9, Resource = "khmerdict" },
            new Script { First = 0xA9E0, Last = 0xA9FE, Resource = "burmesedict" },
            new Script { First = 0xAA60, Last = 0xAA7F, Resource = "burmesedict" },
        };

        private sealed class Dictionary
        {
            public HashSet<string> Words;
            public int MaxLength;
        }

        private static readonly System.Collections.Generic.Dictionary<string, Dictionary> _loaded =
            new System.Collections.Generic.Dictionary<string, Dictionary>();

        private const int UnknownCharCost = 4;   // one unknown letter outweighs several words

        /// <summary>Does this text hold any character of a script written without word spaces?</summary>
        internal static bool NeedsBreaking(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; i++)
                if (ScriptOf(text[i]) != null) return true;
            return false;
        }

        /// <summary>
        /// The text with a zero-width space between the words of every run of such a script.
        /// Returns the SAME instance when nothing had to be inserted. <paramref name="whyNot"/>
        /// names a dictionary that could not be read — the text then comes back unchanged.
        /// </summary>
        internal static string Break(string text, out string whyNot)
        {
            whyNot = null;
            if (!NeedsBreaking(text)) return text;

            StringBuilder sb = null;
            int copied = 0;   // text[0..copied) is already in sb (or untouched, when sb is null)
            int i = 0;
            while (i < text.Length)
            {
                var script = ScriptOf(text[i]);
                if (script == null) { i++; continue; }

                int start = i;
                while (i < text.Length && ScriptOf(text[i]) == script) i++;
                int length = i - start;
                if (length < 2) continue;

                var dict = Load(script.Resource, out whyNot);
                if (dict == null) return text;

                var breaks = Segment(text, start, length, dict);
                if (breaks.Count == 0) continue;

                if (sb == null) sb = new StringBuilder(text.Length + 16);
                foreach (int at in breaks)
                {
                    sb.Append(text, copied, at - copied);
                    sb.Append(ZeroWidthSpace);
                    copied = at;
                }
            }
            if (sb == null) return text;
            sb.Append(text, copied, text.Length - copied);
            return sb.ToString();
        }

        /// <summary>Positions (in the whole text) where a break goes, inside one run.</summary>
        private static List<int> Segment(string text, int start, int length, Dictionary dict)
        {
            // best[k]: cheapest cost to segment the first k characters of the run; back[k]: where
            // that last chunk began. A chunk is a dictionary word or a single unknown character.
            var best = new int[length + 1];
            var back = new int[length + 1];
            for (int k = 1; k <= length; k++)
            {
                best[k] = best[k - 1] + UnknownCharCost;
                back[k] = k - 1;
                int longest = Math.Min(dict.MaxLength, k);
                for (int len = 1; len <= longest; len++)
                {
                    int from = k - len;
                    if (dict.Words.Contains(text.Substring(start + from, len)))
                    {
                        int cost = best[from] + 1;
                        if (cost < best[k]) { best[k] = cost; back[k] = from; }
                    }
                }
            }

            // Chunk starts, walked back from the end.
            var starts = new List<int>();
            for (int k = length; k > 0; k = back[k]) starts.Add(back[k]);
            starts.Reverse();

            var breaks = new List<int>();
            for (int s = 1; s < starts.Count; s++)
            {
                int at = start + starts[s];
                int chunkLen = starts[s] - starts[s - 1];
                int nextLen = (s + 1 < starts.Count ? starts[s + 1] : length) - starts[s];
                // Unknown letters stay together: no break between two single unknown characters.
                if (chunkLen == 1 && nextLen == 1
                    && !dict.Words.Contains(text.Substring(at - 1, 1)) && !dict.Words.Contains(text.Substring(at, 1)))
                    continue;
                if (!BreakAllowed(text, at)) continue;
                breaks.Add(at);
            }
            return breaks;
        }

        /// <summary>Never inside a grapheme, never right after a vowel written before its consonant.</summary>
        private static bool BreakAllowed(string text, int at)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(text[at]);
            if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.SpacingCombiningMark
                || cat == UnicodeCategory.EnclosingMark)
                return false;
            return Array.BinarySearch(IndicTables.VisualOrderLeft, (int)text[at - 1]) < 0;
        }

        private static Script ScriptOf(char c)
        {
            for (int s = 0; s < Scripts.Length; s++)
                if (c >= Scripts[s].First && c <= Scripts[s].Last) return Scripts[s];
            return null;
        }

        private static Dictionary Load(string resource, out string whyNot)
        {
            whyNot = null;
            lock (_loaded)
            {
                if (_loaded.TryGetValue(resource, out var dict)) return dict;
                try
                {
                    string name = "UnityGameTranslator.Core.TextShaping.Dictionaries." + resource + ".gz";
                    using (var raw = typeof(WordBreaker).Assembly.GetManifestResourceStream(name))
                    {
                        if (raw == null) { whyNot = $"embedded dictionary '{name}' not found"; return null; }
                        using (var gz = new GZipStream(raw, CompressionMode.Decompress))
                        using (var reader = new StreamReader(gz, Encoding.UTF8))
                        {
                            var words = new HashSet<string>(StringComparer.Ordinal);
                            int max = 1;
                            string line;
                            while ((line = reader.ReadLine()) != null)
                            {
                                if (line.Length == 0) continue;
                                words.Add(line);
                                if (line.Length > max) max = line.Length;
                            }
                            dict = new Dictionary { Words = words, MaxLength = max };
                        }
                    }
                }
                catch (Exception ex)
                {
                    whyNot = $"dictionary '{resource}' could not be read: {ex.GetType().Name}: {ex.Message}";
                    return null;
                }
                _loaded[resource] = dict;
                return dict;
            }
        }
    }
}
