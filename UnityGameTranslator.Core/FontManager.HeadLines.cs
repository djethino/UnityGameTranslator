using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    public static partial class FontManager
    {
        /// <summary>
        /// 🔴 **A head measures its lines as the GAME's font does** (issue #33, follow-up of
        /// 2026-10-12). TMP takes a line's heights from the font that owns its letters; what no
        /// letter sets — an empty line, a line feed — it takes from the font the text wears, the
        /// head (BackupHead), which holds no letter. Left with the replacement's heights, a menu
        /// written as one text ("item, empty line, item"), every letter of it the game's, spread
        /// away from the pointer the game moves by a fixed step (bench `tmphide`: 14.54 → 19.43;
        /// the Laboratory terminal of Blue Prince).
        /// The game's ascent, descent, line height, underline and strikethrough are written into
        /// the head, in the head's units. A line that holds letters keeps the heights of the font
        /// that draws them, as before.
        /// Off: the replacement's own heights, as every head had.
        /// </summary>
        internal static bool HeadKeepsGameLines = true;

        // ── The game's lines kept (FontSettings.keep_lines) ─────────────────────────────────────
        //
        // 🔴 A line takes the heights of the font that owns its letters, at the size it is drawn.
        // So three things move a text off the lines its game laid out (Blue Prince's terminals,
        // 2026-10-13): a size put on its font (the lines close up, the pointer the game moves by a
        // fixed step does not), a replacement with other heights (a title sits under its bar), a
        // letter taken from a fallback font (its line alone drops).
        //
        // The answer is in the FONTS, not in the text: every asset a text of that game font can
        // draw from carries the game font's line heights, divided by the size its texts are drawn
        // at. TMP then lays the text out on the game's lines by itself — its own measures of the
        // text stay true, nothing is done per frame, and the text is not touched.
        //   - with a replacement: the head HOLDS the replacement's letters (a static copy, on the
        //     replacement's atlas) under those heights — and they are in the text's own mesh;
        //   - behind it, or worn when the font has no replacement: the game font in a copy with
        //     those heights (LinesTwin), then each of its fallback fonts and TMP's own in such a
        //     copy, then the game font itself for a letter that reaches none of them.
        // Proven on the bench (`tmphide`, 6000.0): a menu's step, a translated title's baseline
        // and a paragraph's four lines are the game's to the hundredth at 80, 100 and 120 %.

        /// <summary>The lines a replaced component was last laid out on (KeptLines), beside _replacedFor.</summary>
        private static readonly Dictionary<int, float> _replacedLines = ForgetByNumber.Tracked(new Dictionary<int, float>());

        /// <summary>
        /// 0 when this game font's texts take the heights of whatever draws them (the default);
        /// otherwise the size they are drawn at, as a share of the game's — their lines are kept
        /// the game's (FontSettings.keep_lines).
        /// </summary>
        private static float KeptLines(string gameFontName, int instanceId)
        {
            if (string.IsNullOrEmpty(gameFontName)
                || !TranslatorCore.FontSettingsMap.TryGetValue(gameFontName, out var settings) || !settings.keep_lines)
                return 0f;
            float size = GetFontScale(gameFontName, instanceId);
            return size > 0.001f ? size : 1f;
        }

        private static string LinesText(float size) => size.ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>The heads that hold a replacement's letters themselves, by the replacement's number.</summary>
        private static readonly Dictionary<int, List<object>> _letterHeadsOf = ForgetByNumber.Tracked(new Dictionary<int, List<object>>());

        /// <summary>The live heads that hold this replacement's letters: what is added to it is added to them.</summary>
        internal static List<object> LetterHeadsOf(object replacement)
        {
            var live = new List<object>();
            if (!(replacement is UnityEngine.Object asset) || asset == null) return live;
            if (!_letterHeadsOf.TryGetValue(ObjectNumbers.Of(asset), out var heads)) return live;
            heads.RemoveAll(h => !TypeHelper.IsUnityObjectAlive(h));
            live.AddRange(heads);
            return live;
        }

        /// <summary>A copy made to carry the game's lines, and the font it copies.</summary>
        private sealed class LinesCopy
        {
            public object Twin, Source;
            /// <summary>The source fills its atlas as texts come: the copy is given its new letters (EnsureLinesLetters).</summary>
            public bool Dynamic;
            public readonly HashSet<uint> Has = new HashSet<uint>();
        }

        /// <summary>What a worn asset that keeps the game's lines draws from, in TMP's order.</summary>
        private sealed class LinesFamily
        {
            public string Name;
            public readonly List<LinesCopy> Copies = new List<LinesCopy>();
            /// <summary>The codes already looked at for this family: asked once.</summary>
            public readonly HashSet<uint> Seen = new HashSet<uint>();
        }

        private static readonly Dictionary<string, LinesCopy> _linesCopies = new Dictionary<string, LinesCopy>(StringComparer.Ordinal);
        private static readonly Dictionary<int, LinesFamily> _linesFamilies = ForgetByNumber.Tracked(new Dictionary<int, LinesFamily>());
        /// <summary>How many families hold a copy of a dynamic font: none, and no text is looked at.</summary>
        private static int _dynamicLinesFamilies;

        /// <summary>
        /// <paramref name="source"/> in a copy whose lines are the game font's, kept at the game's
        /// size for texts drawn at <paramref name="size"/>: its own letters on its own atlas, static.
        /// Null when it cannot be made (said): the source itself stands there, its lines its own.
        /// </summary>
        private static LinesCopy LinesTwin(object source, object gameFont, Type type, string gameFontName, float size)
        {
            if (!(source is UnityEngine.Object original) || original == null || !(gameFont is UnityEngine.Object game) || game == null) return null;
            string key = ObjectNumbers.Of(original) + "\u0001" + ObjectNumbers.Of(game) + "\u0001" + LinesText(size);
            if (_linesCopies.TryGetValue(key, out var known))
            {
                if (TypeHelper.IsUnityObjectAlive(known.Twin)) return known;
                _linesCopies.Remove(key);
            }
            try
            {
                const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
                var cast = TypeHelper.Il2CppCast(original, type);
                var twin = TypeHelper.Il2CppCast(UnityEngine.Object.Instantiate(original), type);
                if (!(twin is UnityEngine.Object made)) return null;
                string name = original == game ? gameFontName + " (lines " + LinesText(size) + ")"
                                               : original.name + " (lines of " + gameFontName + " " + LinesText(size) + ")";
                GiveGameLines(twin, gameFont, type, name, size);
                var copy = new LinesCopy { Twin = twin, Source = cast };
                object population = type.GetProperty("atlasPopulationMode", pub)?.GetValue(cast, null);
                copy.Dynamic = population != null && Convert.ToInt32(population) != 0;
                CustomFontLoader.MakeStatic(twin, type, name);
                // A dynamic source may open another atlas later: its copy reads the same list of them.
                if (copy.Dynamic) CustomFontLoader.ShareAtlasList(twin, cast, type);
                foreach (var character in ListOf(type.GetProperty("characterTable", pub)?.GetValue(twin, null)))
                    if (character != null) copy.Has.Add(Convert.ToUInt32(ValueOf(character, "unicode") ?? 0u));
                CustomFontLoader.RebuildLookupTables(twin, type);
                EngineStrings.SetName(made, name);
                made.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                _createdFallbackFontNames.Add(made.name);
                _gameFontOfCreated[made.name] = gameFontName;
                _linesCopies[key] = copy;
                return copy;
            }
            catch (Exception ex) { Faults.Say("FontManager.LinesTwin", ex.InnerException ?? ex, gameFontName); return null; }
        }

        /// <summary>
        /// The fallback list of an asset that keeps the game's lines, in TMP's order: the game font
        /// (its copy when the size is not the game's, <paramref name="gameCopy"/> false when the
        /// asset IS that copy), its own fallback fonts and TMP's, each in a copy with those lines,
        /// then the game font itself for whatever reaches none of them.
        /// </summary>
        private static void PutLinesBehind(object worn, object gameFont, Type type, string gameFontName, float size, bool gameCopy, string name)
        {
            var list = GetFallbackListReflection(worn);
            var add = list?.GetType().GetMethod("Add");
            if (add == null) return;
            var family = new LinesFamily { Name = name };
            if (worn is UnityEngine.Object wornObject)
            {
                // The asset itself, when it is a copy of the game font.
                foreach (var known in _linesCopies.Values)
                    if (known.Twin is UnityEngine.Object twinObject && twinObject == wornObject) { family.Copies.Add(known); break; }
            }
            // What the game font would have searched, before the list is emptied: the worn asset
            // may be the game font's own copy, whose list this is.
            var behind = new List<object>();
            foreach (var fallback in ListOf(GetFallbackListReflection(gameFont))) behind.Add(fallback);
            foreach (var fallback in ListOf(GlobalFallbacks(type))) behind.Add(fallback);
            ClearList(list);

            if (gameCopy && Math.Abs(size - 1f) > 0.001f)
            {
                var twin = LinesTwin(gameFont, gameFont, type, gameFontName, size);
                if (twin != null) { add.Invoke(list, new[] { TypeHelper.Il2CppCast(twin.Twin, type) }); family.Copies.Add(twin); }
            }
            var done = new HashSet<int>();
            foreach (var fallback in behind)
            {
                if (!(fallback is UnityEngine.Object asset) || asset == null) continue;
                if (asset == (gameFont as UnityEngine.Object) || _createdFallbackFontNames.Contains(asset.name)) continue;
                if (!done.Add(ObjectNumbers.Of(asset))) continue;
                var twin = LinesTwin(asset, gameFont, type, gameFontName, size);
                if (twin == null) continue;
                add.Invoke(list, new[] { TypeHelper.Il2CppCast(twin.Twin, type) });
                family.Copies.Add(twin);
            }
            add.Invoke(list, new[] { TypeHelper.Il2CppCast(gameFont, type) });

            if (worn is UnityEngine.Object key)
            {
                int id = ObjectNumbers.Of(key);
                bool wasDynamic = _linesFamilies.TryGetValue(id, out var before) && before.Copies.Exists(c => c.Dynamic);
                _linesFamilies[id] = family;
                bool dynamic = family.Copies.Exists(c => c.Dynamic);
                if (dynamic && !wasDynamic) _dynamicLinesFamilies++;
                else if (!dynamic && wasDynamic) _dynamicLinesFamilies--;
            }
        }

        /// <summary>TMP's own fallback fonts, searched after a font's (TMP_Settings.fallbackFontAssets); null when unreadable.</summary>
        private static object GlobalFallbacks(Type fontAssetType)
        {
            try
            {
                var settings = fontAssetType.Assembly.GetType("TMPro.TMP_Settings");
                return settings?.GetProperty("fallbackFontAssets", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
            }
            // Without them, a letter only TMP's own fallbacks hold keeps its own heights: said.
            catch (Exception ex) { Faults.Say("FontManager.GlobalFallbacks", ex.InnerException ?? ex); return null; }
        }

        /// <summary>
        /// The game font itself in a copy that keeps its lines at the game's size for texts drawn
        /// at <paramref name="size"/>, with what it searched behind it in such copies too — what a
        /// component of a font that has NO replacement wears when its lines are kept. Null when it
        /// cannot be made: the component keeps the game's font.
        /// </summary>
        private static object LinesWear(object gameFont, Type type, string gameFontName, float size)
        {
            // The old TextMesh Pro keeps its letters and its lines another way: not done there.
            if (type.GetProperty("characterTable", BindingFlags.Public | BindingFlags.Instance) == null) return null;
            var copy = LinesTwin(gameFont, gameFont, type, gameFontName, size);
            if (copy == null) return null;
            if (copy.Twin is UnityEngine.Object twin && !_linesFamilies.ContainsKey(ObjectNumbers.Of(twin)))
            {
                PutLinesBehind(copy.Twin, gameFont, type, gameFontName, size, gameCopy: false, name: twin.name);
                CustomFontLoader.RebuildLookupTables(copy.Twin, type);
            }
            return copy.Twin;
        }

        /// <summary>
        /// A text about to be drawn by a component whose font keeps the game's lines: the letters a
        /// DYNAMIC font of its family does not hold yet are asked of that font — which draws them
        /// on its own atlas, as it would have for this text — and entered in its copy, on the same
        /// atlas. Left to the font itself, they would be drawn under its own heights, and found
        /// after the fallbacks its copy stands before. Nothing is looked at while no such family exists.
        /// </summary>
        internal static void EnsureLinesLetters(int instanceId, object component, string text)
        {
            if (_dynamicLinesFamilies == 0 || string.IsNullOrEmpty(text) || component == null) return;
            if (instanceId != -1 && !_replacedFor.ContainsKey(instanceId)) return;
            EnsureLinesLettersOf(TypeHelper.GetFont(component), text);
        }

        private static void EnsureLinesLettersOf(object wornFont, string text)
        {
            if (_dynamicLinesFamilies == 0 || string.IsNullOrEmpty(text)) return;
            if (!(wornFont is UnityEngine.Object worn) || worn == null) return;
            if (!_linesFamilies.TryGetValue(ObjectNumbers.Of(worn), out var family)) return;
            List<uint> wanted = null;
            for (int i = 0; i < text.Length; i++)
            {
                uint code = text[i];
                // Two halves of one code above U+FFFF (UTF-16's own arithmetic, no question of script).
                if ((code & 0xFC00) == 0xD800 && i + 1 < text.Length && (text[i + 1] & 0xFC00) == 0xDC00)
                    code = 0x10000 + ((code - 0xD800) << 10) + (uint)(text[++i] - 0xDC00);
                if (family.Seen.Add(code)) (wanted ?? (wanted = new List<uint>())).Add(code);
            }
            // The ellipsis a cut text ends with is in no text: asked along with the first letters.
            if (family.Seen.Add(0x2026)) (wanted ?? (wanted = new List<uint>())).Add(0x2026);
            if (wanted == null) return;

            try
            {
                const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
                var type = TypeHelper.TMP_FontAssetType;
                foreach (var copy in family.Copies)
                {
                    if (!copy.Dynamic || !TypeHelper.IsUnityObjectAlive(copy.Twin) || !TypeHelper.IsUnityObjectAlive(copy.Source)) continue;
                    var ask = new System.Text.StringBuilder();
                    foreach (uint code in wanted)
                    {
                        if (copy.Has.Contains(code)) continue;
                        if (code < 0x10000) ask.Append((char)code);
                        else ask.Append((char)(0xD800 + ((code - 0x10000) >> 10))).Append((char)(0xDC00 + ((code - 0x10000) & 0x3FF)));
                    }
                    if (ask.Length == 0) continue;
                    AskFontFor(copy.Source, type, ask.ToString());
                    object lookup = type.GetProperty("characterLookupTable", pub)?.GetValue(copy.Source, null);
                    bool added = false;
                    foreach (uint code in wanted)
                    {
                        if (copy.Has.Contains(code)) continue;
                        object character = LookUp(lookup, code);
                        if (character == null) continue;
                        if (!CustomFontLoader.AddCharacterOf(copy.Twin, character, code)) continue;
                        copy.Has.Add(code);
                        added = true;
                    }
                    if (added) CustomFontLoader.RebuildLookupTables(copy.Twin, type);
                    // What this font now holds is its own: the fonts behind it are asked for the rest.
                    wanted.RemoveAll(code => copy.Has.Contains(code));
                    if (wanted.Count == 0) break;
                }
            }
            // The letters stay the font's own, under its own heights: said.
            catch (Exception ex) { Faults.Say("FontManager.EnsureLinesLetters", ex.InnerException ?? ex, family.Name); }
        }

        /// <summary>TMP's own adding of letters to a dynamic font (TryAddCharacters, whatever its shape in this TMP).</summary>
        private static void AskFontFor(object font, Type type, string letters)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "TryAddCharacters") continue;
                var parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(string)) continue;
                var arguments = new object[parameters.Length];
                arguments[0] = letters;
                for (int i = 1; i < parameters.Length; i++)
                    arguments[i] = parameters[i].ParameterType == typeof(bool) ? (object)false : null;
                method.Invoke(font, arguments);
                return;
            }
        }

        private static object ValueOf(object o, string name)
        {
            if (o == null) return null;
            const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
            var type = o.GetType();
            var property = type.GetProperty(name, pub);
            return property != null ? property.GetValue(o, null) : type.GetField(name, pub)?.GetValue(o);
        }

        /// <summary>The elements of a list or an array (managed, or IL2CPP's), by its count and indexer.</summary>
        private static IEnumerable<object> ListOf(object list)
        {
            if (list == null) yield break;
            if (list is Array managed) { foreach (var o in managed) yield return o; yield break; }
            const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
            var type = list.GetType();
            var count = type.GetProperty("Count", pub) ?? type.GetProperty("Length", pub);
            var item = type.GetProperty("Item", pub);
            if (count == null || item == null) yield break;
            int n = Convert.ToInt32(count.GetValue(list, null));
            for (int i = 0; i < n; i++) yield return item.GetValue(list, new object[] { i });
        }

        private static readonly string[] GameLineLengths =
        {
            "ascentLine", "capLine", "meanLine", "baseline", "descentLine", "lineHeight",
            "underlineOffset", "underlineThickness", "strikethroughOffset", "strikethroughThickness",
            "superscriptOffset", "subscriptOffset", "tabWidth",
        };

        /// <summary>The three lengths a line's place comes from; the others follow the letters' size.</summary>
        private static readonly string[] LinePlaceLengths = { "ascentLine", "descentLine", "lineHeight" };

        /// <param name="size">The size the texts of this asset are drawn at, as a share of the
        /// game's (the font's effective scale): the line's place is divided by it, so that the
        /// text, smaller or larger, stays on the game's lines. 1 where the lines follow the size.</param>
        private static void GiveGameLines(object head, object gameFont, Type type, string name, float size = 1f)
        {
            try
            {
                var game = TypeHelper.Il2CppCast(gameFont, type) ?? gameFont;
                if (!CustomFontLoader.TryGetModernFaceInfo(head, out float headPoint, out float headFace) || headPoint <= 0f) return;
                if (!CustomFontLoader.TryGetModernFaceInfo(game, out float gamePoint, out float gameFace) || gamePoint <= 0f) return;
                if (headFace <= 0f) headFace = 1f;
                if (gameFace <= 0f) gameFace = 1f;
                // A length is drawn at fontSize ÷ point size × face scale, in each asset's own terms:
                // equal on both sides once the game's is written in the head's units.
                double factor = (headPoint * gameFace) / (gamePoint * headFace);

                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                var property = type.GetProperty("faceInfo", BindingFlags.Public | BindingFlags.Instance);
                if (property == null || !property.CanWrite) return;
                object own = property.GetValue(head, null), theirs = property.GetValue(game, null);
                if (own == null || theirs == null) return;
                var face = own.GetType();
                int written = 0;
                foreach (string length in GameLineLengths)
                {
                    // A property on Mono; on IL2CPP the struct may show its field alone (m_AscentLine).
                    string field = "m_" + char.ToUpperInvariant(length[0]) + length.Substring(1);
                    var p = face.GetProperty(length, any);
                    var f = p == null ? (face.GetField(field, any) ?? face.GetField(length, any)) : null;
                    object value = p != null ? p.GetValue(theirs, null) : f?.GetValue(theirs);
                    if (value == null) continue;
                    double kept = size > 0.001f && Array.IndexOf(LinePlaceLengths, length) >= 0 ? 1.0 / size : 1.0;
                    float scaled = (float)(Convert.ToDouble(value) * factor * kept);
                    if (p != null && p.CanWrite) p.SetValue(own, scaled, null);
                    else if (f != null) f.SetValue(own, scaled);
                    else continue;
                    written++;
                }
                property.SetValue(head, own, null);
                TranslatorCore.LogDebug($"[FontManager] '{name}': lines laid out as the game font's ({written} length(s) taken{(Math.Abs(size - 1f) > 0.001f ? $", kept at the game's size for texts drawn at {size:F3}" : "")})");
            }
            // The head keeps the replacement's heights: a menu's empty lines are the replacement's — said.
            catch (Exception ex) { Faults.Say("FontManager.GiveGameLines", ex.InnerException ?? ex, name); }
        }
    }
}
