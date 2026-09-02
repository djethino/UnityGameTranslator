using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UniverseLib.UI;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>What a tracked element is waiting for Apply to do.</summary>
    public enum PendingState { None, Added, Modified, Removed }

    /// <summary>
    /// One list per panel of everything Apply will act on, and the ONLY source of two things
    /// that must never disagree: the number on the Apply button, and the mark drawn on each
    /// element that is waiting. A field is registered once with the test that says whether it
    /// changed; <see cref="Refresh"/> then counts and draws in the same pass.
    ///
    /// The marks are the diff convention every program uses — a 3 px bar on the left edge of
    /// the element: green for something added, amber for a changed value, red for something
    /// that goes away on Apply; added and removed rows are tinted as well, since a whole row is
    /// what changes there, not one value in it. Nothing is written in words by the mark itself:
    /// a row that will be removed keeps its own "Removed on Apply" label and Undo button, which
    /// belong to the screen that built the row.
    ///
    /// ⚠ Registered elements can be destroyed and rebuilt (a list refreshed): a destroyed target
    /// is dropped at the next pass, and a rebuilt list re-registers under its group after
    /// <see cref="ClearGroup"/>. Refresh runs on every change and on the panels' per-frame
    /// poll, so each test must be a plain comparison, never a lookup that costs anything.
    /// </summary>
    public sealed class PendingMarks
    {
        private sealed class Entry
        {
            public GameObject Target;
            public Func<PendingState> State;
            public string Group;
            public PendingState Shown;   // what is drawn right now — redraw only on change
        }

        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>A value that is either as it was or changed: amber when <paramref name="changed"/> says so.</summary>
        public void Track(GameObject target, Func<bool> changed, string group = null)
        {
            if (target == null || changed == null) return;
            TrackState(target, () => changed() ? PendingState.Modified : PendingState.None, group);
        }

        /// <summary>A row or card that can be added, changed or removed as a whole.</summary>
        public void TrackState(GameObject target, Func<PendingState> state, string group = null)
        {
            if (target == null || state == null) return;
            for (int i = 0; i < _entries.Count; i++)
                if (_entries[i].Target == target) { _entries[i].State = state; _entries[i].Group = group; return; }
            _entries.Add(new Entry { Target = target, State = state, Group = group });
        }

        /// <summary>Forget every element registered under a group — before its list is rebuilt.</summary>
        public void ClearGroup(string group)
        {
            for (int i = _entries.Count - 1; i >= 0; i--)
                if (_entries[i].Group == group) _entries.RemoveAt(i);
        }

        /// <summary>
        /// Count what is waiting and draw it. The returned number is what the Apply button
        /// shows; every counted element carries a mark, and only those.
        /// </summary>
        public int Refresh()
        {
            int pending = 0;
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                var e = _entries[i];
                if (e.Target == null) { _entries.RemoveAt(i); continue; }

                PendingState state;
                try { state = e.State(); }
                catch { state = PendingState.None; }
                if (state != PendingState.None) pending++;
                if (state == e.Shown) continue;
                e.Shown = state;
                Draw(e.Target, state);
            }
            return pending;
        }

        /// <summary>Every mark off, without forgetting the elements — after an Apply, before the snapshot moves.</summary>
        public void ClearMarks()
        {
            foreach (var e in _entries)
            {
                if (e.Target == null || e.Shown == PendingState.None) continue;
                e.Shown = PendingState.None;
                Draw(e.Target, PendingState.None);
            }
        }

        private const string BarName = "PendingBar";
        private const string TintName = "PendingTint";

        private static void Draw(GameObject target, PendingState state)
        {
            var bar = ChildImage(target, BarName, state != PendingState.None);
            var tint = ChildImage(target, TintName, state == PendingState.Added || state == PendingState.Removed);
            if (bar != null)
            {
                bar.color = UIStyles.PendingBar(state);
                var rt = bar.rectTransform;
                rt.anchorMin = new Vector2(0f, 0f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 0.5f);
                rt.sizeDelta = new Vector2(UIStyles.PendingBarWidth, -4f);
                rt.anchoredPosition = Vector2.zero;
                bar.transform.SetAsLastSibling();
            }
            if (tint != null)
            {
                tint.color = UIStyles.PendingTint(state);
                var rt = tint.rectTransform;
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.sizeDelta = Vector2.zero;
                rt.anchoredPosition = Vector2.zero;
                // Behind the row's own content, over its background.
                tint.transform.SetAsFirstSibling();
            }
        }

        /// <summary>
        /// The mark's own child image, created when first needed and destroyed when no longer
        /// wanted. Out of the layout (a row is a layout group and would otherwise make room for
        /// it) and never a raycast target: the mark is paint, not a control.
        /// </summary>
        private static Image ChildImage(GameObject target, string name, bool wanted)
        {
            Transform existing = target.transform.Find(name);
            if (!wanted)
            {
                if (existing != null) UnityEngine.Object.Destroy(existing.gameObject);
                return null;
            }
            if (existing != null) return existing.GetComponent<Image>();

            var obj = UIFactory.CreateUIObject(name, target);
            var layout = obj.AddComponent<LayoutElement>();
            layout.ignoreLayout = true;
            var image = obj.AddComponent<Image>();
            image.raycastTarget = false;
            return image;
        }
    }
}
