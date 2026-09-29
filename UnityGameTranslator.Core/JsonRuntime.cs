using System;
using System.Reflection;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Makes the JSON library work on a runtime that cannot build methods while running.
    ///
    /// 🔴 **Why** (issue #29, 2026-09-29): the Mono adapters carry Newtonsoft.Json's .NET Framework
    /// build, which creates and fills objects through methods it emits at run time
    /// (<c>DynamicReflectionDelegateFactory</c>). Some runtimes refuse that — measured with MelonLoader
    /// 0.7.3 on a Unity 2019.4 game in the .NET Standard profile, where every object read or written
    /// threw <c>PlatformNotSupportedException</c>: the config was never read (the setup wizard came back
    /// at every launch) nor written, and the font cache was never saved.
    ///
    /// The runtime is ASKED, not guessed: one small object is read through the library at start. When that is refused, the
    /// library is told what it cannot find out alone — its own switch, <c>JsonTypeReflector._dynamicCodeGeneration</c>
    /// — and it falls back on its reflection path (<c>LateBoundReflectionDelegateFactory</c>), slower
    /// per object, with the same results. The switch exists in every build of the library; a version
    /// without it is said in the log rather than silently left broken (JsonRuntimeChecks).
    ///
    /// Pure: no Unity. Linked by tests/UnityGameTranslator.Core.Checks.
    /// </summary>
    public static class JsonRuntime
    {
        /// <param name="info">Where to say what was found.</param>
        /// <param name="warn">Where to say the library could not be adapted.</param>
        public static void AdaptToRuntime(Action<string> info, Action<string> warn)
        {
            string refused = Refused();
            if (refused == null) return;

            info?.Invoke($"[JSON] This runtime refuses to emit methods ({refused}): files are read and written through reflection instead.");
            var @switch = Switch();
            if (@switch == null)
            {
                warn?.Invoke("[JSON] This version of Newtonsoft.Json has no switch to avoid emitting methods: reading and writing the config and the translation will fail.");
                return;
            }
            @switch.SetValue(null, (bool?)false);
            string still = Refused();
            if (still != null) warn?.Invoke($"[JSON] Still refused through reflection ({still}): reading and writing the config and the translation will fail.");
        }

        /// <summary>The library's own "may I emit methods" answer (a <c>bool?</c>, null until it asks itself), or null when this version has none.</summary>
        public static FieldInfo Switch()
        {
            var reflector = typeof(Newtonsoft.Json.JsonConvert).Assembly.GetType("Newtonsoft.Json.Serialization.JsonTypeReflector");
            var field = reflector?.GetField("_dynamicCodeGeneration", BindingFlags.NonPublic | BindingFlags.Static);
            return field != null && field.FieldType == typeof(bool?) ? field : null;
        }

        private sealed class Probe
        {
            public int Value { get; set; }
        }

        /// <summary>
        /// Why the library cannot read an object here, or null when it can — asked of the library
        /// itself, the very operation that failed. A refused contract is not kept by the library, so
        /// the next ask is judged afresh.
        /// </summary>
        private static string Refused()
        {
            try
            {
                var probe = Newtonsoft.Json.JsonConvert.DeserializeObject<Probe>("{\"Value\":1}");
                return probe != null && probe.Value == 1 ? null : "an object read back wrong";
            }
            // PlatformNotSupportedException is one: the refusal measured, and the answer asked for.
            catch (NotSupportedException e) { return $"{e.GetType().Name}: {e.Message}"; }
        }
    }
}
