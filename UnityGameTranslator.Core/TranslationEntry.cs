namespace UnityGameTranslator.Core
{
    /// <summary>
    /// A translation entry with value and tag.
    /// JSON format: {"v": "value", "t": "A/H/V", "i": 123}
    ///
    /// ⚠ **In its own file, and it must stay pure.** It moved out of TranslatorCore.cs so that the
    /// checks project can link the file mechanics that handle it — that file references UnityEngine,
    /// so anything reachable from a check has to live outside it. A `using UnityEngine` here stops
    /// tests/UnityGameTranslator.Core.Checks from compiling, which is the alarm, not an accident.
    /// </summary>
    public class TranslationEntry
    {
        /// <summary>The translated value</summary>
        public string Value { get; set; } = "";

        /// <summary>
        /// Tag indicating the source of this translation.
        /// A = AI generated, H = Human, V = AI Validated by human,
        /// S = Skipped (wrong source language), M = Mod UI.
        /// Null defaults to A.
        /// </summary>
        public string Tag { get; set; } = "A";

        /// <summary>
        /// Capture-order index "i": monotonic number assigned when the text is
        /// first captured, used by the web editors to sort entries in the order
        /// they appeared in-game. Presentation metadata ONLY — excluded from the
        /// content hash (mod and website), ignored by merge comparisons, and
        /// absent on entries written by older mod versions.
        /// </summary>
        public long? Index { get; set; }

        /// <summary>True if this is a Skipped or Mod UI entry (immutable tags)</summary>
        public bool IsImmutableTag => Tag == "S" || Tag == "M";

        /// <summary>True if Value is null or empty</summary>
        public bool IsEmpty => string.IsNullOrEmpty(Value);

        /// <summary>True if this is a Human-tagged empty entry (capture-only placeholder)</summary>
        public bool IsHumanEmpty => Tag == "H" && IsEmpty;

        /// <summary>
        /// Get the priority of this entry for merge conflict resolution.
        /// Higher priority wins: H empty (0) &lt; A (1) &lt; V (2) &lt; H with value (3) &lt; S/M (99)
        /// S and M are immutable and should never be replaced.
        ///
        /// ⚠ The ladder itself lives in <see cref="UnityGameTranslator.Common.Merge.PriorityOf"/>.
        /// It decides who wins a merge with nobody asked, and the manager settles the same lines
        /// from outside a running game — two tables would be two answers about one file.
        /// </summary>
        public int Priority => Common.Merge.PriorityOf(Tag, Value);

        /// <summary>
        /// Create a new TranslationEntry from a string value (defaults to AI tag).
        /// </summary>
        public static TranslationEntry FromValue(string value, string tag = "A")
        {
            return new TranslationEntry { Value = value ?? "", Tag = tag ?? "A" };
        }

        /// <summary>
        /// Check if this entry can replace another entry based on tag hierarchy.
        /// S and M tags are immutable and cannot be replaced.
        /// </summary>
        public bool CanReplace(TranslationEntry other)
        {
            if (other == null) return true;
            // Cannot replace immutable tags (S/M) regardless of priority
            if (other.IsImmutableTag) return false;
            return Priority > other.Priority;
        }

        public override string ToString() => $"{Value} [{Tag}]";
    }
}
