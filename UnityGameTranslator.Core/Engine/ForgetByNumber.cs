using System;
using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The tables the mod files per object NUMBER, and the one way to make them all forget a number
    /// that has come to stand for another object (ObjectNumbers decides when, on the engine's side).
    /// Pure — no engine type — so the Engine's own tables (TextRouter, FontRules…) register here too,
    /// and the checks replay it. Why a number can change objects: analyse/numeros-objets-reutilises.md.
    /// </summary>
    internal static class ForgetByNumber
    {
        private static readonly List<Action<long>> _forgetters = new List<Action<long>>();
        private static readonly object _gate = new object();

        /// <summary>A table filed by object number, forgotten per number.</summary>
        internal static Dictionary<int, TValue> Tracked<TValue>(Dictionary<int, TValue> table)
        {
            Add(id => { if (id >= int.MinValue && id <= int.MaxValue) table.Remove((int)id); });
            return table;
        }

        /// <summary>The same, filed by a long number (an object number widened).</summary>
        internal static Dictionary<long, TValue> Tracked<TValue>(Dictionary<long, TValue> table)
        {
            Add(id => table.Remove(id));
            return table;
        }

        internal static HashSet<int> Tracked(HashSet<int> set)
        {
            Add(id => { if (id >= int.MinValue && id <= int.MaxValue) set.Remove((int)id); });
            return set;
        }

        internal static HashSet<long> Tracked(HashSet<long> set)
        {
            Add(id => set.Remove(id));
            return set;
        }

        /// <summary>
        /// An object that keeps its own per-number memory and forgets it itself (TextRouter,
        /// FontRules, ExclusionRules, SizeTagWrites) — registered ONCE, at the declaration of the one
        /// instance the mod lives with: an instance registered and replaced would stay held here.
        /// </summary>
        internal static T Tracked<T>(T owner, Action<T, long> forget) where T : class
        {
            Add(id => forget(owner, id));
            return owner;
        }

        /// <summary>A table whose key is not one number (a pair packed in a long, a name built on one): it says itself what to drop.</summary>
        internal static void OnForget(Action<long> forget) => Add(forget);

        /// <summary>Every table forgets this number.</summary>
        internal static void Forget(long id)
        {
            Action<long>[] all;
            lock (_gate) all = _forgetters.ToArray();
            foreach (var forget in all) forget(id);
        }

        private static void Add(Action<long> forget)
        {
            lock (_gate) _forgetters.Add(forget);
        }
    }
}
