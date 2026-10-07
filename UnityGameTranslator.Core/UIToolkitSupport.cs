using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityGameTranslator.Common;

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
        private static ResolvedRead _resolvedFont;      // IResolvedStyle.unityFont -> Font

        /// <summary>
        /// One value of an element's resolved style. 🔴 On IL2CPP 2022.3 the interop's IResolvedStyle
        /// lost unityTextAlign, whiteSpace, fontSize, unityFont and backgroundImage (stripped), and
        /// VisualElement's explicit implementation the interop generates is not usable (read on the
        /// element it threw "Object was garbage collected in IL2CPP domain" and left the element's
        /// style unwritable, bench 2026-10-04). The engine answers each of them from the element's
        /// ComputedStyle — VisualElement's IResolvedStyle.X is computedStyle.X, its .value for a
        /// length — kept in its m_Style field (a boxed copy, native getters): read there when the
        /// interface lacks the member. Unread, UI Toolkit pictures were never replaced and a font set
        /// by -unity-font was never found on that runtime.
        /// </summary>
        private sealed class ResolvedRead
        {
            private readonly PropertyInfo _viaInterface, _viaComputed, _lengthValue;

            internal ResolvedRead(PropertyInfo viaInterface, PropertyInfo viaComputed, PropertyInfo lengthValue)
            {
                _viaInterface = viaInterface;
                _viaComputed = viaComputed;
                _lengthValue = lengthValue;
            }

            internal bool CanRead => _viaInterface != null || _viaComputed != null;

            /// <param name="resolved">The element's resolvedStyle, already read by the caller.</param>
            internal object Get(object element, object resolved)
            {
                if (_viaInterface != null) return resolved == null ? null : _viaInterface.GetValue(resolved, null);
                if (_viaComputed == null || element == null) return null;
                var computed = Members.Get(_computedStyle, element);
                if (computed == null) return null;
                var value = _viaComputed.GetValue(computed, null);
                return value == null || _lengthValue == null ? value : _lengthValue.GetValue(value, null);
            }
        }

        // Where a resolved value is read when IResolvedStyle lost it: VisualElement's ComputedStyle.
        private static MemberInfo _computedStyle;
        private static bool _computedStyleResolved;

        /// <summary>The resolved style member of that name, read where this runtime keeps it (<see cref="ResolvedRead"/>).</summary>
        private static ResolvedRead ResolvedMember(string name)
        {
            var pubInst = BindingFlags.Instance | BindingFlags.Public;
            var viaInterface = Members.Property(_resolvedStyleProp?.PropertyType, name, pubInst);
            if (viaInterface != null || _resolvedStyleProp == null) return new ResolvedRead(viaInterface, null, null);

            if (!_computedStyleResolved)
            {
                _computedStyleResolved = true;
                _computedStyle = Members.FieldOrProperty(VisualElementType, "m_Style", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            var viaComputed = Members.Property(Members.TypeOf(_computedStyle), name, pubInst);
            PropertyInfo lengthValue = null;
            if (viaComputed != null && viaComputed.PropertyType.Name == "Length")
            {
                lengthValue = Members.Property(viaComputed.PropertyType, "value", pubInst);
                if (lengthValue == null) viaComputed = null;   // a length whose value cannot be read answers nothing
            }
            TranslatorCore.LogInfo(viaComputed != null
                ? $"[UIToolkit] resolvedStyle.{name} is absent on this runtime: read from the element's computed style"
                : $"[UIToolkit] resolvedStyle.{name} is absent on this runtime, and so is the computed style's");
            return new ResolvedRead(null, viaComputed, lengthValue);
        }

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
        private static ResolvedRead _resolvedBackground;  // IResolvedStyle.backgroundImage
        private static PropertyInfo _styleBackgroundValueProp; // StyleBackground.value -> Background
        // StyleBackground(Background), or the (Background, StyleKeyword.Undefined) it calls: IL2CPP
        // 2022.3 compiled the first into its callers and only the second is left (bench 2026-10-04).
        private static ConstructorInfo _styleBackgroundCtor;
        private static int _styleBackgroundArgs;
        private static object _styleKeywordUndefined;
        private static MethodInfo _backgroundFromSprite;  // Background.FromSprite(Sprite)
        private static MethodInfo _backgroundFromTexture; // Background.FromTexture2D(Texture2D)
        private static PropertyInfo _backgroundSpriteProp;  // Background.sprite
        private static PropertyInfo _backgroundTextureProp; // Background.texture

        /// <summary>True when a picture can be read AND written on this build.</summary>
        public static bool CanSetImage { get; private set; }

        private static PropertyInfo _styleFontSizeProp;   // IStyle.fontSize         -> StyleLength
        private static ResolvedRead _resolvedFontSize;    // IResolvedStyle.fontSize -> float
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
                    _resolvedFont = ResolvedMember("unityFont");
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
                    _resolvedFontSize = ResolvedMember("fontSize");
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
                bool canRead = (_resolvedFont != null && _resolvedFont.CanRead) || _resolvedFontDefProp != null;
                bool canWrite = _styleFontProp != null && _styleFontDefinitionType != null
                                && (_fromFontMethod != null || _fromSdfFontMethod != null);

                CanSetFont = canRead && canWrite;
            }
            // UI Toolkit's font members, found by reflection on the engine this game ships: a
            // refusal leaves fonts unreplaceable in UI Toolkit, and it is said.
            catch (Exception ex)
            {
                CanSetFont = false;
                Faults.Say("UIToolkit.ResolveFontMembers", ex, "UI Toolkit fonts cannot be replaced");
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
                // The types that load (AssemblyTypes: GetTypes refuses a whole assembly for one).
                foreach (var type in AssemblyTypes.Of(asm))
                {
                    if (type.Name == "FontAsset"
                        && type.Namespace != null
                        && type.Namespace.IndexOf("TextCore", StringComparison.Ordinal) >= 0)
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        private static Type FindTypeAnywhere(string fullName)
        {
            return Type.GetType(fullName, false) ?? AssemblyTypes.Find(fullName);
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

            var exact = AssemblyTypes.Find(fullName);
            if (exact != null) return exact;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                // The types that load (AssemblyTypes: GetTypes refuses a whole assembly for one).
                foreach (var type in AssemblyTypes.Of(asm))
                {
                    if (type.Name == simpleName
                        && type.Namespace != null
                        && type.Namespace.EndsWith("UIElements", StringComparison.Ordinal))
                    {
                        return type;
                    }
                }
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

                // 🔴 Documents announce themselves. Looking them up was one atomic engine call
                // per detection cycle — ~31 ms on a large save, i.e. a dropped frame every
                // second, measured (UITK.Cycle max 31-33 ms) — to find the same two or three
                // documents each time. UIDocument.OnEnable fires for every document that comes
                // to life after this patch; the ones alive before it are taken once, on the
                // first cycle. If the patch cannot be applied, the lookup stays, per cycle.
                try
                {
                    // ⚠ Both visibilities: private on Mono, but the IL2CPP proxies are generated
                    // public — looked up as NonPublic only, it was not found there, said nothing,
                    // and left a ~20 ms lookup on every cycle of a game with no UI Toolkit at all.
                    var onEnable = UIDocumentType.GetMethod("OnEnable",
                        BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (onEnable != null)
                    {
                        var postfix = typeof(UIToolkitSupport).GetMethod(
                            nameof(UIDocument_OnEnable_Postfix), BindingFlags.Static | BindingFlags.Public);
                        patcher(onEnable, null, postfix);
                        _documentsFromEvents = true;
                        TranslatorCore.LogInfo("[UIToolkit] Patched UIDocument.OnEnable — documents are discovered on arrival, not by lookup");
                    }
                    else
                    {
                        TranslatorCore.LogWarning("[UIToolkit] UIDocument.OnEnable not found on this runtime — documents will be looked up every cycle");
                    }
                }
                catch (Exception ex)
                {
                    TranslatorCore.LogWarning($"[UIToolkit] UIDocument.OnEnable not patched ({ex.Message}) — documents will be looked up every cycle");
                }
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
                catch (Exception ex) { Faults.Say("UIToolkit.PathOf", ex); break; }
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
            catch (Exception ex) { Faults.Say("UIToolkit.SegmentFor", ex); return "?"; }
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
            catch (Exception ex) { Faults.Say("UIToolkit.FirstClass", ex); }

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
                catch (Exception ex) { Faults.Say("UIToolkit.IsInsideTextInput", ex); return false; }
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
            catch (Exception ex) { Faults.Say("UIToolkit.FocusedText", ex); return null; }

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
            catch (Exception ex) { Faults.Say("UIToolkit.ReadAnyText", ex); }
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

            string candidate = Markup.Strip(text).Trim();
            if (candidate.Length == 0) return false;
            if (!string.Equals(candidate, focused.Trim(), StringComparison.Ordinal)) return false;

            return TranslatorPatches.CouldBeTypedText(candidate, _lastTypedChange);
        }

        public static void TextElement_SetText_Prefix(object __instance, ref string value)
        {
            long tSet = Perf.Start();
            try
            {
            if (_writingBack) return;
            if (string.IsNullOrEmpty(value)) return;
            if (!TranslatorCore.TranslationsActive) return;

            // Unity APIs below are main-thread only; on IL2CPP the wrong thread crashes natively
            // rather than throwing, which is not a failure anyone can diagnose from a log.
            if (!TranslatorCore.IsMainThread) return;

            // What this game shows, for UGT Manager (texts-seen.json). Our own window is uGUI, so
            // every UI Toolkit element is the game's.
            Engine.TextsSeen.Note(Common.TextSystem.UiToolkit);

            // Never the player's own typing — the box itself, or a live echo of it elsewhere.
            if (IsInsideTextInput(__instance)) return;
            if (IsExcluded(__instance)) return;
            if (IsEchoOfTyping(__instance, value)) return;

            // A fresh element gets its font the frame after, not when the walk comes round.
            QueueForFont(__instance);

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
                _originalFontName.TryGet(__instance, out string uitkFont);
                FontOverrideRule uitkOverride = null;
                if (TranslatorCore.FontOverrides.Count > 0)
                    // `before`, not `value`: routing has turned value into our translation, and a
                    // text: rule is written against the game's words (FontRules.Find).
                    uitkOverride = TranslatorCore.FindFontOverride(IdFor(__instance), PathOf(__instance), uitkFont, before);
                TextShaping.RtlPresenter.Present(__instance, IdFor(__instance), ref value, uitkFont, uitkOverride);
                if (!string.Equals(before, value, StringComparison.Ordinal))
                {
                    RememberOriginal(__instance, before);
                    // 🔴 And record what we are about to put there. The scan recognises its own
                    // work by this table alone: without the entry, every scan pass re-routes
                    // every element we translated — the full path, on every element, several
                    // times a second. That is the periodic stutter the user felt, and it appeared
                    // when stage D started finishing the text here (single pass) instead of
                    // through SetElementTextSilently, which had always filled this in.
                    _written.Remove(__instance);
                    _written.Set(__instance, value);
                }
            }
            catch (Exception ex) { Faults.Say("UIToolkit.TextElement_SetText_Prefix", ex); }
            }
            finally { Perf.Stop(Perf.UitkSetter, tSet); }
        }

        #endregion

        #region Discovery

        /// <summary>
        /// What we last wrote into an element, so a pass can tell its own work from the game's.
        ///
        /// ⚠ Weak (an ElementStore) rather than a dictionary of elements: UI Toolkit creates and drops
        /// elements constantly — list virtualisation recycles them by the hundred — and a strong
        /// reference per element would keep every one of them alive for the life of the process.
        /// </summary>
        private static readonly ElementStore<string> _written = new ElementStore<string>();

        /// <summary>
        /// What the GAME had in an element before we wrote over it.
        ///
        /// 🔴 Without this, switching translation off left this framework translated. Restoring
        /// works from TranslatorScanner's per-component originals, which are keyed by instance id —
        /// a VisualElement has none, so it was stored nowhere and put back nowhere. Weak for the
        /// same reason as everything else here: elements are recycled by the hundred.
        /// </summary>
        private static readonly ElementStore<string> _originalText = new ElementStore<string>();

        /// <summary>Remember what was there, the first time we replace it.</summary>
        private static void RememberOriginal(object element, string original)
        {
            if (element == null || string.IsNullOrEmpty(original)) return;
            if (_originalText.TryGet(element, out _)) return;
            _originalText.Set(element, original);
        }

        #region Identity

        private sealed class IdBox { public long Value; }

        /// <summary>What an id stands for: the element, weakly — and on IL2CPP the native object behind it.</summary>
        private sealed class Identity
        {
            public WeakReference Wrapper;   // the last wrapper met, weakly
            public long Native;             // IL2CPP: the native object's address, 0 on Mono
            public IntPtr Handle;           // IL2CPP: a weak handle on it (WeakNativeHandle), Zero otherwise
            public Type Type;               // IL2CPP: the wrapper's type, to wrap the object again
        }

        /// <summary>
        /// A stable number for an element, so it can be followed by the same routing every other
        /// text framework uses.
        ///
        /// 🔴 **Weak, and that is the whole design.** The routing state lives in a strong
        /// dictionary keyed by this number. UI Toolkit recycles elements by the hundred — list
        /// virtualisation — so anything holding them strongly keeps every element ever scrolled
        /// past alive for the life of the process. The number is attached to the element here and
        /// dies with it; <see cref="Sweep"/> then drops the state it pointed at.
        ///
        /// 🔴 **On IL2CPP the element is not its wrapper.** A hook's instance is a NEW wrapper of the
        /// same element at each call (the tree walk's are cached, which is why the check in
        /// ReportProxyIdentityOnce saw nothing): numbered by the wrapper, one element had as many
        /// ids, and every table keyed on it — its original font, its original size, what was
        /// written into it — answered for a stranger (bench 2026-10-04, 2022.3 IL2CPP: the size
        /// scaled, then put back by the replacement font taken for the game's). There the number
        /// follows the native object, and a weak handle on it tells it from the next object made
        /// at the same address once it is collected.
        /// </summary>
        private static readonly ConditionalWeakTable<object, IdBox> _ids =
            new ConditionalWeakTable<object, IdBox>();

        /// <summary>The other direction, weakly, so an id can be resolved and swept.</summary>
        private static readonly Dictionary<long, Identity> _byId = new Dictionary<long, Identity>();

        /// <summary>IL2CPP: native address → id.</summary>
        private static readonly Dictionary<long, long> _idByNative = new Dictionary<long, long>();

        private interface IForgetsIds { void Forget(long id); }
        private static List<IForgetsIds> _elementStores;
        // Lazily: the stores are static fields, made in the order they are written.
        private static List<IForgetsIds> ElementStores => _elementStores ?? (_elementStores = new List<IForgetsIds>());

        /// <summary>
        /// Something the mod keeps per element — what it wore before us, what we wrote into it. On
        /// Mono by the element itself, forgotten with it; on IL2CPP by its <see cref="IdFor"/> (the
        /// native object, never the wrapper a hook happens to receive), forgotten when the id is
        /// (<see cref="Forget"/>: swept once its object is collected).
        /// </summary>
        private sealed class ElementStore<T> : IForgetsIds where T : class
        {
            private readonly ConditionalWeakTable<object, T> _byObject = new ConditionalWeakTable<object, T>();
            private readonly Dictionary<long, T> _byNumber = new Dictionary<long, T>();

            internal ElementStore() { ElementStores.Add(this); }

            internal bool TryGet(object element, out T value) =>
                IsInteropWrapper(element) ? _byNumber.TryGetValue(IdFor(element), out value) : _byObject.TryGetValue(element, out value);

            internal bool Has(object element) => TryGet(element, out _);

            internal void Set(object element, T value)
            {
                if (IsInteropWrapper(element)) { _byNumber[IdFor(element)] = value; return; }
                _byObject.Remove(element);
                _byObject.Add(element, value);
            }

            internal void Remove(object element)
            {
                if (IsInteropWrapper(element)) _byNumber.Remove(IdFor(element));
                else _byObject.Remove(element);
            }

            void IForgetsIds.Forget(long id) => _byNumber.Remove(id);
        }

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

            if (IsInteropWrapper(element))
            {
                long native = NativeAddress(element);
                if (_idByNative.TryGetValue(native, out long known) && _byId.TryGetValue(known, out var who))
                {
                    if (StillAt(who.Handle, native))
                    {
                        if (who.Wrapper.Target == null) who.Wrapper = new WeakReference(element);
                        return known;
                    }
                    Forget(known);   // collected, and another object made at its address
                }
                long id = _nextId++;
                _idByNative[native] = id;
                _byId[id] = new Identity { Wrapper = new WeakReference(element), Native = native, Handle = WeakNativeHandle(element), Type = element.GetType() };
                return id;
            }

            if (_ids.TryGetValue(element, out var box)) return box.Value;

            box = new IdBox { Value = _nextId++ };
            _ids.Add(element, box);
            _byId[box.Value] = new Identity { Wrapper = new WeakReference(element) };
            return box.Value;
        }

        private static readonly Type _il2cppObject = Type.GetType("Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase, Il2CppInterop.Runtime");
        private static readonly PropertyInfo _il2cppPointer = _il2cppObject?.GetProperty("Pointer", BindingFlags.Instance | BindingFlags.Public);

        /// <summary>An IL2CPP interop wrapper — whose identity is the native object's, never its own (<see cref="IdFor"/>).</summary>
        internal static bool IsInteropWrapper(object o) => o != null && _il2cppPointer != null && _il2cppObject.IsInstanceOfType(o);

        private static long NativeAddress(object wrapper) => ((IntPtr)_il2cppPointer.GetValue(wrapper, null)).ToInt64();

        private static bool _gcHandlesSought;
        // IL2CPP.il2cpp_gchandle_new_weakref / _get_target / _free, bound once: the sweep asks every
        // id at every pass, a reflective call each would be the cost of the pass.
        private static Func<IntPtr, bool, IntPtr> _gcNewWeak;
        private static Func<IntPtr, IntPtr> _gcTarget;
        private static Action<IntPtr> _gcFree;

        /// <summary>
        /// A weak handle on the native object behind a wrapper, Zero when the runtime offers none. 🔴 A
        /// native address is the object only while it lives: once collected, the next object of its
        /// size may be made at the same place, and a table keyed by the address would hand it what the
        /// dead one wore. The handle tells the two apart (<see cref="StillAt"/>).
        /// </summary>
        private static IntPtr WeakNativeHandle(object wrapper)
        {
            if (!_gcHandlesSought)
            {
                _gcHandlesSought = true;
                var il2cpp = AssemblyTypes.Find("Il2CppInterop.Runtime.IL2CPP");
                var statics = BindingFlags.Public | BindingFlags.Static;
                var newWeak = il2cpp?.GetMethod("il2cpp_gchandle_new_weakref", statics, null, new[] { typeof(IntPtr), typeof(bool) }, null);
                var target = il2cpp?.GetMethod("il2cpp_gchandle_get_target", statics, null, new[] { typeof(IntPtr) }, null);
                var free = il2cpp?.GetMethod("il2cpp_gchandle_free", statics, null, new[] { typeof(IntPtr) }, null);
                if (newWeak != null && target != null && free != null
                    && newWeak.ReturnType == typeof(IntPtr) && target.ReturnType == typeof(IntPtr))
                {
                    _gcNewWeak = (Func<IntPtr, bool, IntPtr>)Delegate.CreateDelegate(typeof(Func<IntPtr, bool, IntPtr>), newWeak);
                    _gcTarget = (Func<IntPtr, IntPtr>)Delegate.CreateDelegate(typeof(Func<IntPtr, IntPtr>), target);
                    _gcFree = (Action<IntPtr>)Delegate.CreateDelegate(typeof(Action<IntPtr>), free);
                }
                else if (il2cpp != null)
                    TranslatorCore.LogWarning("[UIToolkit] no IL2CPP weak handles on this runtime: an element is known by its address alone, and forgotten with its last wrapper");
            }
            return _gcNewWeak == null ? IntPtr.Zero : _gcNewWeak(new IntPtr(NativeAddress(wrapper)), false);
        }

        /// <summary>Whether the object a weak handle was taken on still lives at that address (Zero: no handle to ask, taken as yes).</summary>
        private static bool StillAt(IntPtr handle, long address) =>
            handle == IntPtr.Zero || _gcTarget(handle).ToInt64() == address;

        private static void FreeWeakHandle(IntPtr handle)
        {
            if (handle != IntPtr.Zero) _gcFree(handle);
        }

        /// <summary>The element behind a number, or null once it has been collected.</summary>
        public static object ElementFor(long id)
        {
            if (!_byId.TryGetValue(id, out var who)) return null;

            var target = who.Wrapper.Target;
            if (target != null) return target;
            // IL2CPP: the wrapper went, the element may not have — wrapped again while it lives.
            if (NativeAlive(who))
            {
                target = Activator.CreateInstance(who.Type, new object[] { new IntPtr(who.Native) });
                who.Wrapper = new WeakReference(target);
                return target;
            }
            Forget(id);
            return null;
        }

        /// <summary>IL2CPP: the native object an id stands for still lives (known only through a weak handle).</summary>
        private static bool NativeAlive(Identity who) =>
            who.Native != 0 && who.Handle != IntPtr.Zero && StillAt(who.Handle, who.Native);

        private static void Forget(long id)
        {
            if (_byId.TryGetValue(id, out var who) && who.Native != 0)
            {
                FreeWeakHandle(who.Handle);
                if (_idByNative.TryGetValue(who.Native, out long current) && current == id) _idByNative.Remove(who.Native);
            }
            _byId.Remove(id);
            foreach (var store in ElementStores) store.Forget(id);
            TranslatorCore.Router.Forget(id);

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
                if (pair.Value.Wrapper.Target != null || NativeAlive(pair.Value)) continue;
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
                _resolvedBackground = ResolvedMember("backgroundImage");

                _styleBackgroundValueProp = Members.Property(_styleBackgroundType, "value", pubInst);
                var anyCtor = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                _styleBackgroundCtor = _styleBackgroundType.GetConstructor(anyCtor, null, new[] { _backgroundType }, null);
                _styleBackgroundArgs = 1;
                var keywordType = FindType("UnityEngine.UIElements.StyleKeyword");
                if (_styleBackgroundCtor == null && keywordType != null)
                {
                    _styleBackgroundCtor = _styleBackgroundType.GetConstructor(anyCtor, null, new[] { _backgroundType, keywordType }, null);
                    _styleBackgroundArgs = 2;
                    _styleKeywordUndefined = Enum.Parse(keywordType, "Undefined");
                }
                _backgroundSpriteProp = _backgroundType.GetProperty("sprite", pubInst);
                _backgroundTextureProp = _backgroundType.GetProperty("texture", pubInst);

                var statics = BindingFlags.Public | BindingFlags.Static;
                _backgroundFromSprite = _backgroundType.GetMethod("FromSprite", statics);
                _backgroundFromTexture = _backgroundType.GetMethod("FromTexture2D", statics);

                CanSetImage = _styleBackgroundProp != null
                              && _resolvedBackground.CanRead
                              && _styleBackgroundCtor != null
                              && (_backgroundFromSprite != null || _backgroundFromTexture != null);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.ResolveImageMembers", ex); CanSetImage = false; }
        }

        /// <summary>What the game had as this element's picture, so it can be put back. [0] = the
        /// Background, kept when a replacement is first written (ElementStore: IL2CPP wrappers).</summary>
        private static readonly ElementStore<object[]> _originalBackground = new ElementStore<object[]>();

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

                object current = _resolvedBackground.Get(element, resolved);
                string name = NameOfBackground(current);
                if (string.IsNullOrEmpty(name))
                {
                    // Nothing resolved — unless the element's own inline style names a picture: then
                    // it is the reading that fails, and that is said (once per picture).
                    if (TranslatorCore.DebugMode && _styleBackgroundValueProp != null)
                    {
                        var inline = StyleGet(_styleBackgroundProp, _styleProp.GetValue(element, null));
                        string inlineName = inline == null ? null : NameOfBackground(_styleBackgroundValueProp.GetValue(inline, null));
                        if (!string.IsNullOrEmpty(inlineName) && DiagnosticOnce.First("UITK.image.unread", inlineName))
                            TranslatorCore.LogDebug($"[UIToolkit] picture '{inlineName}' is set inline, but its resolved style reads {(current == null ? "nothing" : $"sprite {DescribeMember(_backgroundSpriteProp, current)}, texture {DescribeMember(_backgroundTextureProp, current)}")}");
                    }
                    return;
                }

                var replacement = ImageReplacer.GetReplacement(name);
                if (TranslatorCore.DebugMode && DiagnosticOnce.First("UITK.image.seen", name))
                    TranslatorCore.LogDebug($"[UIToolkit] picture '{name}' on {PathOf(element)}: {(replacement == null ? "no replacement" : "a replacement")}, wears {DescribeMember(_backgroundSpriteProp, current)}");
                if (replacement == null) return;

                // Already wearing it: writing every pass would be a style assignment per element
                // per scan, for nothing. ⚠ The very object, never its name: a replacement is named
                // as the picture it replaces (ImageReplacer.ImportReplacement), and compared by name
                // every element counted as already wearing it — nothing was ever replaced.
                if (_backgroundSpriteProp?.GetValue(current, null) is Sprite worn && worn == replacement)
                    return;

                if (!_originalBackground.Has(element))
                    _originalBackground.Set(element, new[] { current });
                WriteBackground(element, BuildBackground(replacement));
            }
            catch (Exception ex) { Faults.Say("UIToolkit.HandleImage", ex); }
        }

        /// <summary>What a member gives back, for a line saying why something was found empty.</summary>
        private static string DescribeMember(PropertyInfo p, object on) =>
            p == null ? "member absent" : DescribeRead(() => p.GetValue(on, null));

        /// <inheritdoc cref="DescribeMember"/>
        private static string DescribeRead(Func<object> read)
        {
            try
            {
                object v = read();
                if (v == null) return "null";
                if (v is UnityEngine.Object o) return o == null ? "destroyed" : $"{v.GetType().Name} '{o.name}'";
                return v.GetType().Name;
            }
            catch (Exception ex) { return "threw " + (ex.InnerException ?? ex).GetType().Name; }
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
            catch (Exception ex) { Faults.Say("UIToolkit.NameOfBackground", ex); }

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
            catch (Exception ex) { Faults.Say("UIToolkit.BuildBackground", ex); }

            return null;
        }

        private static void WriteBackground(object element, object background)
        {
            if (background == null) return;

            try
            {
                var styleValue = _styleBackgroundArgs == 1
                    ? _styleBackgroundCtor.Invoke(new[] { background })
                    : _styleBackgroundCtor.Invoke(new[] { background, _styleKeywordUndefined });
                var style = _styleProp.GetValue(element, null);
                if (style != null) _styleBackgroundProp.SetValue(style, styleValue, null);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.WriteBackground", ex); }
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

                object background = _resolvedBackground.Get(element, resolved);
                return _backgroundSpriteProp?.GetValue(background, null) as Sprite;
            }
            catch (Exception ex) { Faults.Say("UIToolkit.SpriteOf", ex); return null; }
        }

        /// <summary>Give an element its own picture back, if we ever replaced it.</summary>
        private static void RestoreImageOf(object element)
        {
            if (!CanSetImage) return;
            if (!_originalBackground.TryGet(element, out var original) || original[0] == null) return;

            WriteBackground(element, original[0]);
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
                    try { root = _rootProp.GetValue(document, null); } catch (Exception ex) { Faults.Say("UIToolkit.PickAt root", ex); }
                    if (root == null) continue;

                    object panel = null;
                    try { panel = _panelProp.GetValue(root, null); } catch (Exception ex) { Faults.Say("UIToolkit.PickAt panel", ex); }
                    if (panel == null) continue;

                    var mapping = PanelMapping.For(panel, _screenToPanelMethod);
                    object hit = null;
                    try { hit = _pickMethod.Invoke(panel, new object[] { mapping.ToPanel(screenPoint) }); }
                    catch (Exception ex) { Faults.Say("UIToolkit.PickAt pick", ex); }

                    for (int depth = 0; hit != null && depth < MaxPathDepth; depth++)
                    {
                        if (HasText(hit) && !IsInsideTextInput(hit))
                        {
                            screenRect = mapping.ToScreen(WorldBoundOf(hit));
                            return hit;
                        }

                        try { hit = _parentProp.GetValue(hit, null); } catch (Exception ex) { Faults.Say("UIToolkit.PickAt parent", ex); break; }
                    }
                }
            }
            catch (Exception ex) { Faults.Say("UIToolkit.PickAt", ex); }

            return null;
        }

        private static bool HasText(object element)
        {
            try { return !string.IsNullOrEmpty(_textProp.GetValue(element, null) as string); }
            catch (Exception ex) { Faults.Say("UIToolkit.HasText", ex); return false; }
        }

        private static Rect WorldBoundOf(object element)
        {
            try
            {
                if (_worldBoundProp?.GetValue(element, null) is Rect rect) return rect;
            }
            catch (Exception ex) { Faults.Say("UIToolkit.WorldBoundOf", ex); }
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
                catch (Exception ex) { Faults.Say("UIToolkit.For", ex); }

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
                    try { root = _rootProp.GetValue(document, null); } catch (Exception ex) { Faults.Say("UIToolkit.Targets root", ex); }
                    if (root != null) CollectFrom(root, found, keep, ref visited);
                    if (visited >= MaxElementsPerPass) break;
                }
            }
            catch (Exception ex) { Faults.Say("UIToolkit.Targets", ex); }

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
            catch (Exception ex) { Faults.Say("UIToolkit.CollectFrom", ex); }

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
            foreach (long id in new List<long>(_byId.Keys))
            {
                var element = ElementFor(id);
                if (element == null) continue;

                try
                {
                    if (_originalText.TryGet(element, out var original)
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
                catch (Exception ex) { Faults.Say("UIToolkit.RestoreAll", ex); }
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
            if (!_originalFontName.TryGet(element, out var settingsName)) return;

            var resolved = _resolvedStyleProp?.GetValue(element, null);
            if (resolved == null) return;

            var currentFont = CurrentFontOf(element, resolved, out _);
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
                _originalFontName.TryGet(element, out string font);
                FontOverrideRule rule = null;
                if (TranslatorCore.FontOverrides.Count > 0)
                    rule = TranslatorCore.FindFontOverride(IdFor(element), PathOf(element), font, text);  // the text as written, before routing
                TextShaping.RtlPresenter.Present(element, IdFor(element), ref value, font, rule);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.WriteRouted", ex); }
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
                _written.Set(element, text);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.WriteBack", ex); }
        }

        #endregion

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

        /// <summary>
        /// One UI Toolkit pass, SPREAD over frames like the component scan next door.
        ///
        /// 🔴 It used to walk every element of every document in one go, bounded only by a count
        /// (6000). Measured on a real save: ~1300 elements, **45 ms in a single frame, once a
        /// second** — three frames' worth of work at 60 fps, felt as a periodic stutter. Almost
        /// none of it is the text: 6 ms for 6495 elements. It is the walk itself and the per-
        /// element font/picture questions, each a handful of reflection calls.
        ///
        /// So the walk now takes the SAME per-frame budget the scanner computes from the game's
        /// own frame-time noise, and keeps its stack between frames: nothing is skipped, each
        /// element is simply reached a frame or two later. A cycle starts on the configured
        /// interval; while one is still running, the interval is not consulted — finishing the
        /// pass in hand comes first.
        /// </summary>
        public static void Scan(float budgetMs, System.Diagnostics.Stopwatch frameSw)
        {
            if (!Available) return;

            long tScan = Perf.Start();
            try
            {
                if (_walk.Count == 0)
                {
                    // Same cadence as the rest of the scanner: how long a newly shown string may
                    // stay untranslated is one setting, not one per subsystem.
                    float interval = TranslatorCore.Config?.max_text_detection_latency_seconds ?? 1f;
                    if (interval < 0.1f) interval = 0.1f;

                    float now = Time.realtimeSinceStartup;
                    if (_lastScanTime != 0f && now - _lastScanTime < interval) return;
                    _lastScanTime = now;

                    long tCycle = Perf.Start();
                    bool opened = StartWalkCycle();
                    Perf.Stop(Perf.UitkCycle, tCycle);
                    if (!opened) return;
                }

                // Walk until the frame's budget is spent; the stack holds the rest.
                while (_walk.Count > 0)
                {
                    if (frameSw != null && frameSw.Elapsed.TotalMilliseconds > budgetMs) return;

                    var element = _walk.Pop();
                    if (element == null) continue;

                    // ⚠ Children first: the walk resumes from the stack, so an element that throws
                    // below is skipped ALONE — its subtree is already on the stack. Pushed after
                    // it, a subtree under one bad element was never reached, at any pass.
                    long tChildren = Perf.Start();
                    int count = ChildCount(element);
                    for (int i = 0; i < count; i++)
                    {
                        var child = ChildAt(element, i);
                        if (child != null) _walk.Push(child);
                    }
                    Perf.Stop(Perf.UitkChildren, tChildren);

                    ReportProxyIdentityOnce(element);

                    // Pictures are not text and do not depend on the font gate: ANY element can
                    // carry one, and most that do carry no text. Asked of the text elements only,
                    // a picture on a plain VisualElement was never replaced (bench 2026-10-04).
                    long tImage = Perf.Start();
                    HandleImage(element);
                    Perf.Stop(Perf.UitkImage, tImage);

                    var asText = AsTextElement(element);
                    if (asText != null) ProcessElement(asText);
                }
            }
            catch (Exception ex)
            {
                // 🔴 Only the element that threw is lost: the stack keeps the rest of the pass, which
                // goes on at the next frame. Clearing it here dropped every element after the bad
                // one, at every pass — the same one threw first each time.
                Faults.Say("UIToolkitSupport.Scan", ex, "one element skipped");
            }
            finally { Perf.Stop(Perf.UitkScan, tScan); }
        }

        /// <summary>
        /// Open a cycle: sweep recycled elements, then load every document's root onto the walk
        /// stack. False when there is nothing to walk.
        /// </summary>
        private static bool StartWalkCycle()
        {
            // Elements recycled since the last pass: drop their ids and the routing state
            // behind them. Here because this is the pass that knows a scroll has happened.
            Sweep();

            // Documents alive before our patch never fire OnEnable for us: taken once. Without
            // the patch, the lookup remains the only source and runs every cycle, as before.
            if (!_documentsPrimed || !_documentsFromEvents)
            {
                _documentsPrimed = true;
                PrimeDocumentsFromLookup();
            }

            _deadDocuments.Clear();
            foreach (var entry in _documents)
            {
                var document = entry.Value;
                if (!TypeHelper.IsUnityObjectAlive(document)) { _deadDocuments.Add(entry.Key); continue; }

                // A disabled document has nothing on screen; its root would be walked for nothing.
                if (document is UnityEngine.Behaviour b && !b.isActiveAndEnabled) continue;

                object root = null;
                try { root = _rootProp.GetValue(document, null); }
                catch (Exception ex) { Faults.Say("UIToolkit.StartWalkCycle", ex); }
                if (root != null) _walk.Push(root);
            }
            foreach (int id in _deadDocuments) _documents.Remove(id);
            return _walk.Count > 0;
        }

        /// <summary>The walk in progress, kept between frames — see Scan.</summary>
        private static readonly Stack<object> _walk = new Stack<object>();

        // The documents we know of, by instance id, let go once Unity has destroyed them. ⚠ Not weak
        // references: under IL2CPP the object we hold is an interop wrapper nobody else holds, which
        // the collector takes while the document is still on screen — and a wrapper read twice is
        // two objects, so only the id says "the same document". Holding a wrapper keeps nothing of
        // the engine's alive.
        private static readonly Dictionary<int, object> _documents = new Dictionary<int, object>();
        private static readonly List<int> _deadDocuments = new List<int>();
        private static bool _documentsFromEvents;   // OnEnable patched: no per-cycle lookup needed
        private static bool _documentsPrimed;       // the one initial lookup has been done

        public static void UIDocument_OnEnable_Postfix(object __instance)
        {
            if (__instance == null) return;
            try
            {
                int id = TypeHelper.GetInstanceID(__instance);
                if (id == -1 || _documents.ContainsKey(id)) return;
                // A lookup under IL2CPP answers plain UnityEngine.Object wrappers: reading
                // rootVisualElement off one threw "Object does not match target type" (bench,
                // 2026-10-01), and every document found that way was never walked.
                _documents[id] = TypeHelper.Il2CppCast(__instance, UIDocumentType);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.UIDocument_OnEnable_Postfix", ex); }
        }

        /// <summary>Add every document the engine currently has — the one lookup we still pay.</summary>
        private static void PrimeDocumentsFromLookup()
        {
            var found = TypeHelper.FindAllObjectsOfType(UIDocumentType);
            if (found == null) return;
            foreach (var document in found)
            {
                if (document == null) continue;
                UIDocument_OnEnable_Postfix(document);
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
        /// <summary>
        /// An RTL text written before its element had a layout, waiting for the walk to reach
        /// the element on screen. Weak, like every per-element table here.
        /// </summary>
        private sealed class PendingRtl
        {
            internal string LogicalSource, Logical, Measure, Assigned;
            internal bool Mirror;
            internal int Frame;   // when the text was assigned — its layout comes at that frame's end
        }

        private static readonly ElementStore<PendingRtl> _pendingRtl = new ElementStore<PendingRtl>();

        // The fast lane: every element assigned since the last tick, looked at ONCE, the frame
        // after its assignment — the first moment its layout can be read. Laid out by then, it
        // is finished right there instead of waiting for the walk to come round (up to half a
        // second on a large document, seen as "the paragraph settles later"). Not laid out (a
        // hidden pane), it simply leaves the lane and the walk finishes it when it shows. Each
        // element costs one check here, ever: no per-frame polling of anything.
        private static readonly List<object> _pendingLane = new List<object>();

        /// <summary>Where a list of elements holds this one, -1 if nowhere — by IdFor: on IL2CPP one element comes as several wrappers.</summary>
        private static int IndexOfElement(List<object> list, object element)
        {
            long id = IdFor(element);
            for (int i = 0; i < list.Count; i++) if (IdFor(list[i]) == id) return i;
            return -1;
        }

        internal static void DeferUntilLaidOut(object element, string logicalSource, string logical,
                                               string measure, string assigned, bool mirror)
        {
            _pendingRtl.Set(element, new PendingRtl
            {
                LogicalSource = logicalSource, Logical = logical, Measure = measure,
                Assigned = assigned, Mirror = mirror, Frame = Time.frameCount,
            });
            if (IndexOfElement(_pendingLane, element) < 0) _pendingLane.Add(element);
        }

        internal static void ForgetPending(object element)
        {
            _pendingRtl.Remove(element);
            int lane = IndexOfElement(_pendingLane, element);
            if (lane >= 0) _pendingLane.RemoveAt(lane);
        }

        // The font lane: every element the setter prefix met for the FIRST time, looked at once
        // the frame after, for its font and size. The walk used to be the only place a fallback
        // font reached a fresh element — at its cadence, so every new panel showed in the game's
        // font and then re-set itself in the fallback under a second later, its lines visibly
        // moving (bench: the two lines of a description drawing closer). Only elements the font
        // pass has never seen are queued: a known element already wears what it should.
        private static readonly List<object> _fontLane = new List<object>();
        private static readonly List<int> _fontLaneFrame = new List<int>();

        private static void QueueForFont(object element)
        {
            if (_originalFontName.TryGet(element, out _)) return;
            if (IndexOfElement(_fontLane, element) >= 0) return;
            _fontLane.Add(element);
            _fontLaneFrame.Add(Time.frameCount);
        }

        /// <summary>
        /// The fast lanes, once per tick from the scanner's update pass (main thread).
        /// </summary>
        internal static void FinishRecentPending()
        {
            int frame = Time.frameCount;
            for (int i = _fontLane.Count - 1; i >= 0; i--)
            {
                if (frame <= _fontLaneFrame[i]) continue;   // its style pass has not run yet
                object element = _fontLane[i];
                _fontLane.RemoveAt(i);
                _fontLaneFrame.RemoveAt(i);
                // Not laid out yet: the walk meets it when it shows, as before.
                if (!WillBeLaidOut(element)) continue;
                try { HandleFont(element); }
                catch (Exception ex) { TranslatorCore.LogWarning("[UIToolkit] font lane failed, left to the walk: " + ex.Message); }
            }

            if (_pendingLane.Count == 0) return;
            for (int i = _pendingLane.Count - 1; i >= 0; i--)
            {
                object element = _pendingLane[i];
                if (!_pendingRtl.TryGet(element, out var pending)) { _pendingLane.RemoveAt(i); continue; }
                if (frame <= pending.Frame) continue;   // its layout has not run yet — next tick
                _pendingLane.RemoveAt(i);
                try { TryFinishPending(element, pending, "lane"); }
                catch (Exception ex) { TranslatorCore.LogWarning("[RtlPresenter] fast lane failed, left to the walk: " + ex.Message); }
            }
        }

        /// <summary>
        /// Finish a pending RTL text if its layout has run for it — see RtlPresenter. 🔴 Only
        /// then: the layout runs at the end of the frame the text was assigned in, and only over
        /// elements it will lay out. Read earlier, contentRect still holds the previous text's
        /// width — on an element sized by its content, the one width that is wrong by
        /// construction (the UI.Text lesson, §7.10).
        /// </summary>
        // ⚠ Diagnostic, behind DebugMode: which path finished each element and how
        // many frames after its assignment — the user sees "a jump under a second" and the two
        // paths differ by exactly that. Once per element and outcome, never a count.

        private static void TryFinishPending(object element, PendingRtl pending, string via)
        {
            if (Time.frameCount <= pending.Frame) return;
            if (!WillBeLaidOut(element))
            {
                if (TranslatorCore.DebugMode && DiagnosticOnce.First("UITK.finish", PathOf(element) + "\u0001not laid out"))
                {
                    TranslatorCore.LogDebug($"[RtlPresenter] uitk {via}: not laid out yet ({(IsElementAttached(element) ? "display:none somewhere above" : "not attached")}) frames={Time.frameCount - pending.Frame} '{PathOf(element)}'");
                }
                return;
            }
            // 🔴 The font FIRST, and the lines only once it shows. The lines are measured with
            // the element's current metrics; a fallback font written now lands at the next
            // panel update, and a line cut in the game's font but shown in the fallback would
            // overflow its box under NoWrap. So a write here means: same element, next tick.
            _fontStyleWritten = false;
            HandleFont(element);
            if (_fontStyleWritten)
            {
                pending.Frame = Time.frameCount;
                if (IndexOfElement(_pendingLane, element) < 0) _pendingLane.Add(element);
                return;
            }
            // The alignment is mirrored HERE and not at set_text: it is computed from the
            // RESOLVED style, which only exists once the panel has styled the element — read
            // earlier it is the default UpperLeft, and every centred label ended top-right.
            MirrorAlign(element, pending.Mirror);
            bool done = TextShaping.RtlPresenter.FinishUiToolkitPending(element, pending.LogicalSource,
                            pending.Logical, pending.Measure, pending.Assigned);
            if (TranslatorCore.DebugMode && DiagnosticOnce.First("UITK.finish", PathOf(element) + "\u0001" + done + "\u0001" + pending.Logical))
            {
                TranslatorCore.LogDebug($"[RtlPresenter] uitk {via}: {(done ? "finished" : "no width yet")} frames={Time.frameCount - pending.Frame} '{PathOf(element)}'");
            }
            if (done) _pendingRtl.Remove(element);
        }

        private static void ProcessElement(object element)
        {
            // An RTL text the fast lane could not finish (see above): the walk is here because
            // the element is attached, and if its layout has run by now, that is the moment.
            if (_pendingRtl.TryGet(element, out var pending))
                TryFinishPending(element, pending, "walk");

            // Its picture was asked about by the walk, as every element's is (Scan).

            // Also the switch for "translate this font or not", so it is asked every pass.
            if (!HandleFont(element)) return;

            TranslateElement(element);
        }

        private static void TranslateElement(object element)
        {
            long tElement = Perf.Start();
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
                if (_written.TryGet(element, out var mine) && mine == current) return;

                if (IsEchoOfTyping(element, current)) return;

                string translated = current;
                TranslatorPatches.RouteText(element, element, IdFor(element),
                                            isOwnUI: false, componentType: "UIToolkit", textValue: ref translated);
                // Stage D, same as the setter path — the scan is the other way text reaches a
                // UI Toolkit screen. Same catch-up: override rules match here too.
                _originalFontName.TryGet(element, out string scanFont);
                FontOverrideRule scanOverride = null;
                if (TranslatorCore.FontOverrides.Count > 0)
                    scanOverride = TranslatorCore.FindFontOverride(IdFor(element), PathOf(element), scanFont, current);  // the game's text, not ours
                TextShaping.RtlPresenter.Present(element, IdFor(element), ref translated, scanFont, scanOverride);
                if (string.IsNullOrEmpty(translated) || translated == current) return;

                RememberOriginal(element, current);

                _writingBack = true;
                try { _textProp.SetValue(element, translated, null); }
                finally { _writingBack = false; }

                _written.Remove(element);
                _written.Set(element, translated);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.TranslateElement", ex); }
            finally { Perf.Stop(Perf.UitkElement, tElement); }
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

                // Said for what it is: how the walk meets elements. Per-element state does not rest
                // on it — on IL2CPP an element is known by its native object (IdFor), because a
                // hook's wrapper is new at every call even where the walk's are kept.
                TranslatorCore.LogInfo(stable
                    ? "[UIToolkit] The tree walk meets each element as the same object."
                    : $"[UIToolkit] The tree walk meets each element as a new object ({(IsInteropWrapper(first) ? "known by its native object" : "🔴 and nothing else tells them apart: per-element state will repeat")}).");
            }
            catch (Exception ex) { Faults.Say("UIToolkit.ReportProxyIdentityOnce", ex); }
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
            catch (Exception ex) { Faults.Say("UIToolkit.AsTextElement", ex); return null; }
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
            catch (Exception ex) { Faults.Say("UIToolkit.ChildCount", ex); }

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
            catch (Exception ex) { Faults.Say("UIToolkit.ChildAt", ex); }

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
        private static readonly ElementStore<string> _originalFontName = new ElementStore<string>();

        /// <summary>
        /// The font OBJECT each element started with, kept so it can be put back.
        ///
        /// 🔴 The name is not enough to restore. Once an inline style carries our replacement,
        /// clearing the fallback in the Fonts tab has to write something — and the only thing that
        /// puts the element back exactly as it was is the object it had. This is what
        /// FontManager.RestoreOriginalFont does per component, with `_originalFontsPerComponent`.
        /// </summary>
        private static readonly ElementStore<object> _originalFontObject = new ElementStore<object>();

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
        // Set by every inline font/size write below, so a caller that measures text right after
        // HandleFont can know the metrics it would measure with are not the ones about to show:
        // an inline style lands in the resolved style at the next panel update, not at the write.
        private static bool _fontStyleWritten;

        private static bool HandleFont(object element)
        {
            if (!CanSetFont) return true;

            long tPerf = Perf.Start();
            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                if (resolved == null)
                {
                    if (DiagnosticOnce.First("UIToolkit.noResolvedStyle", element.GetType().FullName))
                        TranslatorCore.LogWarning($"[UIToolkit] {element.GetType().Name}: no resolved style — its font cannot be read, so it is not replaced");
                    return true;
                }

                var currentFont = CurrentFontOf(element, resolved, out bool isSdf);
                if (currentFont == null || string.IsNullOrEmpty(currentFont.name))
                {
                    // 🔴 Said, not skipped in silence: on a game where this read fails, no element is
                    // ever registered, replaced or highlighted, and nothing else in the log says why
                    // (2026-10-02, a UI Toolkit game on Unity 6000.5: every font "0 in scene").
                    if (DiagnosticOnce.First("UIToolkit.noFont", element.GetType().FullName))
                        TranslatorCore.LogWarning($"[UIToolkit] {element.GetType().Name}: its font cannot be read — {DescribeFontRead(element, resolved)}");
                    return true;
                }

                if (!_originalFontName.TryGet(element, out var settingsName))
                {
                    settingsName = currentFont.name;
                    _originalFontName.Set(element, settingsName);
                    _originalFontObject.Set(element, currentFont);

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
                    // No text: what the element shows here may be our translation. The answer the
                    // text write took on the game's words is kept (FontRules.Find, null text).
                    var rule = TranslatorCore.FindFontOverride(IdFor(element), PathOf(element), settingsName, null);
                    if (rule != null && !string.IsNullOrEmpty(rule.replacement))
                        replacementName = rule.replacement;
                }

                ApplyReplacement(element, replacementName, currentFont, isSdf);
                ApplyScale(element, settingsName);
                return true;
            }
            catch (Exception ex)
            {
                Faults.Say("UIToolkit.HandleFont", ex);
                return true;
            }
            finally { Perf.Stop(Perf.UitkFont, tPerf); }
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

            object definition = BuildDefinition(replacement, isSdf, settingsName);
            if (definition == null) return;

            var styleValue = Activator.CreateInstance(_styleFontDefinitionType, definition);
            var style = _styleProp.GetValue(element, null);
            if (style == null) return;

            _styleFontProp.SetValue(style, styleValue, null);
            _fontStyleWritten = true;
            ShapeAgainForNewFont(element, settingsName);

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
            if (!_originalFontObject.TryGet(element, out var original) || original == null) return;

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
                _fontStyleWritten = true;
                ShapeAgainForNewFont(element, settingsName);

                if (_restoreLogged.Add(settingsName))
                    TranslatorCore.LogInfo($"[UIToolkit] Font restored: back to {settingsName}");
            }
            catch (Exception ex) { Faults.Say("UIToolkit.RestoreOriginalFont", ex); }
        }

        private static readonly HashSet<string> _restoreLogged = new HashSet<string>();

        /// <summary>
        /// An element whose font just changed, showing a text WE shaped: shaped again, for the font
        /// it wears now.
        ///
        /// 🔴 A shaped text is written in the private names of ONE font's derived copy. The element
        /// kept the names of the copy it was shaped for while it drew with the new one: Tahoma Bold
        /// changed to Tahoma left every shaped glyph a box, the plain letters drawn (2026-10-02).
        /// The presenter already shapes its own output again when the font it was made for is no
        /// longer the one drawing (PresentSyllabic) — it is asked here, as a write would ask it.
        /// A text of the game's (not presented by us) is left to its next write.
        /// </summary>
        private static void ShapeAgainForNewFont(object element, string settingsName)
        {
            if (_textProp == null) return;
            string current;
            try { current = _textProp.GetValue(element, null) as string; }
            catch (Exception ex) { Faults.Say("UIToolkit.ShapeAgainForNewFont read", ex); return; }
            if (string.IsNullOrEmpty(current) || TranslatorCore.TryGetPresentedLogical(current) == null) return;

            string value = current;
            FontOverrideRule rule = TranslatorCore.FontOverrides.Count > 0
                ? TranslatorCore.FindFontOverride(IdFor(element), PathOf(element), settingsName, null) : null;
            TextShaping.RtlPresenter.Present(element, IdFor(element), ref value, settingsName, rule);
            if (!string.Equals(value, current, StringComparison.Ordinal)) WriteBack(element, value);
        }

        /// <summary>Elements whose original size we hold, so a scale can be undone. [0] = the size (float).
        /// An ElementStore: the font lane hands over the set_text hook's wrapper, another one at each
        /// call on IL2CPP — kept by the wrapper, the scaled size was taken for the original.</summary>
        private static readonly ElementStore<object[]> _originalFontSize = new ElementStore<object[]>();

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
            if (_styleFontSizeProp == null || _resolvedFontSize == null || !_resolvedFontSize.CanRead
                || _styleLengthType == null) return;

            try
            {
                var resolved = _resolvedStyleProp.GetValue(element, null);
                if (resolved == null) return;

                object read = _resolvedFontSize.Get(element, resolved);
                if (!(read is float currentSize) || currentSize <= 0f)
                {
                    if (TranslatorCore.DebugMode && DiagnosticOnce.First("UITK.scale.unread", settingsName + "\u0001" + (read?.GetType().Name ?? "null")))
                        TranslatorCore.LogDebug($"[UIToolkit] size for '{settingsName}' not scaled: its resolved size reads {(read == null ? "nothing" : $"{read.GetType().Name} {read}")}");
                    return;
                }

                float original;
                if (_originalFontSize.TryGet(element, out var stored) && stored[0] is float kept)
                {
                    original = kept;
                }
                else
                {
                    original = currentSize;
                    _originalFontSize.Set(element, new object[] { original });
                }

                float scale = FontManager.GetFontScale(settingsName);
                float wanted = original * scale;
                if (TranslatorCore.DebugMode && DiagnosticOnce.First("UITK.scale", settingsName + "\u0001" + scale + "\u0001" + original + "\u0001" + currentSize))
                    TranslatorCore.LogDebug($"[UIToolkit] size for '{settingsName}' on {PathOf(element)}: started {original}, reads {currentSize}, ×{scale} → {wanted}");

                // Below what the eye or the layout can tell apart — writing it would cost a style
                // resolution every pass for nothing.
                if (Math.Abs(wanted - currentSize) < 0.1f) return;

                var style = _styleProp.GetValue(element, null);
                if (style == null) return;

                _styleFontSizeProp.SetValue(
                    style, Activator.CreateInstance(_styleLengthType, wanted), null);
                _fontStyleWritten = true;

                if (!_scaleDiagnosed && Math.Abs(scale - 1f) > 0.001f)
                {
                    _scaleDiagnosed = true;
                    TranslatorCore.LogInfo(
                        $"[UIToolkit] Font size scaled for '{settingsName}': ×{scale:F2} "
                        + $"({original:F1} -> {wanted:F1})");
                }
            }
            catch (Exception ex) { Faults.Say("UIToolkit.ApplyScale", ex); }
        }

        /// <summary>
        /// The font in force on an element, from either of the two ways UI Toolkit states one.
        ///
        /// ⚠ `unityFont` first because it is the cheaper read, but it is null on any modern build:
        /// UI Toolkit states its font as a TextCore FontAsset, and `unityFontDefinition` is where
        /// that lives. Both are UnityEngine.Objects, so the caller only needs the name.
        /// </summary>
        private static UnityEngine.Object ReadResolvedFont(object element, object resolvedStyle, out bool isSdf)
        {
            isSdf = false;

            try
            {
                if (_resolvedFont != null && _resolvedFont.Get(element, resolvedStyle) is Font legacy && legacy != null)
                {
                    return legacy;
                }
            }
            catch (Exception ex) { Faults.Say("UIToolkit.ReadResolvedFont legacy font", ex); }

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
            catch (Exception ex) { Faults.Say("UIToolkit.ReadResolvedFont font definition", ex); }

            return null;
        }

        /// <summary>
        /// The font an element draws with: the one its resolved style names, else the default font of
        /// its document's panel text settings.
        ///
        /// 🔴 **A style that names no font is not a text with no font.** A game can set its fonts
        /// once, as the default of its PanelSettings' text settings, and never in a style sheet: every
        /// element then resolves an empty FontDefinition (no font, no asset) while drawing that default.
        /// Read from the style alone, such a game showed every font "0 in scene", lit nothing in the
        /// highlight and replaced nothing (2026-10-02, Unity 6000.5: "unityFont: null;
        /// unityFontDefinition: FontDefinition (fontAsset: null, font: null)" on every element).
        /// An inline font written on the element still wins over that default: replacing works the same.
        /// </summary>
        private static UnityEngine.Object CurrentFontOf(object element, object resolvedStyle, out bool isSdf)
        {
            var font = ReadResolvedFont(element, resolvedStyle, out isSdf);
            if (font != null && !string.IsNullOrEmpty(font.name)) return font;

            var panelDefault = PanelDefaultFont(element);
            if (panelDefault == null) return null;
            isSdf = true;   // a text settings' default is a TextCore font asset
            return panelDefault;
        }

        /// <summary>The default font of the text settings of the document holding this element; null when none is set or readable.</summary>
        private static UnityEngine.Object PanelDefaultFont(object element)
        {
            if (_parentProp == null || _rootProp == null || _documents.Count == 0) return null;

            // Climbed to the first ancestor that is a document's root: documents can share a panel,
            // so the panel's own top is not the document's.
            var roots = new List<KeyValuePair<object, int>>(_documents.Count);
            foreach (var entry in _documents)
            {
                object root = null;
                try { root = _rootProp.GetValue(entry.Value, null); }
                catch (Exception ex) { Faults.Say("UIToolkit.PanelDefaultFont root", ex); }
                if (root != null) roots.Add(new KeyValuePair<object, int>(root, entry.Key));
            }

            object at = element;
            while (at != null)
            {
                foreach (var r in roots)
                    if (ReferenceEquals(r.Key, at)) return DefaultFontOfDocument(r.Value);
                try { at = _parentProp.GetValue(at, null); }
                catch (Exception ex) { Faults.Say("UIToolkit.PanelDefaultFont parent", ex); return null; }
            }
            return null;
        }

        // A document's default font, read once (the panel settings of a document do not change under it).
        private static readonly Dictionary<int, UnityEngine.Object> _documentDefaultFont = new Dictionary<int, UnityEngine.Object>();

        private static UnityEngine.Object DefaultFontOfDocument(int documentId)
        {
            if (_documentDefaultFont.TryGetValue(documentId, out var known)) return known;
            if (!_documents.TryGetValue(documentId, out var document)) return null;

            // ⚠ A property OR a field: PanelSettings.textSettings is a public FIELD (Unity 6000.5),
            // and a property lookup alone found nothing there.
            object Member(object on, string name)
            {
                var type = on.GetType();
                var p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return p.GetValue(on, null);
                return type.GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(on);
            }

            UnityEngine.Object found = null;
            string step = "panelSettings";
            try
            {
                object settings = Member(document, "panelSettings");
                if (settings != null)
                {
                    step = "textSettings";
                    object text = Member(settings, "textSettings");
                    if (text != null)
                    {
                        step = "defaultFontAsset";
                        found = Member(text, "defaultFontAsset") as UnityEngine.Object;
                    }
                }
            }
            catch (Exception ex) { Faults.Say("UIToolkit.DefaultFontOfDocument", ex, step); }

            _documentDefaultFont[documentId] = found;
            TranslatorCore.LogInfo(found != null
                ? $"[UIToolkit] document {documentId}: panel text settings default font '{found.name}' — elements whose style names no font draw with it"
                : $"[UIToolkit] document {documentId}: no default font readable from its panel text settings (stopped at {step})");
            return found;
        }

        /// <summary>What each member <see cref="ReadResolvedFont"/> asks gave back, for the line saying why it found nothing.</summary>
        private static string DescribeFontRead(object element, object resolvedStyle)
        {
            object definition = null;
            try { definition = _resolvedFontDefProp?.GetValue(resolvedStyle, null); }
            catch (Exception ex) { Faults.Say("UIToolkit.DescribeFontRead", ex); }
            string legacy = _resolvedFont == null || !_resolvedFont.CanRead ? "member absent"
                : DescribeRead(() => _resolvedFont.Get(element, resolvedStyle));
            return $"unityFont: {legacy}; unityFontDefinition: {DescribeMember(_resolvedFontDefProp, resolvedStyle)}"
                 + (definition == null ? "" : $" (fontAsset: {DescribeMember(_fontDefAssetProp, definition)}, font: {DescribeMember(_fontDefFontProp, definition)})");
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
        /// <summary>
        /// Keeps a font asset we made — and its atlas textures and material — out of the game's
        /// Resources.UnloadUnusedAssets.
        ///
        /// 🔴 Nothing the game serialises refers to them, so an unload pass destroys them: the texts
        /// drawn with one showed for a fraction of a second, then vanished (2026-10-02, a UI Toolkit
        /// game unloading assets as its menu opened). TextCore marks its own runtime assets the same
        /// way (FontAssetFactory.SetHideFlags: DontSave on asset, atlas and material); this mod's other
        /// paths use DontUnloadUnusedAsset (CustomFontLoader).
        /// </summary>
        private static void ShieldFromUnload(object asset)
        {
            if (!(asset is UnityEngine.Object o) || o == null) return;
            o.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            try
            {
                var type = asset.GetType();
                if (type.GetProperty("material", BindingFlags.Public | BindingFlags.Instance)?.GetValue(asset, null) is UnityEngine.Object material && material != null)
                    material.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                if (type.GetProperty("atlasTextures", BindingFlags.Public | BindingFlags.Instance)?.GetValue(asset, null) is System.Collections.IEnumerable textures)
                    foreach (var t in textures)
                        if (t is UnityEngine.Object tex && tex != null) tex.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            }
            catch (Exception ex) { Faults.Say("UIToolkit.ShieldFromUnload", ex, o.name); }
        }

        /// <summary>The SDF asset made from the derived copy's file, asked at once for a Latin letter and some of the copy's private names (what a shaped text is written in).</summary>
        private static object CreateFromDerivedFile(DerivedFonts.Entry derived, string name)
        {
            var probe = new System.Text.StringBuilder("A");
            var added = derived.Namer?.Added;
            if (added != null)
                for (int i = 0; i < added.Count && i < 16; i++)
                    if (added[i].Codepoint <= 0xFFFF) probe.Append((char)added[i].Codepoint);
            return FontManager.CreateSdfFontAssetFromFile(derived.CurrentFile, _textCoreFontAssetType, name, probe.ToString());
        }

        private static object BuildDefinition(Font replacement, bool isSdf, string settingsName)
        {
            if (isSdf && _textCoreFontAssetType != null && _fromSdfFontMethod != null)
            {
                string key = replacement.name ?? "?";

                // A cached asset the engine destroyed since is made again, and said: the shield
                // below should prevent it, and a text drawn with a dead asset shows nothing.
                if (_sdfCache.TryGetValue(key, out var cached) && cached != null && !TypeHelper.IsUnityObjectAlive(cached))
                {
                    _sdfCache.Remove(key);
                    if (DiagnosticOnce.First("UIToolkit.sdfDestroyed", key))
                        TranslatorCore.LogWarning($"[UIToolkit] the SDF asset made for '{key}' was destroyed by the engine — made again");
                }

                if (!_sdfCache.TryGetValue(key, out var asset))
                {
                    // ⚠ FontManager's creators, not new ones here — ALL of them, in its order. The
                    // first hands the engine a Font; when that comes back null (a dynamic OS font
                    // with no usable data) the second asks by family name and often succeeds on the
                    // very same font. Using only the first is what made some fonts work and others
                    // not: Ebrima and Lato went through, Liberation Sans and the Adobe faces did not.
                    // The derived copy's FILE comes FIRST when the replacement is that copy
                    // (DerivedForSettings: GetUnityReplacementFont's own conditions): a copy is known
                    // by no family the system lists and its OS Font has no data, so both others fail
                    // on it by construction (FontManager.CreateSdfFontAssetFromFile) — tried first,
                    // they filled every Unity 6 log with four warnings for a font that then drew
                    // fine. Named as the Font it stands for, so the element wearing it is recognised
                    // as wearing the replacement.
                    var derived = FontManager.DerivedForSettings(settingsName);
                    asset = derived != null
                        ? CreateFromDerivedFile(derived, replacement.name)
                          ?? FontManager.CreateSdfFontAsset(replacement, _textCoreFontAssetType)
                          ?? FontManager.CreateSdfFontAssetByFamily(replacement, _textCoreFontAssetType)
                        : FontManager.CreateSdfFontAsset(replacement, _textCoreFontAssetType)
                          ?? FontManager.CreateSdfFontAssetByFamily(replacement, _textCoreFontAssetType);

                    _sdfCache[key] = asset;
                    ShieldFromUnload(asset);

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
        private static readonly ElementStore<object> _highlightOriginalColor = new ElementStore<object>();

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
        /// <summary>
        /// Adds the UI Toolkit text elements on screen to the Fonts tab's "N in scene", by the font
        /// each is filed under (the one it started with). False when no document could be walked.
        ///
        /// ⚠ The count walked only TMP and uGUI components: a game drawn in UI Toolkit showed
        /// "0 in scene" on every font while the highlight lit its texts (2026-10-02).
        /// </summary>
        internal static bool CountFontsInto(Dictionary<string, int> counts)
        {
            if (!Available || _rootProp == null) return false;
            bool walkedAny = false;
            try
            {
                var documents = TypeHelper.FindAllObjectsOfType(UIDocumentType);
                if (documents == null) return false;
                foreach (var document in documents)
                {
                    if (document == null) continue;
                    object root = null;
                    try { root = _rootProp.GetValue(document, null); }
                    catch (Exception ex) { Faults.Say("UIToolkit.CountFontsInto", ex); }
                    if (root == null) continue;
                    walkedAny = true;
                    Walk(root, MaxElementsPerPass, element =>
                    {
                        string name = SettingsFontNameOf(element);
                        if (string.IsNullOrEmpty(name)) return;
                        counts.TryGetValue(name, out int n);
                        counts[name] = n + 1;
                    });
                }
            }
            catch (Exception ex) { Faults.Say("UIToolkit.CountFontsInto", ex); }
            return walkedAny;
        }

        public static int HighlightFont(string fontName, Color highlight, Color dim, out int replaced)
        {
            int matched = 0;
            int wearing = 0;
            replaced = 0;

            if (!Available || _styleColorProp == null || _styleColorType == null)
            {
                // A highlight that lights nothing must say why (asked by a click: said each time).
                if (Available)
                    TranslatorCore.LogWarning($"[UIToolkit] highlight unavailable — style colour member {(_styleColorProp == null ? "absent" : "found")}, StyleColor type {(_styleColorType == null ? "absent" : "found")}");
                return 0;
            }

            ClearHighlight();
            int documentCount = 0, rootCount = 0, walked = 0, unnamed = 0;

            try
            {
                var documents = TypeHelper.FindAllObjectsOfType(UIDocumentType);
                if (documents == null) return 0;

                foreach (var document in documents)
                {
                    if (document == null) continue;
                    documentCount++;

                    object root = null;
                    try { root = _rootProp.GetValue(document, null); }
                    catch (Exception ex) { Faults.Say("UIToolkit.HighlightFont", ex); }
                    if (root == null) continue;
                    rootCount++;

                    Walk(root, MaxElementsPerPass, element =>
                    {
                        walked++;
                        if (string.IsNullOrEmpty(SettingsFontNameOf(element))) unnamed++;
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

            // What the walk met, beside the audit's counts: "0 component(s)" alone cannot tell no
            // document, no element and elements whose font could not be read apart.
            TranslatorCore.LogInfo($"[UIToolkit] highlight '{fontName}': {documentCount} document(s), {rootCount} with a root, {walked} element(s) walked, {unnamed} with no readable font");

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

                return CurrentFontOf(element, resolved, out _)?.name;
            }
            catch (Exception ex) { Faults.Say("UIToolkit.ResolvedFontNameOf", ex); return null; }
        }

        public static void ClearHighlight()
        {
            if (_highlighted.Count == 0) return;

            foreach (var element in _highlighted)
            {
                try
                {
                    if (_highlightOriginalColor.TryGet(element, out var stored)
                        && stored is Color original)
                    {
                        SetColour(element, original);
                    }
                }
                catch (Exception ex) { Faults.Say("UIToolkit.ClearHighlight", ex); }
            }

            _highlighted.Clear();
        }

        /// <summary>
        /// A field's text element showing what was typed: aligned as the game's texts of its font are
        /// (RtlPresenter.AlignTypedLabel), with the font it is filed under and the rule matching it.
        /// </summary>
        /// <returns>false when the field is not styled yet: the caller asks again at its next
        /// UpdateVisibleText (RtlInputFields keeps it, by the object's native key on IL2CPP).</returns>
        internal static bool AlignTypedField(object element, string typed, long key)
        {
            // 🔴 Only once the panel has styled and laid it out: the alignment is mirrored from the
            // RESOLVED style, the engine default before that (see TryFinishPending).
            bool attached = IsElementAttached(element);
            float width = ContentWidth(element);
            if (TranslatorCore.DebugMode && DiagnosticOnce.First("UITK.typedAlign", key + "\u0001" + attached + "\u0001" + (width > 0f)))
                TranslatorCore.LogDebug($"[RtlInputFields] UI Toolkit field {key}: attached {attached}, width {width} — alignment {(attached && width > 0f ? "decided now" : "waits for its box")}");
            if (!attached || !(width > 0f)) return false;   // NaN: no box yet
            string font = SettingsFontNameOf(element);
            FontOverrideRule rule = TranslatorCore.FontOverrides.Count > 0
                ? TranslatorCore.FindFontOverride(IdFor(element), PathOf(element), font, null) : null;
            TextShaping.RtlPresenter.AlignTypedLabel(element, typed, font, rule, false);
            if (TranslatorCore.DebugMode && _resolvedStyleProp != null && CanReadResolvedTextAlign
                && DiagnosticOnce.First("UITK.typedAlignResult", key + "\u0001" + typed))
                TranslatorCore.LogDebug($"[RtlInputFields] UI Toolkit field {key} aligned for its text: inline {StyleGet(_styleTextAlignProp, _styleProp.GetValue(element, null))}, resolved {ResolvedTextAlign(element, _resolvedStyleProp.GetValue(element, null))}");
            return true;
        }

        /// <summary>The font this element is filed under: the one it had before any replacement.</summary>
        private static string SettingsFontNameOf(object element)
        {
            if (_originalFontName.TryGet(element, out var recorded)) return recorded;

            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                if (resolved == null) return null;

                return CurrentFontOf(element, resolved, out _)?.name;
            }
            catch (Exception ex) { Faults.Say("UIToolkit.SettingsFontNameOf", ex); return null; }
        }

        private static void RememberColour(object element)
        {
            if (_highlightOriginalColor.TryGet(element, out _)) return;

            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                if (resolved == null || _resolvedColorProp == null) return;

                if (_resolvedColorProp.GetValue(resolved, null) is Color current)
                    _highlightOriginalColor.Set(element, current);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.RememberColour", ex); }
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
            catch (Exception ex) { Faults.Say("UIToolkit.SetColour", ex); }
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
        private static MethodInfo _measureElementText;       // 2021: TextUtilities.MeasureVisualElementTextSize(VisualElement, string, …, ITextHandle)
        private static object _measureUndefined;             // MeasureMode.Undefined, boxed once
        private static PropertyInfo _contentRectProp;        // VisualElement.contentRect -> Rect
        private static ResolvedRead _resolvedWhiteSpace;     // resolvedStyle.whiteSpace (computed)
        private static PropertyInfo _styleTextAlignProp;     // IStyle.unityTextAlign    (inline)
        private static PropertyInfo _styleWhiteSpaceProp;    // IStyle.whiteSpace        (inline)
        private static ResolvedRead _resolvedTextAlign;      // resolvedStyle.unityTextAlign
        private static PropertyInfo _resolvedTextGenProp;    // resolvedStyle.unityTextGenerator (Unity 6+, else null)
        private static MethodInfo _atgEnabledForElement;      // TextUtilities.IsAdvancedTextEnabledForElement — the engine's own answer
        private static PropertyInfo _resolvedDisplayProp;    // resolvedStyle.display

        // The INLINE style values an element wore before our adjustments — restored verbatim
        // when its text goes back to LTR, so an element that never had an inline value gets its
        // "unset" keyword back, not a frozen copy of what the stylesheet computed that day.
        // Kept in the ElementStores below.

        private static bool CanReadResolvedTextAlign => _resolvedTextAlign != null && _resolvedTextAlign.CanRead;

        private static object ResolvedWhiteSpace(object element, object resolved) => _resolvedWhiteSpace?.Get(element, resolved);
        private static object ResolvedTextAlign(object element, object resolved) => _resolvedTextAlign?.Get(element, resolved);

        // [0] = inline unityTextAlign, [1] = the RESOLVED original the mirror is computed from.
        private static readonly ElementStore<object[]> _rtlAlignOriginal = new ElementStore<object[]>();
        // [0] = inline whiteSpace, before OUR NoWrap (DisableWrap).
        private static readonly ElementStore<object[]> _rtlWrapOriginal = new ElementStore<object[]>();
        // Same content, for an element whose wrap was just PUT BACK for a new measurement
        // (RestoreWrap): until DisableWrap takes it again, its resolved style may still read our
        // NoWrap — a resolved style is recomputed at the next layout, not at the write — so
        // TryBreakLines must measure against the width rather than trust that shortcut.
        private static readonly ElementStore<object[]> _rtlWrapRestoring = new ElementStore<object[]>();
        // [0] = VisualElement.languageDirection before OUR right-to-left (SetRtlDirection).
        private static readonly ElementStore<object[]> _rtlDirectionOriginal = new ElementStore<object[]>();
        private static PropertyInfo _languageDirectionProp;  // VisualElement.languageDirection (Unity 6+, else null)

        private static void EnsureRtlPlumbing()
        {
            if (_rtlPlumbingResolved || !Available) return;
            _rtlPlumbingResolved = true;
            var pubInst = BindingFlags.Public | BindingFlags.Instance;

            // 🔴 Never GetMethod(name, flags) nor GetProperty(name, flags) here: Unity 6 ships TWO
            // public MeasureTextSize overloads, and a name lookup that meets two members throws
            // AmbiguousMatchException — behind a shared try block that one throw read as "no
            // measure API, no styles, no ATG detection" on a runtime that has all of them, with a
            // log line blaming the runtime (Timberborn crash analysis, §7.8). The members are
            // walked instead (Engine/Members for properties): nothing here throws, nothing caught.
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
            // The same measure for an element that is no TextElement but draws text with a handle of
            // its own — 2021's text field input (RtlInputFields.UIToolkit2021): what MeasureTextSize
            // itself calls there. Absent from 2022.3 on (that input holds a TextElement).
            foreach (var m in AssemblyTypes.Find("UnityEngine.UIElements.TextUtilities")?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic) ?? new MethodInfo[0])
            {
                if (m.Name != "MeasureVisualElementTextSize") continue;
                var ps = m.GetParameters();
                if (ps.Length != 7 || ps[1].ParameterType != typeof(string) || !ps[3].ParameterType.IsEnum) continue;
                _measureElementText = m;
                if (_measureUndefined == null) _measureUndefined = Enum.ToObject(ps[3].ParameterType, 0);
                break;
            }
            _contentRectProp = Members.Property(VisualElementType, "contentRect", pubInst);
            _languageDirectionProp = Members.Property(VisualElementType, "languageDirection", pubInst);

            // 🔴 On IL2CPP 2022.3 the interop's IStyle does not declare unityTextAlign (stripped): the
            // inline style keeps it as InlineStyleAccess's explicit implementation, read and written
            // there (StyleAccessor casts the style to that class, as for whiteSpace).
            var styleType = _styleProp?.PropertyType;
            var inlineType = AssemblyTypes.Find("UnityEngine.UIElements.InlineStyleAccess");
            _styleTextAlignProp = Members.Property(styleType, "unityTextAlign", pubInst) ?? ExplicitStyleProperty(inlineType, "IStyle", "unityTextAlign");
            _styleWhiteSpaceProp = Members.Property(styleType, "whiteSpace", pubInst) ?? ExplicitStyleProperty(inlineType, "IStyle", "whiteSpace");

            // The RESOLVED ones through ResolvedRead (stripped from IResolvedStyle there too). Unread,
            // a single-line NoWrap label was cut into lines by width, and no RTL text was ever
            // mirrored, on IL2CPP 2022.3.
            var resolvedType = _resolvedStyleProp?.PropertyType;
            _resolvedWhiteSpace = ResolvedMember("whiteSpace");
            _resolvedTextAlign = ResolvedMember("unityTextAlign");
            if (_resolvedStyleProp != null && !CanReadResolvedTextAlign)
                TranslatorCore.LogWarning("[UIToolkit] the resolved text alignment cannot be read on this runtime: right-to-left text keeps the game's alignment (no mirror)");
            if (_resolvedStyleProp != null && !_resolvedWhiteSpace.CanRead)
                TranslatorCore.LogWarning("[UIToolkit] the resolved white space cannot be read on this runtime: a text's lines are measured against its width, NoWrap or not");
            _resolvedTextGenProp = Members.Property(resolvedType, "unityTextGenerator", pubInst);
            _resolvedDisplayProp = Members.Property(resolvedType, "display", pubInst);

            // The generator an element REALLY uses, as the engine decides it (internal; walked by
            // name and parameter, never looked up by a name that could meet two members).
            foreach (var t in AssemblyTypes.Of(VisualElementType.Assembly))
            {
                if (t.Name != "TextUtilities") continue;
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "IsAdvancedTextEnabledForElement" || m.ReturnType != typeof(bool)) continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.IsAssignableFrom(TextElementType)) { _atgEnabledForElement = m; break; }
                }
                if (_atgEnabledForElement != null) break;
            }
        }

        /// <summary>
        /// Will the next layout pass lay this element out — and so give contentRect the width
        /// THIS text gets? Attached to a panel, and no <c>display: none</c> on it or any
        /// ancestor (Yoga skips such a subtree entirely, leaving the previous layout in place).
        /// The UI Toolkit face of RtlPresenter.WillBeRedrawn; unreadable answers true, the
        /// frame gate in ProcessElement being the other half of the wait.
        /// </summary>
        internal static bool WillBeLaidOut(object element)
        {
            if (!IsElementAttached(element)) return false;
            if (_resolvedDisplayProp == null || _resolvedStyleProp == null || _parentProp == null) return true;
            try
            {
                object current = element;
                int guard = 0;
                while (current != null && guard++ < 64)
                {
                    var resolved = _resolvedStyleProp.GetValue(current, null);
                    object display = resolved == null ? null : _resolvedDisplayProp.GetValue(resolved, null);
                    if (display != null && Enum.GetName(display.GetType(), display) == "None") return false;
                    current = _parentProp.GetValue(current, null);
                }
            }
            catch (Exception ex) { Faults.Say("UIToolkit.WillBeLaidOut", ex); }
            return true;
        }

        internal static bool IsTextElementInstance(object o)
            => Available && o != null && TextElementType.IsInstanceOfType(o);

        internal static string GetElementText(object element)
        {
            try { return _textProp?.GetValue(element, null) as string; }
            catch (Exception ex) { Faults.Say("UIToolkit.GetElementText", ex); return null; }
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
            _written.Set(element, text);
        }

        internal static bool IsElementAttached(object element)
        {
            try { return _panelProp != null && _panelProp.GetValue(element, null) != null; }
            catch (Exception ex) { Faults.Say("UIToolkit.IsElementAttached", ex); return false; }
        }

        /// <summary>
        /// True when this element renders through the Advanced Text Generator, which does bidi
        /// and shaping natively — presenting on top of it would double-process.
        ///
        /// 🔴 The engine's answer (TextUtilities.IsAdvancedTextEnabledForElement), not the style:
        /// an element whose style ASKS for the Advanced generator is drawn by the standard one
        /// when the engine does not run it (Unity 6.0–6.4 turn it on project-wide). Read from the
        /// style, such an element was left unpresented and its Arabic showed unjoined, left to right
        /// (bench, 6000.0.84f1, 2026-10-03: style Advanced, engine False). The style is read only
        /// where the engine has no such answer; anywhere neither can be read: standard generator.
        /// </summary>
        internal static bool IsAtgActive(object element)
        {
            EnsureRtlPlumbing();
            if (_atgEnabledForElement != null)
            {
                try { return (bool)_atgEnabledForElement.Invoke(null, new[] { element }); }
                catch (Exception ex) { Faults.Say("UIToolkit.IsAtgActive engine", ex.InnerException ?? ex); }
            }
            if (_resolvedTextGenProp == null) return false;
            try
            {
                var resolved = _resolvedStyleProp.GetValue(element, null);
                if (resolved == null) return false;
                object gen = _resolvedTextGenProp.GetValue(resolved, null);
                return gen != null && Enum.GetName(gen.GetType(), gen) == "Advanced";
            }
            catch (Exception ex) { Faults.Say("UIToolkit.IsAtgActive", ex); return false; }
        }

        // Underline guard plumbing — see UnderlineIsSafe. The font definition property itself is
        // the font path's _resolvedFontDefProp, resolved at init.
        private static bool _underlineSafetyResolved;
        private static PropertyInfo _fontDefFontAssetProp; // FontDefinition.fontAsset
        private static MethodInfo _hasCharactersMethod;    // FontAsset.HasCharacters(string, out uint[], bool, bool)

        /// <summary>
        /// Can THIS element draw an underline/strikethrough over THIS text without dying?
        ///
        /// Yes whenever the engine draws it with Unity's fix in place — its own, or the one the mod
        /// carries (TextCoreDecorations, which says how the crash happens). Without it the tag is
        /// refused and the element's font is logged: see the block inside for why font coverage,
        /// the obvious condition, did not predict the crash.
        /// </summary>
        internal static bool UnderlineIsSafe(object element, string text)
        {
            if (TextCoreDecorations.UnderlineSafe) return true;
            if (!_underlineSafetyResolved)
            {
                _underlineSafetyResolved = true;
                // A property looked up without the ambiguity trap (Engine/Members), and a method
                // named WITH its parameter types, which cannot be ambiguous: nothing caught.
                var pubInst = BindingFlags.Public | BindingFlags.Instance;
                _fontDefFontAssetProp = Members.Property(_resolvedFontDefProp?.PropertyType, "fontAsset", pubInst);
                var assetType = _fontDefFontAssetProp?.PropertyType;
                _hasCharactersMethod = assetType?.GetMethod("HasCharacters",
                    new[] { typeof(string), typeof(uint[]).MakeByRefType(), typeof(bool), typeof(bool) });
            }

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
                // Font coverage cannot predict it: the '_' is looked up while the line is drawn, in
                // the asset of the glyph being drawn, and through its fallbacks — the material it
                // brings is decided there, out of reach of any question asked from here. So
                // without the fix an RTL text on this generator loses its underline; the details
                // stay logged. TMP is untouched (its bench never crashed).
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
            catch (Exception ex) { Faults.Say("UIToolkit.DescribeAsset coverage", ex); }
            try
            {
                var texturesProp = fontAsset.GetType().GetProperty("atlasTextures", BindingFlags.Public | BindingFlags.Instance);
                if (texturesProp?.GetValue(fontAsset, null) is Array textures)
                    atlases = $", {textures.Length} atlas texture(s)";
            }
            catch (Exception ex) { Faults.Say("UIToolkit.DescribeAsset atlases", ex); }
            return $"'{name}' {coverage}{atlases} — this engine's DrawUnderlineMesh lacks Unity's fix";
        }

        // Every verdict is logged while this engine's underline defect is being characterised:
        // the bench crashed a third time with the tag apparently absent, and the log could not
        // say whether the guard had even been consulted for the element that died. Once per element
        // and verdict — a count of the first twelve hid the thirteenth, the one that crashed.
        private static void LogUnderlineVerdict(object element, bool safe, string why)
        {
            if (!DiagnosticOnce.First("UITK.underline", PathOf(element) + "\u0001" + safe + "\u0001" + why)) return;
            TranslatorCore.LogInfo($"[RtlPresenter] underline verdict for '{PathOf(element)}': {(safe ? "KEEP" : "DROP")} — {why}");
        }

        /// <summary>
        /// The assigned (shaped logical) string cut into lines the way THIS element would wrap
        /// it: greedy word fitting measured by the engine itself. Null = not answerable — no
        /// width yet (a hidden pane, a first frame), or not at all (no measure API, wall of text,
        /// measure failure); whyNot says which.
        /// </summary>
        // ── An inline style property, read and written where it can be ───────────────────────────
        // 🔴 Declared on the IStyle interface. On IL2CPP 2022.3 the interop's IStyle.whiteSpace had no
        // getter ("Property Get method was not found", bench 2022.3.62 IL2CPP): DisableWrap failed
        // there for every right-to-left text — label or field — and the engine wrapped the presented
        // form again. The style object is the engine's InlineStyleAccess: its own member answers.
        private static readonly Dictionary<string, PropertyInfo> _styleAccessors = new Dictionary<string, PropertyInfo>();
        private static Type _inlineStyleType;
        private static bool _inlineStyleTypeSought;

        /// <summary>A class's explicit implementation of an interface's style property (IStyle.x, IResolvedStyle.x), or null.</summary>
        private static PropertyInfo ExplicitStyleProperty(Type type, string iface, string name)
        {
            if (type == null) return null;
            foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (p.GetIndexParameters().Length == 0
                    && (p.Name.EndsWith(iface + "." + name, StringComparison.Ordinal) || p.Name.EndsWith(iface + "_" + name, StringComparison.Ordinal)))
                    return p;
            return null;
        }

        /// <summary>The object a style property is read on: itself, or cast to the class that declares the property.</summary>
        private static object StyleOwner(PropertyInfo p, object target) =>
            p == null || target == null || p.DeclaringType == null || p.DeclaringType.IsInterface || p.DeclaringType.IsInstanceOfType(target)
                ? target : TypeHelper.Il2CppCast(target, p.DeclaringType);

        private static PropertyInfo StyleAccessor(PropertyInfo declared, ref object style, bool write)
        {
            if (declared == null || style == null) return null;
            var accessor = write ? declared.GetSetMethod(true) : declared.GetGetMethod(true);
            if (accessor != null) { style = StyleOwner(declared, style); return declared; }
            if (!_inlineStyleTypeSought)
            {
                _inlineStyleTypeSought = true;
                _inlineStyleType = AssemblyTypes.Find("UnityEngine.UIElements.InlineStyleAccess");
            }
            if (_inlineStyleType != null) style = TypeHelper.Il2CppCast(style, _inlineStyleType);
            var type = style.GetType();
            string key = type.FullName + "|" + declared.Name + "|" + write;
            if (_styleAccessors.TryGetValue(key, out var found)) return found;
            foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                if (p.Name != declared.Name && !p.Name.EndsWith("." + declared.Name, StringComparison.Ordinal)
                    && !p.Name.EndsWith("_" + declared.Name, StringComparison.Ordinal)) continue;
                if ((write ? p.GetSetMethod(true) : p.GetGetMethod(true)) == null) continue;
                found = p;
                break;
            }
            _styleAccessors[key] = found;
            return found;
        }

        private static object StyleGet(PropertyInfo declared, object style)
        {
            var p = StyleAccessor(declared, ref style, write: false)
                    ?? throw new MissingMemberException(style.GetType().Name, declared.Name + " (getter)");
            return p.GetValue(style, null);
        }

        private static void StyleSet(PropertyInfo declared, object style, object value)
        {
            var p = StyleAccessor(declared, ref style, write: true)
                    ?? throw new MissingMemberException(style.GetType().Name, declared.Name + " (setter)");
            p.SetValue(style, value, null);
        }

        /// <summary>
        /// Whether this element wraps its lines, our own NoWrap set aside (DisableWrap): the
        /// stylesheet's or the game's answer. Null when it cannot be read.
        /// </summary>
        internal static bool? WrapsOwnLines(object element)
        {
            EnsureRtlPlumbing();
            if (_rtlWrapOriginal.Has(element) || _rtlWrapRestoring.Has(element)) return true;
            try
            {
                var resolved = _resolvedStyleProp?.GetValue(element, null);
                object ws = ResolvedWhiteSpace(element, resolved);
                if (ws == null) return null;
                string name = Enum.GetName(ws.GetType(), ws);
                return name != "NoWrap" && name != "Pre";
            }
            catch (Exception ex) { Faults.Say("UIToolkit.WrapsOwnLines", ex); return null; }
        }

        /// <summary>The width this element lays its text out in (contentRect), NaN before its first layout.</summary>
        internal static float ContentWidth(object element)
        {
            EnsureRtlPlumbing();
            try { return _contentRectProp?.GetValue(element, null) is Rect r ? r.width : float.NaN; }
            catch (Exception ex) { Faults.Say("UIToolkit.ContentWidth", ex); return float.NaN; }
        }

        /// <param name="textHandle">The element's own text handle when it is no TextElement (2021's text
        /// field input): measured as MeasureTextSize measures there. Null for a TextElement.</param>
        internal static List<string> TryBreakLines(object element, string assigned, out string whyNot, object textHandle = null)
        {
            whyNot = null;
            EnsureRtlPlumbing();
            if ((textHandle == null ? _measureTextSize : _measureElementText) == null || _contentRectProp == null)
            { whyNot = textHandle == null ? "MeasureTextSize not available on this runtime" : "MeasureVisualElementTextSize not available on this runtime"; return null; }

            // No soft wrap on this element → every break is an explicit '\n' already. ⚠ Unless
            // that NoWrap is OURS, just put back for this very measurement and not yet
            // recomputed out of the resolved style (see _rtlWrapRestoring): then the element does
            // wrap, and only the width can say where.
            if (!_rtlWrapRestoring.Has(element))
            {
                try
                {
                    var resolved = _resolvedStyleProp?.GetValue(element, null);
                    object ws = ResolvedWhiteSpace(element, resolved);
                    string wsName = ws == null ? null : Enum.GetName(ws.GetType(), ws);
                    if (wsName == "NoWrap" || wsName == "Pre")
                        return new List<string>(assigned.Split('\n'));
                }
                catch (Exception ex) { Faults.Say("UIToolkit.TryBreakLines white space", ex); }
            }

            float width;
            try
            {
                object rect = _contentRectProp.GetValue(element, null);
                width = rect is Rect r ? r.width : float.NaN;
            }
            catch (Exception ex) { Faults.Say("UIToolkit.TryBreakLines content width", ex); width = float.NaN; }
            if (float.IsNaN(width) || width < 1f)
            { whyNot = "no layout yet (element has no width)"; return null; }

            // A pathological wall of text would mean thousands of reflection round-trips into the
            // engine — the whole-string fallback is the lesser harm there.
            if (assigned.Length > 4000) { whyNot = "too long to measure word by word"; return null; }

            try
            {
                var lines = new List<string>();
                foreach (string paragraph in assigned.Split('\n'))
                {
                    if (paragraph.Length == 0) { lines.Add(""); continue; }
                    // ⚠ One measure first: most game strings are a button label or a title and
                    // fit on their line. Going straight to the word-by-word loop cost one engine
                    // measure PER WORD on every one of them, on every set_text.
                    // 🔴 Half a pixel of TOLERANCE, never a margin. An element sized by its
                    // text is exactly as wide as that text, so "fits with a pixel to spare" is
                    // never true of it and its last word went to a second row (bench: a
                    // two-word link, a two-word title). What a line exactly as wide as its box
                    // needs is not room but NO RE-WRAP — DisableWrap, once the lines are written.
                    if (MeasureWidth(element, paragraph, textHandle) <= width + 0.5f) { lines.Add(paragraph); continue; }
                    string current = "";
                    foreach (string word in paragraph.Split(' '))
                    {
                        string candidate = current.Length == 0 ? word : current + " " + word;
                        if (current.Length == 0 || MeasureWidth(element, candidate, textHandle) <= width + 0.5f)
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
                whyNot = $"measure failed: {(ex.InnerException ?? ex).GetType().Name}: {(ex.InnerException ?? ex).Message}";
                return null;
            }
        }

        private static float MeasureWidth(object element, string s, object textHandle)
        {
            object r = textHandle == null
                ? _measureTextSize.Invoke(element, new object[] { s, 0f, _measureUndefined, 0f, _measureUndefined })
                : _measureElementText.Invoke(null, new object[] { element, s, 0f, _measureUndefined, 0f, _measureUndefined, textHandle });
            if (r is Vector2 v) return v.x;
            var xf = r?.GetType().GetField("x");
            return xf != null ? Convert.ToSingle(xf.GetValue(r)) : float.NaN;
        }

        /// <summary>
        /// The UI Toolkit face of RtlPresenter.MirrorAlignment — same decision, same idempotence
        /// (computed from the stored ORIGINAL, never the current state), different plumbing:
        /// alignment here is a STYLE (unityTextAlign), read resolved, written inline.
        /// 🔴 Only callable once the element has been styled by its panel (attached, and a frame
        /// gone by): the resolved value read before that is the engine default, and it would be
        /// stored as the original for the life of the element.
        /// The original is kept by the native object on IL2CPP (ElementStore): kept by the wrapper,
        /// the mirrored side was taken for the original and a field flipped back.
        /// </summary>
        internal static void MirrorAlign(object element, bool mirror)
        {
            EnsureRtlPlumbing();
            if (_styleTextAlignProp == null || !CanReadResolvedTextAlign) return;
            if (!mirror)
            {
                // "Keep the game's": an element mirrored under an earlier choice gets its inline
                // value back (see RtlPresenter.MirrorAlignment — same rule, same reason).
                try
                {
                    if (_rtlAlignOriginal.TryGet(element, out var align))
                    {
                        _rtlAlignOriginal.Remove(element);
                        var styleBack = _styleProp.GetValue(element, null);
                        if (styleBack != null && align[0] != null) StyleSet(_styleTextAlignProp, styleBack, align[0]);
                    }
                }
                catch (Exception ex) { Faults.Say("UIToolkit.MirrorAlign restore", ex); }
                return;
            }
            try
            {
                object[] stored;
                if (!_rtlAlignOriginal.TryGet(element, out stored))
                {
                    var style = _styleProp.GetValue(element, null);
                    var resolved = _resolvedStyleProp.GetValue(element, null);
                    if (style == null || resolved == null) return;
                    stored = new object[]
                    {
                        StyleGet(_styleTextAlignProp, style),
                        ResolvedTextAlign(element, resolved),
                    };
                    _rtlAlignOriginal.Set(element, stored);
                }

                object originalEnum = stored[1];
                if (originalEnum == null) return;
                object mirrored = TextShaping.RtlPresenter.MirroredAlignmentValue(originalEnum.GetType(), originalEnum);
                if (mirrored == null) return;

                var styleNow = _styleProp.GetValue(element, null);
                if (styleNow == null) return;
                var styleValue = Activator.CreateInstance(_styleTextAlignProp.PropertyType, mirrored);
                // ⚠ Only when it actually differs. Writing an inline style invalidates the
                // element's layout, and this runs on every set_text: re-asserting the same value
                // made UI Toolkit re-lay-out for ever, which is heard as a fan rather than seen
                // as a bug (user report, right after the single-pass path shipped).
                if (Equals(StyleGet(_styleTextAlignProp, styleNow), styleValue)) return;
                StyleSet(_styleTextAlignProp, styleNow, styleValue);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.MirrorAlign", ex); }
        }

        /// <summary>
        /// whiteSpace = NoWrap while OUR line breaks are displayed ('\n' stays honored). A
        /// recomposed line is exactly as wide as the box it was cut against, and rendering
        /// rounding re-wrapped it — the overflowing chunk being, in visual order, the
        /// sentence's FIRST word. An element natively NoWrap/Pre is left alone: nothing to take,
        /// nothing to put back.
        /// </summary>
        internal static void DisableWrap(object element)
        {
            EnsureRtlPlumbing();
            if (_styleWhiteSpaceProp == null) return;
            try
            {
                var style = _styleProp.GetValue(element, null);
                if (style == null) return;
                var styleEnumType = _styleWhiteSpaceProp.PropertyType;
                var wsType = styleEnumType.IsGenericType ? styleEnumType.GetGenericArguments()[0] : null;
                if (wsType == null) return;
                var noWrap = Activator.CreateInstance(styleEnumType, Enum.Parse(wsType, "NoWrap"));

                if (_rtlWrapRestoring.TryGet(element, out var restoring))
                {
                    // Ours again: the original was kept across the restore.
                    _rtlWrapRestoring.Remove(element);
                    _rtlWrapOriginal.Set(element, restoring);
                }
                else if (_rtlWrapOriginal.Has(element))
                {
                    // Already ours.
                }
                else
                {
                    // Natively wrap-free (stylesheet or inline): not ours to touch.
                    var resolved = _resolvedStyleProp?.GetValue(element, null);
                    object ws = ResolvedWhiteSpace(element, resolved);
                    string wsName = ws == null ? null : Enum.GetName(ws.GetType(), ws);
                    if (wsName == "NoWrap" || wsName == "Pre") return;
                    _rtlWrapOriginal.Set(element, new object[] { StyleGet(_styleWhiteSpaceProp, style) });
                }
                // Same rule as MirrorAlign: writing an unchanged inline style still invalidates
                // the layout, every single set_text.
                if (Equals(StyleGet(_styleWhiteSpaceProp, style), noWrap)) return;
                StyleSet(_styleWhiteSpaceProp, style, noWrap);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.DisableWrap", ex); }
        }

        /// <summary>
        /// The element's own wrap mode back, BEFORE a new RTL text is measured on it: measured
        /// under our NoWrap, the engine answers "one line" for any paragraph and the second text
        /// shown in a box came out unwrapped. Remembered in _rtlWrapRestoring until DisableWrap
        /// takes it again — see there and TryBreakLines for why the resolved style cannot be
        /// trusted in between.
        /// </summary>
        internal static void RestoreWrap(object element)
        {
            if (!_rtlWrapOriginal.TryGet(element, out var wrap)) return;
            _rtlWrapOriginal.Remove(element);
            try
            {
                var style = _styleProp?.GetValue(element, null);
                if (style != null && wrap[0] != null && _styleWhiteSpaceProp != null) StyleSet(_styleWhiteSpaceProp, style, wrap[0]);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.RestoreWrap", ex); }
            _rtlWrapRestoring.Set(element, wrap);
        }

        /// <summary>
        /// An element the Advanced generator draws: its paragraph read right to left. The ATG does
        /// the bidi itself but takes the paragraph's direction from the element
        /// (VisualElement.languageDirection, Inherit → left to right): a right-to-left sentence came
        /// out with its words in order and its final period on the right, the side it starts on
        /// (bench, Hebrew and Arabic, 6000.3.6, 2026-10-03). The same paragraph direction the mod
        /// gives every right-to-left text it composes itself (RtlComposer: paragraph level RTL). The
        /// element's own value is kept and put back when its text goes back to left to right; only
        /// written when it differs — a write re-lays the element out.
        /// </summary>
        internal static void SetRtlDirection(object element)
        {
            EnsureRtlPlumbing();
            if (_languageDirectionProp == null) return;   // no ATG on this engine either
            try
            {
                object current = _languageDirectionProp.GetValue(element, null);
                if (!_rtlDirectionOriginal.Has(element))
                    _rtlDirectionOriginal.Set(element, new[] { current });
                object rtl = Enum.Parse(_languageDirectionProp.PropertyType, "RTL");
                if (!Equals(current, rtl)) _languageDirectionProp.SetValue(element, rtl, null);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.SetRtlDirection", ex); }
        }

        /// <summary>
        /// Put back what an element wore before our RTL adjustments — its inline styles, and its
        /// paragraph direction unless <paramref name="keepDirection"/> (an element the ATG still
        /// draws right to left: put back and set again at each text, it would re-lay out each time).
        /// </summary>
        /// <param name="keepAlignment">The alignment is decided by the caller (an input field: AlignTypedField).</param>
        internal static void RestoreRtlAdjustments(object element, bool keepDirection = false, bool keepAlignment = false)
        {
            try
            {
                if (!keepDirection && _rtlDirectionOriginal.TryGet(element, out var direction))
                {
                    _rtlDirectionOriginal.Remove(element);
                    if (direction[0] != null && !Equals(_languageDirectionProp?.GetValue(element, null), direction[0]))
                        _languageDirectionProp?.SetValue(element, direction[0], null);
                }
                var style = _styleProp?.GetValue(element, null);
                if (style == null) return;
                if (!keepAlignment && _rtlAlignOriginal.TryGet(element, out var align))
                {
                    _rtlAlignOriginal.Remove(element);
                    if (align[0] != null && _styleTextAlignProp != null) StyleSet(_styleTextAlignProp, style, align[0]);
                }
                if (_rtlWrapOriginal.TryGet(element, out var wrap))
                {
                    _rtlWrapOriginal.Remove(element);
                    if (wrap[0] != null && _styleWhiteSpaceProp != null) StyleSet(_styleWhiteSpaceProp, style, wrap[0]);
                }
                _rtlWrapRestoring.Remove(element);
            }
            catch (Exception ex) { Faults.Say("UIToolkit.RestoreRtlAdjustments", ex); }
        }

        #endregion
    }
}
