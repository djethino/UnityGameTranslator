using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The screens the mod can put in front of a player — the thirteen windows that take the
    /// game's input while they are up. The corner overlay is NOT one: a notification is glanced
    /// at while playing and never owns the controls (TranslatorUIManager.ShouldCaptureInput).
    /// (Named ScreenId, not Screen: UnityEngine.Screen is in scope wherever the engine is.)
    /// </summary>
    public enum ScreenId
    {
        Wizard,
        Main,
        Options,
        Login,
        Upload,
        UploadSetup,
        Merge,
        Language,
        Backups,
        Confirmation,
        SettingsChoice,
        Inspector,
        TranslationParameters,
    }

    /// <summary>
    /// What the router asks of a screen's view, and the one thing the view owes back: to say
    /// when its visibility ACTUALLY changed, whichever road closed it — a close button, a hotkey,
    /// a close deferred by a frame so the click that asked for it can finish.
    /// </summary>
    public interface IScreen
    {
        bool Visible { get; }
        void Show();
        void Hide();
        event Action<IScreen, bool> VisibilityChanged;
    }

    /// <summary>
    /// Which screen is up, and the rules of their sequence — the "act" layer of the interface.
    ///
    /// 🔴 **A panel never names another panel.** Until 2026-09-11 the navigation lived in the
    /// panels themselves: sixty-one calls through the manager's statics (`UploadPanel?.OpenForUpload()`,
    /// `ConfirmationPanel?.Show(…)`, `MainPanel?.RefreshUI()`), and one sequencing rule written
    /// inside the Inspector (hide the Main while inspecting, put it back after). A screen described
    /// in data cannot say "open UploadSetupPanel"; it can say what it wants, and this resolves it.
    /// The panels now emit intents (UI/Intents.cs), the intents come here for anything that is a
    /// screen, and the rules of sequence are held by ScreenRouterChecks on fake screens.
    ///
    /// ⚠ Pure by contract: no Unity, no clock, no disk. A view is whatever implements IScreen; the
    /// manager registers each panel under its ScreenId and never hands the panel itself around.
    /// Showing a screen nobody registered is a no-op, as `Panel?.SetActive(true)` was: the mod
    /// must translate a game with no window at all (see the tick's start order in the manager).
    /// </summary>
    public sealed class ScreenRouter
    {
        private readonly Dictionary<ScreenId, IScreen> _views = new Dictionary<ScreenId, IScreen>();
        private readonly Dictionary<IScreen, ScreenId> _ids = new Dictionary<IScreen, ScreenId>();

        /// <summary>
        /// Was the Main up when the Inspector came up. The Inspector clears the view to let the
        /// player pick elements in the game, and the Main comes back when the Inspector goes —
        /// but only if it was there: an inspector opened from a hotkey over a closed Main must not
        /// conjure one.
        /// </summary>
        private bool _mainWasOpenBehindInspector;

        /// <summary>A screen's visibility actually changed. Raised after the sequence rules ran.</summary>
        public event Action<ScreenId, bool> VisibilityChanged;

        public void Register(ScreenId screen, IScreen view)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (_views.ContainsKey(screen))
                throw new InvalidOperationException($"{screen} is already registered");
            _views[screen] = view;
            _ids[view] = screen;
            view.VisibilityChanged += OnViewVisibilityChanged;
        }

        public bool IsRegistered(ScreenId screen) => _views.ContainsKey(screen);

        public bool IsVisible(ScreenId screen) => _views.TryGetValue(screen, out var view) && view.Visible;

        public bool AnyVisible
        {
            get
            {
                foreach (var view in _views.Values)
                    if (view.Visible) return true;
                return false;
            }
        }

        /// <summary>The screens up right now, in registration order.</summary>
        public List<ScreenId> Visible()
        {
            var up = new List<ScreenId>();
            foreach (var pair in _views)
                if (pair.Value.Visible) up.Add(pair.Key);
            return up;
        }

        public void Show(ScreenId screen)
        {
            if (_views.TryGetValue(screen, out var view)) view.Show();
        }

        public void Hide(ScreenId screen)
        {
            if (_views.TryGetValue(screen, out var view)) view.Hide();
        }

        public void Toggle(ScreenId screen)
        {
            if (IsVisible(screen)) Hide(screen);
            else Show(screen);
        }

        public void CloseAll()
        {
            foreach (var view in _views.Values) view.Hide();
        }

        /// <summary>The first-run questions, in place of the Main.</summary>
        public void ShowWizard()
        {
            Show(ScreenId.Wizard);
            Hide(ScreenId.Main);
        }

        /// <summary>The Main, in place of the wizard.</summary>
        public void ShowMain()
        {
            Hide(ScreenId.Wizard);
            Show(ScreenId.Main);
        }

        /// <summary>The hotkey's act: the Main goes if it is up, and comes (in place of the wizard) if not.</summary>
        public void ToggleMain()
        {
            if (IsVisible(ScreenId.Main)) Hide(ScreenId.Main);
            else ShowMain();
        }

        private void OnViewVisibilityChanged(IScreen view, bool visible)
        {
            if (!_ids.TryGetValue(view, out var screen)) return;

            // The rule that lived in InspectorPanel.SetActive until 2026-09-11, verbatim: judged on
            // the ACTUAL transition, so a hotkey, the Stop button and a deferred close all count.
            if (screen == ScreenId.Inspector)
            {
                if (visible)
                {
                    _mainWasOpenBehindInspector = IsVisible(ScreenId.Main);
                    if (_mainWasOpenBehindInspector) Hide(ScreenId.Main);
                }
                else
                {
                    if (_mainWasOpenBehindInspector) Show(ScreenId.Main);
                    _mainWasOpenBehindInspector = false;
                }
            }

            VisibilityChanged?.Invoke(screen, visible);
        }
    }
}
