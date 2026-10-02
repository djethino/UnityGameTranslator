using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// No diagnostic falls silent after a count.
    ///
    /// 🔴 User, 2026-10-02: « encore une limite arbitraire qui nous empêchera de debug ? tu crois qu'on
    /// peut se téléporter dans les jeux ? » — a 300-line debug budget had gone silent before the line
    /// being looked for, in a game played elsewhere. A diagnostic is written once per DISTINCT event
    /// (DiagnosticOnce), never "the first N". Read from the sources: a counter named like a log budget
    /// (…Budget, …Said, …Logged, …LogCount, …WarnCount) compared, incremented or decremented.
    /// </summary>
    internal static class DiagnosticCapChecks
    {
        private static readonly Regex Counter = new Regex(@"\b_?\w*(LogBudget|Budget|Said|Logged|LogCount|WarnCount|Refusals)\b\s*(--|\+\+|<|<=|>=|>)");

        // Budgets that are not about logs: an atlas size budget, a frame time budget.
        private static readonly HashSet<string> NotLogs = new HashSet<string>(StringComparer.Ordinal)
        {
            "atlasBudget", "cfgBudget", "curBudget", "AutoCompactBudget", "AutoLargeBudget",
        };

        public static void Run(Action<bool, string, string> check)
        {
            string core = FindCoreFolder();
            check(core != null, "the Core sources are found", "");
            if (core == null) return;
            var offenders = new List<string>();
            foreach (var path in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(core.Length + 1).Replace('\\', '/');
                if (rel.StartsWith("TextShaping/RichTextKit/") || rel.StartsWith("TextShaping/RTLTMPro/")) continue;
                string source = Regex.Replace(File.ReadAllText(path), @"//[^\r\n]*", "");
                foreach (Match m in Counter.Matches(source))
                {
                    string name = Regex.Match(m.Value, @"\w+").Value;
                    if (NotLogs.Contains(name) || name.EndsWith("BudgetMs", StringComparison.Ordinal)) continue;
                    offenders.Add(rel + ": " + m.Value.Trim());
                }
            }
            check(offenders.Count == 0, "no diagnostic is capped by a count",
                offenders.Count == 0 ? "every one is written once per distinct event (DiagnosticOnce)"
                    : string.Join(" | ", offenders) + " — write it once per distinct event with DiagnosticOnce.First");

            check(DiagnosticOnce.First("checks.topic", "a") && !DiagnosticOnce.First("checks.topic", "a") && DiagnosticOnce.First("checks.topic", "b"),
                "an event is said once, and every new event is said", "the same line twice is noise; a new one is never dropped");
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
