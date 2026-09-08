using System;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// What every backend does to a text before sending it, and to the answer before believing it.
    ///
    /// 🔴 **The order is the rule, in both directions, and it is what a second engine's Core has
    /// to reproduce.** Going out: line breaks become tokens, then markup, then the padding is cut.
    /// Coming back: markup, then line breaks, then the model's chatter, then the padding. Every one
    /// of those positions has a reason, and getting one wrong produces a translation that looks
    /// almost right — which is the kind nobody reports.
    ///
    /// 🔴 **Written once because it was written twice.** The model path and the translation-API
    /// path each carried their own copy, identical apart from one step. Two copies of a rule is one
    /// that gets fixed and one that does not — and per engine, it would have been two each.
    /// </summary>
    internal static class BackendsChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            TakingItApart(check);
            PuttingItBack(check);
            WhoAnswered(check);
            TheRoundTrip(check);
        }

        private static void TakingItApart(Action<bool, string, string> check)
        {
            var lines = Backends.Prepare("Hello\nworld");
            check(lines.ToSend == "Hello[!nl]world",
                "a line break becomes a token",
                "the shape belongs to the game's layout; a model free to reflow it returns a label that no longer fits");

            var tagged = Backends.Prepare("<b>Play</b>");
            check(tagged.ToSend == "[!t*0]Play[!t*1]" && tagged.Tags.Count == 2
                  && tagged.Tags[0] == "<b>" && tagged.Tags[1] == "</b>",
                "markup is lifted out and numbered",
                "a model that sees markup translates it, reorders it or invents some, and the game renders the result literally");

            var padded = Backends.Prepare("   Play   ");
            check(padded.ToSend == "Play" && padded.Leading == "   " && padded.Trailing == "   ",
                "visual padding is held back, not sent",
                "asked to translate '  Play  ' a model answers about the spaces as often as not, and the game laid them out for a reason");

            // 🔴 The order: the trim runs LAST, so a trailing newline is already a token and is not
            // whitespace any more. That is what makes the answer accountable for it.
            var trailingBreak = Backends.Prepare("Credits\n\n");
            check(trailingBreak.ToSend == "Credits[!nl][!nl]" && trailingBreak.Trailing == "",
                "a trailing line break survives the trim, because it is no longer whitespace",
                "shaved off as padding it would come back silently missing; as a token, the answer has to give it back");

            var mixed = Backends.Prepare("  <b>Go</b>\nnow  ");
            check(mixed.ToSend == "[!t*0]Go[!t*1][!nl]now"
                  && mixed.Leading == "  " && mixed.Trailing == "  ",
                "and all three together leave only the words",
                "what reaches a backend is the translatable text and the slots, nothing else");

            var plain = Backends.Prepare("Play");
            check(plain.ToSend == "Play" && plain.Tags.Count == 0
                  && plain.Leading == "" && plain.Trailing == "" && !plain.NothingToSend,
                "an ordinary label passes through untouched",
                "the overwhelmingly common case, and it must cost nothing and change nothing");

            check(Backends.Prepare("   ").NothingToSend
                  && Backends.Prepare("").NothingToSend
                  && Backends.Prepare(null).NothingToSend,
                "and a text with nothing translatable is not sent",
                "the two paths disagreed here — one returned, the other sent an empty request to a model and paid for it");
        }

        private static void PuttingItBack(Action<bool, string, string> check)
        {
            var prepared = Backends.Prepare("  <b>Hello</b>\nworld  ");

            check(Backends.Restore(prepared, "[!t*0]Bonjour[!t*1][!nl]monde", AnswerFrom.TranslationApi)
                      == "  <b>Bonjour</b>\nmonde  ",
                "everything taken out comes back where it was",
                "this is the whole contract: what the game gets differs from what it had only in the words");

            check(Backends.Restore(Backends.Prepare("Play"), null, AnswerFrom.Model) == null
                  && Backends.Restore(Backends.Prepare("Play"), "", AnswerFrom.Model) == "",
                "an empty answer is handed straight back",
                "dressing an empty string in the original's padding would produce a translation made entirely of spaces");

            // ⚠ The padding goes on LAST. Removing a model's chatter also trims, so any earlier
            // and it is put on and taken straight off again.
            var padded = Backends.Prepare("  Play  ");
            check(Backends.Restore(padded, "\"Jouer\"", AnswerFrom.Model) == "  Jouer  ",
                "the padding survives the chatter being removed",
                "removing chatter trims, so padding put on before it is put on and taken straight off");

            var untouched = Backends.Prepare("Play");
            check(Backends.Restore(untouched, "Jouer", AnswerFrom.Model) == "Jouer",
                "and an answer with nothing to restore is itself",
                "the common case again: no markup, no breaks, no padding, nothing to do");
        }

        private static void WhoAnswered(Action<bool, string, string> check)
        {
            var prepared = Backends.Prepare("Play");

            // 🔴 The one step that differs between the two paths, and the reason it differs.
            check(Backends.Restore(prepared, "\"Jouer\"", AnswerFrom.Model) == "Jouer",
                "a model's answer is stripped of what it wrapped around the translation",
                "it was given instructions and may answer with a preamble, quotes, or a thinking block");

            check(Backends.Restore(prepared, "\"Jouer\"", AnswerFrom.TranslationApi) == "\"Jouer\"",
                "a translation service's answer is not",
                "it takes no instructions and returns a translation and nothing else — anything removed here is text somebody asked for");

            // ⚠ And that is not a nicety: a game whose label really is quoted gets it back.
            var quotedLabel = Backends.Prepare("\"Quoted\"");
            check(Backends.Restore(quotedLabel, "\"Cité\"", AnswerFrom.TranslationApi) == "\"Cité\"",
                "so a label that is genuinely quoted keeps its quotes",
                "the model path cannot tell the two apart and must guess; the service path does not have to");
        }

        private static void TheRoundTrip(Action<bool, string, string> check)
        {
            // What a real game line looks like, through both paths, with the backend echoing.
            const string source = "  <color=#ff0000>Warning</color>\nPress [!v*0] to continue  ";

            var prepared = Backends.Prepare(source);
            check(Backends.Restore(prepared, prepared.ToSend, AnswerFrom.TranslationApi) == source,
                "a text taken apart and put back untranslated is the text again",
                "anything this loses on its own is lost on every line the mod ever handles");

            check(prepared.ToSend.Contains("[!v*0]"),
                "and the number slot passes straight through",
                "it was lifted before the cache was even consulted, because the text with it IS the cache key");

            // ⚠ A model that answers with the tokens in a different ORDER is honoured — the tokens
            // are numbered, so a language that puts things the other way round still works.
            var reordered = Backends.Prepare("<b>Go</b> now");
            check(Backends.Restore(reordered, "maintenant [!t*0]Va[!t*1]", AnswerFrom.TranslationApi)
                      == "maintenant <b>Va</b>",
                "a translation may put the slots in another order",
                "word order differs between languages, and the numbering is what lets it");
        }
    }
}
