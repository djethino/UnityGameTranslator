using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    public enum UploadMode
    {
        New,
        Update,
        Branch  // Contributing to existing Main (same UUID, becomes Branch)
    }

    /// <summary>
    /// Upload panel for sharing translations to the server.
    /// Handles new uploads, updates (owner), and branches (non-owner contributing).
    /// For NEW uploads, redirects to UploadSetupPanel for language/game selection first.
    /// </summary>
    public class UploadPanel : TranslatorPanelBase
    {
        public override string Name => "Upload Translation";
        public override int MinWidth => 450;
        public override int MinHeight => 300;
        public override int PanelWidth => 450;
        public override int PanelHeight => 420;

        protected override int MinPanelHeight => 300;

        // UI elements
        private Text _titleLabel;
        private Text _gameLabel;
        private Text _entriesLabel;
        private Text _modeInfoLabel;
        private Text _statusLabel;
        private InputFieldRef _notesInput;
        private Toggle _aiToggle;
        private Toggle _aiCorrectedToggle;
        private Toggle _manualToggle;
        private ButtonRef _backBtn;
        private ButtonRef _uploadBtn;

        // State
        private bool _isUploading;
        private bool _isChecking;
        private UploadMode _uploadMode;
        private string _uploadType = "ai";

        // For NEW uploads - selected from UploadSetupPanel
        private string _selectedSourceLanguage;
        private string _selectedTargetLanguage;
        private bool _setupComplete = false;

        public UploadPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Adaptive card - sizes to content (PanelWidth - 2*PanelPadding)
            var card = CreateAdaptiveCard(scrollContent, "UploadCard", PanelWidth - 40);

            // Title
            _titleLabel = CreateTitle(card, "TitleLabel", "Upload Translation");
            RegisterUIText(_titleLabel);

            UIStyles.CreateSpacer(card, 5);

            // Info section
            var infoBox = CreateSection(card, "InfoBox");

            _entriesLabel = UIFactory.CreateLabel(infoBox, "EntriesLabel", "Entries: 0", TextAnchor.MiddleLeft);
            _entriesLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_entriesLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(_entriesLabel);

            _gameLabel = UIFactory.CreateLabel(infoBox, "GameLabel", "Game: Unknown", TextAnchor.MiddleLeft);
            _gameLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_gameLabel.gameObject, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(_gameLabel);

            _modeInfoLabel = UIFactory.CreateLabel(infoBox, "ModeInfoLabel", "", TextAnchor.MiddleLeft);
            _modeInfoLabel.fontStyle = FontStyle.Italic;
            _modeInfoLabel.fontSize = UIStyles.FontSizeSmall;
            _modeInfoLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_modeInfoLabel.gameObject, minHeight: UIStyles.RowHeightSmall);
            RegisterUIText(_modeInfoLabel);

            UIStyles.CreateSpacer(card, 10);

            // Translation type section
            var typeTitle = UIStyles.CreateSectionTitle(card, "TypeLabel", "Translation Type");
            RegisterUIText(typeTitle);

            var typeBox = CreateSection(card, "TypeBox");

            // AI toggle
            var aiObj = UIFactory.CreateToggle(typeBox, "AIToggle", out _aiToggle, out var aiLabel);
            aiLabel.text = "AI (Ollama-generated)";
            aiLabel.color = UIStyles.TextSecondary;
            _aiToggle.isOn = true;
            UIHelpers.AddToggleListener(_aiToggle, (val) =>
            {
                if (val)
                {
                    _uploadType = "ai";
                    _aiCorrectedToggle.isOn = false;
                    _manualToggle.isOn = false;
                }
            });
            UIFactory.SetLayoutElement(aiObj, minHeight: UIStyles.RowHeightNormal);
            RegisterExcluded(aiLabel); // "Ollama" is a brand name

            // AI Corrected toggle
            var aiCorrectedObj = UIFactory.CreateToggle(typeBox, "AICorrectedToggle", out _aiCorrectedToggle, out var aiCorrectedLabel);
            aiCorrectedLabel.text = "AI Corrected (reviewed & fixed)";
            aiCorrectedLabel.color = UIStyles.TextSecondary;
            _aiCorrectedToggle.isOn = false;
            UIHelpers.AddToggleListener(_aiCorrectedToggle, (val) =>
            {
                if (val)
                {
                    _uploadType = "ai_corrected";
                    _aiToggle.isOn = false;
                    _manualToggle.isOn = false;
                }
            });
            UIFactory.SetLayoutElement(aiCorrectedObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(aiCorrectedLabel);

            // Manual toggle
            var manualObj = UIFactory.CreateToggle(typeBox, "ManualToggle", out _manualToggle, out var manualLabel);
            manualLabel.text = "Human (manually translated)";
            manualLabel.color = UIStyles.TextSecondary;
            _manualToggle.isOn = false;
            UIHelpers.AddToggleListener(_manualToggle, (val) =>
            {
                if (val)
                {
                    _uploadType = "human";
                    _aiToggle.isOn = false;
                    _aiCorrectedToggle.isOn = false;
                }
            });
            UIFactory.SetLayoutElement(manualObj, minHeight: UIStyles.RowHeightNormal);
            RegisterUIText(manualLabel);

            UIStyles.CreateSpacer(card, 10);

            // Notes
            var notesLabel = CreateSmallLabel(card, "NotesLabel", "Notes (optional):");
            RegisterUIText(notesLabel);

            _notesInput = CreateStyledInputField(card, "NotesInput", "Add any notes about this translation...", UIStyles.MultiLineSmall);

            // Status
            _statusLabel = CreateStatusLabel(card, "Status");
            RegisterUIText(_statusLabel);

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);
            RegisterUIText(cancelBtn.ButtonText);

            // Back button - only visible for NEW mode to go back to setup
            _backBtn = CreateSecondaryButton(buttonRow, "BackBtn", "← Back");
            _backBtn.OnClick += OnBackToSetup;
            _backBtn.Component.gameObject.SetActive(false); // Hidden by default
            RegisterUIText(_backBtn.ButtonText);

            _uploadBtn = CreatePrimaryButton(buttonRow, "UploadBtn", "Upload");
            _uploadBtn.OnClick += () =>
            {
                try
                {
                    TranslatorCore.LogInfo("[UploadPanel] Upload button clicked!");
                    DoUpload();
                }
                catch (Exception e)
                {
                    TranslatorCore.LogError($"[UploadPanel] Exception in click handler: {e}");
                }
            };
            RegisterUIText(_uploadBtn.ButtonText);
        }

        private void OnBackToSetup()
        {
            // Close this panel and reopen UploadSetupPanel
            SetActive(false);

            // Reopen setup panel - it will pre-populate with detected game
            TranslatorUIManager.UploadSetupPanel.ShowForSetup((game, srcLang, tgtLang) =>
            {
                ContinueAfterSetup(game, srcLang, tgtLang);
            });
        }

        public override void SetActive(bool active)
        {
            // Only trigger logic when transitioning from inactive to active
            // (PanelDragger calls SetActive(true) every frame when mouse is in drag/resize area)
            bool wasActive = Enabled;

            // Skip reset if setup was just completed (ContinueAfterSetup sets _setupComplete = true before calling SetActive)
            bool skipReset = active && _setupComplete;

            base.SetActive(active);

            // Only run on first activation, not repeated SetActive(true) calls
            if (active && !wasActive && !skipReset)
            {
                // Reset setup state when opening fresh
                _setupComplete = false;
                _selectedSourceLanguage = null;
                _selectedTargetLanguage = null;
                CheckUploadMode();
            }
        }

        /// <summary>
        /// Called by UploadSetupPanel when user completes setup for NEW upload.
        /// </summary>
        public void ContinueAfterSetup(GameInfo game, string sourceLanguage, string targetLanguage)
        {
            _selectedSourceLanguage = sourceLanguage;
            _selectedTargetLanguage = targetLanguage;
            _setupComplete = true;

            // Update display
            _uploadMode = UploadMode.New;
            _titleLabel.text = "Upload Translation";
            _modeInfoLabel.text = $"Languages: {sourceLanguage} -> {targetLanguage}";
            _uploadBtn.ButtonText.text = "Upload";
            _statusLabel.text = "";

            // Enable upload button (we're ready to upload after setup)
            _isChecking = false;
            _uploadBtn.Component.interactable = true;

            // Show back button for NEW mode (user can go back to change game/languages)
            _backBtn.Component.gameObject.SetActive(true);

            RefreshInfo();

            // Show the upload panel
            SetActive(true);
        }

        private async void CheckUploadMode()
        {
            TranslatorCore.LogInfo("[UploadPanel] CheckUploadMode started");
            _isChecking = true;
            _statusLabel.text = "Checking...";
            _statusLabel.color = UIStyles.StatusWarning;
            _uploadBtn.Component.interactable = false;

            // Hide back button (only shown for NEW mode after setup)
            _backBtn.Component.gameObject.SetActive(false);

            RefreshInfo();

            try
            {
                // Check UUID to determine mode
                var result = await ApiClient.CheckUuid(TranslatorCore.FileUuid);

                // After await, we may be on a background thread (IL2CPP issue)
                // Use RunOnMainThread for all UI operations
                TranslatorCore.LogInfo($"[UploadPanel] CheckUuid result: Exists={result.Exists}, IsOwner={result.IsOwner}, Success={result.Success}");

                // Handle API errors separately from UUID not existing
                if (!result.Success)
                {
                    var errorMsg = result.Error;
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _statusLabel.text = $"Error: {errorMsg}";
                        _statusLabel.color = UIStyles.StatusError;
                        _isChecking = false;
                        _uploadBtn.Component.interactable = false;
                    });
                    return;
                }

                if (result.Exists)
                {
                    if (result.IsOwner)
                    {
                        // UPDATE mode - update non-UI state first
                        TranslatorCore.LogInfo("[UploadPanel] Mode set to UPDATE");
                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Checked = true,
                            Exists = true,
                            IsOwner = true,
                            Role = result.Role,
                            BranchesCount = result.BranchesCount,
                            SiteId = result.ExistingTranslation?.Id,
                            Uploader = TranslatorCore.Config.api_user,
                            Type = result.ExistingTranslation?.Type,
                            Notes = result.ExistingTranslation?.Notes,
                            Hash = result.ExistingTranslation?.FileHash
                        };

                        // Capture for closure
                        var siteId = TranslatorCore.ServerState.SiteId;
                        var existingType = result.ExistingTranslation?.Type;
                        var existingNotes = result.ExistingTranslation?.Notes ?? "";

                        TranslatorUIManager.RunOnMainThread(() =>
                        {
                            _uploadMode = UploadMode.Update;
                            _titleLabel.text = "Update Translation";
                            _modeInfoLabel.text = $"Updating: ID #{siteId}";
                            _uploadBtn.ButtonText.text = "Update";

                            if (!string.IsNullOrEmpty(existingType))
                            {
                                SetUploadType(existingType);
                            }
                            _notesInput.Text = existingNotes;

                            _statusLabel.text = "";
                            _isChecking = false;
                            _uploadBtn.Component.interactable = true;
                            TranslatorCore.LogInfo($"[UploadPanel] UPDATE mode ready");
                        });
                    }
                    else
                    {
                        // BRANCH mode - update non-UI state first
                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Checked = true,
                            Exists = true,
                            IsOwner = false,
                            Role = TranslationRole.Branch,
                            MainUsername = result.MainUsername,
                            SiteId = result.OriginalTranslation?.Id,
                            Uploader = result.OriginalTranslation?.Uploader,
                            Type = result.OriginalTranslation?.Type
                        };

                        // Capture for closure
                        var uploader = TranslatorCore.ServerState.Uploader ?? "unknown";
                        var originalType = result.OriginalTranslation?.Type ?? "ai";

                        TranslatorUIManager.RunOnMainThread(() =>
                        {
                            _uploadMode = UploadMode.Branch;
                            _titleLabel.text = "Contribute as Branch";
                            _modeInfoLabel.text = $"Contributing to: @{uploader}";
                            _uploadBtn.ButtonText.text = "Contribute";
                            SetUploadType(originalType);
                            _statusLabel.text = "";
                            _isChecking = false;
                            _uploadBtn.Component.interactable = true;
                        });
                    }
                }
                else
                {
                    // NEW mode - redirect to UploadSetupPanel for game/language selection
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _uploadMode = UploadMode.New;
                        _isChecking = false;
                        SetActive(false);

                        TranslatorUIManager.UploadSetupPanel.ShowForSetup((game, srcLang, tgtLang) =>
                        {
                            ContinueAfterSetup(game, srcLang, tgtLang);
                        });
                    });
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Upload] UUID check error: {e.Message}");
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _statusLabel.text = $"Error: {errorMsg}";
                    _statusLabel.color = UIStyles.StatusError;
                    _isChecking = false;
                    _uploadBtn.Component.interactable = true;
                });
            }
        }

        private void SetUploadType(string type)
        {
            _uploadType = type;
            _aiToggle.isOn = type == "ai";
            _aiCorrectedToggle.isOn = type == "ai_corrected";
            _manualToggle.isOn = type == "human";
        }

        private void RefreshInfo()
        {
            if (_entriesLabel == null) return;

            _entriesLabel.text = $"Entries: {TranslatorCore.TranslationCache.Count}";

            var gameInfo = TranslatorCore.CurrentGame;
            _gameLabel.text = gameInfo != null ? $"Game: {gameInfo.name}" : "Game: Unknown";
        }

        private async void DoUpload()
        {
            TranslatorCore.LogInfo($"[UploadPanel] DoUpload called - isUploading={_isUploading}, isChecking={_isChecking}, mode={_uploadMode}");

            if (_isUploading || _isChecking)
            {
                TranslatorCore.LogWarning("[UploadPanel] DoUpload blocked - already uploading or checking");
                return;
            }

            if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                TranslatorCore.LogWarning("[UploadPanel] DoUpload blocked - no API token");
                _statusLabel.text = "Please login first";
                _statusLabel.color = UIStyles.StatusError;
                return;
            }

            _isUploading = true;
            _uploadBtn.Component.interactable = false;

            string actionText = _uploadMode == UploadMode.Update ? "Updating..." :
                               (_uploadMode == UploadMode.Branch ? "Contributing..." : "Uploading...");
            _statusLabel.text = actionText;
            _statusLabel.color = UIStyles.StatusWarning;

            // Capture values before async (for use in RunOnMainThread callbacks)
            var uploadMode = _uploadMode;
            var uploadType = _uploadType;
            string notes = _notesInput.Text;

            try
            {
                // Determine languages based on mode
                string srcLang, tgtLang;
                if (_uploadMode == UploadMode.New && _setupComplete)
                {
                    // NEW: Use selected languages from UploadSetupPanel
                    srcLang = _selectedSourceLanguage;
                    tgtLang = _selectedTargetLanguage;
                }
                else
                {
                    // UPDATE or FORK: Server will use existing languages (we send these but server ignores)
                    srcLang = TranslatorCore.Config.GetSourceLanguage() ?? "English";
                    tgtLang = TranslatorCore.Config.GetTargetLanguage();
                }

                // Build upload request
                var request = new UploadRequest
                {
                    SteamId = TranslatorCore.CurrentGame?.steam_id,
                    GameName = TranslatorCore.CurrentGame?.name ?? "Unknown Game",
                    SourceLanguage = srcLang,
                    TargetLanguage = tgtLang,
                    Type = _uploadType,
                    Status = "in_progress",
                    Content = BuildTranslationContent(),
                    Notes = notes
                };

                TranslatorCore.LogInfo($"[UploadPanel] Calling ApiClient.UploadTranslation...");
                var result = await ApiClient.UploadTranslation(request);
                TranslatorCore.LogInfo($"[UploadPanel] Upload result: Success={result.Success}, Id={result.TranslationId}, Error={result.Error}");

                // After await, we may be on a background thread (IL2CPP issue)
                // Use RunOnMainThread for all UI operations
                if (result.Success)
                {
                    // Update non-UI state (thread-safe)
                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        Exists = true,
                        IsOwner = true,
                        Role = result.Role,
                        SiteId = result.TranslationId,
                        Uploader = TranslatorCore.Config.api_user,
                        Hash = result.FileHash,
                        Type = uploadType,
                        Notes = notes
                    };
                    TranslatorCore.LastSyncedHash = result.FileHash;
                    TranslatorCore.SaveCache();
                    TranslatorCore.SaveAncestorCache();
                    TranslatorUIManager.HasPendingUpdate = false;
                    TranslatorUIManager.PendingUpdateInfo = null;
                    TranslatorUIManager.PendingUpdateDirection = UpdateDirection.None;
                    TranslatorUIManager.NotificationDismissed = false;

                    // Capture for closure
                    var translationId = result.TranslationId;
                    string successMsg = uploadMode == UploadMode.Update ? "Updated" :
                                       (uploadMode == UploadMode.Branch ? "Contributed" : "Uploaded");

                    // Update UI on main thread
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _statusLabel.text = $"{successMsg}! ID: {translationId}";
                        _statusLabel.color = UIStyles.StatusSuccess;
                    });

                    await System.Threading.Tasks.Task.Delay(2000);

                    // Close panel and refresh on main thread
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _isUploading = false;
                        _uploadBtn.Component.interactable = true;
                        SetActive(false);
                        TranslatorUIManager.MainPanel?.RefreshUI();
                    });
                    return; // Skip finally block UI updates (already done above)
                }
                else
                {
                    var errorMsg = result.Error;
                    TranslatorUIManager.RunOnMainThread(() =>
                    {
                        _statusLabel.text = $"Error: {errorMsg}";
                        _statusLabel.color = UIStyles.StatusError;
                        _isUploading = false;
                        _uploadBtn.Component.interactable = true;
                    });
                    return;
                }
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _statusLabel.text = $"Error: {errorMsg}";
                    _statusLabel.color = UIStyles.StatusError;
                    _isUploading = false;
                    _uploadBtn.Component.interactable = true;
                });
            }
        }

        private string BuildTranslationContent()
        {
            var output = new System.Collections.Generic.Dictionary<string, object>();
            output["_uuid"] = TranslatorCore.FileUuid;

            if (TranslatorCore.CurrentGame != null)
            {
                output["_game"] = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["name"] = TranslatorCore.CurrentGame.name,
                    ["steam_id"] = TranslatorCore.CurrentGame.steam_id
                };
            }

            // Use same format as SaveCache: {"v": "value", "t": "tag"}
            foreach (var kv in TranslatorCore.TranslationCache)
            {
                output[kv.Key] = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["v"] = kv.Value.Value,
                    ["t"] = kv.Value.Tag ?? "A"
                };
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(output, Newtonsoft.Json.Formatting.None);
        }
    }
}
