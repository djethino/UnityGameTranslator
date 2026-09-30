using System;
using System.Collections.Generic;
using System.Reflection;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A GSUB feature runs ONCE per shaping, at the earliest stage that names it: HarfBuzz merges
    /// the entries of a feature enabled twice. Our shapers list their stages by hand, and a feature
    /// in two lists ran twice — a font's 'ccmp' undid a Tibetan conjunct, a font's 'calt' took apart
    /// a Bengali one (Nirmala UI, ন্ত্র্). Each shaper's lists, in the order it runs them, must not
    /// share a feature. The lists are read from the shapers themselves.
    /// </summary>
    internal static class ShaperStagesChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string[] common = ShapingCommon.CommonGsubFeatures;
            One(check, typeof(IndicShaper), new[] { "PreFeatures", "BasicFeatures", "PresentationFeatures", "CommonAfterPresentation" }, null);
            One(check, typeof(KhmerShaper), new[] { "Pre", "Basic", "Other", "CommonFeatures" }, null);
            One(check, typeof(MyanmarShaper), new[] { "Pre", "BasicStages", "Other" }, common);
            One(check, typeof(UseShaper), new[] { "Pre", "Rphf", "Pref", "Basic", "Topographical", "Other" }, common);
        }

        private static void One(Action<bool, string, string> check, Type shaper, string[] fields, string[] plus)
        {
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var twice = new List<string>();
            void Take(string list, IEnumerable<string> features)
            {
                foreach (var f in features)
                {
                    if (seen.TryGetValue(f, out var first)) twice.Add($"{f} ({first} and {list})");
                    else seen[f] = list;
                }
            }
            foreach (var name in fields)
            {
                var field = shaper.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null) { check(false, $"{shaper.Name}: stage list {name} exists", "renamed? name the lists this check reads"); return; }
                var value = field.GetValue(null);
                if (value is string[][] stages) foreach (var s in stages) Take(name, s);
                else Take(name, (string[])value);
            }
            if (plus != null) Take("CommonGsubFeatures", plus);
            check(twice.Count == 0, $"{shaper.Name}: no GSUB feature in two stages", string.Join(", ", twice));
        }
    }
}
