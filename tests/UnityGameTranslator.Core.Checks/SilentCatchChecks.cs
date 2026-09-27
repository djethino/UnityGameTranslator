using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// No catch in the Core may swallow a failure without a word — a `catch { }`, or one that only
    /// returns, continues or breaks.
    ///
    /// 🔴 **Why it is a check and not a rule in a document**: the rule existed
    /// (.claude/rules/general-coding.md, "You never fallback to hide legitimate errors") and 375 of
    /// them were counted on 2026-09-27, one of which swallowed every error the router raised while
    /// putting a late translation back on screen. A rule nothing checks is re-broken at the next pass.
    ///
    /// ⚠ **A ratchet, not a judge**: `silent-catches.json` holds, per file, how many are left from
    /// that count. A file may never go above its number, a file not listed may hold none, and a file
    /// cleaned below its number must have it lowered in the same commit — so a fixed one can never
    /// come back unnoticed. The goal is an empty list. Each one is fixed by READING it: a condition
    /// that recognises the case, or <see cref="Faults.Say"/> at a real boundary.
    /// </summary>
    internal static class SilentCatchChecks
    {
        // A catch with nothing in it, or nothing but a return / continue / break — read with the
        // comments taken out, so a comment inside it does not hide it and one that merely NAMES
        // the pattern (this file's own documentation) is not counted.
        private static readonly Regex Silent = new Regex(
            @"catch\s*(\([^)]*\))?\s*\{\s*((return[^;{}]*|continue|break);\s*)?\}",
            RegexOptions.Compiled);
        private static readonly Regex Comments = new Regex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

        public static void Run(Action<bool, string, string> check)
        {
            string core = FindDir("UnityGameTranslator.Core");
            string baselineFile = FindFile("tests", "UnityGameTranslator.Core.Checks", "silent-catches.json");
            check(core != null && baselineFile != null, "the Core and the silent-catch baseline are found",
                "this check reads them; without them, it proves nothing");
            if (core == null || baselineFile == null) return;

            var baseline = ((JObject)JObject.Parse(File.ReadAllText(baselineFile))["files"])
                .Properties().ToDictionary(p => p.Name, p => (int)p.Value);

            var found = new Dictionary<string, int>();
            foreach (string path in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(core, path).Replace('\\', '/');
                if (rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
                int n = Silent.Matches(Comments.Replace(File.ReadAllText(path), "")).Count;
                if (n > 0) found[rel] = n;
            }

            var worse = found.Where(f => f.Value > (baseline.TryGetValue(f.Key, out int b) ? b : 0))
                             .Select(f => $"{f.Key}: {f.Value} (allowed {(baseline.TryGetValue(f.Key, out int b) ? b : 0)})").ToList();
            check(worse.Count == 0, "no new silent catch in the Core",
                worse.Count == 0
                    ? "a failure swallowed without a word is a failure nobody can diagnose"
                    : "🔴 " + string.Join("; ", worse) + " — recognise the case with a condition, or say it through Faults.Say");

            var better = baseline.Where(b => (found.TryGetValue(b.Key, out int n) ? n : 0) < b.Value)
                                 .Select(b => $"{b.Key}: {(found.TryGetValue(b.Key, out int n) ? n : 0)} (baseline {b.Value})").ToList();
            check(better.Count == 0, "and the baseline follows every one fixed",
                better.Count == 0
                    ? $"{found.Values.Sum()} left in {found.Count} files; the goal is none"
                    : "lower silent-catches.json to what is left, in the same commit: " + string.Join("; ", better));
        }

        private static string FindDir(string name)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, name);
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "TranslatorCore.cs"))) return candidate;
                dir = dir.Parent;
            }
            return null;
        }

        private static string FindFile(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var segments = new List<string> { dir.FullName };
                segments.AddRange(parts);
                string candidate = Path.Combine(segments.ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
