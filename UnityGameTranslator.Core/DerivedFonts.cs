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
    /// the system's font folder, which the engine lists ONCE at start. So the copies known then exist
    /// before the redirect is installed (<see cref="Prepare"/>), each under a name of its own; what
    /// comes later takes a name of the POOL (FontPool: listed at start, empty, filled when needed):
    /// - Mono: a name handed out since the file was written → the file is rewritten under the same
    ///   name and a new Font object is created (the engine reads the new content — probe 2 B);
    /// - IL2CPP: CreateDynamicFontFromOSFont is stripped, the mod rewrites the GAME font's fontNames,
    ///   and the engine keeps a family it has opened in memory (probe 2 N_B) → each rewrite takes a
    ///   pool name never opened (probe 2 N_C);
    /// - a font that needs a copy only during the session (a fonts/ file added, an installed font or
    ///   an interface font chosen) is derived then, into a pool name (<see cref="Ensure"/>); a fonts/
    ///   file added during the session that needs no copy is shown the same way (<see cref="LateCopy"/>).
    /// The pool spent, it is said, and what needs a new name shows at the next launch — whose pool is
    /// larger. The names handed out are kept next to the copies: the next launch writes them at once.
    ///
    /// Two origins, the two a translation can name with a file behind it (FontReferences): every
    /// fonts/ font, and every INSTALLED font the translation replaces a game font with (a bare name
    /// in `_fonts`) or the interface font names — a copy kept in the game's folder, never shared: the
    /// translation names the font, each player's machine derives its own (user, 2026-09-30). One face
    /// of a collection (.ttc) is taken out first (FontCollection).
    ///
    /// ⚠ Main thread only (the shapers' buffers). Engine side: never names the interface.
    /// </summary>
    internal static class DerivedFonts
    {
        internal const string Folder = ".ugt-derived";
        private const string PoolFolder = "pool";
        private const string LatePrefix = "late-";

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
            // [0]: the name the copy was first shown under — its own file at start, or a pool name for
            // a copy made during the session; then the pool names IL2CPP rewrites took.
            internal List<FontPool.Slot> Slots = new List<FontPool.Slot>();
            internal int Current;              // the slot the engine draws from now
            internal int Version;              // bumped at each rewrite: replacement fonts made before are stale
            // The copy's real file. ⚠ The engine keeps a font's file OPEN while the font lives and reads
            // it as it needs (measured 2026-10-01: Windows refuses to delete it, allows a rewrite): a
            // version still loaded must never see its file rewritten. Mono rewrites in place — the name
            // the engine listed maps to this path, and a new Font object is made at each version; the
            // old one is no longer drawn. IL2CPP writes each version to a file of its own, under a pool
            // name, and removes the older ones once the engine lets them go.
            internal string OwnFile;
            internal readonly List<string> OlderFiles = new List<string>();
            internal string NamesPath => Path.Combine(_folder, Key + ".names");
            internal string CurrentFamily => Slots[Current].Family;
            /// <summary>The real file of the current version (read by the TMP rasterizer, the coverage probe).</summary>
            internal string CurrentFile => OwnFile;
            /// <summary>The file name the engine knows the current version by.</summary>
            internal string CurrentShownName => Slots[Current].Name;
        }

        private static readonly Dictionary<string, Entry> _byName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Entry> _installedByName = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static string _translationsPath, _folder;
        private static bool _il2cpp;
        private static FontPool _pool;
        private static bool _poolSpentSaid;
        private static readonly List<Entry> _pending = new List<Entry>();
        // Fonts tried during the session that got no copy (no shaped script, unreadable): tried once.
        private static readonly HashSet<string> _refused = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Files of copies no longer needed that the engine still held open: removed when it lets them
        // go — tried again when the fonts in use change, and at the next launch at the latest.
        private static readonly List<string> _toRemove = new List<string>();
        // fonts/ files added during the session, shown under a pool name: font name → its slot.
        private static readonly Dictionary<string, FontPool.Slot> _lent = new Dictionary<string, FontPool.Slot>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether a family is one of the mod's own — a derived copy ("UGT X", "UGT Sys X") or a pool
        /// name ("UGT Pool 0001"): never a font of the system, never offered as one.
        /// </summary>
        internal static bool IsOurFamily(string family) =>
            family != null && family.StartsWith(OurFamilyMark, StringComparison.Ordinal);

        private const string OurFamilyMark = "UGT ";

        /// <summary>The pool of this launch, for FontFolderRedirect.Install; null when it could not be made.</summary>
        internal static FontPool Pool => _pool;

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
            _il2cpp = il2cpp;
            var shown = new List<string>();
            if (string.IsNullOrEmpty(fontsFolder) || !Directory.Exists(fontsFolder)) return shown;
            string folder = Path.Combine(fontsFolder, Folder);
            _folder = folder;
            var translation = ReadTranslation();

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _pool = new FontPool(Path.Combine(folder, PoolFolder));
                TranslatorCore.LogInfo($"[DerivedFonts] pool: {_pool.Size} font name(s) for this session ({_pool.UsedLastSession} used in the previous one), ready in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                // No pool: what comes during the session waits for the next launch, and is said so.
                Faults.Say("DerivedFonts.Prepare pool", ex, Sanitize.Path(folder));
                _pool = null;
            }

            // Only the fonts IN USE get a copy (user, 2026-10-01): a replacement the translation names,
            // the interface font. A font chosen during the session is derived then (Ensure), one left
            // is removed (KeepOnly) — a font tried and abandoned takes no room.
            var references = FallbackReferences(translation);
            // The mod's own interface font too: its window shows translation values and takes typed
            // text, in any script (config.json's interface_font wins over the interface file's).
            foreach (var reference in new[] { ReadString(configPath, "interface_font"), ReadString(interfacePath, "_settings", "ui_font") })
                if (!string.IsNullOrEmpty(reference)) references.Add(reference);

            var customFiles = FontFilesByName(fontsFolder);
            var wantCustom = new List<string>();
            var wantSystem = new List<string>();
            foreach (var reference in references)
            {
                var order = UnityGameTranslator.Common.FontReferences.Order(reference);
                string name = UnityGameTranslator.Common.FontReferences.Name(reference);
                if (order[0] == UnityGameTranslator.Common.FontSource.Custom) { if (customFiles.ContainsKey(name)) AddOnce(wantCustom, name); }
                else if (order[0] == UnityGameTranslator.Common.FontSource.System) AddOnce(wantSystem, name);
            }

            foreach (var name in wantSystem)
            {
                byte[] bytes = InstalledFont(name, folder, out string path);
                if (bytes == null)
                {
                    // A bare name the system lacks is served by its copy in fonts/, when there is one.
                    if (customFiles.ContainsKey(name)) AddOnce(wantCustom, name);
                    continue;
                }
                var entry = Build(name, UnityGameTranslator.Common.FontSource.System, "sys-" + Sanitized(name), path, bytes, OwnSlot("sys-" + Sanitized(name)), translation);
                if (entry == null) { _refused.Add("sys-" + Sanitized(name)); continue; }
                _installedByName[entry.Name] = entry;
                shown.Add(entry.OwnFile);
            }

            foreach (var name in wantCustom)
            {
                string path = customFiles[name];
                byte[] bytes;
                try { bytes = File.ReadAllBytes(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Faults.Say("DerivedFonts.Prepare", ex, Sanitize.Path(path)); continue; }
                var entry = Build(name, UnityGameTranslator.Common.FontSource.Custom, Sanitized(name), path, bytes, OwnSlot(Sanitized(name)), translation);
                if (entry == null) { _refused.Add(Sanitized(name)); continue; }   // not tried again (Ensure)
                _byName[entry.Name] = entry;
                shown.Add(entry.OwnFile);
            }

            RemoveWhatNoCopyOwns();
            return shown;
        }

        /// <summary>The name a copy known at start is shown under: its own file, of its own family.</summary>
        private static Func<string, FontPool.Slot?> OwnSlot(string key) =>
            family => new FontPool.Slot { Name = FileNameOf(key), Family = family };

        private static string FileNameOf(string key) => $"ugt-{key}.ttf";

        /// <summary>Whether the engine was shown the pool at start (FontFolderRedirect).</summary>
        private static bool PoolShown => _pool != null && _pool.Size > 0 && FontFolderRedirect.ShowsFile(_pool.NameOf(1));

        private static bool _servedSaid;

        /// <summary>A pool name for <paramref name="forWhat"/>, or null: the pool not shown, or spent (said once).</summary>
        private static FontPool.Slot? TakeFromPool(string forWhat)
        {
            if (!PoolShown) return null;
            if (!_servedSaid)
            {
                // The proof the engine read the pool through the mod: its reads of empty slots.
                _servedSaid = true;
                TranslatorCore.LogInfo($"[DerivedFonts] pool: {FontFolderRedirect.ServedReads} read(s) of empty names served since start");
            }
            var slot = _pool.Take();
            if (slot == null && !_poolSpentSaid)
            {
                _poolSpentSaid = true;
                TranslatorCore.LogWarning($"[DerivedFonts] {forWhat}: all {_pool.Size} font names of this session are used — what needs a new one shows at the next launch, whose pool is larger");
            }
            return slot;
        }

        /// <summary>Whether a fonts/ file added during the session can reach legacy text now (<see cref="LateCopy"/>).</summary>
        internal static bool CanLend(string fontName) =>
            !string.IsNullOrEmpty(fontName)
            && (_lent.ContainsKey(fontName) || (!_refused.Contains(LateKey(fontName)) && PoolShown && !_pool.Spent));

        private static string LateKey(string fontName) => "late|" + fontName;

        /// <summary>
        /// A fonts/ file added while the game runs, which needs no derived copy: the engine did not
        /// list it, so it is shown under a pool name — the file with the slot's family
        /// (DerivedFontWriter.Write, nothing added). Once per font; null when it cannot be (said).
        /// </summary>
        internal static FontPool.Slot? LateCopy(string fontName, string fontFile, out string realFile)
        {
            realFile = null;
            if (string.IsNullOrEmpty(fontName)) return null;
            string copyPath = Path.Combine(_folder ?? "", LatePrefix + Sanitized(fontName) + ".ttf");
            if (_lent.TryGetValue(fontName, out var lent)) { realFile = copyPath; return lent; }
            if (string.IsNullOrEmpty(fontFile) || _refused.Contains(LateKey(fontName))) return null;
            var slot = TakeFromPool(fontName);
            if (slot == null) return null;
            _refused.Add(LateKey(fontName));   // one try; taken back below when it worked
            try
            {
                byte[] copy = DerivedFontWriter.Write(File.ReadAllBytes(fontFile), slot.Value.Family, new DerivedFontWriter.Added[0], out string refusal);
                if (copy == null)
                {
                    TranslatorCore.LogWarning($"[DerivedFonts] {fontName}: added during the session, shown to legacy text from the next launch — {refusal}");
                    return null;
                }
                File.WriteAllBytes(copyPath, copy);
                _pool.Fill(slot.Value, copyPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                Faults.Say("DerivedFonts.LateCopy", ex, Sanitize.Path(fontFile));
                return null;
            }
            _refused.Remove(LateKey(fontName));
            _lent[fontName] = slot.Value;
            realFile = copyPath;
            TranslatorCore.LogInfo($"[DerivedFonts] {fontName}: added during the session — shown as '{slot.Value.Family}'");
            return slot;
        }

        /// <summary>
        /// The copy of a font from this origin — made now, into a pool name, when the font needs one
        /// and had none at start (a fonts/ file added, an installed or interface font chosen during the
        /// session). Main thread: derives the font (a CFF merge reads every glyph), once per font.
        /// </summary>
        internal static Entry Ensure(string fontName, UnityGameTranslator.Common.FontSource origin)
        {
            var known = Get(fontName, origin);
            if (known != null || string.IsNullOrEmpty(fontName) || _folder == null) return known;
            bool custom = origin == UnityGameTranslator.Common.FontSource.Custom;
            if (!custom && origin != UnityGameTranslator.Common.FontSource.System) return null;
            string key = (custom ? "" : "sys-") + Sanitized(fontName);
            if (_refused.Contains(key) || !PoolShown || _pool.Spent) return null;

            byte[] bytes;
            string path;
            if (custom)
            {
                if (!CustomFontLoader.CustomFonts.TryGetValue(fontName, out var info) || string.IsNullOrEmpty(info.TtfPath)) return null;
                path = info.TtfPath;
                try { bytes = File.ReadAllBytes(path); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _refused.Add(key);
                    Faults.Say("DerivedFonts.Ensure", ex, Sanitize.Path(path));
                    return null;
                }
            }
            else
            {
                bytes = InstalledFont(fontName, _folder, out path);
                if (bytes == null) { _refused.Add(key); return null; }
            }

            _refused.Add(key);   // one try: a font with no shaped script stays without a copy
            var entry = Build(fontName, origin, key, path, bytes, _ => TakeFromPool(fontName), ReadTranslation());
            if (entry == null) return null;
            _refused.Remove(key);
            (custom ? _byName : _installedByName)[entry.Name] = entry;
            TranslatorCore.LogInfo($"[DerivedFonts] {fontName}: derived during the session");
            return entry;
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
        /// Every replacement the translation names in `_fonts` (FontReferences: "[Custom] X" is fonts/,
        /// a bare name the installed font, "[Game] X" has no file). Read from the file: this runs
        /// before the translation is loaded.
        /// </summary>
        private static List<string> FallbackReferences(Newtonsoft.Json.Linq.JObject translation)
        {
            var references = new List<string>();
            if (!(translation?[UnityGameTranslator.Common.SettingsSections.FontsKey] is Newtonsoft.Json.Linq.JObject fonts)) return references;
            foreach (var property in fonts.Properties())
            {
                var fallbackToken = (property.Value as Newtonsoft.Json.Linq.JObject)?["fallback"];
                if (fallbackToken == null || fallbackToken.Type != Newtonsoft.Json.Linq.JTokenType.String) continue;
                string fallback = (string)fallbackToken;
                if (!string.IsNullOrEmpty(fallback)) AddOnce(references, fallback);
            }
            return references;
        }

        private static void AddOnce(List<string> list, string value)
        {
            if (!list.Contains(value, StringComparer.OrdinalIgnoreCase)) list.Add(value);
        }

        /// <summary>The font files of fonts/, by name (the file name without its extension).</summary>
        private static Dictionary<string, string> FontFilesByName(string fontsFolder)
        {
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var extension in UnityGameTranslator.Common.AssetPacks.FontExtensions)
            {
                try
                {
                    foreach (var path in Directory.GetFiles(fontsFolder, "*" + extension))
                    {
                        string name = Path.GetFileNameWithoutExtension(path);
                        if (!files.ContainsKey(name)) files[name] = path;
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Faults.Say("DerivedFonts.FontFilesByName", ex, Sanitize.Path(fontsFolder)); }
            }
            return files;
        }

        /// <summary>
        /// Removes from the copies' folder every file no copy of this launch owns: copies of fonts no
        /// longer in use, their names, faces taken out of a collection, last session's copies of fonts
        /// added while it ran. The pool's own folder is left to the pool.
        /// </summary>
        private static void RemoveWhatNoCopyOwns()
        {
            if (_folder == null || !Directory.Exists(_folder)) return;
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in All())
            {
                owned.Add(entry.OwnFile);
                owned.Add(entry.NamesPath);
                if (entry.SourcePath != null) owned.Add(entry.SourcePath);
            }
            string[] files;
            try { files = Directory.GetFiles(_folder); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Faults.Say("DerivedFonts.RemoveWhatNoCopyOwns", ex, Sanitize.Path(_folder)); return; }
            int removed = 0;
            foreach (var file in files)
            {
                if (owned.Contains(file)) continue;
                if (Delete(file)) removed++;
            }
            if (removed > 0) TranslatorCore.LogInfo($"[DerivedFonts] {removed} file(s) of fonts no longer in use removed");
        }

        /// <summary>
        /// Removes a file the engine may still hold open (a font it loaded reads its file as it needs):
        /// false when it does — that is the engine's state, not a fault, and the file is removed later.
        /// A missing file counts as removed.
        /// </summary>
        private static bool TryRemove(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return true;
            try { File.Delete(file); return true; }
            catch (IOException) { return false; }   // held open by the engine
            catch (UnauthorizedAccessException ex)
            {
                Faults.Say("DerivedFonts.TryRemove", ex, Sanitize.Path(file));
                return false;
            }
        }

        private static bool Delete(string file)
        {
            try { File.Delete(file); return true; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Faults.Say("DerivedFonts.Delete", ex, Sanitize.Path(file));
                return false;
            }
        }

        /// <summary>
        /// Keeps the copies of the fonts in use and removes the others, files included — called when
        /// the fonts in use change (FontManager: a replacement chosen or left, the interface font). A
        /// font chosen again later is derived again, under a pool name (Ensure).
        /// </summary>
        internal static void KeepOnly(IList<KeyValuePair<string, UnityGameTranslator.Common.FontSource>> inUse)
        {
            _toRemove.RemoveAll(TryRemove);
            bool Used(string name, UnityGameTranslator.Common.FontSource origin)
            {
                foreach (var u in inUse)
                    if (u.Value == origin && string.Equals(u.Key, name, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            foreach (var entry in _byName.Values.ToList())
                if (!Used(entry.Name, UnityGameTranslator.Common.FontSource.Custom)) Discard(_byName, entry);
            foreach (var entry in _installedByName.Values.ToList())
                if (!Used(entry.Name, UnityGameTranslator.Common.FontSource.System)) Discard(_installedByName, entry);
            foreach (var name in _lent.Keys.ToList())
            {
                if (Used(name, UnityGameTranslator.Common.FontSource.Custom)) continue;
                _lent.Remove(name);
                _refused.Remove(LateKey(name));
                string late = _folder == null ? null : Path.Combine(_folder, LatePrefix + Sanitized(name) + ".ttf");
                if (late != null && !TryRemove(late)) _toRemove.Add(late);
                TranslatorCore.LogInfo($"[DerivedFonts] {name}: no longer in use — its copy removed");
            }
        }

        /// <summary>
        /// A copy no font in use needs: forgotten, its files removed. What the engine already loaded
        /// stays in memory (it reads a font whole when it opens it), so nothing on screen changes.
        /// </summary>
        private static void Discard(Dictionary<string, Entry> from, Entry entry)
        {
            from.Remove(entry.Name);
            _pending.Remove(entry);
            _refused.Remove(entry.Key);   // chosen again: derived again
            var files = new List<string>(entry.OlderFiles) { entry.OwnFile, entry.NamesPath };
            if (entry.SourcePath != null && _folder != null
                && string.Equals(Path.GetDirectoryName(entry.SourcePath), _folder, StringComparison.OrdinalIgnoreCase))
                files.Add(entry.SourcePath);   // a face taken out of a collection for it
            int held = 0;
            foreach (var file in files)
                if (!TryRemove(file)) { _toRemove.Add(file); held++; }
            TranslatorCore.LogInfo($"[DerivedFonts] {entry.Name}: no longer in use — its derived copy removed"
                + (held > 0 ? $" ({held} file(s) still held by the game: removed when it lets them go, at the next launch at the latest)" : ""));
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

        /// <summary>
        /// The derived copy of a font, written under the name <paramref name="slotFor"/> gives for its
        /// family ("UGT X") — asked only once the font is known to need a copy, so a Latin font never
        /// spends a pool name. Null, and said, when the font gets none.
        /// </summary>
        private static Entry Build(string name, UnityGameTranslator.Common.FontSource origin, string key, string path, byte[] bytes,
                                   Func<string, FontPool.Slot?> slotFor, Newtonsoft.Json.Linq.JObject translation)
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
                BaseFamily = OurFamilyMark + (origin == UnityGameTranslator.Common.FontSource.System ? "Sys " : "") + (parser.Metrics?.FontName ?? name),
                Font = font,
                Namer = new DerivedGlyphs(font, parser.GlyphCount),
            };
            entry.Namer.OnExhausted = why => TranslatorCore.LogWarning($"[DerivedFonts] {name}: {why}");

            try
            {
                Directory.CreateDirectory(_folder);
                if (File.Exists(entry.NamesPath))
                {
                    int refused = entry.Namer.Load(File.ReadAllText(entry.NamesPath));
                    if (refused > 0) TranslatorCore.LogWarning($"[DerivedFonts] {name}: {refused} saved name(s) did not fit this font and were left out");
                }
                PreName(entry, translation);
                if (_il2cpp) PreNameNatural(entry, parser.GlyphCount);
                var slot = slotFor(entry.BaseFamily);
                if (slot == null) return null;   // no pool name: said by TakeFromPool
                entry.Slots.Add(slot.Value);
                entry.OwnFile = Path.Combine(_folder, FileNameOf(key));
                byte[] derived = DerivedFontWriter.Write(bytes, entry.CurrentFamily, entry.Namer.Added, out string refusal);
                if (derived == null)
                {
                    TranslatorCore.LogWarning($"[DerivedFonts] {name}: no derived copy — {refusal}");
                    return null;
                }
                File.WriteAllBytes(entry.OwnFile, derived);
                if (_pool != null && _pool.Has(slot.Value.Name)) _pool.Fill(slot.Value, entry.OwnFile);
                entry.Namer.MarkWritten();
                TranslatorCore.LogInfo($"[DerivedFonts] {name}: derived copy ready ({entry.Namer.Added.Count} shaped glyph(s) known) as '{entry.CurrentFamily}'");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Faults.Say("DerivedFonts.Build", ex, Sanitize.Path(_folder));
                return null;
            }
            return entry;
        }

        /// <summary>
        /// Names, before the copy is written, every glyph the KNOWN translation will show — so the
        /// first scene does not rewrite the copy one line at a time. Only when the translation
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

        /// <summary>
        /// IL2CPP: every rewrite of the copy takes a pool name and makes the fonts it replaces draw
        /// their glyphs again, and a line that arrives while the game runs (a live translation, a
        /// typed text) needs a rewrite only for glyphs never named. Most glyphs a line needs are drawn where they stand — measured on the
        /// Hindi probe corpus: 101 of 133 — so every glyph of the font is named at its natural place
        /// once, at start, and the names left serve the positioned marks. Not for a font so large
        /// its glyphs would take more than half the private codepoints.
        /// </summary>
        private static void PreNameNatural(Entry entry, int glyphCount)
        {
            int capacity = PrivateGlyphs.Last - PrivateGlyphs.First + 1;
            if (glyphCount - 1 > capacity / 2)
            {
                TranslatorCore.LogInfo($"[DerivedFonts] {entry.Name}: {glyphCount} glyphs — too many to name all at start; lines arriving while the game runs use the session's names");
                return;
            }
            int before = entry.Namer.Added.Count;
            for (int g = 1; g < glyphCount; g++) entry.Namer.CodepointFor(g, 0, 0, 0);
            if (entry.Namer.Added.Count > before)
                TranslatorCore.LogInfo($"[DerivedFonts] {entry.Name}: {entry.Namer.Added.Count - before} glyph(s) named at their natural place for this session's new lines");
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
        /// hands their components the new copy. Mono rewrites the same file; IL2CPP takes a pool name
        /// (a family the engine has not opened).
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
                string file = entry.OwnFile;
                if (il2cpp)
                {
                    var slot = TakeFromPool(entry.Name);
                    if (slot == null) continue;   // said once; the new glyphs show at the next launch
                    entry.Slots.Add(slot.Value);
                    target = entry.Slots.Count - 1;
                    file = Path.Combine(_folder, $"ugt-{entry.Key}-{entry.Version + 1}.ttf");
                }
                var into = entry.Slots[target];
                byte[] derived = DerivedFontWriter.Write(entry.Source, into.Family, entry.Namer.Added, out string refusal);
                if (derived == null)
                {
                    TranslatorCore.LogWarning($"[DerivedFonts] {entry.Name}: the derived copy could not be rewritten — {refusal}");
                    continue;
                }
                try
                {
                    File.WriteAllBytes(file, derived);
                    if (_pool != null && _pool.Has(into.Name)) _pool.Fill(into, file);
                    File.WriteAllText(entry.NamesPath, entry.Namer.Save());
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Faults.Say("DerivedFonts.ProcessPending", ex, Sanitize.Path(file));
                    continue;
                }
                if (file != entry.OwnFile)
                {
                    entry.OlderFiles.Add(entry.OwnFile);
                    entry.OwnFile = file;
                    // The versions before: gone at once when the engine no longer holds them.
                    entry.OlderFiles.RemoveAll(TryRemove);
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
