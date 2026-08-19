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

                _panelProp = VisualElementType.GetProperty("panel", pubInst);
                if (_panelProp != null)
                {
                    _focusControllerProp = _panelProp.PropertyType.GetProperty("focusController", pubInst);
                    if (_focusControllerProp != null)
                        _focusedElementProp = _focusControllerProp.PropertyType
                            .GetProperty("focusedElement", pubInst);
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
            return TranslatorCore.IsUserExcludedPath(IdFor(element), () => PathOf(element));
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
        /// Put a text into an element from outside — the routing does this when a reveal settles
        /// on something already translated.
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
    }
}
