using System;
using System.Collections.Generic;
using System.IO;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// A thread the mod starts has to be made known to the runtime it runs on.
    ///
    /// 🔴 **On IL2CPP this is not housekeeping.** The Boehm collector knows only the threads the
    /// engine made; collecting while one of ours holds a reference kills the process with "fatal
    /// error in GC: Collecting from unknown thread" — a native abort, with no exception to catch
    /// and no line in the log, whenever the collector happens to run.
    ///
    /// 🔴 **And the compiler cannot see the failure this guards.** Every adapter satisfying the
    /// interface is enough to build; one implementing it as an empty body compiles, ships, and
    /// kills IL2CPP games at random. So what has to be checked is not that the method EXISTS — the
    /// compiler does that — but that the two runtimes which need it actually do the work, and that
    /// the three that do not have nothing to do.
    ///
    /// ⚠ Lexical, and it has to be: this needs a game, an IL2CPP runtime and a collection to
    /// happen. What is checkable without running anything is that each side still holds up its
    /// half.
    ///
    /// ⚠ It also pins the Core's side of the move: the reflection that walked every loaded
    /// assembly for a type name is gone, and must not come back. It compiled on a runtime where it
    /// meant nothing and would have failed silently on the one where it mattered.
    /// </summary>
    internal static class WorkerThreadChecks
    {
        /// <summary>The call that makes a thread known to the IL2CPP collector.</summary>
        private const string Attach = "il2cpp_thread_attach";

        public static void Run(Action<bool, string, string> check)
        {
            var adapters = new (string What, string File, bool Il2Cpp)[]
            {
                ("BepInEx 5",            "UnityGameTranslator-BepInEx5/Plugin.cs",         false),
                ("BepInEx 6 Mono",       "UnityGameTranslator-BepInEx6-Mono/Plugin.cs",    false),
                ("MelonLoader Mono",     "UnityGameTranslator-MelonLoader-Mono/Mod.cs",    false),
                ("BepInEx 6 IL2CPP",     "UnityGameTranslator-BepInEx6-IL2CPP/Plugin.cs",  true),
                ("MelonLoader IL2CPP",   "UnityGameTranslator-MelonLoader-IL2CPP/Mod.cs",  true),
            };

            int il2cpp = 0;
            foreach (var a in adapters) if (a.Il2Cpp) il2cpp++;

            check(il2cpp == 2 && adapters.Length == 5,
                $"the {adapters.Length} adapters are listed, {il2cpp} of them on IL2CPP",
                "an adapter missing from this list is an adapter nothing holds to the rule");

            foreach (var a in adapters)
            {
                string path = Find(a.File.Split('/'));

                check(path != null,
                    $"{a.What}'s adapter is found",
                    "this check reads it; without it, it proves nothing");
                if (path == null) continue;

                string text = File.ReadAllText(path);

                check(text.Contains("OnWorkerThreadStarted", StringComparison.Ordinal),
                    $"{a.What} answers for a thread starting",
                    "the compiler already requires this; it is read here so the two halves below are read from the same file");

                bool attaches = text.Contains(Attach, StringComparison.Ordinal);

                if (a.Il2Cpp)
                {
                    check(attaches,
                        $"🔴 {a.What} makes the thread known to the collector",
                        "an empty body compiles and ships, and the game it kills says nothing a log can show");
                }
                else
                {
                    check(!attaches,
                        $"{a.What} has nothing to do, and does nothing",
                        "Mono's collector already knows every .NET thread; calling into Il2CppInterop from here would not even resolve");
                }
            }

            // ── The Core's half: it stopped guessing. ──
            string core = Find("UnityGameTranslator.Core", "TranslatorCore.cs");

            check(core != null,
                "TranslatorCore's source is found",
                "this check reads it; without it, it proves nothing");
            if (core == null) return;

            string coreText = File.ReadAllText(core);

            check(!coreText.Contains(Attach, StringComparison.Ordinal),
                "and the Core no longer reaches for the runtime itself",
                "it walked every loaded assembly for a type name and reflected two methods out of it — a guess that compiles where it means nothing and fails in silence where it matters");

            string loop = BodyOf(coreText, "private static void TranslationWorkerLoop()");

            check(loop != null,
                "the worker loop is still there under its own name",
                "renamed, the check must say so rather than pass on an empty comparison");
            if (loop == null) return;

            check(loop.Contains("Adapter?.OnWorkerThreadStarted()", StringComparison.Ordinal),
                "it asks the adapter instead, before doing anything else",
                "asked after the first allocation is asked too late: the collector can run in between");
        }

        /// <summary>The body of a method, by counting braces from its signature.</summary>
        private static string BodyOf(string text, string signature)
        {
            int start = text.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return null;

            int open = text.IndexOf('{', start + signature.Length);
            if (open < 0) return null;

            int depth = 0;
            for (int i = open; i < text.Length; i++)
            {
                if (text[i] == '{') depth++;
                else if (text[i] == '}')
                {
                    depth--;
                    if (depth == 0) return text.Substring(open, i - open + 1);
                }
            }
            return null;
        }

        private static string Find(params string[] parts)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var segments = new List<string> { dir.FullName };
                segments.AddRange(parts);
                string candidate = Path.Combine(segments.ToArray());
                if (File.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return null;
        }
    }
}
