using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The texts waiting for a backend, across the sequence that empties them.
    ///
    /// 🔴 **This queue has a defect history, and all of it is one defect.** It used to be four
    /// parallel containers describing one item — the order, the pending set, the map of waiting
    /// components, the set of "this one is ours" — filled and drained in different places. A
    /// rate-limited text went back into two of the four and came back as somebody else's; clearing
    /// emptied three of the four and the survivor made a later game text into interface. Every case
    /// below exists because one container moved without the others.
    ///
    /// ⚠ Which texts DESERVE to be queued is not asked here: the configuration, the language
    /// conflict, the length, text already in the target language, all of that is decided at the
    /// door before anything reaches this. What is checked here is what happens to what got in.
    /// </summary>
    internal static class TranslationQueueChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            OneItemPerTextAndOrigin(check);
            TargetsTravelWithIt(check);
            TakingAndPuttingBack(check);
            Emptying(check);
            AnAnswerToAFileThatIsGone(check);
            TheTwoThrottles(check);
            TheGiveUpList(check);
        }

        /// <summary>
        /// Texts the backend has already answered for, unusably, this session.
        ///
        /// 🔴 **Without it the mod asks a model that has already failed, on every scan.** An answer
        /// that invented or dropped placeholders is never stored — storing it would write markup
        /// into somebody's game — so the text stays untranslated and comes straight back, for as
        /// long as the game shows it.
        ///
        /// 🔴 **And the case that matters is the one that takes a text OFF it.** A person who reads
        /// what came back and asks for that line again outranks the session's memory: the list
        /// exists so a line is not hammered, never to refuse somebody who asked once, on purpose.
        /// Both halves lived as a dictionary in an engine nothing could replay.
        /// </summary>
        private static void TheGiveUpList(Action<bool, string, string> check)
        {
            var q = new TranslationQueue();

            check(!q.WasRefused("Play"),
                "nothing is on the give-up list to begin with",
                "a session starts owing every text an attempt");

            q.NoteRefused("Play");
            check(q.WasRefused("Play"),
                "a text the backend could not answer for goes on it",
                "asking again would repeat a known failure, on every scan, for as long as the game shows the line");

            check(!q.WasRefused("Quit"),
                "and only that text",
                "one line's bad answer says nothing about the next one");

            // 🔴 A person asking again.
            q.ForgetRefused("Play");
            check(!q.WasRefused("Play"),
                "🔴 an explicit request takes it back off",
                "the list is there so a line is not hammered, never to refuse somebody who asked for it once on purpose");

            // ⚠ Emptying the QUEUE must not empty this: replacing what is waiting says nothing
            // about a model's placeholder mistakes.
            q.NoteRefused("Play");
            q.Submit("Quit", null, ownUi: false, out _, out _);
            q.Clear();

            check(q.WasRefused("Play"),
                "⚠ dropping what is waiting leaves the list alone",
                "a reload replaces the translation; it does not make a model able to place a token it could not place a second ago");

            // What does clear it: the model or the language may have changed under us.
            q.ForgetAllRefused();
            check(!q.WasRefused("Play"),
                "and giving every text another chance empties it",
                "what one model cannot place a token in, the next one may");

            check(!q.WasRefused(null),
                "nothing was never refused",
                "the worker asks about whatever it holds; it must not have to test for it first");

            var quiet = new TranslationQueue();
            quiet.NoteRefused(null);
            check(!quiet.WasRefused(null),
                "and refusing nothing records nothing",
                "an empty entry would match the next null and give up on a text nobody has tried");
        }

        private static void OneItemPerTextAndOrigin(Action<bool, string, string> check)
        {
            var queue = new TranslationQueue();

            queue.Submit("Options", null, ownUi: false, out bool first, out int one);
            check(first && one == 1,
                "a text asked for is a text waiting",
                "nothing else in the mod remembers that a translation was requested");

            queue.Submit("Options", null, ownUi: false, out bool again, out int still);
            check(!again && still == 1,
                "asking twice asks once",
                "a label rewritten every frame would otherwise buy one model call per frame");

            // 🔴 The same word, two owners, two jobs.
            queue.Submit("Options", null, ownUi: true, out bool ours, out int both);
            check(ours && both == 2,
                "but the game's word and the mod's are two jobs",
                "two files, two prompts: keyed by text alone one would win and the other would get an answer to a question nobody asked");

            var game = queue.Take();
            var mine = queue.Take();
            check(game.Text == "Options" && !game.FromOwnUI
                  && mine.Text == "Options" && mine.FromOwnUI,
                "and each comes back knowing whose it is",
                "the origin decides which file the answer is written into, and it cannot be re-derived from a string");
        }

        private static void TargetsTravelWithIt(Action<bool, string, string> check)
        {
            var queue = new TranslationQueue();
            object label = new object();
            object other = new object();

            queue.Submit("Play", label, ownUi: false, out _, out _);
            queue.Submit("Play", other, ownUi: false, out _, out _);

            var item = queue.Take();
            check(item.Targets.Count == 2 && item.Targets.Contains(label) && item.Targets.Contains(other),
                "everything showing a text waits on the one item",
                "one call answers for all of them, and each has to be written to when it lands");

            var repeating = new TranslationQueue();
            repeating.Submit("Play", label, ownUi: false, out _, out _);
            repeating.Submit("Play", label, ownUi: false, out _, out _);
            repeating.Submit("Play", label, ownUi: false, out _, out _);

            check(repeating.Take().Targets.Count == 1,
                "the same one asking again is still one",
                "a UI Toolkit element reached 137 strong references for ONE label — 136 useless writes, and that many elements pinned against collection");

            var anonymous = new TranslationQueue();
            anonymous.Submit("Play", null, ownUi: false, out _, out _);
            check(anonymous.Take().Targets.Count == 0,
                "and a text asked for by nobody still waits",
                "capture mode records a line without anything on screen to write back to");
        }

        private static void TakingAndPuttingBack(Action<bool, string, string> check)
        {
            var queue = new TranslationQueue();
            check(queue.Take() == null && queue.Count == 0,
                "an empty queue hands back nothing",
                "the worker asks in a loop, and nothing waiting is the ordinary answer");

            queue.Submit("A", null, ownUi: false, out _, out _);
            queue.Submit("B", null, ownUi: false, out _, out _);
            check(queue.Take().Text == "A" && queue.Take().Text == "B",
                "and a full one hands them back in order",
                "what a player sees first is what they are waiting on");

            // 🔴 Taken means out of the waiting set, so the same text can be asked afresh while
            // this one is in flight — that is what the item in hand is for.
            var flight = new TranslationQueue();
            object first = new object();
            flight.Submit("Play", first, ownUi: true, out _, out _);
            var taken = flight.Take();
            flight.Submit("Play", new object(), ownUi: true, out bool isNew, out _);

            check(isNew && flight.Count == 1,
                "a text in flight can be asked for again",
                "the screen moved on; the second request has its own targets and must not be dropped into the first");

            // 🔴 The rate-limit case, and the reason PutBack takes an ITEM.
            var limited = new TranslationQueue();
            object waiting = new object();
            limited.Submit("Play", waiting, ownUi: true, out _, out _);
            var held = limited.Take();
            limited.PutBack(held);
            var back = limited.Take();

            check(back != null && back.FromOwnUI && back.Targets.Count == 1 && back.Targets[0] == waiting,
                "and one put back keeps its targets and its owner",
                "re-queuing a bare string left the retry with neither: a mod label came back as a game line, in the game's file, under a game tag");

            var raced = new TranslationQueue();
            raced.Submit("Play", null, ownUi: false, out _, out _);
            var inHand = raced.Take();
            raced.Submit("Play", null, ownUi: false, out _, out _);
            raced.PutBack(inHand);

            check(raced.Count == 1,
                "one put back where the text is waiting again is dropped",
                "the newer request already carries the targets; two items would pay for one text twice");

            var nothing = new TranslationQueue();
            nothing.PutBack(null);
            check(nothing.Count == 0,
                "and putting back nothing does nothing",
                "the worker reaches the retry with an empty hand when it was told to stop");
        }

        private static void Emptying(Action<bool, string, string> check)
        {
            var queue = new TranslationQueue();
            queue.Submit("A", null, ownUi: false, out _, out _);
            queue.Submit("B", null, ownUi: true, out _, out _);

            check(queue.Clear() == 2,
                "clearing says how many were dropped",
                "somebody turned the model off with work outstanding, and that is worth a line");

            check(queue.Count == 0 && queue.Take() == null,
                "and nothing is left waiting",
                "this used to empty three of four containers");

            // 🔴 The survivor. Emptying the order without the waiting set left every cleared text
            // looking like it was still in flight, so it could never be asked for again.
            queue.Submit("A", null, ownUi: false, out bool acceptedAgain, out _);
            check(acceptedAgain,
                "a text cleared can be asked for afresh",
                "a half-cleared queue refuses for ever a text nobody is translating");

            check(new TranslationQueue().Clear() == 0,
                "clearing an empty queue drops nothing",
                "and says so, so nothing is written about work that never existed");
        }

        /// <summary>
        /// An item handed to a backend before the translation was replaced must not be written
        /// into the one that replaced it.
        ///
        /// 🔴 **Emptying the queue cannot settle this on its own, and that is the whole case.**
        /// The item is already OUT of both containers when the file changes — a backend takes
        /// seconds — so it comes back with an answer belonging to a translation nobody holds. Left
        /// alone it adds a line the restored file never had, in the previous target language, and
        /// counts as a local change nobody made.
        /// </summary>
        private static void AnAnswerToAFileThatIsGone(Action<bool, string, string> check)
        {
            var queue = new TranslationQueue();

            queue.Submit("Continue", new object(), ownUi: false, isNew: out _, waiting: out _);
            var inFlight = queue.Take();

            check(queue.IsCurrent(inFlight),
                "an answer to the translation that is loaded is written",
                "the ordinary case: nothing happened while the backend was thinking");

            // A translation is put back, downloaded or merged: the reload empties the queue.
            queue.Clear();

            check(!queue.IsCurrent(inFlight),
                "an answer asked before the translation was replaced is not",
                "it would add a line the restored file never had, in the previous target language, and mark it changed");

            queue.Submit("Continue", new object(), ownUi: false, isNew: out _, waiting: out _);
            var after = queue.Take();

            check(queue.IsCurrent(after),
                "and what is asked afterwards is written again",
                "the guard is about one moment, not a queue that stops working");

            check(!queue.IsCurrent(null),
                "nothing is not current",
                "the caller asks about what Take gave it, which can be nothing at all");
        }

        private static void TheTwoThrottles(Action<bool, string, string> check)
        {
            var queue = new TranslationQueue();

            check(queue.NoteTooLong("a credits roll") && !queue.NoteTooLong("a credits roll"),
                "a text refused for length is said once",
                "it is met on every scan; silence would be worse, since a line never translated has to say why somewhere");

            check(queue.NoteTooLong("a licence blob"),
                "and each text says its own",
                "two different lines are two different things somebody has to find");

            check(queue.NoteOwnUiSubmitted("Play") && !queue.NoteOwnUiSubmitted("Play"),
                "an interface label is submitted once a session",
                "one rewritten every frame would be submitted every frame when the answer never lands in the cache");

            queue.ForgetOwnUiSubmitted();
            check(queue.NoteOwnUiSubmitted("Play"),
                "until the cache is replaced",
                "a download brings another interface file, and what was answered for the old one says nothing about this one");

            // ⚠ The throttles are not the queue. Clearing work outstanding must not re-open the
            // door to a text already refused, nor re-submit a label already asked about.
            queue.Clear();
            check(!queue.NoteOwnUiSubmitted("Play") && !queue.NoteTooLong("a credits roll"),
                "and clearing the queue forgets neither",
                "they answer 'have we already asked', which is still true of work that was thrown away");

            TakingOneBackOut(check);
        }

        /// <summary>
        /// Taking a text back out while it still waits.
        ///
        /// 🔴 For the one thing only knowable after queueing: a game that writes its ability text as
        /// a template and expands it in place. The template is stable long enough to be sent, and
        /// only the expansion arriving proves it was never a line anybody reads.
        /// </summary>
        private static void TakingOneBackOut(Action<bool, string, string> check)
        {
            var queue = new TranslationQueue();
            queue.Submit("first", null, false, out _, out _);
            queue.Submit("*Overclock* ({0})", null, false, out _, out _);
            queue.Submit("last", null, false, out _, out _);

            check(queue.Withdraw("*Overclock* ({0})"),
                "a waiting text can be taken back out",
                "the expansion proves the template was never a line, and it is still waiting when that proof arrives");

            check(queue.Count == 2,
                "and the queue is one shorter",
                "removing it from the map alone left it in the order, so it would still have been sent");

            var first = queue.Take();
            var last = queue.Take();
            check(first != null && first.Text == "first" && last != null && last.Text == "last",
                "the ones around it keep their order",
                "the queue is rebuilt without that one, and everything downstream depends on first-in-first-out");

            check(queue.Take() == null,
                "and nothing else is left",
                "a copy surviving in either container is the defect this class was rewritten to remove");

            check(!queue.Withdraw("never queued") && !queue.Withdraw(null),
                "taking back what was never there changes nothing",
                "the caller acts on a component's behaviour, not on knowledge of the queue — it must be able to ask blind");

            // ⚠ A text in flight is NOT withdrawn: its targets are in somebody's hand.
            var busy = new TranslationQueue();
            busy.Submit("in flight", null, false, out _, out _);
            var taken = busy.Take();
            check(!busy.Withdraw("in flight") && taken != null,
                "one already taken is left alone",
                "reaching into work in flight is how the four containers of this class's history lost track of each other");
        }
    }
}
