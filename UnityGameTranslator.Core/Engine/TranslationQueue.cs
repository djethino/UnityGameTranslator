using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// One text waiting for a backend, with everything the answer will need.
    ///
    /// 🔴 **One object, because four parallel structures could not be emptied together.** The
    /// queue used to be a `Queue&lt;string&gt;` beside a set of pending texts, a map of waiting
    /// components and a set of "this one is ours" — four containers describing one item, filled
    /// and drained in different places. Every defect it produced is the same defect:
    ///
    ///  · a rate-limited text was re-queued into two of the four, so the second attempt had
    ///    neither its components nor its origin: a mod-UI label came back as a GAME line;
    ///  · clearing the queue emptied three of the four, and the surviving set of strings made a
    ///    later GAME text tagged as the mod's interface — the mirror of the same fault;
    ///  · the origin was consumed at dequeue, so it could not be consulted twice.
    ///
    /// ⚠ **The origin is decided ONCE, at the moment of queuing**, which is the only moment the
    /// component still exists to be asked — past the dequeue there is only a string. It then
    /// travels with the item, which is also what tells the cache which file the answer belongs in.
    ///
    /// ⚠ Deliberately NOT a string-keyed lookup of "is this text ours": that existed once and
    /// was removed for false positives when a game's text happened to equal one of our labels.
    /// The component is the authority; this only carries what it said.
    ///
    /// ⚠ <see cref="Targets"/> is a list of <c>object</c> on purpose: uGUI, TextMeshPro and UI
    /// Toolkit hand over three unrelated types, and the queue never asks any of them anything —
    /// it carries them back to whoever will write the answer. The only operation is reference
    /// equality.
    /// </summary>
    public sealed class QueuedText
    {
        public QueuedText(string text, int generation) { Text = text; Generation = generation; }

        public readonly string Text;

        /// <summary>
        /// Which loaded translation this was asked for.
        ///
        /// 🔴 **A backend takes seconds, and the file can be replaced in between.** Emptying the
        /// queue takes care of what has not left yet; the item already handed to a backend is gone
        /// from both containers and comes back with an answer that belongs to a file nobody holds
        /// any more. Written, it adds a line the restored translation never had, marks it changed,
        /// and paints it onto components whose text was just put back.
        ///
        /// ⚠ A number rather than a name: what matters is only whether it is still the same one,
        /// and the queue is the one thing that sees every load.
        /// </summary>
        public readonly int Generation;

        /// <summary>Things displaying it, to be updated when the answer arrives.</summary>
        public readonly List<object> Targets = new List<object>();

        /// <summary>Whether this is the mod's own interface rather than the game's text.</summary>
        public bool FromOwnUI;
    }

    /// <summary>
    /// The texts waiting for a backend, in the order they were asked for.
    ///
    /// 🔴 **What it is NOT: the door.** Whether a text deserves to be translated at all — the
    /// configuration, the language conflict, the length, text already in the target language — is
    /// decided before anything reaches here. This holds what was accepted, once each, and hands it
    /// back in order.
    ///
    /// 🔴 **Its own lock, and that is a deliberate narrowing.** It used to share one monitor with
    /// the translation caches, the order counter and the retranslation requests — one lock for
    /// four unrelated things. No critical section ever touched both sides (verified before the
    /// split), so nothing about the ordering changes; what changes is that the queue can now be
    /// replayed on its own, and a caller can no longer accidentally hold the caches while waiting
    /// on the queue.
    ///
    /// ⚠ **Pure by contract.** No Unity, no disk, no clock, no logging — the counts it returns are
    /// what the caller says something about. The targets are opaque objects it never dereferences.
    ///
    /// Cut out of TranslatorCore on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md).
    /// </summary>
    public sealed class TranslationQueue
    {
        /// <summary>
        /// What identifies one waiting item: the text AND whose text it is.
        ///
        /// 🔴 **The two are not one queue entry.** "Options", "Cancel", "Close" belong to a game
        /// and to us alike, and they are two different jobs: two files, two prompts — the game's
        /// carries its name, its context and its source language, ours says the source is always
        /// English and names this tool's vocabulary. Keyed by text alone, one of the two would win
        /// and the other would get an answer produced for a question nobody asked about it.
        ///
        /// ⚠ It costs one extra request for a string that is genuinely shared, which is rare, and
        /// buys the property somebody would expect anyway: the game's "Options" may become "Salut"
        /// while the interface's becomes "Bonsoir", each in its own file, neither aware of the other.
        /// </summary>
        private readonly struct QueueKey : IEquatable<QueueKey>
        {
            public QueueKey(string text, bool ownUi) { Text = text; OwnUi = ownUi; }

            public readonly string Text;
            public readonly bool OwnUi;

            public bool Equals(QueueKey other) =>
                OwnUi == other.OwnUi && string.Equals(Text, other.Text, StringComparison.Ordinal);

            public override bool Equals(object obj) => obj is QueueKey other && Equals(other);

            // ⚠ Hand-written rather than tuple-derived: this runs on IL2CPP, where a value type's
            // default hashing has cost this project surprises before. One shift is not clever, and
            // it is one thing fewer to depend on.
            public override int GetHashCode() =>
                ((Text != null ? Text.GetHashCode() : 0) << 1) ^ (OwnUi ? 1 : 0);
        }

        private readonly object _lock = new object();

        // Bumped every time the queue is emptied, which is what a translation being replaced does.
        // See QueuedText.Generation and IsCurrent.
        private int _generation;

        // ⚠ The two containers are emptied together, always. See QueuedText for what happened when
        // they were four and one survived.
        private readonly Dictionary<QueueKey, QueuedText> _waiting = new Dictionary<QueueKey, QueuedText>();
        private readonly Queue<QueuedText> _order = new Queue<QueuedText>();

        /// <summary>
        /// Texts already refused for being longer than any backend accepts, so the warning is
        /// said once instead of on every scan. In memory only: nothing about a refusal belongs
        /// in the translation file.
        /// </summary>
        private readonly HashSet<string> _tooLong = new HashSet<string>();

        // Own-UI texts already submitted in this session, so a label rewritten every frame is
        // submitted once even when the answer never produces a cache entry. Cleared on cache reload.
        //
        // ⚠ This is a THROTTLE, never an identity: it answers "have we asked for this already",
        // and nothing reads it to decide whether a text belongs to the mod. Which file a text
        // belongs in is settled at the moment it is queued and carried on the item itself
        // (see QueueKey) — a string-keyed identity existed once and was removed for false
        // positives when a game's text matched one of our labels.
        private readonly HashSet<string> _ownUiSubmitted = new HashSet<string>();

        /// <summary>How many texts are waiting.</summary>
        public int Count
        {
            get { lock (_lock) { return _order.Count; } }
        }

        /// <summary>
        /// Put a text in, or add a target to the one already waiting for it.
        /// </summary>
        /// <param name="isNew">True when this text was not already waiting under this origin.</param>
        /// <param name="waiting">How many are waiting once this call is done.</param>
        public void Submit(string text, object target, bool ownUi, out bool isNew, out int waiting)
        {
            lock (_lock)
            {
                // One item per waiting text AND per origin: the game's "Options" and ours are two
                // jobs, asked with two different prompts and filed in two different files.
                var key = new QueueKey(text, ownUi);
                isNew = !_waiting.TryGetValue(key, out var item);
                if (isNew)
                {
                    item = new QueuedText(text, _generation) { FromOwnUI = ownUi };
                    _waiting[key] = item;
                    _order.Enqueue(item);
                }

                if (target != null)
                {
                    // Same reference, one entry. Without this, a target whose text waits long in
                    // the queue is re-added on every scan cycle — a UI Toolkit element (whose
                    // instance id is -1) reached 137 strong references for ONE label, i.e. 136
                    // useless apply iterations and that many elements pinned against collection.
                    // (Reference equality: two IL2CPP proxies of one native object still slip
                    // through — bounded by proxy caching, and harmless beyond a wasted slot.)
                    if (!item.Targets.Contains(target)) item.Targets.Add(target);
                }

                waiting = _order.Count;
            }
        }

        /// <summary>
        /// The next text to translate, or null when nothing waits.
        ///
        /// ⚠ Taken OUT of the waiting map, so the same text can be asked for afresh while this one
        /// is in flight — and the item stays alive in the caller's hand, which is exactly what
        /// <see cref="PutBack"/> needs.
        /// </summary>
        public QueuedText Take()
        {
            lock (_lock)
            {
                if (_order.Count == 0) return null;

                // The item carries its targets AND its origin, so nothing has to be looked up from
                // the text — and nothing can be lost by looking up one of the two and forgetting
                // the other, which is what a re-queue used to do.
                var item = _order.Dequeue();
                _waiting.Remove(new QueueKey(item.Text, item.FromOwnUI));
                return item;
            }
        }

        /// <summary>
        /// Put a taken item back, after a refusal that is worth retrying.
        ///
        /// 🔴 **The SAME item goes back, never its text.** Re-queuing a bare string left the second
        /// attempt with neither the targets to update nor the origin — so a mod-interface label
        /// came back from the retry as a GAME line, written into the game's file under a game tag.
        ///
        /// ⚠ Silent when the text has meanwhile been asked for again: that request already carries
        /// the targets, and a second item would translate one text twice.
        /// </summary>
        public void PutBack(QueuedText item)
        {
            if (item == null) return;

            lock (_lock)
            {
                var key = new QueueKey(item.Text, item.FromOwnUI);
                if (_waiting.ContainsKey(key)) return;

                _waiting[key] = item;
                _order.Enqueue(item);
            }
        }

        /// <summary>
        /// Empty it, and say how many were dropped.
        ///
        /// ⚠ Both containers, together, and that is the point: this used to empty three of four,
        /// and the survivor was the set of "these texts are the mod's interface". A GAME text
        /// queued afterwards that happened to equal one of our labels was then filed as interface.
        /// </summary>
        public int Clear()
        {
            lock (_lock)
            {
                int count = _order.Count;
                _order.Clear();
                _waiting.Clear();

                // ⚠ After the two containers, never instead of them: this only concerns what has
                // ALREADY left, and everything still here is being dropped on the line above.
                _generation++;
                return count;
            }
        }

        /// <summary>
        /// Whether this answer is still about the translation that is loaded.
        ///
        /// ⚠ Asked by whoever is about to WRITE the answer, not by whoever took the item: the
        /// whole point is the time spent in between.
        /// </summary>
        public bool IsCurrent(QueuedText item)
        {
            if (item == null) return false;
            lock (_lock) { return item.Generation == _generation; }
        }

        /// <summary>
        /// Whether this text's refusal for length is worth saying — true the first time only.
        ///
        /// ⚠ It is met on every scan, so an ungated warning repeats for ever. Silence would be
        /// worse: a line that never gets translated has to say why somewhere.
        /// </summary>
        public bool NoteTooLong(string text)
        {
            lock (_lock) { return _tooLong.Add(text); }
        }

        /// <summary>
        /// Whether this interface label is being submitted for the first time this session.
        ///
        /// ⚠ A throttle and nothing else — see the note on the field. It exists because a label
        /// rewritten every frame would otherwise be submitted every frame when the answer never
        /// produces a cache entry.
        /// </summary>
        public bool NoteOwnUiSubmitted(string text)
        {
            lock (_lock) { return _ownUiSubmitted.Add(text); }
        }

        /// <summary>Forget which interface labels were submitted — the cache has been replaced.</summary>
        public void ForgetOwnUiSubmitted()
        {
            lock (_lock) { _ownUiSubmitted.Clear(); }
        }
    }
}
