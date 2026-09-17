using System;
using System.Threading.Tasks;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// First-run wizard panel. Guides user through initial setup.
    /// Steps: Welcome -> OnlineMode -> Hotkey -> LanguageSelection -> TranslationChoice -> AIConfig -> Complete
    ///
    /// Uses CreateScrollablePanelLayout like all other panels.
    /// Each step is a simple container inside scrollContent.
    /// </summary>
    public class WizardPanel : TranslatorPanelBase
    {
        public enum WizardStep
        {
            Welcome,
            OnlineMode,
            Hotkey,
            LanguageSelection,
            TranslationChoice,
            AIConfig,
            Complete
        }

        /// <summary>The screen as a document — common/spec/screens/wizard.json — read once; the base's constructor reads the sizes below through it.</summary>
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("wizard");

        /// <summary>What the builder made of the document: every piece by name.</summary>
        private BuiltScreen _screen;

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;
        protected override bool PersistWindowPreferences => Doc.Persist;

        // Current step
        private WizardStep _currentStep = WizardStep.Welcome;

        // Scroll content reference
        private Host _scrollContent;

        // Step containers
        private Host _welcomeStep;
        private Host _onlineModeStep;
        private Host _hotkeyStep;
        private Host _languageSelectionStep;
        private Host _translationChoiceStep;
        private Host _aiConfigStep;
        private Host _completeStep;

        // State variables - initialized from config in ConstructPanelContent
        private bool _onlineMode;
        private bool _enableAI;
        private string _translationBackend; // "none", "llm", "google", "deepl"
        private string _aiUrl;
        private string _aiApiKey;
        private string _aiModel;
        private string _gameContext;
        private string _googleApiKey;
        private string _deeplApiKey;
        private bool _deeplUseFree;
        private string _targetLanguage;

        // Language selection
        private SearchableDropdown _targetLanguageDropdown;
        private LabelHandle _detectedLanguageLabel;

        // Hotkey capture (reusable component)
        private HotkeyCapture _hotkeyCapture;
        private LabelHandle _hotkeyDisplayLabel;

        // UI references - Translation config step
        private ToggleHandle _wizardEnableToggle;
        private SearchableDropdown _wizardBackendTypeDropdown;
        private Host _wizardBackendTypeSection;
        private Host _wizardLlmSection;
        private Host _wizardTransApiSection;
        private SearchableDropdown _wizardProviderDropdown;
        private Host _wizardGoogleSection;
        private Host _wizardDeeplSection;
        private FieldHandle _aiUrlInput;
        private FieldHandle _aiApiKeyInput;
        private SearchableDropdown _modelDropdown;
        private FieldHandle _gameContextInput;
        private LabelHandle _aiStatusLabel;
        private FieldHandle _wizardGoogleKeyInput;
        private LabelHandle _wizardGoogleStatusLabel;
        private FieldHandle _wizardDeeplKeyInput;
        private ToggleHandle _wizardDeeplFreeToggle;
        private LabelHandle _wizardDeeplStatusLabel;

        // TranslationChoice step state
        private GameInfo _detectedGame;
        private bool _isDownloading;

        // TranslationChoice UI references
        private LabelHandle _gameLabel;
        private LabelHandle _localTranslationsLabel;
        private LabelHandle _accountStatusLabel;
        private ButtonHandle _loginBtn;
        private TranslationList _translationList;
        private LabelHandle _downloadStatusLabel;
        private LabelHandle _comparisonLabel;
        private ButtonHandle _downloadBtn;
        private Host _actionButtonsRow;
        private Host _onlineChoiceBox;
        private Host _offlineChoiceBox;
        private ToggleHandle _onlineToggle;
        private ToggleHandle _offlineToggle;
        private LabelHandle _completeDescription;
        private Components.HelpZone _helpZone;

        public WizardPanel(UIBase owner) : base(owner)
        {
        }

        public override void Update()
        {
            base.Update();

            // Update hotkey capture component when on hotkey step
            if (_currentStep == WizardStep.Hotkey)
            {
                _hotkeyCapture?.Update();

                // Update display label when hotkey changes
                if (_hotkeyDisplayLabel != null && !_hotkeyCapture.IsCapturing)
                {
                    string newHotkey = _hotkeyCapture.HotkeyString;
                    if (_hotkeyDisplayLabel.Value != newHotkey)
                    {
                        _hotkeyDisplayLabel.Show(newHotkey);
                        _hotkeyDisplayLabel.Tone = Tone.Accent;
                    }
                }
            }
        }

        /// <summary>
        /// The screen is wizard.json; this builds it, fills it from the config and keeps hold of
        /// what the code writes, reads or shows. What the document carries, and why — a document
        /// has no comments:
        /// - seven steps, one stack each, all hidden: ShowStep shows one at a time and the window
        ///   is sized to it; the shared footer is hidden, each step carries its own buttons;
        /// - the two mode boxes carry bare boxes: their words are the title beside them, and the
        ///   whole box is highlighted (Stacks.Highlight) because a 20px box alone is easy to miss;
        /// - the hotkey display is SectionTitle (18px in the original, the nearest role), and
        ///   "Setup Complete!" is Title (22px in the original) — see the migration report;
        /// - the community list is 200 tall like the main panel's: a row is about 130 pixels,
        ///   so at 100 a newcomer saw less than one card and had to scroll to see it;
        /// - the translation-choice step's card is wider (460) for that list;
        /// - the three backend dropdowns take their choices from the code (`options: code`): the
        ///   two backend types are the theme's words, the models are the server's;
        /// - every field asks for an act as it is typed in: the wizard keeps its state as the
        ///   person types, so Test and Finish read what is there.
        /// </summary>
        protected override void ConstructPanelContent()
        {
            // Initialize state from existing config (for re-running wizard)
            _onlineMode = TranslatorCore.Config.online_mode;
            _enableAI = TranslatorCore.Config.enable_ai;
            _translationBackend = TranslatorCore.Config.translation_backend ?? "none";
            _aiUrl = TranslatorCore.Config.ai_url ?? Endpoints.OllamaDefault;
            _aiApiKey = TranslatorCore.Config.ai_api_key ?? "";
            _aiModel = TranslatorCore.Config.ai_model ?? "";
            _gameContext = TranslatorCore.Config.game_context ?? "";
            _googleApiKey = TranslatorCore.Config.google_api_key ?? "";
            _deeplApiKey = TranslatorCore.Config.deepl_api_key ?? "";
            _deeplUseFree = TranslatorCore.Config.deepl_use_free;

            // Initialize target language - auto-detect from system if not set or "auto"
            string configTarget = TranslatorCore.Config.target_language;
            if (string.IsNullOrEmpty(configTarget) || configTarget.ToLower() == "auto")
            {
                _targetLanguage = LanguageHelper.GetSystemLanguageName();
            }
            else
            {
                _targetLanguage = configTarget;
            }

            // Initialize components
            string existingHotkey = TranslatorCore.Config.settings_hotkey ?? "F10";
            _hotkeyCapture = new HotkeyCapture(existingHotkey);
            _translationList = new TranslationList();

            // Use centralized scroll layout - ONE scroll for the entire panel
            Layout(out _scrollContent, out var sharedButtonRow, Doc.Width - 40);

            // Contextual help bar (fixed at the bottom, above the hidden shared row)
            _helpZone = CreateHelpZone(sharedButtonRow, Doc.Help);

            // Hide the shared button row - wizard has per-step buttons
            sharedButtonRow.Visible = false;

            _screen = ScreenBuilder.Build(Doc, _scrollContent, sharedButtonRow, ActOf, help: _helpZone);

            _welcomeStep = _screen.Host("WelcomeStep");
            _onlineModeStep = _screen.Host("OnlineModeStep");
            _hotkeyStep = _screen.Host("HotkeyStep");
            _languageSelectionStep = _screen.Host("LanguageSelectionStep");
            _translationChoiceStep = _screen.Host("TranslationChoiceStep");
            _aiConfigStep = _screen.Host("AIConfigStep");
            _completeStep = _screen.Host("CompleteStep");

            // Online mode: two bare boxes answering for each other, the chosen box highlighted.
            _onlineChoiceBox = _screen.Host("OnlineBox");
            _offlineChoiceBox = _screen.Host("OfflineBox");
            _onlineToggle = _screen.Toggle("OnlineToggle");
            _offlineToggle = _screen.Toggle("OfflineToggle");

            // Hotkey: the capture control in its host, the current combination under it.
            _hotkeyCapture.CreateUI(_screen.Host("HotkeyHost"), onHotkeyChanged: (hotkey) =>
            {
                if (_hotkeyDisplayLabel != null)
                {
                    _hotkeyDisplayLabel.Show(hotkey);
                    _hotkeyDisplayLabel.Tone = Tone.Accent;
                }
            }, includeDisplayLabel: false);
            _helpZone?.Describe(_hotkeyCapture.Handle,
                "Keyboard shortcut that opens the translator menu in-game. Click to record a new combination; Ctrl, Alt and Shift are supported.");
            _hotkeyDisplayLabel = _screen.Label("HotkeyLabel");

            // Language.
            _detectedLanguageLabel = _screen.Label("DetectedLabel");
            _targetLanguageDropdown = _screen.Dropdown("TargetLang");

            // Community translations: the list in its host, under the game's own section.
            _gameLabel = _screen.Label("GameLabel");
            _localTranslationsLabel = _screen.Label("LocalLabel");
            _accountStatusLabel = _screen.Label("AccountStatus");
            _loginBtn = _screen.Button("LoginBtn");
            _translationList.CreateUI(_screen.Host("TranslationListHost"), onSelectionChanged: (t) =>
            {
                UpdateActionButtons();
            });
            _helpZone?.Describe(_translationList.Handle,
                "Community translations found for this game in your language. Select one to download or merge it.");
            _comparisonLabel = _screen.Label("ComparisonLabel");
            _downloadStatusLabel = _screen.Label("DownloadStatus");
            _actionButtonsRow = _screen.Host("ActionBtnsRow");
            _downloadBtn = _screen.Button("DownloadBtn");

            // Auto-translation.
            _wizardEnableToggle = _screen.Toggle("EnableToggle");
            _wizardBackendTypeSection = _screen.Host("BackendTypeSection");
            _wizardBackendTypeDropdown = _screen.Dropdown("WizardType");
            _wizardLlmSection = _screen.Host("LLMSection");
            _aiUrlInput = _screen.Field("AIUrl");
            _aiStatusLabel = _screen.Label("AIStatus");
            _aiApiKeyInput = _screen.Field("AIApiKey");
            _modelDropdown = _screen.Dropdown("ModelDropdown");
            _gameContextInput = _screen.Field("ContextInput");
            _wizardTransApiSection = _screen.Host("TransApiSection");
            _wizardProviderDropdown = _screen.Dropdown("WizardProvider");
            _wizardGoogleSection = _screen.Host("GoogleSection");
            _wizardGoogleKeyInput = _screen.Field("GoogleKey");
            _wizardGoogleStatusLabel = _screen.Label("GoogleStatus");
            _wizardDeeplSection = _screen.Host("DeepLSection");
            _wizardDeeplKeyInput = _screen.Field("DeepLKey");
            _wizardDeeplStatusLabel = _screen.Label("DeepLStatus");
            _wizardDeeplFreeToggle = _screen.Toggle("DeepLFreeToggle");

            _completeDescription = _screen.Label("CompleteDescription");

            // ── What the config says, written into the pieces ──────────────────────
            // ⚠ The acts are already wired, so each write below may fire its handler with the
            // value it was just given — every handler reads the piece and tolerates that.
            _onlineToggle.IsOn = _onlineMode;
            _offlineToggle.IsOn = !_onlineMode;
            UpdateOnlineChoiceHighlight();

            _hotkeyDisplayLabel.Show(_hotkeyCapture.HotkeyString);

            // Detected language is data — never translated.
            _detectedLanguageLabel.Show($"Detected from your system: {LanguageHelper.GetSystemLanguageName()}");
            _targetLanguageDropdown.SelectedValue = _targetLanguage;

            _gameLabel.Show("Game: Detecting...");
            _accountStatusLabel.Say("Optional: connect an account to share your translation later");

            _wizardEnableToggle.IsOn = _translationBackend != "none";

            string[] typeOptions = { UIStyles.BackendTypeLLM, UIStyles.BackendTypeApi };
            bool isTransApi = _translationBackend == "google" || _translationBackend == "deepl";
            _wizardBackendTypeDropdown.SetOptions(typeOptions);
            _wizardBackendTypeDropdown.SelectedValue = isTransApi ? UIStyles.BackendTypeApi : UIStyles.BackendTypeLLM;

            _aiUrlInput.Text = _aiUrl;
            _aiApiKeyInput.Text = _aiApiKey;

            string[] initialModels = !string.IsNullOrEmpty(_aiModel) ? new[] { _aiModel } : new string[0];
            _modelDropdown.SetOptions(initialModels);
            _modelDropdown.SelectedValue = _aiModel;

            _gameContextInput.Text = _gameContext;

            string[] providerOptions = { "Google Translate", "DeepL" };
            string currentProvider = _translationBackend == "deepl" ? "DeepL" : "Google Translate";
            _wizardProviderDropdown.SetOptions(providerOptions);
            _wizardProviderDropdown.SelectedValue = currentProvider;

            _wizardGoogleKeyInput.Text = _googleApiKey;
            _wizardDeeplKeyInput.Text = _deeplApiKey;
            _wizardDeeplFreeToggle.IsOn = _deeplUseFree;

            // Initial visibility
            bool initEnabled = _translationBackend != "none";
            _wizardBackendTypeSection.Visible = initEnabled;
            _wizardLlmSection.Visible = initEnabled && !isTransApi;
            _wizardTransApiSection.Visible = initEnabled && isTransApi;
            _wizardGoogleSection.Visible = _translationBackend == "google";
            _wizardDeeplSection.Visible = _translationBackend == "deepl";

            ShowStep(WizardStep.Welcome);
        }

        /// <summary>What each verb the document asks for does. A verb with no answer here fails at construction, not at the click.</summary>
        private Action ActOf(string act)
        {
            switch (act)
            {
                case "welcomeNext": return () => ShowStep(WizardStep.OnlineMode);
                case "onlineChanged": return OnOnlineToggled;
                case "offlineChanged": return OnOfflineToggled;
                case "onlineBack": return () => ShowStep(WizardStep.Welcome);
                case "onlineNext": return () => ShowStep(WizardStep.Hotkey);
                case "hotkeyBack": return () => ShowStep(WizardStep.OnlineMode);
                case "hotkeyNext": return () => ShowStep(WizardStep.LanguageSelection);
                case "targetChanged": return () => _targetLanguage = _targetLanguageDropdown.SelectedValue;
                case "languageBack": return () => ShowStep(WizardStep.Hotkey);
                case "languageNext": return () => ShowStep(_onlineMode ? WizardStep.TranslationChoice : WizardStep.AIConfig);
                case "login": return OnLoginClicked;
                case "download": return OnDownloadClicked;
                case "choiceBack": return () => ShowStep(WizardStep.LanguageSelection);
                case "choiceNext": return () => ShowStep(WizardStep.AIConfig);
                case "enableChanged": return OnWizardEnableChanged;
                case "typeChanged": return OnWizardTypeChanged;
                case "aiUrlChanged": return () => _aiUrl = _aiUrlInput.Text;
                case "testAi": return TestAIConnection;
                case "aiKeyChanged": return () => _aiApiKey = _aiApiKeyInput.Text;
                case "modelChanged": return () => _aiModel = _modelDropdown.SelectedValue;
                case "refreshModels": return RefreshModels;
                case "contextChanged": return () => _gameContext = _gameContextInput.Text;
                case "providerChanged": return OnWizardProviderChanged;
                case "googleKeyChanged": return () => _googleApiKey = _wizardGoogleKeyInput.Text;
                case "testGoogle": return WizardTestGoogle;
                case "deeplKeyChanged": return () => _deeplApiKey = _wizardDeeplKeyInput.Text;
                case "testDeepl": return WizardTestDeepL;
                case "deeplFreeChanged": return () => _deeplUseFree = _wizardDeeplFreeToggle.IsOn;
                case "aiBack": return () => ShowStep(_onlineMode ? WizardStep.TranslationChoice : WizardStep.LanguageSelection);
                case "aiFinish": return () => ShowStep(WizardStep.Complete);
                case "finish": return FinishWizard;
                default: return null;
            }
        }

        /// <summary>The two mode boxes answer for each other: ticking one unticks the other.</summary>
        private void OnOnlineToggled()
        {
            bool val = _onlineToggle.IsOn;
            _onlineMode = val;
            UpdateOnlineChoiceHighlight();
            _offlineToggle.IsOn = !val;
        }

        private void OnOfflineToggled()
        {
            bool val = _offlineToggle.IsOn;
            if (val) _onlineMode = false;
            _onlineToggle.IsOn = !val;
            UpdateOnlineChoiceHighlight();
        }

        /// <summary>
        /// Highlight the whole selected box so the active choice is visible at a glance
        /// (the 20px checkbox alone is easy to miss).
        /// </summary>
        private void UpdateOnlineChoiceHighlight()
        {
            if (_onlineChoiceBox == null || _offlineChoiceBox == null) return;
            Stacks.Highlight(_onlineChoiceBox, _onlineMode);
            Stacks.Highlight(_offlineChoiceBox, !_onlineMode);
        }

        private async void OnTranslationChoiceEnter()
        {
            try
            {
                // Detect game if not already done. Detection is synchronous; only the optional
                // online search below performs an await that can resume on a background thread.
                if (_detectedGame == null)
                {
                    _detectedGame = GameDetector.DetectGame();
                    if (_detectedGame != null)
                    {
                        _gameLabel.Show(Tr("Game:") + $" {_detectedGame.name}");
                        if (_onlineMode && !_translationList.IsSearching)
                        {
                            // Use the selected target language from wizard
                            string targetLang = _targetLanguage;

                            // Capture values for closure
                            var steamId = _detectedGame.steam_id;
                            var gameName = _detectedGame.name;

                            await _translationList.SearchAsync(steamId, gameName, targetLang);
                        }
                    }
                    else
                    {
                        _gameLabel.Say("Game: Unknown");
                    }
                }

                // After the SearchAsync await we may be on a background thread (IL2CPP).
                // ALL UI access from this point on (label writes, UpdateAccountStatus which
                // internally calls RecalculateSize -> coroutine -> LayoutRebuilder) must run
                // on the main thread, otherwise the IL2CPP runtime faults with
                // AccessViolationException inside LayoutRebuilder.ForceRebuildLayoutImmediate.
                // We enqueue even in the no-await branches for simplicity and safety; the
                // 1-frame delay is imperceptible.
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    // Update local translations count
                    int localCount = TranslatorCore.TranslationCache.Count;
                    if (localCount > 0)
                    {
                        _localTranslationsLabel.Say($"You already have {localCount} local translations");
                        var serverState = TranslatorCore.ServerState;
                        if (serverState != null && serverState.Exists && !string.IsNullOrEmpty(serverState.Uploader))
                        {
                            _localTranslationsLabel.Show(_localTranslationsLabel.Value + $" (synced with @{serverState.Uploader})");
                        }
                    }
                    else
                    {
                        _localTranslationsLabel.Show("");
                    }

                    UpdateAccountStatus(); // calls RecalculateSize() internally
                    UpdateActionButtons();
                });
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[OnTranslationChoiceEnter] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
            }
        }

        /// <summary>
        /// The comparison line and the Download button, from what is selected and what is local.
        ///
        /// ⚠ Download is the only act here (2026-09-11). Which of upload, merge or update applies
        /// to a local file is a question of role, hash and lineage; this screen knows none of
        /// them and used to answer it on line counts. The Main's status card and the corner
        /// overlay answer it from the server state once the game runs.
        /// </summary>
        private void UpdateActionButtons()
        {
            var selected = _translationList?.SelectedTranslation;
            int localCount = TranslatorCore.TranslationCache.Count;

            _downloadBtn.Visible = false;
            _comparisonLabel.Show("");
            _actionButtonsRow.Visible = false;

            if (selected == null)
            {
                if (localCount == 0)
                {
                    _comparisonLabel.Say("No translation found for your language");
                    _comparisonLabel.Tone = Tone.Muted;
                }
                else
                {
                    _comparisonLabel.Say($"You have {localCount} local translations");
                    _comparisonLabel.Tone = Tone.Success;
                }
                return;
            }

            int remoteCount = selected.LineCount;
            if (localCount == 0)
            {
                _comparisonLabel.Show(Tr($"On the server: {remoteCount} lines by") + $" @{selected.Uploader}");
            }
            else
            {
                bool isOwnRemote = !string.IsNullOrEmpty(TranslatorCore.Config.api_token)
                                   && People.IsYou(selected.Uploader, TranslatorCore.Config.api_user);
                _comparisonLabel.Show(isOwnRemote
                    ? $"Local: {localCount} | Server (yours): {remoteCount}"
                    : $"Local: {localCount} | Server (@{selected.Uploader}): {remoteCount}");
            }
            _comparisonLabel.Tone = Tone.Plain;
            _actionButtonsRow.Visible = true;
            _downloadBtn.Visible = true;
        }

        private void SetButtonsInteractable(bool interactable)
        {
            _downloadBtn.Enabled = interactable;
        }

        public void UpdateAccountStatus()
        {
            bool isLoggedIn = !string.IsNullOrEmpty(TranslatorCore.Config.api_token);

            if (isLoggedIn)
            {
                string currentUser = TranslatorCore.Config.api_user;
                _accountStatusLabel.Show(Tr("Connected as") + $" @{currentUser}");
                _accountStatusLabel.Italic = true;
                _loginBtn.Visible = false;
            }
            else
            {
                _accountStatusLabel.Say("Optional: connect an account to share your translation later");
                _loginBtn.Visible = true;
            }

            _translationList?.Refresh();

            // Content changed, recalculate panel size
            RecalculateSize();
        }

        private void OnLoginClicked()
        {
            // Sync wizard state to Config so LoginPanel sees correct online_mode
            TranslatorCore.Config.online_mode = _onlineMode;

            Intents.OpenLogin();
        }

        private void OnDownloadClicked()
        {
            var selected = _translationList?.SelectedTranslation;
            if (selected == null || _isDownloading) return;

            // The same door as the Main's Download: it asks what must be asked before a local
            // file is replaced (another lineage, unpublished changes), then hands over.
            TranslatorUIManager.OfferDownload(selected, () => PerformDownload(selected));
        }

        private async void PerformDownload(TranslationInfo selected)
        {
            try
            {
                _isDownloading = true;
                _downloadStatusLabel.Say("Downloading...");
                _downloadStatusLabel.Tone = Tone.Warning;
                SetButtonsInteractable(false);

                await TranslatorUIManager.DownloadTranslation(selected, (success, message) =>
                {
                    _downloadStatusLabel.Show(message);
                    _downloadStatusLabel.Tone = success ? Tone.Success : Tone.Error;

                    if (success)
                    {
                        // Auto-advance to complete after successful download
                        TranslatorUIManager.RunDelayed(1.5f, () => ShowStep(WizardStep.Complete));
                    }
                    else
                    {
                        _isDownloading = false;
                        SetButtonsInteractable(true);
                    }
                });
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[Wizard.PerformDownload] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
                _isDownloading = false;
                SetButtonsInteractable(true);
            }
        }

        private void UpdateWizardBackendVisibility()
        {
            bool enabled = _wizardEnableToggle != null && _wizardEnableToggle.IsOn;
            if (_wizardBackendTypeSection != null) _wizardBackendTypeSection.Visible = enabled;

            if (!enabled)
            {
                _translationBackend = "none";
                _enableAI = false;
                return;
            }

            // If not online, force LLM (Translation APIs require internet)
            bool canUseTransApi = _onlineMode;
            _wizardBackendTypeDropdown?.SetInteractable(canUseTransApi);
            if (!canUseTransApi && _wizardBackendTypeDropdown?.SelectedValue == UIStyles.BackendTypeApi)
            {
                _wizardBackendTypeDropdown.SelectedValue = UIStyles.BackendTypeLLM;
            }

            string type = _wizardBackendTypeDropdown?.SelectedValue ?? UIStyles.BackendTypeLLM;
            bool isLLM = type == UIStyles.BackendTypeLLM;

            if (_wizardLlmSection != null) _wizardLlmSection.Visible = isLLM;
            if (_wizardTransApiSection != null) _wizardTransApiSection.Visible = !isLLM;

            if (isLLM)
            {
                _translationBackend = "llm";
                _enableAI = true;
            }
            else
            {
                string provider = _wizardProviderDropdown?.SelectedValue ?? "Google Translate";
                _translationBackend = provider == "DeepL" ? "deepl" : "google";
                _enableAI = false;

                if (_wizardGoogleSection != null) _wizardGoogleSection.Visible = provider == "Google Translate";
                if (_wizardDeeplSection != null) _wizardDeeplSection.Visible = provider == "DeepL";
            }
        }

        private void OnWizardEnableChanged()
        {
            UpdateWizardBackendVisibility();
            RecalculateSize();
        }

        private void OnWizardTypeChanged()
        {
            UpdateWizardBackendVisibility();
            RecalculateSize();
        }

        private void OnWizardProviderChanged()
        {
            UpdateWizardBackendVisibility();
            RecalculateSize();
        }

        private async void WizardTestGoogle()
        {
            try
            {
                if (string.IsNullOrEmpty(_googleApiKey))
                {
                    _wizardGoogleStatusLabel.Say("Enter an API key first");
                    _wizardGoogleStatusLabel.Tone = Tone.Warning;
                    return;
                }
                _wizardGoogleStatusLabel.Say("Testing...");
                _wizardGoogleStatusLabel.Tone = Tone.Secondary;

                bool success = await TranslatorCore.TestGoogleConnection(_googleApiKey);
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _wizardGoogleStatusLabel.Show(success ? "Connected!" : "Failed - check API key");
                    _wizardGoogleStatusLabel.Tone = success ? Tone.Success : Tone.Error;
                });
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[WizardTestGoogle] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
            }
        }

        private async void WizardTestDeepL()
        {
            try
            {
                if (string.IsNullOrEmpty(_deeplApiKey))
                {
                    _wizardDeeplStatusLabel.Say("Enter an API key first");
                    _wizardDeeplStatusLabel.Tone = Tone.Warning;
                    return;
                }
                _wizardDeeplStatusLabel.Say("Testing...");
                _wizardDeeplStatusLabel.Tone = Tone.Secondary;

                bool success = await TranslatorCore.TestDeepLConnection(_deeplApiKey, _deeplUseFree);
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _wizardDeeplStatusLabel.Show(success ? "Connected!" : "Failed - check API key and plan type");
                    _wizardDeeplStatusLabel.Tone = success ? Tone.Success : Tone.Error;
                });
            }
            catch (Exception _e)
            {
                TranslatorCore.LogError($"[WizardTestDeepL] {_e.GetType().Name}: {_e.Message}\n{_e.StackTrace}");
            }
        }

        private void ShowStep(WizardStep step)
        {
            _currentStep = step;

            // Hide all steps
            _welcomeStep.Visible = false;
            _onlineModeStep.Visible = false;
            _hotkeyStep.Visible = false;
            _languageSelectionStep.Visible = false;
            _translationChoiceStep.Visible = false;
            _aiConfigStep.Visible = false;
            _completeStep.Visible = false;

            // Show current step
            switch (step)
            {
                case WizardStep.Welcome:
                    _welcomeStep.Visible = true;
                    break;
                case WizardStep.OnlineMode:
                    _onlineModeStep.Visible = true;
                    break;
                case WizardStep.Hotkey:
                    _hotkeyStep.Visible = true;
                    break;
                case WizardStep.LanguageSelection:
                    _languageSelectionStep.Visible = true;
                    break;
                case WizardStep.TranslationChoice:
                    _translationChoiceStep.Visible = true;
                    OnTranslationChoiceEnter();
                    break;
                case WizardStep.AIConfig:
                    _aiConfigStep.Visible = true;
                    break;
                case WizardStep.Complete:
                    _completeStep.Visible = true;
                    // Key names are data — never translated. Written here rather than at
                    // construction, so the combination shown is the one just chosen.
                    _completeDescription.Show(
                        "You're all set!\n\n" +
                        $"Press {_hotkeyCapture.HotkeyString} to open settings at any time.\n\n" +
                        "The translator will automatically detect text in the game\nand translate it to your language.");
                    break;
            }

            // Recalculate panel size for new step content. RecalculateSize() itself waits a
            // frame for the layout to settle before measuring, which is what the wizard's own
            // one-frame-delay coroutine used to do by hand.
            RecalculateSize();
        }

        private async void TestAIConnection()
        {
            if (_aiStatusLabel == null) return;

            _aiStatusLabel.Say("Testing...");
            _aiStatusLabel.Tone = Tone.Warning;

            // Capture values before await
            string url = _aiUrl;
            string apiKey = _aiApiKey;

            try
            {
                bool success = await TranslatorCore.TestAIConnection(url, apiKey);

                // After await, we may be on a background thread (IL2CPP issue)
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success)
                    {
                        _aiStatusLabel.Say("Connection successful!");
                        _aiStatusLabel.Tone = Tone.Success;
                        // Auto-refresh models on successful test
                        RefreshModels();
                    }
                    else
                    {
                        _aiStatusLabel.Say("Connection failed");
                        _aiStatusLabel.Tone = Tone.Error;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _aiStatusLabel.Show(Tr("Error:") + $" {errorMsg}");
                    _aiStatusLabel.Tone = Tone.Error;
                });
            }
        }

        private async void RefreshModels()
        {
            string url = _aiUrl;
            string apiKey = _aiApiKey;

            try
            {
                string[] models = await TranslatorCore.FetchModels(url, apiKey);

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (models.Length > 0)
                    {
                        _modelDropdown.SetOptions(models);
                        // Keep current selection if still valid
                        if (!string.IsNullOrEmpty(_aiModel) && Array.IndexOf(models, _aiModel) >= 0)
                        {
                            _modelDropdown.SelectedValue = _aiModel;
                        }
                    }
                });
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Wizard] Failed to refresh models: {e.Message}");
            }
        }

        private void FinishWizard()
        {
            // Save all settings
            TranslatorCore.Config.online_mode = _onlineMode;
            TranslatorCore.Config.settings_hotkey = _hotkeyCapture.HotkeyString;
            TranslatorCore.Config.target_language = _targetLanguage;
            TranslatorCore.Config.translation_backend = _translationBackend;
            // Picking a backend here IS asking for it to run — the wizard's own step is titled
            // "Auto-Translation" and choosing "none" is how one declines. Deliberately not
            // "== llm": that older line left Google and DeepL users with the switch off while
            // they translated anyway, which is the confusion this flag now exists to end.
            TranslatorCore.Config.enable_ai = (_translationBackend != "none");
            TranslatorCore.Config.ai_url = _aiUrl;
            TranslatorCore.Config.ai_api_key = !string.IsNullOrEmpty(_aiApiKey) ? _aiApiKey : null;
            TranslatorCore.Config.ai_model = _aiModel;
            TranslatorCore.Config.game_context = _gameContext;
            TranslatorCore.Config.google_api_key = !string.IsNullOrEmpty(_googleApiKey) ? _googleApiKey : null;
            TranslatorCore.Config.deepl_api_key = !string.IsNullOrEmpty(_deeplApiKey) ? _deeplApiKey : null;
            TranslatorCore.Config.deepl_use_free = _deeplUseFree;
            // Opens the latch the whole mod waits on (TranslatorCore.SetupCompleted): until this
            // line the tick loop has been running but deliberately not touching the game — no
            // scanning, no translating, no cache written. Saved BEFORE anything is started, so a
            // crash in between cannot leave the mod acting on a game it was never allowed to.
            TranslatorCore.Config.first_run_completed = true;
            TranslatorCore.SaveConfig();

            // Start translation worker if any backend is enabled
            if (TranslatorCore.Config.IsTranslationEnabled)
            {
                TranslatorCore.EnsureWorkerRunning();
            }

            // Everything on screen right now was met while the latch was shut, so nothing was
            // applied to it. Re-submit it all — the same pipeline the Enable Translations toggle
            // and OptionsPanel.ApplySettings use, for the same reason: text the game will never
            // re-write on its own would otherwise stay untranslated until it next changes.
            TranslatorCore.ClearProcessingCaches();
            TranslatorScanner.ForceRefreshAllText(reapplyAllScales: true);

            // Same for the pictures (seen 2026-09-11 on a wizard re-run over a translation that
            // carries images): the sprite patch answered "no replacement" to everything set while
            // the latch was shut, and a sprite already on screen is never set again. This is the
            // pass a scene change runs (ImageReplacer.OnSceneChange) and the Images switch runs —
            // the files from disk, then the targeted apply. Fonts need nothing here: the
            // re-submitted text goes back through the font replacement, and the scene pass
            // (FontManager.ApplyUnityClonesToScene) runs on the scanner's tick; UI Toolkit
            // pictures are asked on every walk.
            ImageReplacer.LoadAllReplacements();
            ImageReplacer.ApplyToScene();

            // What the startup would have done had the setup already been complete. The wizard is
            // the trigger, not a timer: the answers exist now, so the work happens now.
            TranslatorUIManager.TriggerStartupTasks();

            SetActive(false);
            Intents.ShowMain();
        }

        protected override void OnClosePanelClicked()
        {
            // Don't allow closing wizard with X button during first run
        }
    }
}
