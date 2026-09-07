using System;
using Newtonsoft.Json.Linq;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Login panel using Device Flow authentication via SSE.
    /// </summary>
    public class LoginPanel : TranslatorPanelBase
    {
        public override string Name => "Login";
        public override int MinWidth => 380;
        public override int MinHeight => 200;
        public override int PanelWidth => 420;
        public override int PanelHeight => 350;

        protected override int MinPanelHeight => 200;

        private LabelHandle _instructions;
        private LabelHandle _code;
        private StatusLine _status;
        private ButtonHandle _startLoginBtn;
        private ButtonHandle _openWebsiteBtn;
        private ButtonHandle _copyCodeBtn;
        private Host _codeRow;
        private string _verificationUri;
        private SseClient _sseClient;
        private string _deviceCode;
        private string _userCode;

        /// <summary>What the panel says before a code has been asked for — and again after a reset.</summary>
        private const string StartInstructions =
            "Click the button below to start the login process.\n" +
            "You will receive a code to enter on the website.";

        public LoginPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            Layout(out var body, out var footer, PanelWidth - 40);

            // Adaptive card - sizes to content
            var card = Stacks.Card(body, "LoginCard", PanelWidth - 40);

            Labels.Create(card, "Title", "Connect Account", TextRole.Title);

            Stacks.Spacer(card, 10);

            // Instructions — rewritten by the code as the flow advances, so Dynamic.
            _instructions = Labels.Create(card, "Instructions", StartInstructions, TextRole.Description,
                                          policy: TextPolicy.Dynamic, minHeight: UIStyles.MultiLineSmall);

            Stacks.Spacer(card, 10);

            // Code display row (initially hidden) - Excluded: device code, not translatable
            //
            // ⚠ Trough surface and small padding, as wide as its content: that is what this row has
            // always shown. It was asked for transparent with no padding, but the factory reads a
            // clear colour and a zero padding as "nothing given" and paints its defaults — the
            // viewport colour, five pixels each side. Stated here so the screen does not change;
            // whether the band is wanted is a decision for another day (see the report).
            _codeRow = Stacks.Horizontal(card, "CodeRow", spacing: 8, pad: Pad.All(UIStyles.SmallSpacing),
                                         placement: Placement.MiddleCenter, surface: Surface.Trough,
                                         fill: Fill.Content, minHeight: UIStyles.CodeDisplayHeight);
            _codeRow.Visible = false;

            _code = Labels.Create(_codeRow, "CodeLabel", "", TextRole.Code, policy: TextPolicy.Excluded);

            // Copy button
            _copyCodeBtn = Buttons.Secondary(_codeRow, "CopyCodeBtn", "Copy", minWidth: 60, policy: TextPolicy.Dynamic);
            _copyCodeBtn.Clicked += CopyCodeToClipboard;

            // Open website button (initially hidden)
            _openWebsiteBtn = Buttons.Create(card, "OpenWebsiteBtn", "Open Website", ButtonTone.Primary,
                                             minWidth: 200, fill: Fill.Stretch);
            _openWebsiteBtn.Clicked += OpenVerificationUrl;
            _openWebsiteBtn.Visible = false;

            // Status label
            _status = StatusLine.Create(card, "Status");

            // Start login button
            _startLoginBtn = Buttons.Create(card, "StartLoginBtn", "Start Login", ButtonTone.Primary,
                                            minWidth: 200, fill: Fill.Stretch);
            _startLoginBtn.Clicked += StartLogin;

            // Cancel button - in fixed footer (outside scroll)
            var cancelBtn = Buttons.Secondary(footer, "CancelBtn", "Cancel");
            cancelBtn.Clicked += CancelLogin;
        }

        public override void SetActive(bool active)
        {
            if (active && !TranslatorCore.Config.online_mode)
            {
                TranslatorCore.LogWarning("[Login] Cannot open login panel in offline mode");
                return;
            }
            base.SetActive(active);
        }

        private async void StartLogin()
        {
            if (_sseClient != null) return;

            if (!TranslatorCore.Config.online_mode)
            {
                _status.Say("Offline mode - enable Online Mode in Mod Options first", Tone.Error);
                return;
            }

            _startLoginBtn.Enabled = false;
            _status.Say("Requesting code...", Tone.Warning);

            try
            {
                var result = await ApiClient.InitiateDeviceFlow();

                // After await, we may be on a background thread (IL2CPP issue)
                // Capture values for closure
                var deviceCode = result.DeviceCode;
                var userCode = result.UserCode;
                var verificationUri = result.VerificationUri;
                var success = result.Success;
                var error = result.Error;

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    if (success)
                    {
                        _deviceCode = deviceCode;
                        _userCode = userCode;
                        _verificationUri = verificationUri;

                        _code.Show(_userCode);
                        _codeRow.Visible = true;

                        _openWebsiteBtn.Visible = true;
                        _startLoginBtn.Visible = false;

                        _instructions.Say("Click the button below to open the website,\nthen enter this code:");
                        _status.Say("Waiting for authorization...", Tone.Info);

                        // Recalculate size after content changed
                        RecalculateSize();

                        StartDeviceFlowSse();
                    }
                    else
                    {
                        _status.Show(Tr("Error:") + $" {error}", Tone.Error);
                        _startLoginBtn.Enabled = true;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _status.Show(Tr("Error:") + $" {errorMsg}", Tone.Error);
                    _startLoginBtn.Enabled = true;
                });
            }
        }

        private void StartDeviceFlowSse()
        {
            _sseClient?.Disconnect();
            _sseClient = new SseClient(ApiClient.GetSseHttpClient());

            _sseClient.OnEvent += (evt) =>
            {
                // Capture values before RunOnMainThread (IL2CPP safety)
                var eventType = evt.EventType;
                var data = evt.Data;

                TranslatorUIManager.RunOnMainThread(() =>
                {
                    switch (eventType)
                    {
                        case "authorized":
                            HandleAuthorized(data);
                            break;
                        case "expired":
                            HandleExpired();
                            break;
                        case "error":
                            HandleSseError(data);
                            break;
                    }
                });
            };

            _sseClient.OnStateChanged += (state) =>
            {
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    switch (state)
                    {
                        case SseConnectionState.Reconnecting:
                            _status.Say("Connection lost, reconnecting...", Tone.Warning);
                            break;
                        case SseConnectionState.Connected:
                            _status.Say("Waiting for authorization...", Tone.Info);
                            break;
                    }
                });
            };

            _sseClient.OnError += (error) =>
            {
                var errorMsg = error;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    _status.Show(Tr("Error:") + $" {errorMsg}", Tone.Error);
                    _sseClient = null;
                    ResetUI();
                });
            };

            _sseClient.Connect(ApiClient.GetDeviceFlowSseUrl(_deviceCode));
        }

        private void HandleAuthorized(string jsonData)
        {
            try
            {
                var data = ApiClient.ParseJsonSafe(jsonData);
                string token = data["access_token"]?.Value<string>();
                string userName = data["user"]?["name"]?.Value<string>();

                _sseClient?.Disconnect();
                _sseClient = null;

                TranslatorCore.Config.api_token = token;
                TranslatorCore.Config.api_user = userName;
                TranslatorCore.Config.api_token_server = TranslatorCore.Config.api_base_url ?? PluginInfo.ApiBaseUrl;
                TranslatorCore.SaveConfig();
                ApiClient.SetAuthToken(token);

                // The line this access will appear as on the account's "Linked devices" page, so
                // it can be recognised there. Asked for once the token is on the client, and shown
                // by the account row when it lands.
                _ = ApiClient.RefreshAccessCodeAsync();

                _status.Show(Tr("Logged in as") + $" {userName}!", Tone.Success);

                // Refresh panels that show login status
                TranslatorUIManager.WizardPanel?.UpdateAccountStatus();
                TranslatorUIManager.MainPanel?.RefreshUI();

                // Start watching for updates now that we're authenticated
                TranslatorUIManager.StartSyncWatch();

                TranslatorUIManager.RunDelayed(2f, () =>
                {
                    SetActive(false);
                });
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[Login] Error handling auth response: {e.Message}");
                _status.Say("Login succeeded but error processing response", Tone.Error);
            }
        }

        private void HandleExpired()
        {
            _sseClient?.Disconnect();
            _sseClient = null;
            _status.Say("Code expired. Please try again.", Tone.Error);
            ResetUI();
        }

        private void HandleSseError(string jsonData)
        {
            try
            {
                var data = ApiClient.ParseJsonSafe(jsonData);
                string error = data["error"]?.Value<string>() ?? "Unknown error";
                _status.Show(error, Tone.Error);
            }
            catch
            {
                _status.Say("Connection error", Tone.Error);
            }
            _sseClient?.Disconnect();
            _sseClient = null;
            ResetUI();
        }

        private void CancelLogin()
        {
            _sseClient?.Disconnect();
            _sseClient = null;
            ResetUI();
            SetActive(false);
        }

        private void OpenVerificationUrl()
        {
            if (!string.IsNullOrEmpty(_verificationUri))
            {
                TranslatorCore.OpenUrlSafe(_verificationUri);
                TranslatorCore.LogInfo($"[Login] Opening verification URL: {_verificationUri}");
            }
        }

        private void CopyCodeToClipboard()
        {
            if (!string.IsNullOrEmpty(_userCode))
            {
                Platform.CopyToClipboard(_userCode);
                _copyCodeBtn.Label = "Copied!";

                // Reset button text after 2 seconds
                TranslatorUIManager.RunDelayed(2f, () =>
                {
                    if (_copyCodeBtn != null)
                        _copyCodeBtn.Label = "Copy";
                });
            }
        }

        private void ResetUI()
        {
            _startLoginBtn.Enabled = true;
            _startLoginBtn.Visible = true;
            _openWebsiteBtn.Visible = false;
            _codeRow.Visible = false;
            _copyCodeBtn.Label = "Copy";
            _instructions.Show(StartInstructions);
            _status.Clear();
            _verificationUri = null;

            // Recalculate size after content changed
            RecalculateSize();
        }
    }
}
