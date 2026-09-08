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
            TheTwoThrottles(check);
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
        }
    }
}
