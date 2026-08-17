using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Text living in UI Toolkit (UIElements) rather than in uGUI components.
    ///
    /// 🔴 **A second discovery path, not another registered type.** Everything else in this mod
    /// rests on "a piece of text is a Component": <see cref="RegisteredTextType.ComponentType"/>,
    /// and a scan built on <c>FindAllObjectsOfType(componentType)</c>. A UI Toolkit
    /// <c>TextElement</c> is a <c>VisualElement</c> — it sits on no GameObject, appears in no scene
    /// hierarchy, and no FindObjects call will ever return it. Registering one more type could not
    /// have worked; the entry point had to be a different one.
    ///
    /// The way in is that <c>UIDocument</c> IS a MonoBehaviour. So the scan finds the documents the
    /// ordinary way and walks each one's <c>rootVisualElement</c> downwards.
    ///
    /// ⚠ **Everything here is reflection, deliberately.** UnityEngine.UIElementsModule is not among
    /// the assemblies this mod compiles against, and must not become one: the mod has to keep
    /// loading in games that have no UI Toolkit at all. It is also what makes the same code work on
    /// IL2CPP, where these types are interop proxies.
    ///
    /// Found on a game whose entire interface is UI Toolkit: the scanner ran 721 times in five
    /// seconds and never met a single component. See analyse/timberborn-ui-toolkit.md.
    /// </summary>
    internal static class UIToolkitSupport
    {
        #region Resolved types and members

        public static Type TextElementType { get; private set; }
        public static Type VisualElementType { get; private set; }
        public static Type UIDocumentType { get; private set; }

        private static Type _fontDefinitionType;      // UnityEngine.UIElements.FontDefinition
        private static Type _styleFontDefinitionType; // UnityEngine.UIElements.StyleFontDefinition

        private static PropertyInfo _textProp;        // TextElement.text
        private static PropertyInfo _rootProp;        // UIDocument.rootVisualElement

        // Walking children. Two ways in, and the order matters — see ChildCount/ChildAt.
        private static PropertyInfo _childCountProp;  // VisualElement.childCount
        private static MethodInfo _elementAtMethod;   // VisualElement.ElementAt(int)
        private static PropertyInfo _hierarchyProp;   // VisualElement.hierarchy
        private static PropertyInfo _hierCountProp;   // VisualElement.Hierarchy.childCount
        private static MethodInfo _hierElementAt;     // VisualElement.Hierarchy.ElementAt(int)

        private static PropertyInfo _styleProp;       // VisualElement.style  -> IStyle
        private static PropertyInfo _styleFontProp;   // IStyle.unityFontDefinition
        private static MethodInfo _fromFontMethod;    // FontDefinition.FromFont(Font)
        private static PropertyInfo _resolvedStyleProp; // VisualElement.resolvedStyle
        private static PropertyInfo _resolvedFontProp;  // IResolvedStyle.unityFont -> Font

        // The SDF side. Modern UI Toolkit states its font as a TextCore FontAsset, and then
        // `unityFont` is null — reading only that one finds nothing and says nothing.
        private static PropertyInfo _resolvedFontDefProp; // IResolvedStyle.unityFontDefinition
        private static PropertyInfo _fontDefFontProp;     // FontDefinition.font      -> Font
        private static PropertyInfo _fontDefAssetProp;    // FontDefinition.fontAsset -> Object
        private static MethodInfo _fromSdfFontMethod;     // FontDefinition.FromSDFFont(FontAsset)
        private static MethodInfo _createFontAssetMethod; // TextCore FontAsset.CreateFontAsset(Font)

        /// <summary>True when this game has UI Toolkit and we can read its text.</summary>
        public static bool Available { get; private set; }

        /// <summary>True when a replacement font can also be applied.</summary>
        public static bool CanSetFont { get; private set; }

        private static bool _initialized;

        #endregion

        #region Initialisation

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                TextElementType = FindType("UnityEngine.UIElements.TextElement");
                VisualElementType = FindType("UnityEngine.UIElements.VisualElement");
                UIDocumentType = FindType("UnityEngine.UIElements.UIDocument");

                if (TextElementType == null || VisualElementType == null || UIDocumentType == null)
                {
                    TranslatorCore.LogDebug("[UIToolkit] Not present in this game");
                    return;
                }

                var pubInst = BindingFlags.Public | BindingFlags.Instance;

                _textProp = TextElementType.GetProperty("text", pubInst);
                _rootProp = UIDocumentType.GetProperty("rootVisualElement", pubInst);

                _childCountProp = VisualElementType.GetProperty("childCount", pubInst);
                _elementAtMethod = VisualElementType.GetMethod(
                    "ElementAt", pubInst, null, new[] { typeof(int) }, null);

                // ⚠ The fallback, not the first choice: `hierarchy` is a STRUCT, so reflection has
                // to box it and then call through the box. That works on Mono and is the kind of
                // thing that behaves differently on IL2CPP, where a boxed proxy is not the object
                // the runtime expects. VisualElement's own childCount/ElementAt are plain instance
                // members and cost nothing to prefer.
                _hierarchyProp = VisualElementType.GetProperty("hierarchy", pubInst);
                if (_hierarchyProp != null)
                {
                    var hierType = _hierarchyProp.PropertyType;
                    _hierCountProp = hierType.GetProperty("childCount", pubInst);
                    _hierElementAt = hierType.GetMethod(
                        "ElementAt", pubInst, null, new[] { typeof(int) }, null);
                }

                Available = _textProp != null && _rootProp != null
                            && (_elementAtMethod != null || _hierElementAt != null);

                ResolveFontMembers(pubInst);

                TranslatorCore.LogInfo(
                    $"[UIToolkit] Available={Available}, font replacement={CanSetFont}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[UIToolkit] Initialisation failed: {ex.Message}");
                Available = false;
            }
        }

        /// <summary>
        /// What is needed to put a different font on an element.
        ///
        /// ⚠ Its own step, and its own flag: a game whose UI Toolkit build does not expose these
        /// must still have its text translated. Losing the font is a degraded result; losing the
        /// text is no result.
        /// </summary>
        private static void ResolveFontMembers(BindingFlags pubInst)
        {
            try
            {
                _fontDefinitionType = FindType("UnityEngine.UIElements.FontDefinition");
                _styleFontDefinitionType = FindType("UnityEngine.UIElements.StyleFontDefinition");
                _styleProp = VisualElementType.GetProperty("style", pubInst);

                if (_fontDefinitionType == null || _styleProp == null) return;

                _fromFontMethod = _fontDefinitionType.GetMethod(
                    "FromFont", BindingFlags.Public | BindingFlags.Static);

                // ⚠ On the INTERFACE the style is typed as, not on the object behind it: the
                // implementation is internal (InlineStyleAccess) and its members are explicit
                // interface implementations, which GetProperty on the concrete type does not return.
                _styleFontProp = _styleProp.PropertyType.GetProperty("unityFontDefinition", pubInst);

                // Reading what is actually in place, so the replacement can be ASKED FOR by name
                // instead of chosen here. Without it there is no "original font" to look up and any
                // font we applied would be one we picked on the player's behalf.
                _resolvedStyleProp = VisualElementType.GetProperty("resolvedStyle", pubInst);
                if (_resolvedStyleProp != null)
                {
                    var resolvedType = _resolvedStyleProp.PropertyType;
                    _resolvedFontProp = resolvedType.GetProperty("unityFont", pubInst);
                    _resolvedFontDefProp = resolvedType.GetProperty("unityFontDefinition", pubInst);
                }

                _fontDefFontProp = _fontDefinitionType.GetProperty("font", pubInst);
                _fontDefAssetProp = _fontDefinitionType.GetProperty("fontAsset", pubInst);
                _fromSdfFontMethod = _fontDefinitionType.GetMethod(
                    "FromSDFFont", BindingFlags.Public | BindingFlags.Static);

                // Building an SDF asset out of a plain Font — the same thing the TMP path does with
                // TMP_FontAsset.CreateFontAsset, on the other engine's type.
                var textCoreFontAsset = FindTextCoreFontAssetType();
                if (textCoreFontAsset != null)
                {
                    foreach (var candidate in textCoreFontAsset.GetMethods(
                                 BindingFlags.Public | BindingFlags.Static))
                    {
                        if (candidate.Name != "CreateFontAsset") continue;

                        var parameters = candidate.GetParameters();
                        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(Font))
                        {
                            _createFontAssetMethod = candidate;
                            break;
                        }
                    }
                }

                // Reading the font must work one way or the other; writing it must work one way or
                // the other. Neither branch alone is enough to call this available.
                bool canRead = _resolvedFontProp != null || _resolvedFontDefProp != null;
                bool canWrite = _styleFontProp != null && _styleFontDefinitionType != null
                                && (_fromFontMethod != null || _fromSdfFontMethod != null);

                CanSetFont = canRead && canWrite;
            }
            catch
            {
                CanSetFont = false;
            }
        }

        /// <summary>
        /// UI Toolkit's SDF font type — <c>UnityEngine.TextCore.Text.FontAsset</c>, which is NOT
        /// TMPro's <c>TMP_FontAsset</c> even though both wrap the same engine.
        ///
        /// ⚠ That distinction is the whole reason fonts looked absent here: the game reports
        /// "No game TMP fonts found" and it is telling the truth — its fonts are TextCore assets.
        /// </summary>
        private static Type FindTextCoreFontAssetType()
        {
            var direct = FindTypeAnywhere("UnityEngine.TextCore.Text.FontAsset");
            if (direct != null) return direct;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "FontAsset"
                            && type.Namespace != null
                            && type.Namespace.IndexOf("TextCore", StringComparison.Ordinal) >= 0)
                        {
                            return type;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        private static Type FindTypeAnywhere(string fullName)
        {
            var direct = Type.GetType(fullName, false);
            if (direct != null) return direct;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var found = asm.GetType(fullName, false);
                    if (found != null) return found;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// A type by full name, then by simple name across every loaded assembly.
        ///
        /// ⚠ The second pass exists for IL2CPP, where interop assemblies keep the original
        /// namespace but are not always named or loaded the way the first pass expects.
        /// </summary>
        private static Type FindType(string fullName)
        {
            var direct = Type.GetType(fullName, false);
            if (direct != null) return direct;

            string simpleName = fullName.Substring(fullName.LastIndexOf('.') + 1);

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var found = asm.GetType(fullName, false);
                    if (found != null) return found;
                }
                catch { }
            }

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == simpleName
                            && type.Namespace != null
                            && type.Namespace.EndsWith("UIElements", StringComparison.Ordinal))
                        {
                            return type;
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        #endregion

        #region Interception

        /// <summary>
        /// Patches the one setter every piece of UI Toolkit text goes through.
        ///
        /// ⚠ `TextElement.set_text` has no overloads — checked in a running game. Everything
        /// visible descends from TextElement (Label, Button, TextField…), so this single patch
        /// covers the whole framework rather than a list of widget types.
        /// </summary>
        public static int ApplyPatches(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            if (!Available) return 0;

            try
            {
                var setter = _textProp.SetMethod;
                if (setter == null) return 0;

                var prefix = typeof(UIToolkitSupport).GetMethod(
                    nameof(TextElement_SetText_Prefix), BindingFlags.Static | BindingFlags.Public);

                patcher(setter, prefix, null);
                TranslatorCore.LogInfo("[UIToolkit] Patched TextElement.set_text");
                return 1;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[UIToolkit] Could not patch set_text: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// While true, the setter passes text through untouched.
        ///
        /// 🔴 The scan writes translated text back through the very setter it is patching. Without
        /// this, that write is read as a fresh string from the game and translated again — a
        /// translation of a translation, on every pass, for as long as the element exists.
        ///
        /// ⚠ ThreadStatic: the guard belongs to the thread doing the writing, and Unity work
        /// happens on the main thread while nothing stops a background thread from setting text of
        /// its own.
        /// </summary>
        [ThreadStatic] private static bool _writingBack;

        public static void TextElement_SetText_Prefix(object __instance, ref string value)
        {
            if (_writingBack) return;
            if (string.IsNullOrEmpty(value)) return;
            if (!TranslatorCore.TranslationsActive) return;

            // Unity APIs below are main-thread only; on IL2CPP the wrong thread crashes natively
            // rather than throwing, which is not a failure anyone can diagnose from a log.
            if (!TranslatorCore.IsMainThread) return;

            try
            {
                string translated = TranslatorCore.TranslateTextWithTracking(value, __instance);
                if (!string.IsNullOrEmpty(translated)) value = translated;
            }
            catch { }
        }

        #endregion

        #region Discovery

        /// <summary>
        /// What we last wrote into an element, so a pass can tell its own work from the game's.
        ///
        /// ⚠ A ConditionalWeakTable rather than a dictionary: UI Toolkit creates and drops elements
        /// constantly — list virtualisation recycles them by the hundred — and a strong reference
        /// per element would keep every one of them alive for the life of the process.
        /// </summary>
        private static readonly ConditionalWeakTable<object, string> _written =
            new ConditionalWeakTable<object, string>();

        private static float _lastScanTime;

        /// <summary>
        /// How many elements one pass may look at.
        ///
        /// ⚠ A ceiling, not a target. The uGUI scanner spreads its work across frames with a
        /// measured budget; this one does not yet, so the protection against a pathological tree is
        /// a hard stop. A game that exceeds it gets the rest on the next pass — see the note in the
        /// analysis about this not being incremental.
        /// </summary>
        private const int MaxElementsPerPass = 6000;

        public static void Scan()
        {
            if (!Available) return;

            // Same cadence as the rest of the scanner: how long a newly shown string may stay
            // untranslated is one setting, not one per subsystem.
            float interval = TranslatorCore.Config?.max_text_detection_latency_seconds ?? 1f;
            if (interval < 0.1f) interval = 0.1f;

            float now = Time.realtimeSinceStartup;
            if (_lastScanTime != 0f && now - _lastScanTime < interval) return;
            _lastScanTime = now;

            try
            {
                var documents = TypeHelper.FindAllObjectsOfType(UIDocumentType);
                if (documents == null || documents.Length == 0) return;

                int visited = 0;

                foreach (var document in documents)
                {
                    if (document == null) continue;

                    object root = null;
                    try { root = _rootProp.GetValue(document, null); }
                    catch { }

                    if (root == null) continue;

                    ApplyFontIfNeeded(root);

                    visited += WalkTree(root, MaxElementsPerPass - visited);
                    if (visited >= MaxElementsPerPass) break;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[UIToolkit] Scan error: {ex.Message}");
            }
        }

        /// <summary>
        /// Walks one document, translating the text it finds.
        ///
        /// ⚠ An explicit stack, not recursion: a UI Toolkit tree is as deep as its author made it,
        /// and a deep one would take the whole game down with a StackOverflow that no catch can
        /// intercept.
        /// </summary>
        private static int WalkTree(object root, int budget)
        {
            int visited = 0;
            var stack = new Stack<object>();
            stack.Push(root);

            while (stack.Count > 0 && visited < budget)
            {
                var element = stack.Pop();
                visited++;

                if (TextElementType.IsInstanceOfType(element))
                    TranslateElement(element);

                int count = ChildCount(element);
                for (int i = 0; i < count; i++)
                {
                    var child = ChildAt(element, i);
                    if (child != null) stack.Push(child);
                }
            }

            return visited;
        }

        private static void TranslateElement(object element)
        {
            try
            {
                var current = _textProp.GetValue(element, null) as string;
                if (string.IsNullOrEmpty(current)) return;

                // Ours already. Reading it back and asking for a translation would be asking to
                // translate the target language into itself.
                if (_written.TryGetValue(element, out var mine) && mine == current) return;

                string translated = TranslatorCore.TranslateTextWithTracking(current, element);
                if (string.IsNullOrEmpty(translated) || translated == current) return;

                _writingBack = true;
                try { _textProp.SetValue(element, translated, null); }
                finally { _writingBack = false; }

                _written.Remove(element);
                _written.Add(element, translated);
            }
            catch { }
        }

        private static int ChildCount(object element)
        {
            try
            {
                if (_childCountProp != null)
                    return (int)_childCountProp.GetValue(element, null);

                if (_hierarchyProp != null && _hierCountProp != null)
                {
                    var hierarchy = _hierarchyProp.GetValue(element, null);
                    if (hierarchy != null) return (int)_hierCountProp.GetValue(hierarchy, null);
                }
            }
            catch { }

            return 0;
        }

        private static object ChildAt(object element, int index)
        {
            try
            {
                if (_elementAtMethod != null)
                    return _elementAtMethod.Invoke(element, new object[] { index });

                if (_hierarchyProp != null && _hierElementAt != null)
                {
                    var hierarchy = _hierarchyProp.GetValue(element, null);
                    if (hierarchy != null)
                        return _hierElementAt.Invoke(hierarchy, new object[] { index });
                }
            }
            catch { }

            return null;
        }

        #endregion

        #region Fonts

        /// <summary>Roots already carrying a replacement, and which one.</summary>
        private static readonly ConditionalWeakTable<object, string> _fontApplied =
            new ConditionalWeakTable<object, string>();

        /// <summary>
        /// Puts the replacement font on a document's root, once.
        ///
        /// ⚠ **Once per document, not once per element** — a property of UI Toolkit rather than a
        /// shortcut: `-unity-font-definition` is an INHERITED style, so a root carries it down the
        /// whole tree. uGUI has no equivalent, which is why the TMP path has to touch every
        /// component and force a mesh update afterwards.
        ///
        /// ⚠ **The original font is READ, never assumed.** FontManager answers "what replaces this
        /// one", so it needs a name; picking a font here because none could be read would be
        /// choosing on the player's behalf, and choosing wrong is worse than leaving the game's own.
        ///
        /// ⚠ An element the game styles explicitly keeps its font: an inline style beats an
        /// inherited one. Forcing each element instead would undo the game's own layout decisions.
        /// </summary>
        /// <summary>Said once, so a font that never arrives can be diagnosed from the log.</summary>
        private static bool _fontDiagnosed;
        private static bool _noReplacementDiagnosed;

        private static void ApplyFontIfNeeded(object root)
        {
            if (!CanSetFont) return;
            if (!TranslatorCore.FontReplacementActive) return;

            try
            {
                var resolved = _resolvedStyleProp?.GetValue(root, null);
                if (resolved == null) return;

                // What the game actually renders with, whichever engine it states it in.
                var current = ReadResolvedFont(resolved, out bool isSdf);

                // 🔴 Reported, not swallowed. The first version read only `unityFont`, found null on
                // an SDF game and returned in silence: the log said "font replacement=True" while
                // nothing was ever replaced, and nothing said which of the four exits was taken.
                if (!_fontDiagnosed)
                {
                    _fontDiagnosed = true;
                    TranslatorCore.LogInfo(current == null
                        ? "[UIToolkit] No font on the document root — neither unityFont nor "
                          + "unityFontDefinition resolved to one; leaving the game's own."
                        : $"[UIToolkit] Document font: {current.name} "
                          + $"({(isSdf ? "SDF/TextCore" : "legacy Font")})");
                }

                if (current == null || string.IsNullOrEmpty(current.name)) return;

                string originalName = current.name;

                // Already ours: the resolved font IS the replacement, so asking again would look up
                // a replacement for a replacement.
                if (_fontApplied.TryGetValue(root, out var applied) && applied == originalName) return;

                var replacement = FontManager.GetUnityReplacementFont(originalName);

                if (replacement == null)
                {
                    // ⚠ Also said once. "The font was read but nothing replaces it" is the ordinary
                    // case — no replacement is configured for it — and it is indistinguishable from
                    // a failure unless it says so.
                    if (!_noReplacementDiagnosed)
                    {
                        _noReplacementDiagnosed = true;
                        TranslatorCore.LogInfo(
                            $"[UIToolkit] No replacement configured for '{originalName}' — the "
                            + "game's own font is kept.");
                    }
                    return;
                }

                object definition = BuildDefinition(replacement, isSdf);
                if (definition == null) return;

                // StyleFontDefinition wraps it. An implicit operator hides this in C#; through
                // reflection the conversion has to be made by hand.
                var styleValue = Activator.CreateInstance(_styleFontDefinitionType, definition);

                var style = _styleProp.GetValue(root, null);
                if (style == null) return;

                _styleFontProp.SetValue(style, styleValue, null);

                _fontApplied.Remove(root);
                _fontApplied.Add(root, replacement.name ?? originalName);

                TranslatorCore.LogInfo(
                    $"[UIToolkit] Font on document root: {originalName} -> {replacement.name}");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[UIToolkit] Font replacement failed: {ex.Message}");
            }
        }

        /// <summary>
        /// The font in force on an element, from either of the two ways UI Toolkit states one.
        ///
        /// ⚠ `unityFont` first because it is the cheaper read, but it is null on any modern build:
        /// UI Toolkit states its font as a TextCore FontAsset, and `unityFontDefinition` is where
        /// that lives. Both are UnityEngine.Objects, so the caller only needs the name.
        /// </summary>
        private static UnityEngine.Object ReadResolvedFont(object resolvedStyle, out bool isSdf)
        {
            isSdf = false;

            try
            {
                if (_resolvedFontProp != null
                    && _resolvedFontProp.GetValue(resolvedStyle, null) is Font legacy && legacy != null)
                {
                    return legacy;
                }
            }
            catch { }

            try
            {
                var definition = _resolvedFontDefProp?.GetValue(resolvedStyle, null);
                if (definition == null) return null;

                if (_fontDefAssetProp?.GetValue(definition, null) is UnityEngine.Object asset
                    && asset != null)
                {
                    isSdf = true;
                    return asset;
                }

                if (_fontDefFontProp?.GetValue(definition, null) is Font font && font != null)
                    return font;
            }
            catch { }

            return null;
        }

        /// <summary>Replacement fonts already turned into SDF assets, by font name.</summary>
        private static readonly Dictionary<string, object> _sdfCache =
            new Dictionary<string, object>();

        /// <summary>
        /// Wraps our replacement the way the game states its own.
        ///
        /// ⚠ Matching the engine matters: handing a legacy Font to a document laid out for SDF
        /// changes how every glyph is rasterised, and USS styles written against SDF stop applying.
        /// Building the SDF asset is the same move the TMP path makes with
        /// TMP_FontAsset.CreateFontAsset — on the other engine's type.
        ///
        /// ⚠ Cached by name: creating a font asset rasterises an atlas, and doing it once per scan
        /// pass would be the kind of leak that only shows up after twenty minutes of play.
        /// </summary>
        private static object BuildDefinition(Font replacement, bool isSdf)
        {
            if (isSdf && _createFontAssetMethod != null && _fromSdfFontMethod != null)
            {
                string key = replacement.name ?? "?";

                if (!_sdfCache.TryGetValue(key, out var asset))
                {
                    asset = _createFontAssetMethod.Invoke(null, new object[] { replacement });
                    _sdfCache[key] = asset;
                }

                if (asset != null)
                    return _fromSdfFontMethod.Invoke(null, new[] { asset });

                // Falls through to the legacy path below: a document with our font in the wrong
                // engine still reads better than one showing squares.
            }

            return _fromFontMethod?.Invoke(null, new object[] { replacement });
        }

        #endregion
    }
}
