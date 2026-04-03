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
    /// Inspector panel for visually selecting UI elements to exclude from translation.
    /// DevTools-style: hover preview with highlight overlay, click to select.
    /// All Unity API calls use reflection for IL2CPP compatibility.
    /// </summary>
    public class InspectorPanel : TranslatorPanelBase
    {
        public override string Name => "Element Inspector";
        public override int MinWidth => 420;
        public override int MinHeight => 360;
        public override int PanelWidth => 480;
        public override int PanelHeight => 420;

        protected override int MinPanelHeight => 360;

        // UI elements
        private Text _hoveredPathLabel;
        private Text _selectedPathLabel;
        private Text _statusLabel;
        private ButtonRef _excludeThisBtn;
        private ButtonRef _excludePatternBtn;
        private ButtonRef _cancelBtn;

        // State
        private bool _isInspecting = false;
        private string _lastHoveredPath = "";
        private string _lastSelectedPath = "";
        private GameObject _lastSelectedObject = null;
        private int _frameSkip = 0;
        private bool _mainPanelWasOpen = false;

        // Highlight overlay
        private GameObject _highlightCanvas;
        private Image _hoverHighlight;
        private Image _selectedHighlight;
        private RectTransform _hoverHighlightRect;
        private RectTransform _selectedHighlightRect;

        // Colors for highlights (DevTools-style)
        private static readonly Color HoverHighlightColor = new Color(0.20f, 0.56f, 0.85f, 0.25f);    // Blue 25% — like Chrome
        private static readonly Color SelectedHighlightColor = new Color(0.16f, 0.50f, 0.73f, 0.35f);  // Deeper blue 35%

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
                        _raycasterRaycastMethod.Invoke(raycaster, new[] { pointer, resultsList });

                        // Check count
                        int count = (int)_listCountProp.Invoke(resultsList, null);
                        if (count == 0) continue;

                        // Get first result
                        var firstResult = _listGetItem.Invoke(resultsList, new object[] { 0 });
                        if (firstResult == null) continue;

                        // Get gameObject from RaycastResult
                        var gameObj = _raycastResultGameObjectProp.GetValue(firstResult, null);
                        if (gameObj is GameObject go)
                            return go;

                        // IL2CPP: may need cast
                        if (gameObj != null)
                        {
                            var casted = TypeHelper.Il2CppCast(gameObj, typeof(GameObject));
                            if (casted is GameObject go2)
                                return go2;
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
        }

        protected override void ConstructPanelContent()
        {
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Title
            var title = CreateTitle(scrollContent, "Title", "Element Inspector");
            RegisterUIText(title);

            UIStyles.CreateSpacer(scrollContent, 5);

            // Main card
            var card = CreateAdaptiveCard(scrollContent, "InspectorCard", PanelWidth - 60, stretchVertically: true);

            // Instructions
            var instructionTitle = UIStyles.CreateSectionTitle(card, "InstructionsLabel", "Instructions");
            RegisterUIText(instructionTitle);

            var instructionHint = UIStyles.CreateHint(card, "InstructionsHint",
                "Hover over any UI element to preview it. Click to select and exclude from translation.");
            RegisterUIText(instructionHint);

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

            // --- Action buttons ---
            var actionsTitle = UIStyles.CreateSectionTitle(card, "ActionsLabel", "Actions");
            RegisterUIText(actionsTitle);

            var actionRow1 = UIStyles.CreateFormRow(card, "ActionRow1", UIStyles.ButtonHeight, 5);

            _excludeThisBtn = CreatePrimaryButton(actionRow1, "ExcludeThisBtn", "Exclude This Element");
            _excludeThisBtn.OnClick += OnExcludeThisClicked;
            _excludeThisBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_excludeThisBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_excludeThisBtn.ButtonText);

            _excludePatternBtn = CreateSecondaryButton(actionRow1, "ExcludePatternBtn", "Exclude Pattern");
            _excludePatternBtn.OnClick += OnExcludePatternClicked;
            _excludePatternBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_excludePatternBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_excludePatternBtn.ButtonText);

            var actionRow2 = UIStyles.CreateFormRow(card, "ActionRow2", UIStyles.ButtonHeight, 5);

            _cancelBtn = CreateSecondaryButton(actionRow2, "CancelBtn", "Clear Selection");
            _cancelBtn.OnClick += OnCancelClicked;
            _cancelBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_cancelBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_cancelBtn.ButtonText);

            // Status label
            UIStyles.CreateSpacer(card, 5);
            _statusLabel = UIFactory.CreateLabel(card, "Status", "", TextAnchor.MiddleLeft);
            _statusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_statusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Footer button (fixed at bottom)
            var stopBtn = CreatePrimaryButton(buttonRow, "StopBtn", "Stop Inspecting");
            stopBtn.OnClick += OnStopClicked;
            RegisterUIText(stopBtn.ButtonText);

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
            canvas.sortingOrder = 31000; // High but below our UI (UniverseLib uses 32000+)

            // Hover highlight
            var hoverObj = new GameObject("HoverHighlight");
            hoverObj.transform.SetParent(_highlightCanvas.transform, false);
            _hoverHighlight = hoverObj.AddComponent<Image>();
            _hoverHighlight.color = HoverHighlightColor;
            _hoverHighlight.raycastTarget = false; // Don't intercept clicks
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
            _selectedHighlight.raycastTarget = false;
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

            var targetRect = target.GetComponent<RectTransform>();
            if (targetRect == null)
            {
                highlightImage.gameObject.SetActive(false);
                return;
            }

            // Get screen-space bounds using TransformPoint on local rect corners
            // TransformPoint takes Vector3 (value type) — IL2CPP-safe, unlike GetWorldCorners(Vector3[])
            Vector2 screenMin, screenMax;
            if (!GetScreenBounds(targetRect, out screenMin, out screenMax))
            {
                highlightImage.gameObject.SetActive(false);
                return;
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
                    _mainPanelWasOpen = false;
                }
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

                    _selectedPathLabel.text = path;
                    _selectedPathLabel.color = UIStyles.TextPrimary;
                    _selectedPathLabel.fontStyle = FontStyle.Normal;
                    _excludeThisBtn.Component.interactable = true;
                    _excludePatternBtn.Component.interactable = true;
                    _cancelBtn.Component.interactable = true;

                    _statusLabel.text = "Element selected";
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

        private void ClearHoverLabel()
        {
            if (_lastHoveredPath != "")
            {
                _lastHoveredPath = "";
                _hoveredPathLabel.text = "(move cursor over a UI element)";
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
            _selectedPathLabel.text = "(click to select)";
            _selectedPathLabel.color = UIStyles.TextMuted;
            _selectedPathLabel.fontStyle = FontStyle.Italic;
            _excludeThisBtn.Component.interactable = false;
            _excludePatternBtn.Component.interactable = false;
            _cancelBtn.Component.interactable = false;
            if (_selectedHighlight != null) _selectedHighlight.gameObject.SetActive(false);
        }

        private void OnExcludeThisClicked()
        {
            if (string.IsNullOrEmpty(_lastSelectedPath)) return;

            TranslatorCore.AddExclusion(_lastSelectedPath);

            _statusLabel.text = "Excluded!";
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

            _statusLabel.text = $"Excluded: {pattern}";
            _statusLabel.color = UIStyles.StatusSuccess;

            ClearSelection();
        }

        private void OnCancelClicked()
        {
            ClearSelection();
            _statusLabel.text = "";
        }

        private void OnStopClicked()
        {
            SetActive(false);
            // Return to OptionsPanel on the Exclusions tab
            TranslatorUIManager.OptionsPanel?.OpenOnExclusionsTab();
        }
    }
}
