using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Helper class for consistent IMGUI window rendering.
    /// Provides reusable patterns for header, scrolling, and layout.
    /// </summary>
    public static class WindowHelper
    {
        private static GUIStyle boxStyle;
        private static GUIStyle titleStyle;
        private static GUIStyle buttonStyle;
        private static GUIStyle labelStyle;
        private static bool stylesReady = false;

        private static void EnsureStyles()
        {
            if (stylesReady) return;

            boxStyle = new GUIStyle(GUI.skin.box)
            {
                padding = new RectOffset(15, 15, 15, 15)
            };

            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };

            buttonStyle = new GUIStyle(GUI.skin.button)
            {
                fontSize = 14,
                padding = new RectOffset(15, 15, 8, 8)
            };

            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                wordWrap = true
            };

            stylesReady = true;
        }

        public static GUIStyle BoxStyle { get { EnsureStyles(); return boxStyle; } }
        public static GUIStyle TitleStyle { get { EnsureStyles(); return titleStyle; } }
        public static GUIStyle ButtonStyle { get { EnsureStyles(); return buttonStyle; } }
        public static GUIStyle LabelStyle { get { EnsureStyles(); return labelStyle; } }

        /// <summary>
        /// Calculate max height for a window (80% of screen by default).
        /// </summary>
        public static float GetMaxHeight(float preferredHeight, float screenRatio = 0.8f)
        {
            return Mathf.Min(preferredHeight, Screen.height * screenRatio);
        }

        /// <summary>
        /// Center and constrain a window rect to the screen.
        /// </summary>
        public static void CenterWindow(ref Rect rect, float width, float height)
        {
            rect.width = width;
            rect.height = height;
            rect.x = (Screen.width - width) / 2;
            rect.y = (Screen.height - height) / 2;
        }

        /// <summary>
        /// Begin a standard window with box style.
        /// </summary>
        public static void BeginWindow()
        {
            EnsureStyles();
            GUILayout.BeginVertical(boxStyle);
        }

        /// <summary>
        /// Draw standard window header with title and optional close button.
        /// </summary>
        /// <returns>True if close button was clicked</returns>
        public static bool DrawHeader(string title, bool showCloseButton = true)
        {
            EnsureStyles();
            bool closeClicked = false;

            GUILayout.BeginHorizontal();
            GUILayout.Label(title, titleStyle);
            if (showCloseButton)
            {
                if (GUILayout.Button("X", GUILayout.Width(30), GUILayout.Height(25)))
                {
                    closeClicked = true;
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(10);

            return closeClicked;
        }

        /// <summary>
        /// Begin a scrollable content area with max height.
        /// </summary>
        public static Vector2 BeginScrollContent(Vector2 scrollPos, float? maxHeight = null)
        {
            if (maxHeight.HasValue)
            {
                return GUILayout.BeginScrollView(scrollPos, GUILayout.MaxHeight(maxHeight.Value));
            }
            return GUILayout.BeginScrollView(scrollPos);
        }

        /// <summary>
        /// End the scrollable content area.
        /// </summary>
        public static void EndScrollContent()
        {
            GUILayout.EndScrollView();
        }

        /// <summary>
        /// Add spacing before bottom buttons section.
        /// </summary>
        public static void BeginBottomButtons()
        {
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
        }

        /// <summary>
        /// End bottom buttons section.
        /// </summary>
        public static void EndBottomButtons()
        {
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// End the window and make it draggable.
        /// </summary>
        public static void EndWindow()
        {
            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 30));
        }

        /// <summary>
        /// Draw a standard button with consistent styling.
        /// </summary>
        public static bool Button(string text, float height = 35f, float? width = null)
        {
            EnsureStyles();
            if (width.HasValue)
            {
                return GUILayout.Button(text, buttonStyle, GUILayout.Height(height), GUILayout.Width(width.Value));
            }
            return GUILayout.Button(text, buttonStyle, GUILayout.Height(height));
        }

        /// <summary>
        /// Draw a label with standard styling.
        /// </summary>
        public static void Label(string text, bool bold = false, bool italic = false, int? fontSize = null)
        {
            EnsureStyles();
            GUIStyle style = new GUIStyle(labelStyle);
            if (bold) style.fontStyle = FontStyle.Bold;
            if (italic) style.fontStyle = bold ? FontStyle.BoldAndItalic : FontStyle.Italic;
            if (fontSize.HasValue) style.fontSize = fontSize.Value;
            GUILayout.Label(text, style);
        }
    }

    /// <summary>
    /// IMGUI-based overlay system for UnityGameTranslator.
    /// Handles first-run wizard, settings overlay, and status display.
    /// </summary>
    public static class TranslatorUI
    {
        // Wizard state
        public enum WizardStep
        {
            Welcome,
            OnlineMode,
            Hotkey,
            TranslationChoice,
            OllamaConfig,
            Complete
        }

        // UI State
        public static bool ShowWizard { get; set; } = false;
        public static bool ShowSettings { get; set; } = false;
        public static bool ShowStatus { get; set; } = true;
        public static WizardStep CurrentStep { get; set; } = WizardStep.Welcome;

        // Wizard state
        private static bool wizardOnlineMode = false;
        private static string wizardHotkey = "F10";
        private static bool wizardHotkeyCtrl = false;
        private static bool wizardHotkeyAlt = false;
        private static bool wizardHotkeyShift = false;
        private static bool waitingForHotkey = false;
        private static bool wizardEnableOllama = false;
        private static string wizardOllamaUrl = "http://localhost:11434";
        private static string ollamaTestStatus = "";

        // Settings state (temp values while editing)
        private static bool settingsOnlineMode;
        private static bool settingsEnableTranslations;
        private static bool settingsEnableOllama;
        private static string settingsOllamaUrl;
        private static string settingsModel;
        private static string settingsGameContext;
        private static string settingsTargetLanguage;
        private static int settingsTargetLanguageIndex;
        private static bool settingsCheckUpdates;
        private static bool settingsAutoDownload;
        private static bool settingsNotifyUpdates;
        private static string settingsHotkey;
        private static bool settingsHotkeyWaiting;
        private static bool settingsHotkeyCtrl;
        private static bool settingsHotkeyAlt;
        private static bool settingsHotkeyShift;
        private static bool settingsInitialized = false;

        // Target language list with "auto" as first option
        private static string[] targetLanguageOptions = null;

        private static void InitTargetLanguageOptions()
        {
            if (targetLanguageOptions == null)
            {
                var langs = LanguageHelper.GetLanguageNames();
                targetLanguageOptions = new string[langs.Length + 1];
                targetLanguageOptions[0] = "auto (System)";
                for (int i = 0; i < langs.Length; i++)
                {
                    targetLanguageOptions[i + 1] = langs[i];
                }
            }
        }

        private static int GetTargetLanguageIndex(string lang)
        {
            InitTargetLanguageOptions();
            if (string.IsNullOrEmpty(lang) || lang == "auto")
                return 0;
            for (int i = 1; i < targetLanguageOptions.Length; i++)
            {
                if (targetLanguageOptions[i] == lang)
                    return i;
            }
            return 0;
        }

        private static string GetTargetLanguageFromIndex(int index)
        {
            InitTargetLanguageOptions();
            if (index <= 0 || index >= targetLanguageOptions.Length)
                return "auto";
            return targetLanguageOptions[index];
        }

        // Translation search state (for wizard and update checks)
        private static bool isSearchingTranslations = false;
        private static string searchStatus = "";
        private static List<TranslationInfo> availableTranslations = new List<TranslationInfo>();
        private static TranslationInfo selectedTranslation = null;
        private static bool isDownloading = false;
        private static string downloadStatus = "";
        private static GameInfo detectedGame = null;

        // Update notification state
        public enum UpdateDirection { None, Download, Upload, Merge }
        public static bool HasPendingUpdate { get; private set; } = false;
        public static TranslationCheckResult PendingUpdateInfo { get; private set; } = null;
        public static UpdateDirection PendingUpdateDirection { get; private set; } = UpdateDirection.None;
        private static bool notificationDismissed = false; // User dismissed the notification for this session

        // Merge dialog state
        private static bool showMergeDialog = false;
        private static MergeResult pendingMergeResult = null;
        private static Dictionary<string, string> remoteTranslations = null;
        private static Vector2 mergeScrollPos = Vector2.zero;
        private static Vector2 settingsScrollPos = Vector2.zero;
        private static Vector2 mainScrollPos = Vector2.zero;
        private static Dictionary<string, ConflictResolution> conflictResolutions = new Dictionary<string, ConflictResolution>();

        // Login flow state
        private static bool showLoginDialog = false;
        private static string loginDeviceCode = null;
        private static string loginUserCode = null;
        private static string loginVerificationUri = null;
        private static bool isPollingLogin = false;
        private static string loginStatus = "";

        // Upload dialog state
        private static bool showUploadDialog = false;
        private static string uploadType = "ai";
        private static string uploadNotes = "";
        private static bool isUploading = false;
        private static string uploadStatus = "";
        private static Vector2 uploadScrollPos = Vector2.zero;
        private static bool isExistingTranslation = false;
        private static bool isFork = false;

        // Language selection dialog state (for NEW uploads)
        private static bool showLanguageDialog = false;
        private static string selectedSourceLanguage = "English";
        private static string selectedTargetLanguage = "";
        private static int sourceLanguageIndex = 0;
        private static int targetLanguageIndex = 0;
        private static string[] languageList = null;

        // Game search state
        private static string gameSearchQuery = "";
        private static bool isSearchingGames = false;
        private static List<GameApiInfo> gameSearchResults = null;
        private static Vector2 gameSearchScrollPos = Vector2.zero;
        private static GameInfo selectedGame = null; // User-selected game (overrides auto-detect)

        // TextField style (other styles are in WindowHelper)
        private static GUIStyle textFieldStyle;
        private static bool stylesInitialized = false;

        // Window IDs
        private const int WIZARD_WINDOW_ID = 98765;
        private const int MAIN_WINDOW_ID = 98766;
        private const int MERGE_WINDOW_ID = 98767;
        private const int LOGIN_WINDOW_ID = 98768;
        private const int UPLOAD_WINDOW_ID = 98769;
        private const int LANGUAGE_WINDOW_ID = 98770;
        private const int OPTIONS_WINDOW_ID = 98771;

        // Window rects
        private static Rect wizardRect = new Rect(0, 0, 500, 400);
        private static Rect mainRect = new Rect(0, 0, 450, 380);
        private static Rect mergeRect = new Rect(0, 0, 650, 500);
        private static Rect loginRect = new Rect(0, 0, 420, 340);
        private static Rect uploadRect = new Rect(0, 0, 450, 350);
        private static Rect languageRect = new Rect(0, 0, 500, 500);
        private static Rect optionsRect = new Rect(0, 0, 500, 520);

        // Options dialog state
        private static bool showOptionsDialog = false;

        /// <summary>
        /// Initialize UI state based on config
        /// </summary>
        public static void Initialize()
        {
            TranslatorCore.LogInfo($"[UI] Initialize called, first_run_completed={TranslatorCore.Config.first_run_completed}");

            // Restore API token if saved from previous session
            if (!string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                ApiClient.SetAuthToken(TranslatorCore.Config.api_token);
                TranslatorCore.LogInfo($"[UI] Restored API token for user: {TranslatorCore.Config.api_user ?? "unknown"}");
            }

            if (!TranslatorCore.Config.first_run_completed)
            {
                ShowWizard = true;
                CurrentStep = WizardStep.Welcome;
                TranslatorCore.LogInfo("[UI] First run - showing wizard");
            }
            else
            {
                TranslatorCore.LogInfo("[UI] Not first run - fetching server state");
                // Fetch server state and check for updates after a short delay
                TriggerStartupTasks();
            }
        }

        private static async void TriggerStartupTasks()
        {
            // Wait a bit to let the game initialize
            await Task.Delay(3000);

            // Fetch server state if online mode is enabled and we have a token
            if (TranslatorCore.Config.online_mode && !string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                await FetchServerState();
            }

            // Then check for updates (only if we have server state with a site_id)
            CheckForUpdates();
        }

        /// <summary>
        /// Fetch server state for current translation via check-uuid.
        /// Only called if online_mode is enabled and user is authenticated.
        /// </summary>
        private static async Task FetchServerState()
        {
            if (!TranslatorCore.Config.online_mode)
            {
                TranslatorCore.LogInfo("[UI] Online mode disabled, skipping server state fetch");
                return;
            }

            if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                TranslatorCore.LogInfo("[UI] Not authenticated, skipping server state fetch");
                return;
            }

            try
            {
                TranslatorCore.LogInfo($"[UI] Fetching server state for UUID: {TranslatorCore.FileUuid}");
                var result = await ApiClient.CheckUuid(TranslatorCore.FileUuid);

                if (!result.Success)
                {
                    TranslatorCore.LogWarning($"[UI] Server state fetch failed: {result.Error}");
                    TranslatorCore.ServerState = new ServerTranslationState { Checked = true };
                    return;
                }

                TranslatorCore.ServerState = new ServerTranslationState
                {
                    Checked = true,
                    Exists = result.Exists,
                    IsOwner = result.IsOwner,
                    SiteId = result.ExistingTranslation?.Id ?? result.OriginalTranslation?.Id,
                    Uploader = result.IsOwner ? TranslatorCore.Config.api_user : result.OriginalTranslation?.Uploader,
                    Hash = result.ExistingTranslation?.FileHash,
                    Type = result.ExistingTranslation?.Type ?? result.OriginalTranslation?.Type,
                    Notes = result.ExistingTranslation?.Notes
                };

                TranslatorCore.LogInfo($"[UI] Server state: exists={result.Exists}, isOwner={result.IsOwner}, siteId={TranslatorCore.ServerState.SiteId}");
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[UI] Server state fetch error: {e.Message}");
                TranslatorCore.ServerState = new ServerTranslationState { Checked = true };
            }
        }

        /// <summary>
        /// Check for hotkey press using Event.current.
        /// Works with both Legacy Input and New Input System.
        /// Called from OnGUI().
        /// </summary>
        private static void CheckHotkey()
        {
            if (ShowWizard) return;

            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown) return;

            string hotkey = TranslatorCore.Config.settings_hotkey;
            if (string.IsNullOrEmpty(hotkey)) return;

            try
            {
                bool requireCtrl = hotkey.Contains("Ctrl+");
                bool requireAlt = hotkey.Contains("Alt+");
                bool requireShift = hotkey.Contains("Shift+");

                string baseKey = hotkey
                    .Replace("Ctrl+", "")
                    .Replace("Alt+", "")
                    .Replace("Shift+", "");

                KeyCode keyCode = (KeyCode)Enum.Parse(typeof(KeyCode), baseKey, true);

                if (e.keyCode == keyCode)
                {
                    // Check modifiers match
                    if (e.control == requireCtrl && e.alt == requireAlt && e.shift == requireShift)
                    {
                        ShowSettings = !ShowSettings;
                        if (ShowSettings)
                        {
                            InitializeSettingsValues();
                        }
                        e.Use(); // Consume the event
                    }
                }
            }
            catch { }
        }

        // Dark overlay texture (created once)
        private static Texture2D darkOverlayTexture;
        private static bool onGuiLoggedOnce = false;

        /// <summary>
        /// Main OnGUI entry point. Call from mod loader's OnGUI().
        /// </summary>
        public static void OnGUI()
        {
            // Log once to confirm OnGUI is being called
            if (!onGuiLoggedOnce)
            {
                TranslatorCore.LogInfo($"[UI] OnGUI called for first time, ShowWizard={ShowWizard}, ShowSettings={ShowSettings}");
                onGuiLoggedOnce = true;
            }

            InitStyles();

            // Hotkey check using Event.current (works with both input systems)
            CheckHotkey();

            // Check if any modal dialog is open
            bool showDarkOverlay = ShowWizard || showLoginDialog || showUploadDialog || showMergeDialog || ShowSettings || showLanguageDialog || showOptionsDialog;

            if (showDarkOverlay)
            {
                DrawDarkOverlay();
            }

            // Draw base window (wizard, main, or status)
            if (ShowWizard && !showLoginDialog)
            {
                WindowHelper.CenterWindow(ref wizardRect, 500, GetWizardHeight());
                wizardRect = TranslatorCore.Adapter.DrawWindow(WIZARD_WINDOW_ID, wizardRect, DrawWizardWindow, "");
            }
            else if (showMergeDialog)
            {
                WindowHelper.CenterWindow(ref mergeRect, 650, 500);
                mergeRect = TranslatorCore.Adapter.DrawWindow(MERGE_WINDOW_ID, mergeRect, DrawMergeWindow, "");
            }
            else if (ShowSettings && !showLoginDialog && !showUploadDialog && !showOptionsDialog)
            {
                float mainHeight = GetMainWindowHeight();
                float maxHeight = WindowHelper.GetMaxHeight(mainHeight);
                WindowHelper.CenterWindow(ref mainRect, 450, maxHeight);
                mainRect = TranslatorCore.Adapter.DrawWindow(MAIN_WINDOW_ID, mainRect, DrawMainWindow, "");
            }
            else if (!showLoginDialog && !showUploadDialog && ShowStatus)
            {
                DrawStatusOverlay();
            }

            // Options dialog (overlays main window)
            if (showOptionsDialog)
            {
                float maxHeight = WindowHelper.GetMaxHeight(550);
                WindowHelper.CenterWindow(ref optionsRect, 500, maxHeight);
                optionsRect = TranslatorCore.Adapter.DrawWindow(OPTIONS_WINDOW_ID, optionsRect, DrawOptionsWindow, "");
            }

            // Modal dialogs that can overlay wizard/main
            if (showLoginDialog)
            {
                WindowHelper.CenterWindow(ref loginRect, 420, 340);
                loginRect = TranslatorCore.Adapter.DrawWindow(LOGIN_WINDOW_ID, loginRect, DrawLoginWindow, "");
            }
            else if (showUploadDialog)
            {
                float maxHeight = WindowHelper.GetMaxHeight(450);
                WindowHelper.CenterWindow(ref uploadRect, 450, maxHeight);
                uploadRect = TranslatorCore.Adapter.DrawWindow(UPLOAD_WINDOW_ID, uploadRect, DrawUploadWindow, "");
            }

            // Language selection dialog (shown before upload for NEW translations)
            if (showLanguageDialog)
            {
                float dialogHeight = WindowHelper.GetMaxHeight(550, 0.85f);
                WindowHelper.CenterWindow(ref languageRect, 500, dialogHeight);
                languageRect = TranslatorCore.Adapter.DrawWindow(LANGUAGE_WINDOW_ID, languageRect, DrawLanguageSelectionWindow, "");
            }
        }

        /// <summary>
        /// Draw a semi-transparent dark overlay behind modal dialogs.
        /// </summary>
        private static void DrawDarkOverlay()
        {
            // Create texture once
            if (darkOverlayTexture == null)
            {
                darkOverlayTexture = new Texture2D(1, 1);
                darkOverlayTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.7f)); // 70% opacity black
                darkOverlayTexture.Apply();
            }

            // Draw fullscreen overlay
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), darkOverlayTexture);
        }

        private static void InitStyles()
        {
            if (stylesInitialized) return;

            textFieldStyle = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 14
            };

            stylesInitialized = true;
        }

        private static int GetWizardHeight()
        {
            switch (CurrentStep)
            {
                case WizardStep.Welcome: return 250;
                case WizardStep.OnlineMode: return 350;
                case WizardStep.Hotkey: return 300;
                case WizardStep.TranslationChoice:
                    // Dynamic height based on content
                    int baseHeight = 280;
                    if (TranslatorCore.TranslationCache.Count > 0) baseHeight += 60; // Local translations box
                    if (availableTranslations.Count > 0) baseHeight += 40 + Math.Min(3, availableTranslations.Count) * 55; // Online list
                    return Math.Min(550, baseHeight); // Cap at 550
                case WizardStep.OllamaConfig: return 380;
                default: return 300;
            }
        }

        #region Wizard

        private static void DrawWizardWindow(int windowId)
        {
            WindowHelper.BeginWindow();

            switch (CurrentStep)
            {
                case WizardStep.Welcome:
                    DrawWizardWelcome();
                    break;
                case WizardStep.OnlineMode:
                    DrawWizardOnlineMode();
                    break;
                case WizardStep.Hotkey:
                    DrawWizardHotkey();
                    break;
                case WizardStep.TranslationChoice:
                    DrawWizardTranslationChoice();
                    break;
                case WizardStep.OllamaConfig:
                    DrawWizardOllamaConfig();
                    break;
            }

            WindowHelper.EndWindow();
        }

        private static void DrawWizardWelcome()
        {
            GUILayout.Label("Welcome to UnityGameTranslator!", WindowHelper.TitleStyle);
            GUILayout.Space(20);

            WindowHelper.Label(
                "This mod automatically translates Unity games using AI.\n\n" +
                "You can either:\n" +
                "- Download community translations from our website\n" +
                "- Generate translations locally using Ollama AI\n" +
                "- Or both!");

            GUILayout.FlexibleSpace();

            if (WindowHelper.Button("Continue", height: 40))
            {
                CurrentStep = WizardStep.OnlineMode;
            }
        }

        private static void DrawWizardOnlineMode()
        {
            GUILayout.Label("Online Mode", WindowHelper.TitleStyle);
            GUILayout.Space(15);

            WindowHelper.Label("Do you want to enable online features?");
            GUILayout.Space(10);

            // Online mode option
            GUILayout.BeginHorizontal();
            bool newOnline = GUILayout.Toggle(wizardOnlineMode, "", GUILayout.Width(20));
            if (newOnline != wizardOnlineMode) wizardOnlineMode = newOnline;
            GUILayout.BeginVertical();
            WindowHelper.Label("Enable Online Mode", bold: true);
            WindowHelper.Label("- Download community translations\n- Share your translations\n- Check for updates");
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.Space(15);

            // Offline mode option
            GUILayout.BeginHorizontal();
            bool newOffline = GUILayout.Toggle(!wizardOnlineMode, "", GUILayout.Width(20));
            if (newOffline && wizardOnlineMode) wizardOnlineMode = false;
            GUILayout.BeginVertical();
            WindowHelper.Label("Stay Offline", bold: true);
            WindowHelper.Label("- Use only local Ollama AI\n- No internet connection\n- Full privacy");
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();

            if (WindowHelper.Button("Continue", height: 40))
            {
                CurrentStep = WizardStep.Hotkey;
            }
        }

        private static void DrawWizardHotkey()
        {
            GUILayout.Label("Settings Hotkey", WindowHelper.TitleStyle);
            GUILayout.Space(15);

            WindowHelper.Label("Choose a key to open the settings menu:");
            GUILayout.Space(10);

            // Modifier toggles
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            wizardHotkeyCtrl = GUILayout.Toggle(wizardHotkeyCtrl, "Ctrl", GUILayout.Width(50));
            wizardHotkeyAlt = GUILayout.Toggle(wizardHotkeyAlt, "Alt", GUILayout.Width(45));
            wizardHotkeyShift = GUILayout.Toggle(wizardHotkeyShift, "Shift", GUILayout.Width(50));
            GUILayout.Label("+", GUILayout.Width(20));

            if (waitingForHotkey)
            {
                GUILayout.Label("Press key...", new GUIStyle(WindowHelper.LabelStyle)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter
                }, GUILayout.Width(100), GUILayout.Height(30));

                // Detect key press (ignore modifier keys)
                Event e = Event.current;
                if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None &&
                    e.keyCode != KeyCode.LeftControl && e.keyCode != KeyCode.RightControl &&
                    e.keyCode != KeyCode.LeftAlt && e.keyCode != KeyCode.RightAlt &&
                    e.keyCode != KeyCode.LeftShift && e.keyCode != KeyCode.RightShift)
                {
                    wizardHotkey = e.keyCode.ToString();
                    waitingForHotkey = false;
                }
            }
            else
            {
                if (WindowHelper.Button(wizardHotkey, height: 30, width: 100))
                {
                    waitingForHotkey = true;
                }
            }

            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Show current hotkey preview
            string hotkeyPreview = "";
            if (wizardHotkeyCtrl) hotkeyPreview += "Ctrl+";
            if (wizardHotkeyAlt) hotkeyPreview += "Alt+";
            if (wizardHotkeyShift) hotkeyPreview += "Shift+";
            hotkeyPreview += wizardHotkey;
            WindowHelper.Label($"Current: {hotkeyPreview}", italic: true, fontSize: 12);

            GUILayout.FlexibleSpace();

            if (WindowHelper.Button("Continue", height: 40))
            {
                // Build and save hotkey with modifiers
                string fullHotkey = "";
                if (wizardHotkeyCtrl) fullHotkey += "Ctrl+";
                if (wizardHotkeyAlt) fullHotkey += "Alt+";
                if (wizardHotkeyShift) fullHotkey += "Shift+";
                fullHotkey += wizardHotkey;
                TranslatorCore.Config.settings_hotkey = fullHotkey;

                if (wizardOnlineMode)
                {
                    CurrentStep = WizardStep.TranslationChoice;
                    // Search is triggered in DrawWizardTranslationChoice when game is detected
                }
                else
                {
                    CurrentStep = WizardStep.OllamaConfig;
                }
            }
        }

        private static void DrawWizardTranslationChoice()
        {
            GUILayout.Label("Community Translations", WindowHelper.TitleStyle);
            GUILayout.Space(10);

            // Detect game if not already done
            if (detectedGame == null)
            {
                detectedGame = GameDetector.DetectGame();
                if (detectedGame != null && !isSearchingTranslations)
                {
                    SearchForTranslations();
                }
            }

            // Show detected game
            if (detectedGame != null)
            {
                WindowHelper.Label($"Game: {detectedGame.name}", bold: true);
            }
            else
            {
                WindowHelper.Label("Could not detect game");
            }

            // Show existing local translations if any
            int localCount = TranslatorCore.TranslationCache.Count;
            if (localCount > 0)
            {
                GUILayout.Space(8);
                GUILayout.BeginVertical(GUI.skin.box);
                WindowHelper.Label($"You already have {localCount} local translations", bold: true);
                var serverState = TranslatorCore.ServerState;
                if (serverState != null && serverState.Exists && !string.IsNullOrEmpty(serverState.Uploader))
                {
                    WindowHelper.Label($"Source: {serverState.Uploader}", fontSize: 11);
                }
                GUILayout.EndVertical();
            }

            GUILayout.Space(8);

            // Optional login section
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            if (!isLoggedIn)
            {
                GUILayout.BeginHorizontal();
                WindowHelper.Label("Want to sync your translations?", fontSize: 11);
                if (GUILayout.Button("Connect (optional)", GUILayout.Width(130), GUILayout.Height(22)))
                {
                    ShowLogin();
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(5);
            }
            else
            {
                string currentUser = TranslatorCore.Config.api_user;
                WindowHelper.Label($"Connected as @{currentUser}", italic: true, fontSize: 11);

                // Check sync status if we have local translations
                var wizardServerState = TranslatorCore.ServerState;
                if (localCount > 0 && wizardServerState != null && wizardServerState.Exists)
                {
                    bool hasSiteId = wizardServerState.SiteId.HasValue;
                    bool isOwner = wizardServerState.IsOwner;

                    if (isOwner)
                    {
                        // Check for local changes
                        bool hasAncestor = TranslatorCore.AncestorCache.Count > 0;
                        int localChanges = TranslatorCore.LocalChangesCount;

                        if (hasAncestor && localChanges > 0)
                        {
                            WindowHelper.Label($"Your translation has {localChanges} unsaved changes", fontSize: 11);
                        }
                        else if (hasAncestor)
                        {
                            WindowHelper.Label("Your translation is synced with website", fontSize: 11);
                        }
                        else
                        {
                            WindowHelper.Label("This is your translation (ready to update)", fontSize: 11);
                        }
                    }
                    else if (hasSiteId)
                    {
                        WindowHelper.Label($"Downloaded from @{wizardServerState.Uploader} - can fork to upload", fontSize: 11);
                    }
                }
                GUILayout.Space(5);
            }

            // Search status
            if (isSearchingTranslations)
            {
                WindowHelper.Label("Searching online...");
            }
            else if (!string.IsNullOrEmpty(searchStatus))
            {
                WindowHelper.Label(searchStatus);
            }

            // Show available translations
            if (availableTranslations.Count > 0)
            {
                GUILayout.Space(8);
                WindowHelper.Label($"Found {availableTranslations.Count} translation(s) online:");

                // Display top 3 translations
                int displayCount = Math.Min(3, availableTranslations.Count);
                for (int i = 0; i < displayCount; i++)
                {
                    var t = availableTranslations[i];
                    bool isSelected = selectedTranslation == t;

                    // Check if this is user's own translation
                    bool isOwnTranslation = isLoggedIn &&
                        !string.IsNullOrEmpty(TranslatorCore.Config.api_user) &&
                        t.Uploader.Equals(TranslatorCore.Config.api_user, StringComparison.OrdinalIgnoreCase);

                    // Check if same as local source
                    var localServerState = TranslatorCore.ServerState;
                    bool isSameAsLocal = localServerState != null &&
                        localServerState.Exists &&
                        !string.IsNullOrEmpty(localServerState.Uploader) &&
                        t.Uploader.Equals(localServerState.Uploader, StringComparison.OrdinalIgnoreCase);

                    GUILayout.BeginHorizontal(GUI.skin.box);
                    if (GUILayout.Toggle(isSelected, "", GUILayout.Width(20)))
                    {
                        selectedTranslation = t;
                    }
                    GUILayout.BeginVertical();
                    string label = $"{t.TargetLanguage} by {t.Uploader}";
                    if (isOwnTranslation) label += " (you)";
                    else if (isSameAsLocal) label += " (current)";
                    WindowHelper.Label(label, bold: true);
                    WindowHelper.Label($"{t.LineCount} lines | +{t.VoteCount} votes | {t.Type}", fontSize: 11);
                    GUILayout.EndVertical();
                    GUILayout.EndHorizontal();
                }

                if (selectedTranslation == null && availableTranslations.Count > 0)
                {
                    selectedTranslation = availableTranslations[0];
                }
            }

            // Download status
            if (isDownloading)
            {
                GUILayout.Space(5);
                WindowHelper.Label(downloadStatus);
            }

            // Buttons
            GUILayout.FlexibleSpace();

            if (availableTranslations.Count > 0)
            {
                GUI.enabled = selectedTranslation != null && !isDownloading;
                if (WindowHelper.Button("Download Selected Translation", height: 32))
                {
                    DownloadSelectedTranslation();
                }
                GUI.enabled = true;
                GUILayout.Space(8);
            }

            if (WindowHelper.Button("Configure Ollama Instead", height: 32))
            {
                CurrentStep = WizardStep.OllamaConfig;
            }

            GUILayout.Space(8);

            if (WindowHelper.Button(localCount > 0 ? "Keep Current & Continue" : "Skip for Now", height: 32))
            {
                CompleteWizard();
            }
        }

        private static async void SearchForTranslations()
        {
            if (isSearchingTranslations) return;
            if (!TranslatorCore.Config.online_mode) return;

            isSearchingTranslations = true;
            searchStatus = "";
            availableTranslations.Clear();
            selectedTranslation = null;

            try
            {
                // Get target language from system culture (converted to full name)
                string targetLang = LanguageHelper.GetSystemLanguageName();

                TranslationSearchResult result = null;

                // Try Steam ID first
                if (!string.IsNullOrEmpty(detectedGame?.steam_id))
                {
                    result = await ApiClient.SearchBysteamId(detectedGame.steam_id, targetLang);
                }

                // Fallback to game name search
                if ((result == null || !result.Success || result.Count == 0) && !string.IsNullOrEmpty(detectedGame?.name))
                {
                    result = await ApiClient.SearchByGameName(detectedGame.name, targetLang);
                }

                if (result != null && result.Success)
                {
                    availableTranslations = result.Translations ?? new List<TranslationInfo>();
                    searchStatus = availableTranslations.Count == 0
                        ? "No translations found for your language"
                        : ""; // Don't set status here, the list section will show the count
                }
                else
                {
                    searchStatus = result?.Error ?? "Search failed";
                }
            }
            catch (Exception e)
            {
                searchStatus = $"Error: {e.Message}";
                TranslatorCore.LogWarning($"[TranslatorUI] Search error: {e.Message}");
            }
            finally
            {
                isSearchingTranslations = false;
            }
        }

        private static async void DownloadSelectedTranslation()
        {
            if (selectedTranslation == null || isDownloading) return;

            isDownloading = true;
            downloadStatus = "Downloading...";

            try
            {
                var result = await ApiClient.Download(selectedTranslation.Id);

                if (result.Success && !string.IsNullOrEmpty(result.Content))
                {
                    // Save to translations.json
                    System.IO.File.WriteAllText(TranslatorCore.CachePath, result.Content);

                    // Reload cache to apply new content and UUID immediately
                    TranslatorCore.ReloadCache();

                    // Update in-memory server state (not persisted - will be re-fetched via check-uuid at startup)
                    string currentUser = TranslatorCore.Config.api_user;
                    bool isOwner = !string.IsNullOrEmpty(currentUser) &&
                        selectedTranslation.Uploader.Equals(currentUser, StringComparison.OrdinalIgnoreCase);
                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        Exists = true,
                        IsOwner = isOwner,
                        SiteId = selectedTranslation.Id,
                        Uploader = selectedTranslation.Uploader,
                        Hash = result.FileHash ?? selectedTranslation.FileHash,
                        Type = selectedTranslation.Type,
                        Notes = selectedTranslation.Notes
                    };

                    // Save as ancestor for future 3-way merges
                    TranslatorCore.SaveAncestorCache();

                    downloadStatus = "Downloaded successfully!";
                    TranslatorCore.LogInfo($"[TranslatorUI] Downloaded translation from {selectedTranslation.Uploader}");

                    // Wait a moment then complete wizard
                    await Task.Delay(1500);
                    CompleteWizard();
                }
                else
                {
                    downloadStatus = $"Download failed: {result.Error}";
                }
            }
            catch (Exception e)
            {
                downloadStatus = $"Error: {e.Message}";
                TranslatorCore.LogWarning($"[TranslatorUI] Download error: {e.Message}");
            }
            finally
            {
                isDownloading = false;
            }
        }

        private static void DrawWizardOllamaConfig()
        {
            GUILayout.Label("Ollama Configuration", WindowHelper.TitleStyle);
            GUILayout.Space(15);

            GUILayout.BeginHorizontal();
            wizardEnableOllama = GUILayout.Toggle(wizardEnableOllama, " Enable Ollama (local AI translation)", WindowHelper.LabelStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            GUI.enabled = wizardEnableOllama;

            WindowHelper.Label("Ollama URL:");
            wizardOllamaUrl = GUILayout.TextField(wizardOllamaUrl, textFieldStyle, GUILayout.Height(25));

            GUILayout.Space(10);

            GUILayout.BeginHorizontal();
            if (WindowHelper.Button("Test Connection", width: 150))
            {
                TestOllamaConnection();
            }
            WindowHelper.Label(ollamaTestStatus);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            WindowHelper.Label(
                "Recommended model: qwen3:8b\n" +
                "Install with: ollama pull qwen3:8b\n" +
                "Requires ~6-8 GB VRAM", italic: true);

            GUI.enabled = true;

            GUILayout.FlexibleSpace();

            if (WindowHelper.Button("Finish Setup", height: 40))
            {
                CompleteWizard();
            }
        }

        private static void TestOllamaConnection()
        {
            ollamaTestStatus = "Testing...";

            try
            {
                using (var client = new System.Net.Http.HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(5);
                    var response = client.GetAsync($"{wizardOllamaUrl}/api/tags").Result;

                    if (response.IsSuccessStatusCode)
                    {
                        ollamaTestStatus = "Connected!";
                    }
                    else
                    {
                        ollamaTestStatus = $"Error: {response.StatusCode}";
                    }
                }
            }
            catch (Exception e)
            {
                ollamaTestStatus = "Failed: " + e.Message.Substring(0, Math.Min(30, e.Message.Length));
            }
        }

        private static void CompleteWizard()
        {
            // Save all wizard settings
            TranslatorCore.Config.first_run_completed = true;
            TranslatorCore.Config.online_mode = wizardOnlineMode;
            TranslatorCore.Config.settings_hotkey = wizardHotkey;
            TranslatorCore.Config.enable_ollama = wizardEnableOllama;
            TranslatorCore.Config.ollama_url = wizardOllamaUrl;

            TranslatorCore.SaveConfig();

            // Start Ollama worker if enabled
            TranslatorCore.EnsureWorkerRunning();

            ShowWizard = false;
            TranslatorCore.LogInfo("First run wizard completed");
        }

        #endregion

        #region Settings

        private static void InitializeSettingsValues()
        {
            settingsOnlineMode = TranslatorCore.Config.online_mode;
            settingsEnableTranslations = TranslatorCore.Config.enable_translations;
            settingsEnableOllama = TranslatorCore.Config.enable_ollama;
            settingsOllamaUrl = TranslatorCore.Config.ollama_url ?? "http://localhost:11434";
            settingsModel = TranslatorCore.Config.model ?? "qwen3:8b";
            settingsGameContext = TranslatorCore.Config.game_context ?? "";
            settingsTargetLanguage = TranslatorCore.Config.target_language ?? "auto";
            settingsTargetLanguageIndex = GetTargetLanguageIndex(settingsTargetLanguage);
            settingsCheckUpdates = TranslatorCore.Config.sync.check_update_on_start;
            settingsAutoDownload = TranslatorCore.Config.sync.auto_download;
            settingsNotifyUpdates = TranslatorCore.Config.sync.notify_updates;

            // Hotkey - parse modifiers from stored string (e.g., "Ctrl+Alt+F10")
            string storedHotkey = TranslatorCore.Config.settings_hotkey ?? "F10";
            settingsHotkeyCtrl = storedHotkey.Contains("Ctrl+");
            settingsHotkeyAlt = storedHotkey.Contains("Alt+");
            settingsHotkeyShift = storedHotkey.Contains("Shift+");
            // Extract the base key
            settingsHotkey = storedHotkey
                .Replace("Ctrl+", "")
                .Replace("Alt+", "")
                .Replace("Shift+", "");
            settingsHotkeyWaiting = false;

            settingsInitialized = true;
        }

        private static float GetMainWindowHeight()
        {
            float height = 60; // Header + padding

            // Account section
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            height += isLoggedIn ? 130 : 110; // Account box

            // Translation info section
            height += 30; // Title
            height += 25 * 3; // Entries, Target, Source lines
            if (TranslatorCore.LocalChangesCount > 0) height += 22;
            if (TranslatorCore.Config.enable_ollama) height += 20;
            height += 20; // Box padding

            // Bottom buttons
            height += 55;

            return height;
        }

        private static void DrawMainWindow(int windowId)
        {
            WindowHelper.BeginWindow();

            if (WindowHelper.DrawHeader("UnityGameTranslator"))
            {
                ShowSettings = false;
            }

            // Scrollable content
            mainScrollPos = WindowHelper.BeginScrollContent(mainScrollPos);

            // Account Section (prominent)
            DrawAccountSection();

            GUILayout.Space(10);

            // Current Translation Info
            WindowHelper.Label("Current Translation", bold: true);
            GUILayout.BeginVertical(GUI.skin.box);

            int entryCount = TranslatorCore.TranslationCache.Count;
            string targetLang = TranslatorCore.Config.GetTargetLanguage();
            var serverState = TranslatorCore.ServerState;
            bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;

            GUILayout.Label($"Entries: {entryCount}");
            GUILayout.Label($"Target: {targetLang}");

            if (existsOnServer)
            {
                GUILayout.Label($"Source: {serverState.Uploader ?? "Website"} (#{serverState.SiteId})");

                // SYNC STATUS - check LocalChangesCount directly for real-time updates
                int localChanges = TranslatorCore.LocalChangesCount;

                if (HasPendingUpdate && PendingUpdateDirection == UpdateDirection.Merge)
                {
                    // Both sides have changes - conflict!
                    GUILayout.Label($"⚠ CONFLICT - Both local ({localChanges}) and server changed!",
                        new GUIStyle(GUI.skin.label) { normal = { textColor = new Color(1f, 0.5f, 0f) } }); // Orange
                }
                else if (localChanges > 0)
                {
                    // Local has changes to push (real-time check)
                    GUILayout.Label($"⚠ OUT OF SYNC - {localChanges} local changes to upload",
                        new GUIStyle(GUI.skin.label) { normal = { textColor = Color.yellow } });
                }
                else if (HasPendingUpdate && PendingUpdateDirection == UpdateDirection.Download)
                {
                    // Server has newer version (from startup check)
                    int serverLines = PendingUpdateInfo?.LineCount ?? 0;
                    GUILayout.Label($"⚠ OUT OF SYNC - Server has update ({serverLines} lines)",
                        new GUIStyle(GUI.skin.label) { normal = { textColor = Color.yellow } });
                }
                else
                {
                    GUILayout.Label("✓ SYNCED with server",
                        new GUIStyle(GUI.skin.label) { normal = { textColor = Color.green } });
                }
            }
            else
            {
                // Show different message based on whether we've checked with server
                if (serverState != null && serverState.Checked)
                {
                    GUILayout.Label("Source: Local only (not on server)");
                    WindowHelper.Label($"All {entryCount} entries are local", italic: true, fontSize: 11);
                }
                else if (!TranslatorCore.Config.online_mode)
                {
                    GUILayout.Label("Source: Local (offline mode)");
                }
                else
                {
                    GUILayout.Label("Source: Local (checking...)");
                }
            }

            // Ollama status
            if (TranslatorCore.Config.enable_ollama)
            {
                int queueCount = TranslatorCore.QueueCount;
                string ollamaStatus = queueCount > 0 ? $"Ollama: {queueCount} in queue" : "Ollama: Ready";
                WindowHelper.Label(ollamaStatus, fontSize: 11);
            }
            GUILayout.EndVertical();

            WindowHelper.EndScrollContent();

            // Bottom buttons (outside scroll, always visible)
            WindowHelper.BeginBottomButtons();
            if (WindowHelper.Button("Options"))
            {
                InitializeSettingsValues();
                showOptionsDialog = true;
            }
            if (WindowHelper.Button("Close"))
            {
                ShowSettings = false;
            }
            WindowHelper.EndBottomButtons();

            WindowHelper.EndWindow();
        }

        private static void DrawAccountSection()
        {
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);
            string currentUser = TranslatorCore.Config.api_user;

            // Account status (compact)
            WindowHelper.Label("Account", bold: true);
            GUILayout.BeginVertical(GUI.skin.box);
            if (isLoggedIn)
            {
                GUILayout.BeginHorizontal();
                WindowHelper.Label($"Connected as @{currentUser ?? "Unknown"}");
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Logout", GUILayout.Width(70), GUILayout.Height(22)))
                {
                    TranslatorCore.Config.api_token = null;
                    TranslatorCore.Config.api_user = null;
                    TranslatorCore.SaveConfig();
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                WindowHelper.Label("Not connected", italic: true);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Login", GUILayout.Width(70), GUILayout.Height(22)))
                {
                    ShowLogin();
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Translation actions (separate section)
            WindowHelper.Label("Actions", bold: true);
            GUILayout.BeginVertical(GUI.skin.box);

            // Determine upload type based on server state
            string uploadAction;
            string uploadHint;
            var state = TranslatorCore.ServerState;
            bool existsOnServer = state != null && state.Exists && state.SiteId.HasValue;

            if (existsOnServer && state.IsOwner)
            {
                uploadAction = "Update Translation";
                uploadHint = $"Update your translation #{state.SiteId}";
            }
            else if (existsOnServer && !state.IsOwner)
            {
                uploadAction = "Fork Translation";
                uploadHint = $"Create a fork from {state.Uploader}'s translation";
            }
            else
            {
                uploadAction = "Upload Translation";
                uploadHint = "Create a new translation";
            }

            GUI.enabled = isLoggedIn && TranslatorCore.TranslationCache.Count > 0;
            if (WindowHelper.Button(uploadAction))
            {
                ShowUpload();
            }
            WindowHelper.Label(uploadHint, italic: true, fontSize: 11);

            if (!isLoggedIn)
            {
                WindowHelper.Label("Login required", italic: true, fontSize: 11);
            }
            else if (TranslatorCore.TranslationCache.Count == 0)
            {
                WindowHelper.Label("No translations to upload", italic: true, fontSize: 11);
            }
            GUI.enabled = true;

            GUILayout.EndVertical();
        }

        private static void DrawOptionsWindow(int windowId)
        {
            if (!settingsInitialized)
            {
                InitializeSettingsValues();
            }

            WindowHelper.BeginWindow();

            if (WindowHelper.DrawHeader("Options"))
            {
                showOptionsDialog = false;
                settingsInitialized = false;
            }

            // Scrollable content
            settingsScrollPos = WindowHelper.BeginScrollContent(settingsScrollPos);

            // General Section
            WindowHelper.Label("General", bold: true);
            GUILayout.BeginVertical(GUI.skin.box);

            // Enable Translations toggle
            GUILayout.BeginHorizontal();
            settingsEnableTranslations = GUILayout.Toggle(settingsEnableTranslations, " Enable Translations");
            GUILayout.EndHorizontal();
            if (!settingsEnableTranslations)
            {
                WindowHelper.Label("Translations are disabled. Text will appear in original language.", fontSize: 11);
            }

            GUILayout.Space(8);

            // Target Language dropdown
            InitTargetLanguageOptions();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Target Language:", WindowHelper.LabelStyle, GUILayout.Width(120));
            if (GUILayout.Button(targetLanguageOptions[settingsTargetLanguageIndex], GUILayout.Height(25)))
            {
                // Simple popup - cycle through options or show popup
                settingsTargetLanguageIndex = (settingsTargetLanguageIndex + 1) % targetLanguageOptions.Length;
            }
            GUILayout.EndHorizontal();
            WindowHelper.Label("Click to change. First option uses system language.", fontSize: 11);

            GUILayout.Space(8);

            // Hotkey setting with modifiers
            WindowHelper.Label("Settings Hotkey:", bold: false);
            GUILayout.BeginHorizontal();
            settingsHotkeyCtrl = GUILayout.Toggle(settingsHotkeyCtrl, "Ctrl", GUILayout.Width(50));
            settingsHotkeyAlt = GUILayout.Toggle(settingsHotkeyAlt, "Alt", GUILayout.Width(45));
            settingsHotkeyShift = GUILayout.Toggle(settingsHotkeyShift, "Shift", GUILayout.Width(50));
            GUILayout.Label("+", GUILayout.Width(15));

            if (settingsHotkeyWaiting)
            {
                GUILayout.Label("Press key...", GUILayout.Width(100), GUILayout.Height(25));
                Event e = Event.current;
                if (e.type == EventType.KeyDown && e.keyCode != KeyCode.None &&
                    e.keyCode != KeyCode.LeftControl && e.keyCode != KeyCode.RightControl &&
                    e.keyCode != KeyCode.LeftAlt && e.keyCode != KeyCode.RightAlt &&
                    e.keyCode != KeyCode.LeftShift && e.keyCode != KeyCode.RightShift)
                {
                    settingsHotkey = e.keyCode.ToString();
                    settingsHotkeyWaiting = false;
                }
            }
            else
            {
                if (GUILayout.Button(settingsHotkey, GUILayout.Width(80), GUILayout.Height(25)))
                {
                    settingsHotkeyWaiting = true;
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Online Mode Section
            WindowHelper.Label("Online Mode", bold: true);
            GUILayout.BeginVertical(GUI.skin.box);
            settingsOnlineMode = GUILayout.Toggle(settingsOnlineMode, " Enable Online Mode");

            GUI.enabled = settingsOnlineMode;
            settingsCheckUpdates = GUILayout.Toggle(settingsCheckUpdates, " Check for updates on start");
            settingsNotifyUpdates = GUILayout.Toggle(settingsNotifyUpdates, " Notify when updates available");
            settingsAutoDownload = GUILayout.Toggle(settingsAutoDownload, " Auto-download updates (no conflicts)");
            GUI.enabled = true;
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Ollama Section
            WindowHelper.Label("Ollama (Local AI)", bold: true);
            GUILayout.BeginVertical(GUI.skin.box);
            settingsEnableOllama = GUILayout.Toggle(settingsEnableOllama, " Enable Ollama");

            GUI.enabled = settingsEnableOllama;

            GUILayout.BeginHorizontal();
            GUILayout.Label("URL:", GUILayout.Width(40));
            settingsOllamaUrl = GUILayout.TextField(settingsOllamaUrl, textFieldStyle, GUILayout.Height(22));
            if (GUILayout.Button("Test", GUILayout.Width(50)))
            {
                wizardOllamaUrl = settingsOllamaUrl;
                TestOllamaConnection();
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(ollamaTestStatus))
            {
                WindowHelper.Label(ollamaTestStatus, fontSize: 11);
            }

            GUILayout.BeginHorizontal();
            GUILayout.Label("Model:", GUILayout.Width(50));
            settingsModel = GUILayout.TextField(settingsModel ?? "", textFieldStyle ?? GUI.skin.textField, GUILayout.Height(22)) ?? "";
            GUILayout.EndHorizontal();
            WindowHelper.Label("Recommended: qwen3:8b", fontSize: 11);

            GUILayout.Label("Game Context (optional):");
            settingsGameContext = GUILayout.TextField(settingsGameContext ?? "", textFieldStyle ?? GUI.skin.textField, GUILayout.Height(22)) ?? "";

            GUI.enabled = true;
            GUILayout.EndVertical();

            WindowHelper.EndScrollContent();

            // Buttons
            WindowHelper.BeginBottomButtons();
            if (WindowHelper.Button("Save"))
            {
                SaveSettings();
                showOptionsDialog = false;
            }
            if (WindowHelper.Button("Cancel"))
            {
                showOptionsDialog = false;
                settingsInitialized = false;
            }
            WindowHelper.EndBottomButtons();

            WindowHelper.EndWindow();
        }

        private static void SaveSettings()
        {
            TranslatorCore.Config.online_mode = settingsOnlineMode;
            TranslatorCore.Config.enable_translations = settingsEnableTranslations;
            TranslatorCore.Config.enable_ollama = settingsEnableOllama;
            TranslatorCore.Config.ollama_url = settingsOllamaUrl;
            TranslatorCore.Config.model = settingsModel;
            TranslatorCore.Config.game_context = settingsGameContext;
            TranslatorCore.Config.target_language = GetTargetLanguageFromIndex(settingsTargetLanguageIndex);
            TranslatorCore.Config.sync.check_update_on_start = settingsCheckUpdates;
            TranslatorCore.Config.sync.auto_download = settingsAutoDownload;
            TranslatorCore.Config.sync.notify_updates = settingsNotifyUpdates;

            // Build hotkey string with modifiers
            string hotkeyString = "";
            if (settingsHotkeyCtrl) hotkeyString += "Ctrl+";
            if (settingsHotkeyAlt) hotkeyString += "Alt+";
            if (settingsHotkeyShift) hotkeyString += "Shift+";
            hotkeyString += settingsHotkey;
            TranslatorCore.Config.settings_hotkey = hotkeyString;

            TranslatorCore.SaveConfig();

            // Start Ollama worker if just enabled, or clear queue if disabled
            if (settingsEnableOllama)
            {
                TranslatorCore.EnsureWorkerRunning();
            }
            else
            {
                TranslatorCore.ClearQueue();
            }

            settingsInitialized = false;
        }

        #endregion

        #region Status Overlay

        private static void DrawStatusOverlay()
        {
            // Check if we should show notification:
            // 1. Local changes to upload (real-time)
            // 2. OR server update available (from startup check)
            // 3. OR merge needed (both sides changed)
            var serverState = TranslatorCore.ServerState;
            bool existsOnServer = serverState != null && serverState.Exists && serverState.SiteId.HasValue;
            bool hasLocalChanges = existsOnServer && TranslatorCore.LocalChangesCount > 0;
            bool hasServerUpdate = HasPendingUpdate && PendingUpdateDirection == UpdateDirection.Download;
            bool needsMerge = HasPendingUpdate && PendingUpdateDirection == UpdateDirection.Merge;

            bool shouldShowNotification = (hasLocalChanges || hasServerUpdate || needsMerge) && !notificationDismissed;

            if (shouldShowNotification)
            {
                DrawUpdateNotification(PendingUpdateDirection);
            }

            // Only show queue if Ollama is enabled
            if (!TranslatorCore.Config.enable_ollama) return;

            int queueCount = TranslatorCore.QueueCount;
            bool showQueue = queueCount > 0 || TranslatorCore.IsTranslating;

            if (!showQueue) return;

            float width = 250;
            float height = 45;
            float x = Screen.width - width - 10;
            float y = shouldShowNotification ? 80 : 10;

            GUI.Box(new Rect(x, y, width, height), "");

            GUILayout.BeginArea(new Rect(x + 10, y + 5, width - 20, height - 10));

            if (TranslatorCore.IsTranslating)
            {
                string text = TranslatorCore.CurrentText ?? "";
                if (text.Length > 25) text = text.Substring(0, 25) + "...";
                GUILayout.Label($"Translating: {text}");
            }

            if (queueCount > 0)
            {
                GUILayout.Label($"Queue: {queueCount} pending");
            }

            GUILayout.EndArea();
        }

        private static void DrawUpdateNotification(UpdateDirection direction)
        {
            float width = 340;
            float height = 65;
            float x = Screen.width - width - 10;
            float y = 10;

            GUI.Box(new Rect(x, y, width, height), "");

            GUILayout.BeginArea(new Rect(x + 10, y + 5, width - 20, height - 10));
            GUILayout.BeginVertical();

            // Show different message based on direction
            string message;
            string buttonText;
            switch (direction)
            {
                case UpdateDirection.Upload:
                    message = $"You have {TranslatorCore.LocalChangesCount} local changes to upload!";
                    buttonText = "Upload";
                    break;
                case UpdateDirection.Download:
                    message = "Server update available!";
                    buttonText = "Download";
                    break;
                case UpdateDirection.Merge:
                    message = "⚠ Conflict: Both local and server changed!";
                    buttonText = "Merge";
                    break;
                default:
                    message = $"{TranslatorCore.LocalChangesCount} local changes";
                    buttonText = "Upload";
                    break;
            }
            GUILayout.Label(message, new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold });

            GUILayout.BeginHorizontal();

            if (GUILayout.Button(buttonText, GUILayout.Width(80)))
            {
                switch (direction)
                {
                    case UpdateDirection.Upload:
                        ShowUpload();
                        break;
                    case UpdateDirection.Download:
                        ApplyPendingUpdate();
                        break;
                    case UpdateDirection.Merge:
                        // Start merge flow - download remote first
                        ApplyPendingUpdate();
                        break;
                    default:
                        ShowUpload();
                        break;
                }
            }
            if (GUILayout.Button("Ignore", GUILayout.Width(80)))
            {
                notificationDismissed = true;
            }
            if (GUILayout.Button("Settings", GUILayout.Width(80)))
            {
                ShowSettings = true;
                InitializeSettingsValues();
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        #endregion

        #region Update Check

        /// <summary>
        /// Check for translation updates. Call once after initialization.
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

            try
            {
                TranslatorCore.LogInfo("[UpdateCheck] Checking for updates...");

                // Compute local content hash to compare with server
                string localHash = TranslatorCore.ComputeContentHash();
                TranslatorCore.LogInfo($"[UpdateCheck] Local hash: {localHash?.Substring(0, 16)}...");

                var result = await ApiClient.CheckUpdate(
                    serverState.SiteId.Value,
                    localHash
                );

                TranslatorCore.LogInfo($"[UpdateCheck] Server response: HasUpdate={result.HasUpdate}, ServerHash={result.FileHash?.Substring(0, 16)}...");

                if (result.Success && result.HasUpdate)
                {
                    // Determine sync direction based on what changed
                    bool hasLocalChanges = TranslatorCore.LocalChangesCount > 0;

                    // Check if server changed since our last sync
                    // Server changed if: we have a LastSyncedHash AND server hash differs from it
                    string lastSyncedHash = TranslatorCore.LastSyncedHash;
                    bool serverChanged = !string.IsNullOrEmpty(lastSyncedHash) &&
                                         result.FileHash != lastSyncedHash;

                    // If no LastSyncedHash, we can't tell if server changed - assume it did if hash differs
                    if (string.IsNullOrEmpty(lastSyncedHash))
                    {
                        // First sync or legacy file - if we have local changes, assume upload; else download
                        serverChanged = !hasLocalChanges;
                    }

                    // Determine direction based on what changed
                    if (hasLocalChanges && serverChanged)
                    {
                        // Both sides changed - need merge
                        PendingUpdateDirection = UpdateDirection.Merge;
                        TranslatorCore.LogInfo($"[UpdateCheck] CONFLICT: Both local ({TranslatorCore.LocalChangesCount} changes) and server changed - merge needed");
                    }
                    else if (hasLocalChanges)
                    {
                        // Only local changed - upload
                        PendingUpdateDirection = UpdateDirection.Upload;
                        TranslatorCore.LogInfo($"[UpdateCheck] Local has {TranslatorCore.LocalChangesCount} changes to upload");
                    }
                    else
                    {
                        // Only server changed (or we don't know) - download
                        PendingUpdateDirection = UpdateDirection.Download;
                        TranslatorCore.LogInfo($"[UpdateCheck] Server has update: {result.LineCount} lines");
                    }

                    TranslatorCore.LogInfo($"[UpdateCheck] Direction={PendingUpdateDirection}, LastSyncedHash={lastSyncedHash?.Substring(0, Math.Min(16, lastSyncedHash?.Length ?? 0))}..., ServerHash={result.FileHash?.Substring(0, 16)}...");

                    HasPendingUpdate = true;
                    PendingUpdateInfo = result;

                    // Notification will show automatically via DrawStatusOverlay checking LocalChangesCount

                    // Auto-download only if no local changes and no conflict
                    if (PendingUpdateDirection == UpdateDirection.Download &&
                        TranslatorCore.Config.sync.auto_download)
                    {
                        TranslatorCore.LogInfo("[UpdateCheck] Auto-downloading update...");
                        await DownloadUpdate();
                    }
                }
                else if (result.Success)
                {
                    TranslatorCore.LogInfo("[UpdateCheck] Translation is up to date");
                    // Ensure notification is cleared when synced
                    HasPendingUpdate = false;
                    PendingUpdateInfo = null;
                    PendingUpdateDirection = UpdateDirection.None;
                }
                else
                {
                    TranslatorCore.LogWarning($"[UpdateCheck] Failed: {result.Error}");
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[UpdateCheck] Error: {e.Message}");
            }
        }

        private static async void ApplyPendingUpdate()
        {
            if (TranslatorCore.LocalChangesCount > 0)
            {
                // Download remote first, then show merge dialog
                await DownloadForMerge();
            }
            else
            {
                // No local changes - direct replace
                await DownloadUpdate();
            }
        }

        private static async Task DownloadForMerge()
        {
            var serverState = TranslatorCore.ServerState;
            if (serverState?.SiteId == null) return;

            try
            {
                var result = await ApiClient.Download(serverState.SiteId.Value);

                if (result.Success && !string.IsNullOrEmpty(result.Content))
                {
                    // Parse remote translations
                    var parsed = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, object>>(result.Content);
                    remoteTranslations = new Dictionary<string, string>();

                    foreach (var kvp in parsed)
                    {
                        if (!kvp.Key.StartsWith("_") && kvp.Value is string strValue)
                        {
                            remoteTranslations[kvp.Key] = strValue;
                        }
                    }

                    // Perform 3-way merge
                    var local = TranslatorCore.TranslationCache;
                    var ancestor = TranslatorCore.AncestorCache;

                    pendingMergeResult = TranslationMerger.Merge(local, remoteTranslations, ancestor);
                    conflictResolutions.Clear();

                    // Pre-populate resolutions with defaults
                    foreach (var conflict in pendingMergeResult.Conflicts)
                    {
                        conflictResolutions[conflict.Key] = ConflictResolution.TakeRemote;
                    }

                    TranslatorCore.LogInfo($"[Merge] Result: {pendingMergeResult.Statistics.GetSummary()}");

                    if (pendingMergeResult.ConflictCount > 0)
                    {
                        // Show merge dialog for user to resolve conflicts
                        showMergeDialog = true;
                    }
                    else
                    {
                        // No conflicts - apply merge directly
                        ApplyMerge(result.FileHash);
                    }
                }
                else
                {
                    TranslatorCore.LogWarning($"[Merge] Download failed: {result.Error}");
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Merge] Error: {e.Message}");
            }
        }

        private static async Task DownloadUpdate()
        {
            var serverState = TranslatorCore.ServerState;
            if (serverState?.SiteId == null) return;

            try
            {
                var result = await ApiClient.Download(serverState.SiteId.Value);

                if (result.Success && !string.IsNullOrEmpty(result.Content))
                {
                    // Backup current file
                    string backupPath = TranslatorCore.CachePath + ".backup";
                    if (System.IO.File.Exists(TranslatorCore.CachePath))
                    {
                        System.IO.File.Copy(TranslatorCore.CachePath, backupPath, true);
                    }

                    // Write new content
                    System.IO.File.WriteAllText(TranslatorCore.CachePath, result.Content);

                    // Reload cache to apply new content immediately
                    TranslatorCore.ReloadCache();

                    // Update server state hash in memory
                    if (serverState != null)
                    {
                        serverState.Hash = result.FileHash;
                    }

                    // Update LastSyncedHash for multi-device sync detection
                    TranslatorCore.LastSyncedHash = result.FileHash;

                    // Save cache (updates metadata including _source.hash after reload)
                    TranslatorCore.SaveCache();

                    // Save as ancestor for future 3-way merges
                    TranslatorCore.SaveAncestorCache();

                    // Clear all pending update state
                    HasPendingUpdate = false;
                    PendingUpdateInfo = null;
                    PendingUpdateDirection = UpdateDirection.None;

                    TranslatorCore.LogInfo($"[UpdateCheck] Translation updated and applied successfully, LastSyncedHash={result.FileHash?.Substring(0, 16)}...");
                }
                else
                {
                    TranslatorCore.LogWarning($"[UpdateCheck] Download failed: {result.Error}");
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[UpdateCheck] Download error: {e.Message}");
            }
        }

        #endregion

        #region Merge Dialog

        private static void DrawMergeWindow(int windowId)
        {
            if (pendingMergeResult == null)
            {
                showMergeDialog = false;
                return;
            }

            WindowHelper.BeginWindow();

            if (WindowHelper.DrawHeader("Merge Translation Update"))
            {
                CancelMerge();
            }

            // Summary
            var stats = pendingMergeResult.Statistics;
            WindowHelper.Label($"Summary: {stats.GetSummary()}");
            GUILayout.Space(5);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"Local changes: {stats.LocalModifiedCount + stats.LocalOnlyCount}", WindowHelper.LabelStyle, GUILayout.Width(150));
            GUILayout.Label($"Remote updates: {stats.RemoteAddedCount + stats.RemoteUpdatedCount}", WindowHelper.LabelStyle, GUILayout.Width(150));
            GUILayout.Label($"Conflicts: {pendingMergeResult.ConflictCount}", WindowHelper.LabelStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(15);

            // Conflict resolution section
            if (pendingMergeResult.Conflicts.Count > 0)
            {
                WindowHelper.Label("Resolve Conflicts:", bold: true);
                GUILayout.Space(5);

                // Quick actions
                GUILayout.BeginHorizontal();
                if (WindowHelper.Button("Keep All Local", width: 120))
                {
                    foreach (var c in pendingMergeResult.Conflicts)
                        conflictResolutions[c.Key] = ConflictResolution.KeepLocal;
                }
                if (WindowHelper.Button("Take All Remote", width: 120))
                {
                    foreach (var c in pendingMergeResult.Conflicts)
                        conflictResolutions[c.Key] = ConflictResolution.TakeRemote;
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(10);

                // Scrollable conflict list
                mergeScrollPos = WindowHelper.BeginScrollContent(mergeScrollPos, maxHeight: 250);

                foreach (var conflict in pendingMergeResult.Conflicts)
                {
                    DrawConflictEntry(conflict);
                }

                WindowHelper.EndScrollContent();
            }
            else
            {
                WindowHelper.Label("No conflicts detected. Ready to merge.");
            }

            GUILayout.FlexibleSpace();

            // Action buttons
            WindowHelper.BeginBottomButtons();
            if (WindowHelper.Button("Apply Merge", width: 150))
            {
                ApplyMergeWithResolutions();
            }
            if (WindowHelper.Button("Replace with Remote", width: 150))
            {
                ReplaceWithRemote();
            }
            if (WindowHelper.Button("Cancel", width: 100))
            {
                CancelMerge();
            }
            WindowHelper.EndBottomButtons();

            WindowHelper.EndWindow();
        }

        private static void DrawConflictEntry(MergeConflict conflict)
        {
            GUILayout.BeginVertical(GUI.skin.box);

            // Key and type
            GUILayout.BeginHorizontal();
            string keyDisplay = conflict.Key.Length > 40 ? conflict.Key.Substring(0, 40) + "..." : conflict.Key;
            WindowHelper.Label(keyDisplay, bold: true);
            WindowHelper.Label($"[{conflict.Type}]", fontSize: 10);
            GUILayout.EndHorizontal();

            // Values preview
            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(GUILayout.Width(270));
            WindowHelper.Label("Local:", fontSize: 10);
            string localPreview = conflict.LocalValue ?? "(deleted)";
            if (localPreview.Length > 50) localPreview = localPreview.Substring(0, 50) + "...";
            WindowHelper.Label(localPreview, fontSize: 11);
            GUILayout.EndVertical();

            GUILayout.BeginVertical(GUILayout.Width(270));
            WindowHelper.Label("Remote:", fontSize: 10);
            string remotePreview = conflict.RemoteValue ?? "(deleted)";
            if (remotePreview.Length > 50) remotePreview = remotePreview.Substring(0, 50) + "...";
            WindowHelper.Label(remotePreview, fontSize: 11);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            // Resolution buttons
            GUILayout.BeginHorizontal();
            if (!conflictResolutions.TryGetValue(conflict.Key, out var currentRes))
                currentRes = ConflictResolution.TakeRemote;

            if (GUILayout.Toggle(currentRes == ConflictResolution.KeepLocal, "Keep Local", GUILayout.Width(100)))
            {
                conflictResolutions[conflict.Key] = ConflictResolution.KeepLocal;
            }
            if (GUILayout.Toggle(currentRes == ConflictResolution.TakeRemote, "Take Remote", GUILayout.Width(100)))
            {
                conflictResolutions[conflict.Key] = ConflictResolution.TakeRemote;
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUILayout.Space(5);
        }

        private static void ApplyMergeWithResolutions()
        {
            if (pendingMergeResult == null) return;

            // Apply resolutions
            TranslationMerger.ApplyResolutions(pendingMergeResult, conflictResolutions);

            // Use PendingUpdateInfo hash or generate new one
            string newHash = PendingUpdateInfo?.FileHash;
            ApplyMerge(newHash);
        }

        private static void ApplyMerge(string newHash)
        {
            if (pendingMergeResult == null) return;

            try
            {
                // Backup current file
                string backupPath = TranslatorCore.CachePath + ".backup";
                if (System.IO.File.Exists(TranslatorCore.CachePath))
                {
                    System.IO.File.Copy(TranslatorCore.CachePath, backupPath, true);
                }

                // Build final JSON with metadata
                var finalData = new Dictionary<string, object>(pendingMergeResult.Merged.Count + 10);

                // Copy all translations
                foreach (var kvp in pendingMergeResult.Merged)
                {
                    finalData[kvp.Key] = kvp.Value;
                }

                // Add metadata (no _source - server state is in memory only)
                finalData["_uuid"] = TranslatorCore.FileUuid ?? Guid.NewGuid().ToString();

                // Write merged file
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(finalData, Newtonsoft.Json.Formatting.Indented);
                System.IO.File.WriteAllText(TranslatorCore.CachePath, json);

                // Reload cache to apply merged content immediately
                TranslatorCore.ReloadCache();

                // Update server state hash in memory
                if (TranslatorCore.ServerState != null)
                {
                    TranslatorCore.ServerState.Hash = newHash;
                }

                // Update LastSyncedHash - we've "seen" the server version
                TranslatorCore.LastSyncedHash = newHash;

                // Save REMOTE content as ancestor (not merged!)
                // This way LocalChangesCount = our additions that need uploading
                if (remoteTranslations != null)
                {
                    TranslatorCore.SaveAncestorFromRemote(remoteTranslations);
                }

                // Save cache (updates metadata including _source.hash)
                TranslatorCore.SaveCache();

                // Recalculate local changes (merged vs remote ancestor)
                TranslatorCore.RecalculateLocalChanges();

                // Reset state - but DON'T clear HasPendingUpdate, we need to upload!
                // Direction is now Upload (we have merged content to push)
                HasPendingUpdate = TranslatorCore.LocalChangesCount > 0;
                PendingUpdateInfo = null;
                PendingUpdateDirection = HasPendingUpdate ? UpdateDirection.Upload : UpdateDirection.None;
                showMergeDialog = false;
                pendingMergeResult = null;
                remoteTranslations = null;
                conflictResolutions.Clear();

                TranslatorCore.LogInfo($"[Merge] Applied successfully. LocalChangesCount={TranslatorCore.LocalChangesCount}, direction={PendingUpdateDirection}");
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Merge] Error applying: {e.Message}");
            }
        }

        private static async void ReplaceWithRemote()
        {
            showMergeDialog = false;
            pendingMergeResult = null;
            remoteTranslations = null;
            conflictResolutions.Clear();

            // Direct download and replace
            await DownloadUpdate();
        }

        private static void CancelMerge()
        {
            showMergeDialog = false;
            pendingMergeResult = null;
            remoteTranslations = null;
            conflictResolutions.Clear();
            HasPendingUpdate = false;
            PendingUpdateInfo = null;
            PendingUpdateDirection = UpdateDirection.None;
        }

        #endregion

        #region Login Dialog

        public static void ShowLogin()
        {
            showLoginDialog = true;
            loginStatus = "";
            InitiateLogin();
        }

        private static async void InitiateLogin()
        {
            try
            {
                loginStatus = "Initiating...";
                var result = await ApiClient.InitiateDeviceFlow();

                if (result.Success)
                {
                    loginDeviceCode = result.DeviceCode;
                    loginUserCode = result.UserCode;
                    loginVerificationUri = result.VerificationUri;
                    loginStatus = "Waiting for authorization...";
                    StartLoginPolling();
                }
                else
                {
                    loginStatus = $"Error: {result.Error}";
                }
            }
            catch (Exception e)
            {
                loginStatus = $"Error: {e.Message}";
            }
        }

        private static async void StartLoginPolling()
        {
            isPollingLogin = true;

            while (isPollingLogin && showLoginDialog)
            {
                await Task.Delay(5000); // Poll every 5 seconds

                if (!isPollingLogin || !showLoginDialog) break;

                try
                {
                    var result = await ApiClient.PollDeviceFlow(loginDeviceCode);

                    if (result.Success)
                    {
                        // Login successful
                        TranslatorCore.Config.api_token = result.AccessToken;
                        TranslatorCore.Config.api_user = result.UserName;
                        TranslatorCore.SaveConfig();
                        ApiClient.SetAuthToken(result.AccessToken);

                        loginStatus = $"Logged in as {result.UserName}!";
                        isPollingLogin = false;

                        await Task.Delay(1500);
                        showLoginDialog = false;
                    }
                    else if (!result.Pending)
                    {
                        // Error (expired, etc.)
                        loginStatus = $"Error: {result.Error}";
                        isPollingLogin = false;
                    }
                    // else: still pending, continue polling
                }
                catch (Exception e)
                {
                    loginStatus = $"Error: {e.Message}";
                    isPollingLogin = false;
                }
            }
        }

        private static void DrawLoginWindow(int windowId)
        {
            WindowHelper.BeginWindow();

            if (WindowHelper.DrawHeader("Connect to UnityGameTranslator"))
            {
                isPollingLogin = false;
                showLoginDialog = false;
            }

            GUILayout.Space(5);

            if (!string.IsNullOrEmpty(loginUserCode))
            {
                WindowHelper.Label("Enter this code on the website:");
                GUILayout.Space(10);

                // Big code display
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label(loginUserCode, new GUIStyle(WindowHelper.TitleStyle)
                {
                    fontSize = 32,
                    fontStyle = FontStyle.Bold
                });
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(10);

                // Show shorter URL
                string shortUrl = loginVerificationUri?.Replace("https://", "").Replace("http://", "") ?? "";
                WindowHelper.Label($"Go to: {shortUrl}", fontSize: 12);
                GUILayout.Space(8);

                if (WindowHelper.Button("Open Website"))
                {
                    UnityEngine.Application.OpenURL(loginVerificationUri);
                }

                GUILayout.Space(10);
            }

            // Status
            WindowHelper.Label(loginStatus, italic: true);

            GUILayout.FlexibleSpace();

            if (WindowHelper.Button("Cancel"))
            {
                isPollingLogin = false;
                showLoginDialog = false;
            }

            WindowHelper.EndWindow();
        }

        #endregion

        #region Upload Dialog

        // State for UUID check
        private static bool isCheckingUuid = false;
        private static string uuidCheckError = null;

        public static void ShowUpload()
        {
            if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                // Need to login first
                ShowLogin();
                return;
            }

            // Start UUID check
            isCheckingUuid = true;
            uuidCheckError = null;
            uploadScrollPos = Vector2.zero;
            uploadStatus = "Checking...";

            // Show upload dialog with "checking" status
            showUploadDialog = true;

            // Perform async UUID check
            PerformUuidCheck();
        }

        private static async void PerformUuidCheck()
        {
            try
            {
                TranslatorCore.LogInfo($"[Upload] Checking UUID: {TranslatorCore.FileUuid}");
                TranslatorCore.LogInfo($"[Upload] Auth token set: {!string.IsNullOrEmpty(TranslatorCore.Config.api_token)}");
                TranslatorCore.LogInfo($"[Upload] Current user: {TranslatorCore.Config.api_user ?? "null"}");

                var result = await ApiClient.CheckUuid(TranslatorCore.FileUuid);

                if (!result.Success)
                {
                    TranslatorCore.LogWarning($"[Upload] UUID check failed: {result.Error}");
                    uuidCheckError = result.Error;
                    uploadStatus = $"Error: {result.Error}";
                    isCheckingUuid = false;
                    return;
                }

                TranslatorCore.LogInfo($"[Upload] UUID check: exists={result.Exists}, isOwner={result.IsOwner}");

                if (result.Exists && result.IsOwner)
                {
                    // UPDATE: User's own translation
                    isExistingTranslation = true;
                    isFork = false;
                    uploadType = result.ExistingTranslation?.Type ?? "ai";
                    uploadNotes = result.ExistingTranslation?.Notes ?? "";

                    // Update in-memory server state
                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        Exists = true,
                        IsOwner = true,
                        SiteId = result.ExistingTranslation?.Id,
                        Uploader = TranslatorCore.Config.api_user,
                        Type = result.ExistingTranslation?.Type,
                        Notes = result.ExistingTranslation?.Notes,
                        Hash = result.ExistingTranslation?.FileHash
                    };

                    uploadStatus = "";
                    isCheckingUuid = false;
                    // Stay in upload dialog
                }
                else if (result.Exists && !result.IsOwner)
                {
                    // FORK: Someone else's translation
                    isExistingTranslation = false;
                    isFork = true;
                    uploadType = result.OriginalTranslation?.Type ?? "ai";
                    uploadNotes = ""; // Fresh notes for fork

                    // Update in-memory server state
                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        Exists = true,
                        IsOwner = false,
                        SiteId = result.OriginalTranslation?.Id,
                        Uploader = result.OriginalTranslation?.Uploader,
                        Type = result.OriginalTranslation?.Type
                    };

                    uploadStatus = "";
                    isCheckingUuid = false;
                    // Stay in upload dialog
                }
                else
                {
                    // NEW: First upload, need language selection
                    isExistingTranslation = false;
                    isFork = false;
                    uploadType = "ai";
                    uploadNotes = "";

                    // Close upload dialog, open language dialog
                    showUploadDialog = false;

                    // Reset language selection state
                    showSourcePopup = false;
                    showTargetPopup = false;
                    languagePopupScrollPos = Vector2.zero;

                    // Reset game search state
                    gameSearchQuery = "";
                    gameSearchResults = null;
                    gameSearchScrollPos = Vector2.zero;
                    selectedGame = null;
                    isSearchingGames = false;

                    // Initialize language list and defaults
                    languageList = null; // Force re-init
                    InitLanguageList();

                    // Show language selection dialog
                    showLanguageDialog = true;
                    isCheckingUuid = false;
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Upload] UUID check error: {e.Message}");
                uuidCheckError = e.Message;
                uploadStatus = $"Error: {e.Message}";
                isCheckingUuid = false;
            }
        }

        private static void InitLanguageList()
        {
            if (languageList == null)
            {
                languageList = LanguageHelper.GetLanguageNames();

                // Set default target language to system language
                string systemLang = LanguageHelper.GetSystemLanguageName();
                for (int i = 0; i < languageList.Length; i++)
                {
                    if (languageList[i] == "English")
                        sourceLanguageIndex = i;
                    if (languageList[i] == systemLang)
                        targetLanguageIndex = i;
                }
                selectedSourceLanguage = languageList[sourceLanguageIndex];
                selectedTargetLanguage = languageList[targetLanguageIndex];
            }
        }

        private static void DrawUploadWindow(int windowId)
        {
            string title = isCheckingUuid ? "Checking..." :
                          (isExistingTranslation ? "Update Translation" : (isFork ? "Fork Translation" : "Upload Translation"));

            WindowHelper.BeginWindow();

            if (WindowHelper.DrawHeader(title))
            {
                showUploadDialog = false;
            }

            // Show loading state while checking UUID
            if (isCheckingUuid)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                WindowHelper.Label("Checking if translation exists on server...");
                WindowHelper.Label("Please wait...", italic: true);
                GUILayout.EndVertical();
                WindowHelper.EndWindow();
                return;
            }

            // Scrollable content
            uploadScrollPos = WindowHelper.BeginScrollContent(uploadScrollPos);

            // Summary
            GUILayout.BeginVertical(GUI.skin.box);
            WindowHelper.Label($"Entries: {TranslatorCore.TranslationCache.Count}");
            if (TranslatorCore.CurrentGame != null)
            {
                WindowHelper.Label($"Game: {TranslatorCore.CurrentGame.name}");
            }
            if (isExistingTranslation)
            {
                WindowHelper.Label($"Updating: ID #{TranslatorCore.ServerState?.SiteId}", italic: true);
            }
            else if (isFork)
            {
                WindowHelper.Label($"Forking from: {TranslatorCore.ServerState?.Uploader ?? "unknown"}", italic: true);
            }
            else
            {
                // NEW upload - show selected languages
                WindowHelper.Label($"Languages: {selectedSourceLanguage} → {selectedTargetLanguage}", italic: true);
            }
            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Translation type - proper radio button behavior
            WindowHelper.Label("Translation Type:", bold: true);
            GUILayout.BeginVertical(GUI.skin.box);

            if (GUILayout.Toggle(uploadType == "ai", " AI (Ollama-generated)"))
            {
                uploadType = "ai";
            }
            if (GUILayout.Toggle(uploadType == "ai_corrected", " AI Corrected (reviewed & fixed)"))
            {
                uploadType = "ai_corrected";
            }
            if (GUILayout.Toggle(uploadType == "human", " Human (manually translated)"))
            {
                uploadType = "human";
            }

            GUILayout.EndVertical();

            GUILayout.Space(10);

            // Notes
            WindowHelper.Label("Notes (optional):");
            uploadNotes = GUILayout.TextArea(uploadNotes ?? "", textFieldStyle ?? GUI.skin.textArea, GUILayout.Height(60)) ?? "";

            WindowHelper.EndScrollContent();

            // Status
            if (!string.IsNullOrEmpty(uploadStatus))
            {
                WindowHelper.Label(uploadStatus, italic: true);
            }

            // Buttons (outside scroll, always visible)
            WindowHelper.BeginBottomButtons();
            GUI.enabled = !isUploading;
            string buttonText = isExistingTranslation ? "Update" : (isFork ? "Fork" : "Upload");
            if (WindowHelper.Button(buttonText))
            {
                PerformUpload();
            }
            if (WindowHelper.Button("Cancel"))
            {
                showUploadDialog = false;
            }
            GUI.enabled = true;
            WindowHelper.EndBottomButtons();

            WindowHelper.EndWindow();
        }

        private static async void PerformUpload()
        {
            isUploading = true;
            uploadStatus = isExistingTranslation ? "Updating..." : (isFork ? "Forking..." : "Uploading...");

            try
            {
                // Build content JSON
                var contentDict = TranslatorCore.TranslationCache.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value);
                contentDict["_uuid"] = TranslatorCore.FileUuid;
                string content = Newtonsoft.Json.JsonConvert.SerializeObject(contentDict);

                // Determine languages based on upload type
                string srcLang, tgtLang;
                if (isExistingTranslation || isFork)
                {
                    // UPDATE or FORK: Server will use existing languages
                    srcLang = TranslatorCore.Config.GetSourceLanguage() ?? "English";
                    tgtLang = TranslatorCore.Config.GetTargetLanguage();
                }
                else
                {
                    // NEW: Use selected languages from dialog
                    srcLang = selectedSourceLanguage;
                    tgtLang = selectedTargetLanguage;
                }

                var request = new UploadRequest
                {
                    SteamId = TranslatorCore.CurrentGame?.steam_id,
                    GameName = TranslatorCore.CurrentGame?.name ?? "Unknown Game",
                    SourceLanguage = srcLang,
                    TargetLanguage = tgtLang,
                    Type = uploadType,
                    Status = "in_progress",
                    Content = content,
                    Notes = uploadNotes
                };

                var result = await ApiClient.UploadTranslation(request);

                if (result.Success)
                {
                    string successMsg = isExistingTranslation ? "Updated" : (isFork ? "Forked" : "Uploaded");
                    uploadStatus = $"{successMsg}! ID: {result.TranslationId}";

                    // Update in-memory server state (not persisted - will be re-fetched via check-uuid at startup)
                    TranslatorCore.LogInfo($"[Upload] Setting ServerState: site_id={result.TranslationId}, user={TranslatorCore.Config.api_user}");
                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        Exists = true,
                        IsOwner = true,
                        SiteId = result.TranslationId,
                        Uploader = TranslatorCore.Config.api_user,
                        Hash = result.FileHash,
                        Type = uploadType,
                        Notes = uploadNotes
                    };

                    // Update LastSyncedHash for multi-device sync detection
                    TranslatorCore.LastSyncedHash = result.FileHash;

                    // Save cache to persist _source.hash
                    TranslatorCore.SaveCache();

                    // Save as ancestor for future merge detection (our content is now the "base")
                    TranslatorCore.SaveAncestorCache();

                    // Clear pending update state - we just synced!
                    HasPendingUpdate = false;
                    PendingUpdateInfo = null;
                    PendingUpdateDirection = UpdateDirection.None;
                    notificationDismissed = false; // Reset so new local changes will show notification

                    TranslatorCore.LogInfo($"[Upload] ServerState updated. SiteId={TranslatorCore.ServerState?.SiteId}, LastSyncedHash={result.FileHash?.Substring(0, 16)}...");

                    await Task.Delay(2000);
                    showUploadDialog = false;
                }
                else
                {
                    uploadStatus = $"Error: {result.Error}";
                }
            }
            catch (Exception e)
            {
                uploadStatus = $"Error: {e.Message}";
            }
            finally
            {
                isUploading = false;
            }
        }

        #endregion

        #region Language Selection Dialog

        private static void DrawLanguageSelectionWindow(int windowId)
        {
            InitLanguageList();

            WindowHelper.BeginWindow();

            if (WindowHelper.DrawHeader("Select Languages"))
            {
                showLanguageDialog = false;
            }

            WindowHelper.Label("This is a new translation. Please configure:");

            GUILayout.Space(5);

            // Game selection section
            WindowHelper.Label("1. Game", bold: true);
            GUILayout.BeginVertical(GUI.skin.box);

            // Show current game (selected or auto-detected)
            GameInfo currentGame = selectedGame ?? TranslatorCore.CurrentGame;
            if (currentGame != null)
            {
                GUILayout.BeginHorizontal();
                WindowHelper.Label($"{currentGame.name}", bold: true);
                if (selectedGame != null)
                {
                    WindowHelper.Label("(selected)", italic: true, fontSize: 10);
                }
                else
                {
                    WindowHelper.Label("(auto-detected)", italic: true, fontSize: 10);
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("No game detected - please search", new GUIStyle(WindowHelper.LabelStyle) { normal = { textColor = Color.yellow } });
            }

            // Game search
            GUILayout.BeginHorizontal();
            gameSearchQuery = GUILayout.TextField(gameSearchQuery ?? "", GUILayout.Height(24));
            GUI.enabled = !isSearchingGames && !string.IsNullOrEmpty(gameSearchQuery) && gameSearchQuery.Length >= 2;
            if (GUILayout.Button(isSearchingGames ? "..." : "Search", GUILayout.Width(70), GUILayout.Height(24)))
            {
                PerformGameSearch();
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            // Game search results
            if (gameSearchResults != null && gameSearchResults.Count > 0)
            {
                WindowHelper.Label($"Found {gameSearchResults.Count} games:", fontSize: 10);
                gameSearchScrollPos = WindowHelper.BeginScrollContent(gameSearchScrollPos, maxHeight: 80);
                foreach (var game in gameSearchResults)
                {
                    string sourceTag = !string.IsNullOrEmpty(game.Source) ? $" [{game.Source}]" : "";
                    if (GUILayout.Button($"{game.Name}{sourceTag}", GUILayout.Height(22)))
                    {
                        selectedGame = new GameInfo
                        {
                            name = game.Name,
                            steam_id = game.SteamId
                        };
                        gameSearchResults = null;
                        gameSearchQuery = "";
                    }
                }
                WindowHelper.EndScrollContent();
            }
            else if (gameSearchResults != null && gameSearchResults.Count == 0)
            {
                WindowHelper.Label("No games found", italic: true, fontSize: 10);
            }

            GUILayout.EndVertical();

            GUILayout.Space(5);

            // Source language dropdown
            WindowHelper.Label("2. Source Language (original game text):", bold: true);
            GUILayout.BeginHorizontal();
            GUILayout.Space(10);

            // Use a popup-like approach with a button that shows current selection
            if (GUILayout.Button(selectedSourceLanguage, GUILayout.Height(28)))
            {
                // Toggle showing source language popup
                showSourcePopup = !showSourcePopup;
                showTargetPopup = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Target language dropdown
            WindowHelper.Label("3. Target Language (your translation):", bold: true);
            GUILayout.BeginHorizontal();
            GUILayout.Space(10);
            if (GUILayout.Button(selectedTargetLanguage, GUILayout.Height(28)))
            {
                showTargetPopup = !showTargetPopup;
                showSourcePopup = false;
            }
            GUILayout.EndHorizontal();

            // Draw popups if active
            if (showSourcePopup)
            {
                DrawLanguagePopup(true);
            }
            else if (showTargetPopup)
            {
                DrawLanguagePopup(false);
            }

            GUILayout.FlexibleSpace();

            // Validation
            GameInfo gameToUse = selectedGame ?? TranslatorCore.CurrentGame;
            bool hasGame = gameToUse != null && !string.IsNullOrEmpty(gameToUse.name);
            bool hasLanguages = !string.IsNullOrEmpty(selectedSourceLanguage) &&
                                !string.IsNullOrEmpty(selectedTargetLanguage) &&
                                selectedSourceLanguage != selectedTargetLanguage;
            bool isValid = hasGame && hasLanguages;

            if (!hasGame)
            {
                GUILayout.Label("Please select a game", new GUIStyle(WindowHelper.LabelStyle) { normal = { textColor = Color.yellow } });
            }
            else if (selectedSourceLanguage == selectedTargetLanguage)
            {
                GUILayout.Label("Source and target languages must be different!", new GUIStyle(WindowHelper.LabelStyle) { normal = { textColor = Color.red } });
            }

            // Buttons
            WindowHelper.BeginBottomButtons();
            GUI.enabled = isValid && !showSourcePopup && !showTargetPopup && !isSearchingGames;
            if (WindowHelper.Button("Continue to Upload"))
            {
                // Apply selected game if user chose one
                if (selectedGame != null)
                {
                    TranslatorCore.CurrentGame = selectedGame;
                }

                // Languages selected, now show upload dialog
                showLanguageDialog = false;
                showUploadDialog = true;
            }
            GUI.enabled = true;
            if (WindowHelper.Button("Cancel"))
            {
                showLanguageDialog = false;
            }
            WindowHelper.EndBottomButtons();

            WindowHelper.EndWindow();
        }

        // Popup state
        private static bool showSourcePopup = false;
        private static bool showTargetPopup = false;
        private static Vector2 languagePopupScrollPos = Vector2.zero;

        private static void DrawLanguagePopup(bool isSource)
        {
            // Draw a scrollable list overlay
            float popupHeight = 200;
            Rect popupRect = new Rect(10, isSource ? 100 : 160, languageRect.width - 40, popupHeight);

            GUI.Box(popupRect, "");

            GUILayout.BeginArea(new Rect(popupRect.x + 5, popupRect.y + 5, popupRect.width - 10, popupRect.height - 10));
            languagePopupScrollPos = GUILayout.BeginScrollView(languagePopupScrollPos);

            for (int i = 0; i < languageList.Length; i++)
            {
                string lang = languageList[i];
                bool isSelected = isSource ? (lang == selectedSourceLanguage) : (lang == selectedTargetLanguage);

                GUIStyle style = new GUIStyle(GUI.skin.button)
                {
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = isSelected ? Color.cyan : Color.white }
                };

                if (GUILayout.Button(lang, style, GUILayout.Height(22)))
                {
                    if (isSource)
                    {
                        selectedSourceLanguage = lang;
                        sourceLanguageIndex = i;
                        showSourcePopup = false;
                    }
                    else
                    {
                        selectedTargetLanguage = lang;
                        targetLanguageIndex = i;
                        showTargetPopup = false;
                    }
                    languagePopupScrollPos = Vector2.zero;
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static async void PerformGameSearch()
        {
            if (string.IsNullOrEmpty(gameSearchQuery) || gameSearchQuery.Length < 2)
                return;

            isSearchingGames = true;
            gameSearchResults = null;

            try
            {
                var result = await ApiClient.SearchGamesExternal(gameSearchQuery);

                if (result.Success && result.Games != null)
                {
                    gameSearchResults = result.Games;
                }
                else
                {
                    gameSearchResults = new List<GameApiInfo>();
                    TranslatorCore.LogWarning($"[GameSearch] Failed: {result.Error}");
                }
            }
            catch (Exception e)
            {
                gameSearchResults = new List<GameApiInfo>();
                TranslatorCore.LogWarning($"[GameSearch] Error: {e.Message}");
            }
            finally
            {
                isSearchingGames = false;
            }
        }

        #endregion
    }
}
