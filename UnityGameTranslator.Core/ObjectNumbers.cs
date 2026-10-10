using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The number the mod files an engine object under — and the one place that makes sure the
    /// number still means THAT object.
    ///
    /// 🔴 **Unity 6.4+ hands a destroyed object's number to the next object it makes** (« Migrate
    /// from InstanceID to EntityId »: "After Unity destroys an object, it can reuse that object's
    /// EntityId value for a different object"). What GetInstanceID returns there is the entity's
    /// index alone: measured at the bench (`banc-unity`, `-probeMode idreuse`, 6000.6.3f1), sixteen
    /// TMP texts made and destroyed one after the other all came back as 14090, and every second one
    /// inherited the previous one's state — "already wearing its replacement" — and was never
    /// replaced (8/16; 16/16 on 6000.3 and 2021.3, where a number never comes back). Everything the
    /// mod keeps per object is filed by that number (analyse/numeros-objets-reutilises.md).
    ///
    /// So every number is asked HERE (<see cref="Of"/>; TypeHelper.GetInstanceID goes through it,
    /// and Core.Checks refuses any other call to GetInstanceID). The object last met under each
    /// number is kept; when a LIVE object arrives under a number that stood for another one — dead,
    /// or not the same — every table filed by number forgets it (ForgetByNumber) before anybody reads
    /// them. Before Unity 6.4 the answer is always "the same object", and the only change is that the
    /// dead are swept (<see cref="Sweep"/>) instead of piling up.
    ///
    /// ⚠ "The same" is Unity's equality, not the reference: under IL2CPP a hook may receive a new
    /// wrapper of the same object at each call (UI Toolkit learnt it: UIToolkitSupport.IdFor), and
    /// Unity's == compares the objects behind. Kept STRONGLY for the same reason: a weak reference on
    /// a wrapper dies while its object lives on. Only the main thread forgets: the tables are its.
    /// </summary>
    internal static class ObjectNumbers
    {
        private static readonly Dictionary<int, UnityEngine.Object> _metUnder = new Dictionary<int, UnityEngine.Object>();
        private static readonly object _gate = new object();

        /// <summary>The number of an engine object; -1 for null.</summary>
        internal static int Of(UnityEngine.Object obj)
        {
            if ((object)obj == null) return -1;
            int id = obj.GetInstanceID();
            // A destroyed object reports its old number and must never take it back from the live
            // object filed under it now; another thread only reads.
            if (!TranslatorCore.IsMainThread || obj == null) return id;
            lock (_gate)
            {
                if (_metUnder.TryGetValue(id, out var known))
                {
                    if (ReferenceEquals(known, obj)) return id;
                    if (known != null && known == obj) { _metUnder[id] = obj; return id; }
                    ForgetByNumber.Forget(id);
                }
                _metUnder[id] = obj;
            }
            return id;
        }

        /// <summary>
        /// The numbers of objects destroyed since they were met, forgotten everywhere — at a scene
        /// change (TranslatorCore.OnSceneChanged), when whole screens of them are gone. Without it the
        /// tables kept every object a session ever showed.
        /// </summary>
        internal static void Sweep()
        {
            if (!TranslatorCore.IsMainThread) return;
            lock (_gate)
            {
                List<int> dead = null;
                foreach (var kv in _metUnder)
                    if (kv.Value == null) (dead ?? (dead = new List<int>())).Add(kv.Key);
                if (dead == null) return;
                foreach (int id in dead)
                {
                    ForgetByNumber.Forget(id);
                    _metUnder.Remove(id);
                }
            }
        }
    }
}
