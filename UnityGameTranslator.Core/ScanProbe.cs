using System.Collections.Generic;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Where a text is lost between being SEEN by the scanner and being SENT for translation.
    ///
    /// 🔴 **Written from a field observation that reframed the whole search** (2026-09-10): on one
    /// game the mod replaces a tooltip's text without trouble when it already knows that text, and
    /// a new one is almost never queued. In the owner's words — *"il n'a pas de mal à remplacer,
    /// juste à envoyer en trad"*. So the component IS reached; the loss is in one of the decisions
    /// the scanner takes on the way, and none of them said anything.
    ///
    /// ⚠ **Bounded per component, never per session.** A session-wide budget is spent hundreds of
    /// texts before anybody walks to the place being investigated — you cannot teleport into a game
    /// at the spot you want to test. Each component gets its own small quota, so whatever is
    /// hovered gets its answer whenever it is hovered.
    ///
    /// ⚠ Debug only, and the caller checks that before asking: this sits on the path that runs a
    /// hundred thousand times every five seconds.
    /// </summary>
    internal static class ScanProbe
    {
        /// <summary>Lines per component. Enough to see a decision repeat, few enough to read.</summary>
        private const int PerComponent = 4;

        /// <summary>Components tracked at once, so a game spawning text without end cannot leak.</summary>
        private const int Components = 3000;

        private static readonly Dictionary<int, int> _said = new Dictionary<int, int>();

        /// <summary>Whether this component still has something to say. Cheap: one lookup.</summary>
        internal static bool Wants(int instanceId)
        {
            int said;
            if (_said.TryGetValue(instanceId, out said)) return said < PerComponent;
            return _said.Count < Components;
        }

        /// <summary>Name the decision taken about this component's current text.</summary>
        internal static void Say(int instanceId, string decision, string text)
        {
            int said;
            if (!_said.TryGetValue(instanceId, out said))
            {
                if (_said.Count >= Components) return;
                said = 0;
            }
            if (said >= PerComponent) return;
            _said[instanceId] = said + 1;

            string head = text != null && text.Length > 40 ? text.Substring(0, 40) + "…" : text;
            TranslatorCore.LogDebug($"[SCAN-DECISION] comp={instanceId} {decision} '{head}'");
        }

        /// <summary>A new scene: the ids are gone and so is what was said about them.</summary>
        internal static void Forget()
        {
            _said.Clear();
        }
    }
}
