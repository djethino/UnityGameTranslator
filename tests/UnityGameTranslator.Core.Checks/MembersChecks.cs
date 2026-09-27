using System;
using System.Reflection;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Engine/Members — a property looked up without the ambiguity trap. The premise is checked
    /// first: were GetProperty not to throw on a hiding type, the helper would be solving nothing.
    /// </summary>
    internal static class MembersChecks
    {
        private class Plain { public string Text => "plain"; }
        private class Hidden { public object Value => 1; }
        private class Hiding : Hidden { public new string Value => "derived"; }

        public static void Run(Action<bool, string, string> check)
        {
            var flags = BindingFlags.Public | BindingFlags.Instance;

            bool threw = false;
            try { typeof(Hiding).GetProperty("Value", flags); }
            catch (AmbiguousMatchException) { threw = true; }
            check(threw, "GetProperty throws on a type hiding an inherited property",
                "the premise: every such lookup used to sit in a catch { } that read it as \"absent\"");

            var found = Members.Property(typeof(Hiding), "Value", flags);
            check(found != null && found.DeclaringType == typeof(Hiding) && found.PropertyType == typeof(string),
                "Members.Property answers the most derived one instead",
                "the one the object actually exposes — what a caller reading it means");

            check(Members.Property(typeof(Plain), "Text", flags)?.Name == "Text",
                "and an ordinary property as GetProperty would",
                "nothing changes where there was no ambiguity");

            check(Members.Property(typeof(Plain), "Missing", flags) == null && Members.Property(null, "Text", flags) == null,
                "and null for a property that is not there, or no type",
                "an absence is an answer, never a throw");
        }
    }
}
