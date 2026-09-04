using System;
using System.Text.RegularExpressions;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The regular expression a font override rule may carry (<c>text:/…/</c>), compiled once and
    /// matched under a time budget.
    ///
    /// 🔴 **A pattern from a downloaded file ran on the main thread with no limit.** A rule is
    /// evaluated once per text component, on the label's own text — microseconds for any real
    /// pattern. But a pattern built to backtrack (<c>(a+)+$</c> against a text it cannot match)
    /// takes exponential time, and the game does not slow down: it stops. The budget is what keeps
    /// a shared translation from being able to do that, and it is generous on purpose — a second
    /// per label is a thousand times what a legitimate pattern needs, so nothing real is cut.
    ///
    /// ⚠ Pure on purpose, so the checks project can prove three things without a game: a valid
    /// pattern matches as before (case-insensitive), an invalid one is refused rather than thrown,
    /// and a catastrophic one is stopped by the budget instead of running to the end.
    ///
    /// ⚠ What happens AFTER a timeout is the caller's decision, not this class's: the rule is
    /// switched off for the session and named in the log, so the author of the file can see which
    /// pattern misbehaved. A silent <c>false</c> would leave a rule that never applies and nobody
    /// knows why.
    /// </summary>
    public static class TextRule
    {
        /// <summary>One second per text — see the class note for why so much.</summary>
        public static readonly TimeSpan Budget = TimeSpan.FromSeconds(1);

        public enum Outcome
        {
            NotMatched,
            Matched,
            /// <summary>The budget ran out before the engine could decide.</summary>
            TimedOut,
        }

        /// <summary>
        /// The pattern compiled with the same options the rule has always used, or null when it is
        /// not a valid pattern.
        /// </summary>
        public static Regex Compile(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return null;
            try
            {
                return new Regex(pattern, RegexOptions.IgnoreCase, Budget);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        public static Outcome Match(Regex regex, string text)
        {
            if (regex == null || text == null) return Outcome.NotMatched;
            try
            {
                return regex.IsMatch(text) ? Outcome.Matched : Outcome.NotMatched;
            }
            catch (RegexMatchTimeoutException)
            {
                return Outcome.TimedOut;
            }
        }
    }
}
