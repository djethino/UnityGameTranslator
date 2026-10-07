using System;
using System.Reflection;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// What TMP laid a text out as, by reflection: the Core names no TMP type (it may not be in the
    /// game). The characters, lines and meshes of a text's textInfo, and the call that uploads
    /// moved vertices again — read by whatever moves geometry after TMP's layout (RtlInputFields:
    /// a field's glyphs into visual order; TmpDecorations: underlines and strikethroughs over their
    /// letters). Needs only TMP_Text: a game with no TMP input field has it all the same.
    /// </summary>
    internal static class TmpLayout
    {
        private static bool _resolved, _ok;
        internal static PropertyInfo TextInfo, IsRightToLeft, FontSharedMaterial;
        internal static MethodInfo UpdateVertexData;
        internal static MemberInfo CharacterCount, CharacterInfo, LineCount, LineInfo, MeshInfo;
        internal static MemberInfo CiIndex, CiStringLength, CiOrigin, CiXAdvance, CiLine, CiVisible, CiMaterial, CiVertex;
        internal static MemberInfo CiStyle, CiBottomLeft, CiTopRight, CiScale;
        internal static MemberInfo LiFirst, LiAscender, LiDescender, MiVertices, MiUvs0, MiMaterial;

        internal static bool Resolve()
        {
            if (_resolved) return _ok;
            _resolved = true;
            try
            {
                var text = TypeHelper.TMP_TextType;
                if (text == null) return _ok = false;
                const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;

                TextInfo = text.GetProperty("textInfo", pub);
                IsRightToLeft = text.GetProperty("isRightToLeftText", pub);
                FontSharedMaterial = text.GetProperty("fontSharedMaterial", pub);
                UpdateVertexData = text.GetMethod("UpdateVertexData", pub, null, Type.EmptyTypes, null);

                var infoType = TextInfo?.PropertyType;
                CharacterCount = Member(infoType, "characterCount");
                CharacterInfo = Member(infoType, "characterInfo");
                LineCount = Member(infoType, "lineCount");
                LineInfo = Member(infoType, "lineInfo");
                MeshInfo = Member(infoType, "meshInfo");

                var ciType = ElementType(Members.TypeOf(CharacterInfo));
                CiIndex = Member(ciType, "index");
                CiStringLength = Member(ciType, "stringLength");
                CiOrigin = Member(ciType, "origin");
                CiXAdvance = Member(ciType, "xAdvance");
                CiLine = Member(ciType, "lineNumber");
                CiVisible = Member(ciType, "isVisible");
                CiMaterial = Member(ciType, "materialReferenceIndex");
                CiVertex = Member(ciType, "vertexIndex");
                CiStyle = Member(ciType, "style");
                CiBottomLeft = Member(ciType, "bottomLeft");
                CiTopRight = Member(ciType, "topRight");
                CiScale = Member(ciType, "scale");

                var liType = ElementType(Members.TypeOf(LineInfo));
                LiFirst = Member(liType, "firstCharacterIndex");
                LiAscender = Member(liType, "ascender");
                LiDescender = Member(liType, "descender");

                var miType = ElementType(Members.TypeOf(MeshInfo));
                MiVertices = Member(miType, "vertices");
                MiUvs0 = Member(miType, "uvs0");
                MiMaterial = Member(miType, "material");

                _ok = TextInfo != null && CharacterCount != null && CharacterInfo != null && LineInfo != null && MeshInfo != null
                      && CiIndex != null && CiOrigin != null && CiXAdvance != null && CiLine != null && CiVisible != null
                      && CiMaterial != null && CiVertex != null && MiVertices != null;
                if (!_ok && DiagnosticOnce.First("TmpLayout", "resolve"))
                    TranslatorCore.LogWarning("[TmpLayout] a member of TMP's text layout is missing — what moves geometry after TMP's layout is left undone");
            }
            catch (Exception ex)
            {
                _ok = false;
                Faults.Say("TmpLayout.Resolve", ex);
            }
            return _ok;
        }

        private static MemberInfo Member(Type t, string name) =>
            Members.FieldOrProperty(t, name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        private static Type ElementType(Type arrayType) => EngineCollections.ElementType(arrayType);

        internal static object Get(MemberInfo m, object target) => Members.Get(m, target);

        /// <summary>Sets a member of an element read with EngineCollections.Item — a boxed struct on Mono, written back with EngineCollections.SetItem.</summary>
        internal static void Set(MemberInfo m, object target, object value) => Members.Set(m, target, value);
    }
}
