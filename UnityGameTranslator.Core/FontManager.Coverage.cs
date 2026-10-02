using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// "This font cannot display the translation" — for any game font and any language (user,
    /// 2026-09-30: no list of scripts; say the current font seems to lack the target language's
    /// characters, with a way to the font replacement and an Ignore). The characters are those the
    /// translation actually wrote with the font (FontCoverage, fed by the text hooks); whether the
    /// font that draws them has them is answered here, from what can be PROVED:
    /// - a replacement legacy text is drawn with as a GAME font object: that object's own answer;
    /// - a replacement with a file (fonts/, or installed): the file's character map;
    /// - the game's TextMesh Pro asset: its own HasCharacter, fallbacks included, adding the
    ///   character when the asset is dynamic — what drawing it would do;
    /// - the game's legacy Font: Font.HasCharacter, the probe the clone rule already trusts.
    /// A font none of these can speak for (UI Toolkit, tk2d, NGUI, a probe the runtime stripped) is
    /// never reported: a message must be provable.
    /// Main thread (Unity objects).
    /// </summary>
    public static partial class FontManager
    {
        internal static readonly FontCoverage Coverage = new FontCoverage();

        /// <summary>
        /// Accounts for a translated text drawn with a game font. Called by the text hooks
        /// (RtlPresenter.Present) before any shaping, with the logical text. Only our translations
        /// count: the game's own texts are its font's business.
        /// </summary>
        internal static void NoteTextDrawn(long compId, string settingsFontName, string text)
        {
            if (string.IsNullOrEmpty(settingsFontName) || string.IsNullOrEmpty(text)) return;
            if (IsOurFont(settingsFontName)) return;
            if (compId != -1) _fontOfComponent[compId] = settingsFontName;
            if (Coverage.Seen(settingsFontName, text)) return;
            if (!TranslatorCore.IsAlreadyTargetText(text))
            {
                // Remembered as read: a text that is not ours now is the source of one, a different string.
                Coverage.Record(settingsFontName, "");
                return;
            }
            // Only what the translation BRINGS is judged: the letters of its own source line are the
            // game's, drawn by this font before the mod came (FontCoverage.Record). A shaped text is
            // found through the logical line it presents.
            string source = TranslatorCore.SourceOfTranslation(text);
            if (source == null)
            {
                string logical = TranslatorCore.TryGetPresentedLogical(text);
                if (logical != null) source = TranslatorCore.SourceOfTranslation(logical);
            }
            if (source != null && !_crossingFonts.Contains(settingsFontName))
            {
                int from = TextShaping.RtlText.ParagraphDirection(UnityGameTranslator.Common.Markup.Strip(source));
                int to = TextShaping.RtlText.ParagraphDirection(UnityGameTranslator.Common.Markup.Strip(text));
                if (from != 0 && to != 0 && from != to) _crossingFonts.Add(settingsFontName);
            }
            Coverage.Record(settingsFontName, text, source);
        }

        // Fonts that drew a translation written in the other direction than its source line.
        private static readonly HashSet<string> _crossingFonts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this font drew a translated text whose direction differs from the game's line it
        /// replaces — each by Unicode's paragraph rule (RtlText.ParagraphDirection), measured on the
        /// texts themselves: it needs no language, and holds when the source is "auto" (user,
        /// 2026-10-02). Forgotten with the translation (TranslatorCore's reload clears Coverage and this).
        /// </summary>
        internal static bool DrawsCrossedDirection(string settingsFontName) =>
            !string.IsNullOrEmpty(settingsFontName) && _crossingFonts.Contains(settingsFontName);

        /// <summary>Whether any font drew such a text.</summary>
        internal static bool AnyFontCrossesDirection => _crossingFonts.Count > 0;

        /// <summary>The account belongs to the translation it was taken on.</summary>
        internal static void ForgetCrossedDirections() => _crossingFonts.Clear();

        /// <summary>
        /// A font the mod made — a derived copy or pool name ("UGT …"), a replacement it created. The
        /// notice is about the GAME's fonts: one of ours reaching here is a component whose game font
        /// could not be told (GetSettingsFontName), and naming our own copy to the player — "'UGT Sys
        /// Adobe Devanagari #0' draws Hindi incorrectly" (2026-10-02) — sends them to a font they
        /// cannot set. Also the one test the detection asks (Register*, the game font lists): the
        /// copy drawing the mod's window was listed as a game font and offered as "[Game] UGT Sys …".
        /// </summary>
        private static bool IsOurFont(string fontName) =>
            DerivedFonts.IsOurFamily(fontName) || _createdFallbackFontNames.Contains(fontName);

        // The game font each component last drew a text with, by component: what the inspector asks
        // for the texts it found (FontOfComponent).
        private static readonly Dictionary<long, string> _fontOfComponent = new Dictionary<long, string>();

        /// <summary>The game font (its name in the font settings) a component last drew a text with; null when unknown.</summary>
        internal static string FontOfComponent(long compId) =>
            _fontOfComponent.TryGetValue(compId, out var font) ? font : null;

        // ── the answers, cached until something they depend on changes ──

        private static int _missingAtVersion = -1;
        private static int _missingAtSettings;
        private static readonly Dictionary<string, List<int>> _missingByFont = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<int>> _cmapByPath = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _installedPathByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// What the answers depend on besides the texts: the replacements chosen, whether replacing is
        /// on, the fonts/ files known, the game fonts loaded and the objects drawing legacy text. Read at every ask — a handful of
        /// entries — so no change of setting, file or scene has to remember to say it (the answers
        /// follow the state, not the events that changed it).
        /// </summary>
        private static int SettingsSignature()
        {
            unchecked
            {
                int h = TranslatorCore.FontReplacementActive ? 1 : 2;
                h = h * 31 + CustomFontLoader.CustomFonts.Count;
                h = h * 31 + _detectedTMPFontObjects.Count;
                h = h * 31 + _gameUnityFonts.Count;
                foreach (var kv in TranslatorCore.FontSettingsMap)
                    h = h * 31 + (kv.Key.GetHashCode() ^ (kv.Value?.fallback ?? "").GetHashCode());
                // The objects legacy text is drawn with (DrawingGameFont): made as texts need them.
                foreach (var kv in _unityFallbackFonts)
                    h = h * 31 + (kv.Key.GetHashCode() ^ (kv.Value != null ? kv.Value.GetInstanceID() : 0));
                return h;
            }
        }

        /// <summary>A game font that cannot display the translation correctly: characters it lacks, or text it cannot shape.</summary>
        internal struct FontProblem
        {
            public string Font;
            public int Missing;       // characters of the translation the drawing font lacks
            public bool Unshaped;     // a text needing shaping (joined letters, conjuncts) shown without it
        }

        /// <summary>Every game font that cannot display what the translation wrote with it, by name.</summary>
        internal static List<FontProblem> FontProblems()
        {
            Refresh();
            var list = new List<FontProblem>();
            foreach (var font in Coverage.Fonts)
            {
                _missingByFont.TryGetValue(font, out var missing);
                int count = missing?.Count ?? 0;
                bool unshaped = Coverage.IsUnshaped(font);
                if (count > 0 || unshaped) list.Add(new FontProblem { Font = font, Missing = count, Unshaped = unshaped });
            }
            list.Sort((a, b) => string.CompareOrdinal(a.Font, b.Font));
            return list;
        }

        /// <summary>This game font's problem, or null when it displays the translation (or nothing can tell).</summary>
        internal static FontProblem? ProblemOf(string settingsFontName)
        {
            foreach (var p in FontProblems())
                if (string.Equals(p.Font, settingsFontName, StringComparison.Ordinal)) return p;
            return null;
        }

        /// <summary>
        /// A text drawn with this game font needed shaping its font could not give (ShapingRoute said
        /// "reorder only"): every character may be there, and the text is still wrong on screen.
        /// </summary>
        internal static void NoteUnshaped(string settingsFontName, string text)
        {
            if (IsOurFont(settingsFontName)) return;
            if (Coverage.NoteUnshaped(settingsFontName))
                TranslatorCore.LogInfo($"[FontManager] '{settingsFontName}' draws text that needs shaping without it — a font file (Custom or System) shapes it{LegacyFontFacts(settingsFontName, text)}");
        }

        /// <summary>
        /// What a legacy game font does with a character it may not hold, for the log line above: a
        /// dynamic font asks the fonts named in its Font Names, then Unity's built-in list of system
        /// fonts (Unity manual, Font). Whether the letters on screen are the font's own or borrowed
        /// decides what may be said about it; these are the facts that tell.
        /// </summary>
        private static string LegacyFontFacts(string settingsFontName, string text)
        {
            if (!_gameUnityFonts.TryGetValue(settingsFontName, out var font) || font == null) return "";
            int sample = 0;
            foreach (char c in text) if (c > 0x7F && !TextShaping.UnicodeInfo.IsWhiteSpace(c)) { sample = c; break; }
            var probe = FontHasCharacterMethod;
            bool? has = sample == 0 || probe == null ? null : Invoke(probe, font, (char)sample);
            // By reflection: under IL2CPP fontNames is an Il2Cpp array, and naming the Mono property
            // here would make this whole method refuse to compile there (StringsOf).
            var names = StringsOf(typeof(Font).GetProperty("fontNames")?.GetValue(font, null)) ?? new string[0];
            return $" (dynamic {font.dynamic}, Font Names [{string.Join(", ", names)}]"
                 + (sample == 0 ? "" : $", HasCharacter(U+{sample:X4}) {(has.HasValue ? has.Value.ToString() : "unknown")}") + ")";
        }

        // By what the mod's window shows — its interface (null side), the game's source text, its
        // translation (ModWindowText) — the font ("" for none) it was seen unable to shape with.
        private static readonly Dictionary<string, string> _windowUnshapedWith = new Dictionary<string, string>(StringComparer.Ordinal);

        private static string WindowKey(GameTextSide? side) => side?.ToString() ?? "Interface";

        /// <summary>
        /// The mod's window had to show text needing shaping that the font of that part cannot shape
        /// (ShapingRoute "reorder only" for its own text): its translated labels in the interface font,
        /// or the game's text in the source or target text font. Said once per part and font.
        /// </summary>
        internal static void NoteWindowUnshaped(GameTextSide? side, string text)
        {
            // 🔴 The interface part speaks of the window's OWN words — its labels, translated into
            // the target language. A game text the window happens to show there (the corner's
            // "Translating: …" names the game's lines, a game's own list of languages among them)
            // is no label: counted as one, a French translation got "This window's labels draw
            // French incorrectly" (2026-10-02). The source/target/object-name parts are game text
            // by definition and keep reporting it.
            if (side == null && !TranslatorCore.IsAlreadyTargetText(text, ownUi: true)) return;
            string font = TranslatorCore.WindowFontFor(side) ?? "";
            string key = WindowKey(side);
            if (_windowUnshapedWith.TryGetValue(key, out var was) && was == font) return;
            _windowUnshapedWith[key] = font;
            TranslatorCore.LogInfo($"[FontManager] the mod's window shows its {ModWindowText.Describe(side)} needing shaping without it ({(font.Length == 0 ? "no font chosen" : font)}) — a {ModWindowText.Describe(side)} font with that script shapes it");
        }

        /// <summary>Whether this part of the mod's window, with the font it has now, was seen unable to shape what it shows.</summary>
        internal static bool WindowCannotShape(GameTextSide? side) =>
            _windowUnshapedWith.TryGetValue(WindowKey(side), out var font) && font == (TranslatorCore.WindowFontFor(side) ?? "");

        /// <summary>The parts of the mod's window that cannot shape what they show, in the order of ModWindowText.Parts.</summary>
        internal static List<GameTextSide?> WindowPartsUnshaped()
        {
            var parts = new List<GameTextSide?>();
            foreach (var side in ModWindowText.Parts)
                if (WindowCannotShape(side)) parts.Add(side);
            return parts;
        }

        private static void Refresh()
        {
            int signature = SettingsSignature();
            if (_missingAtVersion == Coverage.Version && _missingAtSettings == signature) return;
            if (_missingAtSettings != signature)
            {
                _installedPathByName.Clear();
                _cmapByPath.Clear();
                // Another replacement may shape what the last one could not: learned again from the
                // texts drawn next (a change of font setting re-sets every text).
                if (_missingAtVersion != -1) Coverage.ForgetUnshaped();
            }
            _missingAtVersion = Coverage.Version;
            _missingAtSettings = signature;
            var before = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var kv in _missingByFont) before[kv.Key] = kv.Value.Count;
            _missingByFont.Clear();
            foreach (var font in Coverage.Fonts)
            {
                var sources = CoverageSources(font);
                if (sources.Count == 0) continue;
                _missingByFont[font] = Coverage.Missing(font, cp =>
                {
                    bool known = false;
                    foreach (var source in sources)
                    {
                        bool? has = source(cp);
                        if (has == true) return true;
                        if (has == false) known = true;
                    }
                    return known ? false : (bool?)null;
                });
            }
            // Said in the log when it changes — the first thing to read when the corner speaks of it.
            foreach (var kv in _missingByFont)
            {
                before.TryGetValue(kv.Key, out int was);
                if (kv.Value.Count == was) continue;
                var sample = new System.Text.StringBuilder();
                for (int i = 0; i < kv.Value.Count && i < 12; i++) sample.Append($" U+{kv.Value[i]:X4}");
                TranslatorCore.LogInfo($"[FontManager] '{kv.Key}' cannot display {kv.Value.Count} character(s) of the translation:{sample}{(kv.Value.Count > 12 ? " …" : "")}");
            }
        }

        /// <summary>
        /// What draws this game font's text: its replacement when one is set and active, and the
        /// game's font (TextMesh Pro takes the replacement as a FALLBACK of it; legacy text keeps the
        /// game font for a text the replacement cannot cover). A character is displayed when ANY of
        /// them has it.
        /// </summary>
        private static List<Func<int, bool?>> CoverageSources(string settingsFontName)
        {
            var sources = new List<Func<int, bool?>>();
            TranslatorCore.FontSettingsMap.TryGetValue(settingsFontName, out var settings);
            string fallback = settings?.fallback;
            if (TranslatorCore.FontReplacementActive && !string.IsNullOrEmpty(fallback)
                && DrawingGameFont(settingsFontName) is Font drawing)
            {
                // 🔴 The object that DRAWS, not the font the reference names: legacy text got a GAME
                // font for its replacement (another game font of that name, an older build's
                // lookup order), and the file the reference resolves to was judged instead — an
                // installed Arial with Hebrew vouched for the game's "arial" without it, and the
                // translation showed Latin and digits only, without a word (2026-10-02).
                var own = ObjectCoverage(drawing);
                if (own != null) sources.Add(own);
            }
            else if (TranslatorCore.FontReplacementActive && !string.IsNullOrEmpty(fallback))
            {
                string path = FileOfReference(fallback, out string name, out var served);
                var cmap = CharacterMap(path);
                if (cmap != null) sources.Add(cp => cmap.Contains(cp));
                else if (served == UnityGameTranslator.Common.FontSource.Game)
                {
                    var own = ObjectCoverage(GetGameFont(name) ?? (object)FindLoadedGameUnityFont(name));
                    if (own != null) sources.Add(own);
                }
            }
            object gameFont = null;
            if (_detectedTMPFontObjects.TryGetValue(settingsFontName, out var tmp)) gameFont = tmp;
            else if (_gameUnityFonts.TryGetValue(settingsFontName, out var legacy)) gameFont = legacy;
            var game = ObjectCoverage(gameFont);
            if (game != null) sources.Add(game);
            return sources;
        }

        /// <summary>
        /// The game font legacy text of this game font is drawn with in its place, when the mod's
        /// replacement for it is one (GetUnityReplacementFont). Null when there is none yet, or when
        /// it is a font made from a file — then the file speaks for it — or the game font itself
        /// pointed at an installed one (fontNames): its own HasCharacter counts the letters it
        /// borrows, the file does not.
        /// </summary>
        private static Font DrawingGameFont(string settingsFontName)
        {
            if (!_unityFallbackFonts.TryGetValue(settingsFontName, out var drawing) || drawing == null) return null;
            if (_gameUnityFonts.TryGetValue(settingsFontName, out var own) && own == drawing) return null;
            return IsLoadedGameUnityFont(drawing) ? drawing : null;
        }

        private static string InstalledFontPath(string name)
        {
            if (_installedPathByName.TryGetValue(name, out var path)) return path;
            path = DerivedFonts.SourcePathOfInstalled(name) ?? CustomFontLoader.FindSystemTtfPath(name);
            _installedPathByName[name] = path;
            return path;
        }

        /// <summary>The font file a reference is served by (fonts/ or installed), with its name and origin; null for a game font or none found.</summary>
        private static string FileOfReference(string reference, out string name, out UnityGameTranslator.Common.FontSource? served)
        {
            name = UnityGameTranslator.Common.FontReferences.Name(reference);
            served = UnityGameTranslator.Common.FontReferences.Serving(reference,
                gameHas: IsGameFont(name), customHas: CustomFontLoader.CustomFonts.ContainsKey(name),
                systemHas: AssetAvailability.IsSystemFontAvailable(name));
            if (served == UnityGameTranslator.Common.FontSource.Custom
                && CustomFontLoader.CustomFonts.TryGetValue(name, out var info)) return info?.TtfPath;
            if (served == UnityGameTranslator.Common.FontSource.System) return InstalledFontPath(name);
            return null;
        }

        /// <summary>
        /// Whether a font draws the basic Latin letters and digits — what the mod's window's own labels
        /// are written in. Null when its file cannot be read: nothing to say either way.
        /// </summary>
        internal static bool? DrawsLatin(string reference)
        {
            if (string.IsNullOrEmpty(reference)) return null;
            var cmap = CharacterMap(FileOfReference(reference, out _, out _));
            if (cmap == null) return null;
            for (int c = 'A'; c <= 'Z'; c++) if (!cmap.Contains(c) || !cmap.Contains(c + 32)) return false;
            for (int c = '0'; c <= '9'; c++) if (!cmap.Contains(c)) return false;
            return true;
        }

        /// <summary>
        /// Whether a font (fonts/ or installed) holds every letter and mark of a text — what the mod's
        /// window needs before it calls a text shaped: a font with a derived copy shapes only the
        /// scripts it draws (the interface's Arial has one, and no Hindi: Unity then borrows the
        /// letters from the system one by one, unjoined — seen in a game, 2026-10-01). Null when its
        /// file cannot be read: nothing is claimed.
        /// </summary>
        internal static bool? Covers(string reference, string text)
        {
            if (string.IsNullOrEmpty(reference) || string.IsNullOrEmpty(text)) return null;
            var cmap = CharacterMap(FileOfReference(reference, out _, out _));
            if (cmap == null) return null;
            for (int i = 0; i < text.Length; i++)
            {
                int cp = char.IsHighSurrogate(text[i]) && i + 1 < text.Length ? char.ConvertToUtf32(text[i], text[++i]) : text[i];
                if (cp < 0x80) continue;
                var category = TextShaping.UnicodeInfo.CategoryOf(cp);
                bool letterOrMark = category <= System.Globalization.UnicodeCategory.OtherLetter
                    || category == System.Globalization.UnicodeCategory.NonSpacingMark
                    || category == System.Globalization.UnicodeCategory.SpacingCombiningMark;
                if (letterOrMark && !cmap.Contains(cp)) return false;
            }
            return true;
        }

        /// <summary>A font file's characters, read once per file. Null when there is no file or it cannot be read (then nothing is claimed).</summary>
        private static HashSet<int> CharacterMap(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_cmapByPath.TryGetValue(path, out var cmap)) return cmap;
            try
            {
                cmap = new HashSet<int>(new Rasterizer.TtfParser(System.IO.File.ReadAllBytes(path)).GetSupportedCodepoints());
            }
            catch (Exception ex) when (ex is System.IO.IOException || ex is UnauthorizedAccessException || ex is System.IO.InvalidDataException || ex is ArgumentException || ex is IndexOutOfRangeException)
            {
                Faults.Say("FontManager.CharacterMap", ex, Sanitize.Path(path));
                cmap = null;
            }
            _cmapByPath[path] = cmap;
            return cmap;
        }

        /// <summary>
        /// A loaded font object's own answer: TextMesh Pro's HasCharacter (fallbacks searched, the
        /// character added when the asset is dynamic — as drawing it would), or Font.HasCharacter.
        /// Null when the object or its probe is not there.
        /// </summary>
        private static Func<int, bool?> ObjectCoverage(object font)
        {
            if (font == null) return null;
            if (font is Font legacy)
            {
                var probe = FontHasCharacterMethod;
                if (probe == null) return null;
                return cp => cp > 0xFFFF ? (bool?)null : Invoke(probe, legacy, (char)cp);
            }
            var type = font.GetType();
            var full = type.GetMethod("HasCharacter", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(char), typeof(bool), typeof(bool) }, null);
            if (full != null) return cp => cp > 0xFFFF ? (bool?)null : Invoke(full, font, (char)cp, true, true);
            var plain = type.GetMethod("HasCharacter", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(char) }, null);
            if (plain != null) return cp => cp > 0xFFFF ? (bool?)null : Invoke(plain, font, (char)cp);
            var byInt = type.GetMethod("HasCharacter", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (byInt != null) return cp => Invoke(byInt, font, cp);
            return null;
        }

        private static bool? Invoke(MethodInfo method, object target, params object[] args)
        {
            try { return (bool)method.Invoke(target, args); }
            catch (Exception ex)
            {
                // A probe that fails says nothing — never "missing".
                TranslatorCore.LogDebug($"[FontManager] coverage probe {method.DeclaringType?.Name}.HasCharacter failed: {ex.GetBaseException().Message}");
                return null;
            }
        }
    }
}
