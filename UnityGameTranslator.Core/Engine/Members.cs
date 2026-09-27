using System;
using System.Reflection;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Reflection lookups that answer instead of throwing.
    ///
    /// 🔴 **`Type.GetProperty(name, flags)` throws AmbiguousMatchException** when a type hides an
    /// inherited property of the same name with `new` — which engine and game types do, and which
    /// Unity 6 does. Each such lookup used to sit in its own `catch { }`, so a type with that shape
    /// read as "this property does not exist", without a word. Walking the properties and taking
    /// the most derived one recognises the case instead of catching it.
    /// </summary>
    public static class Members
    {
        /// <summary>
        /// The non-indexed property of that name, the most derived one when several are visible
        /// (a derived type hiding an inherited one); null when there is none.
        /// </summary>
        public static PropertyInfo Property(Type type, string name, BindingFlags flags)
        {
            if (type == null) return null;
            PropertyInfo best = null;
            foreach (var property in type.GetProperties(flags))
            {
                if (property.Name != name || property.GetIndexParameters().Length != 0) continue;
                if (best == null || best.DeclaringType.IsAssignableFrom(property.DeclaringType)) best = property;
            }
            return best;
        }
    }
}
