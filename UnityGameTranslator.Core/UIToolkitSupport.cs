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

        private static PropertyInfo _parentProp;      // VisualElement.parent -> VisualElement
        private static PropertyInfo _nameProp;        // VisualElement.name   -> string

        private static MethodInfo _getClassesMethod;       // VisualElement.GetClasses() -> IEnumerable<string>

        private static PropertyInfo _worldBoundProp;       // VisualElement.worldBound -> Rect
        private static MethodInfo _screenToPanelMethod;    // RuntimePanelUtils.ScreenToPanel(IPanel, Vector2)
        private static MethodInfo _pickMethod;             // IPanel.Pick(Vector2) -> VisualElement

        private static PropertyInfo _panelProp;            // VisualElement.panel     -> IPanel
        private static PropertyInfo _focusControllerProp;  // IPanel.focusController
        private static PropertyInfo _focusedElementProp;   // FocusController.focusedElement

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

        // Pictures. Same shape as the font members: a value type built by a factory, wrapped in a
        // Style* struct, written to the inline style.
        private static Type _backgroundType;              // UnityEngine.UIElements.Background
        private static Type _styleBackgroundType;         // UnityEngine.UIElements.StyleBackground
        private static PropertyInfo _styleBackgroundProp; // IStyle.backgroundImage
        private static PropertyInfo _resolvedBackgroundProp; // IResolvedStyle.backgroundImage
        private static MethodInfo _backgroundFromSprite;  // Background.FromSprite(Sprite)
        private static MethodInfo _backgroundFromTexture; // Background.FromTexture2D(Texture2D)
        private static PropertyInfo _backgroundSpriteProp;  // Background.sprite
        private static PropertyInfo _backgroundTextureProp; // Background.texture

        /// <summary>True when a picture can be read AND written on this build.</summary>
        public static bool CanSetImage { get; private set; }

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

                // Walking UPWARDS, to tell a label from the editable part of a text field.
                _parentProp = VisualElementType.GetProperty("parent", pubInst);
                _nameProp = VisualElementType.GetProperty("name", pubInst);

                // Who has the keyboard. UI Toolkit keeps its own focus, which is why
                // EventSystem.currentSelectedGameObject — the uGUI answer — sees nothing here.
                // USS classes, for elements the game never named — most of them.
                _getClassesMethod = VisualElementType.GetMethod("GetClasses", pubInst, null, Type.EmptyTypes, null);

                // Picking under the cursor, and where the picked element sits on screen.
                _worldBoundProp = VisualElementType.GetProperty("worldBound", pubInst);
                _screenToPanelMethod = FindType("UnityEngine.UIElements.RuntimePanelUtils")
                    ?.GetMethod("ScreenToPanel", BindingFlags.Public | BindingFlags.Static);

                _panelProp = VisualElementType.GetProperty("panel", pubInst);
                if (_panelProp != null)
                {
                    _pickMethod = _panelProp.PropertyType.GetMethod(
                        "Pick", pubInst, null, new[] { typeof(Vector2) }, null);
                    _focusControllerProp = _panelProp.PropertyType.GetProperty("focusController", pubInst);
                    if (_focusControllerProp != null)
                        _focusedElementProp = _focusControllerProp.PropertyType
                            .GetProperty("focusedElement", pubInst);
                }

                Available = _textProp != null && _rootProp != null
                            && (_elementAtMethod != null || _hierElementAt != null);

                ResolveFontMembers(pubInst);
                ResolveImageMembers(pubInst);

                TranslatorCore.LogInfo(
                    $"[UIToolkit] Available={Available}, font replacement={CanSetFont}, "
                    + $"image replacement={CanSetImage}");
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

        #region Naming a target

        /// <summary>
        /// How far up a path is built. A bound rather than a full walk: a malformed or cyclic
        /// tree must not hang the game, and nobody writes a pattern on a thirty-deep ancestor.
        /// </summary>
        private const int MaxPathDepth = 32;

        /// <summary>
        /// A hierarchy path for an element, in the same shape and with the same contract as
        /// TranslatorCore.GetGameObjectPath: parents joined by "/", read left to right.
        ///
        /// 🔴 **This is the brick every "name a target" feature was missing.** Exclusions and font
        /// rules both work from a path, and a VisualElement had none — which is why a whole class
        /// of games could be translated but never tuned. The path is deliberately NOT an identity:
        /// two siblings can produce the same one, exactly as two GameObjects with the same name do.
        /// Identity is the element's id; this is for matching patterns.
        ///
        /// ⚠ **Most elements have no name**, only USS classes — a path of empty segments would be
        /// worth nothing. Hence three levels per segment: the name, then the first USS class, then
        /// the type. The result reads like `root/main-panel/unity-label`.
        /// </summary>
        public static string PathOf(object element)
        {
            if (element == null || _parentProp == null) return "";

            var parts = new List<string>();
            object current = element;

            for (int depth = 0; current != null && depth < MaxPathDepth; depth++)
            {
                parts.Insert(0, SegmentFor(current));
                try { current = _parentProp.GetValue(current, null); }
                catch { break; }
            }

            return string.Join("/", parts.ToArray());
        }

        /// <summary>One step of the path: what this element can be called.</summary>
        private static string SegmentFor(object element)
        {
            try
            {
                // The rule itself lives in TargetPath, pure and checked without a game — including
                // the interop prefix, which would otherwise make the same element name itself
                // differently on Mono and on IL2CPP.
                return TargetPath.Segment(_nameProp?.GetValue(element, null) as string,
                                          FirstClass(element),
                                          element.GetType().Name);
            }
            catch { return "?"; }
        }

        /// <summary>
        /// The element's first USS class, or null.
        ///
        /// ⚠ Optional by design: a build that does not expose GetClasses simply falls through to
        /// the type name. Losing readability in a path is a nuisance; refusing to build one at all
        /// would take exclusions away again.
        /// </summary>
        private static string FirstClass(object element)
        {
            if (_getClassesMethod == null) return null;

            try
            {
                if (!(_getClassesMethod.Invoke(element, null) is System.Collections.IEnumerable classes))
                    return null;

                foreach (var entry in classes)
                {
                    if (entry is string css && css.Length > 0) return css;
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Whether the player has excluded this element by pattern.
        ///
        /// ⚠ Asks the SAME decision uGUI asks — TranslatorCore holds the cache and the matching,
        /// this only supplies the path. A second set of exclusion rules would mean one written
        /// pattern meaning two things depending on the framework behind the label.
        ///
        /// ⚠ The path is passed as a factory: with no patterns configured, which is the common
        /// case, nothing is walked at all.
        /// </summary>
        private static bool IsExcluded(object element)
        {
            if (!TranslatorCore.HasExclusionPatterns) return false;

            long id = IdFor(element);
            if (TranslatorCore.TryCachedExclusion(id, out bool cached)) return cached;

            return TranslatorCore.RememberExclusion(id, PathOf(element));
        }

        #endregion

        /// <summary>
        /// The UI Toolkit name of the editable part of a text field. Unity puts it there itself
        /// (<c>TextInputBaseField&lt;T&gt;.textInputUssName</c>).
        /// </summary>
        private const string TextInputName = "unity-text-input";

        /// <summary>
        /// True when this element IS the editable part of a text field, or sits inside one.
        ///
        /// 🔴 **Without this, what the player types is translated.** Everything visible in UI
        /// Toolkit descends from TextElement — the box you type into included — so the single
        /// setter patch that covers the whole framework covers the input as well. A name or a seed
        /// being typed would be sent to the AI, paid for, written into translations.json, and could
        /// come back replaced on screen mid-word.
        ///
        /// uGUI has the same trap and answers it structurally — IsInputFieldTextComponentCached
        /// walks up to an InputField ancestor. This is that answer in the idiom UI Toolkit offers.
        ///
        /// ⚠ Matched on the element's NAME rather than on a type: Unity sets that name itself, it
        /// has been stable across UI Toolkit versions, and it costs no type resolution on a path
        /// that runs at every set_text. Bounded walk — a malformed tree must not turn this into a
        /// climb of the whole panel.
        /// </summary>
        private static bool IsInsideTextInput(object element)
        {
            if (_parentProp == null || _nameProp == null) return false;

            object current = element;
            for (int depth = 0; current != null && depth < 8; depth++)
            {
                try
                {
                    if ((_nameProp.GetValue(current, null) as string) == TextInputName) return true;
                    current = _parentProp.GetValue(current, null);
                }
                catch { return false; }
            }
            return false;
        }

        // What the focused field held, and when it last changed. Read once per frame: the setter
        // fires many times a frame and a property walk per call is not free.
        private static int _focusFrame = -1;
        private static string _focusedText;
        private static string _lastFocusedText;
        private static float _lastTypedChange = -999f;

        /// <summary>
        /// What is being typed right now in this panel, or null.
        ///
        /// ⚠ UI Toolkit keeps its own focus, so the uGUI answer — EventSystem's selected
        /// GameObject — sees nothing here. The element is in hand, so its panel is one property
        /// away and no scene search is needed.
        /// </summary>
        private static string FocusedText(object element)
        {
            if (_focusedElementProp == null) return null;

            int frame = Time.frameCount;
            if (_focusFrame == frame) return _focusedText;
            _focusFrame = frame;
            _focusedText = null;

            try
            {
                object panel = _panelProp.GetValue(element, null);
                object controller = panel == null ? null : _focusControllerProp.GetValue(panel, null);
                object focused = controller == null ? null : _focusedElementProp.GetValue(controller, null);
                if (focused != null) _focusedText = ReadAnyText(focused);
            }
            catch { return null; }

            if (!string.IsNullOrEmpty(_focusedText))
            {
                if (_focusedText != _lastFocusedText) _lastTypedChange = Time.realtimeSinceStartup;
                _lastFocusedText = _focusedText;
            }

            return _focusedText;
        }

        /// <summary>
        /// The text of whatever holds the keyboard: `text` on a TextElement, `value` on a field.
        /// Which one it is depends on the widget, so both are tried rather than guessed.
        /// </summary>
        private static string ReadAnyText(object element)
        {
            try
            {
                var type = element.GetType();
                var text = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (text != null && text.PropertyType == typeof(string))
                    return text.GetValue(element, null) as string;

                var val = type.GetProperty("value", BindingFlags.Public | BindingFlags.Instance);
                if (val != null && val.PropertyType == typeof(string))
                    return val.GetValue(element, null) as string;
            }
            catch { }
            return null;
        }

        /// <summary>
        /// True when this element is showing, somewhere else on screen, what the player is typing.
        ///
        /// The same rule as uGUI's external mirror, with the same two guards — a string this game
        /// has already shown us is content, and only a recent keystroke opens the window. See
        /// TranslatorPatches.CouldBeTypedText for why both are needed and what still slips through.
        /// </summary>
        private static bool IsEchoOfTyping(object element, string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            string focused = FocusedText(element);
            if (string.IsNullOrEmpty(focused)) return false;

            string candidate = TranslatorCore.StripMarkupTags(text).Trim();
            if (candidate.Length == 0) return false;
            if (!string.Equals(candidate, focused.Trim(), StringComparison.Ordinal)) return false;

            return TranslatorPatches.CouldBeTypedText(candidate, _lastTypedChange);
        }

        public static void TextElement_SetText_Prefix(object __instance, ref string value)
        {
            if (_writingBack) return;
            if (string.IsNullOrEmpty(value)) return;
            if (!TranslatorCore.TranslationsActive) return;

            // Unity APIs below are main-thread only; on IL2CPP the wrong thread crashes natively
            // rather than throwing, which is not a failure anyone can diagnose from a log.
            if (!TranslatorCore.IsMainThread) return;

            // Never the player's own typing — the box itself, or a live echo of it elsewhere.
            if (IsInsideTextInput(__instance)) return;
            if (IsExcluded(__instance)) return;
            if (IsEchoOfTyping(__instance, value)) return;

            try
            {
                // The same routing every other text framework goes through: procedural text,
                // reveals, read-back, the already-written check. It used to call the translator
                // directly and inherited none of it — the asymmetry the routing split removed
                // for NGUI and tk2d, closed here too.
                string before = value;
                TranslatorPatches.RouteText(__instance, __instance, IdFor(__instance),
                                            isOwnUI: false, componentType: "UIToolkit", textValue: ref value);
                // Stage D — a TextElement has no isRightToLeftText, so this yields the
                // visual-order form (single-line correct; multi-line is the emission lot).
                // Catch-up (user-required): font override rules match this framework too now —
                // their RTL alignment applies; fonts and sizes stay with this file's own
                // mechanisms.
                _originalFontName.TryGetValue(__instance, out string uitkFont);
                FontOverrideRule uitkOverride = null;
                if (TranslatorCore.FontOverrides.Count > 0)
                    uitkOverride = TranslatorCore.FindFontOverride(IdFor(__instance), PathOf(__instance), uitkFont, value);
                TextShaping.RtlPresenter.Present(__instance, IdFor(__instance), ref value, uitkFont, uitkOverride);
                if (!string.Equals(before, value, StringComparison.Ordinal))
                    RememberOriginal(__instance, before);
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

        /// <summary>
        /// What the GAME had in an element before we wrote over it.
        ///
        /// 🔴 Without this, switching translation off left this framework translated. Restoring
        /// works from TranslatorScanner's per-component originals, which are keyed by instance id —
        /// a VisualElement has none, so it was stored nowhere and put back nowhere. Weak for the
        /// same reason as everything else here: elements are recycled by the hundred.
        /// </summary>
        private static readonly ConditionalWeakTable<object, string> _originalText =
            new ConditionalWeakTable<object, string>();

        /// <summary>Remember what was there, the first time we replace it.</summary>
        private static void RememberOriginal(object element, string original)
        {
            if (element == null || string.IsNullOrEmpty(original)) return;
            if (_originalText.TryGetValue(element, out _)) return;
            _originalText.Add(element, original);
        }

        #region Identity

        private sealed class IdBox { public long Value; }

        /// <summary>
        /// A stable number for an element, so it can be followed by the same routing every other
        /// text framework uses.
        ///
        /// 🔴 **Weak, and that is the whole design.** The routing state lives in a strong
        /// dictionary keyed by this number. UI Toolkit recycles elements by the hundred — list
        /// virtualisation — so anything holding them strongly keeps every element ever scrolled
        /// past alive for the life of the process. The number is attached to the element here and
        /// dies with it; <see cref="Sweep"/> then drops the state it pointed at.
        /// </summary>
        private static readonly ConditionalWeakTable<object, IdBox> _ids =
            new ConditionalWeakTable<object, IdBox>();

        /// <summary>The other direction, weakly, so an id can be resolved and swept.</summary>
        private static readonly Dictionary<long, WeakReference> _byId = new Dictionary<long, WeakReference>();

        /// <summary>
        /// 🔴 **Beyond every int, so a collision with a Unity instance id is impossible rather
        /// than unlikely.** Unity hands out instance ids of either sign across the whole int range,
        /// so no int window is free to claim — which is why the routing key is a long. The
        /// widening from int is implicit, so every existing caller passing an instance id compiles
        /// and behaves exactly as before.
        /// </summary>
        private static long _nextId = 1L << 32;

        /// <summary>The element's number, assigned on first sight.</summary>
        public static long IdFor(object element)
        {
            if (element == null) return 0;

            if (_ids.TryGetValue(element, out var box)) return box.Value;

            box = new IdBox { Value = _nextId++ };
            _ids.Add(element, box);
            _byId[box.Value] = new WeakReference(element);
            return box.Value;
        }

        /// <summary>The element behind a number, or null once it has been collected.</summary>
        public static object ElementFor(long id)
        {
            if (!_byId.TryGetValue(id, out var weak)) return null;

            var target = weak.Target;
            if (target == null) Forget(id);
            return target;
        }

        private static void Forget(long id)
        {
            _byId.Remove(id);
            TranslatorPatches.ForgetElementState(id);

            // ⚠ The exclusion and font-rule caches too: both are strong and keyed by id, so an
            // element recycled by a list would leave an entry behind on every scroll.
            TranslatorCore.ForgetTargetCaches(id);
        }

        /// <summary>
        /// Drop the ids whose element is gone, and the routing state behind them.
        ///
        /// ⚠ Called from the scan rather than on a timer: it is the same pass that walks the
        /// documents, so it costs nothing extra to know that a scroll has happened.
        /// </summary>
        private static void Sweep()
        {
            if (_byId.Count == 0) return;

            List<long> dead = null;
            foreach (var pair in _byId)
            {
                if (pair.Value.Target != null) continue;
                (dead ?? (dead = new List<long>())).Add(pair.Key);
            }

            if (dead == null) return;
            foreach (long id in dead) Forget(id);
        }

        #region Pictures

        /// <summary>
        /// What is needed to read and replace a picture.
        ///
        /// ⚠ Its own step and its own flag, exactly like the fonts: a build that does not expose
        /// these must still have its text translated. Losing image replacement is a degraded
        /// result; refusing to load is no result.
        /// </summary>
        private static void ResolveImageMembers(BindingFlags pubInst)
        {
            try
            {
                _backgroundType = FindType("UnityEngine.UIElements.Background");
                _styleBackgroundType = FindType("UnityEngine.UIElements.StyleBackground");
                if (_backgroundType == null || _styleBackgroundType == null) return;

                _styleBackgroundProp = _styleProp?.PropertyType.GetProperty("backgroundImage", pubInst);
                _resolvedBackgroundProp = _resolvedStyleProp?.PropertyType
                    .GetProperty("backgroundImage", pubInst);

                _backgroundSpriteProp = _backgroundType.GetProperty("sprite", pubInst);
                _backgroundTextureProp = _backgroundType.GetProperty("texture", pubInst);

                var statics = BindingFlags.Public | BindingFlags.Static;
                _backgroundFromSprite = _backgroundType.GetMethod("FromSprite", statics);
                _backgroundFromTexture = _backgroundType.GetMethod("FromTexture2D", statics);

                CanSetImage = _styleBackgroundProp != null
                              && _resolvedBackgroundProp != null
                              && (_backgroundFromSprite != null || _backgroundFromTexture != null);
            }
            catch { CanSetImage = false; }
        }

        /// <summary>What the game had as this element's picture, so it can be put back.</summary>
        private static readonly ConditionalWeakTable<object, object> _originalBackground =
            new ConditionalWeakTable<object, object>();

        /// <summary>
        /// Swap an element's picture for the one the player provided, by NAME.
        ///
        /// 🔴 UI Toolkit holds its pictures in a style, not in a component — which is why
        /// ImageReplacer, built on Image/RawImage/SpriteRenderer setters, could never reach them.
        /// The name is the contract in both cases, so the same PNG a player dropped in for a uGUI
        /// game works here without them having to know what drew it.
        ///
        /// ⚠ Reads the RESOLVED style and writes the INLINE one, exactly like the font path: the
        /// resolved value is what USS actually produced, and the inline value is the only one we
        /// may own.
        /// </summary>
        private static void HandleImage(object element)
        {
            if (!CanSetImage || !TranslatorCore.ImageReplacementActive) return;

            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                if (resolved == null) return;

                object current = _resolvedBackgroundProp.GetValue(resolved, null);
                string name = NameOfBackground(current);
                if (string.IsNullOrEmpty(name)) return;

                if (!_originalBackground.TryGetValue(element, out _))
                    _originalBackground.Add(element, current);

                var replacement = ImageReplacer.GetReplacement(name);
                if (replacement == null) return;

                // Already wearing it: writing every pass would be a style assignment per element
                // per scan, for nothing.
                if (string.Equals(NameOfBackground(current), replacement.name, StringComparison.Ordinal))
                    return;

                WriteBackground(element, BuildBackground(replacement));
            }
            catch { }
        }

        /// <summary>The name of whatever a background is made of, or null.</summary>
        private static string NameOfBackground(object background)
        {
            if (background == null) return null;

            try
            {
                if (_backgroundSpriteProp?.GetValue(background, null) is UnityEngine.Object sprite
                    && sprite != null)
                    return sprite.name;

                if (_backgroundTextureProp?.GetValue(background, null) is UnityEngine.Object texture
                    && texture != null)
                    return texture.name;
            }
            catch { }

            return null;
        }

        /// <summary>
        /// A Background carrying this sprite.
        ///
        /// ⚠ FromSprite when the build has it, its texture otherwise — a sprite carries slicing
        /// and a pivot that a bare texture loses, so the poorer road is the fallback and not the
        /// first choice.
        /// </summary>
        private static object BuildBackground(Sprite sprite)
        {
            try
            {
                if (_backgroundFromSprite != null)
                    return _backgroundFromSprite.Invoke(null, new object[] { sprite });

                if (_backgroundFromTexture != null && sprite.texture != null)
                    return _backgroundFromTexture.Invoke(null, new object[] { sprite.texture });
            }
            catch { }

            return null;
        }

        private static void WriteBackground(object element, object background)
        {
            if (background == null) return;

            try
            {
                var styleValue = Activator.CreateInstance(_styleBackgroundType, background);
                var style = _styleProp.GetValue(element, null);
                if (style != null) _styleBackgroundProp.SetValue(style, styleValue, null);
            }
            catch { }
        }

        /// <summary>
        /// The sprite an element currently shows, or null when it shows a bare texture or nothing.
        ///
        /// For the inspector, which names and exports a picture through ImageReplacer's own
        /// helpers — they take the sprite object, so this hands over the same thing a uGUI
        /// component would have.
        /// </summary>
        public static Sprite SpriteOf(object element)
        {
            if (!CanSetImage || element == null) return null;

            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                if (resolved == null) return null;

                object background = _resolvedBackgroundProp.GetValue(resolved, null);
                return _backgroundSpriteProp?.GetValue(background, null) as Sprite;
            }
            catch { return null; }
        }

        /// <summary>Give an element its own picture back, if we ever replaced it.</summary>
        private static void RestoreImageOf(object element)
        {
            if (!CanSetImage) return;
            if (!_originalBackground.TryGetValue(element, out var original) || original == null) return;

            WriteBackground(element, original);
            _originalBackground.Remove(element);
        }

        #endregion

        #region Picking

        /// <summary>
        /// The text element under a screen point, or null.
        ///
        /// 🔴 UI Toolkit does its own hit testing. The inspector asks a GraphicRaycaster, which is
        /// the uGUI mechanism and returns nothing here — so on a game whose interface is entirely
        /// UI Toolkit, clicking anywhere found nothing at all and the inspector could not be used.
        ///
        /// ⚠ Walks UP from whatever was hit: the point may land on a container, while the thing
        /// worth naming is the label inside it. Stops at the first element that carries text.
        /// </summary>
        public static object PickAt(Vector2 screenPoint, out Rect screenRect)
        {
            screenRect = default(Rect);
            if (!Available || _pickMethod == null || _textProp == null) return null;

            try
            {
                var documents = TypeHelper.FindAllObjectsOfType(UIDocumentType);
                if (documents == null) return null;

                foreach (var document in documents)
                {
                    if (document == null) continue;

                    object root = null;
                    try { root = _rootProp.GetValue(document, null); } catch { }
                    if (root == null) continue;

                    object panel = null;
                    try { panel = _panelProp.GetValue(root, null); } catch { }
                    if (panel == null) continue;

                    var mapping = PanelMapping.For(panel, _screenToPanelMethod);
                    object hit = null;
                    try { hit = _pickMethod.Invoke(panel, new object[] { mapping.ToPanel(screenPoint) }); }
                    catch { }

                    for (int depth = 0; hit != null && depth < MaxPathDepth; depth++)
                    {
                        if (HasText(hit) && !IsInsideTextInput(hit))
                        {
                            screenRect = mapping.ToScreen(WorldBoundOf(hit));
                            return hit;
                        }

                        try { hit = _parentProp.GetValue(hit, null); } catch { break; }
                    }
                }
            }
            catch { }

            return null;
        }

        private static bool HasText(object element)
        {
            try { return !string.IsNullOrEmpty(_textProp.GetValue(element, null) as string); }
            catch { return false; }
        }

        private static Rect WorldBoundOf(object element)
        {
            try
            {
                if (_worldBoundProp?.GetValue(element, null) is Rect rect) return rect;
            }
            catch { }
            return default(Rect);
        }

        /// <summary>
        /// How this panel's coordinates relate to the screen's, in both directions.
        ///
        /// ⚠ **Derived from two measurements rather than assumed.** A panel can be scaled and
        /// letterboxed by its PanelSettings, so "flip Y and you are done" is right only for the
        /// default setup. Converting two known screen corners and reading the factors back gives
        /// the real mapping, whatever the scale mode — and the inverse comes for free, which is
        /// what the highlight needs and what UI Toolkit offers no helper for.
        ///
        /// ⚠ Falls back to the plain Y flip when RuntimePanelUtils is absent: a highlight in the
        /// wrong place is a nuisance, no picking at all is a feature nobody can use.
        /// </summary>
        private struct PanelMapping
        {
            private Vector2 _origin;
            private Vector2 _scale;

            public static PanelMapping For(object panel, MethodInfo screenToPanel)
            {
                var mapping = new PanelMapping { _origin = Vector2.zero, _scale = Vector2.one };

                if (screenToPanel == null)
                {
                    // The plain flip: panel coordinates start at the top, screen ones at the bottom.
                    mapping._origin = new Vector2(0f, Screen.height);
                    mapping._scale = new Vector2(1f, -1f);
                    return mapping;
                }

                try
                {
                    var a = (Vector2)screenToPanel.Invoke(null, new object[] { panel, Vector2.zero });
                    var b = (Vector2)screenToPanel.Invoke(null,
                        new object[] { panel, new Vector2(Screen.width, Screen.height) });

                    float sx = Screen.width != 0 ? (b.x - a.x) / Screen.width : 1f;
                    float sy = Screen.height != 0 ? (b.y - a.y) / Screen.height : 1f;

                    if (Mathf.Abs(sx) > 0.0001f && Mathf.Abs(sy) > 0.0001f)
                    {
                        mapping._origin = a;
                        mapping._scale = new Vector2(sx, sy);
                    }
                }
                catch { }

                return mapping;
            }

            public Vector2 ToPanel(Vector2 screen) =>
                new Vector2(_origin.x + screen.x * _scale.x, _origin.y + screen.y * _scale.y);

            public Rect ToScreen(Rect panelRect)
            {
                float x0 = (panelRect.xMin - _origin.x) / _scale.x;
                float x1 = (panelRect.xMax - _origin.x) / _scale.x;
                float y0 = (panelRect.yMin - _origin.y) / _scale.y;
                float y1 = (panelRect.yMax - _origin.y) / _scale.y;

                return Rect.MinMaxRect(Mathf.Min(x0, x1), Mathf.Min(y0, y1),
                                       Mathf.Max(x0, x1), Mathf.Max(y0, y1));
            }
        }

        #endregion

        /// <summary>
        /// Every element carrying text, for the screens that list what is on screen.
        ///
        /// ⚠ Walks the documents, like the scan does — an element is not in Unity's object graph,
        /// so there is no FindAllObjectsOfType that could return one. Bounded by the same ceiling
        /// as the scan, for the same reason.
        /// </summary>
        public static List<TextTarget> Targets(Func<string, bool> keep)
        {
            var found = new List<TextTarget>();
            if (!Available || _textProp == null) return found;

            try
            {
                var documents = TypeHelper.FindAllObjectsOfType(UIDocumentType);
                if (documents == null) return found;

                int visited = 0;
                foreach (var document in documents)
                {
                    if (document == null) continue;
                    object root = null;
                    try { root = _rootProp.GetValue(document, null); } catch { }
                    if (root != null) CollectFrom(root, found, keep, ref visited);
                    if (visited >= MaxElementsPerPass) break;
                }
            }
            catch { }

            return found;
        }

        private static void CollectFrom(object element, List<TextTarget> found,
                                        Func<string, bool> keep, ref int visited)
        {
            if (element == null || visited >= MaxElementsPerPass) return;
            visited++;

            try
            {
                if (!IsInsideTextInput(element))
                {
                    string text = _textProp.GetValue(element, null) as string;
                    if (!string.IsNullOrEmpty(text) && (keep == null || keep(text)))
                    {
                        found.Add(new TextTarget
                        {
                            Owner = element,
                            Id = IdFor(element),
                            Engine = "UI Toolkit",
                            Path = PathOf(element),
                            Text = text,
                        });
                    }
                }
            }
            catch { }

            int count = ChildCount(element);
            for (int i = 0; i < count; i++)
            {
                var child = ChildAt(element, i);
                if (child != null) CollectFrom(child, found, keep, ref visited);
            }
        }

        /// <summary>
        /// Put every element back the way the game had it: its own text, and its own font.
        ///
        /// 🔴 Called when translation is switched off. Every other framework is put back by
        /// RestoreAllOriginals, which walks the registered component types and the patched
        /// component refs — a VisualElement is in neither, so this framework stayed translated
        /// while every other one reverted.
        ///
        /// ⚠ Walks the weak id map rather than the documents: an element that has scrolled out of
        /// the tree still shows our text if it comes back, and the ones already collected simply
        /// are not there. It also means this costs nothing on a game with no UI Toolkit.
        /// </summary>
        public static void RestoreAll()
        {
            if (!Available || _textProp == null) return;

            int restored = 0;
            foreach (var pair in new List<KeyValuePair<long, WeakReference>>(_byId))
            {
                var element = pair.Value.Target;
                if (element == null) continue;

                try
                {
                    if (_originalText.TryGetValue(element, out var original)
                        && !string.IsNullOrEmpty(original))
                    {
                        _writingBack = true;
                        try { _textProp.SetValue(element, original, null); }
                        finally { _writingBack = false; }

                        _originalText.Remove(element);
                        _written.Remove(element);
                        restored++;
                    }

                    RestoreFontOf(element);
                    RestoreImageOf(element);
                }
                catch { }
            }

            if (restored > 0)
                TranslatorCore.LogInfo($"[UIToolkit] Restored {restored} element(s) to the game's own text");
        }

        /// <summary>
        /// Give an element its own font back, if we ever replaced it.
        ///
        /// ⚠ Reads the font the element currently resolves to, because RestoreOriginalFont
        /// compares against it to know whether there is anything to undo — the same shape
        /// HandleFont uses when replacement is switched off mid-session.
        /// </summary>
        private static void RestoreFontOf(object element)
        {
            if (!CanSetFont) return;
            if (!_originalFontName.TryGetValue(element, out var settingsName)) return;

            var resolved = _resolvedStyleProp?.GetValue(element, null);
            if (resolved == null) return;

            var currentFont = ReadResolvedFont(resolved, out _);
            if (currentFont == null || string.IsNullOrEmpty(currentFont.name)) return;

            RestoreOriginalFont(element, settingsName, currentFont);
        }

        /// <summary>
        /// Put a text into an element from outside, through the SAME pipeline the setter patch
        /// gives every other write: routing (a stabilized original picks up its cached
        /// translation), then stage D (an RTL text reaches the screen shaped, never logical).
        ///
        /// 🔴 This is the door the editor and the typewriting finalizer must use. They used
        /// WriteBack directly, which skips the patch by design (anti-re-translation guard) — and
        /// skipped stage D with it: a UI Toolkit element received LOGICAL Arabic under a &lt;u&gt;
        /// tag and Unity 6's DrawUnderlineMesh died on it, taking the whole game down with its
        /// own crash handler (Timberborn, §7.8 of the RTL analysis). The uGUI branch of
        /// TextTargets.Write always had this pipeline for free, because TypeHelper.SetText goes
        /// through the patched setter — this restores the symmetry.
        /// </summary>
        public static void WriteRouted(object element, string text)
        {
            if (_textProp == null || element == null || text == null) return;

            string value = text;
            try
            {
                TranslatorPatches.RouteText(element, element, IdFor(element),
                                            isOwnUI: false, componentType: "UIToolkit", textValue: ref value);
                _originalFontName.TryGetValue(element, out string font);
                FontOverrideRule rule = null;
                if (TranslatorCore.FontOverrides.Count > 0)
                    rule = TranslatorCore.FindFontOverride(IdFor(element), PathOf(element), font, value);
                TextShaping.RtlPresenter.Present(element, IdFor(element), ref value, font, rule);
            }
            catch { }
            WriteBack(element, value);
        }

        /// <summary>
        /// The RAW write — no routing, no stage D. Only for text that already went through the
        /// pipeline (WriteRouted above, the reflow's SetElementTextSilently) or that restores the
        /// game's own original.
        ///
        /// ⚠ Through the same write-back guard the scan uses, or the setter patch would read our
        /// own write as the game's and translate the translation.
        /// </summary>
        public static void WriteBack(object element, string text)
        {
            if (_textProp == null || element == null || text == null) return;

            try
            {
                _writingBack = true;
                try { _textProp.SetValue(element, text, null); }
                finally { _writingBack = false; }

                _written.Remove(element);
                _written.Add(element, text);
            }
            catch { }
        }

        #endregion

        private static float _lastScanTime;

        /// <summary>Which document the next pass starts from. See the note in Scan.</summary>
        private static int _resumeAt;

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
                // Elements recycled since the last pass: drop their ids and the routing state
                // behind them. Here because this is the pass that knows a scroll has happened.
                Sweep();

                var documents = TypeHelper.FindAllObjectsOfType(UIDocumentType);
                if (documents == null || documents.Length == 0) return;

                int visited = 0;

                // 🔴 **Resumed, not restarted.** The budget is shared across documents and the walk
                // used to begin at the first one every pass — so whatever sat past the ceiling was
                // never reached, always the same things, while the early documents were re-walked
                // for nothing. On screen that is "some text translated and some not", and "the font
                // finally applies to the rest" when something else happens to shift the order.
                //
                // Starting where the last pass stopped gives every document its turn. The list can
                // change between passes, so this is a position rather than a promise — but a
                // rotating position is what stops a tail from starving.
                if (_resumeAt >= documents.Length) _resumeAt = 0;
                int startedAt = _resumeAt;

                for (int step = 0; step < documents.Length; step++)
                {
                    int index = (startedAt + step) % documents.Length;
                    var document = documents[index];

                    if (document == null) continue;

                    object root = null;
                    try { root = _rootProp.GetValue(document, null); }
                    catch { }

                    if (root == null) continue;

                    visited += Walk(root, MaxElementsPerPass - visited, ProcessElement);

                    if (visited >= MaxElementsPerPass)
                    {
                        // Next pass takes over from the document after this one.
                        _resumeAt = (index + 1) % documents.Length;
                        break;
                    }

                    // Everything fitted: start again from the top next time.
                    _resumeAt = 0;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[UIToolkit] Scan error: {ex.Message}");
            }
        }

        #region RTL / ATG probe (TEMPORARY — feature/text-shaping bench, see TextShaping/RtlProbe.cs)

        private static int _probeStep = -1;
        private static object _probeEl;
        private static string _probeOriginal;

        /// <summary>
        /// From RtlProbe — cycles the TextElement the tester POINTED AT (passed on acquisition,
        /// null on later presses) through: raw Arabic on the current generator, then the SAME
        /// logical text after switching the element's style to the Advanced Text Generator
        /// (Unity 6 — HarfBuzz shapes and orders by itself), then a long paragraph for wrapping,
        /// then restore. If ATG renders the logical text correctly, this whole engine needs NO
        /// shaping from us — the answer §7 of the analyse doc waits on.
        /// Returns true while the cycle is still holding an element.
        /// </summary>
        internal static bool ProbeAtgCycle(object element, string shortLogical, string longLogical)
        {
            if (TextElementType == null || _textProp == null)
            {
                TranslatorCore.LogWarning("[RtlProbe] UI Toolkit not present or not resolved in this game.");
                return false;
            }
            try
            {
                if (_probeEl == null)
                {
                    _probeStep = -1;
                    if (element == null) return false;
                    _probeEl = element;
                    _probeOriginal = _textProp.GetValue(_probeEl, null) as string;
                    TranslatorCore.LogInfo($"[RtlProbe] UITK target acquired under cursor, path='{PathOf(_probeEl)}' original='{(_probeOriginal != null && _probeOriginal.Length > 40 ? _probeOriginal.Substring(0, 40) + "..." : _probeOriginal)}'");
                }

                _probeStep++;
                switch (_probeStep)
                {
                    case 0:
                        ProbeSetText(_probeEl, shortLogical);
                        TranslatorCore.LogInfo("[RtlProbe] UITK 1/4 raw logical Arabic, current generator — expected broken (isolated, LTR) unless the game already runs ATG.");
                        return true;
                    case 1:
                        {
                            string detail;
                            bool ok = ProbeTrySetAdvancedGenerator(_probeEl, out detail);
                            ProbeSetText(_probeEl, shortLogical);
                            TranslatorCore.LogInfo($"[RtlProbe] UITK 2/4 same logical text, generator→Advanced: {(ok ? "SET" : "FAILED")} ({detail}) — if rendered joined+RTL, ATG does the whole job on this engine.");
                        }
                        return true;
                    case 2:
                        ProbeSetText(_probeEl, longLogical);
                        TranslatorCore.LogInfo("[RtlProbe] UITK 3/4 long logical paragraph on Advanced — wrap and line order are ATG's to prove.");
                        return true;
                    default:
                        ProbeSetText(_probeEl, _probeOriginal ?? "");
                        TranslatorCore.LogInfo("[RtlProbe] UITK 4/4 restored text. ⚠ Generator left on Advanced for this element until scene reload. Point at another text and press again to probe it.");
                        _probeEl = null;
                        _probeOriginal = null;
                        _probeStep = -1;
                        return false;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[RtlProbe] UITK: {ex.Message}\n{ex.StackTrace}");
                _probeEl = null;
                _probeOriginal = null;
                _probeStep = -1;
                return false;
            }
        }

        private static void ProbeSetText(object element, string text)
        {
            _writingBack = true;
            try { _textProp.SetValue(element, text, null); }
            finally { _writingBack = false; }
        }

        /// <summary>
        /// style.unityTextGenerator = TextGeneratorType.Advanced, all by reflection: the property
        /// only exists on Unity 6, and Core compiles against far older assemblies.
        ///
        /// ⚠ The enum type is DERIVED from the property's own StyleEnum&lt;T&gt; generic argument,
        /// never looked up by name: the first attempt did FindType("UnityEngine.UIElements.
        /// TextGeneratorType") and reported "no ATG" on a Unity 6000.3 game of the bench — the
        /// enum lives elsewhere (namespace varies), but the property always knows its own T.
        /// </summary>
        private static bool ProbeTrySetAdvancedGenerator(object element, out string detail)
        {
            try
            {
                var style = _styleProp?.GetValue(element, null);
                if (style == null) { detail = "style unreadable"; return false; }

                var prop = style.GetType().GetProperty("unityTextGenerator", BindingFlags.Public | BindingFlags.Instance)
                           ?? _styleProp.PropertyType.GetProperty("unityTextGenerator", BindingFlags.Public | BindingFlags.Instance);
                if (prop?.SetMethod == null) { detail = "unityTextGenerator property absent — no ATG on this Unity"; return false; }

                // StyleEnum<TextGeneratorType> → TextGeneratorType, wherever it lives.
                Type enumType = null;
                var pt = prop.PropertyType;
                if (pt.IsEnum) enumType = pt;
                else if (pt.IsGenericType && pt.GetGenericArguments().Length == 1)
                    enumType = pt.GetGenericArguments()[0];
                if (enumType == null || !enumType.IsEnum)
                { detail = $"cannot derive enum from {pt.Name}"; return false; }

                var advanced = Enum.Parse(enumType, "Advanced");
                object value = pt.IsEnum ? advanced : Activator.CreateInstance(pt, advanced);
                prop.SetValue(style, value, null);
                detail = $"ok ({enumType.FullName})";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        #endregion

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

                ReportProxyIdentityOnce(element);

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
            // Pictures are not text and do not depend on the font gate: an element can carry a
            // picture and no text at all, which is most of them.
            HandleImage(element);

            // Also the switch for "translate this font or not", so it is asked every pass.
            if (!HandleFont(element)) return;

            TranslateElement(element);
        }

        private static void TranslateElement(object element)
        {
            try
            {
                // The scan reaches the editable part of a text field like anything else — the
                // setter is not the only way in. See IsInsideTextInput.
                if (IsInsideTextInput(element)) return;
                if (IsExcluded(element)) return;

                var current = _textProp.GetValue(element, null) as string;
                if (string.IsNullOrEmpty(current)) return;

                // Ours already. Reading it back and asking for a translation would be asking to
                // translate the target language into itself.
                if (_written.TryGetValue(element, out var mine) && mine == current) return;

                if (IsEchoOfTyping(element, current)) return;

                string translated = current;
                TranslatorPatches.RouteText(element, element, IdFor(element),
                                            isOwnUI: false, componentType: "UIToolkit", textValue: ref translated);
                // Stage D, same as the setter path — the scan is the other way text reaches a
                // UI Toolkit screen. Same catch-up: override rules match here too.
                _originalFontName.TryGetValue(element, out string scanFont);
                FontOverrideRule scanOverride = null;
                if (TranslatorCore.FontOverrides.Count > 0)
                    scanOverride = TranslatorCore.FindFontOverride(IdFor(element), PathOf(element), scanFont, translated);
                TextShaping.RtlPresenter.Present(element, IdFor(element), ref translated, scanFont, scanOverride);
                if (string.IsNullOrEmpty(translated) || translated == current) return;

                RememberOriginal(element, current);

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
        private static void ReportProxyIdentityOnce(object element)
        {
            if (_identityReported) return;

            try
            {
                // ⚠ Asked of the first element that HAS a child, wherever it turns up — not of the
                // first document. A first version probed the first document only, that one had no
                // children, and the probe returned in silence on every pass: the very question it
                // was added to answer stayed unanswered while the log looked healthy.
                if (ChildCount(element) < 1) return;

                _identityReported = true;

                var first = ChildAt(element, 0);
                var again = ChildAt(element, 0);

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

                // Font rules by pattern, the same ones the component path honours. Guarded on the
                // rule count like the other caller: building a path costs a walk, and the common
                // case is that nobody has written a rule.
                string replacementName = settingsName;
                if (TranslatorCore.FontOverrides.Count > 0)
                {
                    var rule = TranslatorCore.FindFontOverride(IdFor(element), PathOf(element),
                                                               settingsName, _textProp.GetValue(element, null) as string);
                    if (rule != null && !string.IsNullOrEmpty(rule.replacement))
                        replacementName = rule.replacement;
                }

                ApplyReplacement(element, replacementName, currentFont, isSdf);
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

        #region RTL emission (stage D) — the line source and style adjustments RtlPresenter calls

        // The standard UI Toolkit generator exposes NO line data (unlike UI.Text's
        // cachedTextGenerator) — but it measures on demand: MeasureTextSize is the engine's own
        // ruler, so re-deriving the break points word by word is still the ENGINE deciding where
        // text fits, not a home-grown metric. Resolved lazily; every member may be null on an
        // exotic runtime and every caller must survive that.
        private static bool _rtlPlumbingResolved;
        private static MethodInfo _measureTextSize;          // TextElement.MeasureTextSize(string, float, MeasureMode, float, MeasureMode)
        private static object _measureUndefined;             // MeasureMode.Undefined, boxed once
        private static PropertyInfo _contentRectProp;        // VisualElement.contentRect -> Rect
        private static PropertyInfo _styleWhiteSpaceProp;    // IStyle.whiteSpace        (inline)
        private static PropertyInfo _resolvedWhiteSpaceProp; // resolvedStyle.whiteSpace (computed)
        private static PropertyInfo _styleTextAlignProp;     // IStyle.unityTextAlign    (inline)
        private static PropertyInfo _resolvedTextAlignProp;  // resolvedStyle.unityTextAlign
        private static PropertyInfo _resolvedTextGenProp;    // resolvedStyle.unityTextGenerator (Unity 6+, else null)

        // The INLINE style values an element wore before our adjustments — restored verbatim
        // when its text goes back to LTR, so an element that never had an inline value gets its
        // "unset" keyword back, not a frozen copy of what the stylesheet computed that day.
        // [0] = inline unityTextAlign, [1] = the RESOLVED original the mirror is computed from.
        private static readonly ConditionalWeakTable<object, object[]> _rtlAlignOriginal =
            new ConditionalWeakTable<object, object[]>();
        // [0] = inline whiteSpace.
        private static readonly ConditionalWeakTable<object, object[]> _rtlWrapOriginal =
            new ConditionalWeakTable<object, object[]>();

        private static void EnsureRtlPlumbing()
        {
            if (_rtlPlumbingResolved || !Available) return;
            _rtlPlumbingResolved = true;
            var pubInst = BindingFlags.Public | BindingFlags.Instance;

            // 🔴 Never GetMethod(name, flags) here, and one try PER member: Unity 6 ships TWO
            // public MeasureTextSize overloads, so the single-name lookup throws
            // AmbiguousMatchException — and behind a shared try block that one throw read as
            // "no measure API, no styles, no ATG detection" on a runtime that has all of them,
            // with a log line blaming the runtime (Timberborn crash analysis, §7.8).
            try
            {
                foreach (var m in TextElementType.GetMethods(pubInst))
                {
                    if (m.Name != "MeasureTextSize") continue;
                    var ps = m.GetParameters();
                    if (ps.Length != 5 || ps[0].ParameterType != typeof(string) || !ps[2].ParameterType.IsEnum)
                        continue;
                    // The MeasureMode enum is NESTED in VisualElement and its namespace moved
                    // across versions — the parameter always knows its own type (the same lesson
                    // as the ATG probe's StyleEnum<T> trick).
                    _measureTextSize = m;
                    _measureUndefined = Enum.ToObject(ps[2].ParameterType, 0);
                    break;
                }
            }
            catch { }
            try { _contentRectProp = VisualElementType.GetProperty("contentRect", pubInst); } catch { }
            try
            {
                var styleType = _styleProp?.PropertyType;
                _styleWhiteSpaceProp = styleType?.GetProperty("whiteSpace", pubInst);
                _styleTextAlignProp = styleType?.GetProperty("unityTextAlign", pubInst);
            }
            catch { }
            try
            {
                var resolvedType = _resolvedStyleProp?.PropertyType;
                _resolvedWhiteSpaceProp = resolvedType?.GetProperty("whiteSpace", pubInst);
                _resolvedTextAlignProp = resolvedType?.GetProperty("unityTextAlign", pubInst);
                _resolvedTextGenProp = resolvedType?.GetProperty("unityTextGenerator", pubInst);
            }
            catch { }
        }

        internal static bool IsTextElementInstance(object o)
            => Available && o != null && TextElementType.IsInstanceOfType(o);

        internal static string GetElementText(object element)
        {
            try { return _textProp?.GetValue(element, null) as string; }
            catch { return null; }
        }

        /// <summary>
        /// Write without re-entering our own setter prefix, AND keep <c>_written</c> honest: the
        /// scan compares an element's text against what we last wrote, and a reflow that bypassed
        /// that table would make our own final form look like fresh game text one frame later.
        /// </summary>
        internal static void SetElementTextSilently(object element, string text)
        {
            if (_textProp == null) return;
            _writingBack = true;
            try { _textProp.SetValue(element, text, null); }
            finally { _writingBack = false; }
            _written.Remove(element);
            _written.Add(element, text);
        }

        internal static bool IsElementAttached(object element)
        {
            try { return _panelProp != null && _panelProp.GetValue(element, null) != null; }
            catch { return false; }
        }

        /// <summary>
        /// True when this element renders through the Advanced Text Generator, which does bidi
        /// and shaping natively — presenting on top of it would double-process. The property only
        /// exists on Unity 6+; anywhere it cannot be read, the answer is "standard generator".
        /// </summary>
        internal static bool IsAtgActive(object element)
        {
            EnsureRtlPlumbing();
            if (_resolvedTextGenProp == null) return false;
            try
            {
                var resolved = _resolvedStyleProp.GetValue(element, null);
                if (resolved == null) return false;
                object gen = _resolvedTextGenProp.GetValue(resolved, null);
                return gen != null && Enum.GetName(gen.GetType(), gen) == "Advanced";
            }
            catch { return false; }
        }

        // Underline crash guard plumbing — see UnderlineIsSafe. The font definition property
        // itself is the font path's _resolvedFontDefProp, resolved at init.
        private static bool _underlineSafetyResolved;
        private static bool _engineHasUnderlineFix;        // Unity >= 6000.5 (fix landed in 6000.5.0a5)
        private static PropertyInfo _fontDefFontAssetProp; // FontDefinition.fontAsset
        private static MethodInfo _hasCharactersMethod;    // FontAsset.HasCharacters(string, out uint[], bool, bool)

        /// <summary>
        /// Can THIS element draw an underline/strikethrough over THIS text without dying?
        ///
        /// Unity's tracked defect (fixed in 6000.5.0a5, their repro is "&lt;u&gt;Hello 😁&lt;/u&gt;"):
        /// DrawUnderlineMesh indexes meshInfo with the material of the '_' glyph, and when the
        /// underlined glyphs come from a FALLBACK font asset that index is out of bounds. So the
        /// underline is safe only when the engine carries the fix. Everything else is logged and
        /// refused — see the block inside for why font coverage, the obvious candidate, turned
        /// out not to predict the crash.
        /// </summary>
        internal static bool UnderlineIsSafe(object element, string text)
        {
            if (!_underlineSafetyResolved)
            {
                _underlineSafetyResolved = true;
                try
                {
                    // "6000.3.6f1" → 6000 / 3. Anything at or past 6000.5 ships Unity's fix.
                    var v = Application.unityVersion.Split('.');
                    if (v.Length >= 2 && int.TryParse(v[0], out int major) && int.TryParse(v[1], out int minor))
                        _engineHasUnderlineFix = major > 6000 || (major == 6000 && minor >= 5);
                }
                catch { }
                try
                {
                    var pubInst = BindingFlags.Public | BindingFlags.Instance;
                    _fontDefFontAssetProp = _resolvedFontDefProp?.PropertyType.GetProperty("fontAsset", pubInst);
                    var assetType = _fontDefFontAssetProp?.PropertyType;
                    _hasCharactersMethod = assetType?.GetMethod("HasCharacters",
                        new[] { typeof(string), typeof(uint[]).MakeByRefType(), typeof(bool), typeof(bool) });
                }
                catch { }
            }

            if (_engineHasUnderlineFix) return true;

            try
            {
                if (_resolvedFontDefProp == null || _fontDefFontAssetProp == null || _hasCharactersMethod == null)
                { LogUnderlineVerdict(element, false, "font/HasCharacters API not resolvable"); return false; }
                var resolved = _resolvedStyleProp.GetValue(element, null);
                if (resolved == null) { LogUnderlineVerdict(element, false, "no resolved style"); return false; }
                object def = _resolvedFontDefProp.GetValue(resolved, null);
                object fontAsset = def == null ? null : _fontDefFontAssetProp.GetValue(def, null);
                if (fontAsset == null || (fontAsset is UnityEngine.Object uo && uo == null))
                { LogUnderlineVerdict(element, false, "element resolves to no FontAsset (a legacy Font, or none)"); return false; }

                // 🔴 THE ANSWER IS NO, and the bench is what settled it — four crashes, the last
                // one two lines after this very check logged "carries every glyph to draw".
                //
                // Font coverage was a reasonable hypothesis and it is WRONG as a predicate: a
                // single asset covering every drawn glyph still died. The remaining suspects all
                // live inside Unity's routine and none is observable from here — multi-atlas
                // assets give one materialIndex per atlas texture, and the underline's '_' can
                // sit in a different one from the RTL glyphs; the routine also derives the line
                // from glyph positions that run right-to-left. Unity fixed it in 6000.5.0a5 and
                // we cannot second-guess which branch a given frame takes.
                //
                // So on an engine without the fix, an RTL text on this generator loses its
                // underline. Not arbitrary: it is the only rule the evidence supports. Everything
                // below the return exists to keep LEARNING at zero risk — the details are logged,
                // and the day one of them turns out to be the real discriminator, it becomes a
                // condition. TMP is untouched (its bench never crashed), and so is 6000.5+.
                LogUnderlineVerdict(element, false, DescribeAsset(fontAsset, text));
                return false;
            }
            catch (Exception ex) { LogUnderlineVerdict(element, false, "check threw: " + ex.Message); return false; }
        }

        /// <summary>
        /// What we know about this element's font, for the record: whether one asset covers every
        /// drawn glyph, and how many atlas textures it spreads over. Characterises the defect
        /// without betting the game on the answer.
        /// </summary>
        private static string DescribeAsset(object fontAsset, string text)
        {
            string name = (fontAsset as UnityEngine.Object)?.name ?? "?";
            string coverage = "coverage unknown";
            string atlases = "";
            try
            {
                var args = new object[] { text + "_", null, false, false };
                bool all = (bool)_hasCharactersMethod.Invoke(fontAsset, args);
                var missing = args[1] as uint[];
                coverage = all ? "covers every drawn glyph"
                               : $"missing {(missing == null ? "?" : missing.Length.ToString())} drawn glyph(s)";
            }
            catch { }
            try
            {
                var texturesProp = fontAsset.GetType().GetProperty("atlasTextures", BindingFlags.Public | BindingFlags.Instance);
                if (texturesProp?.GetValue(fontAsset, null) is Array textures)
                    atlases = $", {textures.Length} atlas texture(s)";
            }
            catch { }
            return $"'{name}' {coverage}{atlases} — this engine's DrawUnderlineMesh is not safe for RTL whatever the answer";
        }

        // Every verdict is logged while this engine's underline defect is being characterised:
        // the bench crashed a third time with the tag apparently absent, and the log could not
        // say whether the guard had even been consulted for the element that died.
        private static int _underlineVerdictBudget = 12;

        private static void LogUnderlineVerdict(object element, bool safe, string why)
        {
            if (_underlineVerdictBudget <= 0) return;
            _underlineVerdictBudget--;
            TranslatorCore.LogInfo($"[RtlPresenter] underline verdict for '{PathOf(element)}': {(safe ? "KEEP" : "DROP")} — {why}");
        }

        /// <summary>
        /// The assigned (shaped logical) string cut into lines the way THIS element would wrap
        /// it: greedy word fitting measured by the engine itself. Null = not answerable — either
        /// WAIT (waitForLayout: the element has no width yet, a hidden pane or a first frame; it
        /// will get one when it shows, and the caller must not burn its fallback attempts on
        /// that) or give up per whyNot (no measure API, wall of text, measure failure).
        /// </summary>
        internal static List<string> TryBreakLines(object element, string assigned, out string whyNot, out bool waitForLayout)
        {
            whyNot = null;
            waitForLayout = false;
            EnsureRtlPlumbing();
            if (_measureTextSize == null || _contentRectProp == null)
            { whyNot = "MeasureTextSize not available on this runtime"; return null; }

            // No soft wrap on this element → every break is an explicit '\n' already.
            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                object ws = resolved == null || _resolvedWhiteSpaceProp == null
                    ? null : _resolvedWhiteSpaceProp.GetValue(resolved, null);
                string wsName = ws == null ? null : Enum.GetName(ws.GetType(), ws);
                if (wsName == "NoWrap" || wsName == "Pre")
                    return new List<string>(assigned.Split('\n'));
            }
            catch { }

            float width;
            try
            {
                object rect = _contentRectProp.GetValue(element, null);
                width = rect is Rect r ? r.width : float.NaN;
            }
            catch { width = float.NaN; }
            if (float.IsNaN(width) || width < 1f)
            { whyNot = "no layout yet (element has no width)"; waitForLayout = true; return null; }

            // A pathological wall of text would mean thousands of reflection round-trips into the
            // engine — the whole-string fallback is the lesser harm there.
            if (assigned.Length > 4000) { whyNot = "too long to measure word by word"; return null; }

            try
            {
                var lines = new List<string>();
                foreach (string paragraph in assigned.Split('\n'))
                {
                    if (paragraph.Length == 0) { lines.Add(""); continue; }
                    string current = "";
                    foreach (string word in paragraph.Split(' '))
                    {
                        string candidate = current.Length == 0 ? word : current + " " + word;
                        if (current.Length == 0 || MeasureWidth(element, candidate) <= width + 0.5f)
                        {
                            current = candidate;
                            continue;
                        }
                        lines.Add(current);
                        current = word;
                    }
                    lines.Add(current);
                }
                return lines;
            }
            catch (Exception ex)
            {
                whyNot = $"measure failed: {ex.Message}";
                return null;
            }
        }

        private static float MeasureWidth(object element, string s)
        {
            object r = _measureTextSize.Invoke(element,
                new object[] { s, 0f, _measureUndefined, 0f, _measureUndefined });
            if (r is Vector2 v) return v.x;
            var xf = r?.GetType().GetField("x");
            return xf != null ? Convert.ToSingle(xf.GetValue(r)) : float.NaN;
        }

        /// <summary>
        /// The UI Toolkit face of RtlPresenter.MirrorAlignment — same decision, same idempotence
        /// (computed from the stored ORIGINAL, never the current state), different plumbing:
        /// alignment here is a STYLE (unityTextAlign), read resolved, written inline.
        /// </summary>
        internal static void MirrorAlign(object element, bool mirror)
        {
            if (!mirror) return;
            EnsureRtlPlumbing();
            if (_styleTextAlignProp == null || _resolvedTextAlignProp == null) return;
            try
            {
                object[] stored;
                if (!_rtlAlignOriginal.TryGetValue(element, out stored))
                {
                    var style = _styleProp.GetValue(element, null);
                    var resolved = _resolvedStyleProp.GetValue(element, null);
                    if (style == null || resolved == null) return;
                    stored = new object[]
                    {
                        _styleTextAlignProp.GetValue(style, null),
                        _resolvedTextAlignProp.GetValue(resolved, null),
                    };
                    _rtlAlignOriginal.Add(element, stored);
                }

                object originalEnum = stored[1];
                if (originalEnum == null) return;
                object mirrored = TextShaping.RtlPresenter.MirroredAlignmentValue(originalEnum.GetType(), originalEnum);
                if (mirrored == null) return;

                var styleNow = _styleProp.GetValue(element, null);
                if (styleNow == null) return;
                var styleValue = Activator.CreateInstance(_styleTextAlignProp.PropertyType, mirrored);
                _styleTextAlignProp.SetValue(styleNow, styleValue, null);
            }
            catch { }
        }

        /// <summary>whiteSpace = NoWrap while OUR line breaks are displayed ('\n' stays honored).</summary>
        internal static void DisableWrap(object element)
        {
            EnsureRtlPlumbing();
            if (_styleWhiteSpaceProp == null) return;
            try
            {
                var style = _styleProp.GetValue(element, null);
                if (style == null) return;

                if (!_rtlWrapOriginal.TryGetValue(element, out _))
                    _rtlWrapOriginal.Add(element, new object[] { _styleWhiteSpaceProp.GetValue(style, null) });

                var styleEnumType = _styleWhiteSpaceProp.PropertyType;
                var wsType = styleEnumType.IsGenericType ? styleEnumType.GetGenericArguments()[0] : null;
                if (wsType == null) return;
                var styleValue = Activator.CreateInstance(styleEnumType, Enum.Parse(wsType, "NoWrap"));
                _styleWhiteSpaceProp.SetValue(style, styleValue, null);
            }
            catch { }
        }

        /// <summary>Put back the inline styles an element wore before our RTL adjustments.</summary>
        internal static void RestoreRtlAdjustments(object element)
        {
            try
            {
                var style = _styleProp?.GetValue(element, null);
                if (style == null) return;
                if (_rtlAlignOriginal.TryGetValue(element, out var align))
                {
                    _rtlAlignOriginal.Remove(element);
                    if (align[0] != null) _styleTextAlignProp?.SetValue(style, align[0], null);
                }
                if (_rtlWrapOriginal.TryGetValue(element, out var wrap))
                {
                    _rtlWrapOriginal.Remove(element);
                    if (wrap[0] != null) _styleWhiteSpaceProp?.SetValue(style, wrap[0], null);
                }
            }
            catch { }
        }

        #endregion
    }
}
