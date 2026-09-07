using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.Input;
using UnityGameTranslator.Core.UI.Panels;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// What the picker found, handed to <see cref="Panels.InspectorPanel"/> without a single Unity
    /// type: <see cref="Owner"/> and <see cref="SpriteObject"/> stay opaque on purpose — they only
    /// ever travel back into functions that already accept a bare <c>object</c>
    /// (<c>TextTargets.Write</c>, and every sprite-taking overload of <c>ImageReplacer</c>).
    /// </summary>
    public sealed class PickedTarget
    {
        public string Path;

        /// <summary>The object's own name — the last path segment, for building a "**/name" pattern.</summary>
        public string Name;

        /// <summary>"uGUI", "World" or "UI Toolkit" — nothing branches on it except an existing gap: see InspectorPanel.OnPicked.</summary>
        public string Engine;

        /// <summary>The Component (uGUI/world) or element (UI Toolkit) behind the path, opaque.</summary>
        public object Owner;

        /// <summary>Always false: the picker never surfaces its own UI as a target.</summary>
        public bool IsOwnUi;

        /// <summary>Set only in BitmapReplace mode, when the target carries a named image.</summary>
        public bool HasSprite;
        public object SpriteObject;
        public string SpriteName;
        public int SpriteWidth;
        public int SpriteHeight;
        public string SpriteComponentType;
    }

    /// <summary>
    /// The in-game half of the inspector: DevTools-style hover/click picking, the highlight
    /// overlay, and the reflection-based IL2CPP-safe raycast. None of this is a UI decision and
    /// all of it touches Unity directly — the opposite of <see cref="Panels.InspectorPanel"/>,
    /// which owns the mode-by-mode decisions and must never see a Unity type. Extracted 2026-09-08
    /// (see analyse/inventaire-couches/brief-migration-panneau.md) so the panel could cross the
    /// vocabulary frontier while this mechanism — proven, IL2CPP-safe, and unrelated to what is
    /// drawn — stays exactly as it was.
    /// </summary>
    public sealed class InspectorPicker
    {
        private InspectorMode _currentMode = InspectorMode.Exclusion;
        private bool _isInspecting = false;

        // State
        private string _lastHoveredPath = "";
        private string _lastSelectedPath = "";
        private GameObject _lastSelectedObject = null;
        private object _lastSelectedSpriteObj = null;
        private int _frameSkip = 0;

        // The panel's own window, so a click or hover over it is never picked as game content.
        // Handed in once at Start(): a panel's RectTransform reference does not change for the
        // life of the process, only its geometry does — and GetScreenBounds reads that live.
        private RectTransform _panelRect;

        // Highlight overlay
        private GameObject _highlightCanvas;
        private Image _hoverHighlight;
        private Image _selectedHighlight;
        private RectTransform _hoverHighlightRect;
        private RectTransform _selectedHighlightRect;

        // Colors for highlights (DevTools-style) — from the palette
        private static readonly Color HoverHighlightColor = UIStyles.GameHighlightHover;
        private static readonly Color SelectedHighlightColor = UIStyles.GameHighlightSelected;

        // Camera selection for world-space raycast
        private Camera _selectedCamera = null; // null = UI Only mode
        private Camera[] _sceneCameras = new Camera[0];
        private string[] _cameraNames = new string[0];

        /// <summary>Raised when the hovered path changes — including to "" when nothing is hovered.</summary>
        public event Action<string> Hovered;

        /// <summary>Raised on a click that hit something pickable, own UI excluded.</summary>
        public event Action<PickedTarget> Picked;

        /// <summary>Options for the camera dropdown, "UI Only" first. Same order every refresh.</summary>
        public string[] CameraNames => _cameraNames;

        public InspectorPicker()
        {
            // Initialize raycast infrastructure on first picker creation — the resolved types and
            // methods are static and shared by every InspectorPanel instance there will ever be.
            InitializeRaycast();
        }

        /// <summary>Begin picking. Rebuilds the camera list; call before reading <see cref="CameraNames"/>.</summary>
        public void Start(InspectorMode mode, object panelRect)
        {
            _currentMode = mode;
            _panelRect = panelRect as RectTransform;
            _selectedCamera = null;
            RefreshCameraList();

            ClearSelection();
            ClearHover();

            if (_highlightCanvas == null) CreateHighlightOverlay();
            _highlightCanvas.SetActive(true);
            _isInspecting = true;
        }

        /// <summary>Stop picking and hide the overlay. Safe to call more than once.</summary>
        public void Stop()
        {
            _isInspecting = false;
            HideAllHighlights();
            if (_highlightCanvas != null)
                _highlightCanvas.SetActive(false);
        }

        /// <summary>Forget the current selection and hide its highlight. Picking keeps running.</summary>
        public void ClearSelection()
        {
            _lastSelectedPath = "";
            _lastSelectedObject = null;
            _lastSelectedSpriteObj = null;
            if (_selectedHighlight != null) _selectedHighlight.gameObject.SetActive(false);
        }

        /// <summary>Forget the current hover and hide its highlight.</summary>
        public void ClearHover()
        {
            _lastHoveredPath = "";
            if (_hoverHighlight != null) _hoverHighlight.gameObject.SetActive(false);
        }

        /// <summary>Select a camera by index into <see cref="CameraNames"/>. 0 (or out of range) is "UI Only".</summary>
        public void SelectCamera(int index)
        {
            _selectedCamera = (index > 0 && index <= _sceneCameras.Length) ? _sceneCameras[index - 1] : null;
            ClearSelection();
            ClearHover();
        }

        private void RefreshCameraList()
        {
            var options = new System.Collections.Generic.List<string> { "UI Only" };
            var cameraList = new System.Collections.Generic.List<Camera>();

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
        }

        /// <summary>
        /// Drive one frame of picking: hover (throttled) then click. Call from the panel's own
        /// <c>Update()</c> while the panel is enabled — this does nothing on its own timer.
        /// </summary>
        public void Tick()
        {
            if (!_isInspecting) return;

            // Throttle raycast: every 2 frames for hover (smooth enough, saves perf)
            _frameSkip++;
            bool doHoverRaycast = (_frameSkip % 2 == 0);

            Vector3 mousePos = InputManager.MousePosition;

            // Skip if mouse is over our panel
            if (_panelRect != null && IsMouseOverPanel(mousePos))
            {
                // Hide hover highlight when over our panel
                if (_hoverHighlight != null) _hoverHighlight.gameObject.SetActive(false);
                if (_lastHoveredPath != "")
                {
                    _lastHoveredPath = "";
                    Hovered?.Invoke("");
                }
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
                        if (_lastHoveredPath != "") { _lastHoveredPath = ""; Hovered?.Invoke(""); }
                    }
                    else
                    {
                        string path = TranslatorCore.GetGameObjectPath(hoveredObject);
                        if (path != _lastHoveredPath)
                        {
                            _lastHoveredPath = path;
                            Hovered?.Invoke(path);
                        }

                        // Position hover highlight
                        PositionHighlight(_hoverHighlightRect, _hoverHighlight, hoveredObject);
                    }
                }
                else
                {
                    // Nothing in the Canvas: this may be a UI Toolkit interface, which the
                    // GraphicRaycaster cannot see at all. See UIToolkitSupport.PickAt.
                    var element = UIToolkitSupport.PickAt(mousePos, out var elementRect);
                    if (element != null)
                    {
                        string elementPath = UIToolkitSupport.PathOf(element);
                        if (elementPath != _lastHoveredPath)
                        {
                            _lastHoveredPath = elementPath;
                            Hovered?.Invoke(elementPath);
                        }

                        PositionHighlightRect(_hoverHighlightRect, _hoverHighlight, elementRect);
                    }
                    else
                    {
                        if (_hoverHighlight != null) _hoverHighlight.gameObject.SetActive(false);
                        if (_lastHoveredPath != "") { _lastHoveredPath = ""; Hovered?.Invoke(""); }
                    }
                }
            }

            // --- Click detection (select) ---
            if (InputManager.GetMouseButtonDown(0))
            {
                var hitObject = RaycastUIElement(mousePos);
                if (hitObject == null && SelectUIToolkitAt(mousePos)) return;

                if (hitObject != null && !IsOwnUI(hitObject))
                {
                    string path = TranslatorCore.GetGameObjectPath(hitObject);
                    _lastSelectedPath = path;
                    _lastSelectedObject = hitObject;
                    _lastSelectedSpriteObj = null;

                    var target = new PickedTarget
                    {
                        Path = path,
                        Name = hitObject.name,
                        Engine = _selectedCamera != null ? "World" : "uGUI",
                        Owner = hitObject,
                        IsOwnUi = false,
                    };

                    if (_currentMode == InspectorMode.BitmapReplace)
                    {
                        try
                        {
                            _lastSelectedSpriteObj = ImageReplacer.GetSpriteFromComponent(hitObject);
                            var spriteName = ImageReplacer.GetSpriteName(_lastSelectedSpriteObj) ?? "(unnamed)";
                            var size = ImageReplacer.GetSpriteSize(_lastSelectedSpriteObj);
                            var compType = ImageReplacer.GetComponentTypeName(hitObject);

                            // ⚠ Always filled, even when _lastSelectedSpriteObj is null (an Image
                            // component with no sprite assigned): the panel still names what kind of
                            // component was hit. HasSprite governs the export/mark buttons instead.
                            target.HasSprite = _lastSelectedSpriteObj != null;
                            target.SpriteObject = _lastSelectedSpriteObj;
                            target.SpriteName = spriteName;
                            target.SpriteWidth = size.x;
                            target.SpriteHeight = size.y;
                            target.SpriteComponentType = compType;
                        }
                        catch (Exception ex)
                        {
                            TranslatorCore.LogDebug($"[Inspector] BitmapReplace click handler error: {ex}");
                        }
                    }

                    // Position selected highlight
                    PositionHighlight(_selectedHighlightRect, _selectedHighlight, hitObject);

                    Picked?.Invoke(target);
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

            PositionHighlightRect(highlightRect, highlightImage,
                                  UnityEngine.Rect.MinMaxRect(screenMin.x, screenMin.y,
                                                              screenMax.x, screenMax.y));
        }

        /// <summary>
        /// Place the highlight over a screen rectangle.
        ///
        /// ⚠ Split from PositionHighlight so a third source of coordinates can use it. That method
        /// already handled two — a RectTransform in a Canvas and a Renderer in the world — and
        /// UI Toolkit is a third, which reports its own rectangle rather than any Unity transform.
        /// </summary>
        private void PositionHighlightRect(RectTransform highlightRect, Image highlightImage, Rect screen)
        {
            if (highlightRect == null || highlightImage == null) return;

            // Skip degenerate rects
            if (screen.width < 1f || screen.height < 1f)
            {
                highlightImage.gameObject.SetActive(false);
                return;
            }

            highlightRect.anchoredPosition = new Vector2(screen.xMin, screen.yMin);
            highlightRect.sizeDelta = new Vector2(screen.width, screen.height);
            highlightImage.gameObject.SetActive(true);
        }

        private void HideAllHighlights()
        {
            if (_hoverHighlight != null) _hoverHighlight.gameObject.SetActive(false);
            if (_selectedHighlight != null) _selectedHighlight.gameObject.SetActive(false);
        }

        #endregion

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
            if (_panelRect == null) return false;

            Vector2 screenMin, screenMax;
            if (!GetScreenBounds(_panelRect, out screenMin, out screenMax))
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

        /// <summary>
        /// Select a UI Toolkit element under the pointer. True when one was taken.
        ///
        /// 🔴 The Canvas raycast returns nothing on a UI Toolkit interface, so before this the
        /// inspector simply did not work on those games — clicking anywhere found nothing.
        ///
        /// ⚠ Everything after the selection already worked from the PATH alone: exclusions, font
        /// rules and the text editor all match on it. Only the picking was uGUI's, so only the
        /// picking had to be written again.
        /// </summary>
        private bool SelectUIToolkitAt(Vector2 mousePos)
        {
            var element = UIToolkitSupport.PickAt(mousePos, out var screenRect);
            if (element == null) return false;

            string path = UIToolkitSupport.PathOf(element);
            if (string.IsNullOrEmpty(path)) return false;

            _lastSelectedPath = path;
            _lastSelectedObject = null;   // there is no GameObject behind a VisualElement
            _lastSelectedSpriteObj = null;

            PositionHighlightRect(_selectedHighlightRect, _selectedHighlight, screenRect);

            var target = new PickedTarget
            {
                Path = path,
                Engine = "UI Toolkit",
                Owner = element,
                IsOwnUi = false,
            };

            if (_currentMode == InspectorMode.BitmapReplace)
            {
                // The sprite comes from the element's style rather than from a component, but it is
                // the same object afterwards — so naming, sizing and exporting go through
                // ImageReplacer exactly as they do for uGUI.
                _lastSelectedSpriteObj = UIToolkitSupport.SpriteOf(element);
                target.SpriteComponentType = "UI Toolkit";

                // ⚠ Unlike the uGUI path, a null sprite here says so plainly rather than composing
                // a "ComponentType: (unnamed)" line — an element can draw a bare texture or a shape
                // with no picture at all, and neither has a component type worth naming. Same
                // asymmetry as the original inline branch; the panel renders the two differently.
                if (_lastSelectedSpriteObj != null)
                {
                    var size = ImageReplacer.GetSpriteSize(_lastSelectedSpriteObj);
                    target.HasSprite = true;
                    target.SpriteObject = _lastSelectedSpriteObj;
                    target.SpriteName = ImageReplacer.GetSpriteName(_lastSelectedSpriteObj) ?? "(unnamed)";
                    target.SpriteWidth = size.x;
                    target.SpriteHeight = size.y;
                }
            }

            Picked?.Invoke(target);
            return true;
        }
    }
}
