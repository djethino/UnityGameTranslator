using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Object numbers (analyse/numeros-objets-reutilises.md): Unity 6.4+ gives a destroyed object's
    /// number to the next object it makes, and everything the mod keeps per object is filed by it.
    ///
    /// Two halves, read as their sources are:
    ///   1. lexical — no file of the Core asks an engine object its number except ObjectNumbers.cs,
    ///      the one place that tells "the same object" from "another object under the same number".
    ///      Same protocol as the AddListener rule in CLAUDE.md: a grep that finds something is a bug.
    ///   2. ForgetByNumber (pure, linked here) — a forgotten number leaves every table that registered.
    /// </summary>
    internal static class ObjectNumberChecks
    {
        // An engine object asked its number — never TypeHelper.GetInstanceID, the door that goes through ObjectNumbers.
        private static readonly Regex DirectNumber = new Regex(@"(?<!\bTypeHelper)\.GetInstanceID\s*\(");

        public static void Run(Action<bool, string, string> check)
        {
            // ── 1. Only ObjectNumbers asks an object its number ───────────────
            string core = FindCoreFolder();
            check(core != null, "the Core sources are found", "the check reads files; without them it proves nothing");
            if (core != null)
            {
                var found = new List<string>();
                int read = 0;
                foreach (var file in Directory.GetFiles(core, "*.cs", SearchOption.AllDirectories))
                {
                    if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)) continue;
                    if (Path.GetFileName(file) == "ObjectNumbers.cs") continue;
                    read++;
                    string source = StripComments(File.ReadAllText(file));
                    if (DirectNumber.IsMatch(source)) found.Add(file.Substring(core.Length + 1));
                }
                check(read > 50, "the Core's files are read", $"{read} read — a check over a handful proves nothing");
                check(found.Count == 0, "no file asks an engine object its number but ObjectNumbers",
                    found.Count == 0 ? "TypeHelper.GetInstanceID / ObjectNumbers.Of: one number per object, checked"
                                     : "found in: " + string.Join(", ", found));
                check(File.Exists(Path.Combine(core, "ObjectNumbers.cs")) && DirectNumber.IsMatch(File.ReadAllText(Path.Combine(core, "ObjectNumbers.cs"))),
                    "ObjectNumbers is where the number is asked", "the one exception, still there");
            }
            check(DirectNumber.IsMatch("int id = component.GetInstanceID();") && DirectNumber.IsMatch("x.GetInstanceID ()")
                  && !DirectNumber.IsMatch("int id = TypeHelper.GetInstanceID(component);"),
                "the rule sees a direct call, and only that",
                "the alarm must be able to fire, and TypeHelper's door must not set it off");

            // ── 2. A forgotten number leaves every table ──────────────────────
            var byInt = ForgetByNumber.Tracked(new Dictionary<int, string> { [7] = "seven", [8] = "eight" });
            var byLong = ForgetByNumber.Tracked(new Dictionary<long, string> { [7L] = "seven", [(1L << 32) + 7] = "beyond" });
            var setInt = ForgetByNumber.Tracked(new HashSet<int> { 7, 9 });
            var router = ForgetByNumber.Tracked(new List<long> { 7, 10 }, (list, id) => list.Remove(id));
            ForgetByNumber.Forget(7);
            check(!byInt.ContainsKey(7) && byInt.ContainsKey(8), "an int table forgets that number, and only it", "the eighth object keeps its state");
            check(!byLong.ContainsKey(7L) && byLong.ContainsKey((1L << 32) + 7), "a long table forgets it, not a UI Toolkit id beyond the int range",
                "UI Toolkit's own ids start at 2^32 and are never an object number");
            check(!setInt.Contains(7) && setInt.Contains(9), "a set forgets it", "same rule for a set");
            check(!router.Contains(7) && router.Contains(10), "an owner forgets it through its own Forget", "TextRouter, FontRules, ExclusionRules, SizeTagWrites");
            ForgetByNumber.Forget((1L << 32) + 9);
            check(setInt.Contains(9), "a number beyond the int range touches no int table", "never cut down to an int that means another object");
        }

        /// <summary>Comments may name anything; only code is judged.</summary>
        private static string StripComments(string source)
        {
            source = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(source, @"//[^\r\n]*", "");
        }

        /// <summary>Up from the binary until the Core's folder is found.</summary>
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
