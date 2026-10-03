using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Unity's own fix for the underline crash of TextCore (UI Toolkit's text engine), carried by
    /// the mod into the engines that shipped without it.
    ///
    /// The defect, as the engine's code reads (6000.0.57 against 6000.0.84): DrawUnderlineMesh asks
    /// GetUnderlineSpecialCharacter for the '_' to draw with, in the font asset of the glyph being
    /// drawn. When the '_' comes from ANOTHER asset (a fallback, the default font, a bold typeface),
    /// that call registers a new material — after the text's meshes were sized for the materials
    /// counted before drawing — and the next line indexes textInfo.meshInfo past its end:
    /// IndexOutOfRangeException, the text job dies, and the game's panel with it. Unity's fix
    /// (EnsureMeshInfoCapacityForMaterialReferences, called right after that lookup) grows the
    /// meshes to the materials now registered. Which engine lacks it is read from the engine
    /// itself, never from a version number: the method is there or it is not.
    ///
    /// Carried as a prefix: the lookup is made once more just before the original makes it — the
    /// engine registers a material once and returns the same index after — then the meshes are
    /// grown exactly as Unity's fix grows them. Mono only so far: on IL2CPP an engine struct array
    /// is an interop wrapper, and the text jobs run on native worker threads; neither is proven.
    /// </summary>
    internal static class TextCoreUnderlineFix
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static MethodInfo _getUnderline;       // TextGenerator.GetUnderlineSpecialCharacter(TextGenerationSettings)
        private static FieldInfo _referenceLookup;     // TextGenerator.m_MaterialReferenceIndexLookup
        private static FieldInfo _references;          // TextGenerator.m_MaterialReferences
        private static FieldInfo _meshInfo;            // TextInfo.meshInfo
        private static FieldInfo _materialCount;       // TextInfo.materialCount
        private static FieldInfo _isImgui;             // TextGenerationSettings.isIMGUI
        private static ConstructorInfo _newMesh;       // MeshInfo(int size, bool isIMGUI)
        private static MethodInfo _resizeMesh;         // MeshInfo.ResizeMeshInfo(int size, bool isIMGUI)
        private static FieldInfo _vertexData, _vertexBufferSize, _meshMaterial, _meshRenderMode;
        private static FieldInfo _refCount, _refMaterial, _refFontAsset;
        private static PropertyInfo _atlasRenderMode;  // FontAsset.atlasRenderMode
        // For the debug record only — where the '_' was found, over which asset: absent, nothing is said.
        private static FieldInfo _underline, _underlineAsset, _underlineIndex, _currentAsset;

        /// <summary>
        /// Does this engine draw an underline without the crash — its own fix, or ours in place?
        /// False until <see cref="Patch"/> has answered, and on an engine with no TextCore at all.
        /// </summary>
        internal static bool Present { get; private set; }

        /// <summary>
        /// Puts the fix in place when the engine lacks it. Returns the number of hooks placed (0 or 1).
        /// Every way of not carrying it is said once, by name.
        /// </summary>
        public static int Patch(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            var generator = AssemblyTypes.Find("UnityEngine.TextCore.Text.TextGenerator");
            if (generator == null) return 0;   // no TextCore text engine: nothing draws this underline
            MethodInfo draw = null;
            foreach (var m in generator.GetMethods(Any))
                if (m.Name == "DrawUnderlineMesh") { draw = m; break; }
            if (draw == null) return 0;

            if (generator.GetMethod("EnsureMeshInfoCapacityForMaterialReferences", Any) != null)
            {
                Present = true;
                TranslatorCore.LogInfo("[TextCore] this engine carries Unity's underline fix");
                return 0;
            }
            if (TranslatorCore.Adapter?.IsIL2CPP == true)
            {
                TranslatorCore.LogWarning("[TextCore] this engine lacks Unity's underline fix, and the mod carries it on Mono only: UI Toolkit underlines stay off on right-to-left text");
                return 0;
            }

            string missing = Resolve(generator, draw);
            if (missing != null)
            {
                TranslatorCore.LogWarning($"[TextCore] this engine lacks Unity's underline fix, and the mod cannot carry it here ({missing} not found): UI Toolkit underlines stay off on right-to-left text");
                return 0;
            }
            patcher(draw, typeof(TextCoreUnderlineFix).GetMethod(nameof(DrawUnderlineMesh_Prefix), BindingFlags.Static | BindingFlags.Public), null);
            Present = true;
            TranslatorCore.LogInfo("[TextCore] this engine lacks Unity's underline fix: the mod carries it (TextGenerator.DrawUnderlineMesh)");
            return 1;
        }

        /// <summary>Every member the fix touches, or the name of the first one this engine does not have.</summary>
        private static string Resolve(Type generator, MethodInfo draw)
        {
            Type settingsType = null, textInfoType = null;
            foreach (var p in draw.GetParameters())
            {
                if (p.Name == "generationSettings") settingsType = p.ParameterType;
                else if (p.Name == "textInfo") textInfoType = p.ParameterType;
            }
            if (settingsType == null || textInfoType == null) return "DrawUnderlineMesh(generationSettings, textInfo)";

            _getUnderline = generator.GetMethod("GetUnderlineSpecialCharacter", Any, null, new[] { settingsType }, null);
            if (_getUnderline == null) return "GetUnderlineSpecialCharacter";
            if ((_referenceLookup = generator.GetField("m_MaterialReferenceIndexLookup", Any)) == null) return "m_MaterialReferenceIndexLookup";
            if ((_references = generator.GetField("m_MaterialReferences", Any)) == null) return "m_MaterialReferences";
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

            _underline = generator.GetField("m_Underline", Any);
            _underlineAsset = _underline?.FieldType.GetField("fontAsset", Any);
            _underlineIndex = _underline?.FieldType.GetField("materialIndex", Any);
            _currentAsset = generator.GetField("m_CurrentFontAsset", Any);
            return null;
        }

        /// <summary>
        /// Before the engine draws an underline or a strikethrough: the '_' it is about to take,
        /// then room for its material — Unity's EnsureMeshInfoCapacityForMaterialReferences, line
        /// for line. Runs on the text job's thread; touches only this generator and this text.
        /// </summary>
        public static void DrawUnderlineMesh_Prefix(object __instance, object generationSettings, object textInfo)
        {
            try
            {
                _getUnderline.Invoke(__instance, new[] { generationSettings });
                int count = ((ICollection)_referenceLookup.GetValue(__instance)).Count;
                var meshes = (Array)_meshInfo.GetValue(textInfo);
                if (TranslatorCore.DebugMode) Record(__instance, count, meshes.Length);
                if (count <= meshes.Length) return;

                bool isImgui = (bool)_isImgui.GetValue(generationSettings);
                var references = (Array)_references.GetValue(__instance);
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
                    TranslatorCore.LogInfo($"[TextCore] underline drawn with a material the text had not counted: meshes grown {meshes.Length} → {count} (Unity's fix, carried by the mod)");
            }
            catch (Exception ex) { Faults.Say("TextCoreUnderlineFix", ex.InnerException ?? ex); }
        }

        // Which asset gave the '_', over which asset's letters, and whether its material was counted —
        // once per distinct answer: what decides whether this engine would have died. Assets are told
        // apart by identity: an object's name cannot be read on the text job's thread.
        private static void Record(object generator, int materials, int meshes)
        {
            if (_underline == null || _underlineAsset == null || _underlineIndex == null || _currentAsset == null) return;
            object underline = _underline.GetValue(generator);
            object from = _underlineAsset.GetValue(underline);
            object over = _currentAsset.GetValue(generator);
            int index = (int)_underlineIndex.GetValue(underline);
            // An engine object's hash is its instance id, readable on any thread (its name is not).
            string source = ReferenceEquals(from, over) ? $"the asset of the letters it underlines (#{from?.GetHashCode()})"
                                                        : $"ANOTHER asset (#{from?.GetHashCode()}) than the letters' (#{over?.GetHashCode()})";
            string counted = index < meshes ? "counted" : "NOT counted";
            if (DiagnosticOnce.First("TextCore.underline.source", source + "\u0001" + index + "\u0001" + materials + "\u0001" + meshes))
                TranslatorCore.LogDebug($"[TextCore] underline '_' from {source}: material {index} of {materials}, {meshes} mesh(es) — {counted}");
        }
    }
}
