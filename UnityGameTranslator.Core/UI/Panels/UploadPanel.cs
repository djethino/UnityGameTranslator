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
        Fork
    }

    /// <summary>
    /// Upload panel for sharing translations to the server.
    /// Handles new uploads, updates (owner), and forks (non-owner).
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

            UIStyles.CreateSpacer(card, 5);

            // Info section
            var infoBox = CreateSection(card, "InfoBox");

            _entriesLabel = UIFactory.CreateLabel(infoBox, "EntriesLabel", "Entries: 0", TextAnchor.MiddleLeft);
            _entriesLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_entriesLabel.gameObject, minHeight: UIStyles.RowHeightNormal);

            _gameLabel = UIFactory.CreateLabel(infoBox, "GameLabel", "Game: Unknown", TextAnchor.MiddleLeft);
            _gameLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_gameLabel.gameObject, minHeight: UIStyles.RowHeightNormal);

            _modeInfoLabel = UIFactory.CreateLabel(infoBox, "ModeInfoLabel", "", TextAnchor.MiddleLeft);
            _modeInfoLabel.fontStyle = FontStyle.Italic;
            _modeInfoLabel.fontSize = UIStyles.FontSizeSmall;
            _modeInfoLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_modeInfoLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            UIStyles.CreateSpacer(card, 10);

            // Translation type section
            UIStyles.CreateSectionTitle(card, "TypeLabel", "Translation Type");

            var typeBox = CreateSection(card, "TypeBox");

            // AI toggle
            var aiObj = UIFactory.CreateToggle(typeBox, "AIToggle", out _aiToggle, out var aiLabel);
            aiLabel.text = "AI (Ollama-generated)";
            aiLabel.color = UIStyles.TextSecondary;
            _aiToggle.isOn = true;
            _aiToggle.onValueChanged.AddListener((val) =>
            {
                if (val)
                {
                    _uploadType = "ai";
                    _aiCorrectedToggle.isOn = false;
                    _manualToggle.isOn = false;
                }
            });
            UIFactory.SetLayoutElement(aiObj, minHeight: UIStyles.RowHeightNormal);

            // AI Corrected toggle
            var aiCorrectedObj = UIFactory.CreateToggle(typeBox, "AICorrectedToggle", out _aiCorrectedToggle, out var aiCorrectedLabel);
            aiCorrectedLabel.text = "AI Corrected (reviewed & fixed)";
            aiCorrectedLabel.color = UIStyles.TextSecondary;
            _aiCorrectedToggle.isOn = false;
            _aiCorrectedToggle.onValueChanged.AddListener((val) =>
            {
                if (val)
                {
                    _uploadType = "ai_corrected";
                    _aiToggle.isOn = false;
                    _manualToggle.isOn = false;
                }
            });
            UIFactory.SetLayoutElement(aiCorrectedObj, minHeight: UIStyles.RowHeightNormal);

            // Manual toggle
            var manualObj = UIFactory.CreateToggle(typeBox, "ManualToggle", out _manualToggle, out var manualLabel);
            manualLabel.text = "Human (manually translated)";
            manualLabel.color = UIStyles.TextSecondary;
            _manualToggle.isOn = false;
            _manualToggle.onValueChanged.AddListener((val) =>
            {
                if (val)
                {
                    _uploadType = "human";
                    _aiToggle.isOn = false;
                    _aiCorrectedToggle.isOn = false;
                }
            });
            UIFactory.SetLayoutElement(manualObj, minHeight: UIStyles.RowHeightNormal);

            UIStyles.CreateSpacer(card, 10);

            // Notes
            var notesLabel = CreateSmallLabel(card, "NotesLabel", "Notes (optional):");

            _notesInput = CreateStyledInputField(card, "NotesInput", "Add any notes about this translation...", UIStyles.MultiLineSmall);

            // Status
            _statusLabel = CreateStatusLabel(card, "Status");

            // Buttons - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += () => SetActive(false);

            _uploadBtn = CreatePrimaryButton(buttonRow, "UploadBtn", "Upload");
            _uploadBtn.OnClick += DoUpload;
        }

        public override void SetActive(bool active)
        {
            base.SetActive(active);
            if (active)
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

            RefreshInfo();

            // Show the upload panel
            SetActive(true);
        }

        private async void CheckUploadMode()
        {
            _isChecking = true;
            _statusLabel.text = "Checking...";
            _statusLabel.color = UIStyles.StatusWarning;
            _uploadBtn.Component.interactable = false;

            RefreshInfo();

            try
            {
                // Check UUID to determine mode
                var result = await ApiClient.CheckUuid(TranslatorCore.FileUuid);

                if (result.Success && result.Exists)
                {
                    if (result.IsOwner)
                    {
                        // UPDATE mode
                        _uploadMode = UploadMode.Update;
                        _titleLabel.text = "Update Translation";

                        // Update ServerState from API response
                        TranslatorCore.ServerState = new ServerTranslationState
                        {
                            Checked = true,
                            Exists = true,
                            IsOwner = true,
                            Role = TranslationRole.Main,
                            BranchesCount = result.BranchesCount,
                            SiteId = result.ExistingTranslation?.Id,
                            Uploader = TranslatorCore.Config.api_user,
                            Type = result.ExistingTranslation?.Type,
                            Notes = result.ExistingTranslation?.Notes,
                            Hash = result.ExistingTranslation?.FileHash
                        };

                        _modeInfoLabel.text = $"Updating: ID #{TranslatorCore.ServerState.SiteId}";
                        _uploadBtn.ButtonText.text = "Update";

                        // Restore existing type if available
                        if (!string.IsNullOrEmpty(result.ExistingTranslation?.Type))
                        {
                            SetUploadType(result.ExistingTranslation.Type);
                        }

                        // Restore existing notes
                        _notesInput.Text = result.ExistingTranslation?.Notes ?? "";

                        _statusLabel.text = "";
                        _isChecking = false;
                        _uploadBtn.Component.interactable = true;
                    }
                    else
                    {
                        // FORK mode
                        _uploadMode = UploadMode.Fork;
                        _titleLabel.text = "Fork Translation";

                        // Update ServerState from API response
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

                        _modeInfoLabel.text = $"Forking from: {TranslatorCore.ServerState.Uploader ?? "unknown"}";
                        _uploadBtn.ButtonText.text = "Fork";

                        // Restore type from original translation
                        SetUploadType(result.OriginalTranslation?.Type ?? "ai");

                        _statusLabel.text = "";
                        _isChecking = false;
                        _uploadBtn.Component.interactable = true;
                    }
                }
                else
                {
                    // NEW mode - redirect to UploadSetupPanel for game/language selection
                    _uploadMode = UploadMode.New;
                    _isChecking = false;

                    // Close this panel and open UploadSetupPanel
                    SetActive(false);

                    TranslatorUIManager.UploadSetupPanel.ShowForSetup((game, srcLang, tgtLang) =>
                    {
                        // This is called when user completes setup
                        ContinueAfterSetup(game, srcLang, tgtLang);
                    });
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Upload] UUID check error: {e.Message}");
                _statusLabel.text = $"Error: {e.Message}";
                _statusLabel.color = UIStyles.StatusError;
                _isChecking = false;
                _uploadBtn.Component.interactable = true;
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
            if (_isUploading || _isChecking) return;

            if (string.IsNullOrEmpty(TranslatorCore.Config.api_token))
            {
                _statusLabel.text = "Please login first";
                _statusLabel.color = UIStyles.StatusError;
                return;
            }

            _isUploading = true;
            _uploadBtn.Component.interactable = false;

            string actionText = _uploadMode == UploadMode.Update ? "Updating..." :
                               (_uploadMode == UploadMode.Fork ? "Forking..." : "Uploading...");
            _statusLabel.text = actionText;
            _statusLabel.color = UIStyles.StatusWarning;

            try
            {
                string notes = _notesInput.Text;

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

                var result = await ApiClient.UploadTranslation(request);

                if (result.Success)
                {
                    string successMsg = _uploadMode == UploadMode.Update ? "Updated" :
                                       (_uploadMode == UploadMode.Fork ? "Forked" : "Uploaded");
                    _statusLabel.text = $"{successMsg}! ID: {result.TranslationId}";
                    _statusLabel.color = UIStyles.StatusSuccess;

                    // Update server state - after upload, user is always the Main (owner)
                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        Exists = true,
                        IsOwner = true,
                        Role = TranslationRole.Main,
                        SiteId = result.TranslationId,
                        Uploader = TranslatorCore.Config.api_user,
                        Hash = result.FileHash,
                        Type = _uploadType,
                        Notes = notes
                    };

                    // Update last synced hash for multi-device sync detection
                    TranslatorCore.LastSyncedHash = result.FileHash;

                    // Save cache to persist _source.hash
                    TranslatorCore.SaveCache();

                    // Save as ancestor for future merge detection
                    TranslatorCore.SaveAncestorCache();

                    // Clear pending update state
                    TranslatorUIManager.HasPendingUpdate = false;
                    TranslatorUIManager.PendingUpdateInfo = null;
                    TranslatorUIManager.PendingUpdateDirection = UpdateDirection.None;
                    TranslatorUIManager.NotificationDismissed = false;

                    await System.Threading.Tasks.Task.Delay(2000);
                    SetActive(false);

                    // Refresh main panel if visible
                    TranslatorUIManager.MainPanel?.RefreshUI();
                }
                else
                {
                    _statusLabel.text = $"Error: {result.Error}";
                    _statusLabel.color = UIStyles.StatusError;
                }
            }
            catch (Exception e)
            {
                _statusLabel.text = $"Error: {e.Message}";
                _statusLabel.color = UIStyles.StatusError;
            }
            finally
            {
                _isUploading = false;
                _uploadBtn.Component.interactable = true;
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

            foreach (var kv in TranslatorCore.TranslationCache)
            {
                output[kv.Key] = kv.Value;
            }

            return Newtonsoft.Json.JsonConvert.SerializeObject(output, Newtonsoft.Json.Formatting.None);
        }
    }
}
