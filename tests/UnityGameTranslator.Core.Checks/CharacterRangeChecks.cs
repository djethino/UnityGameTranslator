using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// No decision about a character is written by hand for one script.
    ///
    /// 🔴 What a character IS — its direction, its script, where a line may break, what joins it to
    /// the next — is published by Unicode for every script, and the mod carries that data generated
    /// from the official files (ShapingTables.g.cs, the bidi classes of RichTextKit). A range typed
    /// in the code answers for the scripts its author thought of and is silently wrong for the
    /// others: "right to left" meant Hebrew and Arabic only for a month, line breaking knew Chinese
    /// and Japanese only (2026-10-02, analyse/ecritures-non-universelles-inventaire.md). A fact about
    /// a LANGUAGE (which script it is written in) belongs to the language catalogue.
    ///
    /// Read from the sources, comments out: a comparison or a <c>case</c> on a code point from U+0080
    /// up (hex, <c>\u</c> escape or the character itself), a character literal of one, a string of
    /// such characters searched in, and a comparison to a script by name. Allowed:
    /// <list type="bullet">
    /// <item>the facts Unicode states for every script (<see cref="UnicodeStructure"/>);</item>
    /// <item>files that are not about text (binary formats), vendored libraries, generated tables, and
    /// the shapers that TRANSCRIBE the OpenType specification of one script, as HarfBuzz does
    /// (<see cref="Exempt"/>, each with its reason);</item>
    /// <item>the faults already inventoried (<see cref="KnownFaults"/>), each tied to its entry in the
    /// inventory. That list only shrinks: a fault fixed and still listed fails too.</item>
    /// </list>
    /// </summary>
    internal static class CharacterRangeChecks
    {
        /// <summary>Code points whose meaning Unicode fixes for every script.</summary>
        private static readonly HashSet<int> UnicodeStructure = new HashSet<int>
        {
            0x80, 0x9F, 0xA0,                       // C1 controls, no-break space
            0xFFFE, 0xFFFF, 0x10000, 0x10FFFF, 0x110000,   // plane bounds
            0xD800, 0xDBFF, 0xDC00, 0xDFFF,         // surrogates
            0xE000, 0xF8FF, 0xF0000, 0xFFFFD, 0x10FFFD,    // private use
            0xFE00, 0xFE0F, 0xE0100, 0xE01EF,       // variation selectors
            0x200B, 0x200C, 0x200D, 0x200E, 0x200F, // zero width space, non-joiner, joiner, direction marks
            0x202A, 0x202B, 0x202C, 0x202D, 0x202E, // bidi embeddings and overrides
            0x2066, 0x2067, 0x2068, 0x2069,         // bidi isolates
            0x25CC,                                  // dotted circle, the base of a lone mark in every shaper
            0xFEFF,                                  // byte order mark
        };

        /// <summary>Files or folders not read, and why (path relative to the Core, '/' separated).</summary>
        private static readonly Dictionary<string, string> Exempt = new Dictionary<string, string>
        {
            ["Rasterizer/"] = "font file formats: glyph counts, table sizes and markers, not characters",
            ["Engine/FontFolderRedirect.cs"] = "reads a Windows PE header",
            ["TextShaping/RichTextKit/"] = "vendored bidi algorithm and its Unicode trie",
            ["TextShaping/RTLTMPro/"] = "vendored Arabic presentation-form tables",
            ["TextShaping/IndicShaper.cs"] = "transcribes the OpenType Indic shaping specification (HarfBuzz hb-ot-shaper-indic)",
            ["TextShaping/IndicReorderer.cs"] = "Indic shaping specification",
            ["TextShaping/KhmerShaper.cs"] = "transcribes the OpenType Khmer shaping specification (HarfBuzz hb-ot-shaper-khmer)",
            ["TextShaping/MyanmarShaper.cs"] = "transcribes the OpenType Myanmar shaping specification (HarfBuzz hb-ot-shaper-myanmar)",
            ["TextShaping/UseShaper.cs"] = "transcribes the Universal Shaping Engine specification",
            ["TextShaping/DefaultShaper.cs"] = "Thai and Lao SARA AM, as HarfBuzz hb-ot-shaper-thai does",
            ["TextShaping/PresentationFormsShaper.cs"] = "Unicode's Arabic presentation forms: a fallback that exists for that script only, by Unicode's design",
            ["TextShaping/WordBreaker.cs"] = "names the script each ICU dictionary it ships was made for; which characters need one is Unicode's Line_Break SA",
        };

        /// <summary>
        /// The one place a script is matched by name to its shaping engine — the dispatch HarfBuzz
        /// itself writes per script. Only that method of OpenTypeShaping.cs may name scripts.
        /// </summary>
        private const string EngineDispatchFile = "TextShaping/OpenTypeShaping.cs";

        /// <summary>
        /// Faults inventoried and not fixed yet: file → the literals or script names found there, and
        /// the inventory entry that says what replaces them. Fixing one means removing it here.
        /// </summary>
        private static readonly Dictionary<string, (string[] found, string entry)> KnownFaults = new Dictionary<string, (string[], string)>
        {
        };

        // A comparison, a case, a dictionary key — and an assignment or initializer: a range kept as
        // data ({ First = 0x0E01, Last = 0x0E5B }) is compared later, out of this regex's sight.
        private static readonly Regex HexCompare = new Regex(@"(?:==|!=|<=|>=|<|>)\s*\(?\s*(?:\(char\)|\(int\))?\s*0x([0-9A-Fa-f]+)\b|\b0x([0-9A-Fa-f]+)\s*(?:==|!=|<=|>=|<|>)|\bcase\s+0x([0-9A-Fa-f]+)\b|\[0x([0-9A-Fa-f]+)\]\s*=|(?<![=<>!])=\s*0x([0-9A-Fa-f]+)\b");

        /// <summary>Per file: a code point used for what Unicode says of it in every script, and why.</summary>
        private static readonly Dictionary<string, Dictionary<int, string>> AllowedIn = new Dictionary<string, Dictionary<int, string>>
        {
            ["TextShaping/RtlComposer.cs"] = new Dictionary<int, string> { [0x0300] = "stands in for a lifted tag in the bidi run: any character of class NSM would" },
            ["TextShaping/ShapingCommon.cs"] = new Dictionary<int, string> { [0x25CC] = "the dotted circle every shaper puts under a lone mark" },
            ["TextShaping/RtlText.cs"] = new Dictionary<int, string>
            {
                // Presentation-form blocks: Unicode gives them to Hebrew and Arabic only, by design.
                [0xFB1D] = "presentation forms", [0xFB4F] = "presentation forms", [0xFB50] = "presentation forms",
                [0xFDFF] = "presentation forms", [0xFE70] = "presentation forms",
            },
        };

        private static bool PrivateUse(int cp) => (cp >= 0xE000 && cp <= 0xF8FF) || cp >= 0xF0000;
        private static readonly Regex CharLiteral = new Regex(@"'(\\u[0-9A-Fa-f]{4}|[^\x00-\x7F'\\])'");
        private static readonly Regex SearchedString = new Regex("\"([^\"]*[^\\x00-\\x7F][^\"]*)\"\\s*\\.\\s*(?:IndexOf|Contains|IndexOfAny)\\s*\\(");
        // Either side of the comparison, the type written whole or through the "S" alias.
        private static readonly Regex ScriptCompare = new Regex(@"(?:==|!=)\s*(?:\w+\.)*?(?:Script|S)\.([A-Z]\w*)\b|\b(?:\w+\.)*?(?:Script|S)\.([A-Z]\w*)\s*(?:==|!=)");
        private static readonly HashSet<string> NeutralScripts = new HashSet<string> { "Common", "Inherited", "Unknown" };

        // The runtime's character tables: CharUnicodeInfo and char.IsLetter & co. answer from the
        // Unicode of the .NET a game ships (6, 8, or a corlib trimmed in half). Only UnicodeInfo,
        // which reads the mod's generated tables, may answer what a character is.
        private static readonly Regex RuntimeUnicode = new Regex(@"\bCharUnicodeInfo\b|\bchar\.Is(Letter|Digit|LetterOrDigit|WhiteSpace|Control|Punctuation|Symbol|Separator|Number|Upper|Lower)\s*\(");

        public static void Run(Action<bool, string, string> check)
        {
            string core = FindCoreFolder();
            check(core != null, "the Core sources are found", "the check reads files; without them it proves nothing");
            if (core == null) return;

            var found = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var runtimeAsked = new List<string>();
            int files = 0;
            foreach (var path in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(core.Length + 1).Replace('\\', '/');
                if (rel.EndsWith(".g.cs", StringComparison.Ordinal)) continue;   // generated from Unicode's files
                if (Exempt.Keys.Any(k => !k.Contains("#") && (k.EndsWith("/") ? rel.StartsWith(k, StringComparison.Ordinal) : rel == k))) continue;
                files++;
                string source = StripComments(File.ReadAllText(path));
                var here = new SortedSet<string>(StringComparer.Ordinal);
                if (rel != "TextShaping/UnicodeInfo.cs" && RuntimeUnicode.IsMatch(source)) runtimeAsked.Add(rel);

                foreach (Match m in HexCompare.Matches(source))
                {
                    string hex = null;
                    for (int g = 1; g <= 5 && hex == null; g++) if (m.Groups[g].Success) hex = m.Groups[g].Value;
                    if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)) continue;
                    if (cp < 0x80 || cp > 0x110000 || UnicodeStructure.Contains(cp) || PrivateUse(cp)) continue;
                    if (AllowedIn.TryGetValue(rel, out var allowed) && allowed.ContainsKey(cp)) continue;
                    here.Add("U+" + cp.ToString("X4"));
                }
                foreach (Match m in CharLiteral.Matches(source))
                {
                    string body = m.Groups[1].Value;
                    int cp = body.StartsWith("\\u") ? int.Parse(body.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) : body[0];
                    if (cp < 0x80 || UnicodeStructure.Contains(cp) || PrivateUse(cp)) continue;
                    if (AllowedIn.TryGetValue(rel, out var allowedHere) && allowedHere.ContainsKey(cp)) continue;
                    here.Add("U+" + cp.ToString("X4"));
                }
                if (SearchedString.IsMatch(source)) here.Add("STRING");
                foreach (Match m in ScriptCompare.Matches(rel == EngineDispatchFile ? WithoutEngineOf(source) : source))
                {
                    string name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (!NeutralScripts.Contains(name)) here.Add("Script." + name);
                }

                if (here.Count > 0) found[rel] = here;
            }
            check(files > 20, "the Core's files are read", files + " files");
            check(runtimeAsked.Count == 0, "no file asks the game's runtime what a character is",
                runtimeAsked.Count == 0 ? "UnicodeInfo answers, at the mod's own Unicode"
                    : string.Join(", ", runtimeAsked) + " — ask TextShaping.UnicodeInfo, not CharUnicodeInfo or char.IsX");

            var unexpected = new List<string>();
            foreach (var kv in found)
            {
                KnownFaults.TryGetValue(kv.Key, out var known);
                var extra = kv.Value.Where(v => known.found == null || !known.found.Contains(v)).ToList();
                if (extra.Count > 0) unexpected.Add(kv.Key + ": " + string.Join(" ", extra));
            }
            check(unexpected.Count == 0, "no decision about a character is written for one script",
                unexpected.Count == 0 ? "every character fact comes from Unicode's data or the catalogue"
                    : string.Join(" | ", unexpected) + " — read it from the generated Unicode tables (ShapingCommon, UnicodeClasses), or the language catalogue");

            var stale = new List<string>();
            foreach (var kv in KnownFaults)
            {
                found.TryGetValue(kv.Key, out var here);
                var gone = kv.Value.found.Where(v => here == null || !here.Contains(v)).ToList();
                if (gone.Count > 0) stale.Add(kv.Key + ": " + string.Join(" ", gone));
            }
            check(stale.Count == 0, "the inventoried faults still listed are still there",
                stale.Count == 0 ? KnownFaults.Count + " files still to fix (analyse/ecritures-non-universelles-inventaire.md)"
                    : string.Join(" | ", stale) + " — fixed: remove it from KnownFaults and from the inventory");
        }

        // EngineOf names scripts by design (the per-script dispatch HarfBuzz writes); the rest of
        // its file may not.
        private static string WithoutEngineOf(string source)
        {
            int start = source.IndexOf("private static Engine EngineOf(", StringComparison.Ordinal);
            if (start < 0) return source;
            int end = source.IndexOf("\n        }", start, StringComparison.Ordinal);
            return end < 0 ? source : source.Remove(start, end - start);
        }

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", "");
        }

        /// <summary>Up from the binary until the Core folder is found.</summary>
        private static string FindCoreFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "UnityGameTranslator.Core");
                if (Directory.Exists(Path.Combine(candidate, "UI"))) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
