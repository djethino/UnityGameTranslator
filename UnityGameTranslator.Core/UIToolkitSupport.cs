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
        private static Type _textCoreFontAssetType;       // UnityEngine.TextCore.Text.FontAsset

        private static PropertyInfo _styleColorProp;      // IStyle.color         -> StyleColor
        private static PropertyInfo _resolvedColorProp;   // IResolvedStyle.color -> Color
        private static Type _styleColorType;              // UnityEngine.UIElements.StyleColor

        private static PropertyInfo _styleFontSizeProp;   // IStyle.fontSize         -> StyleLength
        private static PropertyInfo _resolvedFontSizeProp;// IResolvedStyle.fontSize -> float
        private static Type _styleLengthType;             // UnityEngine.UIElements.StyleLength

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

                // Colour, for the Fonts tab's highlight. Same two-sided shape as the font: read
                // what is resolved, write an inline style.
                _styleColorProp = _styleProp.PropertyType.GetProperty("color", pubInst);
                if (_resolvedStyleProp != null)
                    _resolvedColorProp = _resolvedStyleProp.PropertyType.GetProperty("color", pubInst);
                _styleColorType = FindTypeAnywhere("UnityEngine.UIElements.StyleColor");

                // Size, same two-sided shape again.
                _styleFontSizeProp = _styleProp.PropertyType.GetProperty("fontSize", pubInst);
                if (_resolvedStyleProp != null)
                    _resolvedFontSizeProp = _resolvedStyleProp.PropertyType.GetProperty("fontSize", pubInst);
                _styleLengthType = FindTypeAnywhere("UnityEngine.UIElements.StyleLength");

                _fontDefFontProp = _fontDefinitionType.GetProperty("font", pubInst);
                _fontDefAssetProp = _fontDefinitionType.GetProperty("fontAsset", pubInst);
                _fromSdfFontMethod = _fontDefinitionType.GetMethod(
                    "FromSDFFont", BindingFlags.Public | BindingFlags.Static);

                // The type only — building the asset is FontManager's job, which already knows
                // which overload to reach for and with what.
                _textCoreFontAssetType = FindTextCoreFontAssetType();

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

                    ReportProxyIdentityOnce(root);

                    visited += Walk(root, MaxElementsPerPass - visited, ProcessElement);
                    if (visited >= MaxElementsPerPass) break;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[UIToolkit] Scan error: {ex.Message}");
            }
        }

        /// <summary>
        /// Walks one document and hands over every TextElement in it. Returns how many elements
        /// were looked at, so a caller can spend a budget across several documents.
        ///
        /// ⚠ An explicit stack, not recursion: a UI Toolkit tree is as deep as its author made it,
        /// and a deep one would take the whole game down with a StackOverflow that no catch can
        /// intercept.
        ///
        /// ⚠ One walker for both readers — translating and highlighting. Two would drift apart, and
        /// the one that drifts is whichever is used less: the Fonts tab would light up a set of
        /// elements the translation pass never visits.
        /// </summary>
        private static int Walk(object root, int budget, Action<object> action)
        {
            int visited = 0;
            var stack = new Stack<object>();
            stack.Push(root);

            while (stack.Count > 0 && visited < budget)
            {
                var element = stack.Pop();
                visited++;

                var asText = AsTextElement(element);
                if (asText != null) action(asText);

                int count = ChildCount(element);
                for (int i = 0; i < count; i++)
                {
                    var child = ChildAt(element, i);
                    if (child != null) stack.Push(child);
                }
            }

            return visited;
        }

        /// <summary>
        /// One element, both jobs: its font, then its text.
        ///
        /// 🔴 **The font FIRST, and outside the "already translated" shortcut.** Putting the font
        /// handling behind that shortcut made replacement impossible in practice: an element is
        /// translated once, after which the shortcut returns immediately, and a fallback chosen in
        /// the Fonts tab *afterwards* was never applied to anything already on screen. Which is
        /// every element, one pass after the game opens.
        ///
        /// Fonts and text also change on different schedules — a font is re-picked from a settings
        /// screen, a text is written by the game — so tying one to the other's state was wrong in
        /// principle as well as in effect.
        /// </summary>
        private static void ProcessElement(object element)
        {
            // Also the switch for "translate this font or not", so it is asked every pass.
            if (!HandleFont(element)) return;

            TranslateElement(element);
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

        private static bool _identityReported;

        /// <summary>
        /// Says, once, whether asking twice for the same child hands back the same object.
        ///
        /// 🔴 **The one assumption this whole file rests on, and the one IL2CPP is entitled to
        /// break.** Everything remembered per element — what we wrote, the font it started with,
        /// its original size, its highlight colour — is held in a ConditionalWeakTable keyed on the
        /// element itself. That works while a given element is always the same object. On IL2CPP,
        /// each call can build a fresh interop proxy around the same native object, and then every
        /// one of those tables misses on every pass: text retranslated endlessly, fonts never seen
        /// as already replaced, sizes rescaled from an already-scaled value.
        ///
        /// ⚠ Measured rather than assumed, and reported rather than worked around: rewriting all of
        /// it to key on native pointers would be a large change, and doing it before knowing whether
        /// it is needed is how a fix lands on a problem nobody has. The line below is what tells us.
        /// </summary>
        private static void ReportProxyIdentityOnce(object root)
        {
            if (_identityReported) return;

            try
            {
                if (ChildCount(root) < 1) return;

                _identityReported = true;

                var first = ChildAt(root, 0);
                var again = ChildAt(root, 0);

                bool stable = ReferenceEquals(first, again);

                TranslatorCore.LogInfo(stable
                    ? "[UIToolkit] Element identity is stable — per-element state will work."
                    : "🔴 [UIToolkit] Element identity is NOT stable (a fresh proxy per call): "
                      + "per-element state cannot be keyed on the object. Translation will repeat "
                      + "and fonts will be re-applied every pass.");
            }
            catch { }
        }

        /// <summary>
        /// The element as a TextElement, or null when it is not one.
        ///
        /// 🔴 **Not just `IsInstanceOfType`, because of IL2CPP.** There, what a call hands back is an
        /// interop PROXY, and the proxy is often typed as the declared return type — `VisualElement`
        /// — while the native object is a Label. A managed type test then answers "not text" about
        /// every piece of text in the game, and the pass would walk the whole tree finding nothing,
        /// silently. TryCast asks the native side instead, which is what TypeHelper.Il2CppCast wraps.
        ///
        /// ⚠ The CAST result is what gets returned, not the original: reading `text` off a proxy
        /// typed as the base class would not find the property.
        /// </summary>
        private static object AsTextElement(object element)
        {
            if (element == null) return null;
            if (TextElementType.IsInstanceOfType(element)) return element;

            // Mono: the test above is the whole answer, and this call is a no-op that returns null.
            if (TranslatorCore.Adapter?.IsIL2CPP != true) return null;

            try
            {
                var cast = TypeHelper.Il2CppCast(element, TextElementType);
                return TextElementType.IsInstanceOfType(cast) ? cast : null;
            }
            catch { return null; }
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

        /// <summary>Said once, so a font that never arrives can be diagnosed from the log.</summary>
        private static bool _fontDiagnosed;
        private static bool _noReplacementDiagnosed;

        /// <summary>
        /// The font each element STARTED with — its "settings font name".
        ///
        /// 🔴 The equivalent of FontManager's `_originalFontsPerComponent`, and needed for the same
        /// reason: once the font is swapped, reading the element gives OUR font back. Everything the
        /// Fonts tab does — matching, highlighting, deciding what to replace — is keyed on the
        /// game's original name, never on the replacement.
        ///
        /// ⚠ Keyed by element rather than by instance id, because a VisualElement has none. That is
        /// also why FontManager.GetSettingsFontName is not called here: it is an instance-id API.
        /// </summary>
        private static readonly ConditionalWeakTable<object, string> _originalFontName =
            new ConditionalWeakTable<object, string>();

        /// <summary>
        /// The font OBJECT each element started with, kept so it can be put back.
        ///
        /// 🔴 The name is not enough to restore. Once an inline style carries our replacement,
        /// clearing the fallback in the Fonts tab has to write something — and the only thing that
        /// puts the element back exactly as it was is the object it had. This is what
        /// FontManager.RestoreOriginalFont does per component, with `_originalFontsPerComponent`.
        /// </summary>
        private static readonly ConditionalWeakTable<object, object> _originalFontObject =
            new ConditionalWeakTable<object, object>();

        /// <summary>
        /// Registers the element's font, applies the configured replacement, and says whether this
        /// element may be translated at all.
        ///
        /// 🔴 **Registration is the point.** Nothing can be replaced before the font is in
        /// FontSettingsMap: `GetUnityReplacementFont` returns null for a name it does not know, and
        /// the Fonts tab cannot offer what it was never told about. A first version read the font
        /// and never registered it — so the tab kept listing only the mod's own Arial while every
        /// font in the game stayed invisible and unconfigurable.
        ///
        /// ⚠ Registered as **"Unity"**, not as a type of our own. The replacement really does travel
        /// the Unity path — `GetUnityReplacementFont` → `CreateUnityFontFromSystem` yields a Font,
        /// not a TMP asset with its atlas and material — and `BelongsToFamily` rejects any type it
        /// does not know, which would quietly drop these fonts out of every list.
        ///
        /// Returns false when translation is switched off for this font.
        /// </summary>
        private static bool HandleFont(object element)
        {
            if (!CanSetFont) return true;

            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                if (resolved == null) return true;

                var currentFont = ReadResolvedFont(resolved, out bool isSdf);
                if (currentFont == null || string.IsNullOrEmpty(currentFont.name)) return true;

                if (!_originalFontName.TryGetValue(element, out var settingsName))
                {
                    settingsName = currentFont.name;
                    _originalFontName.Add(element, settingsName);
                    _originalFontObject.Add(element, currentFont);

                    // The shared registry, so the font reaches the Fonts tab and can be given a
                    // fallback. RegisterFontObject rather than ...ByName: we hold the object, which
                    // is what the other paths hand over.
                    FontManager.RegisterFontObject(currentFont, "Unity");

                    if (!_fontDiagnosed)
                    {
                        _fontDiagnosed = true;
                        TranslatorCore.LogInfo(
                            $"[UIToolkit] First document font: {settingsName} "
                            + $"({(isSdf ? "SDF/TextCore" : "legacy Font")}) — registered as a game font");
                    }
                }

                if (!FontManager.IsTranslationEnabled(settingsName)) return false;

                ApplyReplacement(element, settingsName, currentFont, isSdf);
                ApplyScale(element, settingsName);
                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Swaps in the configured replacement.
        ///
        /// ⚠ Inline on the element, not inherited from the root. Inheritance would be simpler — and
        /// a first version used it — but it cannot see what each element actually uses: a document
        /// mixes fonts, and the Fonts tab lists them one by one. Acting per element is also what the
        /// other paths do per component, which is what keeps the tab's counts truthful.
        /// </summary>
        private static void ApplyReplacement(object element, string settingsName,
                                             UnityEngine.Object currentFont, bool isSdf)
        {
            var replacement = FontManager.GetUnityReplacementFont(settingsName);

            if (replacement == null)
            {
                // 🔴 **Putting it back is an action, not the absence of one.** Clearing the fallback
                // used to fall straight through this return, so the replacement stayed on screen and
                // "(none)" could never be gone back to while the game ran. Nothing else was going to
                // undo an inline style we wrote.
                RestoreOriginalFont(element, settingsName, currentFont);

                // ⚠ Said once. "Read, but nothing configured to replace it" is the ordinary case,
                // and silence made it indistinguishable from a failure.
                if (!_noReplacementDiagnosed)
                {
                    _noReplacementDiagnosed = true;
                    TranslatorCore.LogInfo(
                        $"[UIToolkit] No fallback configured for '{settingsName}' — the game's own "
                        + "font is kept. Pick one in the Fonts tab to replace it.");
                }
                return;
            }

            // 🔴 **Compared against the font we WANT, never against the original.** The first
            // version returned early as soon as the element no longer wore its original font —
            // which is true the moment one replacement lands, so a second choice from the Fonts tab
            // could never be applied. Picking Bravura then Carlito left Bravura on screen for good.
            //
            // This is the test FontManager.ApplyFontReplacement makes too: current == replacement,
            // stop; anything else, write.
            string wanted = replacement.name;
            if (string.Equals(currentFont.name, wanted, StringComparison.Ordinal)) return;

            object definition = BuildDefinition(replacement, isSdf);
            if (definition == null) return;

            var styleValue = Activator.CreateInstance(_styleFontDefinitionType, definition);
            var style = _styleProp.GetValue(element, null);
            if (style == null) return;

            _styleFontProp.SetValue(style, styleValue, null);

            // ⚠ Said once PER FONT, not once ever: the one-shot flag hid every later change and
            // made a working replacement look like a dead one in the log.
            if (_replacementLogged.Add(wanted))
            {
                TranslatorCore.LogInfo(
                    $"[UIToolkit] Font replaced: {settingsName} -> {wanted}"
                    + $" ({(isSdf ? "as an SDF asset" : "as a Font")})");
            }
        }

        /// <summary>
        /// Puts back the font an element started with, when nothing is configured to replace it.
        ///
        /// ⚠ Only when it is actually wearing something else — otherwise every element of every
        /// pass would be written for nothing, on the ordinary path where no fallback is set.
        ///
        /// ⚠ The original OBJECT is re-applied rather than the inline style being cleared: it is
        /// what FontManager does per component, and it puts the element back in the state we found
        /// it in without depending on how the game had styled it.
        /// </summary>
        private static void RestoreOriginalFont(object element, string settingsName,
                                                UnityEngine.Object currentFont)
        {
            if (string.Equals(currentFont.name, settingsName, StringComparison.Ordinal)) return;
            if (!_originalFontObject.TryGetValue(element, out var original) || original == null) return;

            try
            {
                bool originalIsSdf = !(original is Font);

                object definition = originalIsSdf
                    ? _fromSdfFontMethod?.Invoke(null, new[] { original })
                    : _fromFontMethod?.Invoke(null, new[] { original });

                if (definition == null) return;

                var styleValue = Activator.CreateInstance(_styleFontDefinitionType, definition);
                var style = _styleProp.GetValue(element, null);
                if (style == null) return;

                _styleFontProp.SetValue(style, styleValue, null);

                if (_restoreLogged.Add(settingsName))
                    TranslatorCore.LogInfo($"[UIToolkit] Font restored: back to {settingsName}");
            }
            catch { }
        }

        private static readonly HashSet<string> _restoreLogged = new HashSet<string>();

        /// <summary>Elements whose original size we hold, so a scale can be undone.</summary>
        private static readonly ConditionalWeakTable<object, object> _originalFontSize =
            new ConditionalWeakTable<object, object>();

        private static readonly HashSet<string> _replacementLogged = new HashSet<string>();
        private static bool _scaleDiagnosed;

        /// <summary>
        /// Applies the font's size multiplier, and puts the original back when it returns to 1.
        ///
        /// ⚠ The size the element STARTED with is kept, not the current one: scaling the scaled
        /// value compounds, and a slider dragged three times would end up multiplying three times.
        /// Same reason FontManager keeps `_originalFontSizes` per component.
        ///
        /// ⚠ `GetFontScale(name)` — the overload without a component id. The per-component override
        /// needs an instance id, which a VisualElement has none of, so what applies here is the
        /// font-wide setting. Per-element overrides are simply not offered on this path.
        /// </summary>
        private static void ApplyScale(object element, string settingsName)
        {
            if (_styleFontSizeProp == null || _resolvedFontSizeProp == null
                || _styleLengthType == null) return;

            try
            {
                var resolved = _resolvedStyleProp.GetValue(element, null);
                if (resolved == null) return;

                if (!(_resolvedFontSizeProp.GetValue(resolved, null) is float currentSize)) return;
                if (currentSize <= 0f) return;

                float original;
                if (_originalFontSize.TryGetValue(element, out var stored) && stored is float kept)
                {
                    original = kept;
                }
                else
                {
                    original = currentSize;
                    _originalFontSize.Add(element, original);
                }

                float scale = FontManager.GetFontScale(settingsName);
                float wanted = original * scale;

                // Below what the eye or the layout can tell apart — writing it would cost a style
                // resolution every pass for nothing.
                if (Math.Abs(wanted - currentSize) < 0.1f) return;

                var style = _styleProp.GetValue(element, null);
                if (style == null) return;

                _styleFontSizeProp.SetValue(
                    style, Activator.CreateInstance(_styleLengthType, wanted), null);

                if (!_scaleDiagnosed && Math.Abs(scale - 1f) > 0.001f)
                {
                    _scaleDiagnosed = true;
                    TranslatorCore.LogInfo(
                        $"[UIToolkit] Font size scaled for '{settingsName}': ×{scale:F2} "
                        + $"({original:F1} -> {wanted:F1})");
                }
            }
            catch { }
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
            if (isSdf && _textCoreFontAssetType != null && _fromSdfFontMethod != null)
            {
                string key = replacement.name ?? "?";

                if (!_sdfCache.TryGetValue(key, out var asset))
                {
                    // ⚠ FontManager's creators, not new ones here — and BOTH, in its order. The
                    // first hands the engine a Font; when that comes back null (a dynamic OS font
                    // with no usable data) the second asks by family name and often succeeds on the
                    // very same font. Using only the first is what made some fonts work and others
                    // not: Ebrima and Lato went through, Liberation Sans and the Adobe faces did not.
                    asset = FontManager.CreateSdfFontAsset(replacement, _textCoreFontAssetType)
                            ?? FontManager.CreateSdfFontAssetByFamily(replacement, _textCoreFontAssetType);

                    _sdfCache[key] = asset;

                    if (asset == null)
                    {
                        TranslatorCore.LogWarning(
                            $"[UIToolkit] '{replacement.name}' cannot be turned into an SDF asset — "
                            + "keeping the game's font. Pick another one in the Fonts tab.");
                    }
                }

                if (asset != null)
                    return _fromSdfFontMethod.Invoke(null, new[] { asset });

                // 🔴 **Nothing, rather than a legacy Font.** A document laid out for SDF renders one
                // with different metrics: text lands in the wrong places and some of it is not drawn
                // at all — which reads as "the mod broke the game", not as "that font is unusable".
                // Keeping the game's own font is the honest outcome, and the warning above says why.
                return null;
            }

            return _fromFontMethod?.Invoke(null, new object[] { replacement });
        }

        #endregion

        #region Highlight (Fonts tab — which text wears which font)

        /// <summary>Colour each element had before we tinted it.</summary>
        private static readonly ConditionalWeakTable<object, object> _highlightOriginalColor =
            new ConditionalWeakTable<object, object>();

        /// <summary>Elements currently tinted, so clearing does not have to walk the tree again.</summary>
        private static readonly List<object> _highlighted = new List<object>();

        /// <summary>
        /// Tints the text using <paramref name="fontName"/> and dims the rest — the UI Toolkit half
        /// of the Fonts tab's "show me where this font is used".
        ///
        /// ⚠ Same colours and same rule as the component path (TranslatorScanner.HighlightComponent):
        /// matched by the font the element STARTED with, never by the one it wears now, or every
        /// element we already replaced would stop matching the font it is filed under.
        /// </summary>
        /// <returns>How many elements use this font; <paramref name="replaced"/> how many of them
        /// are wearing the replacement. Reported so the audit line does not say "0 component(s)"
        /// about a game where every piece of text matched — a count that is wrong in the reassuring
        /// direction is worse than no count.</returns>
        public static int HighlightFont(string fontName, Color highlight, Color dim, out int replaced)
        {
            int matched = 0;
            int wearing = 0;
            replaced = 0;

            if (!Available || _styleColorProp == null || _styleColorType == null) return 0;

            ClearHighlight();

            try
            {
                var documents = TypeHelper.FindAllObjectsOfType(UIDocumentType);
                if (documents == null) return 0;

                foreach (var document in documents)
                {
                    if (document == null) continue;

                    object root = null;
                    try { root = _rootProp.GetValue(document, null); }
                    catch { }
                    if (root == null) continue;

                    Walk(root, MaxElementsPerPass, element =>
                    {
                        string settingsName = SettingsFontNameOf(element);
                        bool matches = !string.IsNullOrEmpty(settingsName)
                                       && string.Equals(settingsName, fontName,
                                                        StringComparison.OrdinalIgnoreCase);

                        if (matches)
                        {
                            matched++;

                            // Wearing the replacement when what resolves is no longer what it
                            // started with — the same test the component audit makes.
                            if (!string.Equals(ResolvedFontNameOf(element), settingsName,
                                               StringComparison.OrdinalIgnoreCase))
                            {
                                wearing++;
                            }
                        }

                        RememberColour(element);
                        SetColour(element, matches ? highlight : dim);
                        _highlighted.Add(element);
                    });
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[UIToolkit] HighlightFont error: {ex.Message}");
            }

            replaced = wearing;
            return matched;
        }

        /// <summary>What the element resolves to right now — the replacement once one is applied.</summary>
        private static string ResolvedFontNameOf(object element)
        {
            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                if (resolved == null) return null;

                return ReadResolvedFont(resolved, out _)?.name;
            }
            catch { return null; }
        }

        public static void ClearHighlight()
        {
            if (_highlighted.Count == 0) return;

            foreach (var element in _highlighted)
            {
                try
                {
                    if (_highlightOriginalColor.TryGetValue(element, out var stored)
                        && stored is Color original)
                    {
                        SetColour(element, original);
                    }
                }
                catch { }
            }

            _highlighted.Clear();
        }

        /// <summary>The font this element is filed under: the one it had before any replacement.</summary>
        private static string SettingsFontNameOf(object element)
        {
            if (_originalFontName.TryGetValue(element, out var recorded)) return recorded;

            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                if (resolved == null) return null;

                return ReadResolvedFont(resolved, out _)?.name;
            }
            catch { return null; }
        }

        private static void RememberColour(object element)
        {
            if (_highlightOriginalColor.TryGetValue(element, out _)) return;

            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                if (resolved == null || _resolvedColorProp == null) return;

                if (_resolvedColorProp.GetValue(resolved, null) is Color current)
                    _highlightOriginalColor.Add(element, current);
            }
            catch { }
        }

        private static void SetColour(object element, Color colour)
        {
            try
            {
                var style = _styleProp.GetValue(element, null);
                if (style == null) return;

                var styleColour = Activator.CreateInstance(_styleColorType, colour);
                _styleColorProp.SetValue(style, styleColour, null);
            }
            catch { }
        }


        #endregion
    }
}
