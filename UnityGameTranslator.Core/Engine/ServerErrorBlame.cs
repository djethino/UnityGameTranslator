using System.Collections.Concurrent;
using System.Threading;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Whether a line the translation server answered with an error is to blame — the model
    /// looping on a repetitive text until the server cuts it off, for instance — or whether the
    /// server is simply broken.
    ///
    /// 🔴 **On evidence, not on a count.** A broken server answers EVERY line with an error; filing
    /// them all as failures would bury the tab under lines with nothing wrong. So a line is blamed
    /// when it has failed this way before AND the same server answered something else in between:
    /// then the server works, and this text is what it cannot do. Until then it is asked again at
    /// its next appearance.
    ///
    /// Pure and thread-safe: the worker thread notes, the Core.Checks replay it.
    /// </summary>
    public sealed class ServerErrorBlame
    {
        private int _answers;
        private readonly ConcurrentDictionary<string, int> _failedAt = new ConcurrentDictionary<string, int>();

        /// <summary>The server answered a request with a translation.</summary>
        public void Answered() => Interlocked.Increment(ref _answers);

        /// <summary>
        /// The server answered this line with an error. True when that is enough to blame the line;
        /// the line is then forgotten here, the failure record takes over.
        /// </summary>
        public bool Blames(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            int answers = Volatile.Read(ref _answers);
            if (_failedAt.TryGetValue(key, out int before) && answers > before)
            {
                _failedAt.TryRemove(key, out _);
                return true;
            }
            _failedAt[key] = answers;
            return false;
        }
    }
}
