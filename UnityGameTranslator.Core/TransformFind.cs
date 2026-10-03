using System;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Transform.Find — a child by name or path — on every runtime.
    ///
    /// 🔴 Unity 2023.1+ under IL2CPP: Find hands the path to native code as a <c>ManagedSpanWrapper</c>,
    /// and a game whose code never calls it has it stripped; the interop then rebuilds its body around
    /// an Il2CppSystem.ReadOnlySpan the game's runtime cannot use, and every call throws
    /// MissingMethodException (GetPinnableReference) — the mod's window was never built (bench player
    /// 6000.3.6, 2026-10-03). Same trap and same way out as FontAtlas and LoadImage (SpanWrappers):
    /// Unity's own body, line for line — the path pinned in a wrapper (TryMarshalEmptyOrNullString's
    /// rule for an empty one), the native entry <c>FindRelativeTransformWithPath_Injected(IntPtr,
    /// ref ManagedSpanWrapper, bool)</c>, and its answer turned into the Transform by the engine's own
    /// <c>Unmarshal.UnmarshalUnityObject&lt;Transform&gt;</c>. Where the entry is not reachable (Mono,
    /// IL2CPP before 2023.1) the public method is the right one.
    ///
    /// Every lookup of the mod and of its UniverseLib goes through <see cref="Path"/>
    /// (UnityHelpers.FindChild is pointed here at start).
    /// </summary>
    internal static class TransformFind
    {
        private static bool _resolved;
        private static MethodInfo _injected, _marshalTransform, _unmarshal;
        private static SpanWrappers.Shape _wrapper;

        /// <summary>The child at this name or path under <paramref name="transform"/>, or null.</summary>
        internal static Transform Path(Transform transform, string path)
        {
            if (path == null) throw new ArgumentNullException(nameof(path), "Name cannot be null");
            Resolve();
            if (_injected == null) return transform.Find(path);

            var self = (IntPtr)_marshalTransform.Invoke(null, new object[] { transform });
            if (self == IntPtr.Zero) throw new NullReferenceException("Transform.Find on a destroyed Transform");
            IntPtr handle = IntPtr.Zero;
            if (path.Length == 0)
                handle = (IntPtr)_injected.Invoke(null, new[] { (object)self, SpanWrappers.Wrap(_wrapper, (IntPtr)1, 0), false });
            else
                SpanWrappers.WithText(_wrapper, path, wrapper => handle = (IntPtr)_injected.Invoke(null, new[] { (object)self, wrapper, false }));
            return _unmarshal.Invoke(null, new object[] { handle }) as Transform;
        }

        /// <summary>
        /// The 2023.1+ IL2CPP shape, all or nothing: the injected entry made public by the interop,
        /// its wrapper, the Transform's native pointer and the engine's unmarshaller.
        /// </summary>
        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;

            MethodInfo injected = null;
            foreach (var method in typeof(Transform).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.Name != "FindRelativeTransformWithPath_Injected") continue;
                var p = method.GetParameters();
                if (p.Length == 3 && p[0].ParameterType == typeof(IntPtr) && SpanWrappers.IsWrapperParameter(p[1])
                    && p[2].ParameterType == typeof(bool) && method.ReturnType == typeof(IntPtr))
                {
                    injected = method;
                    break;
                }
            }
            if (injected == null) return;

            var wrapper = SpanWrappers.Of(injected, 1);
            var marshal = SpanWrappers.NativePointerOf(typeof(Transform));
            MethodInfo unmarshal = null;
            var unmarshalType = typeof(Transform).Assembly.GetType("UnityEngine.Bindings.Unmarshal");
            if (unmarshalType != null)
                foreach (var method in unmarshalType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (method.Name == "UnmarshalUnityObject" && method.IsGenericMethodDefinition
                        && method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(IntPtr))
                    {
                        unmarshal = method.MakeGenericMethod(typeof(Transform));
                        break;
                    }
            if (wrapper == null || marshal == null || unmarshal == null)
            {
                TranslatorCore.LogWarning($"[TransformFind] FindRelativeTransformWithPath_Injected found without its parts (wrapper={wrapper != null}, marshal={marshal != null}, unmarshal={unmarshal != null}) — children are found through Transform.Find");
                return;
            }

            _injected = injected;
            _marshalTransform = marshal;
            _unmarshal = unmarshal;
            _wrapper = wrapper;
            TranslatorCore.LogInfo("[TransformFind] Found Transform.FindRelativeTransformWithPath_Injected (Unity 2023.1+ IL2CPP) — Transform.Find is not used");
        }
    }
}
