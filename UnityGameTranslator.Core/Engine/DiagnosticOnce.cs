using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Whether a diagnostic has already been written — once per DISTINCT event (a topic and what makes
    /// the event itself: the text, the component, the reason), never a count.
    ///
    /// 🔴 **No budget on a diagnostic** (user, 2026-10-02: « encore une limite arbitraire qui nous
    /// empêchera de debug ? tu crois qu'on peut se téléporter dans les jeux ? »). A log that falls silent
    /// after its 300th line is silent exactly where the problem shows up — in a game somebody else plays,
    /// with no way back into it. What a cap was protecting against is the SAME line written every frame;
    /// that is what this refuses, and nothing else: every new text, component or reason is written.
    ///
    /// PURE by contract — linked into Core.Checks. Any thread (the seen sets are locked).
    /// </summary>
    internal static class DiagnosticOnce
    {
        private static readonly Dictionary<string, HashSet<string>> _seen = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        /// <summary>True the first time this event of this topic is asked about, false every time after.</summary>
        internal static bool First(string topic, string eventKey)
        {
            lock (_seen)
            {
                if (!_seen.TryGetValue(topic, out var set)) _seen[topic] = set = new HashSet<string>(StringComparer.Ordinal);
                return set.Add(eventKey ?? "");
            }
        }
    }
}
