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

        /// <summary>
        /// The field of that name, or the property when there is no field: an engine struct is
        /// fields on Mono and properties on IL2CPP, where the interop wrapper exposes each field
        /// as a property. Asking for the field alone read as "absent" there, without a word.
        /// </summary>
        public static MemberInfo FieldOrProperty(Type type, string name, BindingFlags flags)
        {
            if (type == null) return null;
            return (MemberInfo)type.GetField(name, flags) ?? Property(type, name, flags);
        }

        /// <summary>The type a <see cref="FieldOrProperty"/> member holds.</summary>
        public static Type TypeOf(MemberInfo member) =>
            member is FieldInfo f ? f.FieldType : member is PropertyInfo p ? p.PropertyType : null;

        public static object Get(MemberInfo member, object target) =>
            member is FieldInfo f ? f.GetValue(target) : ((PropertyInfo)member).GetValue(target, null);

        /// <summary>
        /// Writes a <see cref="FieldOrProperty"/> member. On a boxed struct (Mono) the box itself
        /// changes, so the caller hands the same box on (or writes it back into its array).
        /// </summary>
        public static void Set(MemberInfo member, object target, object value)
        {
            if (member is FieldInfo f) f.SetValue(target, value);
            else ((PropertyInfo)member).SetValue(target, value, null);
        }
    }
}
