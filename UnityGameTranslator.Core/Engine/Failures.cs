using System;
using System.Collections.Generic;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    // FailedAttempt — one answer the AI gave for a line, and why it was refused — lives in the
    // socle (LineTranslation.cs) since 2026-09-23: the loop that produces it moved there.

    /// <summary>
    /// A line the AI could not translate this session: every attempt broke a placeholder. Kept
    /// so somebody can settle it — fix one of the answers, write their own, skip the line, or
    /// exclude the element that showed it — instead of the line being asked again at every
    /// launch and refused again in the log.
    /// </summary>
    public sealed class FailedLine
    {
        /// <summary>The cache key: the normalised text with its placeholders, as the queue holds it.</summary>
        public string Key;

        /// <summary>The text as it was shown, for the eye.</summary>
        public string Source;

        public List<FailedAttempt> Attempts = new List<FailedAttempt>();

        /// <summary>
        /// The elements that showed this text, by hierarchy path — attached after the fact by
        /// the host, because the worker that fails a line only holds the components and their
        /// path can only be read on the main thread. Empty when nothing attached one.
        /// </summary>
        public List<string> Elements = new List<string>();
    }

    /// <summary>
    /// The session's failed lines, one per key, in the order they failed. Pure: no Unity, no
    /// clock — the Core.Checks replay it. Thread-safe: noted from the worker thread, settled from
    /// the main one.
    ///
    /// ⚠ This is not the queue's give-up list (TranslationQueue.NoteRefused), which only says
    /// "do not ask again this session". This holds what there is to look at; settling a line
    /// clears both.
    /// </summary>
    public sealed class FailureLedger
    {
        private readonly object _gate = new object();
        private readonly List<FailedLine> _lines = new List<FailedLine>();

        /// <summary>Raised after every change, on the thread that made it. Subscribers marshal.</summary>
        public event Action Changed;

        public int Count { get { lock (_gate) return _lines.Count; } }

        /// <summary>A snapshot, oldest failure first.</summary>
        public List<FailedLine> All { get { lock (_gate) return new List<FailedLine>(_lines); } }

        public bool Holds(string key)
        {
            if (key == null) return false;
            lock (_gate) return IndexOf(key) >= 0;
        }

        /// <summary>Records a failure; a line that failed before is replaced, keeping its place and its elements.</summary>
        public void Note(FailedLine line)
        {
            if (line == null || string.IsNullOrEmpty(line.Key)) return;
            lock (_gate)
            {
                int at = IndexOf(line.Key);
                if (at >= 0)
                {
                    if (line.Elements.Count == 0) line.Elements = _lines[at].Elements;
                    _lines[at] = line;
                }
                else _lines.Add(line);
            }
            Changed?.Invoke();
        }

        /// <summary>Names the elements that showed a failed line. Unknown key or nothing new: no change, no event.</summary>
        public void AttachElements(string key, IEnumerable<string> paths)
        {
            if (key == null || paths == null) return;
            bool changed = false;
            lock (_gate)
            {
                int at = IndexOf(key);
                if (at < 0) return;
                foreach (string path in paths)
                {
                    if (string.IsNullOrEmpty(path) || _lines[at].Elements.Contains(path)) continue;
                    _lines[at].Elements.Add(path);
                    changed = true;
                }
            }
            if (changed) Changed?.Invoke();
        }

        /// <summary>The line was settled — saved, skipped, excluded, or translated after all.</summary>
        public bool Remove(string key)
        {
            if (key == null) return false;
            bool removed;
            lock (_gate)
            {
                int at = IndexOf(key);
                removed = at >= 0;
                if (removed) _lines.RemoveAt(at);
            }
            if (removed) Changed?.Invoke();
            return removed;
        }

        /// <summary>
        /// Drops every line <paramref name="settled"/> says is — the reconciliation at load: a
        /// key the translation now holds a line for was translated since (downloaded, restored,
        /// written by hand) and is no failure any more. Returns how many left; one event at most.
        /// </summary>
        public int Settle(Func<string, bool> settled)
        {
            if (settled == null) throw new ArgumentNullException(nameof(settled));
            int removed;
            lock (_gate)
            {
                removed = _lines.RemoveAll(line => settled(line.Key));
            }
            if (removed > 0) Changed?.Invoke();
            return removed;
        }

        public void Clear()
        {
            bool had;
            lock (_gate)
            {
                had = _lines.Count > 0;
                _lines.Clear();
            }
            if (had) Changed?.Invoke();
        }

        private int IndexOf(string key)
        {
            for (int i = 0; i < _lines.Count; i++)
                if (string.Equals(_lines[i].Key, key, StringComparison.Ordinal)) return i;
            return -1;
        }
    }
}
