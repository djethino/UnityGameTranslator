using System;
using System.IO;
using System.Linq;
using UnityGameTranslator.Checks.Shared;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// No catch in the mod — the Core and every loader adapter — swallows a failure without a word.
    /// The counter and its rule are common/tests/Shared/SilentCatches.cs, written once for the mod,
    /// the Manager and the library; this file only says which folders are the mod's.
    ///
    /// ⚠ The adapters are read too since 2026-10-07: the Core had been held at zero since
    /// 2026-09-27 while an adapter kept an empty catch around the very call that loads Unity's own
    /// assemblies on IL2CPP — a game whose libraries would not load left no trace of why.
    /// </summary>
    internal static class SilentCatchChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            SilentCatches.SelfCheck(check);
            string root = ModRoot();
            check(root != null, "the mod's sources are found", "this check reads them; without them it proves nothing");
            if (root == null) return;
            SilentCatches.NoneUnder(check, "no silent catch in the mod", Sources(root), root);
        }

        /// <summary>`dotnet run -- silent-list [file]`: where the silent catches are, one per line.</summary>
        internal static int List(string only)
        {
            string root = ModRoot();
            if (root == null) { Console.WriteLine("The mod's sources were not found."); return 1; }
            return SilentCatches.List(Sources(root), root, only);
        }

        // The repository root: the folder holding the Core.
        private static string ModRoot() => SilentCatches.FolderHolding("UnityGameTranslator.Core/TranslatorCore.cs");

        // The Core, and every adapter folder beside it (UnityGameTranslator-<Loader>).
        private static string[] Sources(string root) =>
            new[] { Path.Combine(root, "UnityGameTranslator.Core") }
                .Concat(Directory.GetDirectories(root, "UnityGameTranslator-*").OrderBy(d => d, StringComparer.Ordinal))
                .ToArray();
    }
}
