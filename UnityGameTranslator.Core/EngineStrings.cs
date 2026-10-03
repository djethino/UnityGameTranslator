using System;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The engine calls that hand a STRING to native code, on every runtime: naming an object,
    /// making a named GameObject, GameObject.Find, opening a URL, the clipboard (Transform.Find has
    /// its own door: <see cref="TransformFind"/>).
    ///
    /// 🔴 Unity 2023.1+ under IL2CPP: those methods pass the string as a <c>ManagedSpanWrapper</c>,
    /// and a game whose code never calls one has it stripped; the interop rebuilds its body around an
    /// Il2CppSystem.ReadOnlySpan the runtime cannot use, and every call throws MissingMethodException
    /// (GetPinnableReference) — a known defect of the interop, reported by other mods on the same
    /// games (pieges-projet §2). Each method here does what Unity's own body does, the string pinned
    /// in a wrapper by <see cref="SpanWrappers"/> (null and empty as Unity's
    /// TryMarshalEmptyOrNullString passes them), to the native entry the interop makes public
    /// (<c>X_Injected</c>). Where that entry is not reachable — Mono, IL2CPP before 2023.1, a module
    /// the game has not — the public member is the right one, and is used.
    /// </summary>
    internal static class EngineStrings
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;

        /// <summary>A static injected entry, resolved once, with the index of its wrapper parameter; null when absent.</summary>
        private sealed class Entry
        {
            internal MethodInfo Method;
            internal SpanWrappers.Shape Wrapper;
            internal int WrapperIndex;
        }

        private static Entry Find(Type type, string name, int parameters, int wrapperIndex)
        {
            if (type == null) return null;
            foreach (var method in type.GetMethods(PublicStatic))
            {
                if (method.Name != name || method.GetParameters().Length != parameters) continue;
                var shape = SpanWrappers.Of(method, wrapperIndex);
                if (shape == null) continue;
                return new Entry { Method = method, Wrapper = shape, WrapperIndex = wrapperIndex };
            }
            return null;
        }

        /// <summary>Calls the entry with <paramref name="args"/>, the text pinned at its wrapper's place; returns what it returns.</summary>
        private static object Call(Entry entry, string text, object[] args)
        {
            object result = null;
            SpanWrappers.WithStringArgument(entry.Wrapper, text, wrapper =>
            {
                args[entry.WrapperIndex] = wrapper;
                result = entry.Method.Invoke(null, args);
            });
            return result;
        }

        private static bool _resolved;
        private static Entry _setName, _gameObjectFind, _openUrl, _setClipboard;
        private static MethodInfo _marshalObject, _unmarshalGameObject;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            _marshalObject = SpanWrappers.NativePointerOf(typeof(UnityEngine.Object));
            if (_marshalObject != null)
                _setName = Find(typeof(UnityEngine.Object), "SetName_Injected", 2, 1);
            _gameObjectFind = Find(typeof(GameObject), "Find_Injected", 1, 0);
            _unmarshalGameObject = SpanWrappers.UnmarshalOf(typeof(GameObject));
            if (_unmarshalGameObject == null) _gameObjectFind = null;
            _openUrl = Find(typeof(Application), "OpenURL_Injected", 1, 0);
            _setClipboard = Find(AssemblyTypes.Find("UnityEngine.GUIUtility"), "set_systemCopyBuffer_Injected", 1, 0);
            if (_setName != null || _gameObjectFind != null || _openUrl != null || _setClipboard != null)
                TranslatorCore.LogInfo($"[EngineStrings] Unity 2023.1+ IL2CPP: strings handed to the engine through its native entries (name={_setName != null}, GameObject.Find={_gameObjectFind != null}, OpenURL={_openUrl != null}, clipboard={_setClipboard != null})");
        }

        /// <summary>Object.name = value.</summary>
        internal static void SetName(UnityEngine.Object target, string value)
        {
            Resolve();
            if (_setName == null) { target.name = value; return; }
            var self = (IntPtr)_marshalObject.Invoke(null, new object[] { target });
            if (self == IntPtr.Zero) throw new NullReferenceException("name set on a destroyed object");
            Call(_setName, value, new object[] { self, null });
        }

        /// <summary>new GameObject(name): the object made without a name (no string reaches the engine), then named.</summary>
        internal static GameObject NewGameObject(string name)
        {
            Resolve();
            if (_setName == null) return new GameObject(name);
            var made = new GameObject();
            SetName(made, name);
            return made;
        }

        /// <summary>GameObject.Find(name).</summary>
        internal static GameObject FindGameObject(string name)
        {
            Resolve();
            if (_gameObjectFind == null) return GameObject.Find(name);
            var handle = (IntPtr)Call(_gameObjectFind, name, new object[] { null });
            return _unmarshalGameObject.Invoke(null, new object[] { handle }) as GameObject;
        }

        /// <summary>Application.OpenURL(url).</summary>
        internal static void OpenUrl(string url)
        {
            Resolve();
            if (_openUrl == null) { Application.OpenURL(url); return; }
            Call(_openUrl, url, new object[] { null });
        }

        /// <summary>GUIUtility.systemCopyBuffer = text.</summary>
        internal static void SetClipboard(string text)
        {
            Resolve();
            if (_setClipboard == null) { GUIUtility.systemCopyBuffer = text; return; }
            Call(_setClipboard, text, new object[] { null });
        }
    }
}
