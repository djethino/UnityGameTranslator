using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// The types an assembly can give — every one of them that loads.
    ///
    /// 🔴 **`GetTypes()` refuses the whole assembly for one type it cannot load**, and a game's
    /// assemblies routinely hold such a type (a reference to a module the build stripped, a
    /// platform library absent here). The refusal carries every type that DID load; the twelve
    /// scans that wrapped it in `catch { }` threw them all away, so a game's whole assembly went
    /// unread without a word. This keeps them, and says what was missing, once.
    /// </summary>
    public static class AssemblyTypes
    {
        /// <summary>
        /// The type of that exact full name, in whichever loaded assembly holds it; null when none does.
        ///
        /// ⚠ `Assembly.GetType(name)` answers null for a name it does not hold, but throws when the
        /// assembly needs a file this game does not have — which no condition can tell beforehand.
        /// That assembly is then skipped, and said.
        /// </summary>
        /// <param name="skip">An assembly never to answer from — the mod's own, for a caller reading
        /// the GAME's values (the mod holds things in memory the game does not).</param>
        public static Type Find(string fullName, Assembly skip = null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly == skip) continue;
                try
                {
                    var type = assembly.GetType(fullName);
                    if (type != null) return type;
                }
                catch (Exception ex)
                {
                    Faults.Say("AssemblyTypes.Find", ex, $"{assembly.GetName().Name} skipped while looking for {fullName}");
                }
            }
            return null;
        }

        // The types of each assembly read so far. A loaded assembly's types never change, and the
        // scans ask again and again: an assembly with a type that cannot load threw a
        // ReflectionTypeLoadException at every scan (measured: over a hundred in one session of a
        // game), each said again as a new fault. Read once, said once.
        private static readonly Dictionary<Assembly, Type[]> _read = new Dictionary<Assembly, Type[]>();
        private static readonly object _readLock = new object();

        public static Type[] Of(Assembly assembly)
        {
            // Made at run time (Harmony's patch stubs, emitted helpers): never a game's types, and
            // a runtime may refuse to list them at all.
            if (assembly.IsDynamic) return Type.EmptyTypes;

            lock (_readLock)
            {
                if (_read.TryGetValue(assembly, out var known)) return known;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loaded = new List<Type>();
                foreach (var type in ex.Types)
                    if (type != null) loaded.Add(type);

                Exception first = ex.LoaderExceptions != null && ex.LoaderExceptions.Length > 0 ? ex.LoaderExceptions[0] : ex;
                Faults.Say("AssemblyTypes.Of partial load (" + assembly.GetName().Name + ")", first,
                    $"{ex.Types.Length - loaded.Count} type(s) could not load, the other {loaded.Count} are read");
                types = loaded.ToArray();
            }

            lock (_readLock) { _read[assembly] = types; }
            return types;
        }
    }
}
