using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.Input;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Inspector mode: determines behavior for exclusion or bitmap replacement.
    /// </summary>
    public enum InspectorMode
    {
        Exclusion,
        BitmapReplace,
        FontOverride,
        TextEdit
    }

    /// <summary>
    /// Inspector panel for visually selecting UI elements.
    /// Dual mode: Exclusion (select text to exclude) or BitmapReplace (select images to replace).
    /// DevTools-style: hover preview with highlight overlay, click to select.
    /// All Unity API calls use reflection for IL2CPP compatibility.
    /// </summary>
    public class InspectorPanel : TranslatorPanelBase
    {
        public override string Name => _currentMode == InspectorMode.BitmapReplace ? "Image Inspector"
            : _currentMode == InspectorMode.FontOverride ? "Font Override Inspector"
            : "Element Inspector";
        public override int MinWidth => 420;
        public override int MinHeight => 360;
        public override int PanelWidth => 480;
        public override int PanelHeight => 420;

        protected override int MinPanelHeight => 360;

        // TextEdit mode contains a scrollable list of child texts that benefits from extra
        // vertical room when the user enlarges the panel.
        protected override bool HasFlexibleContent => true;

        // Mode
        private InspectorMode _currentMode = InspectorMode.Exclusion;

        // UI elements — shared
        private Text _hoveredPathLabel;
        private Text _selectedPathLabel;
        private Text _statusLabel;
        private ButtonRef _cancelBtn;
        private Text _titleLabel;
        private Components.HelpZone _helpZone;

        // UI elements — Exclusion mode
        private ButtonRef _excludeThisBtn;
        private ButtonRef _excludePatternBtn;
        private GameObject _exclusionActionsRow;

        // UI elements — BitmapReplace mode
        private ButtonRef _exportOriginalBtn;
        private ButtonRef _markReplaceBtn;
        private GameObject _imageActionsRow;
        private Text _spriteInfoLabel;

        // UI elements — TextEdit mode
        private GameObject _textEditRow;
        private GameObject _textEditScroll;
        private GameObject _textEditListContent;
        private Text _textEditCountLabel;

        /// <summary>
        /// The list's floor, and its ceiling once filled. One text needs a small box; a busy screen
        /// hands this panel a dozen at once, and 260px of them was the complaint. The ceiling is
        /// not a limit on the list — it scrolls — but on how much window it may claim by itself;
        /// past that the panel is still resizable by hand, up to the screen.
        /// </summary>
        private const int TextEditListMinHeight = 260;
        private const int TextEditListMaxHeight = 560;

        /// <summary>Rough height of one edit row: labels + field + preview + buttons + spacing.</summary>
        private const int TextEditRowHeight = 120;

        /// <summary>
        /// Rows waiting for an AI retranslation. The answer arrives seconds later, on the worker
        /// thread, long after the click — without this the row would stay on "Queued for AI..."
        /// forever, which is exactly what "the button does nothing" looked like.
        /// A list, not a dictionary: the same text can be shown by several components at once.
        /// </summary>
        private readonly List<TextEditRowState> _pendingRetranslateRows = new List<TextEditRowState>();

        /// <summary>
        /// One editable line of the in-game text editor, whole. Its handlers, the answer that
        /// comes back from another thread seconds later, and the button/preview refresh all read
        /// from this — passing the pieces around one by one is how one of them gets forgotten.
        /// </summary>
        private sealed class TextEditRowState
        {
            public string Key;
            public object Component;
            // Live values of the [!v*N] placeholders as displayed when the row was built: what is
            // stored and edited carries placeholders, what the game draws carries numbers.
            public Dictionary<int, string> LiveNumbers;

            public InputFieldRef Input;
            public Text KeyLabel;
            public Text PreviewLabel;
            public ButtonRef SaveBtn;
            public ButtonRef RetranslateBtn;
            public ButtonRef RevertBtn;

            /// <summary>
            /// What the AI last proposed for this line, or null. Kept so that saving it untouched
            /// can be filed as "A" — the machine wrote it — instead of claiming a human did.
            /// </summary>
            public string AiProposal;
        }

        // Camera selection for world-space raycast
        private Components.SearchableDropdown _cameraDropdown;
        private Camera _selectedCamera = null; // null = UI Only mode
        private Camera[] _sceneCameras = new Camera[0];
        private string[] _cameraNames = new string[0];

        // State
        private bool _isInspecting = false;
        private string _lastHoveredPath = "";
        private string _lastSelectedPath = "";
        private GameObject _lastSelectedObject = null;
        private object _lastSelectedSpriteObj = null;
        private int _frameSkip = 0;
        private bool _mainPanelWasOpen = false;

        // Highlight overlay
        private GameObject _highlightCanvas;
        private Image _hoverHighlight;
        private Image _selectedHighlight;
        private RectTransform _hoverHighlightRect;
        private RectTransform _selectedHighlightRect;

        // Colors for highlights (DevTools-style) — from the palette
        private static readonly Color HoverHighlightColor = UIStyles.GameHighlightHover;
        private static readonly Color SelectedHighlightColor = UIStyles.GameHighlightSelected;

        #region IL2CPP-safe Raycast Infrastructure

        // Resolved types (cached at first use)
        private static bool _raycastInitialized = false;
        private static bool _raycastAvailable = false;

        // Resolved types
        private static Type _graphicRaycasterType;
        private static Type _pointerEventDataType;
        private static Type _eventSystemType;
        private static Type _raycastResultType;
        private static Type _graphicType;

        // Resolved methods/properties
        private static PropertyInfo _eventSystemCurrentProp;
        private static ConstructorInfo _pointerEventDataCtor;
        private static PropertyInfo _pointerEventDataPositionProp;
        private static MethodInfo _raycasterRaycastMethod;

        // For reading results
        private static PropertyInfo _raycastResultGameObjectProp;

        // For creating the list parameter (IL2CPP needs Il2CppSystem list)
        private static Type _listType;          // The actual List<RaycastResult> type to use
        private static MethodInfo _listCountProp;
        private static MethodInfo _listGetItem;

        /// <summary>
        /// Initialize raycast types and methods via reflection.
        /// Safe for both Mono and IL2CPP.
        /// </summary>
        private static void InitializeRaycast()
        {
            if (_raycastInitialized) return;
            _raycastInitialized = true;

            try
            {
                // Resolve types
                _graphicRaycasterType = FindUIType("UnityEngine.UI.GraphicRaycaster");
                _pointerEventDataType = FindUIType("UnityEngine.EventSystems.PointerEventData");
                _eventSystemType = FindUIType("UnityEngine.EventSystems.EventSystem");
                _raycastResultType = FindUIType("UnityEngine.EventSystems.RaycastResult");
                _graphicType = FindUIType("UnityEngine.UI.Graphic");

                if (_graphicRaycasterType == null || _pointerEventDataType == null ||
                    _eventSystemType == null || _raycastResultType == null)
                {
                    TranslatorCore.LogWarning("[Inspector] Could not resolve UI types for raycast");
                    return;
                }

                // EventSystem.current
                _eventSystemCurrentProp = _eventSystemType.GetProperty("current",
                    BindingFlags.Public | BindingFlags.Static);

                // PointerEventData(EventSystem)
                _pointerEventDataCtor = _pointerEventDataType.GetConstructor(
                    new[] { _eventSystemType });

                // PointerEventData.position
                _pointerEventDataPositionProp = _pointerEventDataType.GetProperty("position",
                    BindingFlags.Public | BindingFlags.Instance);

                // Resolve the List<RaycastResult> type and GraphicRaycaster.Raycast(PointerEventData, List<RaycastResult>)
                ResolveRaycastMethod();

                if (_eventSystemCurrentProp == null || _pointerEventDataCtor == null ||
                    _pointerEventDataPositionProp == null || _raycasterRaycastMethod == null)
                {
                    TranslatorCore.LogWarning("[Inspector] Could not resolve all raycast methods");
                    LogResolvedState();
                    return;
                }

                _raycastAvailable = true;
                TranslatorCore.LogInfo("[Inspector] Raycast infrastructure initialized (IL2CPP-safe)");
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[Inspector] Raycast init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Find the correct Raycast method and the list type it expects.
        /// On IL2CPP, parameters use Il2CppSystem.Collections.Generic.List.
        /// </summary>
        private static void ResolveRaycastMethod()
        {
            var pubInst = BindingFlags.Public | BindingFlags.Instance;

            // Find Raycast(PointerEventData, List<RaycastResult>) on GraphicRaycaster
            foreach (var method in _graphicRaycasterType.GetMethods(pubInst))
            {
                if (method.Name != "Raycast") continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 2) continue;

                // First param should be PointerEventData-like
                var param0Type = parameters[0].ParameterType;
                if (!IsTypeMatch(param0Type, "PointerEventData")) continue;

                // Second param should be List<RaycastResult>-like
                var param1Type = parameters[1].ParameterType;
                if (!param1Type.IsGenericType) continue;

                var genericArgs = param1Type.GetGenericArguments();
                if (genericArgs.Length != 1 || !IsTypeMatch(genericArgs[0], "RaycastResult")) continue;

                _raycasterRaycastMethod = method;
                _listType = param1Type;

                // Resolve list accessors
                var countProp = _listType.GetProperty("Count", pubInst);
                _listCountProp = countProp?.GetGetMethod();

                // get_Item(int) — indexer
                _listGetItem = _listType.GetMethod("get_Item", pubInst, null, new[] { typeof(int) }, null);

                // RaycastResult.gameObject
                _raycastResultGameObjectProp = genericArgs[0].GetProperty("gameObject", pubInst);
                // Fallback: try m_GameObject field (IL2CPP struct)
                if (_raycastResultGameObjectProp == null)
                    _raycastResultGameObjectProp = genericArgs[0].GetProperty("gameObject",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                TranslatorCore.LogDebug($"[Inspector] Resolved Raycast: list={_listType.FullName}, result={genericArgs[0].FullName}");
                break;
            }
        }

        /// <summary>
        /// Check if a type name matches (handles IL2CPP prefixed names).
        /// </summary>
        private static bool IsTypeMatch(Type type, string simpleName)
        {
            if (type == null) return false;
            string name = type.Name;
            if (name == simpleName) return true;
            // IL2CPP prefix
            if (name.StartsWith("Il2Cpp") && name.Substring(6) == simpleName) return true;
            return false;
        }

        /// <summary>
        /// Find a UI type across all loaded assemblies (handles IL2CPP prefixed assemblies).
        /// </summary>
        private static Type FindUIType(string fullName)
        {
            // Direct lookup first
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(fullName);
                    if (type != null) return type;
                }
                catch { }
            }

            // IL2CPP: try with Il2Cpp prefix on the namespace
            // e.g., "UnityEngine.UI.GraphicRaycaster" → "Il2CppUnityEngine.UI.GraphicRaycaster"
            string il2cppName = "Il2Cpp" + fullName;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(il2cppName);
                    if (type != null) return type;
                }
                catch { }
            }

            // Last resort: search by simple name
            string simpleName = fullName.Substring(fullName.LastIndexOf('.') + 1);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == simpleName && type.FullName.Contains(simpleName))
                            return type;
                    }
                }
                catch { }
            }

            return null;
        }

        private static void LogResolvedState()
        {
            TranslatorCore.LogDebug($"[Inspector] GraphicRaycaster={_graphicRaycasterType != null}, " +
                $"PointerEventData={_pointerEventDataType != null}, EventSystem={_eventSystemType != null}, " +
                $"RaycastResult={_raycastResultType != null}");
            TranslatorCore.LogDebug($"[Inspector] EventSystem.current={_eventSystemCurrentProp != null}, " +
                $"PointerEventData ctor={_pointerEventDataCtor != null}, " +
                $"Raycast method={_raycasterRaycastMethod != null}");
        }

        /// <summary>
        /// Raycast to find UI element under screen position.
        /// Uses pure reflection — works on both Mono and IL2CPP.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private GameObject RaycastUIElement(Vector3 screenPosition)
        {
            // If a camera is selected, raycast via that camera (world-space)
            if (_selectedCamera != null)
                return RaycastViaCamera(_selectedCamera, screenPosition);

            // Default: UI Only mode via GraphicRaycasters
            if (!_raycastAvailable) return null;

            try
            {
                // Get EventSystem.current
                var eventSystem = _eventSystemCurrentProp.GetValue(null, null);
                if (eventSystem == null) return null;

                // Find all GraphicRaycasters in the scene
                var raycasters = TypeHelper.FindAllObjectsOfType(_graphicRaycasterType);
                if (raycasters == null || raycasters.Length == 0) return null;

                foreach (var raycasterObj in raycasters)
                {
                    if (raycasterObj == null) continue;

                    // Skip our own highlight canvas raycaster
                    var raycasterComp = raycasterObj as Component;
                    if (raycasterComp == null)
                        raycasterComp = TypeHelper.Il2CppCast(raycasterObj, typeof(Component)) as Component;
                    if (raycasterComp != null && raycasterComp.gameObject != null && IsOwnUI(raycasterComp.gameObject))
                        continue;

                    // IL2CPP: cast to the proper type
                    var raycaster = TypeHelper.Il2CppCast(raycasterObj, _graphicRaycasterType);
                    if (raycaster == null) continue;

                    try
                    {
                        // Create PointerEventData
                        var pointer = _pointerEventDataCtor.Invoke(new[] { eventSystem });
                        if (pointer == null) continue;

                        // Set position
                        _pointerEventDataPositionProp.SetValue(pointer, (Vector2)screenPosition, null);

                        // Create List<RaycastResult>
                        var resultsList = Activator.CreateInstance(_listType);
                        if (resultsList == null) continue;

                        // Call Raycast(pointer, results)
                        // Step aside from the input capture: it silences the game's raycasters so
                        // nothing behind our window reacts to a click, and this raycast IS into
                        // the game — inspecting it is the one time we want them to answer.
                        UniverseLib.Input.InputCapture.ConsumerReading = true;
                        try { _raycasterRaycastMethod.Invoke(raycaster, new[] { pointer, resultsList }); }
                        finally { UniverseLib.Input.InputCapture.ConsumerReading = false; }

                        // Check count
                        int count = (int)_listCountProp.Invoke(resultsList, null);
                        if (count == 0) continue;

                        // Iterate results — in BitmapReplace mode, skip non-image components
                        for (int i = 0; i < count; i++)
                        {
                            var resultItem = _listGetItem.Invoke(resultsList, new object[] { i });
                            if (resultItem == null) continue;

                            var gameObj = _raycastResultGameObjectProp.GetValue(resultItem, null);
                            GameObject go = gameObj as GameObject;

                            // IL2CPP: may need cast
                            if (go == null && gameObj != null)
                            {
                                var casted = TypeHelper.Il2CppCast(gameObj, typeof(GameObject));
                                go = casted as GameObject;
                            }

                            if (go == null) continue;

                            // In BitmapReplace mode, only accept GameObjects with image components
                            if (_currentMode == InspectorMode.BitmapReplace)
                            {
                                if (!ImageReplacer.HasImageComponent(go)) continue;
                            }

                            return go;
                        }
                    }
                    catch (Exception ex)
                    {
                        TranslatorCore.LogDebug($"[Inspector] Raycast on {raycasterObj.name} failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[Inspector] RaycastUIElement error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Raycast World Space Canvases that use a specific camera.
        /// Uses GraphicRaycaster on each matching Canvas.
        /// </summary>
        private GameObject RaycastWorldSpaceCanvases(Camera camera, Vector3 screenPosition)
        {
            if (camera == null || !_raycastAvailable) return null;

            try
            {
                var eventSystem = _eventSystemCurrentProp.GetValue(null, null);
                if (eventSystem == null) return null;

                var raycasters = TypeHelper.FindAllObjectsOfType(_graphicRaycasterType);
                if (raycasters == null) return null;

                foreach (var raycasterObj in raycasters)
                {
                    if (raycasterObj == null) continue;

                    // Get the Canvas of this raycaster
                    var raycasterComp = raycasterObj as Component;
                    if (raycasterComp == null)
                        raycasterComp = TypeHelper.Il2CppCast(raycasterObj, typeof(Component)) as Component;
                    if (raycasterComp == null || raycasterComp.gameObject == null) continue;
                    if (IsOwnUI(raycasterComp.gameObject)) continue;

                    // Check if this Canvas is World Space and uses our selected camera
                    var canvas = raycasterComp.gameObject.GetComponent<Canvas>();
                    if (canvas == null) continue;
                    // Match canvases that use this camera (WorldSpace or ScreenSpaceCamera)
                    if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) continue;
                    if (canvas.worldCamera != camera) continue;

                    // This Canvas uses our camera — raycast through it
                    var raycaster = TypeHelper.Il2CppCast(raycasterObj, _graphicRaycasterType);
                    if (raycaster == null) continue;

                    try
                    {
                        var pointer = _pointerEventDataCtor.Invoke(new[] { eventSystem });
                        if (pointer == null) continue;
                        _pointerEventDataPositionProp.SetValue(pointer, (Vector2)screenPosition, null);

                        var resultsList = Activator.CreateInstance(_listType);
                        if (resultsList == null) continue;

                        // Step aside from the input capture: it silences the game's raycasters so
                        // nothing behind our window reacts to a click, and this raycast IS into
                        // the game — inspecting it is the one time we want them to answer.
                        UniverseLib.Input.InputCapture.ConsumerReading = true;
                        try { _raycasterRaycastMethod.Invoke(raycaster, new[] { pointer, resultsList }); }
                        finally { UniverseLib.Input.InputCapture.ConsumerReading = false; }

                        int count = (int)_listCountProp.Invoke(resultsList, null);
                        for (int i = 0; i < count; i++)
                        {
                            var resultItem = _listGetItem.Invoke(resultsList, new object[] { i });
                            if (resultItem == null) continue;

                            var gameObj = _raycastResultGameObjectProp.GetValue(resultItem, null);
                            GameObject go = gameObj as GameObject;
                            if (go == null && gameObj != null)
                                go = TypeHelper.Il2CppCast(gameObj, typeof(GameObject)) as GameObject;
                            if (go == null || IsOwnUI(go)) continue;

                            if (_currentMode == InspectorMode.BitmapReplace)
                            {
                                if (!ImageReplacer.HasImageComponent(go)) continue;
                            }

                            return go;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[Inspector] RaycastWorldSpaceCanvases error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Raycast via a specific Camera for world-space Renderers.
        /// Only checks renderers visible to this camera (via cullingMask).
        /// </summary>
        private GameObject RaycastViaCamera(Camera camera, Vector3 screenPosition)
        {
            if (camera == null) return null;

            // First: check World Space Canvases that use this camera
            // (these have GraphicRaycasters but aren't found by "UI Only" mode
            // because their GraphicRaycaster needs the correct camera context)
            var canvasHit = RaycastWorldSpaceCanvases(camera, screenPosition);
            if (canvasHit != null) return canvasHit;

            // Then: bounds check for renderers visible to this camera
            try
            {
                int cullingMask = camera.cullingMask;
                var all = TypeHelper.FindAllObjectsOfType(typeof(Renderer));
                if (all == null) return null;

                GameObject bestHit = null;
                float bestArea = float.MaxValue;

                foreach (var obj in all)
                {
                    if (obj == null) continue;

                    Renderer rend = obj as Renderer;
                    if (rend == null)
                    {
                        var casted = TypeHelper.Il2CppCast(obj, typeof(Renderer));
                        rend = casted as Renderer;
                    }
                    if (rend == null || rend.gameObject == null) continue;
                    if (!rend.enabled || !rend.isVisible) continue;
                    if (!rend.gameObject.activeInHierarchy) continue;

                    // Filter by camera culling mask
                    if ((cullingMask & (1 << rend.gameObject.layer)) == 0) continue;

                    if (IsOwnUI(rend.gameObject)) continue;

                    if (_currentMode == InspectorMode.BitmapReplace)
                    {
                        if (!ImageReplacer.HasImageComponent(rend.gameObject)) continue;
                    }

                    try
                    {
                        var bounds = rend.bounds;
                        if (bounds.size == Vector3.zero) continue;

                        Vector3 center = bounds.center;
                        Vector3 extents = bounds.extents;

                        Vector3 screenCenter = camera.WorldToScreenPoint(center);
                        // Only filter by Z for perspective cameras (orthographic can have negative Z)
                        if (!camera.orthographic && screenCenter.z < 0) continue;

                        Vector3 s0 = camera.WorldToScreenPoint(center - extents);
                        Vector3 s1 = camera.WorldToScreenPoint(center + extents);

                        float minX = Mathf.Min(s0.x, s1.x);
                        float maxX = Mathf.Max(s0.x, s1.x);
                        float minY = Mathf.Min(s0.y, s1.y);
                        float maxY = Mathf.Max(s0.y, s1.y);

                        if (screenPosition.x >= minX && screenPosition.x <= maxX &&
                            screenPosition.y >= minY && screenPosition.y <= maxY)
                        {
                            float hitArea = (maxX - minX) * (maxY - minY);
                            if (hitArea < bestArea)
                            {
                                bestArea = hitArea;
                                bestHit = rend.gameObject;
                            }
                        }
                    }
                    catch { }
                }

                return bestHit;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[Inspector] RaycastViaCamera error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Check if an object is a Graphic component (IL2CPP-safe).
        /// </summary>
        private static bool IsGraphic(Component component)
        {
            if (component == null || _graphicType == null) return false;
            try
            {
                return _graphicType.IsInstanceOfType(component);
            }
            catch
            {
                // Fallback: name-based check for IL2CPP proxy types
                var type = component.GetType();
                while (type != null)
                {
                    string name = type.Name;
                    if (name == "Graphic" || name == "Il2CppGraphic") return true;
                    type = type.BaseType;
                }
                return false;
            }
        }

        #endregion

        public InspectorPanel(UIBase owner) : base(owner)
        {
            // Initialize raycast infrastructure on first panel creation
            InitializeRaycast();

            // Panels are built once for the life of the process (CreatePanels), so this
            // subscription needs no matching removal — and must not be made per row, which would
            // pile up one handler per click on a static event.
            TranslatorCore.OnRetranslateFinished += OnRetranslateFinished;
        }

        protected override void ConstructPanelContent()
        {
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Contextual help bar between content and footer
            _helpZone = CreateHelpZone(buttonRow, "Hover an element to see what it does");

            // Title
            _titleLabel = CreateTitle(scrollContent, "Title", "Element Inspector");
            RegisterExcluded(_titleLabel);

            UIStyles.CreateSpacer(scrollContent, 5);

            // Main card
            var card = CreateAdaptiveCard(scrollContent, "InspectorCard", PanelWidth - 60, stretchVertically: true);

            // Instructions
            var instructionTitle = UIStyles.CreateSectionTitle(card, "InstructionsLabel", "Instructions");
            RegisterUIText(instructionTitle);

            var instructionHint = UIStyles.CreateHint(card, "InstructionsHint",
                "Hover over any UI element to preview it. Click to select.");
            RegisterUIText(instructionHint);

            UIStyles.CreateSpacer(card, 8);

            // --- Camera selection ---
            var cameraTitle = UIStyles.CreateSectionTitle(card, "CameraLabel", "Target");
            RegisterUIText(cameraTitle);

            _cameraDropdown = new Components.SearchableDropdown("CameraTarget",
                new[] { "UI Only" }, "UI Only", popupHeight: 150, showSearch: false);
            var cameraObj = _cameraDropdown.CreateUI(card, OnCameraSelected, PanelWidth - 80);
            UIFactory.SetLayoutElement(cameraObj, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);
            _helpZone?.Describe(cameraObj,
                "'UI Only' picks on-screen interface text. Choose a camera to pick objects in the game world instead.");

            UIStyles.CreateSpacer(card, 8);

            // --- Hovered Element section ---
            var hoverTitle = UIStyles.CreateSectionTitle(card, "HoverSectionLabel", "Hovered");
            RegisterUIText(hoverTitle);

            var hoverBox = CreateSection(card, "HoverBox");

            _hoveredPathLabel = UIFactory.CreateLabel(hoverBox, "HoverPathValue", "(move cursor over a UI element)", TextAnchor.MiddleLeft);
            _hoveredPathLabel.color = UIStyles.TextMuted;
            _hoveredPathLabel.fontStyle = FontStyle.Italic;
            _hoveredPathLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_hoveredPathLabel.gameObject, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);

            UIStyles.CreateSpacer(card, 8);

            // --- Selected Element section ---
            var selectedTitle = UIStyles.CreateSectionTitle(card, "SelectedSectionLabel", "Selected");
            RegisterUIText(selectedTitle);

            var selectedBox = CreateSection(card, "SelectedBox");

            _selectedPathLabel = UIFactory.CreateLabel(selectedBox, "SelectedPathValue", "(click to select)", TextAnchor.MiddleLeft);
            _selectedPathLabel.color = UIStyles.TextMuted;
            _selectedPathLabel.fontStyle = FontStyle.Italic;
            _selectedPathLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_selectedPathLabel.gameObject, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);

            UIStyles.CreateSpacer(card, 8);

            // --- Sprite info (BitmapReplace mode only) ---
            _spriteInfoLabel = UIFactory.CreateLabel(card, "SpriteInfo", "", TextAnchor.MiddleLeft);
            _spriteInfoLabel.fontSize = UIStyles.FontSizeSmall;
            _spriteInfoLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_spriteInfoLabel.gameObject, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);
            _spriteInfoLabel.gameObject.SetActive(false);

            UIStyles.CreateSpacer(card, 4);

            // --- Action buttons ---
            var actionsTitle = UIStyles.CreateSectionTitle(card, "ActionsLabel", "Actions");
            RegisterUIText(actionsTitle);

            // Exclusion mode actions
            _exclusionActionsRow = UIStyles.CreateFormRow(card, "ExclusionActionRow", UIStyles.ButtonHeight, 5);

            _excludeThisBtn = CreatePrimaryButton(_exclusionActionsRow, "ExcludeThisBtn", "Exclude This Element");
            _excludeThisBtn.OnClick += OnExcludeThisClicked;
            _excludeThisBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_excludeThisBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_excludeThisBtn.ButtonText);
            _helpZone?.Describe(_excludeThisBtn.Component.gameObject,
                "Never translate this exact element (only this one)");

            _excludePatternBtn = CreateSecondaryButton(_exclusionActionsRow, "ExcludePatternBtn", "Exclude Pattern");
            _excludePatternBtn.OnClick += OnExcludePatternClicked;
            _excludePatternBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_excludePatternBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_excludePatternBtn.ButtonText);
            _helpZone?.Describe(_excludePatternBtn.Component.gameObject,
                "Never translate ANY element with this name, anywhere in the game (e.g. every chat line)");

            // BitmapReplace mode actions
            _imageActionsRow = UIStyles.CreateFormRow(card, "ImageActionRow", UIStyles.ButtonHeight, 5);

            _exportOriginalBtn = CreatePrimaryButton(_imageActionsRow, "ExportOriginalBtn", "Export Original");
            _exportOriginalBtn.OnClick += OnExportOriginalClicked;
            _exportOriginalBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_exportOriginalBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_exportOriginalBtn.ButtonText);
            _helpZone?.Describe(_exportOriginalBtn.Component.gameObject,
                "Save the game's current image to disk as a template you can edit");

            _markReplaceBtn = CreateSecondaryButton(_imageActionsRow, "MarkReplaceBtn", "Mark for Replace");
            _markReplaceBtn.OnClick += OnMarkReplaceClicked;
            _markReplaceBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_markReplaceBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_markReplaceBtn.ButtonText);
            _helpZone?.Describe(_markReplaceBtn.Component.gameObject,
                "Register this image for replacement: drop your edited version in the images folder and it swaps in-game");

            _imageActionsRow.SetActive(false); // Hidden by default (exclusion mode)

            // TextEdit mode — scrollable list of child texts
            _textEditRow = UIFactory.CreateVerticalGroup(card, "TextEditRow", false, false, true, true, 4);
            UIFactory.SetLayoutElement(_textEditRow, flexibleWidth: 9999, flexibleHeight: 9999);

            _textEditCountLabel = UIFactory.CreateLabel(_textEditRow, "TextEditCount", "", TextAnchor.MiddleLeft);
            _textEditCountLabel.fontSize = UIStyles.FontSizeSmall;
            _textEditCountLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_textEditCountLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            _textEditScroll = UIFactory.CreateScrollView(_textEditRow, "TextEditScroll", out _textEditListContent, out _);
            // See TranslatorPanelBase.ScrollingListHeightRule. Both numbers are revised once the
            // list is filled (SizeTextEditList): the smallest useful box for one line is not the
            // smallest useful box for a dozen, and this panel is regularly handed a dozen.
            UIFactory.SetLayoutElement(_textEditScroll, minHeight: TextEditListMinHeight, preferredHeight: TextEditListMinHeight,
                flexibleHeight: 9999, flexibleWidth: 9999);
            UIStyles.SetBackground(_textEditScroll, UIStyles.InputBackground);
            UIFactory.SetLayoutGroup<VerticalLayoutGroup>(_textEditListContent, false, false, true, true, 5, 5, 5, 5, 5);

            _textEditRow.SetActive(false); // Hidden by default

            // Shared clear selection button
            var actionRow2 = UIStyles.CreateFormRow(card, "ActionRow2", UIStyles.ButtonHeight, 5);

            _cancelBtn = CreateSecondaryButton(actionRow2, "CancelBtn", "Clear Selection");
            _cancelBtn.OnClick += OnCancelClicked;
            _cancelBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_cancelBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_cancelBtn.ButtonText);
            _helpZone?.Describe(_cancelBtn.Component.gameObject,
                "Deselect the current element and keep inspecting. Nothing is changed.");

            // Status label
            UIStyles.CreateSpacer(card, 5);
            _statusLabel = UIFactory.CreateLabel(card, "Status", "", TextAnchor.MiddleLeft);
            _statusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_statusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Footer button (fixed at bottom)
            var stopBtn = CreatePrimaryButton(buttonRow, "StopBtn", "Stop Inspecting");
            stopBtn.OnClick += OnStopClicked;
            RegisterUIText(stopBtn.ButtonText);
            _helpZone?.Describe(stopBtn.Component.gameObject,
                "Leave inspect mode and close this window. Element picking stops.");

            // Create the highlight overlay
            CreateHighlightOverlay();
        }

        #region Highlight Overlay

        /// <summary>
        /// Create the highlight overlay canvas with hover and selected highlights.
        /// Uses a separate ScreenSpaceOverlay Canvas with very high sort order.
        /// </summary>
        private void CreateHighlightOverlay()
        {
            // Create a root object for the highlight canvas
            _highlightCanvas = new GameObject("UGT_InspectorHighlight");
            UnityEngine.Object.DontDestroyOnLoad(_highlightCanvas);

            var canvas = _highlightCanvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 29000; // Below our UI (UniverseLib uses 30000)

            // GraphicRaycaster needed so the EventSystem sees our highlights
            // and they can block clicks from reaching game elements below
            _highlightCanvas.AddComponent<UnityEngine.UI.GraphicRaycaster>();

            // Hover highlight — raycastTarget=true to block game clicks on the hovered element
            var hoverObj = new GameObject("HoverHighlight");
            hoverObj.transform.SetParent(_highlightCanvas.transform, false);
            _hoverHighlight = hoverObj.AddComponent<Image>();
            _hoverHighlight.color = HoverHighlightColor;
            _hoverHighlight.raycastTarget = true;
            _hoverHighlightRect = hoverObj.GetComponent<RectTransform>();
            _hoverHighlightRect.anchorMin = Vector2.zero;
            _hoverHighlightRect.anchorMax = Vector2.zero;
            _hoverHighlightRect.pivot = new Vector2(0, 0);
            hoverObj.SetActive(false);

            // Selected highlight
            var selectedObj = new GameObject("SelectedHighlight");
            selectedObj.transform.SetParent(_highlightCanvas.transform, false);
            _selectedHighlight = selectedObj.AddComponent<Image>();
            _selectedHighlight.color = SelectedHighlightColor;
            _selectedHighlight.raycastTarget = true;
            _selectedHighlightRect = selectedObj.GetComponent<RectTransform>();
            _selectedHighlightRect.anchorMin = Vector2.zero;
            _selectedHighlightRect.anchorMax = Vector2.zero;
            _selectedHighlightRect.pivot = new Vector2(0, 0);
            selectedObj.SetActive(false);

            // Start hidden
            _highlightCanvas.SetActive(false);
        }

        /// <summary>
        /// Position a highlight rect over a target GameObject's RectTransform bounds.
        /// Uses TransformPoint instead of GetWorldCorners (IL2CPP-safe: no array params).
        /// </summary>
        private void PositionHighlight(RectTransform highlightRect, Image highlightImage, GameObject target)
        {
            if (target == null || highlightRect == null || highlightImage == null)
            {
                highlightImage?.gameObject.SetActive(false);
                return;
            }

            Vector2 screenMin, screenMax;

            var targetRect = target.GetComponent<RectTransform>();
            if (targetRect != null)
            {
                // Canvas UI: use RectTransform bounds
                if (!GetScreenBounds(targetRect, out screenMin, out screenMax))
                {
                    highlightImage.gameObject.SetActive(false);
                    return;
                }
            }
            else
            {
                // World-space object (SpriteRenderer): project bounds to screen
                if (!GetScreenBoundsFromRenderer(target, out screenMin, out screenMax))
                {
                    highlightImage.gameObject.SetActive(false);
                    return;
                }
            }

            float width = screenMax.x - screenMin.x;
            float height = screenMax.y - screenMin.y;

            // Skip degenerate rects
            if (width < 1f || height < 1f)
            {
                highlightImage.gameObject.SetActive(false);
                return;
            }

            highlightRect.anchoredPosition = screenMin;
            highlightRect.sizeDelta = new Vector2(width, height);
            highlightImage.gameObject.SetActive(true);
        }

        private void HideAllHighlights()
        {
            if (_hoverHighlight != null) _hoverHighlight.gameObject.SetActive(false);
            if (_selectedHighlight != null) _selectedHighlight.gameObject.SetActive(false);
        }

        #endregion

        /// <summary>
        /// Set the inspector mode. Must be called before SetActive(true).
        /// </summary>
        public void SetMode(InspectorMode mode)
        {
            _currentMode = mode;
        }

        private void UpdateUIForMode()
        {
            bool isImage = _currentMode == InspectorMode.BitmapReplace;
            bool isFontOverride = _currentMode == InspectorMode.FontOverride;
            bool isTextEdit = _currentMode == InspectorMode.TextEdit;

            // Update title
            if (_titleLabel != null)
                SetDynamicText(_titleLabel, isImage ? "Image Inspector"
                    : isFontOverride ? "Font Override — Click on an element"
                    : isTextEdit ? "Text Editor — Click on text to edit"
                    : "Element Inspector");

            // Toggle action button visibility per mode
            if (_exclusionActionsRow != null) _exclusionActionsRow.SetActive(!isImage && !isFontOverride && !isTextEdit);
            if (_imageActionsRow != null) _imageActionsRow.SetActive(isImage);
            if (_spriteInfoLabel != null) _spriteInfoLabel.gameObject.SetActive(isImage);
            if (_textEditRow != null) _textEditRow.SetActive(false); // Shown only after clicking a text

            // Refresh camera list
            RefreshCameraList();
        }

        private void RefreshCameraList()
        {
            if (_cameraDropdown == null) return;

            var options = new List<string> { "UI Only" };
            var cameraList = new List<Camera>();

            try
            {
                var allCams = TypeHelper.FindAllObjectsOfType(typeof(Camera));
                if (allCams != null)
                {
                    foreach (var obj in allCams)
                    {
                        Camera cam = obj as Camera;
                        if (cam == null)
                            cam = TypeHelper.Il2CppCast(obj, typeof(Camera)) as Camera;
                        if (cam != null && cam.gameObject.activeInHierarchy)
                            cameraList.Add(cam);
                    }
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[Inspector] RefreshCameraList error: {ex.Message}");
            }

            _sceneCameras = cameraList.ToArray();
            foreach (var cam in _sceneCameras)
            {
                string type = cam.orthographic ? "ortho" : "persp";
                options.Add($"{cam.name} ({type})");
            }

            _cameraNames = options.ToArray();
            _cameraDropdown.SetOptions(_cameraNames);
            _cameraDropdown.SelectedValue = "UI Only";
            _selectedCamera = null;

        }

        private void OnCameraSelected(string value)
        {
            if (value == "UI Only" || string.IsNullOrEmpty(value))
            {
                _selectedCamera = null;
            }
            else
            {
                _selectedCamera = null;
                foreach (var cam in _sceneCameras)
                {
                    if (cam != null && value.StartsWith(cam.name))
                    {
                        _selectedCamera = cam;
                        break;
                    }
                }
            }

            ClearSelection();
            ClearHover();
        }

        public override void SetActive(bool active)
        {
            bool wasActive = Enabled;
            base.SetActive(active);

            if (active)
            {
                _isInspecting = true;
                if (!wasActive)
                {
                    ClearSelection();
                    ClearHover();
                    _statusLabel.text = "";
                    UpdateUIForMode();

                    // Hide MainPanel during inspection to clear the view
                    var mainPanel = TranslatorUIManager.MainPanel;
                    _mainPanelWasOpen = mainPanel != null && mainPanel.Enabled;
                    if (_mainPanelWasOpen)
                        mainPanel.SetActive(false);
                }
                if (_highlightCanvas != null)
                    _highlightCanvas.SetActive(true);
            }
            else
            {
                _isInspecting = false;
                HideAllHighlights();
                if (_highlightCanvas != null)
                    _highlightCanvas.SetActive(false);

                // Restore MainPanel if it was open before inspection
                if (_mainPanelWasOpen)
                {
                    TranslatorUIManager.MainPanel?.SetActive(true);
                }
                _mainPanelWasOpen = false;
            }
        }

        public override void Update()
        {
            base.Update();

            if (!_isInspecting || !Enabled) return;

            // Throttle raycast: every 2 frames for hover (smooth enough, saves perf)
            _frameSkip++;
            bool doHoverRaycast = (_frameSkip % 2 == 0);

            Vector3 mousePos = InputManager.MousePosition;

            // Skip if mouse is over our panel
            if (Rect != null && IsMouseOverPanel(mousePos))
            {
                // Hide hover highlight when over our panel
                if (_hoverHighlight != null) _hoverHighlight.gameObject.SetActive(false);
                ClearHoverLabel();
                return;
            }

            // --- Hover detection (every 2 frames) ---
            if (doHoverRaycast)
            {
                var hoveredObject = RaycastUIElement(mousePos);

                if (hoveredObject != null)
                {
                    // Skip our own UI
                    if (IsOwnUI(hoveredObject))
                    {
                        if (_hoverHighlight != null) _hoverHighlight.gameObject.SetActive(false);
                        ClearHoverLabel();
                    }
                    else
                    {
                        string path = TranslatorCore.GetGameObjectPath(hoveredObject);
                        if (path != _lastHoveredPath)
                        {
                            _lastHoveredPath = path;
                            _hoveredPathLabel.text = path;
                            _hoveredPathLabel.color = UIStyles.TextSecondary;
                            _hoveredPathLabel.fontStyle = FontStyle.Italic;
                        }

                        // Position hover highlight
                        PositionHighlight(_hoverHighlightRect, _hoverHighlight, hoveredObject);
                    }
                }
                else
                {
                    if (_hoverHighlight != null) _hoverHighlight.gameObject.SetActive(false);
                    ClearHoverLabel();
                }
            }

            // --- Click detection (select) ---
            if (InputManager.GetMouseButtonDown(0))
            {
                var hitObject = RaycastUIElement(mousePos);

                if (hitObject != null && !IsOwnUI(hitObject))
                {
                    string path = TranslatorCore.GetGameObjectPath(hitObject);
                    _lastSelectedPath = path;
                    _lastSelectedObject = hitObject;
                    _lastSelectedSpriteObj = null;

                    _selectedPathLabel.text = path;
                    _selectedPathLabel.color = UIStyles.TextPrimary;
                    _selectedPathLabel.fontStyle = FontStyle.Normal;
                    _cancelBtn.Component.interactable = true;

                    if (_currentMode == InspectorMode.TextEdit)
                    {
                        ShowTextEditUI(hitObject, path);
                    }
                    else if (_currentMode == InspectorMode.FontOverride)
                    {
                        // Font override mode: add override for parent path with /** to cover siblings
                        // e.g. "Canvas/Panel/Table/Text" → "path:Canvas/Panel/Table/**"
                        string overridePath = path;
                        int lastSlash = path.LastIndexOf('/');
                        if (lastSlash > 0)
                            overridePath = path.Substring(0, lastSlash) + "/**";
                        // Close inspector FIRST (restores MainPanel if it was open)
                        SetActive(false);
                        // THEN open TranslationParamsPanel (SetAsLastSibling puts it on top)
                        TranslatorUIManager.TranslationParamsPanel?.AddFontOverrideFromInspector("path:" + overridePath);
                        return;
                    }
                    else if (_currentMode == InspectorMode.BitmapReplace)
                    {
                        try
                        {
                            _lastSelectedSpriteObj = ImageReplacer.GetSpriteFromComponent(hitObject);
                            var spriteName = ImageReplacer.GetSpriteName(_lastSelectedSpriteObj) ?? "(unnamed)";
                            var size = ImageReplacer.GetSpriteSize(_lastSelectedSpriteObj);
                            var compType = ImageReplacer.GetComponentTypeName(hitObject);
                            _spriteInfoLabel.text = $"{compType}: \"{spriteName}\" ({size.x}x{size.y})";
                            _spriteInfoLabel.color = UIStyles.TextPrimary;

                            _exportOriginalBtn.Component.interactable = _lastSelectedSpriteObj != null;
                            _markReplaceBtn.Component.interactable = _lastSelectedSpriteObj != null;
                        }
                        catch (Exception ex)
                        {
                            TranslatorCore.LogDebug($"[Inspector] BitmapReplace click handler error: {ex}");
                        }
                    }
                    else
                    {
                        _excludeThisBtn.Component.interactable = true;
                        _excludePatternBtn.Component.interactable = true;
                    }

                    SetDynamicText(_statusLabel, "Element selected");
                    _statusLabel.color = UIStyles.StatusSuccess;

                    // Position selected highlight
                    PositionHighlight(_selectedHighlightRect, _selectedHighlight, hitObject);
                }
            }

            // Keep selected highlight tracking (object may move)
            if (_lastSelectedObject != null && _selectedHighlight != null && _selectedHighlight.gameObject.activeSelf)
            {
                // Re-position every ~10 frames to track moving elements
                if (_frameSkip % 10 == 0)
                    PositionHighlight(_selectedHighlightRect, _selectedHighlight, _lastSelectedObject);
            }
        }

        /// <summary>
        /// Check if a GameObject is part of our mod UI (IL2CPP-safe).
        /// Uses hierarchy name check — no generic Unity methods that crash on IL2CPP JIT.
        /// </summary>
        private bool IsOwnUI(GameObject obj)
        {
            if (obj == null) return false;

            // Check hierarchy by name — works on both Mono and IL2CPP without any
            // generic method calls (GetComponents<T>() crashes at JIT on IL2CPP)
            var current = obj.transform;
            while (current != null)
            {
                string name = current.name;
                if (name.StartsWith("UGT_") || name.StartsWith("UniverseLibCanvas")
                    || name.StartsWith("UniverseLib_") || name == "UGT_InspectorHighlight")
                    return true;
                current = current.parent;
            }

            return false;
        }

        /// <summary>
        /// Check if mouse position is over this panel's rect.
        /// Uses TransformPoint instead of GetWorldCorners (IL2CPP-safe).
        /// </summary>
        private bool IsMouseOverPanel(Vector3 screenPos)
        {
            if (Rect == null) return false;

            Vector2 screenMin, screenMax;
            if (!GetScreenBounds(Rect, out screenMin, out screenMax))
                return false;

            return screenPos.x >= screenMin.x && screenPos.x <= screenMax.x &&
                   screenPos.y >= screenMin.y && screenPos.y <= screenMax.y;
        }

        /// <summary>
        /// Get screen-space bounds of a RectTransform using TransformPoint (IL2CPP-safe).
        /// GetWorldCorners(Vector3[]) crashes on IL2CPP because the array param becomes
        /// Il2CppStructArray — using TransformPoint(Vector3) avoids this entirely.
        /// </summary>
        private static bool GetScreenBounds(RectTransform rect, out Vector2 screenMin, out Vector2 screenMax)
        {
            screenMin = Vector2.zero;
            screenMax = Vector2.zero;

            if (rect == null) return false;

            try
            {
                // Get the local rect (x, y, width, height in local space)
                Rect localRect = rect.rect;

                // Transform the 4 corners from local to world space
                // TransformPoint takes a Vector3 value type — no IL2CPP array issues
                Vector3 c0 = rect.TransformPoint(new Vector3(localRect.xMin, localRect.yMin, 0));
                Vector3 c1 = rect.TransformPoint(new Vector3(localRect.xMin, localRect.yMax, 0));
                Vector3 c2 = rect.TransformPoint(new Vector3(localRect.xMax, localRect.yMax, 0));
                Vector3 c3 = rect.TransformPoint(new Vector3(localRect.xMax, localRect.yMin, 0));

                // For ScreenSpaceOverlay canvases, world coords = screen coords
                // For other render modes, we'd need camera conversion
                float minX = Mathf.Min(c0.x, c1.x, c2.x, c3.x);
                float maxX = Mathf.Max(c0.x, c1.x, c2.x, c3.x);
                float minY = Mathf.Min(c0.y, c1.y, c2.y, c3.y);
                float maxY = Mathf.Max(c0.y, c1.y, c2.y, c3.y);

                // Check if the target might be on a non-Overlay canvas — convert via camera
                // Walk up to find the root Canvas
                Canvas rootCanvas = null;
                try
                {
                    // GetComponentInParent<Canvas>() should be safe on IL2CPP (single generic param, no arrays)
                    rootCanvas = rect.GetComponentInParent<Canvas>();
                    if (rootCanvas != null) rootCanvas = rootCanvas.rootCanvas;
                }
                catch { }

                if (rootCanvas != null && rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
                {
                    var cam = rootCanvas.worldCamera;
                    if (cam != null)
                    {
                        Vector3 s0 = cam.WorldToScreenPoint(c0);
                        Vector3 s1 = cam.WorldToScreenPoint(c1);
                        Vector3 s2 = cam.WorldToScreenPoint(c2);
                        Vector3 s3 = cam.WorldToScreenPoint(c3);

                        minX = Mathf.Min(s0.x, s1.x, s2.x, s3.x);
                        maxX = Mathf.Max(s0.x, s1.x, s2.x, s3.x);
                        minY = Mathf.Min(s0.y, s1.y, s2.y, s3.y);
                        maxY = Mathf.Max(s0.y, s1.y, s2.y, s3.y);
                    }
                }

                screenMin = new Vector2(minX, minY);
                screenMax = new Vector2(maxX, maxY);
                return true;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogDebug($"[Inspector] GetScreenBounds failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Get screen-space bounds for a world-space object (SpriteRenderer).
        /// Uses Renderer.bounds projected to screen via Camera.main.
        /// </summary>
        private static bool GetScreenBoundsFromRenderer(GameObject target, out Vector2 screenMin, out Vector2 screenMax)
        {
            screenMin = screenMax = Vector2.zero;
            try
            {
                var camera = Camera.main;
                if (camera == null) return false;

                // Try to get Renderer.bounds via reflection
                var renderer = target.GetComponent<Renderer>();
                if (renderer == null) return false;

                var bounds = renderer.bounds;
                if (bounds.size == Vector3.zero) return false;

                Vector3 center = bounds.center;
                Vector3 extents = bounds.extents;

                // Project 4 corners to screen space
                Vector3 s0 = camera.WorldToScreenPoint(center + new Vector3(-extents.x, -extents.y, 0));
                Vector3 s1 = camera.WorldToScreenPoint(center + new Vector3(extents.x, -extents.y, 0));
                Vector3 s2 = camera.WorldToScreenPoint(center + new Vector3(-extents.x, extents.y, 0));
                Vector3 s3 = camera.WorldToScreenPoint(center + new Vector3(extents.x, extents.y, 0));

                if (!camera.orthographic && s0.z < 0) return false; // Behind perspective camera

                float minX = Mathf.Min(Mathf.Min(s0.x, s1.x), Mathf.Min(s2.x, s3.x));
                float maxX = Mathf.Max(Mathf.Max(s0.x, s1.x), Mathf.Max(s2.x, s3.x));
                float minY = Mathf.Min(Mathf.Min(s0.y, s1.y), Mathf.Min(s2.y, s3.y));
                float maxY = Mathf.Max(Mathf.Max(s0.y, s1.y), Mathf.Max(s2.y, s3.y));

                screenMin = new Vector2(minX, minY);
                screenMax = new Vector2(maxX, maxY);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ClearHoverLabel()
        {
            if (_lastHoveredPath != "")
            {
                _lastHoveredPath = "";
                SetDynamicText(_hoveredPathLabel, "(move cursor over a UI element)");
                _hoveredPathLabel.color = UIStyles.TextMuted;
                _hoveredPathLabel.fontStyle = FontStyle.Italic;
            }
        }

        private void ClearHover()
        {
            ClearHoverLabel();
            if (_hoverHighlight != null) _hoverHighlight.gameObject.SetActive(false);
        }

        private void ClearSelection()
        {
            _lastSelectedPath = "";
            _lastSelectedObject = null;
            _lastSelectedSpriteObj = null;
            SetDynamicText(_selectedPathLabel, "(click to select)");
            _selectedPathLabel.color = UIStyles.TextMuted;
            _selectedPathLabel.fontStyle = FontStyle.Italic;
            _excludeThisBtn.Component.interactable = false;
            _excludePatternBtn.Component.interactable = false;
            _exportOriginalBtn.Component.interactable = false;
            _markReplaceBtn.Component.interactable = false;
            _cancelBtn.Component.interactable = false;
            if (_spriteInfoLabel != null) _spriteInfoLabel.text = "";
            if (_selectedHighlight != null) _selectedHighlight.gameObject.SetActive(false);

            // Clear TextEdit UI
            _pendingRetranslateRows.Clear();
            if (_textEditRow != null) _textEditRow.SetActive(false);
            if (_textEditListContent != null)
            {
                for (int i = _textEditListContent.transform.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(_textEditListContent.transform.GetChild(i).gameObject);
            }
        }

        private void OnExcludeThisClicked()
        {
            if (string.IsNullOrEmpty(_lastSelectedPath)) return;

            TranslatorCore.AddExclusion(_lastSelectedPath);

            SetDynamicText(_statusLabel, "Excluded!");
            _statusLabel.color = UIStyles.StatusSuccess;

            ClearSelection();
        }

        private void OnExcludePatternClicked()
        {
            if (string.IsNullOrEmpty(_lastSelectedPath) || _lastSelectedObject == null) return;

            string objectName = _lastSelectedObject.name;
            string pattern = "**/" + objectName;

            TranslatorCore.AddExclusion(pattern);
            TranslatorCore.SaveCache();

            _statusLabel.text = Tr("Excluded:") + $" {pattern}";
            _statusLabel.color = UIStyles.StatusSuccess;

            ClearSelection();
        }

        private void OnCancelClicked()
        {
            ClearSelection();
            _statusLabel.text = "";
        }

        #region BitmapReplace Actions

        private void OnExportOriginalClicked()
        {
            if (_lastSelectedSpriteObj == null) return;

            var spriteName = ImageReplacer.GetSpriteName(_lastSelectedSpriteObj);
            if (string.IsNullOrEmpty(spriteName))
            {
                SetDynamicText(_statusLabel, "Cannot export: sprite has no name");
                _statusLabel.color = UIStyles.StatusError;
                return;
            }

            // If not already marked, mark it first
            if (!ImageReplacer.GetAll().ContainsKey(spriteName))
            {
                MarkCurrentForReplace(spriteName);
            }

            var exportedPath = ImageReplacer.ExportOriginal(_lastSelectedSpriteObj, spriteName);
            if (exportedPath != null)
            {
                _statusLabel.text = $"Exported: {System.IO.Path.GetFileName(exportedPath)}";
                _statusLabel.color = UIStyles.StatusSuccess;
                TranslatorCore.SaveCache();
            }
            else
            {
                SetDynamicText(_statusLabel, "Export failed (check log)");
                _statusLabel.color = UIStyles.StatusError;
            }
        }

        private void OnMarkReplaceClicked()
        {
            if (_lastSelectedSpriteObj == null) return;

            var spriteName = ImageReplacer.GetSpriteName(_lastSelectedSpriteObj);
            if (string.IsNullOrEmpty(spriteName))
            {
                SetDynamicText(_statusLabel, "Cannot mark: sprite has no name");
                _statusLabel.color = UIStyles.StatusError;
                return;
            }

            MarkCurrentForReplace(spriteName);

            _statusLabel.text = Tr("Marked:") + $" {spriteName}";
            _statusLabel.color = UIStyles.StatusSuccess;

            TranslatorCore.SaveCache();
            ClearSelection();
        }

        private void MarkCurrentForReplace(string spriteName)
        {
            var size = ImageReplacer.GetSpriteSize(_lastSelectedSpriteObj);

            Vector2 pivot = new Vector2(0.5f, 0.5f);
            float ppu = 100f;
            Vector4 border = Vector4.zero;
            TextureUtils.GetSpriteProperties(_lastSelectedSpriteObj, out pivot, out ppu, out border);

            ImageReplacer.AddReplacement(spriteName, _lastSelectedPath,
                size.x, size.y, pivot, border, ppu);
        }

        #endregion

        #region TextEdit Mode

        private void ShowTextEditUI(GameObject hitObject, string path)
        {
            if (_textEditRow == null || _textEditListContent == null) return;

            var textEntries = new List<(object component, string text, string originalKey, string tag, string childPath, Dictionary<int, string> liveNumbers)>();

            // Try the clicked path, then walk up the hierarchy until we find text components
            string searchPath = path;
            for (int attempt = 0; attempt < 3 && textEntries.Count == 0; attempt++)
            {
                FindTextComponentsAtPath(searchPath, textEntries);

                if (textEntries.Count == 0)
                {
                    // Go up one level
                    int lastSlash = searchPath.LastIndexOf('/');
                    if (lastSlash <= 0) break;
                    searchPath = searchPath.Substring(0, lastSlash);
                }
            }

            if (textEntries.Count == 0)
            {
                SetDynamicText(_statusLabel, "No text components found");
                _statusLabel.color = UIStyles.StatusWarning;
                return;
            }

            // Clear previous entries — and with them any retranslation still expected for a row
            // that is about to be destroyed
            _pendingRetranslateRows.Clear();
            for (int i = _textEditListContent.transform.childCount - 1; i >= 0; i--)
                UnityEngine.Object.Destroy(_textEditListContent.transform.GetChild(i).gameObject);

            // Show the edit UI
            _textEditRow.SetActive(true);
            SetDynamicText(_textEditCountLabel, $"{textEntries.Count} text(s) found:");

            // Create an editable row for each text
            for (int i = 0; i < textEntries.Count; i++)
            {
                CreateTextEditRow(textEntries[i]);
            }

            SizeTextEditList(textEntries.Count);

            SetDynamicText(_statusLabel, "Edit translations and click Save");
            _statusLabel.color = UIStyles.TextSecondary;
        }

        /// <summary>
        /// Ask the window for the height this many rows actually need, and let it grow.
        ///
        /// ⚠ The panel measures its content to size itself, and a scrolling list is weighed at its
        /// PREFERRED height, not at what it holds — so a list left at its floor keeps the window at
        /// the size of one or two rows however many were found. Revising the floor is what makes
        /// the measurement tell the truth. A size the user picked themselves still wins:
        /// RecalculateSize leaves it alone.
        /// </summary>
        private void SizeTextEditList(int rowCount)
        {
            if (_textEditScroll == null) return;

            int wanted = Mathf.Clamp(rowCount * TextEditRowHeight, TextEditListMinHeight, TextEditListMaxHeight);
            UIFactory.SetLayoutElement(_textEditScroll, minHeight: wanted, preferredHeight: wanted,
                flexibleHeight: 9999, flexibleWidth: 9999);

            RecalculateSize();
        }

        /// <summary>
        /// Find all text components whose path matches or is a child of the given path.
        /// Uses FindAllObjectsOfType (IL2CPP-safe).
        /// </summary>
        private void FindTextComponentsAtPath(string pathPrefix,
            List<(object component, string text, string originalKey, string tag, string childPath, Dictionary<int, string> liveNumbers)> results)
        {
            var seenIds = new HashSet<int>();

            var textTypes = new List<Type>();
            if (TypeHelper.UI_TextType != null) textTypes.Add(TypeHelper.UI_TextType);
            if (TypeHelper.TMP_TextType != null) textTypes.Add(TypeHelper.TMP_TextType);

            foreach (var textType in textTypes)
            {
                var allComponents = TypeHelper.FindAllObjectsOfType(textType);
                if (allComponents == null) continue;

                for (int i = 0; i < allComponents.Length; i++)
                {
                    var obj = allComponents[i];
                    if (obj == null) continue;
                    try
                    {
                        Component comp = obj as Component;
                        if (comp == null)
                            comp = TypeHelper.Il2CppCast(obj, typeof(Component)) as Component;
                        if (comp == null || comp.gameObject == null) continue;

                        int id = comp.GetInstanceID();
                        if (seenIds.Contains(id)) continue;
                        if (TranslatorCore.IsOwnUI(comp)) continue;

                        string compPath = TranslatorCore.GetGameObjectPath(comp.gameObject);
                        if (compPath != pathPrefix && !compPath.StartsWith(pathPrefix + "/"))
                            continue;

                        string text = TypeHelper.GetText(obj);
                        if (string.IsNullOrEmpty(text)) continue;

                        seenIds.Add(id);

                        // Resolve to the cache entry with pipeline normalization, so texts
                        // with dynamic numbers map to their [!v*N] pattern key
                        var resolution = TranslatorCore.ResolveDisplayedText(text);
                        if (resolution == null) continue;

                        string tag = resolution.Entry?.Tag ?? "—";
                        // Prefill: current translation with placeholders, or the normalized
                        // source text when no entry exists yet
                        string translation = resolution.Entry?.Value ?? resolution.Key;

                        results.Add((obj, translation, resolution.Key, tag, compPath, resolution.CapturedNumbers));
                    }
                    catch { }
                }
            }
        }

        private void CreateTextEditRow((object component, string text, string originalKey, string tag, string childPath, Dictionary<int, string> liveNumbers) entry)
        {
            var row = UIFactory.CreateVerticalGroup(_textEditListContent, "TextEditEntry", false, false, true, true, 3);
            UIFactory.SetLayoutElement(row, flexibleWidth: 9999);
            UIStyles.SetBackground(row, UIStyles.CardBackground);

            // Original key: full text, word-wrapped (translating needs the whole source).
            //
            // ⚠ supportRichText: false, and this is the whole point of the row. Left on — the
            // UIFactory default — the label RENDERS `<color=#FF0000>` instead of showing it, so a
            // decorated line appeared coloured with its markup invisible, and there was no way to
            // see what had to be preserved while editing. What is edited here is the file's exact
            // text; that is what has to be on screen. The rendering is shown separately below.
            var keyLabel = UIFactory.CreateLabel(row, "Key", $"[{entry.tag}] {entry.originalKey}", TextAnchor.UpperLeft,
                                                 supportRichText: false);
            keyLabel.fontSize = UIStyles.FontSizeSmall;
            keyLabel.color = UIStyles.TextMuted;
            keyLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            UIFactory.SetLayoutElement(keyLabel.gameObject, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);

            // Live values of the [!v*N] placeholders, as currently displayed in-game
            if (entry.liveNumbers != null && entry.liveNumbers.Count > 0)
            {
                var parts = new List<string>();
                foreach (var kv in entry.liveNumbers)
                    parts.Add($"[!v*{kv.Key}] = {kv.Value}");
                var hintLabel = UIFactory.CreateLabel(row, "LiveValues",
                    $"Keep placeholders as-is. Current values: {string.Join("   ", parts)}", TextAnchor.UpperLeft,
                    supportRichText: false);
                hintLabel.fontSize = UIStyles.FontSizeHint;
                hintLabel.color = UIStyles.TextAccent;
                hintLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
                UIFactory.SetLayoutElement(hintLabel.gameObject, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);
            }

            // Editable translation field — raw text, markup included, exactly as the file holds it
            var input = UIFactory.CreateInputField(row, "TranslationInput", "Enter translation...");
            UIFactory.SetLayoutElement(input.Component.gameObject, minHeight: 40, flexibleWidth: 9999);
            UIStyles.SetBackground(input.Component.gameObject, UIStyles.InputBackground);
            input.Component.lineType = UnityEngine.UI.InputField.LineType.MultiLineNewline;
            if (input.Component.textComponent != null)
                input.Component.textComponent.supportRichText = false;
            input.Text = entry.text;

            // …and right under it, the same string RENDERED. One shows what you are editing, the
            // other what the game will draw — a colour tag broken while typing shows up here
            // immediately, instead of on a screen you have to go back to.
            var previewLabel = UIFactory.CreateLabel(row, "Preview", "", TextAnchor.UpperLeft);
            previewLabel.fontSize = UIStyles.FontSizeSmall;
            previewLabel.color = UIStyles.TextSecondary;
            previewLabel.horizontalOverflow = HorizontalWrapMode.Wrap;
            UIFactory.SetLayoutElement(previewLabel.gameObject, minHeight: UIStyles.RowHeightSmall, flexibleWidth: 9999);
            RegisterExcluded(previewLabel);

            // Buttons row
            var btnRow = UIFactory.CreateHorizontalGroup(row, "BtnRow", false, false, true, true, 4);
            UIFactory.SetLayoutElement(btnRow, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);

            // Everything this row needs, in one place: its handlers, the answer that arrives
            // seconds later on another thread, and the button-state refresh all work from it.
            // Passing the pieces around separately is how one of them ends up forgotten.
            var rowState = new TextEditRowState
            {
                Key = entry.originalKey,
                Component = entry.component,
                LiveNumbers = entry.liveNumbers,
                Input = input,
                KeyLabel = keyLabel,
                PreviewLabel = previewLabel
            };
            string capturedKey = entry.originalKey;
            object capturedComponent = entry.component;
            var capturedNumbers = entry.liveNumbers;

            var saveBtn = UIFactory.CreateButton(btnRow, "SaveBtn", "Save (H)");
            UIFactory.SetLayoutElement(saveBtn.Component.gameObject, minWidth: 80, minHeight: UIStyles.RowHeightNormal);
            UIStyles.SetBackground(saveBtn.Component.gameObject, UIStyles.ButtonSuccess);

            var retranslateBtn = UIFactory.CreateButton(btnRow, "RetranslateBtn", "Retranslate (AI)");
            UIFactory.SetLayoutElement(retranslateBtn.Component.gameObject, minWidth: 110, minHeight: UIStyles.RowHeightNormal);

            var revertBtn = UIFactory.CreateButton(btnRow, "RevertBtn", "Revert");
            UIFactory.SetLayoutElement(revertBtn.Component.gameObject, minWidth: 70, minHeight: UIStyles.RowHeightNormal);
            _helpZone?.Describe(revertBtn.Component.gameObject,
                "Put the field back to what the translation file holds, discarding what you typed or what the AI proposed.");

            rowState.SaveBtn = saveBtn;
            rowState.RetranslateBtn = retranslateBtn;
            rowState.RevertBtn = revertBtn;

            // Both buttons exist before either handler is written: each one has to be able to put
            // the other back in its right state, and a lambda cannot reach a local declared later.
            saveBtn.OnClick += () =>
            {
                string newValue = input.Text;
                if (string.IsNullOrEmpty(newValue)) return;
                // Saving an unchanged field is not a no-op: it would stamp the line "H", turning a
                // machine translation into a human one nobody wrote. The button is greyed for it,
                // and refuses anyway — a greyed button is a hint, not a guarantee.
                if (!HasUnsavedEdit(capturedKey, newValue)) return;

                // Refuse edits that drop or invent placeholders — they would break
                // dynamic numbers for every future value
                string placeholderError = TranslatorCore.ValidateEditedPlaceholders(capturedKey, newValue);
                if (placeholderError != null)
                {
                    _statusLabel.text = $"Not saved — {placeholderError}";
                    _statusLabel.color = UIStyles.StatusError;
                    return;
                }

                // Who wrote what is being saved. Accepting an AI proposal untouched files it as A:
                // stamping H would claim a review nobody performed, and that tag drives the
                // quality score, the A → V gesture and what the community sees.
                string tag = rowState.AiProposal != null
                             && string.Equals(newValue, rowState.AiProposal, StringComparison.Ordinal)
                    ? "A" : "H";

                TranslatorCore.SetTranslationFromEditor(capturedKey, newValue, tag);

                // Apply immediately to the component, with the live numbers re-injected
                try
                {
                    TypeHelper.SetText(capturedComponent,
                        TranslatorCore.RestoreNumbersFromPlaceholders(newValue, capturedNumbers));
                }
                catch { }

                SetDynamicText(_statusLabel, tag == "A" ? "AI translation applied" : "Saved!");
                _statusLabel.color = UIStyles.StatusSuccess;
                keyLabel.text = $"[{tag}] {capturedKey}";
                RefreshRow(rowState);
            };

            retranslateBtn.OnClick += () =>
            {
                if (TranslatorCore.Config == null || !TranslatorCore.Config.IsTranslationEnabled)
                {
                    SetDynamicText(_statusLabel, "Translation is switched off — turn it on in Options first");
                    _statusLabel.color = UIStyles.StatusWarning;
                    return;
                }

                // No confirmation here any more, and its absence is deliberate: a retranslation
                // now only PROPOSES. A hand-written line is replaced by the Save click, never by
                // this one — asking twice for something that has not happened yet trains people to
                // dismiss the question. The browser still asks, because there it does write.
                StartRetranslate(rowState);
            };

            revertBtn.OnClick += () =>
            {
                // Back to what the file holds — the AI's proposal and anything typed both go.
                input.Text = TranslatorCore.GetTranslationValue(capturedKey) ?? capturedKey;
                rowState.AiProposal = null;
                keyLabel.text = $"[{TranslatorCore.GetTranslationTag(capturedKey) ?? "—"}] {capturedKey}";
                SetDynamicText(_statusLabel, "Back to the saved translation");
                _statusLabel.color = UIStyles.TextSecondary;
                RefreshRow(rowState);
            };

            // The C# event of InputFieldRef, never onValueChanged.AddListener — see UIHelpers.
            input.OnValueChanged += _ => RefreshRow(rowState);
            RefreshRow(rowState);
        }

        /// <summary>
        /// True when the field holds something the file does not. The comparison is against the
        /// stored value read NOW, so a line the AI or the browser changed under an untouched field
        /// settles back to "nothing to save" on its own.
        /// </summary>
        private static bool HasUnsavedEdit(string key, string fieldText)
        {
            if (string.IsNullOrEmpty(fieldText)) return false;
            // No entry yet: the field was prefilled with the source text, so saving it verbatim
            // would file the source as its own translation — still nothing worth saving.
            string stored = TranslatorCore.GetTranslationValue(key) ?? key;
            return !string.Equals(fieldText, stored, StringComparison.Ordinal);
        }

        /// <summary>
        /// Grey out what would do nothing — Save and Revert while the field matches the file,
        /// Retranslate while an answer for that line is already on its way — and keep the rendered
        /// preview in step with what is being typed.
        /// </summary>
        private void RefreshRow(TextEditRowState row)
        {
            if (row?.Input?.Component == null) return;

            bool changed = HasUnsavedEdit(row.Key, row.Input.Text);

            if (row.SaveBtn?.Component != null)
                row.SaveBtn.Component.interactable = changed;

            // Revert answers the same question as Save, from the other side: there is something to
            // undo exactly when there is something to save.
            if (row.RevertBtn?.Component != null)
                row.RevertBtn.Component.interactable = changed;

            if (row.RetranslateBtn?.Component != null)
                row.RetranslateBtn.Component.interactable = !_pendingRetranslateRows.Contains(row);

            if (row.PreviewLabel != null)
            {
                string field = row.Input.Text ?? "";
                // Shown only when there is markup to interpret. On a plain line the preview would
                // repeat the field word for word, costing a row of height per entry in a list that
                // routinely holds a dozen — and this panel was reported as too short.
                bool worthShowing = field.IndexOf('<') >= 0 && field.IndexOf('>') >= 0;
                row.PreviewLabel.gameObject.SetActive(worthShowing);
                if (worthShowing)
                {
                    // The only place in this row where markup is meant to be interpreted. Numbers
                    // are put back too, so this is the line as the game would draw it right now.
                    row.PreviewLabel.text = TranslatorCore.RestoreNumbersFromPlaceholders(
                        field, row.LiveNumbers);
                }
            }
        }

        /// <summary>
        /// Ask the AI for another translation of this line. It PROPOSES: nothing is written, the
        /// answer lands in the field and waits for Save — which is why there is no confirmation
        /// step and nothing to lose if it goes wrong.
        /// </summary>
        private void StartRetranslate(TextEditRowState row)
        {
            if (row == null) return;
            if (!_pendingRetranslateRows.Contains(row))
                _pendingRetranslateRows.Add(row);

            if (!TranslatorCore.RemoveTranslationForRetranslate(row.Key, storeResult: false))
            {
                _pendingRetranslateRows.Remove(row);
                SetDynamicText(_statusLabel, "Could not ask the AI — check the backend in Options");
                _statusLabel.color = UIStyles.StatusError;
                row.KeyLabel.text = $"[{TranslatorCore.GetTranslationTag(row.Key) ?? "—"}] {row.Key}";
                RefreshRow(row);
                return;
            }

            SetDynamicText(_statusLabel, "Asking the AI for another translation...");
            _statusLabel.color = UIStyles.TextAccent;
            row.KeyLabel.text = $"[AI...] {row.Key}";
            RefreshRow(row);
        }

        /// <summary>
        /// A retranslation ended. Raised on the WORKER thread — everything below touches Unity
        /// objects, so it hops to the main thread first.
        /// </summary>
        private void OnRetranslateFinished(string key, string value, TranslatorCore.RetranslateOutcome outcome)
        {
            TranslatorUIManager.RunOnMainThread(() => ApplyRetranslateResult(key, value, outcome));
        }

        private void ApplyRetranslateResult(string key, string value, TranslatorCore.RetranslateOutcome outcome)
        {
            var rows = _pendingRetranslateRows.FindAll(r => r.Key == key);
            if (rows.Count == 0)
            {
                // The rows were destroyed while the AI was answering — another element was clicked,
                // or the selection was cleared. The proposal has nowhere to land and, being a
                // proposal, was never written anywhere: it is simply lost. Said out loud rather
                // than dropped in silence; nothing is damaged, but "I asked and got nothing" must
                // have an explanation somewhere.
                TranslatorCore.LogInfo("[Retranslate] Answer arrived after its row was gone — proposal discarded");
                return;
            }
            _pendingRetranslateRows.RemoveAll(r => r.Key == key);

            bool proposed = outcome == TranslatorCore.RetranslateOutcome.Replaced && value != null;

            foreach (var row in rows)
            {
                // The row may have been destroyed since (another element was clicked)
                if (row.Input?.Component == null || row.KeyLabel == null) continue;

                if (proposed)
                {
                    // Into the FIELD, not into the file and not onto the game screen. Remembered
                    // so that accepting it untouched can be filed as A rather than as human work.
                    row.AiProposal = value;
                    row.Input.Text = value;
                }

                row.KeyLabel.text = $"[{TranslatorCore.GetTranslationTag(key) ?? "—"}] {key}";
                RefreshRow(row);
            }

            switch (outcome)
            {
                case TranslatorCore.RetranslateOutcome.Replaced:
                    SetDynamicText(_statusLabel, "New translation proposed — Save to keep it, Revert to drop it");
                    _statusLabel.color = UIStyles.StatusSuccess;
                    break;
                case TranslatorCore.RetranslateOutcome.Unchanged:
                    SetDynamicText(_statusLabel, "The AI gave the same translation again — nothing changed");
                    _statusLabel.color = UIStyles.StatusWarning;
                    break;
                default:
                    SetDynamicText(_statusLabel, "The AI returned nothing — the line is untouched");
                    _statusLabel.color = UIStyles.StatusError;
                    break;
            }
        }

        #endregion

        private void OnStopClicked()
        {
            SetActive(false);

            // Return to the appropriate TranslationParametersPanel tab
            if (_currentMode == InspectorMode.BitmapReplace)
                TranslatorUIManager.TranslationParamsPanel?.OpenOnBitmapReplaceTab();
            else if (_currentMode == InspectorMode.FontOverride)
                TranslatorUIManager.TranslationParamsPanel?.OpenOnFontOverridesTab();
            else if (_currentMode == InspectorMode.TextEdit)
                TranslatorUIManager.TranslationParamsPanel?.OpenOnToolsTab();
            else
                TranslatorUIManager.TranslationParamsPanel?.OpenOnExclusionsTab();
        }
    }
}
