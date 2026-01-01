using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UniverseLib;
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
        private static bool _showUI;

        // Update notification state
        public static bool HasPendingUpdate { get; set; } = false;
        public static TranslationCheckResult PendingUpdateInfo { get; set; } = null;
        public static UpdateDirection PendingUpdateDirection { get; set; } = UpdateDirection.None;
        public static bool NotificationDismissed { get; set; } = false;

        // Mod update notification state
        public static bool HasModUpdate { get; set; } = false;
        public static ModUpdateInfo ModUpdateInfo { get; set; } = null;
        public static bool ModUpdateDismissed { get; set; } = false;

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
        /// Initialize the UI system. Called from TranslatorCore after UniverseLib is ready.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized)
                return;

            TranslatorCore.LogInfo("[UIManager] Initializing UniverseLib...");

            Universe.Init(1f, OnUniverseLibInitialized, LogHandler, new UniverseLib.Config.UniverseLibConfig
            {
                Disable_EventSystem_Override = false,
                Force_Unlock_Mouse = false, // We manage cursor ourselves to avoid unlocking when only StatusOverlay is shown
                Unhollowed_Modules_Folder = null
            });
        }

        private static void OnUniverseLibInitialized()
        {
            TranslatorCore.LogInfo("[UIManager] UniverseLib initialized, creating UI...");

            UiBase = UniversalUI.RegisterUI("UnityGameTranslator", UpdateUI);

            CreatePanels();

            _initialized = true;

            // Initialize UI state based on config
            InitializeUIState();
        }

        private static void CreatePanels()
        {
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

            // Hide all panels initially
            WizardPanel.SetActive(false);
            MainPanel.SetActive(false);
            OptionsPanel.SetActive(false);
            LoginPanel.SetActive(false);
            UploadPanel.SetActive(false);
            UploadSetupPanel.SetActive(false);
            MergePanel.SetActive(false);
            LanguagePanel.SetActive(false);
            StatusOverlay.SetActive(false);
            ConfirmationPanel.SetActive(false);
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

            // Fetch server state if online mode is enabled and we have a token
            if (TranslatorCore.Config.online_mode && !string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                await FetchServerState();
            }

            // Then check for translation updates (only if we have server state with a site_id)
            CheckForUpdates();
        }

        #region Server State and Updates

        /// <summary>
        /// Fetch server state for current translation via check-uuid.
        /// Only called if online_mode is enabled and user is authenticated.
        /// </summary>
        private static async Task FetchServerState()
        {
            if (!TranslatorCore.Config.online_mode)
            {
                TranslatorCore.LogInfo("[UIManager] Online mode disabled, skipping server state fetch");
                return;
            }

            if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                TranslatorCore.LogInfo("[UIManager] Not authenticated, skipping server state fetch");
                return;
            }

            try
            {
                TranslatorCore.LogInfo($"[UIManager] Fetching server state for UUID: {TranslatorCore.FileUuid}");
                var result = await ApiClient.CheckUuid(TranslatorCore.FileUuid);

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var error = result.Error;
                var exists = result.Exists;
                var isOwner = result.IsOwner;
                var role = result.Role;
                var mainUsername = result.MainUsername;
                var branchesCount = result.BranchesCount;
                var existingTranslation = result.ExistingTranslation;
                var originalTranslation = result.OriginalTranslation;

                RunOnMainThread(() =>
                {
                    if (!success)
                    {
                        TranslatorCore.LogWarning($"[UIManager] Server state fetch failed: {error}");
                        TranslatorCore.ServerState = new ServerTranslationState { Checked = true };
                        MainPanel?.RefreshUI();
                        return;
                    }

                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        Exists = exists,
                        IsOwner = isOwner,
                        Role = role,
                        MainUsername = mainUsername,
                        BranchesCount = branchesCount,
                        SiteId = existingTranslation?.Id ?? originalTranslation?.Id,
                        Uploader = isOwner ? TranslatorCore.Config.api_user : originalTranslation?.Uploader,
                        Hash = existingTranslation?.FileHash,
                        Type = existingTranslation?.Type ?? originalTranslation?.Type,
                        Notes = existingTranslation?.Notes
                    };

                    TranslatorCore.LogInfo($"[UIManager] Server state: exists={exists}, isOwner={isOwner}, role={role}, siteId={TranslatorCore.ServerState.SiteId}");

                    // Refresh MainPanel if visible to show updated server state
                    MainPanel?.RefreshUI();
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[UIManager] Server state fetch error: {errorMsg}");
                    TranslatorCore.ServerState = new ServerTranslationState { Checked = true };

                    // Refresh MainPanel even on error to update "checking..." status
                    MainPanel?.RefreshUI();
                });
            }
        }

        /// <summary>
        /// Check for translation updates from the server.
        /// </summary>
        public static async void CheckForUpdates()
        {
            // Only check if online mode is enabled
            if (!TranslatorCore.Config.online_mode)
            {
                TranslatorCore.LogInfo("[UpdateCheck] Skipped - online mode disabled");
                return;
            }

            if (!TranslatorCore.Config.sync.check_update_on_start)
            {
                TranslatorCore.LogInfo("[UpdateCheck] Skipped - check_update_on_start disabled");
                return;
            }

            // Need server state with site_id to check for updates
            var serverState = TranslatorCore.ServerState;
            if (serverState?.SiteId == null)
            {
                TranslatorCore.LogInfo("[UpdateCheck] Skipped - no server translation");
                return;
            }

            // Capture values for closure
            var siteId = serverState.SiteId.Value;

            try
            {
                TranslatorCore.LogInfo("[UpdateCheck] Checking for updates...");

                // Compute local content hash to compare with server
                string localHash = TranslatorCore.ComputeContentHash();
                TranslatorCore.LogInfo($"[UpdateCheck] Local hash: {localHash?.Substring(0, 16)}...");

                var result = await ApiClient.CheckUpdate(siteId, localHash);

                // After await, we may be on a background thread (IL2CPP issue)
                var success = result.Success;
                var hasUpdate = result.HasUpdate;
                var fileHash = result.FileHash;
                var lineCount = result.LineCount;
                var error = result.Error;

                RunOnMainThread(() =>
                {
                    TranslatorCore.LogInfo($"[UpdateCheck] Server response: HasUpdate={hasUpdate}, ServerHash={fileHash?.Substring(0, 16)}...");

                    if (success && hasUpdate)
                    {
                        // Determine sync direction based on what changed
                        bool hasLocalChanges = TranslatorCore.LocalChangesCount > 0;

                        // Check if server changed since our last sync
                        string lastSyncedHash = TranslatorCore.LastSyncedHash;
                        bool serverChanged = !string.IsNullOrEmpty(lastSyncedHash) &&
                                             fileHash != lastSyncedHash;

                        // If no LastSyncedHash, we can't tell definitively what changed
                        // If we have local changes AND server hash differs, assume potential conflict to be safe
                        // This ensures merge dialog is shown rather than accidentally overwriting server changes
                        if (string.IsNullOrEmpty(lastSyncedHash))
                        {
                            serverChanged = hasLocalChanges;
                        }

                        // Determine direction based on what changed
                        if (hasLocalChanges && serverChanged)
                        {
                            PendingUpdateDirection = UpdateDirection.Merge;
                            TranslatorCore.LogInfo($"[UpdateCheck] CONFLICT: Both local ({TranslatorCore.LocalChangesCount} changes) and server changed - merge needed");
                        }
                        else if (hasLocalChanges)
                        {
                            PendingUpdateDirection = UpdateDirection.Upload;
                            TranslatorCore.LogInfo($"[UpdateCheck] Local has {TranslatorCore.LocalChangesCount} changes to upload");
                        }
                        else
                        {
                            PendingUpdateDirection = UpdateDirection.Download;
                            TranslatorCore.LogInfo($"[UpdateCheck] Server has update: {lineCount} lines");
                        }

                        HasPendingUpdate = true;
                        PendingUpdateInfo = result;

                        // Auto-download only if no local changes and no conflict
                        if (PendingUpdateDirection == UpdateDirection.Download &&
                            TranslatorCore.Config.sync.auto_download)
                        {
                            TranslatorCore.LogInfo("[UpdateCheck] Auto-downloading update...");
                            // Note: DownloadUpdate is already fixed for IL2CPP threading
                            _ = DownloadUpdate();
                        }

                        // Refresh MainPanel to show updated sync status
                        MainPanel?.RefreshUI();
                    }
                    else if (success)
                    {
                        TranslatorCore.LogInfo("[UpdateCheck] Translation is up to date");
                        HasPendingUpdate = false;
                        PendingUpdateInfo = null;
                        PendingUpdateDirection = UpdateDirection.None;

                        // Refresh MainPanel to show updated sync status
                        MainPanel?.RefreshUI();
                    }
                    else
                    {
                        TranslatorCore.LogWarning($"[UpdateCheck] Failed: {error}");

                        // Refresh MainPanel even on error
                        MainPanel?.RefreshUI();
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                RunOnMainThread(() =>
                {
                    TranslatorCore.LogWarning($"[UpdateCheck] Error: {errorMsg}");

                    // Refresh MainPanel even on error
                    MainPanel?.RefreshUI();
                });
            }
        }

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
                            Notes = translationNotes
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
        /// Hide all panels including status overlay.
        /// </summary>
        public static void HideAll()
        {
            WizardPanel.SetActive(false);
            MainPanel.SetActive(false);
            OptionsPanel.SetActive(false);
            LoginPanel.SetActive(false);
            UploadPanel.SetActive(false);
            MergePanel.SetActive(false);
            LanguagePanel.SetActive(false);
            StatusOverlay.SetActive(false);
            ShowUI = false;
        }

        /// <summary>
        /// Hide all main panels but allow status overlay to remain.
        /// </summary>
        public static void HideMainPanels()
        {
            WizardPanel.SetActive(false);
            MainPanel.SetActive(false);
            OptionsPanel.SetActive(false);
            LoginPanel.SetActive(false);
            UploadPanel.SetActive(false);
            MergePanel.SetActive(false);
            LanguagePanel.SetActive(false);
        }

        private static bool AnyPanelVisible()
        {
            return WizardPanel.Enabled || MainPanel.Enabled || OptionsPanel.Enabled ||
                   LoginPanel.Enabled || UploadPanel.Enabled || UploadSetupPanel.Enabled ||
                   MergePanel.Enabled || LanguagePanel.Enabled;
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
