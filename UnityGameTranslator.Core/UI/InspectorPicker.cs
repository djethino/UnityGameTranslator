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

        /// <summary>
        /// The twelve edges of the bounding box, per highlight — the 3D form of the marker.
        ///
        /// 🔴 **A flat rectangle could not say what was picked in a 3D scene**, and worse, it was
        /// wrong: the bounds were projected as four points of an XY rectangle, ignoring
        /// extents.z, so an object seen at an angle collapsed to a line — measured 2026-09-19,
        /// "Degenerate highlight rect 0,17x56,53 — hidden", which is why the highlight seemed
        /// absent. Eight corners are projected now, and the twelve edges between them show the
        /// volume and its orientation the way an editor does.
        ///
        /// ⚠ uGUI draws no arbitrary segment: each edge is a thin Image placed at the midpoint,
        /// sized to the length and rotated to the angle. Hence a centre pivot on these, where the
        /// flat rectangle keeps its corner pivot.
        /// </summary>
        private RectTransform[] _hoverEdges;
        private RectTransform[] _selectedEdges;
        private Image[] _hoverEdgeImages;
        private Image[] _selectedEdgeImages;

        /// <summary>The eight corners, reused between frames so a hover allocates nothing.</summary>
        private readonly Vector3[] _corners = new Vector3[8];

        private const float EdgeThickness = 2f;

        /// <summary>How much of the marker's own colour the 3D wash keeps — a hint, not a fill.</summary>
        private const float VeilAlpha = 0.35f;

        /// <summary>The twelve edges of a box, as pairs of corner indices (bit 0 = x, 1 = y, 2 = z).</summary>
        private static readonly int[] BoxEdges =
        {
            0,1, 2,3, 4,5, 6,7,   // along X
            0,2, 1,3, 4,6, 5,7,   // along Y
            0,4, 1,5, 2,6, 3,7,   // along Z
        };

        // Colors for highlights (DevTools-style) — from the palette
        private static readonly Color HoverHighlightColor = UIStyles.GameHighlightHover;
        private static readonly Color SelectedHighlightColor = UIStyles.GameHighlightSelected;

        // Camera selection for world-space raycast
        private Camera _selectedCamera = null; // null = UI Only mode
        private Camera[] _sceneCameras = new Camera[0];
        private string[] _cameraNames = new string[0];

        #region What the scene holds — found once, not thirty times a second

        /// <summary>
        /// 🔴 **These two lists were rebuilt on EVERY raycast** (fixed 2026-09-19): a
        /// FindAllObjectsOfType — which walks the whole scene and, on IL2CPP, crosses the interop
        /// boundary — ran twice a second per hover and again at every click. In camera mode it was
        /// every Renderer in the game.
        ///
        /// ⚠ **A cache must not cost fidelity**, which is the trade this one refuses. It is
        /// dropped on three events, never on a clock: the active scene changes
        /// (<see cref="DropIfSceneChanged"/>), picking starts, and — the case the other two do not
        /// cover — the first time a hover finds NOTHING. A menu that has just opened is exactly
        /// what a stale list cannot see, and looking again at that moment is what finds it. One
        /// rebuild per run of empty hovers, never one per frame: the flag clears as soon as
        /// something is found again.
        /// </summary>
        private static UnityEngine.Object[] _raycastersCache;
        private static UnityEngine.Object[] _renderersCache;

        /// <summary>Set once a rebuild has been spent on the current run of empty hovers.</summary>
        private bool _rebuiltOnMiss;

        /// <summary>Which scene the lists describe. A different one makes them meaningless.</summary>
        private static int _cacheScene = -1;

        private static void DropSceneCaches()
        {
            _raycastersCache = null;
            _renderersCache = null;
        }

        /// <summary>
        /// Drop the lists when the active scene is no longer the one they were taken from.
        ///
        /// ⚠ **Checked here rather than hooked into TranslatorCore.OnSceneChanged**, where the
        /// other modules hang: that method belongs to the engine, and the engine never names the
        /// interface (EngineFrontierChecks). Reading the active scene's handle is O(1) and keeps
        /// the cache's validity the cache's own business.
        /// </summary>
        private static void DropIfSceneChanged()
        {
            int scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
            if (scene == _cacheScene) return;
            _cacheScene = scene;
            DropSceneCaches();
        }

        private static UnityEngine.Object[] Raycasters()
        {
            DropIfSceneChanged();
            if (_raycastersCache == null)
            {
                _raycastersCache = TypeHelper.FindAllObjectsOfType(_graphicRaycasterType)
                                   ?? new UnityEngine.Object[0];
                TranslatorCore.LogDebug($"[Inspector] Scene walked: {_raycastersCache.Length} graphic raycaster(s)");
            }
            return _raycastersCache;
        }

        /// <summary>
        /// The six frustum planes of a camera, as (a,b,c,d) rows — derived from its own matrices
        /// rather than asked of Unity.
        ///
        /// 🔴 **Because GeometryUtility does not exist on this runtime, in either direction.**
        /// 2026-09-19, measured twice: TestPlanesAABB was stripped, then CalculateFrustumPlanes
        /// too. ⚠ **And a try/catch does not save you**: IL2CPP resolves a missing method when it
        /// compiles the method that NAMES it, so the throw happens before the guarded body is
        /// entered — the exception surfaced in the CALLER while the guard sat unused. That is the
        /// same lesson CLAUDE.md states in red for AddListener: the answer is never to wrap the
        /// call, it is to not name it.
        ///
        /// Gribb-Hartmann: with M = projection * worldToCamera, the planes are row3 ± rowN. ⚠ Not
        /// normalised, on purpose — the AABB test below compares a distance against a radius, and
        /// both scale by the same factor, so normalising would only cost six square roots.
        /// </summary>
        private static readonly float[] _frustum = new float[24];

        private static void BuildFrustum(Camera camera)
        {
            Matrix4x4 m = camera.projectionMatrix * camera.worldToCameraMatrix;

            // left, right, bottom, top, near, far
            Set(0, m.m30 + m.m00, m.m31 + m.m01, m.m32 + m.m02, m.m33 + m.m03);
            Set(1, m.m30 - m.m00, m.m31 - m.m01, m.m32 - m.m02, m.m33 - m.m03);
            Set(2, m.m30 + m.m10, m.m31 + m.m11, m.m32 + m.m12, m.m33 + m.m13);
            Set(3, m.m30 - m.m10, m.m31 - m.m11, m.m32 - m.m12, m.m33 - m.m13);
            Set(4, m.m30 + m.m20, m.m31 + m.m21, m.m32 + m.m22, m.m33 + m.m23);
            Set(5, m.m30 - m.m20, m.m31 - m.m21, m.m32 - m.m22, m.m33 - m.m23);
        }

        private static void Set(int plane, float a, float b, float c, float d)
        {
            int i = plane * 4;
            _frustum[i] = a; _frustum[i + 1] = b; _frustum[i + 2] = c; _frustum[i + 3] = d;
        }

        /// <summary>True once this runtime has shown it carries no physics; not asked again.</summary>
        private static bool _physicsRefused;

        /// <summary>
        /// The first surface the ray meets, or null when it meets none.
        ///
        /// 🔴 **Its own method on purpose.** IL2CPP resolves a missing method when it compiles the
        /// method that NAMES it, so a game shipping no PhysicsModule throws as the caller steps
        /// into this one — inside the caller's try, which is the only place a guard can work. A
        /// try written HERE would never run. Measured twice on 2026-09-19 with GeometryUtility.
        /// </summary>
        private static GameObject PhysicsPick(Camera camera, Vector3 screenPosition)
        {
            RaycastHit hit;
            if (!Physics.Raycast(camera.ScreenPointToRay(screenPosition), out hit, camera.farClipPlane))
                return null;
            if (hit.collider == null) return null;

            // 🔴 **A collider is not the thing you see.** Measured 2026-09-19: the ray kept
            // landing on objects named "Cube (7)" — invisible collision volumes wrapped around
            // furniture — 1 675 times in one session. They carry no Renderer, so no highlight
            // could be drawn, and they are not what holds the text either, so the inspector
            // listed the neighbours' strings. Walk to what is actually drawn: the collider's own
            // renderer, else one below it, else the one above.
            var go = hit.collider.gameObject;
            var rend = go.GetComponent<Renderer>();
            if (rend == null) rend = go.GetComponentInChildren<Renderer>();
            if (rend == null) rend = go.GetComponentInParent<Renderer>();
            return rend != null ? rend.gameObject : go;
        }

        /// <summary>Is this box inside what the camera frames? Plain arithmetic, no Unity helper.</summary>
        private static bool InFrustum(Bounds bounds)
        {
            Vector3 c = bounds.center, e = bounds.extents;
            for (int i = 0; i < 24; i += 4)
            {
                float a = _frustum[i], b = _frustum[i + 1], cc = _frustum[i + 2], d = _frustum[i + 3];
                float radius = e.x * Mathf.Abs(a) + e.y * Mathf.Abs(b) + e.z * Mathf.Abs(cc);
                if (a * c.x + b * c.y + cc * c.z + d + radius < 0f) return false;
            }
            return true;
        }

        private static UnityEngine.Object[] Renderers()
        {
            DropIfSceneChanged();
            if (_renderersCache == null)
            {
                _renderersCache = TypeHelper.FindAllObjectsOfType(typeof(Renderer))
                                  ?? new UnityEngine.Object[0];
                TranslatorCore.LogDebug($"[Inspector] Scene walked: {_renderersCache.Length} renderer(s)");
            }
            return _renderersCache;
        }

        #endregion

        #region Probe — what picking costs, in DebugMode only

        /// <summary>
        /// 🔴 **Aggregated, never per frame.** A line per raycast would be thirty a second and
        /// would itself become the cost being measured — the mistake the freeze of 2026-09-18
        /// taught (its own probe logs once, above a felt threshold). One summary per
        /// <see cref="ProbeEvery"/> hovers gives the average, the worst and how many objects were
        /// walked: enough to tell whether a game stutters because of this or in spite of it, and
        /// enough to see whether a further fix is worth its complexity.
        ///
        /// ⚠ Behind DebugMode, as asked. The numbers are therefore read on a build where every
        /// other diagnostic is on too, which inflates them all equally.
        /// </summary>
        private const int ProbeEvery = 60;
        private static int _probeCount;
        private static long _probeTicks;
        private static long _probeWorst;
        private static int _probeWalked;        // objects walked by the raycast under way
        private static int _probeWalkedTotal;

        private void ResetProbe()
        {
            _probeCount = 0;
            _probeTicks = 0;
            _probeWorst = 0;
            _probeWalkedTotal = 0;
        }

        private void NoteProbe(long ticks)
        {
            if (!TranslatorCore.DebugMode) return;

            _probeCount++;
            _probeTicks += ticks;
            if (ticks > _probeWorst) _probeWorst = ticks;
            _probeWalkedTotal += _probeWalked;
            if (_probeCount < ProbeEvery) return;

            double toMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string where = _selectedCamera != null ? $"camera '{_selectedCamera.name}'" : "UI Only";
            TranslatorCore.LogInfo(
                $"[INSPECTOR-PROBE] {where}: {_probeTicks * toMs / _probeCount:F2} ms avg, "
                + $"{_probeWorst * toMs:F2} ms worst, over {_probeCount} hovers — "
                + $"{_probeWalkedTotal / _probeCount} object(s) walked each");
            ResetProbe();
        }

        #endregion

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
            // Picking starts on what the scene holds now, not on what it held last time.
            DropSceneCaches();
            _rebuiltOnMiss = false;
            ResetProbe();
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

            // 🔴 **Off the window, nothing is under the pointer** (2026-09-19). Reported: objects
            // were being selected while the mouse was outside the game entirely. Every picking
            // path answers a screen position, and a position nobody is pointing at still lands
            // inside some large bounding box — so the guard belongs here, before any of them.
            Vector3 probe = InputManager.MousePosition;
            if (probe.x < 0f || probe.y < 0f || probe.x > Screen.width || probe.y > Screen.height)
            {
                ClearHover();
                return;
            }

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
                long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _probeWalked = 0;
                var hoveredObject = RaycastUIElement(mousePos);

                // Found nothing: the lists may predate a menu that has just opened. Look once
                // more with fresh ones — and only once, until something is found again.
                if (hoveredObject == null && !_rebuiltOnMiss)
                {
                    _rebuiltOnMiss = true;
                    DropSceneCaches();
                    hoveredObject = RaycastUIElement(mousePos);
                }
                if (hoveredObject != null) _rebuiltOnMiss = false;

                NoteProbe(System.Diagnostics.Stopwatch.GetTimestamp() - t0);

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
                        PositionHighlight(_hoverHighlightRect, _hoverHighlight, _hoverEdges, _hoverEdgeImages, hoveredObject);
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

                        HideEdges(_hoverEdgeImages);
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
                    PositionHighlight(_selectedHighlightRect, _selectedHighlight, _selectedEdges, _selectedEdgeImages, hitObject);

                    Picked?.Invoke(target);
                }
            }

            // Keep selected highlight tracking (object may move)
            if (_lastSelectedObject != null && _selectedHighlight != null && _selectedHighlight.gameObject.activeSelf)
            {
                // Re-position every ~10 frames to track moving elements
                if (_frameSkip % 10 == 0)
                    PositionHighlight(_selectedHighlightRect, _selectedHighlight, _selectedEdges, _selectedEdgeImages, _lastSelectedObject);
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
                var raycasters = Raycasters();
                if (raycasters.Length == 0) return null;

                foreach (var raycasterObj in raycasters)
                {
                    if (raycasterObj == null) continue;
                    _probeWalked++;

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

                var raycasters = Raycasters();
                if (raycasters == null) return null;

                foreach (var raycasterObj in raycasters)
                {
                    if (raycasterObj == null) continue;
                    _probeWalked++;

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

            // 🔴 **The ray first: it is the only thing that knows what is IN FRONT.** The bounds
            // pass below asks "is the cursor inside this box on screen", which a wall does not
            // stop — hence objects picked through walls, and, once sorted by depth instead, an
            // enclosing box winning everywhere including off screen. Two symptoms, one limit: a
            // bounding box cannot answer occlusion. A ray can, and is indexed, so it also ends
            // the walk over every renderer in the scene.
            //
            // ⚠ It sees only what carries a collider, so the bounds pass stays as the fallback
            // rather than being replaced: plenty of decorative meshes, and most 3D text, have none.
            if (!_physicsRefused)
            {
                GameObject viaRay = null;
                try { viaRay = PhysicsPick(camera, screenPosition); }
                catch (Exception ex)
                {
                    _physicsRefused = true;
                    TranslatorCore.LogWarning(
                        $"[Inspector] This game ships no usable physics ({ex.Message}) — picking "
                        + "falls back to bounding boxes, which cannot see what is in front");
                }

                if (viaRay != null && !IsOwnUI(viaRay)
                    && (_currentMode != InspectorMode.BitmapReplace || ImageReplacer.HasImageComponent(viaRay)))
                    return viaRay;
            }

            // Then: bounds check for renderers visible to this camera
            try
            {
                int cullingMask = camera.cullingMask;
                var all = Renderers();

                // 🔴 **Reject what the camera cannot see, before projecting anything** — measured
                // 2026-09-19: 329 ms average per hover, 57 143 objects walked each time, on a
                // game holding 71 336 renderers. `isVisible` filtered barely a fifth of them,
                // because it is true as soon as ANY camera sees the object, shadow and offscreen
                // cameras included. The frustum of the camera being picked through is the honest
                // question, asked once per hover rather than per object.
                //
                BuildFrustum(camera);

                GameObject bestHit = null;
                float bestDepth = float.MaxValue;
                float bestArea = float.MaxValue;

                foreach (var obj in all)
                {
                    if (obj == null) continue;
                    _probeWalked++;

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

                    // Outside what this camera frames: nothing to pick, nothing to project.
                    if (!InFrustum(rend.bounds)) continue;

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
                            // 🔴 **The nearest wins, not the smallest** (2026-09-19). This kept
                            // whichever box was smallest on screen, so standing in a room and
                            // aiming at the television picked objects OUTSIDE the flat, behind
                            // the wall: they were smaller, and distance was never asked about.
                            // Depth first; area only settles an exact tie, which is what it was
                            // good for — telling a label apart from the panel it sits on.
                            //
                            // ⚠ The depth is the box's CENTRE, not its surface. A large object
                            // whose middle is far can still lose to a small one nearer the
                            // camera. Bounding boxes cannot do better than that; a real
                            // Physics.Raycast could, and would need colliders on everything.
                            float depth = screenCenter.z;
                            float hitArea = (maxX - minX) * (maxY - minY);

                            // 🔴 **A box bigger than the whole screen is a container, never the
                            // thing being aimed at** — the floor, the room, the building shell.
                            // Sorting by depth alone handed those the win everywhere, cursor off
                            // screen included, because their box contains every position and
                            // their centre is near. ⚠ The bound is the screen's own area, not a
                            // number chosen here: what cannot be framed cannot be pointed at.
                            if (hitArea >= (float)Screen.width * Screen.height) continue;

                            if (depth < bestDepth || (depth == bestDepth && hitArea < bestArea))
                            {
                                bestDepth = depth;
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

            _hoverEdges = new RectTransform[12];
            _hoverEdgeImages = new Image[12];
            _selectedEdges = new RectTransform[12];
            _selectedEdgeImages = new Image[12];
            BuildEdges("HoverEdge", HoverHighlightColor, _hoverEdges, _hoverEdgeImages);
            BuildEdges("SelectedEdge", SelectedHighlightColor, _selectedEdges, _selectedEdgeImages);

            // Start hidden
            _highlightCanvas.SetActive(false);
        }

        private void BuildEdges(string name, Color color, RectTransform[] rects, Image[] images)
        {
            for (int i = 0; i < 12; i++)
            {
                var obj = new GameObject(name + i);
                obj.transform.SetParent(_highlightCanvas.transform, false);
                var image = obj.AddComponent<Image>();
                image.color = color;
                // ⚠ Never a raycast target: twelve thin strips across the screen would swallow the
                // clicks this tool exists to let through. The flat rectangle blocks them on
                // purpose; a wireframe is a drawing, not a surface.
                image.raycastTarget = false;
                var rect = obj.GetComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.zero;
                rect.pivot = new Vector2(0.5f, 0.5f);
                obj.SetActive(false);
                rects[i] = rect;
                images[i] = image;
            }
        }

        /// <summary>The eight corners of a world bounding box, in screen space. False when the box is behind a perspective camera.</summary>
        private bool ProjectBox(Bounds bounds, Camera camera)
        {
            if (camera == null) return false;
            Vector3 c = bounds.center, e = bounds.extents;
            if (e == Vector3.zero) return false;

            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    c.x + ((i & 1) == 0 ? -e.x : e.x),
                    c.y + ((i & 2) == 0 ? -e.y : e.y),
                    c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 p = camera.WorldToScreenPoint(corner);
                if (!camera.orthographic && p.z < 0) return false;
                _corners[i] = p;
            }
            return true;
        }

        /// <summary>Draw the box: twelve strips between the projected corners.</summary>
        private void ShowBox(RectTransform[] rects, Image[] images)
        {
            for (int i = 0; i < 12; i++)
            {
                Vector3 a = _corners[BoxEdges[i * 2]];
                Vector3 b = _corners[BoxEdges[i * 2 + 1]];
                float dx = b.x - a.x, dy = b.y - a.y;
                float length = Mathf.Sqrt(dx * dx + dy * dy);

                rects[i].anchoredPosition = new Vector2((a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f);
                rects[i].sizeDelta = new Vector2(Mathf.Max(length, EdgeThickness), EdgeThickness);
                rects[i].localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dy, dx) * Mathf.Rad2Deg);
                images[i].gameObject.SetActive(true);
            }
        }

        private static void HideEdges(Image[] images)
        {
            if (images == null) return;
            for (int i = 0; i < images.Length; i++)
                images[i]?.gameObject.SetActive(false);
        }

        /// <summary>
        /// Position a highlight rect over a target GameObject's RectTransform bounds.
        /// Uses TransformPoint instead of GetWorldCorners (IL2CPP-safe: no array params).
        /// </summary>
        private void PositionHighlight(RectTransform highlightRect, Image highlightImage,
                                       RectTransform[] edges, Image[] edgeImages, GameObject target)
        {
            if (target == null || highlightRect == null || highlightImage == null)
            {
                HideEdges(edgeImages);
                highlightImage?.gameObject.SetActive(false);
                return;
            }

            // 🔴 **Flat marker for flat things, box for things with a volume** (2026-09-19). A
            // Canvas element IS a rectangle on the screen, so a rectangle says everything about
            // it; a mesh in the world has an orientation, and a rectangle around it says only
            // "somewhere in there". The two never show at once.
            var targetRect = target.GetComponent<RectTransform>();
            if (targetRect != null)
            {
                HideEdges(edgeImages);
                // Back to a blocking marker: a Canvas element's rectangle is the element itself.
                highlightImage.raycastTarget = true;
                if (!GetScreenBounds(targetRect, _selectedCamera, out var screenMin, out var screenMax))
                {
                    highlightImage.gameObject.SetActive(false);
                    return;
                }
                PositionHighlightRect(highlightRect, highlightImage,
                                      UnityEngine.Rect.MinMaxRect(screenMin.x, screenMin.y,
                                                                  screenMax.x, screenMax.y));
                return;
            }

            // A renderer in the world: the eight corners of its bounds, projected through the
            // camera being picked through — never Camera.main, which is what used to draw the
            // marker somewhere else entirely.
            var renderer = target.GetComponent<Renderer>();
            var camera = _selectedCamera ?? Camera.main;

            // Nothing drawn on it, but something solid: frame what the ray could hit. Better than
            // no marker at all, and it says plainly that the pick landed on a collision volume.
            Bounds box;
            bool haveBox = renderer != null;
            if (haveBox) box = renderer.bounds;
            else
            {
                var solid = target.GetComponent<Collider>();
                haveBox = solid != null;
                box = haveBox ? solid.bounds : default(Bounds);
            }

            if (haveBox && camera != null && ProjectBox(box, camera))
            {
                // 🔴 **A veil behind the wires, or the marker vanishes on a same-coloured object**
                // (2026-09-19, user's remark). Twelve thin lines are invisible against a surface
                // of their own colour; a wash over the whole footprint is always readable, and the
                // wires on top still say where the volume begins and ends.
                //
                // ⚠ It reuses the flat rectangle rather than adding a thirteenth image — same
                // piece, two roles — but NOT as a click blocker: the flat marker is a raycast
                // target so nothing behind it reacts, whereas a wash over a whole 3D footprint
                // would swallow the neighbours you are trying to aim at next.
                float minX = _corners[0].x, maxX = minX, minY = _corners[0].y, maxY = minY;
                for (int i = 1; i < 8; i++)
                {
                    if (_corners[i].x < minX) minX = _corners[i].x;
                    if (_corners[i].x > maxX) maxX = _corners[i].x;
                    if (_corners[i].y < minY) minY = _corners[i].y;
                    if (_corners[i].y > maxY) maxY = _corners[i].y;
                }

                Color wire = highlightImage.color;
                highlightImage.raycastTarget = false;
                highlightImage.color = new Color(wire.r, wire.g, wire.b, wire.a * VeilAlpha);
                PositionHighlightRect(highlightRect, highlightImage,
                                      UnityEngine.Rect.MinMaxRect(minX, minY, maxX, maxY));
                highlightImage.color = wire;

                ShowBox(edges, edgeImages);
                return;
            }

            TranslatorCore.LogDebug(
                $"[Inspector] '{target.name}' has neither a RectTransform nor a projectable renderer"
                + (camera == null ? " (and no camera to project with)" : "") + " — highlight hidden");
            HideEdges(edgeImages);
            highlightImage.gameObject.SetActive(false);
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

            // A rectangle under a pixel is not a shape: it is a conversion that went wrong
            // upstream (world units read as pixels, a projection behind the camera). Said out
            // loud, because hiding it silently is what made the missing highlight undebuggable.
            if (screen.width < 1f || screen.height < 1f)
            {
                TranslatorCore.LogDebug(
                    $"[Inspector] Degenerate highlight rect {screen.width:F2}x{screen.height:F2} at " +
                    $"({screen.xMin:F1},{screen.yMin:F1}) — hidden");
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
            HideEdges(_hoverEdgeImages);
            HideEdges(_selectedEdgeImages);
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

            // No picking camera here on purpose: this is OUR window, on UniverseLib's own
            // Screen Space Overlay canvas, where world coordinates already are screen ones.
            Vector2 screenMin, screenMax;
            if (!GetScreenBounds(_panelRect, null, out screenMin, out screenMax))
                return false;

            return screenPos.x >= screenMin.x && screenPos.x <= screenMax.x &&
                   screenPos.y >= screenMin.y && screenPos.y <= screenMax.y;
        }

        /// <summary>
        /// Get screen-space bounds of a RectTransform using TransformPoint (IL2CPP-safe).
        /// GetWorldCorners(Vector3[]) crashes on IL2CPP because the array param becomes
        /// Il2CppStructArray — using TransformPoint(Vector3) avoids this entirely.
        /// </summary>
        private static bool GetScreenBounds(RectTransform rect, Camera picking, out Vector2 screenMin, out Vector2 screenMax)
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
                    // 🔴 **A canvas that is not Overlay MUST be converted, and the fallbacks are
                    // new** (2026-09-19). This took `worldCamera` and, when it was null, simply
                    // kept the WORLD coordinates as if they were pixels: a rectangle a few units
                    // wide, which PositionHighlightRect then threw away on `width < 1f`. The
                    // highlight vanished with nothing said, on the very common case of a
                    // ScreenSpaceCamera canvas whose camera is assigned at runtime.
                    //
                    // Order: the canvas's own camera is the truth when it has one; otherwise the
                    // camera the person is picking through; otherwise the main one. Out of all
                    // three, the honest answer is to refuse — out loud.
                    var cam = rootCanvas.worldCamera ?? picking ?? Camera.main;
                    if (cam == null)
                    {
                        TranslatorCore.LogDebug(
                            $"[Inspector] Canvas '{rootCanvas.name}' is {rootCanvas.renderMode} with no camera to convert through — highlight hidden");
                        return false;
                    }

                    Vector3 s0 = cam.WorldToScreenPoint(c0);
                    Vector3 s1 = cam.WorldToScreenPoint(c1);
                    Vector3 s2 = cam.WorldToScreenPoint(c2);
                    Vector3 s3 = cam.WorldToScreenPoint(c3);

                    minX = Mathf.Min(s0.x, s1.x, s2.x, s3.x);
                    maxX = Mathf.Max(s0.x, s1.x, s2.x, s3.x);
                    minY = Mathf.Min(s0.y, s1.y, s2.y, s3.y);
                    maxY = Mathf.Max(s0.y, s1.y, s2.y, s3.y);
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

        private bool SelectUIToolkitAt(Vector2 mousePos)
        {
            var element = UIToolkitSupport.PickAt(mousePos, out var screenRect);
            if (element == null) return false;

            string path = UIToolkitSupport.PathOf(element);
            if (string.IsNullOrEmpty(path)) return false;

            _lastSelectedPath = path;
            _lastSelectedObject = null;   // there is no GameObject behind a VisualElement
            _lastSelectedSpriteObj = null;

            HideEdges(_selectedEdgeImages);
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
