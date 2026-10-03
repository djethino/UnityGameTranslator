using System;
using System.Collections;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The engine's arrays, lists and dictionaries, read and written by reflection the same way on
    /// Mono and IL2CPP: a managed T[] / List / Dictionary on one, the interop's own types on the
    /// other — both answer to an <c>Item</c> indexer and a <c>Length</c> or <c>Count</c>. An element
    /// that is a struct comes back boxed: changed through <see cref="Members.Set"/>, then written
    /// back with <see cref="SetItem"/> / <see cref="SetEntry"/>. Engine objects are wrapped anew at
    /// every read on IL2CPP: compared with <see cref="Same"/>, never by reference.
    /// </summary>
    internal static class EngineCollections
    {
        /// <summary>The element type of an array or a list — T[], the interop's array, List&lt;T&gt;.</summary>
        internal static Type ElementType(Type collectionType)
        {
            if (collectionType == null) return null;
            if (collectionType.IsArray) return collectionType.GetElementType();
            var item = collectionType.GetProperty("Item");
            if (item != null && item.GetIndexParameters().Length == 1 && item.GetIndexParameters()[0].ParameterType == typeof(int))
                return item.PropertyType;
            for (var t = collectionType; t != null; t = t.BaseType)
                if (t.IsGenericType && t.GetGenericArguments().Length == 1) return t.GetGenericArguments()[0];
            return null;
        }

        /// <summary>Element count of an array, a list or a dictionary; 0 for none.</summary>
        internal static int Length(object collection)
        {
            if (collection == null) return 0;
            if (collection is Array a) return a.Length;
            if (collection is ICollection c) return c.Count;
            var p = collection.GetType().GetProperty("Length") ?? collection.GetType().GetProperty("Count");
            return p == null ? 0 : Convert.ToInt32(p.GetValue(collection, null));
        }

        /// <summary>The element at <paramref name="index"/>, or null past the end of a managed array.</summary>
        internal static object Item(object collection, int index)
        {
            if (collection is Array a) return index >= 0 && index < a.Length ? a.GetValue(index) : null;
            return collection.GetType().GetProperty("Item")?.GetValue(collection, new object[] { index });
        }

        internal static void SetItem(object collection, int index, object value)
        {
            if (collection is Array a) { a.SetValue(value, index); return; }
            collection.GetType().GetProperty("Item")?.SetValue(collection, value, new object[] { index });
        }

        /// <summary>One element added at the end of a list — a managed one, or the interop's (its own Add).</summary>
        internal static void Add(object list, object value)
        {
            if (list is IList managed) { managed.Add(value); return; }
            var add = list.GetType().GetMethod("Add", new[] { ElementType(list.GetType()) ?? typeof(object) });
            if (add == null) throw new MissingMethodException(list.GetType().Name, "Add");
            add.Invoke(list, new[] { value });
        }

        /// <summary>A new array of <paramref name="arrayType"/> — managed, or the interop's (made by length).</summary>
        internal static object NewArray(Type arrayType, int length) =>
            arrayType.IsArray ? Array.CreateInstance(arrayType.GetElementType(), length)
                              : arrayType.GetConstructor(new[] { typeof(long) }).Invoke(new object[] { (long)length });

        /// <summary>A dictionary's value for <paramref name="key"/>, or null when it has none.</summary>
        internal static object Entry(object dictionary, object key)
        {
            var type = dictionary.GetType();
            if (!(bool)type.GetMethod("ContainsKey").Invoke(dictionary, new[] { key })) return null;
            return type.GetProperty("Item").GetValue(dictionary, new[] { key });
        }

        internal static void SetEntry(object dictionary, object key, object value) =>
            dictionary.GetType().GetProperty("Item").SetValue(dictionary, value, new[] { key });

        internal static bool Same(object a, object b) => ReferenceEquals(a, b) || (a != null && b != null && a.Equals(b));
    }
}
