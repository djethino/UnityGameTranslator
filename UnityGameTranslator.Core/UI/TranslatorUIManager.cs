using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UniverseLib;
using UniverseLib.Config;
using UniverseLib.Input;
using UniverseLib.Runtime;
using UniverseLib.UI;

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

        private static bool _initialized;
        public static bool IsInitialized => _initialized;

        // Callback for when initialization completes (used by TranslatorPatches to retry failed font replacements)
        public static event Action OnInitialized;
        private static bool _showUI;
        private static bool _lastPanelVisibleState; // Track panel state for EventSystem and cursor management

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

            CreatePanels();

            _initialized = true;

            // Notify listeners (e.g., TranslatorPatches to retry failed font replacements)
            try { OnInitialized?.Invoke(); } catch { }

            // Initialize UI state based on config
            InitializeUIState();

            // Single source of truth for the per-frame tick: run OnUpdate (drains the
            // main-thread queue, feeds the scanner's adaptive frame-time budget, persists
            // cache) and Scan (applies pending translations + scans the scene) inside a
            // permanent coroutine. We intentionally do NOT also tick from each mod
            // loader's Update() callback — that would double-call. The coroutine works
            // even in games whose host suppresses our MonoBehaviour.Update (one was
            // observed to do so) because Unity drives coroutines through a separate path
            // hosted by UniverseLib's own runtime, which is proven to tick wherever our UI
            // already works.
            try
            {
                RuntimeHelper.StartCoroutine(MainTickLoop());
                TranslatorCore.LogInfo("[UIManager] Main tick coroutine started");
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[UIManager] Failed to start main tick coroutine: {e.GetType().Name}: {e.Message}");
            }
        }

        private static IEnumerator MainTickLoop()
        {
            while (true)
            {
                // Catch around each iteration so a transient failure (e.g. a scene with
                // unexpected components) does not silently terminate the loop — Unity
                // stops a coroutine the first time an exception escapes.
                try
                {
                    TranslatorCore.OnUpdate(Time.realtimeSinceStartup);
                    TranslatorScanner.Scan();
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
            _interactivePanels.Add(InspectorPanel);
            _interactivePanels.Add(TranslationParamsPanel);

            // Hide all panels initially (using centralized list + StatusOverlay)
            CloseAllPanels();
            StatusOverlay.SetActive(false);
        }

        private static void InitializeUIState()
        {
            TranslatorCore.LogInfo($"[UIManager] InitializeUIState, first_run_completed={TranslatorCore.Config.first_run_completed}");

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

        private static async void TriggerStartupTasks()
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

                // Start SSE sync stream (replaces FetchServerState + CheckForUpdates)
                // The SSE 'state' event combines check-uuid + check in one real-time payload
                if (TranslatorCore.Config.online_mode && !string.IsNullOrEmpty(TranslatorCore.Config.api_token))
                {
                    StartSyncStream();
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

        #region SSE Sync Stream

        /// <summary>
        /// Start the SSE sync stream. Replaces FetchServerState + CheckForUpdates with a
        /// single real-time connection. The 'state' event provides initial state on connect,
        /// and 'translation_updated' events push live changes.
        /// Called at startup and after successful login.
        /// </summary>
        public static void StartSyncStream()
        {
            if (!TranslatorCore.Config.online_mode)
            {
                TranslatorCore.LogInfo("[SyncSSE] Online mode disabled, skipping sync stream");
                return;
            }

            if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                TranslatorCore.LogInfo("[SyncSSE] Not authenticated, skipping sync stream");
                return;
            }

            string uuid = TranslatorCore.FileUuid;
            if (string.IsNullOrEmpty(uuid))
            {
                TranslatorCore.LogInfo("[SyncSSE] No FileUuid, skipping sync stream");
                return;
            }

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
        /// Stop the SSE sync stream. Called on logout, offline mode toggle, or shutdown.
        /// </summary>
        public static void StopSyncStream()
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
                };

                if (translation != null && translation.Type != JTokenType.Null)
                {
                    serverState.SiteId = translation["id"]?.Value<int>();
                    serverState.Uploader = TranslatorCore.Config.api_user;
                    serverState.Hash = translation["file_hash"]?.Value<string>();
                    serverState.Type = translation["type"]?.Value<string>();
                    serverState.Notes = translation["notes"]?.Value<string>();
                    serverState.ResourcesUrl = translation["resources_url"]?.Value<string>();
                }
                else if (main != null && main.Type != JTokenType.Null)
                {
                    serverState.SiteId = main["id"]?.Value<int>();
                    serverState.Uploader = main["uploader"]?.Value<string>();
                    serverState.MainUsername = main["uploader"]?.Value<string>();
                    serverState.Hash = main["file_hash"]?.Value<string>();
                    serverState.ResourcesUrl = main["resources_url"]?.Value<string>();
                }

                TranslatorCore.ServerState = serverState;

                TranslatorCore.LogDebug($"[SyncSSE] State: exists={exists}, role={role}, siteId={serverState.SiteId}");

                // Client-side update detection (URL hash may be stale after reconnection)
                string serverHash = serverState.Hash;
                string localHash = TranslatorCore.ComputeContentHash();
                bool hasUpdate = !string.IsNullOrEmpty(serverHash) && serverHash != localHash;

                if (hasUpdate && TranslatorCore.Config.sync.check_update_on_start)
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
            bool hasLocalChanges = TranslatorCore.LocalChangesCount > 0 || TranslatorCore.MetadataDirty;

            // Check if server changed since our last sync
            string lastSyncedHash = TranslatorCore.LastSyncedHash;
            bool serverChanged = !string.IsNullOrEmpty(lastSyncedHash) &&
                                 serverHash != lastSyncedHash;

            // If no LastSyncedHash, we can't tell definitively what changed
            // If we have local changes AND server hash differs, assume potential conflict to be safe
            if (string.IsNullOrEmpty(lastSyncedHash))
            {
                serverChanged = hasLocalChanges;
            }

            // Determine direction based on what changed
            if (hasLocalChanges && serverChanged)
            {
                PendingUpdateDirection = UpdateDirection.Merge;
                TranslatorCore.LogInfo($"[SyncSSE] CONFLICT: Both local ({TranslatorCore.LocalChangesCount} changes) and server changed - merge needed");
            }
            else if (hasLocalChanges)
            {
                PendingUpdateDirection = UpdateDirection.Upload;
                TranslatorCore.LogInfo($"[SyncSSE] Local has {TranslatorCore.LocalChangesCount} changes to upload");
            }
            else
            {
                PendingUpdateDirection = UpdateDirection.Download;
                TranslatorCore.LogInfo($"[SyncSSE] Server has update: {lineCount} lines");
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
        private static async void HandleMergeCompleted(string jsonData, int translationId)
        {
            try
            {
                var data = ApiClient.ParseJsonSafe(jsonData);
                string fileHash = data["file_hash"]?.Value<string>();
                int lineCount = data["line_count"]?.Value<int>() ?? 0;

                TranslatorCore.LogInfo($"[MergeSSE] Merge completed! hash={fileHash?.Substring(0, 16)}..., lines={lineCount}");

                // Stop listening — we only need one event
                StopMergeCompletionListener();

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
                        TranslatorCore.SaveCache();
                        TranslatorCore.SaveAncestorCache();

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

            string backupPath = TranslatorCore.CachePath + ".backup";
            if (System.IO.File.Exists(TranslatorCore.CachePath))
            {
                System.IO.File.Copy(TranslatorCore.CachePath, backupPath, true);
            }

            System.IO.File.WriteAllText(TranslatorCore.CachePath, content);

            // Reload cache to apply new content immediately
            TranslatorCore.ReloadCache();
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
        public static void StartEditSessionListener(string modKey)
        {
            if (string.IsNullOrEmpty(modKey))
            {
                TranslatorCore.LogWarning("[EditSSE] No mod key, skipping edit session listener");
                return;
            }

            StopEditSessionListener();
            _editSessionModKey = modKey;
            _sessionContentHash = null;
            _lastAppliedSaveHash = null;
            // Baseline for the browser-save merge: the file we just handed to the
            // browser. Any key captured in-game after this is "local only" (kept);
            // any key the browser removes relative to this is an honored deletion.
            _editSessionAncestor = SnapshotTranslationCache();
            _nextKeepaliveTime = Time.realtimeSinceStartup + KeepaliveIntervalSeconds;

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
            if (string.IsNullOrEmpty(modKey)) return;

            try
            {
                ApiClient.EndEditSession(modKey).Wait(2000);
                TranslatorCore.LogInfo("[EditSSE] Session ended (game shutdown)");
            }
            catch { }
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
            try
            {
                string backupPath = TranslatorCore.CachePath + ".backup";
                if (System.IO.File.Exists(TranslatorCore.CachePath))
                    System.IO.File.Copy(TranslatorCore.CachePath, backupPath, true);
            }
            catch { }

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
                        string backupPath = TranslatorCore.CachePath + ".backup";
                        if (System.IO.File.Exists(TranslatorCore.CachePath))
                        {
                            System.IO.File.Copy(TranslatorCore.CachePath, backupPath, true);
                        }

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

                        // Save cache and ancestor
                        TranslatorCore.SaveCache();
                        TranslatorCore.SaveAncestorCache();

                        // Clear all pending update state
                        HasPendingUpdate = false;
                        PendingUpdateInfo = null;
                        PendingUpdateDirection = UpdateDirection.None;

                        TranslatorCore.LogInfo($"[UpdateCheck] Translation updated successfully");

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
                        }
                        else
                        {
                            // No real conflicts - auto-apply and notify
                            ApplyMergeWithTags(mergeResult, fileHash, remoteTranslations);
                            TranslatorCore.LogInfo($"[Merge] Auto-merged: {mergeResult.Statistics.GetSummary()}");
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
        public static void ApplyMerge(MergeResult mergeResult, string serverHash, Dictionary<string, string> remoteTranslations = null)
        {
            // Apply the merged translations (convert to TranslationEntry with AI tag for legacy merge)
            foreach (var kvp in mergeResult.Merged)
            {
                // For now, merged values get AI tag by default
                // Full tag support will be added when TranslationMerger is updated.
                // The capture-order index of an existing local entry survives the
                // rewrite; keys new to us stay index-less until LoadCache backfills.
                long? existingIndex = null;
                if (TranslatorCore.TranslationCache.TryGetValue(kvp.Key, out var existingEntry))
                    existingIndex = existingEntry.Index;

                TranslatorCore.TranslationCache[kvp.Key] = new TranslationEntry
                {
                    Value = kvp.Value,
                    Tag = "A",  // TODO: Preserve original tags when merger is updated
                    Index = existingIndex
                };
            }
            TranslatorCore.SyncOrderIndexCounter();

            // Update server state
            var serverState = TranslatorCore.ServerState;
            if (serverState != null)
            {
                serverState.Hash = serverHash;
            }
            TranslatorCore.LastSyncedHash = serverHash;

            // Save cache
            TranslatorCore.SaveCache();

            // Save REMOTE content as ancestor (not merged!)
            // This way LocalChangesCount = our additions that need uploading
            if (remoteTranslations != null)
            {
                TranslatorCore.SaveAncestorFromRemote(remoteTranslations);
            }
            else
            {
                TranslatorCore.SaveAncestorCache();
            }

            // Recalculate local changes (merged vs remote ancestor)
            TranslatorCore.RecalculateLocalChanges();

            // Set pending update state based on local changes
            // After merge, if we have local additions/changes, we need to upload
            HasPendingUpdate = TranslatorCore.LocalChangesCount > 0;
            PendingUpdateInfo = null;
            PendingUpdateDirection = HasPendingUpdate ? UpdateDirection.Upload : UpdateDirection.None;

            TranslatorCore.LogInfo($"[Merge] Applied successfully. LocalChangesCount={TranslatorCore.LocalChangesCount}, direction={PendingUpdateDirection}");

            // Clear processing caches so scanner re-evaluates all text with merged translations
            TranslatorCore.ClearProcessingCaches();

            // Refresh MainPanel to show updated translation count and sync status
            MainPanel?.RefreshUI();
        }

        /// <summary>
        /// Apply a merge result with tags and update sync state.
        /// This version preserves tags from the merge result (critical for scoring system).
        /// </summary>
        /// <param name="mergeResult">The merge result containing resolved translations with tags</param>
        /// <param name="serverHash">The server hash for sync tracking</param>
        /// <param name="remoteTranslations">The remote translations to save as ancestor</param>
        public static void ApplyMergeWithTags(MergeResultWithTags mergeResult, string serverHash, Dictionary<string, TranslationEntry> remoteTranslations = null)
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

            // Save cache
            TranslatorCore.SaveCache();

            // Save REMOTE content as ancestor (not merged!)
            // This way LocalChangesCount = our additions that need uploading
            if (remoteTranslations != null)
            {
                TranslatorCore.SaveAncestorFromRemote(remoteTranslations);
            }
            else
            {
                TranslatorCore.SaveAncestorCache();
            }

            // Recalculate local changes (merged vs remote ancestor)
            TranslatorCore.RecalculateLocalChanges();

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
                        // Write content to file
                        System.IO.File.WriteAllText(TranslatorCore.CachePath, content);
                        TranslatorCore.ReloadCache();

                        // Check if current user owns this translation
                        string currentUser = TranslatorCore.Config.api_user;
                        bool isOwner = !string.IsNullOrEmpty(currentUser) &&
                            translationUploader.Equals(currentUser, StringComparison.OrdinalIgnoreCase);

                        // Update server state
                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Checked = true,
                            Exists = true,
                            IsOwner = isOwner,
                            Role = isOwner ? TranslationRole.Main : TranslationRole.Branch,
                            MainUsername = isOwner ? null : translationUploader,
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

                        // Save cache (reformats JSON with Formatting.Indented)
                        TranslatorCore.SaveCache();

                        // Save as ancestor for sync tracking
                        TranslatorCore.SaveAncestorCache();
                        HasPendingUpdate = false;
                        PendingUpdateDirection = UpdateDirection.None;

                        TranslatorCore.LogInfo($"[Download] Downloaded translation #{translationId} from @{translationUploader}");

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
                        // Parse remote translations
                        var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(content);
                        var remoteTranslations = new Dictionary<string, string>();

                        foreach (var kvp in parsed)
                        {
                            if (!kvp.Key.StartsWith("_") && kvp.Value is string strValue)
                            {
                                // Normalize line endings for cross-platform consistency
                                string normalizedKey = TranslatorCore.NormalizeLineEndings(kvp.Key);
                                string normalizedValue = TranslatorCore.NormalizeLineEndings(strValue);
                                remoteTranslations[normalizedKey] = normalizedValue;
                            }
                        }

                        // Perform 3-way merge (using string dictionaries for legacy merge support)
                        var local = TranslatorCore.GetCacheAsStrings();
                        var ancestor = TranslatorCore.GetAncestorAsStrings();
                        var mergeResult = TranslationMerger.Merge(local, remoteTranslations, ancestor);

                        TranslatorCore.LogInfo($"[Merge] Result: {mergeResult.Statistics.GetSummary()}");

                        // Update server state to track this translation
                        string currentUser = TranslatorCore.Config.api_user;
                        bool isOwner = !string.IsNullOrEmpty(currentUser) &&
                            translationUploader.Equals(currentUser, StringComparison.OrdinalIgnoreCase);

                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Checked = true,
                            Exists = true,
                            IsOwner = isOwner,
                            Role = isOwner ? TranslationRole.Main : TranslationRole.Branch,
                            MainUsername = isOwner ? null : translationUploader,
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
                            MergePanel?.SetMergeData(mergeResult, remoteTranslations, fileHash);
                            // Don't call onComplete - MergePanel handles the rest
                        }
                        else
                        {
                            // No conflicts - apply merge directly
                            ApplyMerge(mergeResult, fileHash, remoteTranslations);
                            onComplete?.Invoke(true, "Merged successfully!");
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
            if (panelsVisible != _lastPanelVisibleState)
            {
                _lastPanelVisibleState = panelsVisible;
                if (panelsVisible)
                {
                    // Enable cursor unlock - UniverseLib will handle the rest
                    ConfigManager.Force_Unlock_Mouse = true;
                    EventSystemHelper.EnableEventSystem();
                }
                else
                {
                    // Disable cursor unlock - UniverseLib will restore game's cursor state
                    ConfigManager.Force_Unlock_Mouse = false;
                    EventSystemHelper.ReleaseEventSystem();
                }
            }

            // Contextual help bar: resolve the hovered control by geometric poll.
            // Only while a panel is open (nothing to hover otherwise). Event-based hover
            // (injected IPointerEnterHandler) is silent on IL2CPP, so we poll instead.
            if (panelsVisible)
                Components.HelpZone.PollHover();

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
            if (string.IsNullOrEmpty(hotkey))
                return false;

            // Parse hotkey
            bool requireCtrl = hotkey.Contains("Ctrl+");
            bool requireAlt = hotkey.Contains("Alt+");
            bool requireShift = hotkey.Contains("Shift+");

            string baseKey = hotkey
                .Replace("Ctrl+", "")
                .Replace("Alt+", "")
                .Replace("Shift+", "");

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

        // Remembers the last non-"none" backend so Toggle AI can restore it when re-enabling.
        private static string _lastActiveBackend = "llm";

        /// <summary>
        /// Toggles the translation backend on/off (pause/resume all live translation calls).
        /// Does not touch the translation cache — already-translated entries stay.
        /// When turning on, restores the last active backend (defaults to "llm").
        /// </summary>
        private static void ToggleAIHotkey()
        {
            var config = TranslatorCore.Config;

            if (config.translation_backend != "none")
            {
                // Remember current backend so we can restore it later.
                _lastActiveBackend = config.translation_backend;
                config.translation_backend = "none";
                config.enable_ai = false;
                TranslatorCore.ClearQueue();
                ShowHotkeyFeedback("Translation Backend: OFF", false);
            }
            else
            {
                string backend = string.IsNullOrEmpty(_lastActiveBackend) ? "llm" : _lastActiveBackend;
                config.translation_backend = backend;
                config.enable_ai = (backend == "llm");
                TranslatorCore.EnsureWorkerRunning();
                ShowHotkeyFeedback($"Translation Backend: ON ({backend})", true);
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
