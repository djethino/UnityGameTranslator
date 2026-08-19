using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>One piece of text on screen, whatever drew it.</summary>
    public sealed class TextTarget
    {
        /// <summary>The Component or the VisualElement behind it.</summary>
        public object Owner;

        /// <summary>Its id in the one space every per-target map uses.</summary>
        public long Id;

        /// <summary>Which framework drew it — shown to the player, so plain words.</summary>
        public string Engine;

        /// <summary>Where it sits, for patterns and for reading on screen.</summary>
        public string Path;

        /// <summary>What it says right now.</summary>
        public string Text;
    }

    /// <summary>
    /// Every piece of text this mod can reach, listed on demand.
    ///
    /// 🔴 **Why this exists.** Three screens enumerated text components, each on its own, and two of
    /// them named `UI.Text` and `TMP_Text` in their own code — so "Find by value" and the inspector
    /// were blind to NGUI, tk2d, TMProOld, TextMesh and UI Toolkit, on games the mod otherwise
    /// translates perfectly. Meanwhile the scanner keeps a proper registry, documented "for Find by
    /// Value", that nothing outside the scanner ever called. See analyse/text-targets-audit.md.
    ///
    /// ⚠ **Listing is not scanning, and the difference is the point.** TextMesh, tk2d and the
    /// alternate TMP are deliberately absent from the continuous scan: Harmony covers them and a
    /// FindAllObjectsOfType per type per cycle would buy nothing. That decision is still right —
    /// but it was read as "cannot be listed", which is a different sentence. This runs when
    /// somebody asks a question, never on a tick.
    ///
    /// ⚠ Deliberately NOT an interface with registered providers. The frameworks are known and
    /// closed, and the two families genuinely differ — one is found through Unity's object graph,
    /// the other by walking documents. A method per family, read top to bottom, says that; a
    /// provider list would hide it behind an order of registration.
    /// </summary>
    public static class TextTargets
    {
        /// <summary>
        /// Put a text into a target, through whichever setter owns it.
        ///
        /// 🔴 The one place that knows there is more than one way to write text. Every screen that
        /// applies an edit used TypeHelper.SetText directly, which is the uGUI answer and silently
        /// does nothing for a UI Toolkit element — the edit would be saved to the file and never
        /// appear on screen, which reads as the save having failed.
        ///
        /// ⚠ Through UIToolkitSupport for an element, never by setting its property here: that
        /// path carries the write-back guard, without which the setter patch reads our own write
        /// as the game's and translates the translation.
        /// </summary>
        public static void Write(object owner, string text)
        {
            if (owner == null || text == null) return;

            try
            {
                if (owner is Component) TypeHelper.SetText(owner, text);
                else UIToolkitSupport.WriteBack(owner, text);
            }
            catch { }
        }

        /// <summary>
        /// Everything reachable, with its text. Pass a filter to stop building strings for texts
        /// nobody asked about — on a large scene that is most of them.
        /// </summary>
        public static List<TextTarget> All(Func<string, bool> keep = null)
        {
            var found = new List<TextTarget>();
            var seen = new HashSet<long>();

            CollectRegistered(found, seen, keep);
            CollectPatchedOnly(found, seen, keep);
            CollectUIToolkit(found, seen, keep);

            return found;
        }

        /// <summary>
        /// The types the scanner already follows: TMP, UI.Text, and every generic type detected in
        /// this game (NGUI, SuperTextMesh, DaikonForge…).
        /// </summary>
        private static void CollectRegistered(List<TextTarget> found, HashSet<long> seen,
                                              Func<string, bool> keep)
        {
            foreach (var type in TranslatorScanner.RegisteredTypes)
            {
                if (type?.ComponentType == null) continue;

                var objects = TypeHelper.FindAllObjectsOfType(type.ComponentType);
                if (objects == null) continue;

                foreach (var obj in objects)
                    Consider(found, seen, keep, obj,
                             type.FontTypeName ?? type.Category,
                             () => TranslatorScanner.GetTextForTypePublic(obj, type));
            }
        }

        /// <summary>
        /// The types Harmony patches without the scanner following them. Their text is read through
        /// TypeHelper, which already knows how to ask each of them.
        /// </summary>
        private static void CollectPatchedOnly(List<TextTarget> found, HashSet<long> seen,
                                               Func<string, bool> keep)
        {
            Collect(found, seen, keep, TypeHelper.TextMeshType, "TextMesh");
            Collect(found, seen, keep, TranslatorPatches.Tk2dType, "tk2d");

            foreach (var alt in TranslatorPatches.AlternateTmpTypes)
                Collect(found, seen, keep, alt, "TMP (alt)");
        }

        private static void Collect(List<TextTarget> found, HashSet<long> seen,
                                    Func<string, bool> keep, Type type, string engine)
        {
            if (type == null) return;

            var objects = TypeHelper.FindAllObjectsOfType(type);
            if (objects == null) return;

            foreach (var obj in objects)
                Consider(found, seen, keep, obj, engine, () => TypeHelper.GetText(obj));
        }

        /// <summary>
        /// UI Toolkit, which no FindObjects call can return — see UIToolkitSupport for why the way
        /// in is the documents rather than the object graph.
        /// </summary>
        private static void CollectUIToolkit(List<TextTarget> found, HashSet<long> seen,
                                             Func<string, bool> keep)
        {
            foreach (var target in UIToolkitSupport.Targets(keep))
            {
                if (!seen.Add(target.Id)) continue;
                found.Add(target);
            }
        }

        /// <summary>
        /// One candidate: read its text, drop it if the caller does not want it, and skip anything
        /// already listed under another type — TMP_Text and its subclasses overlap by design.
        /// </summary>
        private static void Consider(List<TextTarget> found, HashSet<long> seen,
                                     Func<string, bool> keep, object obj, string engine,
                                     Func<string> readText)
        {
            if (obj == null) return;

            try
            {
                long id = TypeHelper.GetInstanceID(obj);
                if (id == -1 || !seen.Add(id)) return;

                string text = readText();
                if (string.IsNullOrEmpty(text)) return;
                if (keep != null && !keep(text)) return;

                var comp = obj as Component;
                if (comp == null) comp = TypeHelper.Il2CppCast(obj, typeof(Component)) as Component;
                if (comp == null || comp.gameObject == null) return;

                // Our own interface is not part of the game's text.
                if (TranslatorCore.ShouldSkipTranslation(comp)) return;

                found.Add(new TextTarget
                {
                    Owner = comp,
                    Id = id,
                    Engine = engine,
                    Path = TranslatorCore.GetGameObjectPath(comp.gameObject),
                    Text = text,
                });
            }
            catch { }
        }
    }
}
