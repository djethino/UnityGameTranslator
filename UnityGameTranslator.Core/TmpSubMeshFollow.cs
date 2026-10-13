using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// 🔴 **The objects TMP draws a text's other fonts in follow the text's own object** (issue #33,
    /// follow-up of 2026-10-12).
    ///
    /// TMP draws in a text's own mesh only the letters of the font it wears; every letter of another
    /// font — a fallback's — goes to a child object, a TMP SubMesh with its own renderer and material.
    /// A game hides, fades or moves a text through the text's OWN object: its layer, its renderer,
    /// its material. TMP hands the child the parent's layer once, when it makes it, and the
    /// renderer's state only when the text is laid out again — so a text the game hid stayed on
    /// screen, and one shown again stayed blank (bench `tmphide`; Blue Prince's HUD and blueprint,
    /// whose machines move layers and swap materials by the thousand).
    ///
    /// A component the mod replaced the font of draws EVERYTHING through such children (its head
    /// holds no letter: FontManager.BackupHead). And a text nobody replaced the font of gets one
    /// the day its translation holds a letter the game's font lacks: the game's own fallback draws
    /// it, in a child — the "é" of a room's name stayed alone on Blue Prince's pause menu, in a
    /// font with no replacement set (2026-10-13). So for EVERY text that has such children, they
    /// are set to what their parent is, every frame, just before the draw: nothing tells when a
    /// game writes a layer or a renderer, so the state is compared, and written only where it
    /// differs. What differs is said once per kind (DiagnosticOnce): that is how a game's way of
    /// hiding its texts gets known.
    ///
    /// A text on a canvas (TextMeshProUGUI) is drawn by its canvas renderer, and so are its
    /// children (TMP SubMesh UI): what is followed there is the canvas renderer's colour — its
    /// alpha is the fade — and its culling, and the material. TMP itself carries to the children
    /// a component switched off, a fade asked through CrossFadeAlpha and the text's colour; a
    /// canvas renderer written directly, it does not (bench `tmphide`, canvas cases, 2026-10-13:
    /// the game alone leaves the child drawn too).
    ///
    /// ⚠ What a game does in code to the letters themselves (a reveal that moves the vertices of the
    /// first mesh alone) is no state to compare, and is not followed.
    /// </summary>
    internal static class TmpSubMeshFollow
    {
        private sealed class Child
        {
            public GameObject Object;
            public Renderer Renderer;
            /// <summary>On a canvas: what draws it, and the TMP SubMesh UI whose material it is.</summary>
            public CanvasRenderer Canvas;
            public Component Sub;
            /// <summary>The material TMP gave it, and the copy of ours put in its place once the parent's changed.</summary>
            public Material Given, Own;
            /// <summary>Set to its parent's state at the last frame, and how many frames running it was found undone.</summary>
            public bool Wrote;
            public int Undone;
        }

        private sealed class Followed
        {
            public Component Text;
            public GameObject Object;
            public Renderer Renderer;
            /// <summary>On a canvas: what draws the text itself.</summary>
            public CanvasRenderer Canvas;
            public bool Culled;
            public Color Colour;
            public readonly List<Child> Children = new List<Child>();
            /// <summary>The parent's material as it was when the children were last made to match it.</summary>
            public int MaterialId, MaterialCrc;
            /// <summary>The parent's own state at the last frame: unchanged, a child found off again was put there by the game.</summary>
            public bool Seen, Shown;
            public int Layer, SortingLayer, SortingOrder;
        }

        /// <summary>
        /// 🔴 **A child the game drives itself is left to the game.** Set to its parent's state and
        /// found off again at the very next frame, the parent unchanged, this many frames running:
        /// the game writes that child on purpose, every frame, and following it would undo what
        /// the game does — the mod's rule for a font the game keeps putting back
        /// (FontManager.MaxFontRevertsPerComponent). The text is followed no more, and it is said.
        /// </summary>
        private const int MaxUndone = 3;
        private static readonly HashSet<int> _leftToTheGame = ForgetByNumber.Tracked(new HashSet<int>());

        private static readonly Dictionary<int, Followed> _followed = ForgetByNumber.Tracked(new Dictionary<int, Followed>());
        private static readonly List<int> _gone = new List<int>();
        private static FieldInfo _subObjectsField;
        private static PropertyInfo _subObjectsProperty;
        private static Type _subObjectsOf;

        /// <summary>
        /// After TMP laid a text out (TmpMeshDone): its children as they now are. TMP has just set
        /// them from the parent — its renderer's state, a material made from the parent's — so the
        /// pair starts in step; a text with no child left is followed no more.
        /// </summary>
        internal static void Note(object text)
        {
            int id = TypeHelper.GetInstanceID(text);
            if (id == -1 || _leftToTheGame.Contains(id)) return;
            if (!(text is Component component)) return;
            // A 3D text is drawn by its renderer, a canvas text by its canvas renderer.
            var renderer = component.GetComponent<Renderer>();
            var canvas = renderer == null ? component.GetComponent<CanvasRenderer>() : null;
            if (renderer == null && canvas == null) { _followed.Remove(id); return; }

            Followed entry = null;
            foreach (var sub in SubObjects(text))
            {
                if (!(sub is Component subComponent) || subComponent == null) continue;
                Child child;
                if (renderer != null)
                {
                    var subRenderer = subComponent.GetComponent<Renderer>();
                    if (subRenderer == null) continue;
                    child = new Child { Object = subComponent.gameObject, Renderer = subRenderer, Given = subRenderer.sharedMaterial };
                }
                else
                {
                    var subCanvas = subComponent.GetComponent<CanvasRenderer>();
                    if (subCanvas == null) continue;
                    child = new Child { Object = subComponent.gameObject, Canvas = subCanvas, Sub = subComponent, Given = MaterialOf(subComponent, SubMaterial) };
                }
                if (entry == null) entry = new Followed { Text = component, Object = component.gameObject, Renderer = renderer, Canvas = canvas };
                entry.Children.Add(child);
            }
            if (entry == null) { _followed.Remove(id); return; }
            var material = renderer != null ? renderer.sharedMaterial : MaterialOf(component, TextMaterial);
            entry.MaterialId = material != null ? ObjectNumbers.Of(material) : 0;
            entry.MaterialCrc = material != null ? LookOf(material) : 0;
            _followed[id] = entry;
        }

        /// <summary>Every frame, just before the draw (FontManager.OnWillRenderCanvases).</summary>
        internal static void Tick()
        {
            if (_followed.Count == 0) return;
            _gone.Clear();
            foreach (var pair in _followed)
            {
                var entry = pair.Value;
                // Destroyed (a scene left): Unity's own test, through the overloaded comparison.
                if (entry.Text == null || entry.Object == null || (entry.Renderer == null && entry.Canvas == null)) { _gone.Add(pair.Key); continue; }
                try
                {
                    // A text nobody draws: its children are not drawn either, whatever they hold.
                    if (!entry.Object.activeInHierarchy) continue;
                    if (entry.Renderer != null ? Follow(entry) : FollowCanvas(entry)) continue;
                    _gone.Add(pair.Key);
                    _leftToTheGame.Add(pair.Key);
                    TranslatorCore.LogInfo($"[SubMeshFollow] The game keeps setting a child object of {TranslatorCore.GetGameObjectPath(entry.Object)} "
                                           + "apart from its text. It is left to the game from now on.");
                }
                // One text's children: said, and that text left alone rather than failing every frame.
                catch (Exception ex) { Faults.Say("TmpSubMeshFollow.Tick", ex, entry.Text.GetType().Name); _gone.Add(pair.Key); }
            }
            for (int i = 0; i < _gone.Count; i++) _followed.Remove(_gone[i]);
        }

        /// <summary>False when the game drives a child of this text itself (<see cref="MaxUndone"/>).</summary>
        private static bool Follow(Followed entry)
        {
            int layer = entry.Object.layer;
            bool shown = entry.Renderer.enabled;
            int sortingLayer = entry.Renderer.sortingLayerID, sortingOrder = entry.Renderer.sortingOrder;
            var material = entry.Renderer.sharedMaterial;
            int materialId = material != null ? ObjectNumbers.Of(material) : 0;
            int materialCrc = material != null ? LookOf(material) : 0;
            bool materialChanged = materialId != entry.MaterialId || materialCrc != entry.MaterialCrc;
            bool parentSame = entry.Seen && entry.Layer == layer && entry.Shown == shown
                              && entry.SortingLayer == sortingLayer && entry.SortingOrder == sortingOrder;
            entry.Seen = true; entry.Layer = layer; entry.Shown = shown;
            entry.SortingLayer = sortingLayer; entry.SortingOrder = sortingOrder;

            foreach (var child in entry.Children)
            {
                if (child.Object == null || child.Renderer == null) continue;
                bool off = false;
                if (child.Object.layer != layer) { child.Object.layer = layer; Say("layer"); off = true; }
                if (child.Renderer.enabled != shown) { child.Renderer.enabled = shown; Say("renderer"); off = true; }
                if (child.Renderer.sortingLayerID != sortingLayer) { child.Renderer.sortingLayerID = sortingLayer; Say("sorting layer"); off = true; }
                if (child.Renderer.sortingOrder != sortingOrder) { child.Renderer.sortingOrder = sortingOrder; Say("sorting order"); off = true; }
                // Off again right after being set, the parent as it was: the game's doing.
                if (off && child.Wrote && parentSame) { if (++child.Undone >= MaxUndone) return false; }
                else child.Undone = 0;
                child.Wrote = off;
                if (material == null) continue;

                // TMP laid the text out again and gave the child a material made from the parent's
                // as it now is: in step again, ours is no longer needed.
                var worn = child.Renderer.sharedMaterial;
                if (worn != null && worn != child.Own && worn != child.Given) { child.Given = worn; continue; }
                if (!materialChanged || worn == null) continue;
                TakeLook(child, worn, material);
                Say("material");
            }
            entry.MaterialId = materialId;
            entry.MaterialCrc = materialCrc;
            return true;
        }

        /// <summary>The same for a text on a canvas: its canvas renderer's colour (the alpha a fade writes) and culling, and its material.</summary>
        private static bool FollowCanvas(Followed entry)
        {
            Color colour = entry.Canvas.GetColor();
            bool culled = entry.Canvas.cull;
            var material = MaterialOf(entry.Text, TextMaterial);
            int materialId = material != null ? ObjectNumbers.Of(material) : 0;
            int materialCrc = material != null ? LookOf(material) : 0;
            bool materialChanged = materialId != entry.MaterialId || materialCrc != entry.MaterialCrc;
            bool parentSame = entry.Seen && entry.Colour == colour && entry.Culled == culled;
            entry.Seen = true; entry.Colour = colour; entry.Culled = culled;

            foreach (var child in entry.Children)
            {
                if (child.Object == null || child.Canvas == null) continue;
                bool off = false;
                if (child.Canvas.GetColor() != colour) { child.Canvas.SetColor(colour); Say("canvas renderer's colour"); off = true; }
                if (child.Canvas.cull != culled) { child.Canvas.cull = culled; Say("canvas renderer's culling"); off = true; }
                if (off && child.Wrote && parentSame) { if (++child.Undone >= MaxUndone) return false; }
                else child.Undone = 0;
                child.Wrote = off;
                if (material == null) continue;

                var worn = MaterialOf(child.Sub, SubMaterial);
                if (worn != null && worn != child.Own && worn != child.Given) { child.Given = worn; continue; }
                if (!materialChanged || worn == null) continue;
                TakeLook(child, worn, material);
                Say("material");
            }
            entry.MaterialId = materialId;
            entry.MaterialCrc = materialCrc;
            return true;
        }

        /// <summary>The material a text is given by its game (TMP_Text), and the one TMP gives a child on a canvas (TMP_SubMeshUI).</summary>
        private const string TextMaterial = "fontSharedMaterial", SubMaterial = "sharedMaterial";
        private static readonly Dictionary<Type, PropertyInfo> _textMaterials = new Dictionary<Type, PropertyInfo>();
        private static readonly Dictionary<Type, PropertyInfo> _subMaterials = new Dictionary<Type, PropertyInfo>();

        private static PropertyInfo MaterialProperty(Component owner, string name)
        {
            var known = name == TextMaterial ? _textMaterials : _subMaterials;
            var type = owner.GetType();
            if (!known.TryGetValue(type, out var property))
                known[type] = property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property;
        }

        private static Material MaterialOf(Component owner, string name)
        {
            if (owner == null) return null;
            return MaterialProperty(owner, name)?.GetValue(owner, null) as Material;
        }

        /// <summary>
        /// The parent's material changed (a colour faded, another preset put on): the child draws
        /// with a copy that takes everything from it but what belongs to the child's own atlas —
        /// what TMP itself keeps when it makes a fallback's material (CopyMaterialPresetProperties).
        /// A copy of the child's own: TMP shares the one it gave between every text of that look.
        /// </summary>
        private static void TakeLook(Child child, Material worn, Material parent)
        {
            if (child.Own == null)
            {
                child.Own = new Material(worn);
                EngineStrings.SetName(child.Own, worn.name + " (UGT follows)");
                child.Own.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            }
            var own = child.Own;
            var source = child.Given != null ? child.Given : worn;
            Texture atlas = source.HasProperty("_MainTex") ? source.GetTexture("_MainTex") : null;
            var kept = new float[AtlasFloats.Length];
            for (int i = 0; i < AtlasFloats.Length; i++) kept[i] = source.HasProperty(AtlasFloats[i]) ? source.GetFloat(AtlasFloats[i]) : float.NaN;

            if (own.shader != parent.shader) own.shader = parent.shader;
            own.CopyPropertiesFromMaterial(parent);
            // The keywords by reflection: an array of strings here, IL2CPP's own array there — named
            // in the code, the method fails whole on the other runtime (bench 6000.0 IL2CPP).
            var keywords = typeof(Material).GetProperty("shaderKeywords", BindingFlags.Public | BindingFlags.Instance);
            if (keywords != null && keywords.CanWrite) keywords.SetValue(own, keywords.GetValue(parent, null), null);
            if (atlas != null && own.HasProperty("_MainTex")) own.SetTexture("_MainTex", atlas);
            for (int i = 0; i < AtlasFloats.Length; i++)
                if (!float.IsNaN(kept[i]) && own.HasProperty(AtlasFloats[i])) own.SetFloat(AtlasFloats[i], kept[i]);
            if (child.Renderer != null)
            {
                if (child.Renderer.sharedMaterial != own) child.Renderer.sharedMaterial = own;
                return;
            }
            // On a canvas, through TMP's own setter: it marks the child to be drawn again with it,
            // masks included.
            var property = MaterialProperty(child.Sub, SubMaterial);
            if (property != null && property.CanWrite && MaterialOf(child.Sub, SubMaterial) != own) property.SetValue(child.Sub, own, null);
        }

        private static MethodInfo _computeCrc;
        private static bool _computeCrcLooked;
        private static readonly string[] LookColours = { "_FaceColor", "_OutlineColor", "_UnderlayColor", "_GlowColor", "_Color" };

        /// <summary>
        /// A number that changes when the material's look does: the engine's own (Material.ComputeCRC,
        /// reached by reflection — Unity 2018.1 has none), or its colours folded together where it
        /// is absent: a fade is a colour's.
        /// </summary>
        private static int LookOf(Material material)
        {
            if (!_computeCrcLooked)
            {
                _computeCrcLooked = true;
                _computeCrc = typeof(Material).GetMethod("ComputeCRC", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            }
            if (_computeCrc != null) return Convert.ToInt32(_computeCrc.Invoke(material, null));
            int look = 17;
            for (int i = 0; i < LookColours.Length; i++)
                if (material.HasProperty(LookColours[i])) look = look * 31 + material.GetColor(LookColours[i]).GetHashCode();
            return look;
        }

        /// <summary>What a TMP material says of its ATLAS, not of its look.</summary>
        private static readonly string[] AtlasFloats = { "_GradientScale", "_TextureWidth", "_TextureHeight", "_WeightNormal", "_WeightBold" };

        private static void Say(string what)
        {
            if (DiagnosticOnce.First("TmpSubMeshFollow", what))
                TranslatorCore.LogInfo($"[SubMeshFollow] this game changes a text's {what} on the text's own object: the letters TextMesh Pro draws in child objects are made to follow");
        }

        /// <summary>TMP's own list of a text's child objects (m_subTextObjects: its first entry is the text itself, empty).</summary>
        private static IEnumerable<object> SubObjects(object text)
        {
            var type = text.GetType();
            if (_subObjectsOf != type)
            {
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                _subObjectsOf = type;
                _subObjectsField = null;
                _subObjectsProperty = null;
                for (var t = type; t != null && _subObjectsField == null && _subObjectsProperty == null; t = t.BaseType)
                {
                    _subObjectsField = t.GetField("m_subTextObjects", any | BindingFlags.DeclaredOnly);
                    if (_subObjectsField == null) _subObjectsProperty = t.GetProperty("m_subTextObjects", any | BindingFlags.DeclaredOnly);
                }
            }
            object list = _subObjectsField != null ? _subObjectsField.GetValue(text) : _subObjectsProperty?.GetValue(text, null);
            if (list == null) yield break;
            if (list is Array managed) { foreach (var o in managed) if (o != null) yield return o; yield break; }
            var listType = list.GetType();
            var count = listType.GetProperty("Length") ?? listType.GetProperty("Count");
            var item = listType.GetProperty("Item");
            if (count == null || item == null) yield break;
            int n = Convert.ToInt32(count.GetValue(list, null));
            for (int i = 0; i < n; i++)
            {
                var o = item.GetValue(list, new object[] { i });
                if (o != null) yield return o;
            }
        }
    }
}
