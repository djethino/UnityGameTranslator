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
    /// </summary>
    public class UploadPanel : TranslatorPanelBase
    {
        public override string Name => "Upload Translation";
        public override int MinWidth => 450;
        public override int MinHeight => 420;
        public override int PanelWidth => 450;
        public override int PanelHeight => 420;

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

        public UploadPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, 450);

            // Adaptive card - sizes to content
            var card = CreateAdaptiveCard(scrollContent, "UploadCard", 450);

            // Title
            _titleLabel = CreateTitle(card, "TitleLabel", "Upload Translation");

            UIStyles.CreateSpacer(card, 5);

            // Info section
            var infoBox = CreateSection(card, "InfoBox");

            _entriesLabel = UIFactory.CreateLabel(infoBox, "EntriesLabel", "Entries: 0", TextAnchor.MiddleLeft);
            _entriesLabel.color = UIStyles.TextPrimary;
            UIFactory.SetLayoutElement(_entriesLabel.gameObject, minHeight: 22);

            _gameLabel = UIFactory.CreateLabel(infoBox, "GameLabel", "Game: Unknown", TextAnchor.MiddleLeft);
            _gameLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_gameLabel.gameObject, minHeight: 22);

            _modeInfoLabel = UIFactory.CreateLabel(infoBox, "ModeInfoLabel", "", TextAnchor.MiddleLeft);
            _modeInfoLabel.fontStyle = FontStyle.Italic;
            _modeInfoLabel.fontSize = UIStyles.FontSizeSmall;
            _modeInfoLabel.color = UIStyles.TextMuted;
            UIFactory.SetLayoutElement(_modeInfoLabel.gameObject, minHeight: 20);

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
            UIFactory.SetLayoutElement(aiObj, minHeight: 22);

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
            UIFactory.SetLayoutElement(aiCorrectedObj, minHeight: 22);

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
            UIFactory.SetLayoutElement(manualObj, minHeight: 22);

            UIStyles.CreateSpacer(card, 10);

            // Notes
            var notesLabel = UIFactory.CreateLabel(card, "NotesLabel", "Notes (optional):", TextAnchor.MiddleLeft);
            notesLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(notesLabel.gameObject, minHeight: 18);

            _notesInput = UIFactory.CreateInputField(card, "NotesInput", "Add any notes about this translation...");
            UIFactory.SetLayoutElement(_notesInput.Component.gameObject, minHeight: 45, flexibleWidth: 9999);
            UIStyles.SetBackground(_notesInput.Component.gameObject, UIStyles.InputBackground);

            // Status
            _statusLabel = UIFactory.CreateLabel(card, "Status", "", TextAnchor.MiddleCenter);
            _statusLabel.fontSize = UIStyles.FontSizeNormal;
            UIFactory.SetLayoutElement(_statusLabel.gameObject, minHeight: 25);

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
                CheckUploadMode();
            }
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
                        _modeInfoLabel.text = $"Updating: ID #{TranslatorCore.ServerState?.SiteId}";
                        _uploadBtn.ButtonText.text = "Update";

                        // Restore existing type if available
                        if (!string.IsNullOrEmpty(result.ExistingTranslation?.Type))
                        {
                            SetUploadType(result.ExistingTranslation.Type);
                        }
                    }
                    else
                    {
                        // FORK mode
                        _uploadMode = UploadMode.Fork;
                        _titleLabel.text = "Fork Translation";
                        _modeInfoLabel.text = $"Forking from: {TranslatorCore.ServerState?.Uploader ?? "unknown"}";
                        _uploadBtn.ButtonText.text = "Fork";
                    }

                    _statusLabel.text = "";
                }
                else
                {
                    // NEW mode - show language selection
                    _uploadMode = UploadMode.New;
                    _titleLabel.text = "Upload Translation";
                    _modeInfoLabel.text = $"Languages: {TranslatorCore.Config.GetSourceLanguage() ?? "auto"} → {TranslatorCore.Config.GetTargetLanguage()}";
                    _uploadBtn.ButtonText.text = "Upload";
                    _statusLabel.text = "";
                }
            }
            catch (Exception e)
            {
                TranslatorCore.LogWarning($"[Upload] UUID check error: {e.Message}");
                _statusLabel.text = $"Error: {e.Message}";
                _statusLabel.color = UIStyles.StatusError;
            }
            finally
            {
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

                // Determine languages
                string srcLang = TranslatorCore.Config.GetSourceLanguage() ?? "English";
                string tgtLang = TranslatorCore.Config.GetTargetLanguage();

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

                    // Update server state
                    TranslatorCore.ServerState = new ServerTranslationState
                    {
                        Checked = true,
                        Exists = true,
                        IsOwner = true,
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
