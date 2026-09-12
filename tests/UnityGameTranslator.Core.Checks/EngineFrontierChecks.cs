using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The frontier in the other direction: the engine names nothing of the interface. What it
    /// has to say goes to its host (Engine/EngineHost.cs, IEngineHost), attached by the interface
    /// at its start; the interface answers through UI/EngineHostAdapter.cs.
    ///
    /// ⚠ Lexical, like UiBoundaryChecks: every source file outside UI/ is read, its comments
    /// stripped, and judged on the tokens it names. A file that reaches TranslatorUIManager, a
    /// panel or an intent is a line the second Core would have to rewrite.
    /// </summary>
    internal static class EngineFrontierChecks
    {
        private static readonly (Regex pattern, string what)[] Forbidden =
        {
            (new Regex(@"\bTranslatorUIManager\b"), "the UI manager"),
            // Our UI namespace's members, qualified — never UnityEngine.UI.Text, which the engine
            // handles all day: the look-behind refuses a dot before UI, the list names ours.
            (new Regex(@"(?<![\w.])UI\.(TranslatorUIManager|Panels|Components|Intents|ToastTone|EngineHostAdapter|UIStyles)\b"),
                "a qualified UI name (UI.Panels, UI.Intents, UI.ToastTone…)"),
            (new Regex(@"^\s*using\s+UnityGameTranslator\.Core\.UI\b", RegexOptions.Multiline), "a using of the UI namespace"),
            (new Regex(@"\bIntents\.\w+\("), "an intent (the engine states facts, the host decides screens)"),
        };

        public static void Run(Action<bool, string, string> check)
        {
            string core = FindCoreFolder();
            check(core != null, "the Core's sources are found", "the check reads files; without them it proves nothing");
            if (core == null) return;

            int judged = 0;
            foreach (var file in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories))
            {
                string relative = file.Substring(core.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (relative.StartsWith("UI/", StringComparison.Ordinal)) continue;
                if (relative.StartsWith("obj/", StringComparison.Ordinal) || relative.StartsWith("bin/", StringComparison.Ordinal)) continue;
                judged++;

                string source = StripComments(File.ReadAllText(file));
                var found = new List<string>();
                foreach (var (pattern, what) in Forbidden)
                    if (pattern.IsMatch(source)) found.Add(what);

                if (found.Count > 0)
                    check(false, $"{relative} names nothing of the interface", "found: " + string.Join(", ", found));
            }
            check(judged > 50, $"{judged} engine files judged, none names the interface",
                "the engine says what happened to its host; the host decides what a screen does with it");

            string adapter = Path.Combine(core, "UI", "EngineHostAdapter.cs");
            check(File.Exists(adapter) && File.ReadAllText(adapter).Contains(": IEngineHost", StringComparison.Ordinal),
                "UI/EngineHostAdapter.cs implements IEngineHost", "the one file that turns an engine fact into a screen");

            string manager = Path.Combine(core, "UI", "TranslatorUIManager.cs");
            string managerSource = File.Exists(manager) ? File.ReadAllText(manager) : "";
            check(managerSource.Contains("TranslatorCore.AttachHost(new EngineHostAdapter())", StringComparison.Ordinal)
                  && managerSource.Contains("TranslatorCore.NotifyHostReady()", StringComparison.Ordinal),
                "the interface attaches itself as the host and says when it is ready",
                "the patches wait on TranslatorCore.HostReady, which only the host can raise");

            check(Forbidden[1].pattern.IsMatch("UI.TranslatorUIManager.RunOnMainThread(x);")
                  && Forbidden[1].pattern.IsMatch("x = UI.Intents.Toast(m);")
                  && !Forbidden[1].pattern.IsMatch("var t = go.GetComponent<UnityEngine.UI.Text>();")
                  && !Forbidden[1].pattern.IsMatch("UI.Text label;"),
                "the rule tells our UI namespace from UnityEngine.UI", "the engine handles UnityEngine.UI.Text all day; that is not a screen");
        }

        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", "");
        }

        private static string FindCoreFolder()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "UnityGameTranslator.Core");
                if (Directory.Exists(Path.Combine(candidate, "UI"))) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
