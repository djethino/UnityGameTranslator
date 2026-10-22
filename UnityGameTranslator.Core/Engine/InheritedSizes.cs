using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The sizes the mod wrote, remembered by what a COPY of the component would carry — so a copy
    /// met for the first time is sized from the game's size, not from ours.
    ///
    /// 🔴 **Why it exists** (2026-10-22). A game that builds its scenes by copying a template it keeps
    /// in the scene (a pool of rooms, a list row) copies the component as it is at that moment —
    /// with the size the mod put on it. Met for the first time, the copy's size was taken for the
    /// game's own: sized again when a rule matched it too (×0.8 then ×0.8), kept at the template's
    /// size when nothing matched it at all — a text elsewhere, in the same font, left small.
    ///
    /// ⚠ **How a copy is recognised: three things together.** The same font, the same object name (a
    /// copy keeps its children's names; its root gets Unity's « (Clone) », see <see cref="CopyName"/>), and a size the mod itself wrote on
    /// such a component. A component of the game's own with that name, that font and, natively,
    /// exactly that size would be taken for a copy — which is why two different game sizes leading to
    /// the same written size make the answer ambiguous, and an ambiguous answer is no answer.
    ///
    /// ⚠ Not keyed by component, so not forgotten with one: a template may die before its copies are
    /// met, and the copies still carry its size. One entry per distinct (kind, font, name, size).
    ///
    /// ⚠ Pure by contract (linked into Core.Checks).
    /// </summary>
    internal sealed class InheritedSizes
    {
        private struct Key : IEquatable<Key>
        {
            public string What, Font, Name;
            public int Written;   // tenths of a point: what a float size compares on

            public bool Equals(Key o) => Written == o.Written && What == o.What && Font == o.Font && Name == o.Name;
            public override bool Equals(object obj) => obj is Key k && Equals(k);
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Written;
                    h = h * 31 + (What?.GetHashCode() ?? 0);
                    h = h * 31 + (Font?.GetHashCode() ?? 0);
                    h = h * 31 + (Name?.GetHashCode() ?? 0);
                    return h;
                }
            }
        }

        /// <summary>NaN marks an ambiguous entry: two game sizes led to the same written one.</summary>
        private readonly Dictionary<Key, float> _originals = new Dictionary<Key, float>();

        /// <summary>Whether the mod has written any size a copy could carry — asked before reading a component.</summary>
        public bool Any => _originals.Count > 0;

        private static int Tenths(float size) => (int)Math.Round(size * 10f);

        private static bool Usable(string font, string name) => !string.IsNullOrEmpty(font) && !string.IsNullOrEmpty(name);

        private const string CloneSuffix = "(Clone)";

        /// <summary>
        /// The name a copy shares with its template. Unity renames the ROOT of what it copies by
        /// appending <c>(Clone)</c> — once per copy, so a copy of a copy reads <c>(Clone)(Clone)</c> —
        /// and a text sitting on that root would otherwise never match its template. Taken off both
        /// sides, so a template that is itself a copy matches too. A root the GAME renames afterwards
        /// is out of reach: nothing of the template is left in its name.
        /// </summary>
        internal static string CopyName(string name)
        {
            if (name == null) return null;
            while (name.EndsWith(CloneSuffix, StringComparison.Ordinal))
                name = name.Substring(0, name.Length - CloneSuffix.Length).TrimEnd();
            return name;
        }

        /// <summary>
        /// The mod put <paramref name="written"/> on a component whose own size is
        /// <paramref name="original"/>. Nothing is remembered when the two are the same: a copy then
        /// carries the game's size, which needs no answer.
        /// </summary>
        public void Wrote(string what, string font, string name, float original, float written)
        {
            name = CopyName(name);
            if (!Usable(font, name) || Tenths(original) == Tenths(written)) return;
            var key = new Key { What = what, Font = font, Name = name, Written = Tenths(written) };
            if (_originals.TryGetValue(key, out var known) && (float.IsNaN(known) || Tenths(known) != Tenths(original)))
                _originals[key] = float.NaN;
            else
                _originals[key] = original;
        }

        /// <summary>
        /// The game's size for a component met for the first time that carries
        /// <paramref name="current"/> — when that is a size the mod wrote on a component of the same
        /// font and name. False when it is not, or when the answer is ambiguous.
        /// </summary>
        public bool TryOriginal(string what, string font, string name, float current, out float original)
        {
            original = 0f;
            name = CopyName(name);
            if (!Usable(font, name)) return false;
            var key = new Key { What = what, Font = font, Name = name, Written = Tenths(current) };
            if (!_originals.TryGetValue(key, out var known) || float.IsNaN(known)) return false;
            original = known;
            return true;
        }
    }
}
