using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The sequence rules of the screens, replayed on fake views: which screen is up after which
    /// act. A rule that lived inside a panel (the Inspector hiding the Main and putting it back)
    /// could only be seen in a game; here it is a sequence, and a wrong MOMENT goes red.
    /// </summary>
    internal static class ScreenRouterChecks
    {
        /// <summary>A view that does what a panel does: reports the actual change, once, and only when there is one.</summary>
        private sealed class FakeScreen : IScreen
        {
            private bool _visible;
            public int Reports;
            public bool Visible => _visible;
            public event Action<IScreen, bool> VisibilityChanged;
            public void Show() => Set(true);
            public void Hide() => Set(false);
            private void Set(bool visible)
            {
                if (_visible == visible) return;
                _visible = visible;
                Reports++;
                VisibilityChanged?.Invoke(this, visible);
            }
        }

        private static (ScreenRouter router, Dictionary<ScreenId, FakeScreen> views) Build()
        {
            var router = new ScreenRouter();
            var views = new Dictionary<ScreenId, FakeScreen>();
            foreach (ScreenId s in Enum.GetValues(typeof(ScreenId)))
            {
                var view = new FakeScreen();
                views[s] = view;
                router.Register(s, view);
            }
            return (router, views);
        }

        public static void Run(Action<bool, string, string> check)
        {
            {
                var (router, _) = Build();
                check(!router.AnyVisible && router.Visible().Count == 0,
                    "nothing is up after registration", "a screen is born hidden; showing one is an act");
                check(Enum.GetValues(typeof(ScreenId)).Length == 12,
                    "twelve screens", "the windows that take the input; the overlay is not one (the language chooser went 2026-09-15: nothing had opened it since the uGUI migration)");
            }

            {
                var (router, views) = Build();
                router.ShowWizard();
                check(router.IsVisible(ScreenId.Wizard) && !router.IsVisible(ScreenId.Main),
                    "the wizard comes in place of the Main", "first run: the questions, not the dashboard");
                router.ShowMain();
                check(router.IsVisible(ScreenId.Main) && !router.IsVisible(ScreenId.Wizard),
                    "the Main comes in place of the wizard", "the wizard's last step lands here");
                check(views[ScreenId.Main].Reports == 1 && views[ScreenId.Wizard].Reports == 2,
                    "a view reports each actual change once", "ShowWizard hid a Main that was not up: no report");
            }

            {
                var (router, _) = Build();
                router.ToggleMain();
                check(router.IsVisible(ScreenId.Main), "toggle on a closed Main opens it", "the hotkey's act");
                router.ToggleMain();
                check(!router.IsVisible(ScreenId.Main) && !router.AnyVisible, "toggle on an open Main closes it",
                    "and nothing else is left up");
                router.ShowWizard();
                router.ToggleMain();
                check(router.IsVisible(ScreenId.Main) && !router.IsVisible(ScreenId.Wizard),
                    "toggle from the wizard goes to the Main and puts the wizard away",
                    "ToggleMain opens through ShowMain, never beside a wizard");
            }

            {
                var (router, _) = Build();
                router.ShowMain();
                router.Show(ScreenId.Inspector);
                check(router.IsVisible(ScreenId.Inspector) && !router.IsVisible(ScreenId.Main),
                    "the inspector hides the Main behind it", "the view must be clear to pick elements in the game");
                router.Hide(ScreenId.Inspector);
                check(!router.IsVisible(ScreenId.Inspector) && router.IsVisible(ScreenId.Main),
                    "closing the inspector brings the Main back", "it was there before; the player did not close it");
            }

            {
                var (router, _) = Build();
                router.Show(ScreenId.Inspector);
                router.Hide(ScreenId.Inspector);
                check(!router.IsVisible(ScreenId.Main),
                    "an inspector opened over a closed Main leaves it closed", "a hotkey must not conjure a window nobody opened");
            }

            {
                var (router, views) = Build();
                router.ShowMain();
                router.Show(ScreenId.Inspector);
                // The view closes itself (Stop button, hotkey): the rule runs on the REPORT, not on the router's own Hide
                views[ScreenId.Inspector].Hide();
                check(router.IsVisible(ScreenId.Main),
                    "the Main comes back whichever road closed the inspector", "the rule is on the actual transition, not on one caller");
                router.Show(ScreenId.Inspector);
                int mainReports = views[ScreenId.Main].Reports;
                int inspectorReports = views[ScreenId.Inspector].Reports;
                router.Show(ScreenId.Inspector);
                check(views[ScreenId.Main].Reports == mainReports && views[ScreenId.Inspector].Reports == inspectorReports,
                    "showing an inspector already up changes nothing", "no second capture, no second hide");
            }

            {
                var (router, _) = Build();
                router.ShowMain();
                router.Show(ScreenId.Options);
                router.Show(ScreenId.Login);
                var up = router.Visible();
                check(up.Count == 3 && up[0] == ScreenId.Main && up[1] == ScreenId.Options && up[2] == ScreenId.Login,
                    "Visible() lists what is up, in registration order", "what the manager iterates for input ownership");
                router.CloseAll();
                check(!router.AnyVisible, "CloseAll leaves nothing up", "the hotkey that hides everything");
            }

            {
                var router = new ScreenRouter();
                var view = new FakeScreen();
                router.Register(ScreenId.Main, view);
                router.Show(ScreenId.Upload);
                router.Hide(ScreenId.Upload);
                router.Toggle(ScreenId.Upload);
                check(!router.IsVisible(ScreenId.Upload) && !router.IsRegistered(ScreenId.Upload),
                    "a screen nobody registered is a no-op", "as `Panel?.SetActive(true)` was: the mod runs with no window at all");
                bool refused = false;
                try { router.Register(ScreenId.Main, new FakeScreen()); } catch (InvalidOperationException) { refused = true; }
                check(refused, "registering a screen twice is refused", "two views for one screen would split the rules");
            }

            {
                var (router, _) = Build();
                var seen = new List<string>();
                router.VisibilityChanged += (s, v) => seen.Add($"{s}:{(v ? "on" : "off")}");
                router.ShowMain();
                router.Show(ScreenId.Inspector);
                check(string.Join(",", seen) == "Main:on,Main:off,Inspector:on",
                    "the router's event fires after its rules, per actual change",
                    $"got {string.Join(",", seen)} — the Main's hide is reported before the inspector's show, as it happens");
            }
        }
    }
}
