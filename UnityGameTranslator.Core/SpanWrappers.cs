using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Calling an engine method that takes a string or an array on Unity 2023.1+ under IL2CPP.
    ///
    /// 🔴 From 2023.1 Unity hands strings and arrays to native code as a <c>ManagedSpanWrapper</c>
    /// (pointer + length), so those methods have a managed body, and the interop rebuilds it around an
    /// Il2CppSystem span the game's runtime cannot use: MissingMethodException at every call
    /// (GetPinnableReference), or the process dies outright (LoadImage). The way out is the native
    /// entry the interop makes public, <c>X_Injected(…, ref ManagedSpanWrapper, …)</c>, handed a
    /// wrapper pointed at our own pinned data — native code reads it once, synchronously, and keeps
    /// nothing. Shared by every such call (FontAtlas, TextureUtils, TypeHelper.NewFontFromFile, TransformFind, EngineStrings).
    /// </summary>
    internal static class SpanWrappers
    {
        /// <summary>The wrapper type of an injected entry and its two fields.</summary>
        internal sealed class Shape
        {
            internal Type Type;
            internal FieldInfo Begin, Length;
        }

        /// <summary>
        /// The shape of the wrapper parameter number <paramref name="index"/> of an injected entry, or
        /// null when that parameter is no <c>ref ManagedSpanWrapper</c> with the two fields expected.
        /// </summary>
        internal static Shape Of(MethodInfo injected, int index)
        {
            var p = injected?.GetParameters();
            if (p == null || index >= p.Length || !p[index].ParameterType.IsByRef) return null;
            var type = p[index].ParameterType.GetElementType();
            if (type?.Name != "ManagedSpanWrapper") return null;
            var begin = type.GetField("begin", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var length = type.GetField("length", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (begin == null || begin.FieldType != typeof(IntPtr) || length == null || length.FieldType != typeof(int)) return null;
            return new Shape { Type = type, Begin = begin, Length = length };
        }

        /// <summary>Whether this parameter of the method is a <c>ref ManagedSpanWrapper</c> (its fields not looked at).</summary>
        internal static bool IsWrapperParameter(ParameterInfo parameter) =>
            parameter.ParameterType.IsByRef && parameter.ParameterType.GetElementType()?.Name == "ManagedSpanWrapper";

        /// <summary>A wrapper pointing at <paramref name="length"/> elements from <paramref name="begin"/>.</summary>
        internal static object Wrap(Shape shape, IntPtr begin, int length)
        {
            object wrapper = Activator.CreateInstance(shape.Type);
            shape.Begin.SetValue(wrapper, begin);
            shape.Length.SetValue(wrapper, length);
            return wrapper;
        }

        /// <summary>
        /// Runs <paramref name="call"/> with a wrapper over the characters of <paramref name="text"/>,
        /// pinned for the length of the call.
        /// </summary>
        internal static void WithText(Shape shape, string text, Action<object> call)
        {
            var pinned = GCHandle.Alloc(text, GCHandleType.Pinned);
            try { call(Wrap(shape, pinned.AddrOfPinnedObject(), text.Length)); }
            finally { pinned.Free(); }
        }

        /// <summary>
        /// Runs <paramref name="call"/> with the wrapper Unity's own bodies make for a string ARGUMENT:
        /// null → an empty wrapper, "" → a wrapper of length 0 at address 1
        /// (StringMarshaller.TryMarshalEmptyOrNullString), anything else → its characters, pinned for
        /// the call.
        /// </summary>
        internal static void WithStringArgument(Shape shape, string text, Action<object> call)
        {
            if (text == null) { call(Activator.CreateInstance(shape.Type)); return; }
            if (text.Length == 0) { call(Wrap(shape, (IntPtr)1, 0)); return; }
            WithText(shape, text, call);
        }

        private static readonly Dictionary<Type, MethodInfo> _unmarshal = new Dictionary<Type, MethodInfo>();

        /// <summary>
        /// <c>UnityEngine.Bindings.Unmarshal.UnmarshalUnityObject&lt;T&gt;</c> — what Unity's bodies turn
        /// an injected entry's returned handle into the object with. Null when the interop has none.
        /// </summary>
        internal static MethodInfo UnmarshalOf(Type unityType)
        {
            if (_unmarshal.TryGetValue(unityType, out var known)) return known;
            MethodInfo found = null;
            var unmarshalType = typeof(UnityEngine.Object).Assembly.GetType("UnityEngine.Bindings.Unmarshal");
            if (unmarshalType != null)
                foreach (var method in unmarshalType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (method.Name == "UnmarshalUnityObject" && method.IsGenericMethodDefinition
                        && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(IntPtr))
                    {
                        found = method.MakeGenericMethod(unityType);
                        break;
                    }
            _unmarshal[unityType] = found;
            return found;
        }

        private static readonly Dictionary<Type, MethodInfo> _marshal = new Dictionary<Type, MethodInfo>();

        /// <summary>
        /// <c>Object.MarshalledUnityObject.MarshalNotNull&lt;T&gt;</c> — the native pointer the
        /// injected entries that take an <c>IntPtr</c> self want, as the rebuilt bodies get it. Null
        /// when the interop has none.
        /// </summary>
        internal static MethodInfo NativePointerOf(Type unityType)
        {
            if (_marshal.TryGetValue(unityType, out var known)) return known;
            MethodInfo found = null;
            var marshaller = typeof(UnityEngine.Object).GetNestedType("MarshalledUnityObject", BindingFlags.Public | BindingFlags.NonPublic);
            if (marshaller != null)
                foreach (var method in marshaller.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (method.Name == "MarshalNotNull" && method.IsGenericMethodDefinition
                        && method.GetParameters().Length == 1 && method.ReturnType == typeof(IntPtr))
                    {
                        found = method.MakeGenericMethod(unityType);
                        break;
                    }
            _marshal[unityType] = found;
            return found;
        }
    }
}
