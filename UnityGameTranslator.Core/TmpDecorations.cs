using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Puts TMP's underlines and strikethroughs back over their letters after its layout — where its
    /// right-to-left setting drew them backwards, or where the mod moved the letters (an input field
    /// in visual order). What is decided is in <see cref="DecorationSpans"/> (pure, checked); this
    /// reads TMP's geometry for it and writes the strips back. Highlights (&lt;mark&gt;) are drawn
    /// right by TMP in both directions (bench tmpdeco) and are not touched.
    /// </summary>
    internal static class TmpDecorations
    {
        private const int Underline = 4, Strikethrough = 64; // FontStyles, the same in every TMP

        /// <summary>
        /// Called after TMP built a text's mesh. <paramref name="shift"/>: how far each character was
        /// moved since (null: none). True when a strip was rewritten — the caller uploads the
        /// vertices, once for everything it moved.
        /// </summary>
        internal static bool Respan(object text, float[] shift)
        {
            if (text == null || !TmpLayout.Resolve() || TmpLayout.CiStyle == null || TmpLayout.CiBottomLeft == null
                || TmpLayout.CiTopRight == null || TmpLayout.CiScale == null || TmpLayout.MiUvs0 == null) return false;
            bool rtl = TmpLayout.IsRightToLeft != null && Convert.ToBoolean(TmpLayout.IsRightToLeft.GetValue(text, null));
            if (!rtl && shift == null) return false;

            var info = TmpLayout.TextInfo.GetValue(text, null);
            if (info == null) return false;
            int count = Convert.ToInt32(TmpLayout.Get(TmpLayout.CharacterCount, info));
            var chars = TmpLayout.Get(TmpLayout.CharacterInfo, info);
            var meshes = TmpLayout.Get(TmpLayout.MeshInfo, info);
            if (chars == null || meshes == null || count <= 0) return false;

            // Styles first, alone: most texts have no strip, and every right-to-left text comes here
            // at each layout.
            var items = new object[count];
            var decorated = new bool[count];
            bool any = false;
            for (int k = 0; k < count; k++)
            {
                items[k] = EngineCollections.Item(chars, k);
                decorated[k] = (Convert.ToInt32(TmpLayout.Get(TmpLayout.CiStyle, items[k])) & (Underline | Strikethrough)) != 0;
                any |= decorated[k];
            }
            if (!any) return false;

            var letters = new DecorationSpans.Letter[count];
            var glyphAt = new HashSet<long>();
            for (int k = 0; k < count; k++)
            {
                var c = items[k];
                bool visible = Convert.ToBoolean(TmpLayout.Get(TmpLayout.CiVisible, c));
                letters[k] = new DecorationSpans.Letter
                {
                    Visible = visible,
                    Decorated = decorated[k],
                    Line = Convert.ToInt32(TmpLayout.Get(TmpLayout.CiLine, c)),
                    Left = ((Vector3)TmpLayout.Get(TmpLayout.CiBottomLeft, c)).x,
                    Right = ((Vector3)TmpLayout.Get(TmpLayout.CiTopRight, c)).x,
                    Shift = shift != null && k < shift.Length ? shift[k] : 0f,
                    Scale = Convert.ToSingle(TmpLayout.Get(TmpLayout.CiScale, c)),
                };
                if (visible)
                    glyphAt.Add(((long)Convert.ToInt32(TmpLayout.Get(TmpLayout.CiMaterial, c)) << 32) | (uint)Convert.ToInt32(TmpLayout.Get(TmpLayout.CiVertex, c)));
            }

            bool changed = false;
            int meshCount = EngineCollections.Length(meshes);
            for (int m = 0; m < meshCount; m++)
            {
                var mesh = EngineCollections.Item(meshes, m);
                var vertices = mesh == null ? null : TmpLayout.Get(TmpLayout.MiVertices, mesh);
                int n = EngineCollections.Length(vertices);
                if (n < 12) continue;
                var xs = new float[n];
                var ys = new float[n];
                for (int v = 0; v < n; v++)
                {
                    var p = (Vector3)EngineCollections.Item(vertices, v);
                    xs[v] = p.x;
                    ys[v] = p.y;
                }
                for (int at = 0; at + 12 <= n; at += 4)
                {
                    if (glyphAt.Contains(((long)m << 32) | (uint)at) || !DecorationSpans.IsStrip(xs, ys, at)) continue;
                    var x = DecorationSpans.Respan(xs, at, letters, CapHalf(text, mesh, at));
                    if (TranslatorCore.DebugMode && DiagnosticOnce.First("TmpDecorations.strip", TypeHelper.GetInstanceID(text) + "/" + xs[at] + "/" + xs[at + 10]))
                        TranslatorCore.LogDebug($"[TmpDecorations] strip {xs[at]:F2} -> {xs[at + 10]:F2} in mesh {m} at {at}: " + (x == null ? "left as it is" : $"now {x[0]:F2} -> {x[10]:F2}"));
                    if (x == null) continue;
                    for (int v = 0; v < 12; v++)
                    {
                        var p = (Vector3)EngineCollections.Item(vertices, at + v);
                        p.x = x[v];
                        EngineCollections.SetItem(vertices, at + v, p);
                        xs[at + v] = x[v];
                    }
                    changed = true;
                    at += 8; // the strip's three quads
                }
            }
            return changed;
        }

        /// <summary>
        /// Half the width of the glyph the strip is drawn with, unscaled, as TMP computes its caps:
        /// its first cap spans half the glyph in the atlas (uv0 → uv2 = half the glyph rectangle),
        /// times the atlas width — read off the strip and its material, whatever the TMP version.
        /// The mesh's material where the mesh has one (TMP 1.4 has none): else the text's own, the
        /// primary font's, whose '_' TMP draws every strip with.
        /// </summary>
        private static float CapHalf(object text, object mesh, int at)
        {
            var uvs = TmpLayout.Get(TmpLayout.MiUvs0, mesh);
            var material = (TmpLayout.MiMaterial != null ? TmpLayout.Get(TmpLayout.MiMaterial, mesh) as Material : null)
                           ?? TmpLayout.FontSharedMaterial?.GetValue(text, null) as Material;
            var texture = material != null ? material.mainTexture : null;
            if (uvs == null || texture == null || EngineCollections.Length(uvs) < at + 3) return 0f;
            float u0 = UvX(EngineCollections.Item(uvs, at)), u2 = UvX(EngineCollections.Item(uvs, at + 2));
            return Math.Abs(u2 - u0) * texture.width;
        }

        // uvs0 is Vector2[] in older TMP and Vector4[] in newer: both have x.
        private static float UvX(object uv) =>
            uv is Vector2 v2 ? v2.x : uv is Vector4 v4 ? v4.x : 0f;
    }
}
