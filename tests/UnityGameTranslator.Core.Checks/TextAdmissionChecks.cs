using System;
using System.Collections.Generic;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What a text is refused for at the queue's door and at the store (Engine/TextAdmission) —
    /// one refusal per case, each against the facts that trigger it and nothing else.
    ///
    /// ⚠ Asked directly rather than through the routing corpus: this is the last line of defence,
    /// and the router already turns most of these back before they reach it — a corpus case would
    /// stay green with a hole in the door. The corpus holds the sequences (`template/…`); this holds
    /// each refusal on its own.
    /// </summary>
    internal static class TextAdmissionChecks
    {
        private sealed class Facts : IAdmissionFacts
        {
            public readonly HashSet<string> Templates = new HashSet<string>();
            public readonly HashSet<string> Targets = new HashSet<string>();
            public readonly HashSet<string> Readbacks = new HashSet<string>();
            public readonly HashSet<string> GivenUp = new HashSet<string>();
            public bool IsExpandedInPlace(string text) => Templates.Contains(text);
            public bool IsAlreadyTarget(string text) => Targets.Contains(text);
            public bool IsReadback(string key, bool ownUi) => Readbacks.Contains(key);
            public bool WasGivenUp(string text, bool ownUi) => GivenUp.Contains(text);
        }

        public static void Run(Action<bool, string, string> check)
        {
            var facts = new Facts();

            void Queue(string text, bool ownUi, Admission expected, string what, string why)
            {
                var got = TextAdmission.ForQueue(text, ownUi, facts);
                check(got == expected, what, got == expected ? why : $"got {got}, expected {expected}  —  {why}");
            }

            void Store(string key, bool ownUi, Admission expected, string what, string why)
            {
                var got = TextAdmission.ForStore(key, ownUi, facts);
                check(got == expected, what, got == expected ? why : $"got {got}, expected {expected}  —  {why}");
            }

            Queue("Open the door", false, Admission.Admitted,
                "an ordinary line is sent", "nothing about it is refused");

            Queue("ﺍﻠﺮﺎ", false, Admission.PresentationForms,
                "a shaped right-to-left text is never sent",
                "it is our own composed output read back, or a game's own shaping: sent, it becomes a key no logical text ever matches");

            Queue("12 / 40", false, Admission.NumericOrSymbol,
                "numbers and symbols alone are never sent", "they read the same in every language");

            facts.Templates.Add("*Activate* ({0}): Add {1} *Power*.");
            Queue("*Activate* ({0}): Add {1} *Power*.", false, Admission.Template,
                "a template the game expands in place is never sent", "its translation, written back, stops the game expanding it");

            Queue(new string('a', Limits.AiTextLength + 1), false, Admission.TooLong,
                "a text longer than any backend accepts is never sent", "refused at the door, never stored as an entry tagged S");

            facts.Targets.Add("Ouvrir la porte");
            Queue("Ouvrir la porte", false, Admission.AlreadyTarget,
                "our own translation read back is never sent", "sent, it is translated again and drifts");
            Queue("Ouvrir la porte", true, Admission.Admitted,
                "but a label of the mod's own interface is",
                "its labels are source text the mod produces, never a read-back — and the game's index must not decide for it");

            facts.GivenUp.Add("A line the model failed");
            Queue("A line the model failed", false, Admission.GivenUp,
                "a line given up this session is not sent again", "what reopens it is a person asking, never another hover");

            Store("Open the door", false, Admission.Admitted,
                "an ordinary answer is stored", "nothing about it is refused");
            Store("*Activate* ({0}): Add {1} *Power*.", false, Admission.Template,
                "an answer for a template is never stored",
                "🔴 the proof can arrive after the model took the text: refusing it here is what makes the rule deterministic");
            Store("*Activate* ({0}): Add {1} *Power*.", true, Admission.Admitted,
                "the interface's own store is not asked about the game's templates", "the two sides never answer for each other");

            facts.Readbacks.Add("<b>Ouvrir la porte</b>");
            Store("<b>Ouvrir la porte</b>", false, Admission.AlreadyTarget,
                "our own translation under another decoration never becomes an entry",
                "every route that creates one passes here: the last stop before the file holds a key in the target language");

            var queue = new TranslationQueue();
            queue.Submit("*Activate* ({0}): Add {1} *Power*.", null, false, out _, out _);
            bool waiting = TextAdmission.WithdrawTemplate(queue, "*Activate* ({0}): Add {1} *Power*.", "*Activate* ({0}): Add {1} *Power*.");
            check(waiting && queue.Count == 0 && queue.WasRefused("*Activate* ({0}): Add {1} *Power*."),
                "a template proved while waiting is taken out, and given up",
                "taken out so no call is made; given up so the next write of it does not queue it again");
            check(!TextAdmission.WithdrawTemplate(queue, "*Overclock* ({0})", "*Overclock* ({0})") && queue.WasRefused("*Overclock* ({0})"),
                "one proved before it was queued is given up all the same",
                "the answer says whether a call may already have gone; the give-up holds either way");
        }
    }
}
