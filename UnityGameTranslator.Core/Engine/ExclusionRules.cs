using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The patterns somebody wrote to keep a part of their game out of the translation, and the
    /// answers already given for the targets seen so far.
    ///
    /// 🔴 **What it protects.** An exclusion is a promise: a chat window, a player's name, a
    /// server's message. When it stops matching, that text goes to a model and comes back
    /// translated — shared with everyone on upload, and nobody is told. So the interesting failure
    /// is not "it refuses too much", it is "it quietly stops refusing", and that is a failure no
    /// screen shows.
    ///
    /// 🔴 **Why it holds state rather than being a function.** The decision is per target and is
    /// remembered, because it runs on every text write; the remembering is where the defects live.
    /// Forget to drop the memory when a pattern is added and the pattern does nothing for the rest
    /// of the session on everything already seen. Drop it too eagerly and the hierarchy is walked
    /// again on every label of every frame. Both are sequences, and a sequence can only be checked
    /// by replaying it — hence a small object with its own memory rather than a static call.
    ///
    /// ⚠ **Pure by contract, and stateful is not the opposite of pure.** No Unity, no disk, no
    /// clock, no logging: paths and ids in, answers out. What the caller owes is the two things
    /// this deliberately does NOT do — write the file and mark the metadata dirty — because those
    /// belong to whoever owns the file, not to the rule.
    ///
    /// ⚠ The pattern grammar itself lives in <see cref="ExclusionPatterns"/>, shared with the font
    /// rules: one syntax, one reader. This is only about which patterns are held and what has
    /// already been decided.
    ///
    /// Cut out of TranslatorCore on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md).
    /// </summary>
    public sealed class ExclusionRules
    {
        private readonly List<string> _patterns = new List<string>();

        /// <summary>
        /// ⚠ Keyed by long, like every other per-target map: uGUI passes an instance id, UI Toolkit
        /// passes an id from beyond the int range. The widening from int is implicit, so nothing
        /// that used to pass a component id had to change.
        /// </summary>
        private readonly Dictionary<long, bool> _decided = new Dictionary<long, bool>();

        /// <summary>What has been written, in the order it was written. Read-only for the UI.</summary>
        public IReadOnlyList<string> Patterns => _patterns;

        /// <summary>
        /// True when anything has been written at all.
        ///
        /// ⚠ Asked BEFORE a path is built, on the write path: walking a hierarchy to answer a
        /// question nobody asked is a cost that shows up nowhere and never goes away.
        /// </summary>
        public bool Any => _patterns.Count > 0;

        /// <summary>
        /// The answer already known for this target, if there is one.
        ///
        /// 🔴 Separate from <see cref="Decide"/> so a caller can consult the memory BEFORE building
        /// a path. A version taking the path as a callback was worse: it allocated a closure per
        /// call for anyone who had written a single pattern.
        /// </summary>
        public bool TryRecall(long id, out bool excluded)
        {
            return _decided.TryGetValue(id, out excluded);
        }

        /// <summary>
        /// Decide for this path, and remember the answer under this id.
        ///
        /// 🔴 The decision itself, shared by every framework: only the PATH is theirs. A second set
        /// of exclusion rules would mean one written pattern meaning two different things depending
        /// on what the label happens to be made of.
        /// </summary>
        public bool Decide(long id, string path)
        {
            if (_patterns.Count == 0) return false;

            bool excluded = Matches(path ?? "");
            _decided[id] = excluded;
            return excluded;
        }

        /// <summary>Whether a path matches anything written, with nothing remembered.</summary>
        public bool Matches(string path)
        {
            foreach (string pattern in _patterns)
            {
                if (ExclusionPatterns.Matches(path, pattern))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Write a pattern. False when there was nothing to write — empty, or already there.
        ///
        /// ⚠ The return value is what tells the caller whether the file changed. Saving on a
        /// duplicate would rewrite the translation and mark the metadata dirty for nothing.
        /// </summary>
        public bool Add(string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;

            pattern = pattern.Trim();
            if (pattern.Length == 0 || _patterns.Contains(pattern)) return false;

            _patterns.Add(pattern);
            ForgetAll();
            return true;
        }

        /// <summary>Take a pattern back. False when it was not there.</summary>
        public bool Remove(string pattern)
        {
            if (!_patterns.Remove(pattern)) return false;

            ForgetAll();
            return true;
        }

        /// <summary>Take everything back, memory included.</summary>
        public void Clear()
        {
            _patterns.Clear();
            ForgetAll();
        }

        /// <summary>
        /// What the translation file says, replacing what is held.
        ///
        /// ⚠ Drops the memory too: an answer decided against the previous patterns is an answer to
        /// a question nobody is asking any more.
        /// </summary>
        public void Load(IEnumerable<string> patterns)
        {
            _patterns.Clear();
            if (patterns != null) _patterns.AddRange(patterns);
            ForgetAll();
        }

        /// <summary>
        /// Forget one target.
        ///
        /// 🔴 The memory is a STRONG map keyed by id. For a Component that is harmless — ids are
        /// never reused and it is dropped wholesale when the rules change. For a UI Toolkit element
        /// it is not: they are recycled by the hundred, so an entry per element scrolled past would
        /// accumulate for the life of the process. The framework that holds its targets weakly
        /// calls this when one is collected.
        /// </summary>
        public void Forget(long id)
        {
            _decided.Remove(id);
        }

        /// <summary>Forget every answer — a scene change, or the rules having moved.</summary>
        public void ForgetAll()
        {
            _decided.Clear();
        }
    }
}
