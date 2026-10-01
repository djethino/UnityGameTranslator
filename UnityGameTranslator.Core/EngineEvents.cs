using System;
using System.Reflection;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// A static event of an engine type (Canvas.willRenderCanvases, Font.textureRebuilt…) subscribed
    /// on both runtimes. The Core is compiled against Mono's UnityEngine, where the event takes a .NET
    /// delegate; under IL2CPP the same event takes an Il2Cpp delegate, which Il2CppInterop's
    /// DelegateSupport.ConvertDelegate makes from ours. On Mono the event's own delegate type is made
    /// from the handler's method when it is not the handler's type (a named delegate such as
    /// Canvas.WillRenderCanvases refuses a System.Action).
    /// ⚠ What Add returns is what Remove needs: keep it, and remove it on shutdown — a static
    /// subscription left live while the game tears down calls back into destroyed objects.
    /// </summary>
    internal static class EngineEvents
    {
        /// <summary>Subscribes; returns the delegate the event holds, or null when the type has no such event.</summary>
        internal static object Add(Type owner, string eventName, Delegate handler)
        {
            var add = Accessor(owner, eventName, "add_");
            if (add == null) return null;
            var given = ForEvent(add.GetParameters()[0].ParameterType, handler);
            if (given == null) return null;
            add.Invoke(null, new[] { given });
            return given;
        }

        /// <summary>Unsubscribes what <see cref="Add"/> returned.</summary>
        internal static void Remove(Type owner, string eventName, object given)
        {
            if (given == null) return;
            Accessor(owner, eventName, "remove_")?.Invoke(null, new[] { given });
        }

        private static MethodInfo Accessor(Type owner, string eventName, string prefix)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            var method = owner.GetMethod(prefix + eventName, flags);
            if (method != null) return method;
            var evt = owner.GetEvent(eventName, flags);
            return evt == null ? null : (prefix == "add_" ? evt.GetAddMethod(true) : evt.GetRemoveMethod(true));
        }

        private static object ForEvent(Type eventType, Delegate handler)
        {
            if (TranslatorCore.Adapter?.IsIL2CPP == true)
            {
                // Without the conversion the event refuses the delegate, and what the handler keeps
                // up to date never is: said.
                try
                {
                    var support = AssemblyTypes.Find("Il2CppInterop.Runtime.DelegateSupport");
                    var convert = support?.GetMethod("ConvertDelegate", BindingFlags.Public | BindingFlags.Static);
                    if (convert != null) return convert.MakeGenericMethod(eventType).Invoke(null, new object[] { handler });
                    TranslatorCore.LogWarning($"[EngineEvents] Il2CppInterop's DelegateSupport not found — {eventType.Name} not subscribed");
                }
                catch (Exception ex) { Faults.Say("EngineEvents.ForEvent convert", ex, eventType.Name); }
                return null;
            }
            if (eventType.IsInstanceOfType(handler)) return handler;
            return Delegate.CreateDelegate(eventType, handler.Target, handler.Method);
        }
    }
}
