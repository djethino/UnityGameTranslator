using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    public sealed partial class TextRouter
    {
        // === TEMPLATES THE GAME EXPANDS IN PLACE ===
        //
        // A game writes its own template on a component (`*Overclock* ({0}): Add {1} Strength.`)
        // and then resolves its tokens into markup and values, a few at a time. The template is
        // not a line anybody reads — and its translation, written back, stops the game finding
        // `*Overclock*` and `{0}` to expand at all. The reveal sees the expansion
        // (TextRouter.Reveal, SameAfterExpansion) and says so here.
        //
        // ⚠ The habit is the GAME's code, not an engine's: this lives beside the reveal it comes
        // from, so every engine and the corpus's replay follow the same rule.
        //
        // ⚠ In memory, for the session, like every refusal of the queue: a scene change does not
        // forget it (Clear leaves it alone), the next launch asks again — and if the game still
        // expands the text in place it is refused again in the same second.

        // Skeletons of the templates seen expanded. Read from the worker's thread as well (the
        // answer is refused before it is stored), hence the lock.
        private readonly HashSet<string> _expandedInPlace = new HashSet<string>();
        private readonly object _templatesLock = new object();

        /// <summary>
        /// Whether this text is one the game expands in place — a template, not a line anybody
        /// reads. Asked at the three moments it matters (the queue's door, the store, the lookup),
        /// because it is one FACT rather than one act.
        ///
        /// 🔴 **Taking it out of the queue is not enough.** The proof arrives with the expansion, a
        /// few hundred milliseconds after the template was queued, and the worker may have taken it
        /// in between. So the withdrawal is the best case; what makes the rule deterministic is that
        /// once the pair has been seen, the text is never queued, never stored, and above all never
        /// written back.
        ///
        /// ⚠ That last one is what protects a file polluted before this rule existed. The line stays
        /// in it — deleting somebody's translation on a local observation is the thing this project
        /// refuses — but it stops reaching the screen, so the game can expand its own text again.
        /// </summary>
        public bool IsExpandedInPlace(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            lock (_templatesLock)
            {
                if (_expandedInPlace.Count == 0) return false;
            }

            // 🔴 The FINISHED form of the same template goes through, and it must: it is the line
            // the player reads and the one worth translating. Only the states that still carry
            // something for the game to resolve are refused.
            if (!TextRelations.HasUnresolvedTokens(text)) return false;

            string skeleton = TextRelations.ExpansionSkeleton(text);
            lock (_templatesLock) { return _expandedInPlace.Contains(skeleton); }
        }

        /// <summary>
        /// This text turned out to be a template the game expands in place: remembered, and taken
        /// back from the queue by the host the first time.
        ///
        /// 🔴 The SKELETON, not the text. The game resolves its tokens a few at a time, and each
        /// state is its own string — so a refusal recorded on one of them says nothing about the
        /// next, nor about the same template appearing on another component in another half-
        /// resolved form. Measured: the fully-tokenised state was refused while
        /// `…[*White*] Energy, add 2 Strength.` went to the model on the component beside it and
        /// came back with the keyword translated, which is exactly what the game cannot expand.
        /// </summary>
        private void ForgetTemplate(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            string skeleton = TextRelations.ExpansionSkeleton(text);
            if (skeleton.Length == 0) return;

            bool added;
            lock (_templatesLock) { added = _expandedInPlace.Add(skeleton); }
            if (added) _host.Withdraw(text, Admission.Template);
        }
    }
}
