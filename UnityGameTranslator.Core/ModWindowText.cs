using System.Collections.Generic;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>Which side of the game's text an element of the mod's own window shows.</summary>
    public enum GameTextSide { Source, Target }

    /// <summary>
    /// The elements of the mod's own windows that show the GAME's text — its source text or its
    /// translation — as opposed to the interface (user, 2026-10-01: "les textes du jeu sont des textes
    /// du jeu donc pas ceux de l'interface"). Each side is drawn in its own font (source_text_font,
    /// target_text_font; the interface font when none), shaped with that font's derived copy, and a
    /// side its font cannot shape is said with its own sentence.
    /// Declared in the screen documents (`"gameText": "source" | "target"`) and recorded here by the
    /// screen builder; read by the presenter, the field presenter and whatever puts fonts on the
    /// window. Known by instance id — a field by its own id and by its text's. Main thread.
    /// </summary>
    public static class ModWindowText
    {
        private sealed class Entry
        {
            public GameTextSide Side;
            public UnityEngine.UI.Text Text;   // the component that draws it
        }

        private static readonly Dictionary<int, Entry> _byId = new Dictionary<int, Entry>();

        /// <summary>A label of the window that shows the game's text.</summary>
        public static void Mark(UnityEngine.UI.Text text, GameTextSide side)
        {
            if (text == null) return;
            _byId[text.GetInstanceID()] = new Entry { Side = side, Text = text };
        }

        /// <summary>A field of the window that holds the game's text: the field and the text that draws it.</summary>
        public static void Mark(Component field, UnityEngine.UI.Text drawnBy, GameTextSide side)
        {
            if (drawnBy == null) return;
            var entry = new Entry { Side = side, Text = drawnBy };
            _byId[drawnBy.GetInstanceID()] = entry;
            if (field != null) _byId[field.GetInstanceID()] = entry;
        }

        /// <summary>The side a component of the window shows, or null when it is the interface (or not ours).</summary>
        public static GameTextSide? SideOf(object component)
        {
            if (!(component is Object unityObject) || unityObject == null) return null;
            return _byId.TryGetValue(unityObject.GetInstanceID(), out var entry) ? entry.Side : (GameTextSide?)null;
        }

        /// <summary>Every live text of the window showing the game's text, with its side; the destroyed ones let go.</summary>
        public static List<KeyValuePair<UnityEngine.UI.Text, GameTextSide>> All()
        {
            var live = new List<KeyValuePair<UnityEngine.UI.Text, GameTextSide>>();
            var gone = new List<int>();
            var seen = new HashSet<int>();
            foreach (var kv in _byId)
            {
                if (kv.Value.Text == null) { gone.Add(kv.Key); continue; }
                if (seen.Add(kv.Value.Text.GetInstanceID()))
                    live.Add(new KeyValuePair<UnityEngine.UI.Text, GameTextSide>(kv.Value.Text, kv.Value.Side));
            }
            foreach (int id in gone) _byId.Remove(id);
            return live;
        }
    }
}
