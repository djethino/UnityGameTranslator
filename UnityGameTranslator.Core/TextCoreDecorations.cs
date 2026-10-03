using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

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
    /// '_''s texture coordinates but goes into the mesh of the CURRENT material, which can be another
    /// asset's. It then samples another atlas at the '_''s place: a highlight became a sliver of some
    /// glyph instead of a box (measured, 6000.0.84). The prefix makes the '_''s material current for
    /// the call (<see cref="PointAtUnderscore"/>), the postfix puts the text's back.
    ///
    /// Which engine needs ① is read from the engine itself, never from a version number. Mono only so
    /// far: on IL2CPP an engine struct array is an interop wrapper, and the text jobs run on native
    /// worker threads; neither is proven.
    /// </summary>
    internal static class TextCoreDecorations
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static MethodInfo _getUnderline;       // TextGenerator.GetUnderlineSpecialCharacter(TextGenerationSettings)
        private static MethodInfo _engineEnsure;       // TextGenerator.EnsureMeshInfoCapacityForMaterialReferences — Unity's fix, when present
        private static FieldInfo _referenceLookup;     // TextGenerator.m_MaterialReferenceIndexLookup
        private static FieldInfo _references;          // TextGenerator.m_MaterialReferences
        private static FieldInfo _currentIndex;        // TextGenerator.m_CurrentMaterialIndex
        private static FieldInfo _underline;           // TextGenerator.m_Underline
        private static FieldInfo _underlineCharacter, _underlineAsset, _underlineIndex;
        private static FieldInfo _currentAsset;        // TextGenerator.m_CurrentFontAsset (debug record only)
        private static FieldInfo _meshInfo;            // TextInfo.meshInfo
        private static FieldInfo _materialCount;       // TextInfo.materialCount
        private static FieldInfo _isImgui;             // TextGenerationSettings.isIMGUI
        private static ConstructorInfo _newMesh;       // MeshInfo(int size, bool isIMGUI)
        private static MethodInfo _resizeMesh;         // MeshInfo.ResizeMeshInfo(int size, bool isIMGUI)
        private static FieldInfo _vertexData, _vertexBufferSize, _meshMaterial, _meshRenderMode;
        private static FieldInfo _refCount, _refMaterial, _refFontAsset;
        private static PropertyInfo _atlasRenderMode;  // FontAsset.atlasRenderMode

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
            if (TranslatorCore.Adapter?.IsIL2CPP == true)
            {
                TranslatorCore.LogWarning("[TextCore] the mod corrects TextCore's decorations on Mono only:"
                    + (UnderlineSafe ? "" : " UI Toolkit underlines stay off on right-to-left text, and")
                    + " a highlight over letters from another font than its '_' stays drawn as the engine draws it");
                return 0;
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
            if ((_referenceLookup = generator.GetField("m_MaterialReferenceIndexLookup", Any)) == null) return "m_MaterialReferenceIndexLookup";
            if ((_references = generator.GetField("m_MaterialReferences", Any)) == null) return "m_MaterialReferences";
            if ((_currentIndex = generator.GetField("m_CurrentMaterialIndex", Any)) == null) return "m_CurrentMaterialIndex";
            if ((_underline = generator.GetField("m_Underline", Any)) == null) return "m_Underline";
            if ((_underlineCharacter = _underline.FieldType.GetField("character", Any)) == null) return "SpecialCharacter.character";
            if ((_underlineAsset = _underline.FieldType.GetField("fontAsset", Any)) == null) return "SpecialCharacter.fontAsset";
            if ((_underlineIndex = _underline.FieldType.GetField("materialIndex", Any)) == null) return "SpecialCharacter.materialIndex";
            if ((_meshInfo = textInfoType.GetField("meshInfo", Any)) == null) return "TextInfo.meshInfo";
            if ((_materialCount = textInfoType.GetField("materialCount", Any)) == null) return "TextInfo.materialCount";
            if ((_isImgui = settingsType.GetField("isIMGUI", Any)) == null) return "TextGenerationSettings.isIMGUI";

            var meshType = _meshInfo.FieldType.GetElementType();
            var referenceType = _references.FieldType.GetElementType();
            if (meshType == null || referenceType == null) return "the mesh and material arrays";
            if ((_newMesh = meshType.GetConstructor(Any, null, new[] { typeof(int), typeof(bool) }, null)) == null) return "MeshInfo(int, bool)";
            if ((_resizeMesh = meshType.GetMethod("ResizeMeshInfo", Any, null, new[] { typeof(int), typeof(bool) }, null)) == null) return "MeshInfo.ResizeMeshInfo";
            if ((_vertexData = meshType.GetField("vertexData", Any)) == null) return "MeshInfo.vertexData";
            if ((_vertexBufferSize = meshType.GetField("vertexBufferSize", Any)) == null) return "MeshInfo.vertexBufferSize";
            if ((_meshMaterial = meshType.GetField("material", Any)) == null) return "MeshInfo.material";
            if ((_meshRenderMode = meshType.GetField("glyphRenderMode", Any)) == null) return "MeshInfo.glyphRenderMode";
            if ((_refCount = referenceType.GetField("referenceCount", Any)) == null) return "MaterialReference.referenceCount";
            if ((_refMaterial = referenceType.GetField("material", Any)) == null) return "MaterialReference.material";
            if ((_refFontAsset = referenceType.GetField("fontAsset", Any)) == null) return "MaterialReference.fontAsset";
            if ((_atlasRenderMode = Members.Property(_refFontAsset.FieldType, "atlasRenderMode", Any)) == null) return "FontAsset.atlasRenderMode";

            _currentAsset = generator.GetField("m_CurrentFontAsset", Any);
            return null;
        }

        /// <summary>
        /// ① Before the engine draws an underline or a strikethrough: the '_' it is about to take,
        /// room for its material, and the mesh it goes in (<see cref="PointAtUnderscore"/>). Runs on
        /// the text job's thread; touches only this generator and this text.
        /// </summary>
        public static void DrawUnderlineMesh_Prefix(object __instance, object generationSettings, object textInfo, out int __state)
        {
            __state = -1;
            try
            {
                _getUnderline.Invoke(__instance, new[] { generationSettings });
                if (TranslatorCore.DebugMode) Record(__instance, textInfo);
                EnsureRoom(__instance, generationSettings, textInfo);
                __state = PointAtUnderscore(__instance, "underline");
            }
            catch (Exception ex) { Faults.Say("TextCoreDecorations.underline", ex.InnerException ?? ex); }
        }

        /// <summary>② The same before a highlight.</summary>
        public static void DrawTextHighlight_Prefix(object __instance, object generationSettings, object textInfo, out int __state)
        {
            __state = -1;
            try
            {
                _getUnderline.Invoke(__instance, new[] { generationSettings });
                EnsureRoom(__instance, generationSettings, textInfo);
                __state = PointAtUnderscore(__instance, "highlight");
            }
            catch (Exception ex) { Faults.Say("TextCoreDecorations.highlight", ex.InnerException ?? ex); }
        }

        /// <summary>After either draw: the current material the text had, when the prefix moved it.</summary>
        public static void Decoration_Postfix(object __instance, int __state)
        {
            if (__state < 0) return;
            try { _currentIndex.SetValue(__instance, __state); }
            catch (Exception ex) { Faults.Say("TextCoreDecorations restore", ex.InnerException ?? ex); }
        }

        /// <summary>
        /// The mesh a decoration is drawn in must be the one whose atlas holds its '_': its texture
        /// coordinates point there. The engine takes the CURRENT material's mesh, and its current
        /// material can be another asset's than the '_''s — after a fallback's letter it keeps the
        /// fallback's index while its current asset is back to the main one (measured: '_' from
        /// material 0's asset, drawn into material 1's). The '_''s own material is made current for
        /// the call: GetUnderlineSpecialCharacter, called again by the draw, then names it. Returns the
        /// index to put back, or -1 when nothing was moved.
        /// </summary>
        private static int PointAtUnderscore(object generator, string what)
        {
            object underline = _underline.GetValue(generator);
            if (_underlineCharacter.GetValue(underline) == null) return -1;   // the engine draws nothing
            object asset = _underlineAsset.GetValue(underline);
            int current = (int)_currentIndex.GetValue(generator);
            int count = ((ICollection)_referenceLookup.GetValue(generator)).Count;
            var references = (Array)_references.GetValue(generator);
            if (current < count && ReferenceEquals(_refFontAsset.GetValue(references.GetValue(current)), asset)) return -1;
            for (int i = 0; i < count; i++)
            {
                if (!ReferenceEquals(_refFontAsset.GetValue(references.GetValue(i)), asset)) continue;
                _currentIndex.SetValue(generator, i);
                if (DiagnosticOnce.First("TextCore.decoration.moved", what))
                    TranslatorCore.LogDebug($"[TextCore] {what} drawn in the mesh of its '_' (material {i}) instead of the letters' ({current})");
                return current;
            }
            return -1;   // the '_''s asset has no material of its own yet: the engine registers it while drawing
        }

        /// <summary>
        /// A mesh for every material the text now references — the engine's own fix when it has it,
        /// otherwise the same lines (EnsureMeshInfoCapacityForMaterialReferences, 6000.0.84).
        /// </summary>
        private static void EnsureRoom(object generator, object generationSettings, object textInfo)
        {
            if (_engineEnsure != null) { _engineEnsure.Invoke(generator, new[] { textInfo, generationSettings }); return; }

            int count = ((ICollection)_referenceLookup.GetValue(generator)).Count;
            var meshes = (Array)_meshInfo.GetValue(textInfo);
            if (count <= meshes.Length) return;

            bool isImgui = (bool)_isImgui.GetValue(generationSettings);
            var references = (Array)_references.GetValue(generator);
            var grown = Array.CreateInstance(meshes.GetType().GetElementType(), count);
            Array.Copy(meshes, grown, meshes.Length);
            for (int i = meshes.Length; i < count; i++)
            {
                object reference = references.GetValue(i);
                int referenceCount = (int)_refCount.GetValue(reference);
                object mesh = grown.GetValue(i);
                if (_vertexData.GetValue(mesh) == null)
                    mesh = _newMesh.Invoke(new object[] { referenceCount <= 0 ? 1 : referenceCount + 1, isImgui });
                else if ((int)_vertexBufferSize.GetValue(mesh) < referenceCount * 4)
                    _resizeMesh.Invoke(mesh, new object[] { referenceCount > 1024 ? referenceCount + 256 : Mathf.NextPowerOfTwo(referenceCount), isImgui });
                _meshMaterial.SetValue(mesh, _refMaterial.GetValue(reference));
                _meshRenderMode.SetValue(mesh, _atlasRenderMode.GetValue(_refFontAsset.GetValue(reference), null));
                grown.SetValue(mesh, i);
            }
            _meshInfo.SetValue(textInfo, grown);
            _materialCount.SetValue(textInfo, count);
            if (DiagnosticOnce.First("TextCore.underline", "grown"))
                TranslatorCore.LogInfo($"[TextCore] decoration drawn with a material the text had not counted: meshes grown {meshes.Length} → {count} (Unity's fix, carried by the mod)");
        }

        // Which asset gave the '_', over which asset's letters, and whether its material was counted —
        // once per distinct answer: what decides whether an engine without Unity's fix would have died.
        // Assets are told apart by their hash: an object's name cannot be read on the text job's thread.
        private static void Record(object generator, object textInfo)
        {
            if (_currentAsset == null) return;
            object underline = _underline.GetValue(generator);
            object from = _underlineAsset.GetValue(underline);
            object over = _currentAsset.GetValue(generator);
            int index = (int)_underlineIndex.GetValue(underline);
            int materials = ((ICollection)_referenceLookup.GetValue(generator)).Count;
            int meshes = ((Array)_meshInfo.GetValue(textInfo)).Length;
            string source = ReferenceEquals(from, over) ? $"the asset of the letters it underlines (#{from?.GetHashCode()})"
                                                        : $"ANOTHER asset (#{from?.GetHashCode()}) than the letters' (#{over?.GetHashCode()})";
            string counted = index < meshes ? "counted" : "NOT counted";
            if (DiagnosticOnce.First("TextCore.underline.source", source + "\u0001" + index + "\u0001" + materials + "\u0001" + meshes))
                TranslatorCore.LogDebug($"[TextCore] underline '_' from {source}: material {index} of {materials}, {meshes} mesh(es) — {counted}");
        }
    }
}
