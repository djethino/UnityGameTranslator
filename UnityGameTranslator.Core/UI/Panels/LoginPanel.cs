using System;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Login panel using Device Flow authentication.
    /// </summary>
    public class LoginPanel : TranslatorPanelBase
    {
        public override string Name => "Login";
        public override int MinWidth => 420;
        public override int MinHeight => 300;
        public override int PanelWidth => 420;
        public override int PanelHeight => 420;

        protected override int MinPanelHeight => 300;

        private Text _instructionLabel;
        private Text _codeLabel;
        private Text _statusLabel;
        private ButtonRef _startLoginBtn;
        private ButtonRef _openWebsiteBtn;
        private string _verificationUri;
        private bool _isPolling;
        private string _deviceCode;
        private string _userCode;

        public LoginPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Adaptive card - sizes to content
            var card = CreateAdaptiveCard(scrollContent, "LoginCard", PanelWidth - 40);

            var title = CreateTitle(card, "Title", "Connect Account");
            RegisterUIText(title);

            UIStyles.CreateSpacer(card, 10);

            // Instructions
            _instructionLabel = UIFactory.CreateLabel(card, "Instructions",
                "Click the button below to start the login process.\n" +
                "You will receive a code to enter on the website.",
                TextAnchor.MiddleCenter);
            _instructionLabel.fontSize = UIStyles.FontSizeNormal;
            _instructionLabel.color = UIStyles.TextSecondary;
            UIFactory.SetLayoutElement(_instructionLabel.gameObject, minHeight: UIStyles.MultiLineSmall);
            RegisterUIText(_instructionLabel);

            UIStyles.CreateSpacer(card, 10);

            // Code display (initially hidden) - Excluded: device code, not translatable
            _codeLabel = UIFactory.CreateLabel(card, "CodeLabel", "", TextAnchor.MiddleCenter);
            _codeLabel.fontSize = UIStyles.CodeDisplayFontSize;
            _codeLabel.fontStyle = FontStyle.Bold;
            _codeLabel.color = UIStyles.TextAccent;
            UIFactory.SetLayoutElement(_codeLabel.gameObject, minHeight: UIStyles.CodeDisplayHeight);
            _codeLabel.gameObject.SetActive(false);
            RegisterExcluded(_codeLabel);

            // Open website button (initially hidden)
            _openWebsiteBtn = CreatePrimaryButton(card, "OpenWebsiteBtn", "Open Website", 200);
            UIFactory.SetLayoutElement(_openWebsiteBtn.Component.gameObject, flexibleWidth: 9999);
            _openWebsiteBtn.OnClick += OpenVerificationUrl;
            _openWebsiteBtn.Component.gameObject.SetActive(false);
            RegisterUIText(_openWebsiteBtn.ButtonText);

            // Status label
            _statusLabel = CreateStatusLabel(card, "Status");
            RegisterUIText(_statusLabel);

            // Start login button
            _startLoginBtn = CreatePrimaryButton(card, "StartLoginBtn", "Start Login", 200);
            UIFactory.SetLayoutElement(_startLoginBtn.Component.gameObject, flexibleWidth: 9999);
            _startLoginBtn.OnClick += StartLogin;
            RegisterUIText(_startLoginBtn.ButtonText);

            // Cancel button - in fixed footer (outside scroll)
            var cancelBtn = CreateSecondaryButton(buttonRow, "CancelBtn", "Cancel");
            cancelBtn.OnClick += CancelLogin;
            RegisterUIText(cancelBtn.ButtonText);
        }

        private async void StartLogin()
        {
            if (_isPolling) return;

            _startLoginBtn.Component.interactable = false;
            _statusLabel.text = "Requesting code...";
            _statusLabel.color = UIStyles.StatusWarning;

            try
            {
                var result = await ApiClient.InitiateDeviceFlow();

                if (result.Success)
                {
                    _deviceCode = result.DeviceCode;
                    _userCode = result.UserCode;
                    _verificationUri = result.VerificationUri;

                    _codeLabel.text = _userCode;
                    _codeLabel.gameObject.SetActive(true);

                    _openWebsiteBtn.Component.gameObject.SetActive(true);
                    _startLoginBtn.Component.gameObject.SetActive(false);

                    _instructionLabel.text = "Click the button below to open the website,\nthen enter this code:";
                    _statusLabel.text = "Waiting for authorization...";
                    _statusLabel.color = UIStyles.StatusInfo;

                    _isPolling = true;
                    PollForAuth();
                }
                else
                {
                    _statusLabel.text = $"Error: {result.Error}";
                    _statusLabel.color = UIStyles.StatusError;
                    _startLoginBtn.Component.interactable = true;
                }
            }
            catch (Exception e)
            {
                _statusLabel.text = $"Error: {e.Message}";
                _statusLabel.color = UIStyles.StatusError;
                _startLoginBtn.Component.interactable = true;
            }
        }

        private async void PollForAuth()
        {
            while (_isPolling)
            {
                await System.Threading.Tasks.Task.Delay(5000);

                if (!_isPolling) break;

                try
                {
                    var result = await ApiClient.PollDeviceFlow(_deviceCode);

                    if (result.Success)
                    {
                        // Save token and the server it was issued from (for security)
                        TranslatorCore.Config.api_token = result.AccessToken;
                        TranslatorCore.Config.api_user = result.UserName;
                        TranslatorCore.Config.api_token_server = TranslatorCore.Config.api_base_url ?? PluginInfo.ApiBaseUrl;
                        TranslatorCore.SaveConfig();
                        ApiClient.SetAuthToken(result.AccessToken);

                        _statusLabel.text = $"Logged in as {result.UserName}!";
                        _statusLabel.color = UIStyles.StatusSuccess;

                        _isPolling = false;

                        // Refresh panels that show login status
                        TranslatorUIManager.WizardPanel?.UpdateAccountStatus();
                        TranslatorUIManager.MainPanel?.RefreshUI();

                        await System.Threading.Tasks.Task.Delay(2000);
                        SetActive(false);
                        return;
                    }
                    else if (!result.Pending)
                    {
                        // Not pending and not success = expired or error
                        _statusLabel.text = result.Error ?? "Code expired. Please try again.";
                        _statusLabel.color = UIStyles.StatusError;
                        _isPolling = false;
                        ResetUI();
                    }
                    // If Pending, continue polling
                }
                catch (Exception e)
                {
                    TranslatorCore.LogWarning($"[Login] Poll error: {e.Message}");
                }
            }
        }

        private void CancelLogin()
        {
            _isPolling = false;
            ResetUI();
            SetActive(false);
        }

        private void OpenVerificationUrl()
        {
            if (!string.IsNullOrEmpty(_verificationUri))
            {
                Application.OpenURL(_verificationUri);
                TranslatorCore.LogInfo($"[Login] Opening verification URL: {_verificationUri}");
            }
        }

        private void ResetUI()
        {
            _startLoginBtn.Component.interactable = true;
            _startLoginBtn.Component.gameObject.SetActive(true);
            _openWebsiteBtn.Component.gameObject.SetActive(false);
            _codeLabel.gameObject.SetActive(false);
            _instructionLabel.text = "Click the button below to start the login process.\n" +
                                      "You will receive a code to enter on the website.";
            _statusLabel.text = "";
            _verificationUri = null;
        }
    }
}
