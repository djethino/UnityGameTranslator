using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// No catch in the Core may swallow a failure without a word: every catch either says it (a
    /// Log call, Faults.Say) or lets it go on (throw). A `catch { }`, one that only returns,
    /// continues or breaks, one that quietly hands back a default — all the same blindness.
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
        // Read with the comments and the string literals taken out, so a comment inside a catch does
        // not hide it and one that merely NAMES the pattern (this file's own documentation) is not
        // counted.
        private static readonly Regex Comments = new Regex(@"//[^\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
        private static readonly Regex Strings = new Regex(@"@""(?:[^""]|"""")*""|\$?""(?:[^""\\\n]|\\.)*""", RegexOptions.Compiled);
        private static readonly Regex CatchHead = new Regex(@"\bcatch\b\s*(\([^)]*\))?\s*(when\s*\([^)]*\)\s*)?\{", RegexOptions.Compiled);
        // What makes a catch NOT silent: it says something, or it lets the failure go on.
        private static readonly Regex Speaks = new Regex(@"\bLog\w*\s*\(|\bFaults\.|\bthrow\b|\bSay\w*\s*\(", RegexOptions.Compiled);
        private static readonly Regex CaughtName = new Regex(@"catch\s*\(\s*[\w.]+\s+(\w+)\s*\)", RegexOptions.Compiled);

        /// <summary>How many catches in this source neither say anything nor rethrow.</summary>
        internal static int CountSilent(string source) => SilentLines(source).Count;

        /// <summary>
        /// The line of each silent catch. Comments and strings are blanked, never removed, so a
        /// position in the blanked text is a position in the source.
        /// </summary>
        internal static List<int> SilentLines(string source)
        {
            // Strings are kept for one question only — does the catch use its exception, which an
            // interpolated $"…{ex.Message}" does — and blanked for the rest (braces, calls).
            string withStrings = Comments.Replace(source, Blank);
            string code = Strings.Replace(withStrings, Blank);
            var lines = new List<int>();
            foreach (Match m in CatchHead.Matches(code))
            {
                int open = m.Index + m.Length - 1;
                int depth = 0, end = -1;
                for (int i = open; i < code.Length; i++)
                {
                    if (code[i] == '{') depth++;
                    else if (code[i] == '}' && --depth == 0) { end = i; break; }
                }
                if (end < 0) continue;
                string body = code.Substring(open, end - open + 1);
                if (Speaks.IsMatch(body)) continue;
                // It carries the exception somewhere (a diagnostic line it returns, a field a
                // report reads): not mute either.
                var named = CaughtName.Match(m.Value);
                if (named.Success && Regex.IsMatch(withStrings.Substring(open, end - open + 1),
                        @"\b" + Regex.Escape(named.Groups[1].Value) + @"\b")) continue;
                lines.Add(1 + code.Take(m.Index).Count(c => c == '\n'));
            }
            return lines;
        }

        // Same length, line breaks kept: positions survive the blanking.
        private static string Blank(Match m) => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray());

        /// <summary>`dotnet run -- silent-list [file]`: where the silent catches are, file by file.</summary>
        internal static int List(string only)
        {
            string core = FindDir("UnityGameTranslator.Core");
            if (core == null) { Console.WriteLine("Core not found."); return 1; }
            foreach (string path in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                string rel = Path.GetRelativePath(core, path).Replace('\\', '/');
                if (rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
                if (only != null && !rel.EndsWith(only, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (int line in SilentLines(File.ReadAllText(path))) Console.WriteLine($"{rel}:{line}");
            }
            return 0;
        }

        public static void Run(Action<bool, string, string> check)
        {
            // The counter itself, on the shapes it has to tell apart.
            void Counts(string code, int expected, string what)
            {
                int got = CountSilent(code);
                check(got == expected, what, got == expected ? "the ratchet counts what it names" : $"counted {got}, expected {expected}");
            }
            Counts("try { A(); } catch { }", 1, "an empty catch is silent");
            Counts("try { A(); } catch { return null; }", 1, "one that only returns is silent");
            Counts("try { A(); } catch { list = new List<int>(); }", 1, "one that quietly hands back a default is silent");
            Counts("try { A(); } catch (Exception ex) { Faults.Say(\"here\", ex); }", 0, "one that says it through Faults is not");
            Counts("try { A(); } catch (Exception e) { TranslatorCore.LogWarning(e.Message); return; }", 0, "nor one that logs");
            Counts("try { A(); } catch { throw; }", 0, "nor one that lets it go on");
            Counts("try { return A(); } catch (Exception ex) { return \"unknown (\" + ex.GetType().Name + \")\"; }", 0,
                "nor one that carries the exception into what it returns");
            Counts("try { A(); } catch (Exception ex) { return null; }", 1, "but naming it and dropping it is silent");
            Counts("try { A(); } catch (Exception ex) { note = $\"(error: {ex.Message})\"; }", 0,
                "an interpolated string that carries it counts as carrying it");
            Counts("// catch { }\nvar s = \"catch { }\";", 0, "a catch in a comment or a string is not code");
            Counts("try { A(); } catch { if (x) { y = 1; } }", 1, "nested braces are read to the catch's own end");

            string core = FindDir("UnityGameTranslator.Core");
            string baselineFile = FindFile("tests", "UnityGameTranslator.Core.Checks", "silent-catches.json");
            check(core != null && baselineFile != null, "the Core and the silent-catch baseline are found",
                "this check reads them; without them, it proves nothing");
            if (core == null || baselineFile == null) return;

            var baseline = ((JObject)JObject.Parse(File.ReadAllText(baselineFile))["files"])
                .Properties().ToDictionary(p => p.Name, p => (int)p.Value);

            var found = CountAll(core);

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

        private static Dictionary<string, int> CountAll(string core)
        {
            var found = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (string path in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(core, path).Replace('\\', '/');
                if (rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
                int n = CountSilent(File.ReadAllText(path));
                if (n > 0) found[rel] = n;
            }
            return new Dictionary<string, int>(found);
        }

        /// <summary>`dotnet run -- silent-baseline`: the baseline rewritten to what is left.</summary>
        internal static int WriteBaseline()
        {
            string core = FindDir("UnityGameTranslator.Core");
            string file = FindFile("tests", "UnityGameTranslator.Core.Checks", "silent-catches.json");
            if (core == null || file == null) { Console.WriteLine("Core or baseline not found."); return 1; }

            var found = CountAll(core);
            var doc = new JObject
            {
                ["about"] = "Silent catches left in UnityGameTranslator.Core, per file (SilentCatchChecks): a catch that neither says anything nor rethrows. A ratchet: never above, lowered in the same commit as each fix (dotnet run -- silent-baseline). The goal is an empty list. Inventory: analyse/catch-silencieux.md (root, out of git).",
                ["files"] = new JObject(found.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new JProperty(f.Key, f.Value))),
            };
            File.WriteAllText(file, doc.ToString() + "\n");
            Console.WriteLine($"{found.Values.Sum()} silent catches left in {found.Count} files.");
            return 0;
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
