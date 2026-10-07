using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Two defects of TextCore (UI Toolkit's text engine) in how it draws decorations, corrected by
    /// the mod. Both come from the same lookup: the '_' an underline, a strikethrough or a highlight
    /// is drawn with is fetched WHILE drawing (GetUnderlineSpecialCharacter), in the font asset of the
    /// glyph being drawn, through its fallbacks — so it can belong to another asset than the letters,
    /// with a material of its own.
    ///
    /// ① The crash, fixed by Unity in later engines: the '_''s material, registered after the text's
    /// meshes were sized, indexes textInfo.meshInfo past its end (DrawUnderlineMesh) —
    /// IndexOutOfRangeException, the text job dies and the game's panel with it. Unity's fix
    /// (EnsureMeshInfoCapacityForMaterialReferences, right after the lookup) grows the meshes; where
    /// the engine lacks it, the prefix does the lookup once more just before the original does (a
    /// material is registered once, the same index comes back) and grows them the same way.
    ///
    /// ② The wrong atlas, not fixed in any engine read (6000.0 to 6000.5): the decoration takes the
    /// '_''s texture coordinates but goes into the mesh the generator's CURRENT state names, which can
    /// be another asset's. It then samples another atlas at the '_''s place: a highlight became a
    /// sliver of some glyph instead of a box, a strikethrough bits of letters (measured, 6000.0.84 and
    /// 6000.3.6). The prefix makes the '_''s material, index and asset current for the call
    /// (<see cref="PointAtUnderscore"/>), the postfix puts the text's back.
    ///
    /// Which engine needs ① is read from the engine itself, never from a version number. The same
    /// code serves Mono and IL2CPP: every member is reached as a field or as the property the interop
    /// makes of it (Members), arrays and dictionaries through what both kinds answer to.
    /// </summary>
    internal static class TextCoreDecorations
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static MethodInfo _getUnderline;       // TextGenerator.GetUnderlineSpecialCharacter(TextGenerationSettings)
        private static MethodInfo _engineEnsure;       // TextGenerator.EnsureMeshInfoCapacityForMaterialReferences — Unity's fix, when present
        private static MemberInfo _referenceLookup;    // TextGenerator.m_MaterialReferenceIndexLookup
        private static MemberInfo _references;         // TextGenerator.m_MaterialReferences
        private static MemberInfo _currentIndex;       // TextGenerator.m_CurrentMaterialIndex
        private static MemberInfo _underline;          // TextGenerator.m_Underline
        private static MemberInfo _underlineCharacter, _underlineAsset, _underlineIndex;
        private static MemberInfo _currentAsset;       // TextGenerator.m_CurrentFontAsset
        private static MemberInfo _currentMaterial;    // TextGenerator.m_CurrentMaterial
        private static MemberInfo _meshInfo;           // TextInfo.meshInfo
        private static MemberInfo _materialCount;      // TextInfo.materialCount
        private static MemberInfo _isImgui;            // TextGenerationSettings.isIMGUI
        private static ConstructorInfo _newMesh;       // MeshInfo(int size, bool isIMGUI)
        private static MemberInfo _meshMaterial, _meshRenderMode;
        private static MemberInfo _refCount, _refMaterial, _refFontAsset;
        private static PropertyInfo _atlasRenderMode;  // FontAsset.atlasRenderMode
        private static Type _meshArrayType;            // MeshInfo[] on Mono, the interop's array on IL2CPP

        /// <summary>
        /// Does this engine draw an underline without the crash — its own fix, or ours in place?
        /// False until <see cref="Patch"/> has answered, and on an engine with no TextCore at all.
        /// </summary>
        internal static bool UnderlineSafe { get; private set; }

        /// <summary>
        /// Puts the corrections in place where they apply. Returns the number of hooks placed.
        /// Every way of not carrying one is said once, by name.
        /// </summary>
        public static int Patch(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            var generator = AssemblyTypes.Find("UnityEngine.TextCore.Text.TextGenerator");
            if (generator == null) return 0;   // no TextCore text engine: nothing to correct
            MethodInfo underline = null, highlight = null;
            foreach (var m in generator.GetMethods(Any))
            {
                if (m.Name == "DrawUnderlineMesh") underline = m;
                else if (m.Name == "DrawTextHighlight") highlight = m;
            }
            if (underline == null && highlight == null) return 0;

            _engineEnsure = generator.GetMethod("EnsureMeshInfoCapacityForMaterialReferences", Any);
            if (_engineEnsure != null)
            {
                UnderlineSafe = true;
                TranslatorCore.LogInfo("[TextCore] this engine carries Unity's underline fix");
            }

            string missing = Resolve(generator, underline ?? highlight);
            if (missing != null)
            {
                TranslatorCore.LogWarning($"[TextCore] the mod cannot correct TextCore's decorations here ({missing} not found)"
                    + (UnderlineSafe ? "" : ": UI Toolkit underlines stay off on right-to-left text"));
                return 0;
            }

            int placed = 0;
            const BindingFlags hook = BindingFlags.Static | BindingFlags.Public;
            var restore = typeof(TextCoreDecorations).GetMethod(nameof(Decoration_Postfix), hook);
            if (underline != null)
            {
                patcher(underline, typeof(TextCoreDecorations).GetMethod(nameof(DrawUnderlineMesh_Prefix), hook), restore);
                placed++;
                if (!UnderlineSafe)
                    TranslatorCore.LogInfo("[TextCore] this engine lacks Unity's underline fix: the mod carries it (TextGenerator.DrawUnderlineMesh)");
                UnderlineSafe = true;
            }
            if (highlight != null)
            {
                patcher(highlight, typeof(TextCoreDecorations).GetMethod(nameof(DrawTextHighlight_Prefix), hook), restore);
                placed++;
            }
            TranslatorCore.LogInfo("[TextCore] underlines, strikethroughs and highlights drawn in the mesh of their own '_'");
            return placed;
        }

        /// <summary>Every member the corrections touch, or the name of the first one this engine does not have.</summary>
        private static string Resolve(Type generator, MethodInfo draw)
        {
            Type settingsType = null, textInfoType = null;
            foreach (var p in draw.GetParameters())
            {
                if (p.Name == "generationSettings") settingsType = p.ParameterType;
                else if (p.Name == "textInfo") textInfoType = p.ParameterType;
            }
            if (settingsType == null || textInfoType == null) return draw.Name + "(generationSettings, textInfo)";

            _getUnderline = generator.GetMethod("GetUnderlineSpecialCharacter", Any, null, new[] { settingsType }, null);
            if (_getUnderline == null) return "GetUnderlineSpecialCharacter";
            if ((_referenceLookup = Members.FieldOrProperty(generator, "m_MaterialReferenceIndexLookup", Any)) == null) return "m_MaterialReferenceIndexLookup";
            if ((_references = Members.FieldOrProperty(generator, "m_MaterialReferences", Any)) == null) return "m_MaterialReferences";
            if ((_currentIndex = Members.FieldOrProperty(generator, "m_CurrentMaterialIndex", Any)) == null) return "m_CurrentMaterialIndex";
            if ((_underline = Members.FieldOrProperty(generator, "m_Underline", Any)) == null) return "m_Underline";
            var special = Members.TypeOf(_underline);
            if ((_underlineCharacter = Members.FieldOrProperty(special, "character", Any)) == null) return "SpecialCharacter.character";
            if ((_underlineAsset = Members.FieldOrProperty(special, "fontAsset", Any)) == null) return "SpecialCharacter.fontAsset";
            if ((_underlineIndex = Members.FieldOrProperty(special, "materialIndex", Any)) == null) return "SpecialCharacter.materialIndex";
            if ((_meshInfo = Members.FieldOrProperty(textInfoType, "meshInfo", Any)) == null) return "TextInfo.meshInfo";
            if ((_materialCount = Members.FieldOrProperty(textInfoType, "materialCount", Any)) == null) return "TextInfo.materialCount";
            // Absent before Unity 6 (MeshInfo was made by size alone): the mesh is then made as that engine makes it.
            _isImgui = Members.FieldOrProperty(settingsType, "isIMGUI", Any);

            _meshArrayType = Members.TypeOf(_meshInfo);
            var meshType = ElementType(_meshArrayType);
            var referenceType = ElementType(Members.TypeOf(_references));
            if (meshType == null || referenceType == null) return "the mesh and material arrays";
            _newMesh = meshType.GetConstructor(Any, null, new[] { typeof(int), typeof(bool) }, null)
                       ?? meshType.GetConstructor(Any, null, new[] { typeof(int) }, null);
            if (_newMesh == null) return "MeshInfo(int[, bool])";
            if ((_meshMaterial = Members.FieldOrProperty(meshType, "material", Any)) == null) return "MeshInfo.material";
            _meshRenderMode = Members.FieldOrProperty(meshType, "glyphRenderMode", Any);   // absent before Unity 6
            if ((_refCount = Members.FieldOrProperty(referenceType, "referenceCount", Any)) == null) return "MaterialReference.referenceCount";
            if ((_refMaterial = Members.FieldOrProperty(referenceType, "material", Any)) == null) return "MaterialReference.material";
            if ((_refFontAsset = Members.FieldOrProperty(referenceType, "fontAsset", Any)) == null) return "MaterialReference.fontAsset";
            _atlasRenderMode = Members.Property(Members.TypeOf(_refFontAsset), "atlasRenderMode", Any);
            if (_meshRenderMode != null && _atlasRenderMode == null) return "FontAsset.atlasRenderMode";

            if ((_currentAsset = Members.FieldOrProperty(generator, "m_CurrentFontAsset", Any)) == null) return "m_CurrentFontAsset";
            if ((_currentMaterial = Members.FieldOrProperty(generator, "m_CurrentMaterial", Any)) == null) return "m_CurrentMaterial";
            return null;
        }

        /// <summary>
        /// ① Before the engine draws an underline or a strikethrough: the '_' it is about to take,
        /// room for its material, and the mesh it goes in (<see cref="PointAtUnderscore"/>). Runs on
        /// the text job's thread; touches only this generator and this text.
        /// </summary>
        public static void DrawUnderlineMesh_Prefix(object __instance, object generationSettings, object textInfo, out object __state)
        {
            __state = null;
            try
            {
                if (TranslatorCore.DebugMode && DiagnosticOnce.First("TextCore.hook", "underline"))
                    TranslatorCore.LogDebug("[TextCore] first underline or strikethrough drawn through the mod's correction");
                _getUnderline.Invoke(__instance, new[] { generationSettings });
                if (TranslatorCore.DebugMode) Record(__instance, textInfo);
                EnsureRoom(__instance, generationSettings, textInfo);
                __state = PointAtUnderscore(__instance, "underline");
            }
            catch (Exception ex) { Faults.Say("TextCoreDecorations.underline", ex.InnerException ?? ex); }
        }

        /// <summary>② The same before a highlight.</summary>
        public static void DrawTextHighlight_Prefix(object __instance, object generationSettings, object textInfo, out object __state)
        {
            __state = null;
            try
            {
                if (TranslatorCore.DebugMode && DiagnosticOnce.First("TextCore.hook", "highlight"))
                    TranslatorCore.LogDebug("[TextCore] first highlight drawn through the mod's correction");
                _getUnderline.Invoke(__instance, new[] { generationSettings });
                EnsureRoom(__instance, generationSettings, textInfo);
                __state = PointAtUnderscore(__instance, "highlight");
            }
            catch (Exception ex) { Faults.Say("TextCoreDecorations.highlight", ex.InnerException ?? ex); }
        }

        /// <summary>After either draw: the generator's current material, index and asset as the text had them.</summary>
        public static void Decoration_Postfix(object __instance, object __state)
        {
            if (!(__state is Current before)) return;
            try { before.Restore(__instance); }
            catch (Exception ex) { Faults.Say("TextCoreDecorations restore", ex.InnerException ?? ex); }
        }

        /// <summary>The generator's current material index, material and font asset — moved together, put back together.</summary>
        private sealed class Current
        {
            internal int Index;
            internal object Material, Asset;

            internal static Current Of(object generator) => new Current
            {
                Index = (int)Members.Get(_currentIndex, generator),
                Material = Members.Get(_currentMaterial, generator),
                Asset = Members.Get(_currentAsset, generator),
            };

            internal void Restore(object generator)
            {
                Members.Set(_currentIndex, generator, Index);
                Members.Set(_currentMaterial, generator, Material);
                Members.Set(_currentAsset, generator, Asset);
            }
        }

        /// <summary>
        /// The mesh a decoration is drawn in must be the one whose atlas holds its '_': its texture
        /// coordinates point there. The engine draws into the mesh of the '_''s material index
        /// (DrawUnderlineMesh, 6000.0 and 6000.3 read the same), which GetUnderlineSpecialCharacter —
        /// called again inside the draw — takes from the generator's CURRENT state: the current index
        /// when the current ASSET is the '_''s, otherwise a material registered from the current
        /// MATERIAL. That state can be another asset's than the '_''s — after a fallback's letter
        /// (measured: '_' from material 0's asset, drawn into material 1's). Moving the index alone left
        /// asset and material behind: the strip went into a mesh with another atlas, bits of glyphs at
        /// the strike's height — always in some texts, one run in four in others, as the material left
        /// by the previous text decided (bench, Hebrew and Arabic from a fallback, 2026-10-03). Index,
        /// material and asset are made the '_''s together for the call. Returns what to put back, or null.
        /// </summary>
        private static Current PointAtUnderscore(object generator, string what)
        {
            object underline = Members.Get(_underline, generator);
            if (Members.Get(_underlineCharacter, underline) == null) return null;   // the engine draws nothing
            object asset = Members.Get(_underlineAsset, underline);
            int count = CountOf(Members.Get(_referenceLookup, generator));
            object references = Members.Get(_references, generator);
            for (int i = 0; i < count; i++)
            {
                object reference = At(references, i);
                if (!Same(Members.Get(_refFontAsset, reference), asset)) continue;
                var before = Current.Of(generator);
                object material = Members.Get(_refMaterial, reference);
                if (before.Index == i && Same(before.Asset, asset) && Same(before.Material, material)) return null;
                Members.Set(_currentIndex, generator, i);
                Members.Set(_currentMaterial, generator, material);
                Members.Set(_currentAsset, generator, asset);
                if (DiagnosticOnce.First("TextCore.decoration.moved", what))
                    TranslatorCore.LogDebug($"[TextCore] {what} drawn in the mesh of its '_' (material {i}) instead of the letters' ({before.Index})");
                return before;
            }
            return null;   // the '_''s asset has no material of its own yet: the engine registers it while drawing
        }

        /// <summary>
        /// A mesh for every material the text now references — the engine's own fix when it has it,
        /// otherwise the same lines (EnsureMeshInfoCapacityForMaterialReferences, 6000.0.84): the
        /// slots past the old end are new, so each is made for its material.
        /// </summary>
        private static void EnsureRoom(object generator, object generationSettings, object textInfo)
        {
            if (_engineEnsure != null) { _engineEnsure.Invoke(generator, new[] { textInfo, generationSettings }); return; }

            int count = CountOf(Members.Get(_referenceLookup, generator));
            object meshes = Members.Get(_meshInfo, textInfo);
            int had = LengthOf(meshes);
            if (count <= had) return;

            bool isImgui = _isImgui != null && (bool)Members.Get(_isImgui, generationSettings);
            bool withImgui = _newMesh.GetParameters().Length == 2;
            object references = Members.Get(_references, generator);
            object grown = NewArray(_meshArrayType, count);
            for (int i = 0; i < had; i++) Put(grown, i, At(meshes, i));
            for (int i = had; i < count; i++)
            {
                object reference = At(references, i);
                int referenceCount = (int)Members.Get(_refCount, reference);
                int size = referenceCount <= 0 ? 1 : referenceCount + 1;
                object mesh = _newMesh.Invoke(withImgui ? new object[] { size, isImgui } : new object[] { size });
                Members.Set(_meshMaterial, mesh, Members.Get(_refMaterial, reference));
                if (_meshRenderMode != null)
                    Members.Set(_meshRenderMode, mesh, _atlasRenderMode.GetValue(Members.Get(_refFontAsset, reference), null));
                Put(grown, i, mesh);
            }
            Members.Set(_meshInfo, textInfo, grown);
            Members.Set(_materialCount, textInfo, count);
            if (DiagnosticOnce.First("TextCore.underline", "grown"))
                TranslatorCore.LogInfo($"[TextCore] decoration drawn with a material the text had not counted: meshes grown {had} → {count} (Unity's fix, carried by the mod)");
        }

        // What Mono and IL2CPP hold differently: EngineCollections.
        private static Type ElementType(Type arrayType) => EngineCollections.ElementType(arrayType);
        private static int CountOf(object collection) => EngineCollections.Length(collection);
        private static int LengthOf(object array) => EngineCollections.Length(array);
        private static object At(object array, int i) => EngineCollections.Item(array, i);
        private static void Put(object array, int i, object value) => EngineCollections.SetItem(array, i, value);
        private static object NewArray(Type arrayType, int length) => EngineCollections.NewArray(arrayType, length);
        private static bool Same(object a, object b) => EngineCollections.Same(a, b);

        // Which asset gave the '_', over which asset's letters, and whether its material was counted —
        // once per distinct answer: what decides whether an engine without Unity's fix would have died.
        // Assets are told apart by their hash: an object's name cannot be read on the text job's thread.
        private static void Record(object generator, object textInfo)
        {
            if (_currentAsset == null) return;
            object underline = Members.Get(_underline, generator);
            object from = Members.Get(_underlineAsset, underline);
            object over = Members.Get(_currentAsset, generator);
            int index = (int)Members.Get(_underlineIndex, underline);
            int materials = CountOf(Members.Get(_referenceLookup, generator));
            int meshes = LengthOf(Members.Get(_meshInfo, textInfo));
            string source = Same(from, over) ? $"the asset of the letters it underlines (#{from?.GetHashCode()})"
                                             : $"ANOTHER asset (#{from?.GetHashCode()}) than the letters' (#{over?.GetHashCode()})";
            string counted = index < meshes ? "counted" : "NOT counted";
            if (DiagnosticOnce.First("TextCore.underline.source", source + "\u0001" + index + "\u0001" + materials + "\u0001" + meshes))
                TranslatorCore.LogDebug($"[TextCore] underline '_' from {source}: material {index} of {materials}, {meshes} mesh(es) — {counted}");
        }
    }
}
