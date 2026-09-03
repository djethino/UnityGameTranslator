using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// What becomes of the mod's interface lines that a game translation is still carrying.
    ///
    /// 🔴 **Pure by contract, and here for that reason.** These are the two judgements the split
    /// rests on — which stranded line is kept and which is thrown away, and whether a line the
    /// published copy holds still counts towards this file's identity. Both were three lines deep
    /// inside <c>LoadCache</c> and <c>ComputeContentHash</c>, where nothing could ask them a
    /// question without a game, a disk and a network. They answer from their inputs alone, so the
    /// checks project links this file and asks them directly.
    ///
    /// ⚠ No Unity, no state, no clock. See tests/UnityGameTranslator.Core.Checks.
    /// </summary>
    public static class ModUiMigration
    {
        /// <summary>What to do with one interface line found in a game translation.</summary>
        public enum Verdict
        {
            /// <summary>Keep it: move it into the mod's interface file.</summary>
            Move,

            /// <summary>Throw it away.</summary>
            Drop,
        }

        /// <summary>
        /// Decide the fate of one line tagged as the mod's interface, found in translations.json.
        ///
        /// 🔴 **Kept when this machine wrote it, dropped when it came from outside.** The ancestor
        /// is the copy of what the server holds, so a line that is in it arrived with somebody
        /// else's translation — and an interface line from a stranger chooses the words of our own
        /// buttons, on a mod that handles tokens, uploads and file replacement. That is the whole
        /// reason the interface has a file of its own; importing them back would undo it.
        ///
        /// A line absent from the ancestor was produced here, and throwing away a pass of the
        /// translator would be a loss for no gain.
        /// </summary>
        /// <param name="inAncestor">The published copy carries this key.</param>
        /// <param name="alreadyHeld">The interface file already has this key — it wins, being the
        /// current language's work rather than a leftover.</param>
        /// <param name="isEmpty">Nothing in it. There is no work to save in an empty line, and the
        /// interface file has no editor to fill one in.</param>
        public static Verdict Decide(bool inAncestor, bool alreadyHeld, bool isEmpty)
        {
            if (inAncestor || alreadyHeld || isEmpty) return Verdict.Drop;
            return Verdict.Move;
        }

        /// <summary>
        /// Whether one line of the published copy must still be counted as present when this
        /// file's identity is computed.
        ///
        /// 🔴 **The hashing rule is a contract with the website** — what it produces is the
        /// file_hash every "is there an update" decision compares — so it cannot be taught to
        /// ignore interface lines: a file_hash that moves is every installed mod being told the
        /// server changed. What changed is this file, which no longer keeps the mod's interface
        /// inside the game's translation.
        ///
        /// So an interface line the published copy holds and this file no longer does is counted
        /// as still there. The answer is then "yes, this is the same translation as the published
        /// one", which is what the hash is actually asked.
        ///
        /// ⚠ Self-clearing: the first legitimate upload publishes a file without those lines, the
        /// ancestor is rewritten from what was sent, and nothing meets this condition again.
        ///
        /// ⚠ Nothing else is added back. A game line missing locally is a deletion somebody made,
        /// and saying otherwise would hide it.
        /// </summary>
        /// <param name="ancestorTag">The tag the published copy carries for this key.</param>
        /// <param name="presentLocally">The game's file still has this key.</param>
        public static bool StillCountsAsPublished(string ancestorTag, bool presentLocally)
        {
            if (presentLocally) return false;
            return !Merge.IsGameLine(ancestorTag);
        }
    }
}
