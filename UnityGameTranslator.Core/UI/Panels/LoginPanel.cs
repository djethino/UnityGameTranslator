using System;
using UniverseLib.UI;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Login panel using Device Flow authentication via SSE.
    ///
    /// ⚠ Described in data since 2026-09-15 (<c>common/spec/screens/login.json</c>): every piece
    /// is the document's, including the ones that start hidden. What stays here is the flow —
    /// which piece is shown when, what the status line says, what the code writes — and the acts.
    /// </summary>
    public class LoginPanel : TranslatorPanelBase
    {
        private static readonly ScreenDocument Doc = ScreenDocument.FromEmbedded("login");

        public override string Name => Doc.Name;
        public override int MinWidth => Doc.MinWidth;
        public override int MinHeight => Doc.MinHeight;
        public override int PanelWidth => Doc.Width;
        public override int PanelHeight => Doc.Height;

        protected override int MinPanelHeight => Doc.MinHeight;
        protected override bool PersistWindowPreferences => Doc.Persist;
        protected override bool UseBackdrop => Doc.Backdrop;

        private BuiltScreen _screen;
        private string _verificationUri;
        private SseClient _sseClient;
        private string _deviceCode;
        private string _userCode;

        /// <summary>What the panel says before a code has been asked for — and again after a reset.</summary>
        private const string StartInstructions =
            "Click the button below to start the login process.\n" +
            "You will receive a code to enter on the website.";

        private LabelHandle Instructions => _screen.Label("Instructions");
        private LabelHandle Code => _screen.Label("CodeLabel");
        private StatusLine Status => _screen.Status("Status");
        private ButtonHandle StartLoginBtn => _screen.Button("StartLoginBtn");
        private ButtonHandle OpenWebsiteBtn => _screen.Button("OpenWebsiteBtn");
        private ButtonHandle CopyCodeBtn => _screen.Button("CopyCodeBtn");
        private Host CodeRow => _screen.Host("CodeRow");

        public LoginPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            Layout(out var body, out var footer, Doc.CardWidth);
            _screen = ScreenBuilder.Build(Doc, body, footer, ActOf);
            _screen.Say("instructions", StartInstructions);
        }

        private Action ActOf(string act)
        {
            switch (act)
            {
                case "start": return StartLogin;
                case "openWebsite": return OpenVerificationUrl;
                case "copy": return CopyCodeToClipboard;
                case "cancel": return CancelLogin;
                default: return null;
            }
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
                Status.Say("Offline mode - enable Online Mode in Mod Options first", Tone.Error);
                return;
            }

            StartLoginBtn.Enabled = false;
            // ⚠ waiting: the longest, stillest wait in the mod — a code asked for, then a person
            // going to a browser and coming back. A line that never moves for half a minute is
            // what somebody reads as "it has stopped".
            Status.Say("Requesting code...", Tone.Warning, waiting: true);

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

                        Code.Show(_userCode);
                        CodeRow.Visible = true;

                        OpenWebsiteBtn.Visible = true;
                        StartLoginBtn.Visible = false;

                        Instructions.Say("Click the button below to open the website,\nthen enter this code:");
                        Status.Say("Waiting for authorization...", Tone.Info, waiting: true);

                        // Recalculate size after content changed
                        RecalculateSize();

                        StartDeviceFlowSse();
                    }
                    else
                    {
                        Status.Show(Tr("Error:") + $" {error}", Tone.Error);
                        StartLoginBtn.Enabled = true;
                    }
                });
            }
            catch (Exception e)
            {
                var errorMsg = e.Message;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    Status.Show(Tr("Error:") + $" {errorMsg}", Tone.Error);
                    StartLoginBtn.Enabled = true;
                });
            }
        }

        private void StartDeviceFlowSse()
        {
            _sseClient?.Disconnect();
            _sseClient = new SseClient(ApiClient.GetSseHttpClient());

            // ⚠ One event and the server closes the stream — authorized, expired or error, any of
            // them ends the device flow. Saying so here is what stops a deliberate close from
            // being read as a loss and written over the success message.
            _sseClient.StopAfterFirstEvent = true;

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
                            Status.Say("Connection lost, reconnecting...", Tone.Warning);
                            break;
                        case SseConnectionState.Connected:
                            Status.Say("Waiting for authorization...", Tone.Info, waiting: true);
                            break;
                    }
                });
            };

            _sseClient.OnError += (error) =>
            {
                var errorMsg = error;
                TranslatorUIManager.RunOnMainThread(() =>
                {
                    Status.Show(Tr("Error:") + $" {errorMsg}", Tone.Error);
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
                var authorized = ApiReaders.ReadDeviceAuthorized(ApiClient.ParseJsonSafe(jsonData));
                string token = authorized.AccessToken;
                string userName = authorized.UserName;

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

                Status.Show(Tr("Logged in as") + $" {userName}!", Tone.Success);

                // Every screen that shows who is signed in re-reads it
                Intents.AccountChanged();

                // Start watching for updates now that we're authenticated
                TranslatorUIManager.StartSyncWatch();

                // ⚠ Closed at once: the window has nothing left to say, and what it just said
                // is carried out by the corner notification instead of by two seconds of an open
                // panel (2026-09-18).
                SetActive(false);
                Intents.Toast(Tr("Logged in as") + $" {userName}", ToastTone.On);
            }
            catch (Exception e)
            {
                TranslatorCore.LogError($"[Login] Error handling auth response: {e.Message}");
                Status.Say("Login succeeded but error processing response", Tone.Error);
            }
        }

        private void HandleExpired()
        {
            _sseClient?.Disconnect();
            _sseClient = null;
            Status.Say("Code expired. Please try again.", Tone.Error);
            ResetUI();
        }

        private void HandleSseError(string jsonData)
        {
            try
            {
                string error = ApiReaders.ReadStreamError(ApiClient.ParseJsonSafe(jsonData)).Error ?? "Unknown error";
                Status.Show(error, Tone.Error);
            }
            catch
            {
                Status.Say("Connection error", Tone.Error);
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
                // ⚠ It stays "Copied!", and that is the honest state: the code IS in the
                // clipboard until something else replaces it, and pressing the button again
                // copies it again. Reverting after two seconds was a lie on a timer — the label
                // went back to saying the copy had not happened (2026-09-18).
                CopyCodeBtn.Label = "Copied!";
            }
        }

        private void ResetUI()
        {
            StartLoginBtn.Enabled = true;
            StartLoginBtn.Visible = true;
            OpenWebsiteBtn.Visible = false;
            CodeRow.Visible = false;
            CopyCodeBtn.Label = "Copy";
            Instructions.Show(StartInstructions);
            Status.Clear();
            _verificationUri = null;

            // Recalculate size after content changed
            RecalculateSize();
        }
    }
}
