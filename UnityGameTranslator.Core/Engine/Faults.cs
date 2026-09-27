using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The one place a failure caught at a real boundary is SAID — a Harmony patch body the game
    /// calls, a reflective call into a type the running game may have stripped, a callback whose
    /// escaping exception would kill the game.
    ///
    /// 🔴 **Never a reason to catch.** Most failures have a condition that recognises them before
    /// they happen (a member that is absent, a file that is not there, a collection another thread
    /// writes): that condition is the answer, not a catch. What reaches here is what no condition
    /// can foresee, at a place where letting it escape would do more harm than the failure itself.
    ///
    /// 🔴 **And never silent.** A `catch { }` made the mod blind: 375 of them were found on
    /// 2026-09-27 (analyse/catch-silencieux.md), one of which swallowed every error the router
    /// raised while putting a late translation back on screen.
    ///
    /// ⚠ Once per place in full (type, message, stack), then counted, and said again at 10, 100,
    /// 1000: a failure that repeats must not vanish after its first line, nor flood the log.
    /// </summary>
    public static class Faults
    {
        private static readonly Dictionary<string, int> _counts = new Dictionary<string, int>();
        private static readonly List<string> _pending = new List<string>();
        private static readonly object _gate = new object();
        private static Action<string> _sink;

        /// <summary>Where the lines go (the mod's log). Lines said before it is set are kept and handed over.</summary>
        public static void AttachSink(Action<string> sink)
        {
            List<string> held;
            lock (_gate)
            {
                _sink = sink;
                held = new List<string>(_pending);
                _pending.Clear();
            }
            if (sink != null) foreach (string line in held) sink(line);
        }

        /// <summary>A failure caught at <paramref name="place"/> — a short name a reader can search the code for.</summary>
        public static void Say(string place, Exception ex)
        {
            int count;
            Action<string> sink;
            lock (_gate)
            {
                _counts.TryGetValue(place, out count);
                _counts[place] = ++count;
                sink = _sink;
            }

            string line;
            if (count == 1)
                line = $"[Fault] {place}: {ex?.GetType().Name ?? "?"}: {ex?.Message}\n{ex?.StackTrace}";
            else if (count == 10 || count == 100 || count == 1000)
                line = $"[Fault] {place}: {count} times now (last: {ex?.GetType().Name ?? "?"}: {ex?.Message})";
            else
                return;

            if (sink != null) sink(line);
            else lock (_gate) { _pending.Add(line); }
        }

        /// <summary>How many times each place failed this session — for a diagnostic report.</summary>
        public static IDictionary<string, int> Counts()
        {
            lock (_gate) { return new Dictionary<string, int>(_counts); }
        }
    }
}
