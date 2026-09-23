using System;
using UnityGameTranslator.Core.UI.Panels;

namespace UnityGameTranslator.Core.UI
{
    /// <summary>The colour of a corner toast: plain, something switched on, something switched off or failed.</summary>
    public enum ToastTone { Info, On, Off }

    /// <summary>The tabs of the translation-parameters window a screen may ask to land on.</summary>
    public enum ParametersTab { Exclusions, Images, Tools, FontOverrides, Failures }

    /// <summary>
    /// What a screen may ASK for — and the one place that knows which panel answers.
    ///
    /// 🔴 **A panel names no other panel** (UiBoundaryChecks, rule 3). It says what it wants in
    /// the terms of the act — open the sign-in, set up an upload, confirm this, toast that, the
    /// account changed — and this resolves it against the manager's panels and the ScreenRouter.
    /// The sixty-eight calls this replaced (2026-09-11) each named a concrete panel through the
    /// manager's statics, which is what kept a screen from ever being described in data: a
    /// document can emit `OpenUpload`, it cannot say `UploadPanel?.OpenForUpload()`.
    ///
    /// ⚠ There is no generic Open(ScreenId) on purpose: the upload window serves two acts and
    /// settles which on a fresh activation, so every way into it says what for (OpenUpload,
    /// OpenDetails — UploadOfferChecks greps for the bare road). Closing needs no reason.
    ///
    /// ⚠ Every method is a no-op before the panels exist, as the `?.` calls were: an intent from
    /// a screen that is up has a manager behind it, and nothing else may assume one.
    /// </summary>
    public static class Intents
    {
        private static ScreenRouter Screens => TranslatorUIManager.Screens;

        // ── Questions ──────────────────────────────────────────────────────────────

        /// <summary>Is this screen up. What an opener button reads to show as pressed.</summary>
        public static bool IsOpen(ScreenId screen) => Screens != null && Screens.IsVisible(screen);

        /// <summary>The upload window is up on its upload act (as opposed to its details act).</summary>
        public static bool IsShowingUpload() => TranslatorUIManager.UploadPanel?.IsShowingUpload == true;

        /// <summary>The upload window is up on its details act.</summary>
        public static bool IsShowingDetails() => TranslatorUIManager.UploadPanel?.IsShowingDetails == true;

        /// <summary>Is there a way to ask. No way to ask is not a licence to decide.</summary>
        public static bool CanConfirm() => TranslatorUIManager.ConfirmationPanel != null;

        // ── Navigation ─────────────────────────────────────────────────────────────

        public static void ShowMain() => TranslatorUIManager.ShowMain();

        /// <summary>Put a screen away, whichever act it was up for.</summary>
        public static void Close(ScreenId screen) => Screens?.Hide(screen);

        /// <summary>A screen that needs no reason to come up goes, or comes, on the same button.</summary>
        public static void Toggle(ScreenId screen)
        {
            if (screen == ScreenId.Upload)
                throw new ArgumentException("the upload window is opened for an act: OpenUpload or OpenDetails", nameof(screen));
            Screens?.Toggle(screen);
        }

        public static void OpenLogin() => Screens?.Show(ScreenId.Login);

        /// <summary>The backups window, refreshed as it comes up.</summary>
        public static void OpenBackups() => TranslatorUIManager.BackupsPanel?.ShowPanel();

        /// <summary>The upload window on its SEND act; it reads the situation itself (new, update, contribute).</summary>
        public static void OpenUpload() => TranslatorUIManager.UploadPanel?.OpenForUpload();

        /// <summary>The upload window on its DETAILS act (notes, languages of the published copy).</summary>
        public static void OpenDetails() => TranslatorUIManager.UploadPanel?.OpenForDetails();

        /// <summary>The questions an upload needs first (game, languages); the answers go to the caller.</summary>
        public static void SetUpUpload(Action<GameInfo, string, string> onComplete)
            => TranslatorUIManager.UploadSetupPanel?.ShowForSetup(onComplete);

        public static void OpenInspector(InspectorMode mode = InspectorMode.Exclusion)
            => TranslatorUIManager.OpenInspectorPanel(mode);

        public static void OpenTranslationParameters(ParametersTab tab)
        {
            var panel = TranslatorUIManager.TranslationParamsPanel;
            if (panel == null) return;
            switch (tab)
            {
                case ParametersTab.Images: panel.OpenOnBitmapReplaceTab(); break;
                case ParametersTab.Tools: panel.OpenOnToolsTab(); break;
                case ParametersTab.FontOverrides: panel.OpenOnFontOverridesTab(); break;
                case ParametersTab.Failures: panel.OpenOnFailuresTab(); break;
                default: panel.OpenOnExclusionsTab(); break;
            }
        }

        /// <summary>A font override picked in the inspector, handed to the parameters window which opens on it.</summary>
        public static void AddFontOverride(string path)
            => TranslatorUIManager.TranslationParamsPanel?.AddFontOverrideFromInspector(path);

        // ── Dialogs and notices ────────────────────────────────────────────────────

        public static void Confirm(string title, string message, string confirmText,
                                   Action onConfirm, Action onCancel = null, bool isDanger = true)
            => TranslatorUIManager.ConfirmationPanel?.Show(title, message, confirmText, onConfirm, onCancel, isDanger);

        public static void Toast(string message, ToastTone tone = ToastTone.Info)
            => TranslatorUIManager.StatusOverlay?.ShowToast(message, tone);

        // ── Facts that changed, for every screen that shows them ───────────────────

        /// <summary>Signed in or out: the wizard's account row, the Main and the corner overlay re-read it.</summary>
        public static void AccountChanged()
        {
            TranslatorUIManager.WizardPanel?.UpdateAccountStatus();
            TranslatorUIManager.MainPanel?.RefreshUI();
            TranslatorUIManager.StatusOverlay?.RefreshOverlay();
        }

        /// <summary>A setting that changes what the Main and the overlay say (online mode).</summary>
        public static void SettingsChanged()
        {
            TranslatorUIManager.MainPanel?.RefreshUI();
            TranslatorUIManager.StatusOverlay?.RefreshOverlay();
        }

        /// <summary>Something the Main shows moved: an upload landed, notes were saved, an update was found.</summary>
        public static void StateChanged() => TranslatorUIManager.MainPanel?.RefreshUI();

        /// <summary>Pause live translation — the same act as its hotkey, which turns it back on.</summary>
        public static void PauseLiveTranslation() => TranslatorUIManager.SetLiveTranslation(false);

        /// <summary>The overlay's corner was changed in the options.</summary>
        public static void OverlayPositionChanged() => TranslatorUIManager.StatusOverlay?.ApplyPositionFromConfig();
    }
}
