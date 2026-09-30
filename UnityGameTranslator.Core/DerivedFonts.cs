using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityGameTranslator.Core.Rasterizer;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The DERIVED copies of the fonts that carry a script needing shaping (Devanagari, Khmer, Thai…),
    /// for the engines that draw by codepoint — UI.Text, TextMesh, UI Toolkit: every glyph of
    /// a shaped run is named by a private codepoint (DerivedGlyphs) that the copy maps to a composite
    /// placed as the shaper decided (DerivedFontWriter). Proven on the probe bench, Unity 2018.4 →
    /// 6000.6, Mono and IL2CPP (analyse/ecritures-complexes-etat-reel.md, probes 1 and 2).
    ///
    /// How a copy reaches the engine: like any fonts/ file, by NAME — FontFolderRedirect shows it in
    /// the system's font folder, which the engine lists ONCE at start. So the copies exist before the
    /// redirect is installed (<see cref="Prepare"/>), under names chosen then:
    /// - Mono: one name. A name handed out since the file was written → the file is rewritten under
    ///   the same name and a new Font object is created (the engine reads the new content — probe 2 B);
    /// - IL2CPP: CreateDynamicFontFromOSFont is stripped, the mod rewrites the GAME font's fontNames,
    ///   and the engine keeps a family it has opened in memory (probe 2 N_B) → a RESERVE of names,
    ///   each rewrite takes one never opened (probe 2 N_C). Spent, the reserve is said, and the runs
    ///   needing a new name keep showing as they did until the next launch.
    /// The names handed out are kept next to the copies: the next launch writes them all at once.
    ///
    /// Two origins, the two a translation can name with a file behind it (FontReferences): every
    /// fonts/ font, and every INSTALLED font the translation replaces a game font with (a bare name
    /// in `_fonts`) — a copy kept in the game's folder, never shared: the translation names the font,
    /// each player's machine derives its own (user, 2026-09-30). One face of a collection (.ttc) is
    /// taken out first (FontCollection). An installed font chosen during the session has no copy
    /// until the next launch, when the translation names it.
    ///
    /// ⚠ Main thread only (the shapers' buffers). Engine side: never names the interface.
    /// </summary>
    internal static class DerivedFonts
    {
        internal const string Folder = ".ugt-derived";
        private const int Il2CppReserve = 8;

        internal sealed class Entry
        {
            internal string Name;              // the font's reference without its mark: fonts/ file name, or installed family
            internal UnityGameTranslator.Common.FontSource Origin;
            internal string Key;               // what its files are named after: unique across both origins
            internal string SourcePath;        // a single font: the file itself, or the face taken out of a collection
            internal byte[] Source;
            internal string BaseFamily;        // "UGT <source family>"
            internal TtfShapingFont Font;
            internal DerivedGlyphs Namer;
            internal List<string> Files = new List<string>();   // the names shown to the engine, in reserve order
            internal int Current;              // the file the engine draws from now
            internal int Version;              // bumped at each rewrite: replacement fonts made before are stale
            internal bool Spent;               // IL2CPP reserve used up (said once)
            internal string NamesPath => Path.Combine(Path.GetDirectoryName(Files[0]), Key + ".names");
            internal string Family(int k) => Files.Count == 1 ? BaseFamily : BaseFamily + " " + k;
            internal string CurrentFamily => Family(Current);
            internal string CurrentFile => Files[Current];
        }

        private static readonly Dictionary<string, Entry> _byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Entry> _installedByName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static string _translationsPath;
        private static readonly List<Entry> _pending = new List<Entry>();

        /// <summary>Whether a fonts/ font has a derived copy the engine was shown at start.</summary>
        internal static bool Has(string fontName) => !string.IsNullOrEmpty(fontName) && _byName.ContainsKey(fontName);

        internal static Entry Get(string fontName) => fontName != null && _byName.TryGetValue(fontName, out var e) ? e : null;

        /// <summary>The derived copy of an INSTALLED font, when the translation named it at start.</summary>
        internal static Entry GetInstalled(string fontName) => fontName != null && _installedByName.TryGetValue(fontName, out var e) ? e : null;

        /// <summary>The copy of a font as the origin serving it: fonts/X, or the installed X.</summary>
        internal static Entry Get(string fontName, UnityGameTranslator.Common.FontSource origin)
            => origin == UnityGameTranslator.Common.FontSource.Custom ? Get(fontName)
             : origin == UnityGameTranslator.Common.FontSource.System ? GetInstalled(fontName)
             : null;

        private static IEnumerable<Entry> All() => _byName.Values.Concat(_installedByName.Values);

        /// <summary>
        /// Writes the copies of the fonts that need them and returns the files to show the engine
        /// (FontFolderRedirect.Install). Called BEFORE the redirect is installed — so nothing here
        /// asks the ENGINE for its font list, which it builds once: installed fonts are found on disk.
        /// A font that cannot be derived is said and left out: its text keeps the codepoint path.
        /// </summary>
        internal static List<string> Prepare(string fontsFolder, bool il2cpp, string translationsPath = null,
                                             string configPath = null, string interfacePath = null)
        {
            _translationsPath = translationsPath;
            var shown = new List<string>();
            if (string.IsNullOrEmpty(fontsFolder) || !Directory.Exists(fontsFolder)) return shown;
            string folder = Path.Combine(fontsFolder, Folder);
            int reserve = il2cpp ? Il2CppReserve : 1;
            var translation = ReadTranslation();

            foreach (var extension in UnityGameTranslator.Common.AssetPacks.FontExtensions)
            {
                string[] files;
                try { files = Directory.GetFiles(fontsFolder, "*" + extension); }
                catch (Exception ex) { Faults.Say("DerivedFonts.Prepare", ex, Sanitize.Path(fontsFolder)); continue; }
                foreach (var path in files)
                {
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(path); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Faults.Say("DerivedFonts.Prepare", ex, Sanitize.Path(path)); continue; }
                    string name = Path.GetFileNameWithoutExtension(path);
                    var entry = Build(name, UnityGameTranslator.Common.FontSource.Custom, Sanitized(name), path, bytes, folder, reserve, translation);
                    if (entry == null) continue;
                    _byName[entry.Name] = entry;
                    shown.AddRange(entry.Files);
                }
            }

            var installed = InstalledFontsNamed(translation);
            // The mod's own interface font too: its window shows translation values and takes typed
            // text, in any script (config.json's interface_font wins over the interface file's).
            foreach (var reference in new[] { ReadString(configPath, "interface_font"), ReadString(interfacePath, "_settings", "ui_font") })
                if (!string.IsNullOrEmpty(reference)
                    && UnityGameTranslator.Common.FontReferences.Order(reference)[0] == UnityGameTranslator.Common.FontSource.System
                    && !installed.Contains(UnityGameTranslator.Common.FontReferences.Name(reference), StringComparer.OrdinalIgnoreCase))
                    installed.Add(UnityGameTranslator.Common.FontReferences.Name(reference));

            foreach (var name in installed)
            {
                byte[] bytes = InstalledFont(name, folder, out string path);
                if (bytes == null) continue;
                var entry = Build(name, UnityGameTranslator.Common.FontSource.System, "sys-" + Sanitized(name), path, bytes, folder, reserve, translation);
                if (entry == null) continue;
                _installedByName[entry.Name] = entry;
                shown.AddRange(entry.Files);
            }
            return shown;
        }

        /// <summary>One string of a JSON file read before the mod loads it; null when absent or unreadable (the loader says why).</summary>
        private static string ReadString(string path, params string[] keys)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                Newtonsoft.Json.Linq.JToken token = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(path));
                foreach (var key in keys) token = (token as Newtonsoft.Json.Linq.JObject)?[key];
                return token != null && token.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)token : null;
            }
            catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException)
            {
                Faults.Say("DerivedFonts.ReadString", ex, Sanitize.Path(path));
                return null;
            }
        }

        private static Newtonsoft.Json.Linq.JObject ReadTranslation()
        {
            if (string.IsNullOrEmpty(_translationsPath) || !File.Exists(_translationsPath)) return null;
            try { return Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(_translationsPath)); }
            catch (Exception ex) when (ex is IOException || ex is Newtonsoft.Json.JsonException)
            {
                // The translation is read again, properly, by the loader — which says what is wrong with it.
                Faults.Say("DerivedFonts.ReadTranslation", ex, Sanitize.Path(_translationsPath));
                return null;
            }
        }

        /// <summary>
        /// The installed fonts the translation replaces a game font with: a bare name in `_fonts`
        /// (FontReferences — "[Custom] X" is fonts/, "[Game] X" has no file). Read from the file:
        /// this runs before the translation is loaded.
        /// </summary>
        private static List<string> InstalledFontsNamed(Newtonsoft.Json.Linq.JObject translation)
        {
            var names = new List<string>();
            if (!(translation?[UnityGameTranslator.Common.SettingsSections.FontsKey] is Newtonsoft.Json.Linq.JObject fonts)) return names;
            foreach (var property in fonts.Properties())
            {
                var fallbackToken = (property.Value as Newtonsoft.Json.Linq.JObject)?["fallback"];
                if (fallbackToken == null || fallbackToken.Type != Newtonsoft.Json.Linq.JTokenType.String) continue;
                string fallback = (string)fallbackToken;
                if (string.IsNullOrEmpty(fallback)) continue;
                if (UnityGameTranslator.Common.FontReferences.Order(fallback)[0] != UnityGameTranslator.Common.FontSource.System) continue;
                string name = UnityGameTranslator.Common.FontReferences.Name(fallback);
                if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// An installed font's bytes, as a single font: its .ttf/.otf (CustomFontLoader's search), or
        /// its face in a collection of the system's font folders — taken out and kept in the copies'
        /// folder, since the rasterizer of TMP text reads a single font from a path
        /// (<see cref="SourcePathOfInstalled"/>). Null, and said, when not found.
        /// </summary>
        private static byte[] InstalledFont(string name, string folder, out string path)
        {
            path = CustomFontLoader.FindSystemTtfPath(name);
            try
            {
                if (path != null) return File.ReadAllBytes(path);
                foreach (var dir in CustomFontLoader.SystemFontDirectories())
                    foreach (var file in Directory.GetFiles(dir, "*.ttc", SearchOption.AllDirectories))
                    {
                        int face;
                        using (var stream = File.OpenRead(file)) face = FontCollection.FindFace(stream, name);
                        if (face < 0) continue;
                        byte[] bytes = FontCollection.Face(File.ReadAllBytes(file), face);
                        Directory.CreateDirectory(folder);
                        path = Path.Combine(folder, "sys-" + Sanitized(name) + ".ttf");
                        File.WriteAllBytes(path, bytes);
                        return bytes;
                    }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException)
            {
                Faults.Say("DerivedFonts.InstalledFont", ex, Sanitize.Path(path ?? name));
                path = null;
                return null;
            }
            TranslatorCore.LogInfo($"[DerivedFonts] installed font '{name}' named by the translation: no file found — no derived copy");
            return null;
        }

        /// <summary>
        /// The single-font file an installed font was derived from — the one the rasterizer builds a
        /// TMP asset from, so TMP text in that font is shaped by its tables as a fonts/ font's is.
        /// Null when the translation did not name it at start.
        /// </summary>
        internal static string SourcePathOfInstalled(string fontName) => GetInstalled(fontName)?.SourcePath;

        private static Entry Build(string name, UnityGameTranslator.Common.FontSource origin, string key, string path, byte[] bytes,
                                   string folder, int reserve, Newtonsoft.Json.Linq.JObject translation)
        {
            TtfParser parser;
            try { parser = new TtfParser(bytes); }
            catch (Exception ex) { Faults.Say("DerivedFonts.Build", ex, Sanitize.Path(path)); return null; }

            // Only a font carrying a script the shapers act on: a Latin font needs no copy.
            if (!CoversShapedScript(parser)) return null;
            if (!parser.TryGetTable("glyf", out _, out _))
            {
                // CFF outlines (a PostScript .otf): merged into TrueType ones once, here — every copy
                // is written from the merged font (the merge reads every glyph).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                byte[] merged = DerivedFontWriter.WithTrueTypeOutlines(bytes, out string why);
                if (merged == null)
                {
                    TranslatorCore.LogWarning($"[DerivedFonts] {name}: no derived copy — {why}");
                    return null;
                }
                bytes = merged;
                try { parser = new TtfParser(bytes); }
                catch (Exception ex) { Faults.Say("DerivedFonts.Build merged", ex, Sanitize.Path(path)); return null; }
                TranslatorCore.LogInfo($"[DerivedFonts] {name}: CFF outlines merged into TrueType ones ({parser.GlyphCount} glyphs, {sw.ElapsedMilliseconds} ms)");
            }

            var font = new TtfShapingFont(parser);
            var entry = new Entry
            {
                Name = name,
                Origin = origin,
                Key = key,
                SourcePath = path,
                Source = bytes,
                // Apart per origin: fonts/ may hold a copy of the very font installed on the machine,
                // and two copies under one family would let the engine open either.
                BaseFamily = (origin == UnityGameTranslator.Common.FontSource.System ? "UGT Sys " : "UGT ") + (parser.Metrics?.FontName ?? name),
                Font = font,
                Namer = new DerivedGlyphs(font, parser.GlyphCount),
            };
            entry.Namer.OnExhausted = why => TranslatorCore.LogWarning($"[DerivedFonts] {name}: {why}");
            for (int k = 0; k < reserve; k++)
                entry.Files.Add(Path.Combine(folder, $"ugt-{key}-{k}.ttf"));

            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(entry.NamesPath))
                {
                    int refused = entry.Namer.Load(File.ReadAllText(entry.NamesPath));
                    if (refused > 0) TranslatorCore.LogWarning($"[DerivedFonts] {name}: {refused} saved name(s) did not fit this font and were left out");
                }
                PreName(entry, translation);
                // Every file of the reserve is a valid font of its own family from the start: the
                // engine reads the names when it lists the folder. The first holds everything known.
                for (int k = 0; k < entry.Files.Count; k++)
                {
                    byte[] derived = DerivedFontWriter.Write(bytes, entry.Family(k), entry.Namer.Added, out string refusal);
                    if (derived == null)
                    {
                        TranslatorCore.LogWarning($"[DerivedFonts] {name}: no derived copy — {refusal}");
                        return null;
                    }
                    File.WriteAllBytes(entry.Files[k], derived);
                }
                entry.Namer.MarkWritten();
                TranslatorCore.LogInfo($"[DerivedFonts] {name}: derived copy ready ({entry.Namer.Added.Count} shaped glyph(s) known, {entry.Files.Count} name(s) shown)");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Faults.Say("DerivedFonts.Build", ex, Sanitize.Path(folder));
                return null;
            }
            return entry;
        }

        /// <summary>
        /// Names, before the copy is written, every glyph the KNOWN translation will show — so the
        /// first scene does not spend the IL2CPP reserve one line at a time. Only when the translation
        /// changed since the names were last saved (a relaunch with the same file does nothing), and
        /// timed in the log: this is main-thread work while the game starts.
        /// </summary>
        private static void PreName(Entry entry, Newtonsoft.Json.Linq.JObject file)
        {
            if (file == null) return;
            if (File.Exists(entry.NamesPath) && File.GetLastWriteTimeUtc(entry.NamesPath) >= File.GetLastWriteTimeUtc(_translationsPath)) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            int before = entry.Namer.Added.Count, lines = 0;
            foreach (var property in file.Properties())
            {
                if (property.Name.StartsWith("_")) continue;
                string value = property.Value.Type == Newtonsoft.Json.Linq.JTokenType.Object
                    ? (string)property.Value["v"]
                    : property.Value.Type == Newtonsoft.Json.Linq.JTokenType.String ? (string)property.Value : null;
                if (string.IsNullOrEmpty(value) || !OpenTypeText.NeedsShaping(value)) continue;
                OpenTypeText.Shape(value, entry.Font, entry.Namer);
                lines++;
            }
            File.WriteAllText(entry.NamesPath, entry.Namer.Save());
            TranslatorCore.LogInfo($"[DerivedFonts] {entry.Name}: {lines} shaped line(s) of the translation named {entry.Namer.Added.Count - before} new glyph(s) in {sw.ElapsedMilliseconds} ms");
        }

        private static bool CoversShapedScript(TtfParser parser)
        {
            foreach (int cp in parser.GetSupportedCodepoints())
                if (cp > 0x7F && OpenTypeText.NeedsShaping(char.ConvertFromUtf32(cp))) return true;
            return false;
        }

        private static string Sanitized(string name)
        {
            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_') chars[i] = '_';
            return new string(chars);
        }

        /// <summary>
        /// A name handed out since the copy was written: queue the copy for rewriting. Done by the
        /// scanner's pass (<see cref="ProcessPending"/>) — in the same tick, never on a timer.
        /// </summary>
        internal static void NoteNamed(Entry entry)
        {
            if (entry == null || !entry.Namer.Dirty || _pending.Contains(entry)) return;
            _pending.Add(entry);
        }

        /// <summary>
        /// Rewrites every queued copy and returns the fonts/ names whose copy changed — the caller
        /// hands their components the new copy. Mono rewrites the same file; IL2CPP takes the next
        /// name of the reserve (a family the engine has not opened).
        /// </summary>
        internal static List<string> ProcessPending(bool il2cpp)
        {
            var changed = new List<string>();
            if (_pending.Count == 0) return changed;
            foreach (var entry in _pending.ToArray())
            {
                _pending.Remove(entry);
                if (!entry.Namer.Dirty) continue;
                int target = entry.Current;
                if (il2cpp)
                {
                    if (entry.Current + 1 >= entry.Files.Count)
                    {
                        if (!entry.Spent)
                        {
                            entry.Spent = true;
                            TranslatorCore.LogWarning($"[DerivedFonts] {entry.Name}: all {entry.Files.Count} font names of this session are used — shaped text written from now on shows its new glyphs after a relaunch");
                        }
                        continue;
                    }
                    target = entry.Current + 1;
                }
                byte[] derived = DerivedFontWriter.Write(entry.Source, entry.Family(target), entry.Namer.Added, out string refusal);
                if (derived == null)
                {
                    TranslatorCore.LogWarning($"[DerivedFonts] {entry.Name}: the derived copy could not be rewritten — {refusal}");
                    continue;
                }
                try
                {
                    File.WriteAllBytes(entry.Files[target], derived);
                    File.WriteAllText(entry.NamesPath, entry.Namer.Save());
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Faults.Say("DerivedFonts.ProcessPending", ex, Sanitize.Path(entry.Files[target]));
                    continue;
                }
                entry.Namer.MarkWritten();
                entry.Current = target;
                entry.Version++;
                changed.Add(entry.Name);
                TranslatorCore.LogDebug($"[DerivedFonts] {entry.Name}: copy rewritten ({entry.Namer.Added.Count} shaped glyph(s)) as '{entry.CurrentFamily}'");
            }
            return changed;
        }

        /// <summary>The names handed out, kept for the next launch — when the game closes.</summary>
        internal static void SaveNames()
        {
            foreach (var entry in All())
            {
                try { File.WriteAllText(entry.NamesPath, entry.Namer.Save()); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                { Faults.Say("DerivedFonts.SaveNames", ex, Sanitize.Path(entry.NamesPath)); }
            }
        }
    }
}
