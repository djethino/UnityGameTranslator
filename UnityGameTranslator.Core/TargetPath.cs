using System;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// How one step of a hierarchy path is named, when the thing being named may have no name.
    ///
    /// A path is what every "act on this particular text" feature matches against — exclusions,
    /// font rules by pattern. In uGUI every GameObject has a name and the question never comes up.
    /// In UI Toolkit most elements have none: they are told apart by their USS classes, and a path
    /// of empty segments would match nothing and mean nothing.
    ///
    /// 🔴 Pure by contract — no Unity, no reflection. The caller reads the three candidates off
    /// whatever it is holding; this only decides which one to write. That is what lets the checks
    /// project link this FILE and pin the order without a game.
    /// </summary>
    public static class TargetPath
    {
        /// <summary>
        /// The name to write for one step: what it is called, else what it looks like, else what
        /// it is.
        ///
        /// ⚠ The order is the point. A name is chosen by whoever built the interface and is the
        /// most specific thing available; a USS class is shared by every element of that kind, so
        /// it groups rather than identifies; a type name groups even harder. Reversing any two
        /// would make every path in a panel identical, and a pattern written against one of them
        /// would silently match the lot.
        /// </summary>
        /// <param name="name">The element's own name, usually empty in UI Toolkit.</param>
        /// <param name="firstClass">Its first USS class, when the build exposes them.</param>
        /// <param name="typeName">Its type's short name, the last resort.</param>
        public static string Segment(string name, string firstClass, string typeName)
        {
            if (!string.IsNullOrEmpty(name)) return name;
            if (!string.IsNullOrEmpty(firstClass)) return firstClass;
            if (!string.IsNullOrEmpty(typeName)) return StripInteropPrefix(typeName);

            // Never an empty segment: a path with a hole in it silently changes what a pattern
            // written against it matches.
            return "?";
        }

        /// <summary>
        /// The name a type really has, without the prefix the IL2CPP interop puts in front of it.
        ///
        /// 🔴 Without this the SAME element yields a different path on the two runtimes —
        /// `Il2CppLabel` against IL2CPP, `Label` against Mono — so a pattern a player wrote while
        /// playing one would quietly stop matching on the other. The project strips this prefix
        /// wherever it compares type names, and a path is a comparison like any other.
        /// </summary>
        public static string StripInteropPrefix(string typeName)
        {
            if (string.IsNullOrEmpty(typeName)) return typeName;
            return typeName.StartsWith("Il2Cpp", StringComparison.Ordinal)
                ? typeName.Substring(6)
                : typeName;
        }
    }
}
