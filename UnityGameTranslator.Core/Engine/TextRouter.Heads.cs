using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        // === HEADS OF A REVEAL RESUMED PART-WAY ===
        //
        // A recording resumed where the player stopped it: the game sets the first part of a line
        // at once, the text stands still, and it is finalised like any finished text. Sent, it is
        // a fragment the file must not keep.
        //
        // 🔴 **Decided on what FOLLOWS, never on the text.** At the moment a text settles, the head
        // of a resumed reveal and a whole label are the same thing: characters that stopped moving.
        // The first answer compared the text with the file's lines ("it begins a known line, so it
        // is a head") and needed an exception per game — at most three characters (聪慧, a talent),
        // ending where the known line breaks (a talent tooltip), several ways to go on (金刚密宗, a
        // sect) — until a fourth fell between the three (性格 纯粹 on a character sheet, the file
        // holding 性格 纯粹(好感加成…) from another screen): never sent, for good. Every one of those
        // rules was a guess about length, punctuation or a language. Analysis:
        // analyse/debuts-de-revelation.md (root).
        //
        // So a settled text nobody has is SENT, and the component says afterwards what it was:
        //   - WRITTEN at once, it goes on revealing from that text (grows from it, then grows again
        //     before settling) → a head: taken back, and held on this PLACE from then on. A text the
        //     component revealed itself and stopped on is a reading pause (a dialogue box waiting for
        //     a click), never a head — held, it would be read in the source language;
        //   - held as a head, it is replaced by something that does not go on from it → it was whole
        //     this time: the finding is dropped and the text is sent.
        // The only measure is the stabiliser's own window, which already says "the game stopped".
        //
        // ⚠ **A place, not a text.** The same characters are a whole label on another component;
        // the queue never gives a head up by its text, and only the request that was in flight when
        // the proof came has its answer refused (TextAdmission.ForStore).

        // "place \u0001 normalised text": proved on that place. Read from the worker's thread as
        // well (the store asks), hence the lock.
        private readonly HashSet<string> _heads = new HashSet<string>();
        // Normalised texts whose request was on its way when they were proved heads: the answer
        // is refused once, when it comes (TextAdmission.ForStore), then forgotten.
        private readonly HashSet<string> _headAnswersRefused = new HashSet<string>();
        private readonly object _headsLock = new object();

        private static string HeadKey(string place, string key) => place + "\u0001" + key;

        /// <summary>The place this component sits in, or null when nothing can be said about it.</summary>
        private string PlaceOfId(long compId)
        {
            object target = TargetOf(compId);
            return target == null ? null : _host.PlaceOf(target);
        }

        /// <summary>This component's place has resumed a reveal from this text before.</summary>
        private bool IsHeadHere(long compId, string key)
        {
            string place = PlaceOfId(compId);
            if (place == null) return false;
            lock (_headsLock) { return _heads.Contains(HeadKey(place, key)); }
        }

        /// <summary>
        /// The request for this text left before it was proved a head: its answer is a fragment's.
        /// Asked by the store, through TextAdmission.
        /// </summary>
        public bool IsWithdrawnHead(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_headsLock) { return _headAnswersRefused.Contains(key); }
        }

        /// <summary>The fragment's answer came and was refused: nothing more to wait for.</summary>
        public void ForgetWithdrawnHead(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_headsLock) { _headAnswersRefused.Remove(key); }
        }

        /// <summary>
        /// The component went on revealing from a text already handed over: that text was the head
        /// of a reveal. Taken back if it still waits; refused at the store if its request has left
        /// and the file does not hold it — a line already there stays, nothing is deleted on a
        /// local observation.
        /// </summary>
        private void ProveHead(long compId, string text)
        {
            string key = NormalizeForCacheLookup(text);
            string place = PlaceOfId(compId);
            if (place != null)
                lock (_headsLock) { _heads.Add(HeadKey(place, key)); }

            bool waiting = _host.Withdraw(text, Admission.Head);
            if (!waiting && !_host.GameStore.ContainsKey(key))
                lock (_headsLock) { _headAnswersRefused.Add(key); }

            _host.Log($"[TW-HEAD] comp={compId} went on revealing from a text already sent — {(waiting ? "taken out of the queue" : "its answer will not be stored")}; held on this component from now on: '{Head40(text)}'");
        }

        /// <summary>
        /// Held as a head, then replaced by a text that does not go on from it: the text was whole
        /// this time. The finding is dropped — whatever made it (a game updated since, a sibling at
        /// the same place) — and the text goes to the queue like any finished text.
        /// </summary>
        private void RefuteHead(long compId, ComponentTextState state)
        {
            string text = state.HeldAsHead;
            state.HeldAsHead = null;
            if (text == null) return;

            DropHead(compId, text);
            _host.Log($"[TW-HEAD] comp={compId} replaced without going on — it was whole this time: no longer held here, sent: '{Head40(text)}'");
            ProcessFinalizedText(compId, text, stillShown: false);
        }

        private void DropHead(long compId, string text)
        {
            string place = PlaceOfId(compId);
            if (place == null) return;
            string key = NormalizeForCacheLookup(text);
            lock (_headsLock) { _heads.Remove(HeadKey(place, key)); }
        }
    }
}
