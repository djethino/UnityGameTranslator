using System;
using System.Collections;
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
        public static void RunOnMainThread(Action action)
        {
            if (action == null) return;
            RuntimeHelper.StartCoroutine(RunOnMainThreadCoroutine(action));
        }

        private static IEnumerator RunOnMainThreadCoroutine(Action action)
        {
            yield return null; // Wait one frame to ensure we're on main thread
            try
            {
                action();
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[UIManager] RunOnMainThread error: {e.Message}");
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

            // Configure UniverseLib default colors to match our theme (navy blue)
            UIFactory.Colors.DefaultLayoutBackground = UIStyles.ViewportBackground;
            UIFactory.Colors.DefaultLayoutPadding = new Vector4(
                UIStyles.SmallSpacing, UIStyles.SmallSpacing,
                UIStyles.SmallSpacing, UIStyles.SmallSpacing);

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

            CreatePanels();

            _initialized = true;

            // Notify listeners (e.g., TranslatorPatches to retry failed font replacements)
            try { OnInitialized?.Invoke(); } catch { }

            // Initialize UI state based on config
            InitializeUIState();
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
                TranslatorCore.LogInfo($"[UIManager] Restored API token for user: {TranslatorCore.Config.api_user ?? "unknown"}");
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
            }
        }

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
                var data = JObject.Parse(jsonData);

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
                }
                else if (main != null && main.Type != JTokenType.Null)
                {
                    serverState.SiteId = main["id"]?.Value<int>();
                    serverState.Uploader = main["uploader"]?.Value<string>();
                    serverState.MainUsername = main["uploader"]?.Value<string>();
                    serverState.Hash = main["file_hash"]?.Value<string>();
                }

                TranslatorCore.ServerState = serverState;

                TranslatorCore.LogInfo($"[SyncSSE] State: exists={exists}, role={role}, siteId={serverState.SiteId}");

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
                var data = JObject.Parse(jsonData);

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
            bool hasLocalChanges = TranslatorCore.LocalChangesCount > 0;

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
                var data = JObject.Parse(jsonData);
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

                var result = await GitHubUpdateChecker.CheckForUpdatesAsync(currentVersion, modLoaderType);

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
                            // Show merge panel for user to resolve conflicts
                            MergePanel?.SetMergeDataWithTags(mergeResult, remoteTranslations, fileHash);
                            MergePanel?.SetActive(true);
                        }
                        else
                        {
                            // No conflicts - apply merge directly
                            ApplyMergeWithTags(mergeResult, fileHash, remoteTranslations);
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
                // Full tag support will be added when TranslationMerger is updated
                TranslatorCore.TranslationCache[kvp.Key] = new TranslationEntry
                {
                    Value = kvp.Value,
                    Tag = "A"  // TODO: Preserve original tags when merger is updated
                };
            }

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
                            SourceLanguage = translationSourceLang,
                            TargetLanguage = translationTargetLang
                        };

                        // Save as ancestor for sync tracking
                        TranslatorCore.SaveAncestorCache();

                        // Update sync state
                        TranslatorCore.LastSyncedHash = fileHash ?? translationFileHash;
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
                            Notes = translationNotes
                        };

                        if (mergeResult.ConflictCount > 0)
                        {
                            // Show merge panel for user to resolve conflicts
                            MergePanel?.SetMergeData(mergeResult, remoteTranslations, fileHash);
                            MergePanel?.SetActive(true);
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
        /// Open the Inspector Panel for selecting UI elements to exclude.
        /// </summary>
        public static void OpenInspectorPanel()
        {
            if (InspectorPanel == null) return;
            ShowUI = true;
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

            // Manage status overlay visibility
            UpdateStatusOverlay();
        }

        private static void UpdateStatusOverlay()
        {
            if (StatusOverlay == null) return;

            // Determine what should be shown
            bool panelsOpen = AnyPanelVisible();
            bool firstRunDone = TranslatorCore.Config.first_run_completed;

            // Ollama queue is ALWAYS visible when translating (even with panels open)
            bool ollamaActive = TranslatorCore.Config.enable_ollama &&
                               (TranslatorCore.QueueCount > 0 || TranslatorCore.IsTranslating);

            // Other notifications only show when no panels are open
            // (mod update and sync are now shown in MainPanel)
            bool hasOtherContent = !panelsOpen && StatusOverlay.HasNotificationContent();

            bool shouldShow = firstRunDone && (ollamaActive || hasOtherContent);

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

        private static void CheckHotkey()
        {
            // Skip hotkey check during wizard
            if (WizardPanel != null && WizardPanel.Enabled)
                return;

            string hotkey = TranslatorCore.Config.settings_hotkey;
            if (string.IsNullOrEmpty(hotkey))
                return;

            // Parse hotkey
            bool requireCtrl = hotkey.Contains("Ctrl+");
            bool requireAlt = hotkey.Contains("Alt+");
            bool requireShift = hotkey.Contains("Shift+");

            string baseKey = hotkey
                .Replace("Ctrl+", "")
                .Replace("Alt+", "")
                .Replace("Shift+", "");

            if (!Enum.TryParse<KeyCode>(baseKey, true, out KeyCode keyCode))
                return;

            // Check if hotkey is pressed
            if (UniverseLib.Input.InputManager.GetKeyDown(keyCode))
            {
                bool ctrlHeld = UniverseLib.Input.InputManager.GetKey(KeyCode.LeftControl) ||
                               UniverseLib.Input.InputManager.GetKey(KeyCode.RightControl);
                bool altHeld = UniverseLib.Input.InputManager.GetKey(KeyCode.LeftAlt) ||
                              UniverseLib.Input.InputManager.GetKey(KeyCode.RightAlt);
                bool shiftHeld = UniverseLib.Input.InputManager.GetKey(KeyCode.LeftShift) ||
                                UniverseLib.Input.InputManager.GetKey(KeyCode.RightShift);

                if (ctrlHeld == requireCtrl && altHeld == requireAlt && shiftHeld == requireShift)
                {
                    ToggleMain();
                }
            }
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
