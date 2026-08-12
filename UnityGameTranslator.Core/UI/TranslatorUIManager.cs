using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UniverseLib;
using UniverseLib.Config;
using UniverseLib.Input;
using UniverseLib.Runtime;
using UniverseLib.UI;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>
    /// Update direction for translation sync notifications.
    /// </summary>
    public enum UpdateDirection
    {
        None,
        Download,
        Upload,
        Merge
    }

    /// <summary>
    /// Main UI manager for UnityGameTranslator using UniverseLib uGUI system.
    /// Replaces the IMGUI-based TranslatorUI.
    /// </summary>
    public static class TranslatorUIManager
    {
        public static UIBase UiBase { get; private set; }

        // UniverseLib's original UI font, captured at init — restored when the interface font is cleared.
        private static UnityEngine.Font _originalUIFont;
        private static string _originalUIFontFamily;   // its original family (to restore fontNames)
        private static int _uiFontBumpDelta;            // current +1 atlas-invalidation bump (0 or 1)
        private static int _fontRerenderCountdown;      // frames until a deferred re-dirty (atlas warms async)
        private static bool _uiFontRebacked;            // true = IL2CPP reback path in effect (vs Mono object swap)
        private static string _pendingRebackFont;       // IL2CPP: font to reback after the deferred restore→reback gap
        private static int _rebackDelay;                // frames left before the pending reback fires
        private static string _missingInterfaceFontReported; // font we already warned about (warn once per value)
        // The interface font + mod-UI translation are applied lazily on first show: at init the custom
        // fonts aren't loaded yet and the translation worker isn't ready, so an early pass is a no-op.
        private static bool _uiFontBootstrapped;

        private static bool _initialized;
        public static bool IsInitialized => _initialized;

        // Callback for when initialization completes (used by TranslatorPatches to retry failed font replacements)
        public static event Action OnInitialized;
        private static bool _showUI;
        private static bool _lastPanelVisibleState; // Track panel state for EventSystem and cursor management

        // True while the mod's interface owns the game's input. Follows the panels, but lags them
        // on the way down until the mouse is idle — see the handover in UpdateUI.
        private static bool _uiHoldsInput;

        // Update notification state
        public static bool HasPendingUpdate { get; set; } = false;
        public static TranslationCheckResult PendingUpdateInfo { get; set; } = null;
        public static UpdateDirection PendingUpdateDirection { get; set; } = UpdateDirection.None;
        public static bool NotificationDismissed { get; set; } = false;

        // Mod update notification state
        public static bool HasModUpdate { get; set; } = false;
        public static ModUpdateInfo ModUpdateInfo { get; set; } = null;
        public static bool ModUpdateDismissed { get; set; } = false;

        // SSE sync stream
        private static SseClient _syncSseClient;
        public static SseConnectionState SyncConnectionState { get; private set; } = SseConnectionState.Disconnected;

        // Panels
        public static Panels.WizardPanel WizardPanel { get; private set; }
        public static Panels.MainPanel MainPanel { get; private set; }
        public static Panels.OptionsPanel OptionsPanel { get; private set; }
        public static Panels.LoginPanel LoginPanel { get; private set; }
        public static Panels.UploadPanel UploadPanel { get; private set; }
        public static Panels.UploadSetupPanel UploadSetupPanel { get; private set; }
        public static Panels.MergePanel MergePanel { get; private set; }
        public static Panels.LanguagePanel LanguagePanel { get; private set; }
        public static Panels.StatusOverlay StatusOverlay { get; private set; }
        public static Panels.ConfirmationPanel ConfirmationPanel { get; private set; }
        public static Panels.SettingsChoicePanel SettingsChoicePanel { get; private set; }
        public static Panels.InspectorPanel InspectorPanel { get; private set; }
        public static Panels.TranslationParametersPanel TranslationParamsPanel { get; private set; }

        /// <summary>
        /// List of all interactive panels (excludes StatusOverlay which is a notification overlay).
        /// Used for centralized panel state management.
        /// </summary>
        private static readonly List<Panels.TranslatorPanelBase> _interactivePanels = new List<Panels.TranslatorPanelBase>();

        /// <summary>
        /// Gets all registered interactive panels.
        /// </summary>
        public static IReadOnlyList<Panels.TranslatorPanelBase> InteractivePanels => _interactivePanels;

        /// <summary>
        /// Whether any main panel is visible (not including status overlay).
        /// Note: UiBase remains enabled for hotkey detection and status overlay.
        /// </summary>
        public static bool ShowUI
        {
            get => _showUI;
            set
            {
                _showUI = value;
                // Don't disable UiBase - keep it enabled for hotkey detection and status overlay
                // Individual panels control their own visibility
            }
        }

        /// <summary>
        /// Execute an action on the main Unity thread.
        /// Essential for IL2CPP builds where async continuations run on background threads.
        /// Safe to call from any thread - if already on main thread, executes immediately via coroutine.
        /// </summary>
        /// <summary>
        /// Run an async block as a fire-and-forget on the SynchronizationContext
        /// of the caller, with a top-level try/catch that prevents unobserved
        /// exceptions from escaping to the host runtime (where they may crash
        /// Unity / IL2CPP).
        ///
        /// Use this instead of declaring `async void` methods. The original
        /// `async void` pattern exposes the entire mod to silent process crashes
        /// whenever an exception escapes — typical when an HTTP call fails, the
        /// server returns malformed JSON, or any UI lookup hits an unexpected
        /// null.
        ///
        /// Example:
        ///   private void OnButtonClicked() =&gt; TranslatorUIManager.RunSafe(
        ///       async () =&gt; { ... await something ...; },
        ///       nameof(OnButtonClicked));
        /// </summary>
        public static async void RunSafe(Func<Task> work, string context = null)
        {
            if (work == null) return;
            try
            {
                await work();
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[{context ?? "RunSafe"}] {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            }
        }

        // Thread-safe queue of actions to execute on the main thread.
        // Drained by DrainMainThreadQueue() called every frame from
        // TranslatorCore.OnUpdate (itself called by every adapter's frame
        // callback). The previous implementation called Unity's StartCoroutine
        // directly, which throws "can only be called from the main thread"
        // when invoked from a background thread (typical IL2CPP situation
        // after an HTTP await). The throw bubbled up to async void callers
        // and crashed the process silently.
        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        public static void RunOnMainThread(Action action)
        {
            if (action == null) return;
            _mainThreadQueue.Enqueue(action);
        }

        /// <summary>
        /// Drain queued actions on the main thread. Each action runs inside try/catch
        /// so a faulty callback does not kill the entire batch nor surface as an
        /// unobserved exception.
        /// </summary>
        public static void DrainMainThreadQueue()
        {
            // Snapshot count first so a callback that re-enqueues itself
            // doesn't make this loop run unbounded for a frame.
            int budget = _mainThreadQueue.Count;
            while (budget-- > 0 && _mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    TranslatorCore.LogError($"[UIManager] RunOnMainThread callback error: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                }
            }

            // Polled update check, when the player did not ask for a live stream
            try
            {
                TickSyncCheck();
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Sync] Update check tick failed: {e.Message}");
            }

            // Live edit session housekeeping (debounced pushes, browser grace timer)
            try
            {
                TickEditSession();
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[UIManager] TickEditSession error: {e.Message}");
            }
        }

        /// <summary>
        /// Run an action after a delay (in seconds).
        /// </summary>
        public static void RunDelayed(float seconds, Action action)
        {
            if (action == null) return;
            RuntimeHelper.StartCoroutine(RunDelayedCoroutine(seconds, action));
        }

        private static IEnumerator RunDelayedCoroutine(float seconds, Action action)
        {
            yield return new WaitForSeconds(seconds);
            try
            {
                action();
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[UIManager] RunDelayed error: {e.Message}");
            }
        }

        /// <summary>
        /// Initialize the UI system. Called from TranslatorCore after UniverseLib is ready.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            TranslatorCore.LogInfo("[UIManager] Initializing UniverseLib...");

            ApiClient.OnAuthenticationRejected += HandleAuthenticationRejected;

            // Drive the ENTIRE UniverseLib color palette from our UIStyles theme, so every UI color is
            // controlled from ONE place (the plugin). Runs before any panel is built. Elements created
            // by UniverseLib itself (toggles/checkboxes, sliders, default buttons, dropdowns, inputs)
            // now follow the theme too — previously only DefaultLayoutBackground was wired, so e.g. the
            // unchecked checkbox stayed on UniverseLib's own hardcoded default.
            UIFactory.Colors.DefaultLayoutBackground = UIStyles.ViewportBackground;
            UIFactory.Colors.DefaultLayoutPadding = new Vector4(
                UIStyles.SmallSpacing, UIStyles.SmallSpacing,
                UIStyles.SmallSpacing, UIStyles.SmallSpacing);
            UIFactory.Colors.PanelBackground       = UIStyles.CardBackground;
            UIFactory.Colors.SlightBackground      = UIStyles.ItemBackground;
            UIFactory.Colors.DarkBackground        = UIStyles.PanelBackground;
            UIFactory.Colors.TitleBarBackground    = UIStyles.TabBarBackground;
            UIFactory.Colors.Accent                = UIStyles.ButtonPrimary;
            UIFactory.Colors.AccentHighlight       = UIStyles.ButtonHover;
            UIFactory.Colors.AccentPressed         = UIStyles.AccentPressed;
            UIFactory.Colors.ButtonNormal          = UIStyles.ButtonSecondary;
            UIFactory.Colors.ButtonHighlight       = UIStyles.InputBackground;
            UIFactory.Colors.ButtonPressed         = UIStyles.ButtonPressed;
            UIFactory.Colors.SliderBackground      = UIStyles.SliderBackgroundColor;
            UIFactory.Colors.SliderFill            = UIStyles.SliderFillColor;
            UIFactory.Colors.SliderHandle          = UIStyles.SliderHandleColor;
            UIFactory.Colors.InputBackground       = UIStyles.InputBackground;
            UIFactory.Colors.InputBorder           = UIStyles.InputBorderColor;
            UIFactory.Colors.PlaceholderText       = UIStyles.TextMuted;
            UIFactory.Colors.ToggleBackground      = UIStyles.CheckboxUnchecked;
            UIFactory.Colors.ToggleCheckmark       = UIStyles.CheckboxCheckmark;
            UIFactory.Colors.ToggleBorder          = UIStyles.CheckboxBorder;
            UIFactory.Colors.DropdownBackground    = UIStyles.DropdownBackground;
            UIFactory.Colors.DropdownItemNormal    = UIStyles.DropdownItemNormal;
            UIFactory.Colors.DropdownItemHighlight = UIStyles.DropdownItemHighlight;

            // Use per-game setting for EventSystem override (stored in translations.json as _settings.disable_eventsystem_override)
            // Default is false (UniverseLib CAN override). Set to true in translations.json if the game's UI animations break.
            Universe.Init(1f, OnUniverseLibInitialized, LogHandler, new UniverseLib.Config.UniverseLibConfig
            {
                Disable_EventSystem_Override = TranslatorCore.DisableEventSystemOverride, // Per-game setting, requires restart
                Force_Unlock_Mouse = false, // We manage cursor ourselves to avoid unlocking when only StatusOverlay is shown
                Allow_UI_Selection_Outside_UIBase = true, // Don't block game's UI navigation when our overlay is shown
                Unhollowed_Modules_Folder = null
            });
        }

        private static void OnUniverseLibInitialized()
        {
            TranslatorCore.LogInfo("[UIManager] UniverseLib initialized, creating UI...");

            UiBase = UniversalUI.RegisterUI("UnityGameTranslator", UpdateUI);

            // Register the mod's top-level UI root for hierarchy-based own-UI detection.
            // Every mod panel, overlay and popup is a descendant of this root, so
            // IsOwnUIByHierarchy identifies ALL our UI no matter when (or whether) an
            // individual label calls RegisterUIText/RegisterExcluded. This closes the leak
            // window where a runtime label (e.g. a corner-overlay notification) set its text
            // — and thus exposed its font "arial" — before its registration ran, letting the
            // mod's own font leak into the game's font map.
            if (UiBase?.RootObject != null)
                TranslatorCore.RegisterPanelRoot(UiBase.RootObject);

            // Remember UniverseLib's original UI font so we can restore it if the user clears
            // the interface font. Captured before any panel is created.
            if (_originalUIFont == null)
                _originalUIFont = UniversalUI.DefaultFont;

            // Answer, per frame and per kind, whether the game's input belongs to us right now.
            // Asked by UniverseLib at each read, so it follows the panels with no state of ours to
            // keep in sync — the bug that a cached "are we open?" flag always ends up producing.
            UniverseLib.Input.InputCapture.ShouldCapture = ShouldCaptureInput;
            // Independent of the capture switches: a game asking "is the pointer over UI?" must
            // hear yes while our window owns it, so it dismisses the click instead of keeping it.
            UniverseLib.Input.InputCapture.UiOwnsPointer = () => _uiHoldsInput;

            // Our canvases that live outside UniverseLib's root: the click absorber, and the
            // inspector's highlight overlay. Both must answer the pointer, not be silenced with
            // the game's.
            UniverseLib.Input.InputCapture.OwnsRaycaster = caster =>
                caster != null && TranslatorCore.IsOwnUIByHierarchy(caster);

            // A click that closes a panel must not also reach the game behind it. Two frames for
            // the panel to go away, six before the game gets its input back — about 30 ms and
            // 100 ms, neither of which anybody can feel, and both well clear of the single frame
            // that theory says is needed. Established by pulling them apart to 1 s and 10 s until
            // the leak stopped, then closing the gap; see analyse/input-capture-and-priority.md.
            UniverseLib.UI.Panels.PanelBase.CloseDelayFrames = 2;

            // Single source of truth for the per-frame tick: run OnUpdate (feeds the scanner's
            // adaptive frame-time budget, persists cache) and Scan (applies pending translations
            // + scans the scene) inside a permanent coroutine, plus the main-thread queue drain
            // on every frame. We intentionally do NOT also tick from each mod loader's Update()
            // callback — that would double-call. The coroutine works even in games whose host
            // suppresses our MonoBehaviour.Update (one was observed to do so) because Unity
            // drives coroutines through a separate path hosted by UniverseLib's own runtime,
            // which is proven to tick wherever our UI already works.
            //
            // ⚠ STARTED BEFORE THE PANELS, and that ordering is load-bearing. Translating a game
            // needs no window: when a panel constructor threw, CreatePanels() aborted and took
            // the tick down with it, so a cosmetic bug became a mod that translated nothing.
            // The tick asks TranslatorCore.SetupCompleted before touching the game, so starting
            // it early grants nothing the player has not agreed to.
            try
            {
                RuntimeHelper.StartCoroutine(MainTickLoop());
                TranslatorCore.LogInfo("[UIManager] Main tick coroutine started");
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[UIManager] Failed to start main tick coroutine: {e.GetType().Name}: {e.Message}");
            }

            CreatePanels();

            _initialized = true;

            // Notify listeners (e.g., TranslatorPatches to retry failed font replacements)
            try { OnInitialized?.Invoke(); } catch { }

            // Initialize UI state based on config
            InitializeUIState();
        }

        /// <summary>
        /// Does the game's input belong to us at this instant?
        ///
        /// Deliberately NOT "is any of our UI showing". Three states, not two:
        ///
        /// - <b>Nothing open</b> — the game owns everything. Obviously.
        /// - <b>Only the corner overlay</b> — still the game's. A notification is something you
        ///   glance at while playing; taking the controls away to show one would be an ambush, and
        ///   the overlay is not even clickable. This is why the test is AnyPanelVisible() and not
        ///   UniversalUI.AnyUIShowing, which counts the overlay.
        /// - <b>A panel open</b> — ours. Someone reading a settings window is not also driving; the
        ///   keys they press are meant for the field they are typing in, and the mouse they move is
        ///   aiming at a button, not at a target in the game.
        ///
        /// Each kind stays subject to its own per-game setting, so a game whose menus break under
        /// capture can have it turned off one kind at a time rather than all or nothing.
        /// </summary>
        private static bool ShouldCaptureInput(UniverseLib.Input.InputCapture.CaptureKind kind)
        {
            if (!_uiHoldsInput)
                return false;



            switch (kind)
            {
                case UniverseLib.Input.InputCapture.CaptureKind.Keyboard:
                    return TranslatorCore.CaptureKeyboard;
                case UniverseLib.Input.InputCapture.CaptureKind.MouseButtons:
                    return TranslatorCore.CaptureMouseButtons;
                case UniverseLib.Input.InputCapture.CaptureKind.MouseAxes:
                    return TranslatorCore.CaptureMouseAxes;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Is the mouse doing nothing at all this frame — no button held, none released?
        /// </summary>
        /// <remarks>
        /// Gates handing the EventSystem back to the game. Closing a panel used to give it back
        /// the instant the panel went away, which is mid-click: the click that pressed our close
        /// button was still being resolved, and ReleaseEventSystem re-activates the game's input
        /// module (UniverseLib's EventSystemHelper), which then finished the click on whatever sat
        /// behind — a game menu opening the moment our window closed.
        ///
        /// Reported precisely: buttons that do NOT close a panel never leaked, only the close
        /// button did, and the game saw it AFTER the window was gone. That is a handover problem,
        /// not a click-through one, which is why nothing here touches raycasts.
        ///
        /// This is a trigger, not a delay: the handover happens on the first frame the mouse is
        /// idle, which is usually the very next one. Nothing is timed and nothing is guessed.
        /// </remarks>
        /// <summary>
        /// Who is listening to the pointer right now — every EventSystem in the scene and whether
        /// it is enabled, plus its current input module.
        /// </summary>
        private static void LogEventSystemState()
        {
            try
            {
                var all = UnityEngine.Object.FindObjectsOfType(typeof(UnityEngine.EventSystems.EventSystem));
                var parts = new List<string>();
                for (int i = 0; i < all.Length; i++)
                {
                    var es = all[i] as UnityEngine.EventSystems.EventSystem;
                    if (es == null) continue;
                    var mod = es.currentInputModule;
                    parts.Add($"{es.name}[enabled={es.enabled}, module={(mod == null ? "none" : mod.GetType().Name)}"
                        + $"{(es == UnityEngine.EventSystems.EventSystem.current ? ", CURRENT" : "")}]");
                }
                TranslatorCore.LogInfo($"[Handover] at release — EventSystems: {string.Join(" | ", parts.ToArray())}");
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Handover] could not inspect EventSystems: {e.Message}");
            }
        }

        /// <summary>
        /// Colour of the click absorber: none. It has to be a raycast target, not a visible thing.
        /// </summary>
        /// <remarks>
        /// Tint it (e.g. red at 0.2 alpha) to debug. Fifteen attempts at this bug ASSUMED the
        /// absorber was in place at the moment a panel closed; one glance at a coloured wash
        /// showed it was there and that the click still went past it, which is what finally
        /// pointed at the real cause. A log line could not have shown that.
        /// </remarks>
        private static readonly Color AbsorberTint = new Color(0f, 0f, 0f, 0f);

        // A full-screen, fully transparent surface that swallows what is left of a click.
        private static GameObject _clickAbsorber;
        private static UnityEngine.UI.Image _clickAbsorberImage;

        /// <summary>
        /// Catch the click a closing panel leaves behind, before the game does.
        /// </summary>
        /// <remarks>
        /// The click that presses a close button is HELD by the input module and delivered to
        /// whatever the raycast finds once a target is available again — measured: ten seconds
        /// later with the mouse still, and it still landed on the game's button. It is never
        /// cancelled, only redirected, so the answer is to be what it finds.
        ///
        /// ⚠ On its OWN canvas, like the inspector's highlight overlay, and for the same stated
        /// reason — "so they can block clicks from reaching game elements below". A child of
        /// UniverseLib's canvas is no good: when the last panel closes the module lets go, that
        /// canvas stops being consulted, and the absorber vanishes at the exact moment it is
        /// needed. DontDestroyOnLoad for the same reason as the inspector's: a scene change must
        /// not take it away mid-click.
        ///
        /// Sorting order sits below the inspector's highlights (29000) and below UniverseLib's own
        /// UI (30000), so it never covers anything of ours — only the game.
        /// </remarks>
        private static void SetClickAbsorber(bool on)
        {
            if (_clickAbsorber == null)
            {
                if (!on) return;

                _clickAbsorber = new GameObject("UGT_ClickAbsorber");
                UnityEngine.Object.DontDestroyOnLoad(_clickAbsorber);

                var canvas = _clickAbsorber.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.sortingOrder = 28000;
                _clickAbsorber.AddComponent<UnityEngine.UI.GraphicRaycaster>();

                var surface = new GameObject("Surface");
                surface.transform.SetParent(_clickAbsorber.transform, false);
                var rect = surface.AddComponent<RectTransform>();
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;

                _clickAbsorberImage = surface.AddComponent<UnityEngine.UI.Image>();
                _clickAbsorberImage.color = AbsorberTint;
                _clickAbsorberImage.raycastTarget = true;

                TranslatorCore.RegisterPanelRoot(_clickAbsorber);
            }

            if (_clickAbsorber.activeSelf != on)
                _clickAbsorber.SetActive(on);
        }

        /// <summary>
        /// Drop whatever the game had selected, so nothing can be submitted to it behind our back.
        /// </summary>
        /// <remarks>
        /// ⚠ The last chink. Measured with everything else finally correct: the game's button
        /// reported "blocked" on every raycast, our absorber reported "ours", and it was pressed
        /// anyway. A blocked raycast cannot deliver a pointer click — so it was never a pointer
        /// click. uGUI has exactly one other route: Submit, sent by the module to the SELECTED
        /// object with no raycast at all, and on this input module Submit is bound to the same
        /// button as clicking.
        ///
        /// Allow_UI_Selection_Outside_UIBase stops the game being selected ANEW, but whatever was
        /// already selected when our window opened stays selected. Hence clearing it outright.
        ///
        /// The library's own guard rejects a null selection while a UI is showing, so the flag is
        /// lifted for the duration of the call — deliberately, and put straight back.
        /// </remarks>
        private static void DeselectGameObject()
        {
            try
            {
                // EVERY EventSystem, not EventSystem.current: on this game "current" is the
                // game's, disabled and holding nothing, so clearing only that one did nothing at
                // all. The selection that matters belongs to whichever system the module serves.
                var all = UnityEngine.Object.FindObjectsOfType(typeof(UnityEngine.EventSystems.EventSystem));
                bool previous = ConfigManager.Allow_UI_Selection_Outside_UIBase;
                ConfigManager.Allow_UI_Selection_Outside_UIBase = true;
                try
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        var es = all[i] as UnityEngine.EventSystems.EventSystem;
                        if (es == null) continue;

                        var selected = es.currentSelectedGameObject;
                        if (selected == null) continue;

                        es.SetSelectedGameObject(null);
                    }
                }
                finally { ConfigManager.Allow_UI_Selection_Outside_UIBase = previous; }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[UIManager] Could not clear the game's selection: {e.Message}");
            }
        }

        /// <summary>
        /// How long the absorber outlives the capture. Must cover the frames the input module
        /// takes to notice the release it could not see while input was held.
        /// </summary>
        private const float SecondsAbsorberOutlivesCapture = 0.1f;

        private static float _absorberUntil;

        /// <summary>Take the absorber down once the released click has had time to land on it.</summary>
        /// <remarks>
        /// A fresh press takes it down at once, whatever the countdown says. It is the only thing
        /// this surface could cost: a click aimed at the game during the tenth of a second it
        /// lingers would be swallowed by it. Removing it ON the press — before that press has been
        /// read — means the click reaches the game and nothing is lost.
        /// </remarks>
        private static void TickAbsorber()
        {
            if (_absorberUntil <= 0f)
                return;

            if (Time.realtimeSinceStartup < _absorberUntil && !NewPressStarted())
                return;

            _absorberUntil = 0f;
            SetClickAbsorber(false);
        }

        /// <summary>Has a mouse button gone down this frame?</summary>
        private static bool NewPressStarted()
        {
            for (int btn = 0; btn <= 2; btn++)
            {
                if (InputManager.GetMouseButtonDown(btn))
                    return true;
            }
            return false;
        }

        private static bool MouseAtRest()
        {
            for (int btn = 0; btn <= 2; btn++)
            {
                if (InputManager.GetMouseButton(btn) || InputManager.GetMouseButtonUp(btn))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Frames to keep the input after the last panel closes, before the game gets it back.
        /// </summary>
        /// <remarks>
        /// ⚠ A timer, and it is the last resort it looks like — but the alternative was measured
        /// and found blind, not merely suspected. Logged at the instant a panel closed:
        ///
        ///     [Handover] panel closed — btn0=False up0=False backend=InputSystem
        ///
        /// The mouse reads as idle WHILE the click that pressed the close button is still being
        /// resolved, because that game's backend reports neither the press nor the release to us.
        /// Waiting "until the mouse is idle" therefore expired instantly and guarded nothing. There
        /// is nothing observable here to trigger on; counting frames is what is left.
        ///
        /// ⚠ Ten frames were tried against the click that survives a panel closing, and changed
        /// nothing — so this is NOT what that bug is about, and the number is not tuned against it.
        /// Two frames is kept for what it does justify on its own: not handing input back in the
        /// middle of the frame a panel disappeared in. Raising it would be cargo cult.
        /// <see cref="MouseAtRest"/> is still required on top: on a backend that DOES answer, the
        /// wait ends on the event and this ceiling never bites.
        /// </remarks>
        /// <summary>
        /// How long the game waits for its input back after the last panel closes.
        /// </summary>
        /// <remarks>
        /// In SECONDS, not frames: the thing being waited out is a timeout inside the game's input
        /// handling, so counting frames would make it a third of this at 20 fps and twice at 120.
        ///
        /// Measured by isolation. With the close kept short, a 10 s handover stopped the leak and
        /// 0.1 s did not — so it is the handover, not the closing, that matters. That points at
        /// the Input System holding an interaction for a while before dropping it: its
        /// defaultTapTime is 0.2 s, which sits exactly between the two results. 0.3 s clears it
        /// with margin and is still nothing: nobody clicks into the game within a third of a
        /// second of closing a window.
        /// </remarks>
        /// <remarks>
        /// ⚠ Long on purpose, and it costs nothing: a fresh press hands the input back at once
        /// (see <see cref="ShouldCaptureInput"/>), so this only ever elapses while nobody is
        /// touching the mouse. It is a ceiling for the case where the player closes a window and
        /// walks away, not a pause anyone waits through.
        ///
        /// Measured on the game that leaks: 0.1 s and 0.3 s both let the click through, 10 s did
        /// not. The Input System keeps an interaction alive for its configured tap window —
        /// 0.2 s by default, 0.75 s for a multi-tap — so a game tuning that upwards explains the
        /// two failures. 0.9 s clears the longest of those defaults.
        /// </remarks>
        /// <summary>
        /// How long the absorber stays after the last panel closes, before the game gets its
        /// input back. Long enough for the held click to be released onto it, short enough that
        /// nobody notices the game was not listening.
        /// </summary>
        private const float SecondsBeforeHandover = 0.05f;

        private static float _closedAt;



        /// <summary>
        /// Frames since the panel closed. Deliberately NOT reset by mouse activity: on a backend
        /// that never reports any, resetting on "not idle" is what made the previous guard expire
        /// on its first frame, every time.
        /// </summary>
        /// <summary>Seconds since the panel closed. Started on the first call after a close.</summary>
        private static float SecondsSinceClose()
        {
            if (_closedAt <= 0f)
                _closedAt = Time.realtimeSinceStartup;

            float elapsed = Time.realtimeSinceStartup - _closedAt;

            // Info, not Debug: it has to show up on a machine nobody thought to put in debug mode,
            // and it is one line per handover.
            if (elapsed <= 0f && TranslatorCore.DebugMode)
            {
                TranslatorCore.LogInfo($"[Handover] panel closed — "
                    + $"btn0={InputManager.GetMouseButton(0)} up0={InputManager.GetMouseButtonUp(0)} "
                    + $"backend={InputManager.CurrentType}");
            }

            return elapsed;
        }

        private static IEnumerator MainTickLoop()
        {
            while (true)
            {
                // Catch around each iteration so a transient failure (e.g. a scene with
                // unexpected components) does not silently terminate the loop — Unity
                // stops a coroutine the first time an exception escapes. It logs: this is a
                // process boundary, not a way of not knowing.
                try
                {
                    // ALWAYS, gate or no gate: this is the plumbing that carries async results
                    // back to the main thread, and the wizard itself runs on it (its connection
                    // test and model list are RunOnMainThread callbacks). Freezing it would
                    // freeze the very screen that grants permission.
                    DrainMainThreadQueue();

                    // Everything below TOUCHES THE GAME — reads its scene, rewrites its text,
                    // writes the cache to disk. None of it may happen before someone said yes.
                    if (TranslatorCore.SetupCompleted)
                    {
                        TranslatorCore.OnUpdate(Time.realtimeSinceStartup);
                        TranslatorScanner.Scan();
                    }
                }
                catch (Exception e)
                {
                    TranslatorCore.LogError($"[MainTick] {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                }
                yield return null;
            }
        }

        private static void CreatePanels()
        {
            // Create all panels
            WizardPanel = new Panels.WizardPanel(UiBase);
            MainPanel = new Panels.MainPanel(UiBase);
            OptionsPanel = new Panels.OptionsPanel(UiBase);
            LoginPanel = new Panels.LoginPanel(UiBase);
            UploadPanel = new Panels.UploadPanel(UiBase);
            UploadSetupPanel = new Panels.UploadSetupPanel(UiBase);
            MergePanel = new Panels.MergePanel(UiBase);
            LanguagePanel = new Panels.LanguagePanel(UiBase);
            StatusOverlay = new Panels.StatusOverlay(UiBase);
            ConfirmationPanel = new Panels.ConfirmationPanel(UiBase);
            SettingsChoicePanel = new Panels.SettingsChoicePanel(UiBase);
            InspectorPanel = new Panels.InspectorPanel(UiBase);
            TranslationParamsPanel = new Panels.TranslationParametersPanel(UiBase);

            // Register interactive panels (excludes StatusOverlay which is a notification overlay)
            _interactivePanels.Clear();
            _interactivePanels.Add(WizardPanel);
            _interactivePanels.Add(MainPanel);
            _interactivePanels.Add(OptionsPanel);
            _interactivePanels.Add(LoginPanel);
            _interactivePanels.Add(UploadPanel);
            _interactivePanels.Add(UploadSetupPanel);
            _interactivePanels.Add(MergePanel);
            _interactivePanels.Add(LanguagePanel);
            _interactivePanels.Add(ConfirmationPanel);
            _interactivePanels.Add(SettingsChoicePanel);
            _interactivePanels.Add(InspectorPanel);
            _interactivePanels.Add(TranslationParamsPanel);

            // Hide all panels initially (using centralized list + StatusOverlay)
            CloseAllPanels();
            StatusOverlay.SetActive(false);
        }

        /// <summary>
        /// Apply the mod's interface font. When translate_mod_ui is on and the user has assigned a
        /// font in config (Config.interface_font), load that font and use it for the whole mod UI;
        /// otherwise restore UniverseLib's original UI font. Sets
        /// UniversalUI.DefaultFont for future text and re-fonts every existing mod UI Text.
        /// Applied separately from the game font pipeline (which never touches our UI).
        /// </summary>
        public static void ApplyInterfaceFont()
        {
            if (UiBase?.RootObject == null || _originalUIFont == null) return;
            if (_originalUIFontFamily == null) _originalUIFontFamily = _originalUIFont.name;

            // IL2CPP can't create a fresh OS-backed Font (CreateDynamicFontFromOSFont / new Font are
            // stripped → MissingMethodException). So we don't swap the Font object: we REBACK UniverseLib's
            // UI font in place — rewrite its fontNames to the chosen system font's family, and FreeType
            // re-rasterizes with it. Same mechanism the game font-replacement uses (FontManager reback via
            // TextureHelper.SetFontNames). Works on Mono AND IL2CPP.
            // The font in effect: the user's local override, else the one the translation asks for.
            string requestedFont = TranslatorCore.EffectiveInterfaceFont;

            // The font may simply not be here: the translation names one whose files come from the
            // author's resources link, or a config arrived from another machine. The interface then
            // stays English (ShouldTranslateOwnUI is false) rather than showing boxes — so say what
            // is missing, since this window is where the user comes to find out.
            if (!string.IsNullOrEmpty(requestedFont) && TranslatorCore.InterfaceFontMissing)
            {
                if (_missingInterfaceFontReported != requestedFont)
                {
                    _missingInterfaceFontReported = requestedFont;
                    TranslatorCore.LogWarning($"[UIManager] Interface font '{requestedFont}' is missing — " +
                        "mod interface kept in English (get the translation's resources to install it)");
                    StatusOverlay?.ShowToast(
                        $"Missing font '{requestedFont}' — interface kept in English. Install the translation's resources.",
                        Panels.StatusOverlay.ToastTone.Off);
                }
            }
            else
            {
                _missingInterfaceFontReported = null;
            }

            bool wantCustom = TranslatorCore.ShouldTranslateOwnUI && !string.IsNullOrEmpty(requestedFont)
                && !TranslatorCore.InterfaceFontMissing;

            // The split is by RUNTIME CAPABILITY, not by platform: wherever a fresh OS-backed Font can
            // be created (Mono, and IL2CPP builds that kept CreateDynamicFontFromOSFont) we swap the
            // font object — an empty atlas re-renders cleanly at runtime. Where it was stripped, we
            // reback the existing UI font's OS backing (fontNames) instead.
            UnityEngine.Font freshFont = wantCustom ? FontManager.LoadUIFont(requestedFont) : null;
            TranslatorCore.LogDebug($"[UIManager] Interface font: want={wantCustom} fresh={(freshFont != null ? freshFont.name : "null")} " +
                $"path={(wantCustom && freshFont != null ? "swap" : wantCustom ? "reback" : "restore")}");

            if (wantCustom && freshFont != null)
            {
                UniversalUI.DefaultFont = freshFont;
                int changed = 0;
                SwapModUIFont(UiBase.RootObject.transform, freshFont, ref changed);
                _uiFontRebacked = false;
                _pendingRebackFont = null;
            }
            else if (wantCustom)
            {
                // IL2CPP: fresh Font creation is stripped, so reback the UI font's OS backing (fontNames).
                // Rebacking directly over a previous font leaves the atlas stale at runtime — the change
                // only shows after an off→on toggle (as the user found). So do that cycle automatically:
                // restore the original font NOW, then reback the chosen font after a gap (TickFontRerender)
                // long enough for the atlas to re-rasterize between the two — like two Apply clicks.
                FontManager.RestoreFontToOriginal(_originalUIFont, _originalUIFontFamily);
                UniversalUI.DefaultFont = _originalUIFont;
                RerenderModUIFont(false);
                _pendingRebackFont = requestedFont;
                _rebackDelay = 60; // ~1s — the slowest atlas we've seen (Frog); LongYin tolerates it too
                _uiFontRebacked = true;
            }
            else
            {
                // Restore original. Cancel any pending reback first, else a queued reback would re-apply
                // the font after the user turned the feature off ("keeps the first fallback" bug).
                _pendingRebackFont = null;
                _fontRerenderCountdown = 0;
                if (_uiFontRebacked)
                {
                    FontManager.RestoreFontToOriginal(_originalUIFont, _originalUIFontFamily);
                    RerenderModUIFont(false);
                    _uiFontRebacked = false;
                }
                UniversalUI.DefaultFont = _originalUIFont;
                int changed = 0;
                SwapModUIFont(UiBase.RootObject.transform, _originalUIFont, ref changed);
            }
        }

        /// <summary>
        /// Swap the uGUI Text font across the mod UI subtree to <paramref name="font"/> (Mono path /
        /// restore). Warms the atlas at each label's size and toggles enabled to rebind — a fresh font
        /// object has an empty atlas so this renders at runtime.
        /// </summary>
        private static void SwapModUIFont(Transform node, UnityEngine.Font font, ref int changed)
        {
            if (node == null) return;
            var text = node.GetComponent<UnityEngine.UI.Text>();
            if (text != null && text.font != font)
            {
                text.font = font;
                if (!string.IsNullOrEmpty(text.text))
                {
                    try { font.RequestCharactersInTexture(text.text, text.fontSize, text.fontStyle); }
                    catch { }
                }
                text.SetAllDirty();
                if (text.gameObject.activeInHierarchy)
                {
                    text.enabled = false;
                    text.enabled = true;
                }
                changed++;
            }
            int count = node.childCount;
            for (int i = 0; i < count; i++)
                SwapModUIFont(node.GetChild(i), font, ref changed);
        }

        /// <summary>Deferred re-dirty tick (called from UpdateUI): once the atlas has warmed after a
        /// font reback, rebuild the mod UI meshes so they pick up the new glyphs.</summary>
        private static void TickFontRerender()
        {
            if (UiBase?.RootObject == null) return;

            // Deferred reback (IL2CPP): the original font was restored on Apply; after the gap that lets
            // the atlas re-rasterize, reback the chosen font — the off→on cycle automated.
            if (_pendingRebackFont != null)
            {
                if (--_rebackDelay <= 0)
                {
                    string font = _pendingRebackFont;
                    _pendingRebackFont = null;
                    FontManager.RebackFontToSystem(_originalUIFont, font);
                    UniversalUI.DefaultFont = _originalUIFont;
                    RerenderModUIFont(true);
                    _fontRerenderCountdown = 30;
                }
                return;
            }

            if (_fontRerenderCountdown <= 0) return;
            _fontRerenderCountdown--;
            // Re-request glyphs + re-dirty every frame in the window (no size bump — applied once, no
            // enable toggle — would flicker). When the async atlas rebuild finishes, the next dirty
            // rebuild picks it up. Toggle once at the end to force a final rebind.
            bool last = _fontRerenderCountdown == 0;
            int changed = 0;
            RerenderModUIWalk(UiBase.RootObject.transform, 0, last, ref changed);
        }

        /// <summary>
        /// Re-render the mod UI after a font reback. The glyph atlas caches by (char, size); the current
        /// sizes still hold the old glyphs, so re-requesting them returns the cached ones. Bumping the
        /// size by +1 while a custom interface font is active forces FreeType to rasterize fresh entries
        /// with the new fontNames (1px larger — negligible, opt-in only); dropping the bump on restore
        /// returns to the original glyphs. Then warm + toggle enabled to rebind the CanvasRenderer.
        /// (Limitation: switching between two custom fonts reuses the +1 size and may not re-raster; the
        /// common arial↔custom flow does.)
        /// </summary>
        private static void RerenderModUIFont(bool custom)
        {
            int target = custom ? 1 : 0;
            int delta = target - _uiFontBumpDelta;
            _uiFontBumpDelta = target;

            int changed = 0;
            RerenderModUIWalk(UiBase.RootObject.transform, delta, true, ref changed);
            TranslatorCore.LogInfo($"[UIManager] Interface font re-render: {changed} Text (bump {_uiFontBumpDelta}, custom={custom})");
        }

        private static void RerenderModUIWalk(Transform node, int sizeDelta, bool toggle, ref int changed)
        {
            if (node == null) return;
            var text = node.GetComponent<UnityEngine.UI.Text>();
            if (text != null)
            {
                if (sizeDelta != 0) text.fontSize += sizeDelta;
                if (!string.IsNullOrEmpty(text.text))
                {
                    try { text.font.RequestCharactersInTexture(text.text, text.fontSize, text.fontStyle); }
                    catch { }
                }
                text.SetAllDirty();
                // Toggle enabled only on the initial pass (rebind); the per-frame tick must not toggle
                // (would flicker for the whole window).
                if (toggle && text.gameObject.activeInHierarchy)
                {
                    text.enabled = false;
                    text.enabled = true;
                }
                changed++;
            }
            int count = node.childCount;
            for (int i = 0; i < count; i++)
                RerenderModUIWalk(node.GetChild(i), sizeDelta, toggle, ref changed);
        }

        /// <summary>
        /// Kick off translation of the mod's own UI text. Static labels are created during
        /// construction mode (translation skipped) and never re-set, so they never reach the
        /// text patch — nothing submits them for translation. Re-assigning each Text's value
        /// re-invokes the patched setter: with translate_mod_ui on it queues the string and
        /// tracks the component (PatchedComponentRefs), so the async result is applied back
        /// like any other text. No-op when the mod UI isn't being translated.
        /// </summary>
        public static void RefreshOwnUITranslation()
        {
            if (UiBase?.RootObject == null) return;
            if (!TranslatorCore.ShouldTranslateOwnUI || !TranslatorCore.TranslationsActive) return;

            int count = 0;
            RetriggerOwnUIText(UiBase.RootObject.transform, ref count);
            TranslatorCore.LogInfo($"[UIManager] Re-submitted {count} mod UI text(s) for translation");
        }

        private static void RetriggerOwnUIText(Transform node, ref int count)
        {
            if (node == null) return;
            var text = node.GetComponent<UnityEngine.UI.Text>();
            // WHITELIST: only submit text that was EXPLICITLY registered as translatable UI chrome
            // (labels, buttons, hints). Never the rest — dropdown VALUES, input-field text, language/
            // font names, hotkeys, tags: those are data/identifiers/user input and translating them
            // corrupts the UI. IsOwnUITranslatable(int) checks explicit registration only (no hierarchy).
            if (text != null && !string.IsNullOrEmpty(text.text)
                && TranslatorCore.IsOwnUITranslatable(text.GetInstanceID()))
            {
                // Enqueue directly (not via a text re-assignment): re-setting the same value does
                // not fire the set_text patch (the setter short-circuits on equal values), so the
                // patch route never queued anything. QueueForTranslation registers this component in
                // pendingComponents AND enqueues; isOwnUI is resolved at processing time from those
                // components (UI-specific prompt + tag "M"). When the worker finishes, the result is
                // applied back to this component via the normal completion path.
                if (!TranslatorCore.HasCachedTranslation(text.text))
                {
                    TranslatorCore.QueueForTranslation(text.text, text, isOwnUI: true);
                    count++;
                }
                else
                {
                    // Already translated in the cache — apply it now. Wrap the assignment in
                    // BypassTextPrefix so the set_text patch doesn't re-process the translated value.
                    string translated = TranslatorCore.TranslateTextWithTracking(text.text, text, isOwnUI: true);
                    if (!string.IsNullOrEmpty(translated) && translated != text.text)
                    {
                        TranslatorPatches.BypassTextPrefix = true;
                        try { text.text = translated; }
                        finally { TranslatorPatches.BypassTextPrefix = false; }
                        count++;
                    }
                }
            }
            int n = node.childCount;
            for (int i = 0; i < n; i++)
                RetriggerOwnUIText(node.GetChild(i), ref count);
        }

        /// <summary>
        /// Restore every own-UI label we translated back to its original (English) text. Called when
        /// the mod-UI translation is turned off. Wraps each assignment in BypassTextPrefix so the
        /// set_text patch doesn't re-translate on the way back.
        /// </summary>
        public static void RestoreOwnUIEnglish()
        {
            if (UiBase?.RootObject == null) return;

            int restored = 0;
            TranslatorPatches.BypassTextPrefix = true;
            try { RestoreOwnUIWalk(UiBase.RootObject.transform, ref restored); }
            finally { TranslatorPatches.BypassTextPrefix = false; }
            TranslatorCore.LogInfo($"[UIManager] Restored {restored} mod UI text(s) to original (English)");
        }

        /// <summary>
        /// Walk the mod UI and restore each Text that has a stored original (i.e. was translated by
        /// ANY path — the startup set_text patch or the runtime re-submit) back to English. Uses the
        /// mod's own original tracking (StoreOriginalText/GetOriginalText), which a per-enqueue
        /// snapshot misses (that only covered the panels built at bootstrap time).
        /// </summary>
        private static void RestoreOwnUIWalk(Transform node, ref int restored)
        {
            if (node == null) return;
            var text = node.GetComponent<UnityEngine.UI.Text>();
            if (text != null)
            {
                int id = text.GetInstanceID();
                string original = TranslatorScanner.GetOriginalText(id);
                if (original != null && text.text != original)
                {
                    text.text = original;
                    text.SetAllDirty();
                    TranslatorScanner.ClearOriginalText(id);
                    restored++;
                }
            }
            int n = node.childCount;
            for (int i = 0; i < n; i++)
                RestoreOwnUIWalk(node.GetChild(i), ref restored);
        }

        private static void InitializeUIState()
        {
            TranslatorCore.LogInfo($"[UIManager] InitializeUIState, first_run_completed={TranslatorCore.Config.first_run_completed}");

            // Interface font + mod-UI translation are applied lazily on first show
            // (BootstrapInterfaceFontOnce): at this point custom fonts and the worker aren't ready yet.

            // Restore API token if saved
            if (!string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                ApiClient.SetAuthToken(TranslatorCore.Config.api_token);
                TranslatorCore.LogInfo($"[UIManager] Restored API token for user: {Sanitize.UserName(TranslatorCore.Config.api_user ?? "unknown")}");
            }

            if (!TranslatorCore.Config.first_run_completed)
            {
                // Show wizard on first run
                ShowWizard();
            }
            else
            {
                // Normal startup - trigger background tasks
                TriggerStartupTasks();
            }
        }

        /// <summary>
        /// Everything the mod goes and does on the network once it is allowed to: mod update
        /// check, edit session left open by a previous run, sync watch and notifications.
        ///
        /// Two callers, and they are the two sides of the same latch — InitializeUIState when the
        /// setup was already done, and WizardPanel.FinishWizard the moment it has just been done.
        /// Without the second one, somebody who turns online mode on in the wizard gets no sync
        /// and no update notice until the next time they launch the game, for no reason they
        /// could ever guess.
        /// </summary>
        internal static async void TriggerStartupTasks()
        {
            try
            {
                // Wait a bit to let the game initialize
                await Task.Delay(3000);

                // Check for mod updates first (non-blocking, independent of auth)
                if (TranslatorCore.Config.online_mode && TranslatorCore.Config.sync.check_mod_updates)
                {
                    CheckForModUpdates();
                }

                // Pick up an edit session the last run left open (crash, kill,
                // power cut — none of them send the DELETE). No account needed:
                // live edit sessions are anonymous, hence no token check here.
                if (TranslatorCore.Config.online_mode)
                {
                    ResumePersistedEditSession();
                }

                // Watch the translation the way the player asked to. For someone signed in this
                // is the SSE state event (check-uuid + check in one payload); for someone with no
                // account, StartSyncWatch falls through to the public check.
                //
                // ⚠ NOT gated on api_token. It was, and that token test duplicated a decision
                // StartSyncWatch already makes — badly: the whole point of its CanWatchSync branch
                // is to serve the person who has NO account, and the guard shut that branch out
                // before it could run. Someone who installed a community translation without
                // signing in was therefore never told it had been updated, for the entire life of
                // the install; the public watch only ever started if they happened to toggle
                // online mode in the options mid-session. See
                // analyse/false-branch-role-after-download.md.
                if (TranslatorCore.Config.online_mode)
                {
                    StartSyncWatch();
                }

                // This one IS an account's business — it re-checks the token in its own loop.
                if (TranslatorCore.Config.online_mode && !string.IsNullOrEmpty(TranslatorCore.Config.api_token))
                {
                    StartNotificationsPolling();
                }
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[TriggerStartupTasks] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
            }
        }

        #region Website notifications relay

        /// <summary>Latest unread website notifications, shown by the StatusOverlay.</summary>
        public static ModNotificationsResult WebsiteNotifications { get; private set; }

        /// <summary>Set when the user dismisses the notifications box (until new items arrive).</summary>
        public static bool WebsiteNotificationsDismissed { get; set; }

        private static bool _notificationsPollingStarted;

        /// <summary>
        /// Light poll of the website's in-app notifications (contributions to
        /// review, announcements): once at startup, then every 30 minutes.
        /// </summary>
        private static async void StartNotificationsPolling()
        {
            if (_notificationsPollingStarted) return;
            _notificationsPollingStarted = true;

            try
            {
                while (TranslatorCore.Config.online_mode && !string.IsNullOrEmpty(TranslatorCore.Config.api_token))
                {
                    var result = await ApiClient.GetNotificationsAsync();
                    if (result.Success)
                    {
                        bool hasNew = result.Unread > 0 &&
                            (WebsiteNotifications == null || result.Unread != WebsiteNotifications.Unread);
                        WebsiteNotifications = result;
                        if (hasNew)
                        {
                            WebsiteNotificationsDismissed = false;
                        }
                        RunOnMainThread(() => StatusOverlay?.RefreshNotificationsBox());
                    }

                    await Task.Delay(TimeSpan.FromMinutes(30));
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Notifications] Polling stopped: {e.Message}");
            }
            finally
            {
                _notificationsPollingStarted = false;
            }
        }

        /// <summary>
        /// Mark all website notifications as read (called from the overlay's dismiss).
        /// </summary>
        public static async void MarkWebsiteNotificationsRead()
        {
            WebsiteNotificationsDismissed = true;
            WebsiteNotifications = null;
            await ApiClient.MarkNotificationsReadAsync();
        }

        #endregion

        #region Sync Watch (polled check, or permanent stream when asked for)

        // Polled update check. 0 means "no timer running": either the player chose
        // a frequency that never repeats, or nothing is being watched at all.
        private static float _nextSyncCheckTime;
        private static float _syncCheckIntervalSeconds;
        private static bool _syncCheckInFlight;

        // Auto resolves against the role, which only the site can tell us. Set
        // once per watch so a later state event does not restart the stream.
        private static bool _autoRegimeApplied;

        // The periodic tick serves two different calls: the authenticated sync
        // state, or the public check for someone with no account
        private static bool _publicWatchActive;

        // The validator the public check handed back, and the translation it describes. Kept as
        // a pair: an ETag is only an answer about the file it came from.
        private static string _publicCheckETag;
        private static int? _publicCheckETagSiteId;

        /// <summary>
        /// Ask the site about the translation, the way the player chose to.
        ///
        /// Only "realtime" opens a stream. Every other frequency does a plain HTTP
        /// call — at startup and then on a timer — because a permanent connection
        /// costs one for the whole session, and knowing within two seconds that a
        /// new version exists is worth that price to almost nobody. The one case
        /// where it is: editing on the website while the game runs.
        ///
        /// Called at startup, after a successful login, and whenever the setting
        /// changes (Apply in the options).
        /// </summary>
        public static void StartSyncWatch()
        {
            StopSyncStream();
            _nextSyncCheckTime = 0f;
            _autoRegimeApplied = false;
            _publicWatchActive = false;

            string frequency = UpdateCheckFrequency.Normalize(
                TranslatorCore.Config.sync.update_check_frequency);

            if (frequency == UpdateCheckFrequency.Never)
            {
                TranslatorCore.LogInfo("[Sync] Update checks disabled by the player");
                return;
            }

            // No account, but a translation downloaded from the site: the public
            // check is the only signal they can receive, and until now they got
            // none at all. Periodic too, at the same rhythm — the endpoint is
            // ETag'd, so an unchanged translation costs a 304.
            if (!CanWatchSync(logReason: false))
            {
                StartPublicWatch(frequency);
                return;
            }

            // Auto cannot decide yet: the role comes from the site. Ask once, and
            // ApplyAutoRegime settles the rhythm when the answer arrives.
            if (frequency == UpdateCheckFrequency.Auto)
            {
                TranslatorCore.LogInfo("[Sync] Automatic rhythm — asking the site who we are");
                CheckSyncStateNow();
                return;
            }

            if (frequency == UpdateCheckFrequency.Realtime)
            {
                StartSyncStream();
                return;
            }

            // Look once now — before play starts is when an update can be applied
            // without interrupting anything — then let the timer take over.
            CheckSyncStateNow();

            float interval = UpdateCheckFrequency.IntervalSeconds(frequency);
            _syncCheckIntervalSeconds = interval;
            _nextSyncCheckTime = interval > 0f ? Time.realtimeSinceStartup + interval : 0f;

            TranslatorCore.LogInfo(interval > 0f
                ? $"[Sync] Checking for updates every {interval / 60f:0} min"
                : "[Sync] Checking for updates at startup only");
        }

        /// <summary>
        /// Watch for updates WITHOUT an account, through the public endpoint.
        ///
        /// Needs only the site id kept at download time. Silent when there is
        /// none: a translation typed locally has no upstream to watch.
        /// </summary>
        private static void StartPublicWatch(string frequency)
        {
            if (!TranslatorCore.Config.online_mode) return;
            if (!TranslatorCore.SourceSiteId.HasValue) return;

            // Automatic means hourly here: with no account there is nothing of
            // one's own to keep in sync, only a new version to hear about.
            float interval = frequency == UpdateCheckFrequency.Auto
                ? UpdateCheckFrequency.IntervalSeconds(UpdateCheckFrequency.Hourly)
                : UpdateCheckFrequency.IntervalSeconds(frequency);

            CheckPublicUpdateNow();

            _publicWatchActive = true;
            _syncCheckIntervalSeconds = interval;
            _nextSyncCheckTime = interval > 0f ? Time.realtimeSinceStartup + interval : 0f;

            TranslatorCore.LogInfo(interval > 0f
                ? $"[Sync] No account — checking the published translation every {interval / 60f:0} min"
                : "[Sync] No account — checking the published translation at startup only");
        }

        /// <summary>
        /// One public check. Builds the minimal server state an anonymous user
        /// can have: which translation, its hash, and nothing about ownership.
        /// </summary>
        private static async void CheckPublicUpdateNow()
        {
            if (_syncCheckInFlight) return;
            if (!TranslatorCore.SourceSiteId.HasValue) return;

            _syncCheckInFlight = true;
            int siteId = TranslatorCore.SourceSiteId.Value;

            try
            {
                string localHash = TranslatorCore.ComputeContentHash();

                // Paired with the translation it came from: a validator kept across a fork or a
                // fresh download would have the server answer "unchanged" about another file.
                string knownETag = _publicCheckETagSiteId == siteId ? _publicCheckETag : null;

                var result = await ApiClient.CheckPublicUpdate(siteId, localHash, knownETag);

                var success = result.Success;
                var notModified = result.NotModified;
                var hasUpdate = result.HasUpdate;
                var fileHash = result.FileHash;
                var lineCount = result.LineCount;
                var voteCount = result.VoteCount;
                var uploader = result.Uploader;
                var etag = result.ETag;

                RunOnMainThread(() =>
                {
                    _syncCheckInFlight = false;
                    if (!success) return;

                    _publicCheckETag = etag;
                    _publicCheckETagSiteId = siteId;

                    var state = TranslatorCore.ServerState ?? new ServerTranslationState();
                    state.Checked = true;
                    state.Exists = true;
                    state.IsOwner = false;
                    state.Role = TranslationRole.None;
                    state.SiteId = siteId;

                    // Nothing came back but the confirmation that nothing moved. Writing the
                    // empty fields through would blank the hash, the vote count and the name we
                    // already hold — the exact opposite of what asking cheaply was for. No
                    // refresh either: a 304 can only follow a 200, so the screen is already right.
                    if (notModified)
                    {
                        TranslatorCore.ServerState = state;
                        return;
                    }

                    if (!string.IsNullOrEmpty(fileHash)) state.Hash = fileHash;

                    // Whose work this is. Without an account this endpoint is the ONLY place that
                    // name can come from — a download leaves behind the site id and nothing else —
                    // and the panel falls back to "Website" when it is missing.
                    if (!string.IsNullOrEmpty(uploader)) state.Uploader = uploader;

                    // With no account there is nothing to vote WITH, but the count is public and
                    // worth seeing: it is what tells someone the translation they installed was
                    // appreciated, and it is the only reason they would consider signing in.
                    state.Vote = new VoteState
                    {
                        TargetId = siteId,
                        Count = voteCount,
                        UserVote = null,
                        CanVote = false,
                    };

                    TranslatorCore.ServerState = state;

                    if (hasUpdate)
                    {
                        TranslatorCore.LogInfo("[Sync] The published translation has a newer version");
                        DetermineAndApplyUpdateDirection(fileHash, lineCount, voteCount);
                    }

                    MainPanel?.RefreshUI();
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    _syncCheckInFlight = false;
                    TranslatorCore.LogWarning($"[Sync] Public update check failed: {errorMsg}");
                });
            }
        }

        /// <summary>
        /// Settle the automatic rhythm now that the site has told us the role.
        ///
        /// Owning a translation means having work in flight — corrections made on
        /// the website or on another machine must come back on their own, and an
        /// open connection is both the fastest and, measured, the cheapest way to
        /// do that. Everyone else is told at a calm pace.
        ///
        /// Runs once per watch: a later state event must not tear down the very
        /// stream it arrived on.
        /// </summary>
        private static void ApplyAutoRegime()
        {
            if (_autoRegimeApplied) return;
            if (UpdateCheckFrequency.Normalize(TranslatorCore.Config.sync.update_check_frequency)
                != UpdateCheckFrequency.Auto) return;

            var role = TranslatorCore.ServerState?.Role ?? TranslationRole.None;
            string resolved = UpdateCheckFrequency.ResolveAuto(role);
            _autoRegimeApplied = true;

            if (resolved == UpdateCheckFrequency.Realtime)
            {
                TranslatorCore.LogInfo($"[Sync] Automatic: {role} owns a translation — staying connected");
                StartSyncStream();
                return;
            }

            float interval = UpdateCheckFrequency.IntervalSeconds(resolved);
            _syncCheckIntervalSeconds = interval;
            _nextSyncCheckTime = interval > 0f ? Time.realtimeSinceStartup + interval : 0f;
            TranslatorCore.LogInfo($"[Sync] Automatic: no translation of our own — checking every {interval / 60f:0} min");
        }

        /// <summary>
        /// True when the Main this branch derives from has moved since the last
        /// merge from it.
        ///
        /// Compares against LastMergedMainHash, never against the local content:
        /// a branch differs from its Main permanently — that is what being a branch
        /// means — so comparing content would notify forever.
        /// Unknown upstream (older site, or Main never merged) answers false: the
        /// mod stays quiet rather than crying wolf on its first launch.
        /// </summary>
        public static bool HasMainUpdate()
        {
            var state = TranslatorCore.ServerState;
            if (state == null || state.Role != TranslationRole.Branch) return false;
            if (string.IsNullOrEmpty(state.MainHash)) return false;

            // Never merged from upstream: nothing to compare, so nothing to claim.
            // The offer to merge is still reachable from the panel at any time.
            if (string.IsNullOrEmpty(TranslatorCore.LastMergedMainHash)) return false;

            return state.MainHash != TranslatorCore.LastMergedMainHash;
        }

        /// <summary>
        /// Pull the Main into this branch: download it, merge it in, and ALWAYS
        /// let the player look before anything is written.
        ///
        /// Two rules, both deliberate (analyse/main-to-branch-sync.md):
        /// - the ancestor is the UPSTREAM one, never AncestorCache — feeding this
        ///   merge the branch's own ancestor would read every key the branch owns
        ///   as a deletion made by the Main;
        /// - it is proposed, never auto-applied, even with zero conflicts: content
        ///   coming from someone else does not enter a translation unattended.
        /// </summary>
        public static async Task MergeFromMain()
        {
            var state = TranslatorCore.ServerState;
            if (state?.MainSiteId == null)
            {
                TranslatorCore.LogWarning("[MainMerge] No upstream Main to merge from");
                return;
            }

            int mainId = state.MainSiteId.Value;
            string expectedHash = state.MainHash;

            var result = await ApiClient.Download(mainId);

            var success = result.Success;
            var content = result.Content;
            var fileHash = result.FileHash;
            var error = result.Error;

            RunOnMainThread(() =>
            {
                if (!success || string.IsNullOrEmpty(content))
                {
                    TranslatorCore.LogWarning($"[MainMerge] Could not download the Main: {error}");
                    StatusOverlay?.ShowToast($"Could not fetch the Main: {error}",
                        Panels.StatusOverlay.ToastTone.Off);
                    return;
                }

                var mainContent = TranslatorCore.ParseTranslationsFromJson(content);
                var upstreamAncestor = TranslatorCore.LoadMainAncestor();

                var mergeResult = TranslationMerger.MergeWithTags(
                    TranslatorCore.TranslationCache, mainContent, upstreamAncestor);

                TranslatorCore.LogInfo($"[MainMerge] {mergeResult.Statistics.GetSummary()} " +
                    $"(upstream ancestor: {upstreamAncestor.Count} entries)");

                // Shown whatever the conflict count: the summary IS the decision point
                MergePanel?.SetActive(true);
                MergePanel?.SetMergeDataWithTags(mergeResult, mainContent, fileHash ?? expectedHash);
                MergePanel?.SetUpstreamMerge(mainContent, fileHash ?? expectedHash);
                // The Main's baseline is its own (.mainancestor), never this
                // branch's: mixing them is exactly what analyse/main-to-branch-sync.md
                // §2 warns against
                MergePanel?.SetSettingsContext(
                    TranslationSettings.FromCurrentState(),
                    TranslationSettings.FromJsonText(content),
                    TranslatorCore.LoadMainAncestorSettings(),
                    "the Main translation");
            });
        }

        /// <summary>
        /// Fetch the sync state once if it has never arrived. Called when a panel
        /// needs it: "Never" must mean "do not interrupt me", not "leave the mod
        /// ignorant of its own translation". Cheap and idempotent — it does
        /// nothing once the state is known, and never runs twice at a time.
        /// </summary>
        public static void EnsureServerStateKnown()
        {
            if (TranslatorCore.ServerState != null && TranslatorCore.ServerState.Checked) return;
            if (_syncCheckInFlight) return;
            // Silent: this runs on every panel refresh, and a reason logged that
            // often is noise, not information
            if (!CanWatchSync(logReason: false)) return;

            CheckSyncStateNow();
        }

        /// <summary>Preconditions shared by both the stream and the polled check.</summary>
        private static bool CanWatchSync(bool logReason = true)
        {
            if (!TranslatorCore.Config.online_mode)
            {
                if (logReason) TranslatorCore.LogInfo("[Sync] Online mode disabled, not watching for updates");
                return false;
            }

            if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                if (logReason) TranslatorCore.LogInfo("[Sync] Not authenticated, not watching for updates");
                return false;
            }

            // Having a token in the config is not having presented it: the header is
            // installed during UI init, and a panel refreshing before that would send
            // an anonymous call to an authenticated endpoint. The 401 that comes back
            // is indistinguishable from a revoked token, and signs the player out.
            if (!ApiClient.HasAuthToken)
            {
                if (logReason) TranslatorCore.LogInfo("[Sync] Auth token not installed yet, not watching for updates");
                return false;
            }

            if (string.IsNullOrEmpty(TranslatorCore.FileUuid))
            {
                if (logReason) TranslatorCore.LogInfo("[Sync] No FileUuid, not watching for updates");
                return false;
            }

            return true;
        }

        /// <summary>
        /// Poll the sync state once. The payload is identical to the SSE 'state'
        /// event, so it goes through the same handler: the two paths cannot drift.
        /// </summary>
        private static async void CheckSyncStateNow()
        {
            if (_syncCheckInFlight) return;
            _syncCheckInFlight = true;

            try
            {
                string uuid = TranslatorCore.FileUuid;
                string localHash = TranslatorCore.ComputeContentHash();

                string json = await ApiClient.FetchSyncState(uuid, localHash);

                RunOnMainThread(() =>
                {
                    _syncCheckInFlight = false;
                    if (string.IsNullOrEmpty(json)) return;
                    HandleSyncStateEvent(json);
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    _syncCheckInFlight = false;
                    TranslatorCore.LogWarning($"[Sync] Update check failed: {errorMsg}");
                });
            }
        }

        /// <summary>
        /// Per-frame housekeeping for the polled update check (called from
        /// DrainMainThreadQueue, next to the edit-session tick).
        /// </summary>
        private static void TickSyncCheck()
        {
            if (_nextSyncCheckTime <= 0f || _syncCheckInFlight) return;

            float now = Time.realtimeSinceStartup;
            if (now < _nextSyncCheckTime) return;

            _nextSyncCheckTime = now + _syncCheckIntervalSeconds;

            if (_publicWatchActive) CheckPublicUpdateNow();
            else CheckSyncStateNow();
        }

        /// <summary>
        /// Open the permanent stream. Reserved to the "realtime" frequency —
        /// go through StartSyncWatch, which honours the player's choice.
        /// </summary>
        private static void StartSyncStream()
        {
            if (!CanWatchSync()) return;

            string uuid = TranslatorCore.FileUuid;

            StopSyncStream();

            string localHash = TranslatorCore.ComputeContentHash();
            string url = ApiClient.GetSyncSseUrl(uuid, localHash);

            var headers = new Dictionary<string, string>
            {
                { "Authorization", $"Bearer {TranslatorCore.Config.api_token}" }
            };

            _syncSseClient = new SseClient(ApiClient.GetSseHttpClient());

            _syncSseClient.OnEvent += (evt) =>
            {
                // Capture values before RunOnMainThread (IL2CPP safety)
                var eventType = evt.EventType;
                var data = evt.Data;

                RunOnMainThread(() =>
                {
                    switch (eventType)
                    {
                        case "state":
                            HandleSyncStateEvent(data);
                            break;
                        case "translation_updated":
                            HandleTranslationUpdatedEvent(data);
                            break;
                    }
                });
            };

            _syncSseClient.OnStateChanged += (state) =>
            {
                RunOnMainThread(() =>
                {
                    SyncConnectionState = state;
                    StatusOverlay?.RefreshOverlay();
                });
            };

            _syncSseClient.OnError += (error) =>
            {
                var errorMsg = error;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[SyncSSE] Permanent error: {errorMsg}");
                    SyncConnectionState = SseConnectionState.Disconnected;
                    // Set server state as checked (even on error) so UI stops showing "checking..."
                    if (TranslatorCore.ServerState == null || !TranslatorCore.ServerState.Checked)
                    {
                        TranslatorCore.ServerState = new ServerTranslationState { Checked = true };
                    }
                    StatusOverlay?.RefreshOverlay();
                    MainPanel?.RefreshUI();
                });
            };

            _syncSseClient.Connect(url, headers);
            TranslatorCore.LogInfo($"[SyncSSE] Connecting for UUID: {uuid}, hash: {localHash?.Substring(0, 16)}...");
        }

        /// <summary>
        /// Stop watching entirely: the stream AND the polled check. Use this on
        /// logout, offline mode or shutdown — stopping only the stream would leave
        /// the timer firing calls nobody asked for any more.
        /// </summary>
        public static void StopSyncWatch()
        {
            StopSyncStream();
            _nextSyncCheckTime = 0f;
            _publicWatchActive = false;
        }

        /// <summary>
        /// Stop the SSE sync stream only. Internal: callers outside this region
        /// want StopSyncWatch.
        /// </summary>
        private static void StopSyncStream()
        {
            if (_syncSseClient != null)
            {
                _syncSseClient.Disconnect();
                _syncSseClient.Dispose();
                _syncSseClient = null;
            }
            SyncConnectionState = SseConnectionState.Disconnected;
        }

        /// <summary>
        /// Handle the SSE 'state' event — combines check-uuid + check in one payload.
        /// Sent immediately on connect and on reconnect (with Last-Event-ID).
        /// </summary>
        private static void HandleSyncStateEvent(string jsonData)
        {
            try
            {
                var data = ApiClient.ParseJsonSafe(jsonData);

                bool exists = data["exists"]?.Value<bool>() ?? false;
                string roleStr = data["role"]?.Value<string>() ?? "none";
                int branchesCount = data["branches_count"]?.Value<int>() ?? 0;

                TranslationRole role;
                switch (roleStr)
                {
                    case "main": role = TranslationRole.Main; break;
                    case "branch": role = TranslationRole.Branch; break;
                    default: role = TranslationRole.None; break;
                }

                var translation = data["translation"];
                var main = data["main"];

                // Build ServerState (replaces FetchServerState logic)
                var serverState = new ServerTranslationState
                {
                    Checked = true,
                    Exists = exists,
                    IsOwner = role == TranslationRole.Main || role == TranslationRole.Branch,
                    Role = role,
                    BranchesCount = branchesCount,
                    // Absent from an older site: stays null, which reads as "unknown" and never
                    // as "the Main is fine".
                    MainMissing = data["main_missing"]?.ToObject<bool?>(),
                    MainIgnoring = data["main_ignoring"]?.ToObject<bool?>(),
                    MergedLinesTotal = data["merged_lines_total"]?.ToObject<int?>() ?? 0,
                };

                if (translation != null && translation.Type != JTokenType.Null)
                {
                    serverState.SiteId = translation["id"]?.Value<int>();
                    serverState.Uploader = TranslatorCore.Config.api_user;
                    serverState.Hash = translation["file_hash"]?.Value<string>();
                    serverState.Type = translation["type"]?.Value<string>();
                    serverState.Notes = translation["notes"]?.Value<string>();
                    serverState.ResourcesUrl = translation["resources_url"]?.Value<string>();

                    // A branch now also hears about the Main it derives from. Absent
                    // from an older site: stays null, which reads as "unknown" and
                    // never as "the Main is gone".
                    if (role == TranslationRole.Branch && main != null && main.Type != JTokenType.Null)
                    {
                        serverState.MainSiteId = main["id"]?.Value<int>();
                        serverState.MainHash = main["file_hash"]?.Value<string>();
                        serverState.MainLineCount = main["line_count"]?.Value<int>() ?? 0;
                        serverState.MainUsername = main["uploader"]?.Value<string>();
                    }

                    serverState.BranchesPendingReview = data["branches_pending_review"]?.Value<int>() ?? 0;
                }
                else if (main != null && main.Type != JTokenType.Null)
                {
                    serverState.SiteId = main["id"]?.Value<int>();
                    serverState.Uploader = main["uploader"]?.Value<string>();
                    serverState.MainUsername = main["uploader"]?.Value<string>();
                    serverState.Hash = main["file_hash"]?.Value<string>();
                    serverState.ResourcesUrl = main["resources_url"]?.Value<string>();
                }

                // Votes on the published translation of this lineage. Left null on a server that
                // does not report it: the card then shows no vote at all, rather than "0".
                var voteToken = data["vote"];
                if (voteToken != null && voteToken.Type == JTokenType.Object)
                {
                    serverState.Vote = new VoteState
                    {
                        TargetId = voteToken["target_id"]?.Value<int>() ?? 0,
                        Count = voteToken["count"]?.Value<int>() ?? 0,
                        UserVote = voteToken["user_vote"]?.Value<int?>(),
                        CanVote = voteToken["can_vote"]?.Value<bool>() ?? false,
                    };
                }

                TranslatorCore.ServerState = serverState;

                TranslatorCore.LogDebug($"[SyncSSE] State: exists={exists}, role={role}, siteId={serverState.SiteId}");

                // The role is only knowable here, so this is where "automatic"
                // stops being a promise and becomes a rhythm
                ApplyAutoRegime();

                // Client-side update detection (URL hash may be stale after reconnection)
                string serverHash = serverState.Hash;
                string localHash = TranslatorCore.ComputeContentHash();
                bool hasUpdate = !string.IsNullOrEmpty(serverHash) && serverHash != localHash;

                // A state can arrive even under "Never": a panel asked for it so it
                // could show the role and the site id. Filling that in is fine;
                // raising an update notice is exactly what was declined.
                bool wantsUpdateNotices = UpdateCheckFrequency.Normalize(
                    TranslatorCore.Config.sync.update_check_frequency) != UpdateCheckFrequency.Never;

                if (hasUpdate && wantsUpdateNotices)
                {
                    int lineCount = translation?["line_count"]?.Value<int>()
                                    ?? main?["line_count"]?.Value<int>()
                                    ?? 0;
                    int voteCount = translation?["vote_count"]?.Value<int>() ?? 0;

                    TranslatorCore.LogInfo($"[SyncSSE] Update detected: serverHash={serverHash?.Substring(0, 16)}..., localHash={localHash?.Substring(0, 16)}...");
                    DetermineAndApplyUpdateDirection(serverHash, lineCount, voteCount);
                }
                else
                {
                    HasPendingUpdate = false;
                    PendingUpdateInfo = null;
                    PendingUpdateDirection = UpdateDirection.None;
                }

                MainPanel?.RefreshUI();
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[SyncSSE] Error handling state event: {e.Message}");
                TranslatorCore.ServerState = new ServerTranslationState { Checked = true };
                MainPanel?.RefreshUI();
            }
        }

        /// <summary>
        /// Handle the SSE 'translation_updated' event — real-time notification when
        /// the server translation is modified (upload from another device, merge, etc.).
        /// </summary>
        private static void HandleTranslationUpdatedEvent(string jsonData)
        {
            try
            {
                var data = ApiClient.ParseJsonSafe(jsonData);

                string serverHash = data["file_hash"]?.Value<string>();
                int lineCount = data["line_count"]?.Value<int>() ?? 0;
                int voteCount = data["vote_count"]?.Value<int>() ?? 0;

                // Update server state hash
                var serverState = TranslatorCore.ServerState;
                if (serverState != null)
                {
                    serverState.Hash = serverHash;
                }

                // Client-side update detection
                string localHash = TranslatorCore.ComputeContentHash();
                bool hasUpdate = !string.IsNullOrEmpty(serverHash) && serverHash != localHash;

                TranslatorCore.LogInfo($"[SyncSSE] Translation updated: serverHash={serverHash?.Substring(0, 16)}..., hasUpdate={hasUpdate}");

                if (hasUpdate)
                {
                    DetermineAndApplyUpdateDirection(serverHash, lineCount, voteCount);
                }
                else
                {
                    // Local content matches server — we're synced
                    HasPendingUpdate = false;
                    PendingUpdateInfo = null;
                    PendingUpdateDirection = UpdateDirection.None;
                }

                MainPanel?.RefreshUI();
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[SyncSSE] Error handling translation_updated event: {e.Message}");
            }
        }

        /// <summary>
        /// Determine the sync direction (Download/Upload/Merge) and set pending update state.
        /// Shared logic used by both 'state' and 'translation_updated' event handlers.
        /// </summary>
        private static void DetermineAndApplyUpdateDirection(string serverHash, int lineCount, int voteCount)
        {
            // ⚠ MetadataDirty counts as a local change and must stay in: the languages or the notes
            // of a translation can move without a single line of text changing, and a file in that
            // state has something to publish.
            bool hasLocalChanges = TranslatorCore.LocalChangesCount > 0 || TranslatorCore.MetadataDirty;

            // The rule is shared with the manager, which reaches the same verdict from the file on
            // disk — so a game and the window outside it cannot disagree about what is waiting.
            // Reached here only once the caller has established that the content differs, hence
            // passing the two hashes as unequal is exactly what it means.
            switch (Sync.Decide(TranslatorCore.ComputeContentHash(), serverHash,
                                TranslatorCore.LastSyncedHash, hasLocalChanges))
            {
                case SyncDirection.Merge:
                    PendingUpdateDirection = UpdateDirection.Merge;
                    TranslatorCore.LogInfo($"[SyncSSE] CONFLICT: Both local ({TranslatorCore.LocalChangesCount} changes) and server changed - merge needed");
                    break;

                case SyncDirection.Upload:
                    PendingUpdateDirection = UpdateDirection.Upload;
                    TranslatorCore.LogInfo($"[SyncSSE] Local has {TranslatorCore.LocalChangesCount} changes to upload");
                    break;

                case SyncDirection.Download:
                    PendingUpdateDirection = UpdateDirection.Download;
                    TranslatorCore.LogInfo($"[SyncSSE] Server has update: {lineCount} lines");
                    break;

                default:
                    // The content turned out to match after all — the caller compared hashes a
                    // moment ago, and a save can land in between. Nothing to offer.
                    HasPendingUpdate = false;
                    PendingUpdateInfo = null;
                    PendingUpdateDirection = UpdateDirection.None;
                    return;
            }

            HasPendingUpdate = true;
            PendingUpdateInfo = new TranslationCheckResult
            {
                Success = true,
                HasUpdate = true,
                FileHash = serverHash,
                LineCount = lineCount,
                VoteCount = voteCount,
            };

            // Auto-download only if no local changes and no conflict
            if (PendingUpdateDirection == UpdateDirection.Download &&
                TranslatorCore.Config.sync.auto_download)
            {
                TranslatorCore.LogInfo("[SyncSSE] Auto-downloading update...");
                _ = DownloadUpdate();
            }
        }

        #endregion

        #region SSE Merge Completion

        private static SseClient _mergeSseClient;

        /// <summary>
        /// Token of the comparison in flight. A comparison that ends on the server is read back
        /// through the ordinary download; one that ends here can only be read back through it.
        /// </summary>
        private static string _mergeToken;

        /// <summary>
        /// Start listening for merge preview completion via SSE.
        /// When the user completes a merge in the browser, auto-downloads the result.
        /// Called after opening the merge preview URL in the browser.
        /// </summary>
        /// <param name="token">Merge preview token from InitMergePreview API</param>
        /// <param name="translationId">Translation ID to download after merge completes</param>
        public static void StartMergeCompletionListener(string token, int translationId)
        {
            if (string.IsNullOrEmpty(token))
            {
                TranslatorCore.LogWarning("[MergeSSE] No token, skipping merge completion listener");
                return;
            }

            StopMergeCompletionListener();
            // Kept because a comparison that ends HERE has no published version to download:
            // its result is fetched back through the token that produced it.
            _mergeToken = token;

            string url = ApiClient.GetMergeStreamUrl(token);

            _mergeSseClient = new SseClient(ApiClient.GetSseHttpClient());

            _mergeSseClient.OnEvent += (evt) =>
            {
                var eventType = evt.EventType;
                var data = evt.Data;

                RunOnMainThread(() =>
                {
                    if (eventType == "merge_completed")
                    {
                        HandleMergeCompleted(data, translationId);
                    }
                });
            };

            _mergeSseClient.OnError += (error) =>
            {
                var errorMsg = error;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[MergeSSE] Error: {errorMsg}");
                    StopMergeCompletionListener();
                });
            };

            _mergeSseClient.Connect(url);
            TranslatorCore.LogInfo($"[MergeSSE] Listening for merge completion (token: {token.Substring(0, 8)}...)");
        }

        /// <summary>
        /// The server refused our token — revoked from the website, or deleted along with a ban.
        /// Sign out locally rather than keep showing a connected account whose every sync silently
        /// fails, and say why: the reason comes from the server when it gave one. Fires from a
        /// background thread (HTTP), so the UI work is marshalled to the main thread.
        /// </summary>
        private static void HandleAuthenticationRejected(string reason)
        {
            RunOnMainThread(() =>
            {
                // Already signed out (several calls can fail back-to-back): nothing to announce.
                if (string.IsNullOrEmpty(TranslatorCore.Config.api_token)) return;

                TranslatorCore.LogWarning($"[Auth] Token refused by the server — signing out locally. {reason}");
                TranslatorCore.ClearApiSession();

                string message = string.IsNullOrEmpty(reason)
                    ? "Signed out: the server refused this account's token."
                    : "Signed out: " + reason;
                StatusOverlay?.ShowToast(message, Panels.StatusOverlay.ToastTone.Off);

                MainPanel?.RefreshUI();
                StatusOverlay?.RefreshOverlay();
                NotificationDismissed = false;
            });
        }

        /// <summary>
        /// Stop listening for merge preview completion.
        /// </summary>
        public static void StopMergeCompletionListener()
        {
            if (_mergeSseClient != null)
            {
                _mergeSseClient.Disconnect();
                _mergeSseClient.Dispose();
                _mergeSseClient = null;
            }
        }

        /// <summary>
        /// Handle the merge_completed SSE event — auto-download the merged translation.
        /// </summary>
        /// <summary>
        /// Open the comparison page in the browser for a translation.
        ///
        /// <paramref name="toLocal"/> decides what the page is FOR: publishing our version, or
        /// bringing the online one back into our own file without publishing anything. The second
        /// is the only mode that works against a translation we do not own — a branch measuring
        /// itself against its Main.
        /// </summary>
        public static async Task OpenComparison(int translationId, bool toLocal, Action onFinished = null)
        {
            var result = await ApiClient.InitMergePreview(translationId, TranslatorCore.TranslationCache, toLocal);

            // After the await we may be off the main thread (IL2CPP)
            var success = result.Success;
            var url = result.Url;
            var token = result.Token;
            var error = result.Error;

            RunOnMainThread(() =>
            {
                if (success && !string.IsNullOrEmpty(url))
                {
                    // Debug only: the URL carries a one-time login token
                    TranslatorCore.LogDebug($"[Compare] Opening {(toLocal ? "local" : "publish")} comparison");
                    TranslatorCore.OpenUrlSafe(ApiClient.GetMergePreviewFullUrl(url));

                    if (!string.IsNullOrEmpty(token))
                    {
                        StartMergeCompletionListener(token, translationId);
                    }
                }
                else
                {
                    TranslatorCore.LogWarning($"[Compare] Could not open the comparison: {error}");
                    StatusOverlay?.ShowToast("Could not open the comparison page",
                        Panels.StatusOverlay.ToastTone.Off);
                }

                onFinished?.Invoke();
            });
        }

        /// <summary>
        /// Take back the result of a comparison that was arbitrated in the browser but never
        /// published.
        ///
        /// This file IS ours — our lines, our settings, our lineage — with the decisions applied.
        /// So it replaces the local file wholesale rather than being merged into it: merging
        /// would re-introduce, as "local changes", precisely what the player just chose to drop.
        ///
        /// The content still goes through the same door as any downloaded file
        /// (ApplyDownloadedTranslationFile): valid JSON, non-empty, backed up first. It comes
        /// from our own server round-trip, but "we sent it" is not a reason to skip the checks.
        /// </summary>
        private static async Task ApplyLocalMergeResult(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                TranslatorCore.LogWarning("[MergeSSE] Comparison ended locally but its token is gone - nothing to apply");
                return;
            }

            var result = await ApiClient.GetMergePreviewResult(token);
            var success = result.Success;
            var content = result.Content;
            var error = result.Error;

            RunOnMainThread(() =>
            {
                if (!success || string.IsNullOrEmpty(content))
                {
                    TranslatorCore.LogWarning($"[MergeSSE] Could not collect the comparison result: {error}");
                    StatusOverlay?.ShowToast("Could not retrieve the comparison result", Panels.StatusOverlay.ToastTone.Off);
                    MainPanel?.RefreshUI();
                    return;
                }

                if (!ApplyDownloadedTranslationFile(content))
                {
                    StatusOverlay?.ShowToast("The comparison result could not be applied", Panels.StatusOverlay.ToastTone.Off);
                    return;
                }

                // Deliberately NOT touching ServerState.Hash or LastSyncedHash: nothing was
                // published, so the online version is exactly where it was. Claiming to be in
                // step with it would hide the update the player still has to send.
                TranslatorCore.ClearProcessingCaches();
                TranslatorCore.LogInfo("[MergeSSE] Comparison result applied locally (nothing published)");
                StatusOverlay?.ShowToast("Comparison applied to your translation", Panels.StatusOverlay.ToastTone.On);
                MainPanel?.RefreshUI();
            });
        }

        private static async void HandleMergeCompleted(string jsonData, int translationId)
        {
            try
            {
                var data = ApiClient.ParseJsonSafe(jsonData);
                string fileHash = data["file_hash"]?.Value<string>();
                int lineCount = data["line_count"]?.Value<int>() ?? 0;
                bool toLocal = data["destination"]?.Value<string>() == "local";

                TranslatorCore.LogInfo($"[MergeSSE] Merge completed! destination={(toLocal ? "local" : "server")}, lines={lineCount}");

                string mergeToken = _mergeToken;

                // Stop listening — we only need one event
                StopMergeCompletionListener();

                if (toLocal)
                {
                    // Nothing was published: the result is the player's own file, arbitrated.
                    // Downloading the online version here would throw their decisions away and
                    // hand them back exactly what they just chose against.
                    await ApplyLocalMergeResult(mergeToken);
                    return;
                }

                // Auto-download the merged translation
                var result = await ApiClient.Download(translationId);

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var content = result.Content;
                var downloadHash = result.FileHash;
                var error = result.Error;

                RunOnMainThread(() =>
                {
                    if (success && !string.IsNullOrEmpty(content))
                    {
                        ApplyDownloadedTranslationFile(content);

                        // Update sync state
                        var serverState = TranslatorCore.ServerState;
                        if (serverState != null)
                        {
                            serverState.Hash = downloadHash ?? fileHash;
                        }
                        TranslatorCore.LastSyncedHash = downloadHash ?? fileHash;

                        // Ancestor first: the file's change count is measured against it.
                        TranslatorCore.SaveAncestorCache();
                        TranslatorCore.SaveCache();

                        // Clear pending update
                        HasPendingUpdate = false;
                        PendingUpdateInfo = null;
                        PendingUpdateDirection = UpdateDirection.None;

                        TranslatorCore.LogInfo("[MergeSSE] Merge result downloaded and applied!");

                        // Clear processing caches so scanner re-evaluates text
                        TranslatorCore.ClearProcessingCaches();

                        MainPanel?.RefreshUI();
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[MergeSSE] Auto-download after merge failed: {error}");
                        MainPanel?.RefreshUI();
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogError($"[MergeSSE] Error handling merge_completed: {errorMsg}");
                });
            }
        }

        /// <summary>
        /// Backup the local translations file, overwrite it with new content
        /// and hot-reload it. Shared by merge completion and edit session
        /// saves. Must run on the main thread (ReloadCache touches Unity APIs).
        /// Returns false (and leaves the local file untouched) if the content
        /// is not valid JSON — a corrupted write would make LoadCache reset
        /// the cache and regenerate the file UUID, breaking the lineage.
        /// </summary>
        #region Settings reconciliation

        /// <summary>
        /// Decide what happens to the settings sections when content arrives.
        ///
        /// Two very different starting points, hence the flag:
        /// - a full download has ALREADY overwritten our settings (the file was
        ///   written and reloaded), so keeping ours means putting them back;
        /// - a merge has ignored the incoming settings entirely, so taking
        ///   theirs means applying them.
        ///
        /// Either way the player is only asked about sections BOTH sides moved.
        /// Before this, full downloads silently replaced everything and merges
        /// silently dropped everything — see
        /// analyse/metadata-visibility-and-sync.md §3.
        /// </summary>
        /// <param name="ours">Our settings BEFORE the incoming content was applied</param>
        /// <param name="theirs">The settings carried by the incoming content</param>
        /// <param name="ancestor">The last common state, null when unknown</param>
        /// <param name="incomingAlreadyApplied">True on the full-download paths</param>
        /// <param name="sourceLabel">Where it comes from, in the player's words</param>
        /// <param name="explicitRequest">
        /// True when the player picked THIS translation and asked for it. The
        /// 3-way rule "only I moved, so keep mine" is right for a background
        /// sync and wrong here: they just clicked Download on someone's version,
        /// through a dialog announcing a replacement. Silently handing their own
        /// settings back is the invisible behaviour this whole change removes.
        /// So on an explicit request every difference is put to them, with the
        /// downloaded side ticked.
        /// </param>
        public static void ReconcileSettings(
            TranslationSettings ours,
            TranslationSettings theirs,
            TranslationSettings ancestor,
            bool incomingAlreadyApplied,
            string sourceLabel,
            bool explicitRequest = false)
        {
            if (ours == null || theirs == null) return;

            var plan = SettingsSyncPlan.Build(ours, theirs, ancestor);

            var arbitrated = explicitRequest
                ? plan.Sections.Where(s => s.State != SettingsSectionState.Same).ToList()
                : plan.Decisions;
            var arbitratedNames = arbitrated.Select(s => s.Section).ToList();

            // What nobody needs to arbitrate is settled here and now
            var automatic = (incomingAlreadyApplied
                    ? plan.Sections
                        .Where(s => s.State == SettingsSectionState.OursChanged)
                        .Select(s => s.Section)
                    : plan.AutoAccepted.AsEnumerable())
                .Where(s => !arbitratedNames.Contains(s))
                .ToList();

            if (automatic.Count > 0)
            {
                (incomingAlreadyApplied ? ours : theirs).ApplySections(automatic);
                TranslatorCore.SaveCache();
            }

            if (arbitrated.Count == 0)
            {
                if (automatic.Count > 0) MainPanel?.RefreshUI();
                return;
            }

            if (SettingsChoicePanel == null)
            {
                // No UI to ask with: keep what the player already had rather
                // than take a silent decision on their behalf
                TranslatorCore.LogWarning("[Settings] No panel available - keeping local settings");
                if (incomingAlreadyApplied)
                {
                    ours.ApplySections(arbitratedNames);
                    TranslatorCore.SaveCache();
                }
                return;
            }

            TranslatorCore.LogInfo($"[Settings] Asking about {arbitrated.Count} section(s): "
                + string.Join(", ", arbitratedNames.ToArray()));

            // A dialog nobody can see is the same as no dialog at all
            ShowUI = true;
            // "Compare" opens the browser side-by-side view — settings included since it can now
            // show them one by one. Offered only when we know WHICH online translation to compare
            // against; without that there is nowhere to go, and a button that leads nowhere is
            // worse than no button.
            int? compareTarget = TranslatorCore.ServerState?.SiteId;
            Action onCompare = compareTarget.HasValue
                ? () => { var _ = OpenComparison(compareTarget.Value, toLocal: true); }
                : (Action)null;

            SettingsChoicePanel.Show(
                arbitrated,
                sourceLabel,
                chosen => ApplySettingsChoice(arbitratedNames, ours, theirs, incomingAlreadyApplied, chosen),
                onCompare,
                () => ApplySettingsChoice(arbitratedNames, ours, theirs, incomingAlreadyApplied, new List<string>()),
                // The backup note belongs to the paths that replaced the file — the ones that
                // took one. Restoring settings touches no file until Apply.
                fileWasBackedUp: incomingAlreadyApplied);
        }

        /// <summary>
        /// Copy the translation file aside before something replaces it. Every path that
        /// overwrites the player's file wholesale calls this — one of them did not, and left no
        /// way back after a community version had taken the place of someone's own work.
        ///
        /// Failure is swallowed on purpose: a backup that cannot be written must not stop the
        /// operation the player asked for, and it is reported rather than silently skipped.
        /// </summary>
        public static void BackupCacheFile()
        {
            try
            {
                if (System.IO.File.Exists(TranslatorCore.CachePath))
                    System.IO.File.Copy(TranslatorCore.CachePath, TranslatorCore.CachePath + ".backup", true);
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[UIManager] Could not back up the translation file: {e.Message}");
            }
        }

        /// <summary>
        /// Put the settings back to the online version's, section by section.
        ///
        /// The way back the player had no way to take: after declining a replacement at download
        /// time — or after configuring things locally — nothing could undo it, and nothing even
        /// said the settings had drifted.
        ///
        /// Goes through the same panel and the same code as a download so the gesture reads the
        /// same and the rule holds: nothing is applied until Apply is pressed.
        /// </summary>
        public static void RestoreOnlineSettings()
        {
            var reference = TranslatorCore.GetOnlineSettingsReference();
            if (reference == null || !reference.HasDifferences)
            {
                // The button is hidden in this case; reaching it means the state changed
                // underneath, and doing nothing is the right answer
                TranslatorCore.LogInfo("[Settings] Nothing to restore - settings already match the online version");
                return;
            }

            // ancestor: null and explicitRequest: true — this is not a sync, it is a request.
            // Every section that differs is put to the player, ticked on the online side, and
            // nothing has been applied to the file yet (incomingAlreadyApplied: false).
            ReconcileSettings(
                TranslationSettings.FromCurrentState(),
                reference.Settings,
                null,
                false,
                reference.Label,
                explicitRequest: true);
        }

        private static void ApplySettingsChoice(
            List<string> arbitrated,
            TranslationSettings ours,
            TranslationSettings theirs,
            bool incomingAlreadyApplied,
            List<string> chosen)
        {
            if (incomingAlreadyApplied)
            {
                // Theirs is already in place: restore ours where declined
                var declined = arbitrated.Where(s => !chosen.Contains(s)).ToList();
                ours.ApplySections(declined);
            }
            else
            {
                // Ours is in place: apply theirs where accepted
                theirs.ApplySections(chosen.Where(s => arbitrated.Contains(s)).ToList());
            }

            TranslatorCore.SaveCache();
            MainPanel?.RefreshUI();
            TranslatorCore.LogInfo($"[Settings] Player replaced {chosen.Count} of {arbitrated.Count} section(s)");
        }

        #endregion

        private static bool ApplyDownloadedTranslationFile(string content)
        {
            try
            {
                Newtonsoft.Json.Linq.JObject.Parse(content);
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[UIManager] Refusing to apply downloaded content: not valid JSON ({e.Message})");
                return false;
            }

            BackupCacheFile();

            // Snapshot our settings BEFORE the file replaces them: the reload
            // below applies the incoming ones wholesale, and this is the only
            // moment ours still exist
            var ourSettings = TranslationSettings.FromCurrentState();
            var ancestorSettings = TranslatorCore.AncestorSettings;

            System.IO.File.WriteAllText(TranslatorCore.CachePath, content);

            // Reload cache to apply new content immediately
            TranslatorCore.ReloadCache();

            ReconcileSettings(ourSettings, TranslationSettings.FromJsonText(content),
                ancestorSettings, incomingAlreadyApplied: true, sourceLabel: "the version you just validated");
            return true;
        }

        #endregion

        #region SSE Edit Session (anonymous live edit in browser)

        private static SseClient _editSessionSseClient;
        private static string _editSessionModKey;
        // Hash of the content currently in the server session — set by our own pushes
        // AND by applying a browser save. Used only to suppress a redundant push (echo).
        private static string _sessionContentHash;
        // Hash of the last browser save we already applied. Set ONLY when a save is
        // applied, NEVER by a push. The SSE server replays the latest save on every
        // reconnection; without a hash that a push can't clobber, an intervening push
        // made the dedup "forget" the applied save and every reconnection re-applied it,
        // overwriting in-game captures made since. This key makes a save idempotent.
        private static string _lastAppliedSaveHash;
        // 3-way merge baseline for browser saves: snapshot when the session starts,
        // updated to the merged result after each apply (never on push). Lets the merge
        // preserve in-game captures the browser never saw while honoring the browser's
        // edits and deletions. See analyse/edit-session-sync-bugs.md.
        private static Dictionary<string, TranslationEntry> _editSessionAncestor;

        // Mod → browser live sync: local file changes are pushed to the session
        // (debounced) so new AI translations / in-game edits show up in the editor
        private const float PushDebounceSeconds = 10f;
        private static volatile bool _pendingLocalPush;   // set by SaveCache (any thread)
        private static bool _pushInFlight;                // main thread only
        private static float _nextPushAllowedTime;        // main thread only

        // Browser presence: the page signals departure (pagehide beacon) and
        // return; the mod ends the session after a grace period without the
        // browser (grace absorbs page refreshes and navigations).
        // ONLY the explicit beacon counts as "left": background tabs get
        // frozen/discarded by the browser (their heartbeat stops) and that
        // must never end a session — the player can stay in-game for hours
        // before coming back to save.
        private const float BrowserGraceSeconds = 90f;
        private static float _browserLeftSince = -1f;     // main thread only, -1 = present

        // Keepalive: extends the server-side TTL for the whole play session,
        // so the session only ever ends on browser close or game shutdown
        private const float KeepaliveIntervalSeconds = 600f;
        private static float _nextKeepaliveTime;           // main thread only
        private static bool _keepaliveInFlight;            // main thread only

        /// <summary>True while a live edit session is listening for browser saves.</summary>
        public static bool IsEditSessionActive => _editSessionSseClient != null;

        /// <summary>
        /// Called by TranslatorCore.SaveCache whenever the local file is written.
        /// Cheap and thread-safe: just flags the change, TickEditSession pushes.
        /// </summary>
        public static void NotifyLocalFileChanged()
        {
            if (_editSessionSseClient == null) return;
            _pendingLocalPush = true;
        }

        /// <summary>
        /// Per-frame housekeeping for the live edit session (called from
        /// DrainMainThreadQueue): debounced local-file pushes and the
        /// browser-absence grace timer.
        /// </summary>
        private static void TickEditSession()
        {
            if (_editSessionSseClient == null) return;

            float now = Time.realtimeSinceStartup;

            if (_browserLeftSince >= 0f && now - _browserLeftSince > BrowserGraceSeconds)
            {
                TranslatorCore.LogInfo("[EditSSE] Browser page gone past grace period, ending session");
                EndEditSessionFromMod("Browser page closed — session ended.");
                return;
            }

            if (_pendingLocalPush && !_pushInFlight && now >= _nextPushAllowedTime)
            {
                _pendingLocalPush = false;
                _pushInFlight = true;
                _nextPushAllowedTime = now + PushDebounceSeconds;
                PushLocalFileToEditSession();
            }

            if (now >= _nextKeepaliveTime && !_keepaliveInFlight)
            {
                _keepaliveInFlight = true;
                _nextKeepaliveTime = now + KeepaliveIntervalSeconds;
                SendEditSessionKeepalive();
            }
        }

        /// <summary>
        /// Periodic server-side TTL extension: the session must survive for
        /// as long as the game runs, whatever the player's editing rhythm.
        /// </summary>
        private static async void SendEditSessionKeepalive()
        {
            try
            {
                string modKey = _editSessionModKey;
                if (string.IsNullOrEmpty(modKey))
                {
                    _keepaliveInFlight = false;
                    return;
                }

                bool alive = await ApiClient.KeepAliveEditSession(modKey);

                RunOnMainThread(() =>
                {
                    _keepaliveInFlight = false;
                    if (!alive)
                    {
                        TranslatorCore.LogInfo("[EditSSE] Session no longer exists server-side, stopping");
                        StopEditSessionListener();
                        ClearPersistedEditSession();
                        TranslationParamsPanel?.OnEditSessionEnded("Session expired — stopped.");
                    }
                });
            }
            catch
            {
                RunOnMainThread(() => { _keepaliveInFlight = false; });
            }
        }

        /// <summary>
        /// Push the local file to the session so the browser editor sees
        /// in-game changes. Skipped when the file still matches the session
        /// content (e.g. the save we just applied) — natural echo suppression.
        /// </summary>
        private static async void PushLocalFileToEditSession()
        {
            try
            {
                string modKey = _editSessionModKey;
                if (string.IsNullOrEmpty(modKey))
                {
                    _pushInFlight = false;
                    return;
                }

                string localHash = ComputeLocalFileHash();
                if (localHash != null && localHash == _sessionContentHash)
                {
                    _pushInFlight = false;
                    return;
                }

                var result = await ApiClient.UpdateEditSession(modKey);

                // Capture before RunOnMainThread (IL2CPP safety)
                var success = result.Success;
                var sessionGone = result.SessionGone;
                var contentHash = result.ContentHash;
                var browserLeft = result.BrowserLeft;
                var seenSecondsAgo = result.BrowserSeenSecondsAgo;
                var error = result.Error;

                RunOnMainThread(() =>
                {
                    _pushInFlight = false;

                    if (sessionGone)
                    {
                        TranslatorCore.LogInfo("[EditSSE] Session no longer exists server-side, stopping");
                        StopEditSessionListener();
                        ClearPersistedEditSession();
                        TranslationParamsPanel?.OnEditSessionEnded("Session expired — stopped.");
                        return;
                    }

                    if (!success)
                    {
                        // Transient failure: re-arm, the next tick retries after the debounce
                        TranslatorCore.LogWarning($"[EditSSE] Push failed: {error}");
                        _pendingLocalPush = true;
                        return;
                    }

                    // Push only advances the "session content" hash (echo suppression) —
                    // NOT the applied-save hash, so a replayed browser save stays deduped.
                    _sessionContentHash = contentHash;
                    // A successful push also extended the server TTL
                    _nextKeepaliveTime = Time.realtimeSinceStartup + KeepaliveIntervalSeconds;
                    TranslatorCore.LogDebug("[EditSSE] Local changes pushed to the browser editor");

                    // Presence: ONLY the explicit pagehide beacon counts as
                    // "left" — a stale heartbeat just means the tab is frozen
                    // in the background, which must never end the session
                    if (browserLeft)
                    {
                        if (_browserLeftSince < 0f)
                            _browserLeftSince = Time.realtimeSinceStartup;
                    }
                    else
                    {
                        _browserLeftSince = -1f;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    _pushInFlight = false;
                    TranslatorCore.LogWarning($"[EditSSE] Push error: {errorMsg}");
                });
            }
        }

        /// <summary>
        /// sha256 of the local translations file, matching the server-side
        /// hash of the session content (same bytes after an applied save).
        /// </summary>
        private static string ComputeLocalFileHash()
        {
            try
            {
                if (!System.IO.File.Exists(TranslatorCore.CachePath)) return null;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                {
                    var bytes = System.IO.File.ReadAllBytes(TranslatorCore.CachePath);
                    return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// End the session from the mod side: server cleanup (idempotent
        /// DELETE), stop listening, notify the panel.
        /// </summary>
        public static async void EndEditSessionFromMod(string reason)
        {
            string modKey = _editSessionModKey;
            StopEditSessionListener();
            ClearPersistedEditSession();

            if (!string.IsNullOrEmpty(modKey))
            {
                await ApiClient.EndEditSession(modKey);
            }

            RunOnMainThread(() =>
            {
                TranslatorCore.LogInfo($"[EditSSE] Session ended: {reason}");
                TranslationParamsPanel?.OnEditSessionEnded(reason);
            });
        }

        /// <summary>
        /// Start listening for edit session saves via SSE.
        /// Unlike the merge listener, the stream stays open across many saves:
        /// each one is downloaded and hot-reloaded so the user sees it in-game.
        /// </summary>
        /// <param name="modKey">Session credential returned by init (or restored from disk).</param>
        /// <param name="resuming">
        /// True when picking a session back up after the game restarted. The merge
        /// baseline is then UNKNOWN — the state the two sides last shared died with
        /// the process — so it must stay empty rather than be guessed. An empty
        /// baseline makes the merge purely additive: keys captured in-game are kept,
        /// the browser wins genuine conflicts, and nothing can be read as a deletion.
        /// Snapshotting the cache instead would be actively wrong: every line
        /// captured in-game and never pushed would look like a browser deletion and
        /// be dropped. The price is that a deletion the browser made while the game
        /// was away comes back — visible and undoable, unlike lost work.
        /// </param>
        public static void StartEditSessionListener(string modKey, bool resuming = false)
        {
            if (string.IsNullOrEmpty(modKey))
            {
                TranslatorCore.LogWarning("[EditSSE] No mod key, skipping edit session listener");
                return;
            }

            // A session running under another key is being abandoned here. Stopping
            // the listener only drops OUR end: the session stays alive server-side,
            // holding one of the slots shared by every user, and the browser tab
            // still attached to it keeps it that way for as long as it is open.
            // Nothing else would ever close it, so close it now.
            string abandonedKey = _editSessionModKey;
            if (!string.IsNullOrEmpty(abandonedKey) && abandonedKey != modKey)
            {
                CloseAbandonedEditSession(abandonedKey);
            }

            StopEditSessionListener();
            _editSessionModKey = modKey;
            _sessionContentHash = null;
            _lastAppliedSaveHash = null;
            // Baseline for the browser-save merge: the file we just handed to the
            // browser. Any key captured in-game after this is "local only" (kept);
            // any key the browser removes relative to this is an honored deletion.
            _editSessionAncestor = resuming ? null : SnapshotTranslationCache();
            _nextKeepaliveTime = Time.realtimeSinceStartup + KeepaliveIntervalSeconds;
            PersistEditSession(modKey);

            string url = ApiClient.GetEditSessionStreamUrl(modKey);

            _editSessionSseClient = new SseClient(ApiClient.GetSseHttpClient());

            _editSessionSseClient.OnEvent += (evt) =>
            {
                var eventType = evt.EventType;
                var data = evt.Data;

                RunOnMainThread(() =>
                {
                    if (eventType == "edit_saved")
                    {
                        HandleEditSessionSaved(data);
                    }
                    else if (eventType == "edit_session_ended")
                    {
                        TranslatorCore.LogInfo("[EditSSE] Edit session ended from the browser");
                        StopEditSessionListener();
                        ClearPersistedEditSession();
                        TranslationParamsPanel?.OnEditSessionEnded("Session ended from the browser.");
                    }
                    else if (eventType == "browser_left")
                    {
                        // pagehide fired (close, refresh or navigation) — start
                        // the grace timer; a rejoin cancels it
                        if (_browserLeftSince < 0f)
                        {
                            _browserLeftSince = Time.realtimeSinceStartup;
                            TranslatorCore.LogDebug("[EditSSE] Browser page left, grace period started");
                        }
                    }
                    else if (eventType == "browser_joined")
                    {
                        _browserLeftSince = -1f;
                        TranslatorCore.LogDebug("[EditSSE] Browser page (re)joined");
                    }
                    else if (eventType == "edit_retranslate")
                    {
                        HandleEditSessionRetranslate(data);
                    }
                });
            };

            _editSessionSseClient.OnError += (error) =>
            {
                var errorMsg = error;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[EditSSE] Error: {errorMsg}");
                    StopEditSessionListener();
                });
            };

            _editSessionSseClient.Connect(url);
            TranslatorCore.LogInfo($"[EditSSE] Listening for edit session saves (key: {modKey.Substring(0, 8)}...)");
        }

        /// <summary>
        /// Release a session the mod no longer follows. Fire-and-forget by
        /// design — the new session must not wait on the old one — but never
        /// unobserved: a failure here only means the server TTL does the job.
        /// </summary>
        private static async void CloseAbandonedEditSession(string modKey)
        {
            try
            {
                TranslatorCore.LogInfo($"[EditSSE] Releasing the replaced session (key: {modKey.Substring(0, 8)}...)");
                await ApiClient.EndEditSession(modKey);
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[EditSSE] Could not release the replaced session: {e.Message}");
            }
        }

        /// <summary>
        /// Stop listening for edit session saves.
        /// </summary>
        public static void StopEditSessionListener()
        {
            if (_editSessionSseClient != null)
            {
                _editSessionSseClient.Disconnect();
                _editSessionSseClient.Dispose();
                _editSessionSseClient = null;
            }
            _editSessionModKey = null;
            _sessionContentHash = null;
            _lastAppliedSaveHash = null;
            _editSessionAncestor = null;
            _pendingLocalPush = false;
            _pushInFlight = false;
            _keepaliveInFlight = false;
            _browserLeftSince = -1f;
            _seenRetranslateIds.Clear();
        }

        /// <summary>
        /// Best-effort synchronous session end during game shutdown (bounded
        /// wait — the process is exiting). Closing the game is one of the two
        /// legitimate ways a session ends; the server TTL covers hard crashes.
        /// </summary>
        public static void EndEditSessionOnShutdown()
        {
            string modKey = _editSessionModKey;
            StopEditSessionListener();
            ClearPersistedEditSession();
            if (string.IsNullOrEmpty(modKey)) return;

            try
            {
                ApiClient.EndEditSession(modKey).Wait(2000);
                TranslatorCore.LogInfo("[EditSSE] Session ended (game shutdown)");
            }
            catch { }
        }

        // ── Surviving a game restart ─────────────────────────────────────────
        // A crash, a kill or a power cut never sends the DELETE: the session
        // outlives the process, and the browser page is very likely still open
        // with work in it. The mod key is all that is needed to pick it back up,
        // so it is written next to the cache (same convention as ".ancestor" and
        // ".backup") and read once at startup.
        //
        // The file is removed only when the session is really over — never on a
        // mere SSE drop, which would throw away a session the server still holds.

        private static string EditSessionStatePath => TranslatorCore.CachePath + ".editsession";

        /// <summary>
        /// Remember the session credential for the next launch. Encrypted at
        /// rest like the mod's other secrets: `mod_key` is the sole credential
        /// for the session content, and this file sits in a game folder users
        /// routinely hand around (support archives, cloud saves).
        /// </summary>
        private static void PersistEditSession(string modKey)
        {
            try
            {
                var state = new JObject
                {
                    ["mod_key"] = TokenProtection.EncryptToken(modKey)
                };
                System.IO.File.WriteAllText(EditSessionStatePath, state.ToString());
            }
            catch (Exception e)
            {
                // Never fatal: losing this costs the resume, not the session
                TranslatorCore.LogWarning($"[EditSSE] Could not save the session for resuming: {e.Message}");
            }
        }

        /// <summary>
        /// Forget the persisted session. Called only where the session is known
        /// to be finished: ended from either side, or reported gone (404).
        /// </summary>
        private static void ClearPersistedEditSession()
        {
            try
            {
                if (System.IO.File.Exists(EditSessionStatePath))
                    System.IO.File.Delete(EditSessionStatePath);
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[EditSSE] Could not clear the persisted session: {e.Message}");
            }
        }

        /// <summary>
        /// Mod key of the session left behind by a previous run, or null.
        /// A file we cannot read or decrypt is worthless — drop it rather than
        /// keep retrying at every launch.
        /// </summary>
        private static string ReadPersistedEditSession()
        {
            try
            {
                if (!System.IO.File.Exists(EditSessionStatePath)) return null;

                var state = JObject.Parse(System.IO.File.ReadAllText(EditSessionStatePath));
                string modKey = TokenProtection.DecryptToken(state["mod_key"]?.Value<string>());

                if (string.IsNullOrEmpty(modKey))
                {
                    TranslatorCore.LogWarning("[EditSSE] Persisted session unreadable (machine changed?), discarding it");
                    ClearPersistedEditSession();
                    return null;
                }

                return modKey;
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[EditSSE] Persisted session unusable: {e.Message}");
                ClearPersistedEditSession();
                return null;
            }
        }

        /// <summary>
        /// Offer to pick up a session the previous run left open. Called at startup.
        ///
        /// ASKS rather than acts: resuming merges the browser's saves into the
        /// player's translations file, and doing that unannounced at every launch
        /// turns a helpful feature into an unexplained edit of their data. Only a
        /// cheap keepalive runs before the question — nothing is downloaded and
        /// nothing is written until the player says yes.
        /// </summary>
        public static async void ResumePersistedEditSession()
        {
            if (IsEditSessionActive || _resumeOfferDeclined) return;

            string modKey = ReadPersistedEditSession();
            if (string.IsNullOrEmpty(modKey)) return;

            // Existence check only. Downloading here would pull megabytes for a
            // session the player may well decline.
            bool alive = await ApiClient.KeepAliveEditSession(modKey);

            RunOnMainThread(() =>
            {
                if (!alive)
                {
                    TranslatorCore.LogInfo("[EditSSE] The saved session no longer exists, forgetting it");
                    ClearPersistedEditSession();
                    return;
                }

                if (ConfirmationPanel == null) return;

                // The mod's UI is hidden at startup: a dialog nobody can see is
                // the same as no dialog at all
                ShowUI = true;
                ConfirmationPanel.Show(
                    "Live edit session",
                    "A browser edit session was still open when this game last closed.\n\n"
                        + "Resuming brings back what you saved in the browser since, and merges it into your "
                        + "local translations file. Nothing is downloaded or written until you choose.\n\n"
                        + "Declining changes nothing: the session stays as it is, and you will be asked again "
                        + "next time the game starts.",
                    "Resume session",
                    () => CompleteEditSessionResume(modKey),
                    () => _resumeOfferDeclined = true,
                    isDanger: false);
            });
        }

        /// <summary>Declined this run: do not ask twice in the same session.</summary>
        private static bool _resumeOfferDeclined;

        /// <summary>
        /// Second half of the resume, once the player has agreed: fetch the
        /// session content, start listening, and merge. The download does double
        /// duty — it brings back everything saved from the browser while the game
        /// was away, which had nowhere to land at the time.
        /// </summary>
        private static async void CompleteEditSessionResume(string modKey)
        {
            var result = await ApiClient.GetEditSessionContent(modKey);

            // Capture before RunOnMainThread (IL2CPP safety)
            var success = result.Success;
            var content = result.Content;
            var sessionGone = result.SessionGone;
            var error = result.Error;

            RunOnMainThread(() =>
            {
                if (sessionGone)
                {
                    TranslatorCore.LogInfo("[EditSSE] The saved session no longer exists, forgetting it");
                    ClearPersistedEditSession();
                    return;
                }

                if (!success || string.IsNullOrEmpty(content))
                {
                    // Network trouble, not a dead session: keep the file so the
                    // next launch can offer it again rather than stranding the browser
                    TranslatorCore.LogWarning($"[EditSSE] Could not resume the session: {error}");
                    StatusOverlay?.ShowToast("Could not resume the edit session", Panels.StatusOverlay.ToastTone.Off);
                    return;
                }

                // Listener first: the merge below writes the cache, and the
                // resulting push must reach the browser
                StartEditSessionListener(modKey, resuming: true);

                // Applying the server content also sets the merge baseline for
                // everything that follows (see ApplyEditSessionMerge)
                ApplyEditSessionMerge(content);

                TranslatorCore.LogInfo("[EditSSE] Edit session resumed after game restart");
                TranslationParamsPanel?.OnEditSessionResumed();
                MainPanel?.RefreshUI();
                StatusOverlay?.ShowToast("Live edit session resumed", Panels.StatusOverlay.ToastTone.On);
            });
        }

        /// <summary>
        /// Close a session left over from a previous run, before opening a new one.
        ///
        /// Without this, every declined or failed resume would strand its session
        /// server-side until it expires, holding one of the few concurrent slots
        /// the site can offer — multiplied by every player who restarts a game.
        /// The mod is the only one able to do this: sessions are anonymous, so the
        /// site cannot tell that two of them belong to the same person.
        /// </summary>
        public static async Task DiscardPersistedEditSession()
        {
            string modKey = ReadPersistedEditSession();
            ClearPersistedEditSession();

            if (string.IsNullOrEmpty(modKey)) return;

            TranslatorCore.LogInfo("[EditSSE] Closing the session left by a previous run");
            await ApiClient.EndEditSession(modKey);
        }

        // Request ids already honored: the browser RE-EMITS its retranslate
        // request every 30s while pending (SSE delivery is not guaranteed —
        // events published during a reconnection gap are lost), always with
        // the same id. Bounded FIFO, cleared with the session.
        private static readonly Queue<string> _seenRetranslateIds = new Queue<string>();
        private const int MaxSeenRetranslateIds = 32;

        /// <summary>
        /// Handle an edit_retranslate SSE event — the browser asked to
        /// re-translate ONE entry with the player's own AI backend. Runs on
        /// the main thread (dispatched by the SSE handler). Guards:
        /// the request id must be new (browser retries reuse the id),
        /// AI must be enabled, and the key must already exist in OUR cache —
        /// the request key comes from the browser verbatim, and arbitrary
        /// text must never be fed to the player's AI (cost, prompt abuse).
        /// The result comes back through the normal AI worker → SaveCache →
        /// session push pipeline; the debounce is lifted so the browser sees
        /// it as soon as the translation lands.
        /// </summary>
        private static void HandleEditSessionRetranslate(string jsonData)
        {
            try
            {
                var data = ApiClient.ParseJsonSafe(jsonData);
                string key = data["key"]?.Value<string>();
                if (string.IsNullOrEmpty(key)) return;

                string requestId = data["id"]?.Value<string>();
                if (!string.IsNullOrEmpty(requestId))
                {
                    if (_seenRetranslateIds.Contains(requestId))
                    {
                        TranslatorCore.LogDebug("[EditSSE] Duplicate retranslate request (browser retry), ignored");
                        return;
                    }
                    _seenRetranslateIds.Enqueue(requestId);
                    while (_seenRetranslateIds.Count > MaxSeenRetranslateIds)
                        _seenRetranslateIds.Dequeue();
                }

                if (!TranslatorCore.Config.enable_ai)
                {
                    TranslatorCore.LogWarning("[EditSSE] Retranslate requested but AI is disabled, ignored");
                    return;
                }

                if (!TranslatorCore.HasTranslationKey(key))
                {
                    TranslatorCore.LogWarning("[EditSSE] Retranslate requested for a key not in the local file, ignored");
                    return;
                }

                TranslatorCore.LogInfo("[EditSSE] Browser requested AI retranslation of one entry");
                TranslatorCore.RemoveTranslationForRetranslate(key);
                // The user is waiting in the browser: push as soon as the
                // AI worker saves, without the usual debounce window
                _nextPushAllowedTime = 0f;
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[EditSSE] Error handling edit_retranslate: {e.Message}");
            }
        }

        /// <summary>
        /// Handle an edit_saved SSE event — download the session content and
        /// hot-reload it so the edit shows up in-game immediately.
        /// </summary>
        private static async void HandleEditSessionSaved(string jsonData)
        {
            try
            {
                var data = ApiClient.ParseJsonSafe(jsonData);
                string contentHash = data["content_hash"]?.Value<string>();
                int lineCount = data["line_count"]?.Value<int>() ?? 0;

                // Reconnections replay the latest save — skip if already applied.
                // Dedup on the applied-save hash (never clobbered by our pushes), so a
                // replay stays idempotent no matter how many pushes happened in between.
                if (!string.IsNullOrEmpty(contentHash) && contentHash == _lastAppliedSaveHash)
                {
                    TranslatorCore.LogDebug("[EditSSE] Save already applied (replay), skipping");
                    return;
                }

                string modKey = _editSessionModKey;
                if (string.IsNullOrEmpty(modKey)) return;

                TranslatorCore.LogInfo($"[EditSSE] Browser saved ({lineCount} lines), downloading...");

                var result = await ApiClient.GetEditSessionContent(modKey);

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var content = result.Content;
                var error = result.Error;

                RunOnMainThread(() =>
                {
                    if (success && !string.IsNullOrEmpty(content))
                    {
                        // MERGE the browser save with the live cache (not a full replace):
                        // in-game captures made during the session must survive a save that
                        // predates them, while the browser's edits/deletions are honored.
                        if (ApplyEditSessionMerge(content))
                        {
                            // Both hashes advance: the save is now applied (dedup) and it is
                            // the current session content (echo). A push of the merged result
                            // follows via SaveCache→NotifyLocalFileChanged.
                            _lastAppliedSaveHash = contentHash;
                            _sessionContentHash = contentHash;
                            // A save proves the browser is alive
                            _browserLeftSince = -1f;
                            MainPanel?.RefreshUI();
                        }
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[EditSSE] Download after save failed: {error}");
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogError($"[EditSSE] Error handling edit_saved: {errorMsg}");
                });
            }
        }

        /// <summary>
        /// Deep copy (fresh Value/Tag/Index entries) of a translation entry set, decoupled
        /// from the source so later in-place edits (SetTranslationFromEditor mutates entries)
        /// can't corrupt it. Metadata keys excluded. Used to snapshot the edit-session merge
        /// baseline.
        /// </summary>
        private static Dictionary<string, TranslationEntry> CopyEntries(IEnumerable<KeyValuePair<string, TranslationEntry>> src)
        {
            var copy = new Dictionary<string, TranslationEntry>();
            if (src == null) return copy;
            foreach (var kvp in src)
            {
                if (kvp.Key.StartsWith("_") || kvp.Value == null) continue;
                copy[kvp.Key] = new TranslationEntry
                {
                    Value = kvp.Value.Value,
                    Tag = kvp.Value.Tag,
                    Index = kvp.Value.Index
                };
            }
            return copy;
        }

        private static Dictionary<string, TranslationEntry> SnapshotTranslationCache()
            => CopyEntries(TranslatorCore.TranslationCache);

        /// <summary>
        /// Apply a browser save by MERGING it with the live cache (3-way, tag-aware),
        /// instead of overwriting the file. local = current cache (in-game captures),
        /// remote = the browser's saved content, ancestor = the session baseline.
        /// Keeps captures the browser never saw (Case 1) and honors the browser's edits
        /// and deletions (Case 5); conflicts default to the browser (editor of record).
        /// Returns false without touching anything if the content is unusable.
        /// See analyse/edit-session-sync-bugs.md.
        /// </summary>
        private static bool ApplyEditSessionMerge(string content)
        {
            // Validate JSON up front: ParseTranslationsFromJson swallows parse errors and
            // returns an EMPTY dict — merging that would read as "browser deleted every
            // key" and wipe the file. Refuse anything that isn't valid, non-empty JSON.
            try
            {
                Newtonsoft.Json.Linq.JObject.Parse(content);
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[EditSSE] Refusing to apply: not valid JSON ({e.Message})");
                return false;
            }

            var remote = TranslatorCore.ParseTranslationsFromJson(content);
            if (remote == null || (remote.Count == 0 && TranslatorCore.TranslationCache.Count > 0))
            {
                // Empty remote against a non-empty cache is almost certainly a transport
                // glitch, not a deliberate "delete everything" — refuse to avoid a wipe.
                TranslatorCore.LogWarning("[EditSSE] Refusing to apply: downloaded content has no entries");
                return false;
            }

            // Backup the current file before we rewrite it.
            BackupCacheFile();

            var mergeResult = TranslationMerger.MergeWithTags(
                TranslatorCore.TranslationCache, remote, _editSessionAncestor);

            // Conflicts are already defaulted to the remote (browser) value inside the
            // merge — the browser is the editor of record, so take the merged set as-is.
            TranslatorCore.TranslationCache.Clear();
            foreach (var kvp in mergeResult.Merged)
                TranslatorCore.TranslationCache[kvp.Key] = kvp.Value;
            // The browser side can carry capture-order indices above our counter.
            TranslatorCore.SyncOrderIndexCounter();

            // Persist the merged result, then reload so in-game text picks up the edits
            // (RestoreAllOriginals + LoadCache + ClearProcessingCaches). SaveCache also
            // schedules a push of the merged file back to the browser.
            TranslatorCore.SaveCache();
            TranslatorCore.ReloadCache();

            // New baseline = the SERVER content we just merged against, NOT the merged cache.
            // The merged cache holds captures not yet pushed to the server (push is debounced);
            // if the ancestor included them, the next browser save — whose complete server file
            // still lacks them until our push lands — would read them as deletions (Case 5) and
            // drop them. Anchoring on the remote keeps un-pushed captures "local only" (Case 1 →
            // preserved), while a genuine browser deletion still shows up as ancestor-had /
            // remote-lacks. Deep-copied so later in-place edits can't corrupt it.
            _editSessionAncestor = CopyEntries(remote);

            TranslatorCore.LogInfo($"[EditSSE] Edit merged in-game: {mergeResult.Statistics.GetSummary()}");
            return true;
        }

        #endregion

        #region Server State and Updates

        /// <summary>
        /// Check for mod updates on GitHub.
        /// </summary>
        public static async void CheckForModUpdates()
        {
            if (!TranslatorCore.Config.online_mode)
            {
                TranslatorCore.LogInfo("[ModUpdate] Skipped - online mode disabled");
                return;
            }

            if (!TranslatorCore.Config.sync.check_mod_updates)
            {
                TranslatorCore.LogInfo("[ModUpdate] Skipped - check_mod_updates disabled");
                return;
            }

            try
            {
                string currentVersion = PluginInfo.Version;
                string modLoaderType = TranslatorCore.Adapter?.ModLoaderType ?? "Unknown";

                var result = await GitHubUpdateChecker.CheckForUpdatesAsync(currentVersion, modLoaderType,
                    TranslatorCore.Config.sync.notify_prereleases);

                if (result.Success && result.HasUpdate)
                {
                    // Format published_at for comparison (ISO 8601 string)
                    string publishedAt = result.PublishedAt?.ToString("o");

                    // Only skip notification if we've already seen this EXACT release
                    // Check: same version + same current version + same published_at (handles re-releases)
                    bool alreadyNotified = TranslatorCore.Config.sync.last_seen_mod_version == result.LatestVersion &&
                                           TranslatorCore.Config.sync.last_seen_from_version == currentVersion &&
                                           TranslatorCore.Config.sync.last_seen_published_at == publishedAt;

                    if (alreadyNotified)
                    {
                        TranslatorCore.LogInfo($"[ModUpdate] Already notified about v{result.LatestVersion} from v{currentVersion}");
                        return;
                    }

                    HasModUpdate = true;
                    ModUpdateInfo = result;

                    // Log re-release detection if same version but different published_at
                    if (TranslatorCore.Config.sync.last_seen_mod_version == result.LatestVersion &&
                        TranslatorCore.Config.sync.last_seen_published_at != publishedAt)
                    {
                        TranslatorCore.LogInfo($"[ModUpdate] Re-release detected for v{result.LatestVersion} (new publish date)");
                    }
                    else
                    {
                        TranslatorCore.LogInfo($"[ModUpdate] New version available: v{result.LatestVersion} (current: v{currentVersion})");
                    }

                    // Save the seen version, current version, and published timestamp
                    TranslatorCore.Config.sync.last_seen_mod_version = result.LatestVersion;
                    TranslatorCore.Config.sync.last_seen_from_version = currentVersion;
                    TranslatorCore.Config.sync.last_seen_published_at = publishedAt;
                    TranslatorCore.SaveConfig();
                }
                else if (result.Success)
                {
                    TranslatorCore.LogInfo($"[ModUpdate] Mod is up to date (v{currentVersion})");

                    // Clear old notification tracking since we're up to date
                    if (TranslatorCore.Config.sync.last_seen_mod_version != null)
                    {
                        TranslatorCore.Config.sync.last_seen_mod_version = null;
                        TranslatorCore.Config.sync.last_seen_from_version = null;
                        TranslatorCore.Config.sync.last_seen_published_at = null;
                        TranslatorCore.SaveConfig();
                    }
                }
                else
                {
                    TranslatorCore.LogWarning($"[ModUpdate] Check failed: {result.Error}");
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[ModUpdate] Error: {e.Message}");
            }
        }

        /// <summary>
        /// Download and apply a translation update directly (no conflicts).
        /// </summary>
        public static async Task DownloadUpdate()
        {
            var serverState = TranslatorCore.ServerState;
            if (serverState?.SiteId == null) return;

            // Capture values for closure
            var siteId = serverState.SiteId.Value;

            try
            {
                var result = await ApiClient.Download(siteId);

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var content = result.Content;
                var fileHash = result.FileHash;
                var error = result.Error;

                RunOnMainThread(() =>
                {
                    if (success && !string.IsNullOrEmpty(content))
                    {
                        // Backup current file
                        BackupCacheFile();

                        // Our settings, captured before the incoming file replaces them
                        var ourSettings = TranslationSettings.FromCurrentState();
                        var ancestorSettings = TranslatorCore.AncestorSettings;

                        // Write new content
                        System.IO.File.WriteAllText(TranslatorCore.CachePath, content);

                        // Reload cache to apply new content immediately
                        TranslatorCore.ReloadCache();

                        // Update server state hash in memory
                        var currentServerState = TranslatorCore.ServerState;
                        if (currentServerState != null)
                        {
                            currentServerState.Hash = fileHash;
                        }

                        // Update LastSyncedHash for multi-device sync detection
                        TranslatorCore.LastSyncedHash = fileHash;
                        // Remember where this file came from: without an account,
                        // this id is the only way to ever hear about a new version
                        TranslatorCore.SourceSiteId = siteId;

                        // Ancestor first: the file's change count is measured against it.
                        TranslatorCore.SaveAncestorCache();
                        TranslatorCore.SaveCache();

                        // Clear all pending update state
                        HasPendingUpdate = false;
                        PendingUpdateInfo = null;
                        PendingUpdateDirection = UpdateDirection.None;

                        TranslatorCore.LogInfo($"[UpdateCheck] Translation updated successfully");

                        ReconcileSettings(ourSettings, TranslationSettings.FromJsonText(content),
                            ancestorSettings, incomingAlreadyApplied: true,
                            sourceLabel: "the newer version online");

                        // Refresh MainPanel to show new translation count
                        MainPanel?.RefreshUI();
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[UpdateCheck] Download failed: {error}");
                        // Refresh MainPanel in all cases to update status
                        MainPanel?.RefreshUI();
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[UpdateCheck] Download error: {errorMsg}");
                    // Refresh MainPanel in all cases to update status
                    MainPanel?.RefreshUI();
                });
            }
        }

        /// <summary>
        /// Download remote translations and start merge process.
        /// Uses tag-aware merge to preserve scoring (A/H/V tags).
        /// </summary>
        public static async Task DownloadForMerge()
        {
            var serverState = TranslatorCore.ServerState;
            if (serverState?.SiteId == null) return;

            // Capture values for closure
            var siteId = serverState.SiteId.Value;

            try
            {
                var result = await ApiClient.Download(siteId);

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var content = result.Content;
                var fileHash = result.FileHash;
                var error = result.Error;

                RunOnMainThread(() =>
                {
                    if (success && !string.IsNullOrEmpty(content))
                    {
                        // Parse remote translations with tags support
                        var remoteTranslations = TranslatorCore.ParseTranslationsFromJson(content);

                        // Perform 3-way merge with tag preservation
                        var local = TranslatorCore.TranslationCache;
                        var ancestor = TranslatorCore.AncestorCache;

                        var mergeResult = TranslationMerger.MergeWithTags(local, remoteTranslations, ancestor);

                        TranslatorCore.LogInfo($"[Merge] Result: {mergeResult.Statistics.GetSummary()}");

                        if (mergeResult.ConflictCount > 0)
                        {
                            // Real conflicts - show merge panel for user to resolve
                            // SetActive first to ensure UI is constructed before setting data
                            MergePanel?.SetActive(true);
                            MergePanel?.SetMergeDataWithTags(mergeResult, remoteTranslations, fileHash);
                            MergePanel?.SetSettingsContext(
                                TranslationSettings.FromCurrentState(),
                                TranslationSettings.FromJsonText(content),
                                TranslatorCore.AncestorSettings,
                                "your version online");
                        }
                        else
                        {
                            // No real conflicts - auto-apply and notify
                            var ourSettings = TranslationSettings.FromCurrentState();
                            var ancestorSettings = TranslatorCore.AncestorSettings;
                            var remoteSettings = TranslationSettings.FromJsonText(content);

                            ApplyMergeWithTags(mergeResult, fileHash, remoteTranslations, remoteSettings);
                            TranslatorCore.LogInfo($"[Merge] Auto-merged: {mergeResult.Statistics.GetSummary()}");

                            // A merge keeps OUR settings and drops theirs, silently.
                            // Now that the server's are known, they get a say.
                            ReconcileSettings(ourSettings, remoteSettings, ancestorSettings,
                                incomingAlreadyApplied: false, sourceLabel: "your version online");
                        }
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[Merge] Download failed: {error}");
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[Merge] Error: {errorMsg}");
                });
            }
        }

        /// <summary>
        /// Apply a merge result and update sync state.
        /// </summary>
        /// <param name="mergeResult">The merge result containing resolved translations</param>
        /// <param name="serverHash">The server hash for sync tracking</param>
        /// <param name="remoteTranslations">The remote translations to save as ancestor (null = use merged)</param>
        /// <summary>
        /// Apply a merge that came from the UPSTREAM Main, not from this
        /// translation's own line on the site.
        ///
        /// Deliberately does NOT touch LastSyncedHash nor AncestorCache: those
        /// track this branch against its own published version, and overwriting
        /// them with the Main's would both lie about what was last uploaded and
        /// destroy the baseline that protects the branch's own keys.
        /// What it does record is the upstream ancestor, so the next merge knows
        /// what the Main changed instead of asking about everything again.
        ///
        /// After this, the branch legitimately differs from its published version:
        /// the imported lines become work to upload.
        /// </summary>
        public static void ApplyUpstreamMergeWithTags(
            MergeResultWithTags mergeResult,
            Dictionary<string, TranslationEntry> mainContent,
            string mainHash,
            TranslationSettings mainSettings = null)
        {
            TranslatorCore.TranslationCache.Clear();
            foreach (var kvp in mergeResult.Merged)
            {
                TranslatorCore.TranslationCache[kvp.Key] = kvp.Value;
            }
            // The Main can carry capture-order indices above our counter
            TranslatorCore.SyncOrderIndexCounter();

            TranslatorCore.SaveCache();

            if (mainContent != null)
            {
                // The Main's settings go into ITS ancestor, never into ours: the
                // two baselines answer different questions and must not mix
                TranslatorCore.SaveMainAncestor(mainContent, mainHash, mainSettings);
            }

            // Against OUR ancestor, untouched above: what we just imported counts
            // as changes waiting to be published on our own translation
            TranslatorCore.RecalculateLocalChanges();

            HasPendingUpdate = TranslatorCore.LocalChangesCount > 0;
            PendingUpdateInfo = null;
            PendingUpdateDirection = HasPendingUpdate ? UpdateDirection.Upload : UpdateDirection.None;
            NotificationDismissed = false;

            TranslatorCore.ReloadCache();
            MainPanel?.RefreshUI();

            TranslatorCore.LogInfo($"[MainMerge] Applied: {TranslatorCore.LocalChangesCount} line(s) now waiting to be uploaded");
        }

        public static void ApplyMergeWithTags(MergeResultWithTags mergeResult, string serverHash,
            Dictionary<string, TranslationEntry> remoteTranslations = null,
            TranslationSettings remoteSettings = null)
        {
            // Apply the merged translations with their tags preserved
            TranslatorCore.TranslationCache.Clear();
            foreach (var kvp in mergeResult.Merged)
            {
                TranslatorCore.TranslationCache[kvp.Key] = kvp.Value;
            }
            // The other branch can bring in capture-order indices above our
            // counter — future captures must not reuse them
            TranslatorCore.SyncOrderIndexCounter();

            // Update server state
            var serverState = TranslatorCore.ServerState;
            if (serverState != null)
            {
                serverState.Hash = serverHash;
            }
            TranslatorCore.LastSyncedHash = serverHash;

            // ⚠ The ancestor is settled BEFORE the file is written, here as everywhere else. The
            // count the file carries is the difference against the ancestor, so writing first
            // recorded the difference against the one this merge was about to replace — a number
            // that was already false as it was written, and that nothing came back to correct.
            //
            // Save REMOTE content as ancestor (not merged!)
            // This way LocalChangesCount = our additions that need uploading.
            // The remote SETTINGS go with it when we know them: that baseline is
            // what lets the next sync tell who changed a section.
            if (remoteTranslations != null)
            {
                TranslatorCore.SaveAncestorFromRemote(remoteTranslations, remoteSettings);
            }
            else
            {
                TranslatorCore.SaveAncestorCache();
            }

            // Recalculate local changes (merged vs remote ancestor)
            TranslatorCore.RecalculateLocalChanges();

            // Now the file, which counts again as it writes and so records what is true after the
            // merge: our own additions, waiting to be uploaded.
            TranslatorCore.SaveCache();

            // Set pending update state based on local changes
            // After merge, if we have local additions/changes, we need to upload
            HasPendingUpdate = TranslatorCore.LocalChangesCount > 0;
            PendingUpdateInfo = null;
            PendingUpdateDirection = HasPendingUpdate ? UpdateDirection.Upload : UpdateDirection.None;

            TranslatorCore.LogInfo($"[Merge] Applied with tags. LocalChangesCount={TranslatorCore.LocalChangesCount}, direction={PendingUpdateDirection}");

            // Clear processing caches so scanner re-evaluates all text with merged translations
            TranslatorCore.ClearProcessingCaches();

            // Refresh MainPanel to show updated translation count and sync status
            MainPanel?.RefreshUI();
        }

        /// <summary>
        /// Download and apply a translation from a TranslationInfo (selected from list).
        /// Used by Wizard and MainPanel community translations.
        /// </summary>
        /// <param name="translation">The translation to download</param>
        /// <param name="onComplete">Callback with (success, message)</param>
        public static async Task DownloadTranslation(TranslationInfo translation, Action<bool, string> onComplete = null)
        {
            if (translation == null)
            {
                onComplete?.Invoke(false, "No translation selected");
                return;
            }

            // Capture values for closure
            var translationId = translation.Id;
            var translationUploader = translation.Uploader;
            var translationFileHash = translation.FileHash;
            var translationType = translation.Type;
            var translationNotes = translation.Notes;
            var translationResourcesUrl = translation.ResourcesUrl;
            var translationSourceLang = translation.SourceLanguage;
            var translationTargetLang = translation.TargetLanguage;

            try
            {
                var result = await ApiClient.Download(translationId);

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var content = result.Content;
                var fileHash = result.FileHash;
                var error = result.Error;

                RunOnMainThread(() =>
                {
                    if (success && !string.IsNullOrEmpty(content))
                    {
                        // Our settings, captured before the incoming file replaces them
                        var ourSettings = TranslationSettings.FromCurrentState();
                        var ancestorSettings = TranslatorCore.AncestorSettings;

                        // Same safety net as the other two paths that replace the file wholesale
                        // (ApplyDownloadedTranslationFile, DownloadUpdate). This one overwrote a
                        // player's own work with a community version and left nothing behind —
                        // and the settings dialog was meanwhile promising a backup.
                        BackupCacheFile();

                        // Write content to file
                        System.IO.File.WriteAllText(TranslatorCore.CachePath, content);
                        TranslatorCore.ReloadCache();

                        // Update server state
                        TranslatorCore.SourceSiteId = translationId;

                        // What a download knows, and nothing beyond it.
                        //
                        // ⚠ Checked, Role, IsOwner and MainUsername are DELIBERATELY absent.
                        // Download is a PUBLIC endpoint: it says who published this file, never
                        // who we are to it. A role deduced here from a username comparison is
                        // what made a plain download announce "[BRANCH]" to someone who had never
                        // uploaded anything — and, logged out, api_user is empty so EVERY download
                        // deduced "branch", including of one's own translation.
                        //
                        // Being a branch means having uploaded into a lineage whose main belongs
                        // to someone else. That is the server's answer to give (check-uuid, or the
                        // sync stream), and until one of them answers it is UNKNOWN — which is
                        // exactly what leaving Checked false says. Same convention as the public
                        // watch in CheckPublicUpdateNow. See
                        // analyse/false-branch-role-after-download.md.
                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Exists = true,
                            SiteId = translationId,
                            Uploader = translationUploader,
                            Hash = fileHash ?? translationFileHash,
                            Type = translationType,
                            Notes = translationNotes,
                            ResourcesUrl = translationResourcesUrl,
                            SourceLanguage = translationSourceLang,
                            TargetLanguage = translationTargetLang
                        };

                        // Update sync state before saving (so SaveCache persists the hash)
                        TranslatorCore.LastSyncedHash = fileHash ?? translationFileHash;

                        // Ancestor first, then the file: the change count written into the file
                        // is measured against the ancestor, so the order decides whether it is true.
                        TranslatorCore.SaveAncestorCache();
                        TranslatorCore.SaveCache();
                        HasPendingUpdate = false;
                        PendingUpdateDirection = UpdateDirection.None;

                        TranslatorCore.LogInfo($"[Download] Downloaded translation #{translationId} from @{translationUploader}");

                        // The player picked this translation and confirmed a
                        // replacement: every difference goes to them, ticked on
                        // the downloaded side. Deciding for them here would undo
                        // the very gesture they just made.
                        ReconcileSettings(ourSettings, TranslationSettings.FromJsonText(content),
                            ancestorSettings, incomingAlreadyApplied: true,
                            sourceLabel: $"the translation by @{translationUploader}",
                            explicitRequest: true);

                        MainPanel?.RefreshUI();
                        onComplete?.Invoke(true, "Downloaded successfully!");
                    }
                    else
                    {
                        onComplete?.Invoke(false, error ?? "Download failed");
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[Download] Error: {errorMsg}");
                    onComplete?.Invoke(false, errorMsg);
                });
            }
        }

        /// <summary>
        /// Download a translation and merge with local changes.
        /// Shows MergePanel if conflicts exist.
        /// </summary>
        /// <param name="translation">The translation to merge with</param>
        /// <param name="onComplete">Callback with (success, message) - only called if no conflicts</param>
        public static async Task DownloadAndMerge(TranslationInfo translation, Action<bool, string> onComplete = null)
        {
            if (translation == null)
            {
                onComplete?.Invoke(false, "No translation selected");
                return;
            }

            // Capture values for closure
            var translationId = translation.Id;
            var translationUploader = translation.Uploader;
            var translationFileHash = translation.FileHash;
            var translationType = translation.Type;
            var translationNotes = translation.Notes;
            var translationResourcesUrl = translation.ResourcesUrl;

            try
            {
                var result = await ApiClient.Download(translationId);

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var content = result.Content;
                var fileHash = result.FileHash;
                var error = result.Error;

                RunOnMainThread(() =>
                {
                    if (success && !string.IsNullOrEmpty(content))
                    {
                        // Same parser and same merger as every other path.
                        //
                        // What stood here before read the file as Dictionary<string,object>
                        // and kept only the entries whose value `is string`, i.e. the LEGACY
                        // format alone: against a file written as {"v":...,"t":...} — every
                        // file today — it collected nothing. And the merge that followed ran
                        // on flattened strings, so the applied result rewrote every tag to
                        // "A", quietly demoting human and validated lines to "AI".
                        // This is the wizard, the very first contact with a community
                        // translation. See analyse/sync-paths-audit.md §2.
                        var remoteTranslations = TranslatorCore.ParseTranslationsFromJson(content);

                        var mergeResult = TranslationMerger.MergeWithTags(
                            TranslatorCore.TranslationCache,
                            remoteTranslations,
                            TranslatorCore.AncestorCache);

                        TranslatorCore.LogInfo($"[Merge] Result: {mergeResult.Statistics.GetSummary()}");

                        // Update server state to track this translation.
                        //
                        // ⚠ No Checked, Role, IsOwner or MainUsername here either — same reason as
                        // in DownloadTranslation above: merging someone's file in tells us nothing
                        // about who we are to it. The role is the server's to give.
                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Exists = true,
                            SiteId = translationId,
                            Uploader = translationUploader,
                            Hash = fileHash ?? translationFileHash,
                            Type = translationType,
                            Notes = translationNotes,
                            ResourcesUrl = translationResourcesUrl
                        };

                        if (mergeResult.ConflictCount > 0)
                        {
                            // Show merge panel for user to resolve conflicts
                            // SetActive first to ensure UI is constructed before setting data
                            MergePanel?.SetActive(true);
                            MergePanel?.SetMergeDataWithTags(mergeResult, remoteTranslations, fileHash);
                            MergePanel?.SetSettingsContext(
                                TranslationSettings.FromCurrentState(),
                                TranslationSettings.FromJsonText(content),
                                TranslatorCore.AncestorSettings,
                                $"the translation by @{translationUploader}",
                                explicitRequest: true);
                            // Don't call onComplete - MergePanel handles the rest
                        }
                        else
                        {
                            // No conflicts - apply merge directly
                            var ourSettings = TranslationSettings.FromCurrentState();
                            var ancestorSettings = TranslatorCore.AncestorSettings;
                            var remoteSettings = TranslationSettings.FromJsonText(content);

                            ApplyMergeWithTags(mergeResult, fileHash, remoteTranslations, remoteSettings);
                            onComplete?.Invoke(true, "Merged successfully!");

                            // First contact with a community translation: its
                            // fonts and exclusions are usually the reason it
                            // reads correctly, so they must not be dropped —
                            // and the player asked for THIS one, so they arbitrate
                            ReconcileSettings(ourSettings, remoteSettings, ancestorSettings,
                                incomingAlreadyApplied: false,
                                sourceLabel: $"the translation by @{translationUploader}",
                                explicitRequest: true);
                        }
                    }
                    else
                    {
                        onComplete?.Invoke(false, error ?? "Download failed");
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[Merge] Error: {errorMsg}");
                    onComplete?.Invoke(false, errorMsg);
                });
            }
        }

        #endregion

        /// <summary>
        /// Show the wizard panel (first run or manual trigger).
        /// </summary>
        public static void ShowWizard()
        {
            if (WizardPanel == null || MainPanel == null) return;

            ShowUI = true;
            WizardPanel.SetActive(true);
            MainPanel.SetActive(false);
        }

        /// <summary>
        /// Show the main settings panel.
        /// </summary>
        public static void ShowMain()
        {
            if (WizardPanel == null || MainPanel == null) return;

            ShowUI = true;
            WizardPanel.SetActive(false);
            MainPanel.SetActive(true);
            BootstrapInterfaceFontOnce();
        }

        /// <summary>
        /// First-show bootstrap for the mod UI font + translation. Runs once, when the UI is first
        /// opened: custom fonts are loaded, panels are active and the translation worker is running —
        /// unlike init time, where an early pass finds no custom font and no worker.
        /// </summary>
        private static void BootstrapInterfaceFontOnce()
        {
            if (_uiFontBootstrapped) return;
            _uiFontBootstrapped = true;
            ApplyInterfaceFont();
            RefreshOwnUITranslation();
        }

        /// <summary>
        /// Toggle the main settings panel visibility.
        /// </summary>
        public static void ToggleMain()
        {
            if (MainPanel == null) return;

            if (MainPanel.Enabled)
            {
                MainPanel.SetActive(false);
                if (!AnyPanelVisible())
                    ShowUI = false;
            }
            else
            {
                ShowMain();
            }
        }

        /// <summary>
        /// Open the Inspector Panel in the specified mode.
        /// Exclusion mode: select elements to exclude from translation.
        /// BitmapReplace mode: select images to replace with translated versions.
        /// </summary>
        public static void OpenInspectorPanel(Panels.InspectorMode mode = Panels.InspectorMode.Exclusion)
        {
            if (InspectorPanel == null) return;
            ShowUI = true;
            InspectorPanel.SetMode(mode);
            InspectorPanel.SetActive(true);
        }

        /// <summary>
        /// Hide all panels including status overlay.
        /// </summary>
        public static void HideAll()
        {
            CloseAllPanels();
            StatusOverlay?.SetActive(false);
            ShowUI = false;
        }

        /// <summary>
        /// Hide all main panels but allow status overlay to remain.
        /// Alias for CloseAllPanels() for backward compatibility.
        /// </summary>
        public static void HideMainPanels()
        {
            CloseAllPanels();
        }

        /// <summary>
        /// Check if any interactive panel is currently visible.
        /// Uses the centralized panel list.
        /// </summary>
        private static bool AnyPanelVisible()
        {
            for (int i = 0; i < _interactivePanels.Count; i++)
            {
                if (_interactivePanels[i].Enabled)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Close all interactive panels.
        /// Uses the centralized panel list.
        /// </summary>
        public static void CloseAllPanels()
        {
            for (int i = 0; i < _interactivePanels.Count; i++)
            {
                _interactivePanels[i].SetActive(false);
            }
        }

        /// <summary>
        /// Get all currently visible panels.
        /// </summary>
        public static List<Panels.TranslatorPanelBase> GetVisiblePanels()
        {
            var visible = new List<Panels.TranslatorPanelBase>();
            for (int i = 0; i < _interactivePanels.Count; i++)
            {
                if (_interactivePanels[i].Enabled)
                    visible.Add(_interactivePanels[i]);
            }
            return visible;
        }

        private static float _overlayRefreshTimer = 0f;
        private const float OVERLAY_REFRESH_INTERVAL = 0.5f; // Refresh every 0.5 seconds

        private static void UpdateUI()
        {
            // Don't do anything until fully initialized
            if (!_initialized) return;

            // Called every frame when UI is active
            // Can be used for hotkey detection, etc.
            CheckHotkey();

            // Manage EventSystem and Cursor for InputField support
            // Enable when panels open, release when all panels close
            // Uses UniverseLib's Force_Unlock_Mouse to properly handle cursor locking
            bool panelsVisible = AnyPanelVisible();
            // ONE state for "the interface holds the input", used by the EventSystem handover AND
            // by the capture. Two separate notions would drift, and the drift would show up as the
            // half-captured frame that let a click through.
            //
            // It outlives the panel by design: a panel closes on the click that pressed its close
            // button, and that click is not finished being resolved. Handing everything back right
            // then is what delivered it to whatever sat behind. So we hold until the mouse is idle.
            // Put the absorber in place ON THE PRESS, while the panel is still there — not when it
            // vanishes. The held click is delivered to whatever the raycast finds once a target is
            // available again, and by the frame the panel is gone that raycast has already been
            // taken. Standing behind the panel from the start means the click lands on us whenever
            // it is finally released, and never on the game.
            if (panelsVisible && (InputManager.GetMouseButtonDown(0) || InputManager.GetMouseButtonDown(1)))
            {
                SetClickAbsorber(true);
                // Narrate the raycasts of this whole click, absorber included: twelve attempts have
                // assumed where the click lands and none has checked. If UGT_ClickAbsorber never
                // shows up in these lines, it is not in the raycast at all and every version of
                // this idea was dead on arrival.
                DeselectGameObject();
            }

            if (panelsVisible != _lastPanelVisibleState)
            {
                if (panelsVisible)
                {
                    _lastPanelVisibleState = true;
                    _uiHoldsInput = true;
                    _closedAt = 0f;
                    _absorberUntil = 0f;
                    SetClickAbsorber(false);
                    // Enable cursor unlock - UniverseLib will handle the rest
                    ConfigManager.Force_Unlock_Mouse = true;
                    // While OUR panels hold the input, the game must not be selectable either.
                    // Left on permanently — as it was — the EventSystem may pick a game object as
                    // the selected one the instant our panel stops being it, which is one way a
                    // button behind a closing window gets activated without any raycast reaching it.
                    // It goes back on below, because the corner overlay alone must never take the
                    // game's menu navigation away.
                    ConfigManager.Allow_UI_Selection_Outside_UIBase = false;
                    EventSystemHelper.EnableEventSystem();
                    UniverseLib.Input.InputCapture.ResetActivity();
                }
                // ⚠ Only mark it handled once we actually hand back, or a deferred release becomes
                // a release that never happens — the panel would close and the game would never
                // get its EventSystem back for the rest of the session.
                else if (SecondsSinceClose() >= SecondsBeforeHandover && MouseAtRest())
                {
                    // ⚠ The absorber stays. Lifting the capture and removing it in the same frame
                    // is what defeated it: while input is captured the module cannot even see the
                    // release, so the held click was never offered to the absorber — and the frame
                    // it finally was, the absorber had just gone, leaving the game's button as the
                    // only thing under the cursor.
                    //
                    // So: hand the input back FIRST, with the absorber still covering everything.
                    // The module processes the release, raycasts, finds our surface, and the click
                    // dies there. The absorber is taken down a moment later, by TickAbsorber.
                    _lastPanelVisibleState = false;
                    _uiHoldsInput = false;
                    _absorberUntil = Time.realtimeSinceStartup + SecondsAbsorberOutlivesCapture;
                    // Disable cursor unlock - UniverseLib will restore game's cursor state
                    ConfigManager.Force_Unlock_Mouse = false;
                    ConfigManager.Allow_UI_Selection_Outside_UIBase = true;
                    EventSystemHelper.ReleaseEventSystem();

                    // What the capture actually managed while the panel was open. Without this,
                    // "the game reads through another API" and "the capture never armed" look
                    // exactly alike from a chair in front of the game.
                    TranslatorCore.LogInfo($"[InputCapture] {UniverseLib.Input.InputCapture.DescribeActivity()}");
                }
            }

            // Under debug only: narrate a whole click's raycasts, and say who is listening at the
            // release. Kept because it is what finally showed the capture had stopped working —
            // no reasoning had caught that, and none would have.
            if (TranslatorCore.DebugMode && panelsVisible)
            {
                if (InputManager.GetMouseButtonDown(0) || InputManager.GetMouseButtonDown(1))
                    UniverseLib.Input.InputCapture.DiagnoseNext(8);
                if (InputManager.GetMouseButtonUp(0))
                    LogEventSystemState();
            }

            TickAbsorber();

            // Contextual help bar: resolve the hovered control by geometric poll.
            // Only while a panel is open (nothing to hover otherwise). Event-based hover
            // (injected IPointerEnterHandler) is silent on IL2CPP, so we poll instead.
            if (panelsVisible)
                Components.HelpZone.PollHover();

            // Deferred interface-font re-dirty (atlas warms async after a reback).
            TickFontRerender();

            // Manage status overlay visibility
            UpdateStatusOverlay();
        }

        private static void UpdateStatusOverlay()
        {
            if (StatusOverlay == null) return;

            // Tick the hotkey-feedback toast (auto-hides after its duration)
            StatusOverlay.TickToast();

            // User can disable the entire notification overlay
            if (!TranslatorCore.Config.sync.notifications_enabled)
            {
                if (StatusOverlay.Enabled)
                {
                    StatusOverlay.SetActive(false);
                }
                return;
            }

            // Determine what should be shown
            bool panelsOpen = AnyPanelVisible();
            bool firstRunDone = TranslatorCore.Config.first_run_completed;

            // AI queue is ALWAYS visible when translating (even with panels open)
            bool aiActive = TranslatorCore.Config.IsTranslationEnabled &&
                           (TranslatorCore.QueueCount > 0 || TranslatorCore.IsTranslating);

            // Other notifications only show when no panels are open
            // (mod update and sync are now shown in MainPanel)
            bool hasOtherContent = !panelsOpen && StatusOverlay.HasNotificationContent();

            bool shouldShow = firstRunDone && (aiActive || hasOtherContent);

            if (shouldShow)
            {
                // Tell overlay which mode to use
                StatusOverlay.SetPanelsOpenMode(panelsOpen);

                // Show and refresh periodically
                if (!StatusOverlay.Enabled)
                {
                    StatusOverlay.SetActive(true);
                }

                // Refresh status overlay content periodically
                _overlayRefreshTimer += UnityEngine.Time.unscaledDeltaTime;
                if (_overlayRefreshTimer >= OVERLAY_REFRESH_INTERVAL)
                {
                    _overlayRefreshTimer = 0f;
                    StatusOverlay.RefreshOverlay();
                }
            }
            else if (StatusOverlay.Enabled)
            {
                StatusOverlay.SetActive(false);
            }
        }

        /// <summary>
        /// Checks if the given hotkey string (e.g., "Ctrl+F10") was just pressed this frame.
        /// Returns false for null/empty strings (disabled hotkeys).
        /// </summary>
        private static bool IsHotkeyPressed(string hotkey)
        {
            // Spelling is settled in UnityGameTranslator.Common.Hotkeys, shared with the capture
            // widget that writes this string and with the manager that can write it too. It used
            // to be taken apart here with a case-sensitive Contains/Replace while the manager
            // stripped case-insensitively, so "ctrl+F10" passed as valid over there and never
            // fired here — and nothing says so, the panel simply never opens.
            if (!Hotkeys.TryParse(hotkey, out string baseKey, out bool requireCtrl, out bool requireAlt, out bool requireShift))
                return false;

            // KeyCode stays the authority on which keys exist: it is Unity's enum, wider than any
            // list we could keep, and second-guessing it would refuse keys that work today.
            if (!Enum.TryParse<KeyCode>(baseKey, true, out KeyCode keyCode))
                return false;

            if (!UniverseLib.Input.InputManager.GetKeyDown(keyCode))
                return false;

            bool ctrlHeld = UniverseLib.Input.InputManager.GetKey(KeyCode.LeftControl) ||
                           UniverseLib.Input.InputManager.GetKey(KeyCode.RightControl);
            bool altHeld = UniverseLib.Input.InputManager.GetKey(KeyCode.LeftAlt) ||
                          UniverseLib.Input.InputManager.GetKey(KeyCode.RightAlt);
            bool shiftHeld = UniverseLib.Input.InputManager.GetKey(KeyCode.LeftShift) ||
                            UniverseLib.Input.InputManager.GetKey(KeyCode.RightShift);

            return ctrlHeld == requireCtrl && altHeld == requireAlt && shiftHeld == requireShift;
        }

        private static void CheckHotkey()
        {
            // Skip hotkey check during wizard
            if (WizardPanel != null && WizardPanel.Enabled)
                return;

            var config = TranslatorCore.Config;

            // Main settings panel hotkey (always configured, default Ctrl+F10)
            if (IsHotkeyPressed(config.settings_hotkey))
            {
                ToggleMain();
                return;
            }

            // Additional toggles (all empty by default, configured in OptionsPanel)
            if (IsHotkeyPressed(config.toggle_translations_hotkey))
            {
                ToggleTranslationsHotkey();
                return;
            }

            if (IsHotkeyPressed(config.toggle_ai_hotkey))
            {
                ToggleAIHotkey();
                return;
            }

            if (IsHotkeyPressed(config.toggle_images_hotkey))
            {
                ToggleImagesHotkey();
                return;
            }

            if (IsHotkeyPressed(config.toggle_fonts_hotkey))
            {
                ToggleFontsHotkey();
                return;
            }

            if (IsHotkeyPressed(config.toggle_overlay_hotkey))
            {
                ToggleOverlayHotkey();
                return;
            }

            if (IsHotkeyPressed(config.open_inspector_hotkey))
            {
                OpenInspectorHotkey();
                return;
            }

            if (IsHotkeyPressed(config.open_upload_hotkey))
            {
                OpenUploadHotkey();
                return;
            }

            if (IsHotkeyPressed(config.open_exclusion_mode_hotkey))
            {
                OpenExclusionModeHotkey();
                return;
            }

            if (IsHotkeyPressed(config.open_text_editor_hotkey))
            {
                OpenTextEditorHotkey();
                return;
            }

            if (IsHotkeyPressed(config.force_scan_hotkey))
            {
                ForceScanHotkey();
                return;
            }
        }

        /// <summary>
        /// Toggles the global enable_translations flag.
        /// When disabled, the scanner restores original text for all previously translated elements.
        /// Mirrors the full pipeline used by OptionsPanel.ApplySettings.
        /// </summary>
        private static void ToggleTranslationsHotkey()
        {
            var config = TranslatorCore.Config;
            config.enable_translations = !config.enable_translations;
            TranslatorCore.SaveConfig();
            TranslatorCore.ClearProcessingCaches();
            // reapplyAllScales: re-derive every tracked component's size from its gated scale, so
            // components the game doesn't re-trigger don't stay at the old scaled fontSize (issue #21).
            TranslatorScanner.ForceRefreshAllText(reapplyAllScales: true);
            OptionsPanel?.RefreshFromConfig();
            ShowHotkeyFeedback(config.enable_translations ? "Translations: ON" : "Translations: OFF", config.enable_translations);
        }

        /// <summary>
        /// Pauses and resumes live translation. Already-translated entries stay on screen — this
        /// stops new calls being made, it does not undo anything.
        ///
        /// ⚠ The backend is NOT touched, and that is the fix rather than a detail. This used to
        /// pause by writing translation_backend = "none" and remembering the real value in a
        /// static field: the config on disk no longer said which service had been chosen, so
        /// quitting while paused lost it, and anything reading the file from outside the game —
        /// the manager, for one — saw a setup that had never been configured.
        ///
        /// A pause you cannot resume from where you left off is not a pause.
        /// </summary>
        private static void ToggleAIHotkey()
        {
            var config = TranslatorCore.Config;

            // Nothing to pause, and nothing to resume onto: there is no backend to run. Said out
            // loud rather than silently flipping a flag that would change nothing — a hotkey that
            // appears to do nothing is read as a broken hotkey.
            if (config.translation_backend == "none")
            {
                ShowHotkeyFeedback("No translation backend selected", false);
                return;
            }

            config.enable_ai = !config.enable_ai;

            if (config.enable_ai)
            {
                TranslatorCore.EnsureWorkerRunning();
                ShowHotkeyFeedback($"Translation: ON ({config.translation_backend})", true);
            }
            else
            {
                TranslatorCore.ClearQueue();
                ShowHotkeyFeedback("Translation: OFF", false);
            }

            TranslatorCore.SaveConfig();
            OptionsPanel?.RefreshFromConfig();
        }

        /// <summary>
        /// Toggles image replacement (persisted to config.json).
        /// Primarily a debug toggle for translators — end users should leave this on.
        /// </summary>
        private static void ToggleImagesHotkey()
        {
            var config = TranslatorCore.Config;
            config.enable_image_replacement = !config.enable_image_replacement;
            TranslatorCore.SaveConfig();

            if (config.enable_image_replacement)
            {
                // Re-apply replacements to the current scene.
                ImageReplacer.ApplyToScene();
            }
            else
            {
                // Restore original sprites for every component we tracked on apply.
                ImageReplacer.RestoreAllOriginalImages();
            }

            TranslationParamsPanel?.RefreshFromConfig();
            ShowHotkeyFeedback(config.enable_image_replacement ? "Image Replacement: ON" : "Image Replacement: OFF", config.enable_image_replacement);
        }

        /// <summary>
        /// Toggles font replacement (persisted to config.json).
        /// Primarily a debug toggle for translators — end users should leave this on.
        /// </summary>
        private static void ToggleFontsHotkey()
        {
            var config = TranslatorCore.Config;
            config.enable_font_replacement = !config.enable_font_replacement;
            TranslatorCore.SaveConfig();

            if (!config.enable_font_replacement)
            {
                FontManager.RestoreAllOriginalFonts();
            }

            TranslatorCore.ClearProcessingCaches();
            // reapplyAllScales: the design-scale gate makes GetFontScale correct on toggle, but only
            // re-triggered components get ApplyFontScale — force a re-derive on ALL so static /
            // game-managed text doesn't keep the old scaled size (issue #21: toggle grew/shrank text).
            TranslatorScanner.ForceRefreshAllText(reapplyAllScales: true);
            TranslationParamsPanel?.RefreshFromConfig();
            ShowHotkeyFeedback(config.enable_font_replacement ? "Font Replacement: ON" : "Font Replacement: OFF", config.enable_font_replacement);
        }

        /// <summary>
        /// Toggles the visibility of the notification overlay (for clean screenshots).
        /// </summary>
        private static void ToggleOverlayHotkey()
        {
            if (StatusOverlay == null) return;
            bool newState = !StatusOverlay.Enabled;
            StatusOverlay.SetActive(newState);
            ShowHotkeyFeedback(newState ? "Notifications: ON" : "Notifications: OFF", newState);
        }

        /// <summary>
        /// Toggles the Inspector panel (opens if closed, closes if open).
        /// </summary>
        private static void OpenInspectorHotkey()
        {
            if (InspectorPanel == null) return;
            if (InspectorPanel.Enabled)
            {
                InspectorPanel.SetActive(false);
                ShowHotkeyFeedback("Inspector: CLOSED", false);
            }
            else
            {
                ShowUI = true;
                InspectorPanel.SetActive(true);
                ShowHotkeyFeedback("Inspector: OPEN", true);
            }
        }

        /// <summary>
        /// Toggles the Upload panel (opens if closed, closes if open).
        /// </summary>
        private static void OpenUploadHotkey()
        {
            if (UploadPanel == null) return;
            if (UploadPanel.Enabled)
            {
                UploadPanel.SetActive(false);
                ShowHotkeyFeedback("Upload: CLOSED", false);
            }
            else
            {
                ShowUI = true;
                UploadPanel.SetActive(true);
                ShowHotkeyFeedback("Upload: OPEN", true);
            }
        }

        /// <summary>
        /// Toggles the Inspector panel in Exclusion mode.
        /// If already open in another mode, switches to Exclusion.
        /// </summary>
        private static void OpenExclusionModeHotkey()
        {
            if (InspectorPanel == null) return;
            if (InspectorPanel.Enabled)
            {
                InspectorPanel.SetActive(false);
                ShowHotkeyFeedback("Exclusion mode: CLOSED", false);
            }
            else
            {
                OpenInspectorPanel(Panels.InspectorMode.Exclusion);
                ShowHotkeyFeedback("Exclusion mode: ON", true);
            }
        }

        /// <summary>
        /// Toggles the Inspector panel in TextEdit mode (click UI text to edit translation in place).
        /// </summary>
        private static void OpenTextEditorHotkey()
        {
            if (InspectorPanel == null) return;
            if (InspectorPanel.Enabled)
            {
                InspectorPanel.SetActive(false);
                ShowHotkeyFeedback("Text editor: CLOSED", false);
            }
            else
            {
                OpenInspectorPanel(Panels.InspectorMode.TextEdit);
                ShowHotkeyFeedback("Text editor: ON", true);
            }
        }

        /// <summary>
        /// Forces a full scene scan (useful after problematic scene changes).
        /// </summary>
        private static void ForceScanHotkey()
        {
            TranslatorCore.ClearProcessingCaches();
            TranslatorScanner.Scan();
            ShowHotkeyFeedback("Scene rescanned");
        }

        /// <summary>
        /// Shows a brief visual feedback for a hotkey action via the StatusOverlay toast.
        /// Also logs to the mod loader console for accessibility.
        /// </summary>
        private static void ShowHotkeyFeedback(string message, bool? enabled = null)
        {
            TranslatorCore.LogInfo($"[Hotkey] {message}");
            if (StatusOverlay == null) return;

            var tone = Panels.StatusOverlay.ToastTone.Info;
            if (enabled.HasValue)
                tone = enabled.Value ? Panels.StatusOverlay.ToastTone.On : Panels.StatusOverlay.ToastTone.Off;

            StatusOverlay.ShowToast(message, tone);
        }

        private static void LogHandler(string message, LogType type)
        {
            switch (type)
            {
                case LogType.Error:
                case LogType.Exception:
                    TranslatorCore.LogError($"[UniverseLib] {message}");
                    break;
                case LogType.Warning:
                    TranslatorCore.LogWarning($"[UniverseLib] {message}");
                    break;
                default:
                    TranslatorCore.LogInfo($"[UniverseLib] {message}");
                    break;
            }
        }
    }
}
