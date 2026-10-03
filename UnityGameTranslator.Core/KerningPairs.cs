using System;
using System.Collections.Generic;
using System.Reflection;
using UnityGameTranslator.Core.Engine;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Kerning pairs a font asset keeps from its font, put in the asset's units where the engine left
    /// them in the font's (<see cref="KerningUnits"/>: what is wrong, how it is told, measured
    /// 2026-10-03). One postfix on AddPairAdjustmentRecords — TextMesh Pro's TMP_FontAsset and
    /// TextCore's FontAsset (UI Toolkit) carry the same method and the same defect — reading the
    /// pairs it was given against the ones it kept. Seen as letters on top of each other or a gap
    /// inside a word in Hebrew, from the second time a text is laid out (the pairs arrive after the
    /// first). Mono and IL2CPP: every member is reached as a field or the interop's property.
    /// </summary>
    internal static class KerningPairs
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The members of one asset type, resolved once; null when the type lacks one of them.</summary>
        private sealed class Shape
        {
            internal MemberInfo FaceInfo, PointSize, UnitsPerEm, FeatureTable, Lookup, List;
            internal MemberInfo First, Second, GlyphIndex, ValueRecord;
            internal MemberInfo[] Values;   // xPlacement, yPlacement, xAdvance, yAdvance
            internal string Name;
            internal bool? Converts;         // does this asset type convert on storing? Read on a pair with an advance
        }

        private static readonly Dictionary<Type, Shape> _shapes = new Dictionary<Type, Shape>();

        public static int Patch(Action<MethodInfo, MethodInfo, MethodInfo> patcher)
        {
            int placed = 0;
            const BindingFlags hook = BindingFlags.Static | BindingFlags.Public;
            foreach (var name in new[] { "TMPro.TMP_FontAsset", "UnityEngine.TextCore.Text.FontAsset" })
            {
                var type = AssemblyTypes.Find(name);
                if (type == null) continue;
                MethodInfo add = null;
                foreach (var m in type.GetMethods(Any))
                    if (m.Name == "AddPairAdjustmentRecords" && m.GetParameters().Length == 1) add = m;
                if (add == null) continue;   // an engine that takes its pairs another way (TMP before Unity 6): nothing to read
                var shape = Resolve(type, out string missing);
                if (shape == null)
                {
                    TranslatorCore.LogWarning($"[Kerning] {type.Name}.AddPairAdjustmentRecords not checked: {missing} not found");
                    continue;
                }
                lock (_shapes) _shapes[type] = shape;
                patcher(add, typeof(KerningPairs).GetMethod(nameof(Add_Prefix), hook), typeof(KerningPairs).GetMethod(nameof(Add_Postfix), hook));
                placed++;
            }
            if (placed > 0) TranslatorCore.LogInfo($"[Kerning] kerning pairs read against their font as font assets take them ({placed} type(s))");
            return placed;
        }

        private static Shape Resolve(Type asset, out string missing)
        {
            var s = new Shape { Name = asset.Name };
            missing = null;
            if ((s.FaceInfo = Members.FieldOrProperty(asset, "m_FaceInfo", Any)) == null) { missing = "m_FaceInfo"; return null; }
            var face = Members.TypeOf(s.FaceInfo);
            if ((s.PointSize = Members.FieldOrProperty(face, "pointSize", Any)) == null) { missing = "FaceInfo.pointSize"; return null; }
            if ((s.UnitsPerEm = Members.FieldOrProperty(face, "unitsPerEM", Any)) == null) { missing = "FaceInfo.unitsPerEM"; return null; }
            if ((s.FeatureTable = Members.FieldOrProperty(asset, "m_FontFeatureTable", Any)) == null) { missing = "m_FontFeatureTable"; return null; }
            var table = Members.TypeOf(s.FeatureTable);
            if ((s.Lookup = Members.FieldOrProperty(table, "m_GlyphPairAdjustmentRecordLookup", Any)) == null) { missing = "m_GlyphPairAdjustmentRecordLookup"; return null; }
            if ((s.List = Members.FieldOrProperty(table, "m_GlyphPairAdjustmentRecords", Any)) == null) { missing = "m_GlyphPairAdjustmentRecords"; return null; }
            var record = EngineCollections.ElementType(Members.TypeOf(s.List));
            if ((s.First = Members.FieldOrProperty(record, "firstAdjustmentRecord", Any)) == null) { missing = "firstAdjustmentRecord"; return null; }
            if ((s.Second = Members.FieldOrProperty(record, "secondAdjustmentRecord", Any)) == null) { missing = "secondAdjustmentRecord"; return null; }
            var adjustment = Members.TypeOf(s.First);
            if ((s.GlyphIndex = Members.FieldOrProperty(adjustment, "glyphIndex", Any)) == null) { missing = "glyphIndex"; return null; }
            if ((s.ValueRecord = Members.FieldOrProperty(adjustment, "glyphValueRecord", Any)) == null) { missing = "glyphValueRecord"; return null; }
            var value = Members.TypeOf(s.ValueRecord);
            s.Values = new MemberInfo[4];
            string[] names = { "xPlacement", "yPlacement", "xAdvance", "yAdvance" };
            for (int i = 0; i < 4; i++)
                if ((s.Values[i] = Members.FieldOrProperty(value, names[i], Any)) == null) { missing = "GlyphValueRecord." + names[i]; return null; }
            return s;
        }

        /// <summary>How many pairs the asset kept before this call: the ones after are this call's.</summary>
        public static void Add_Prefix(object __instance, out int __state)
        {
            __state = -1;
            try
            {
                var s = ShapeOf(__instance);
                if (s != null) __state = EngineCollections.Length(Members.Get(s.List, Members.Get(s.FeatureTable, __instance)));
            }
            catch (Exception ex) { Faults.Say("KerningPairs.prefix", ex.InnerException ?? ex); }
        }

        /// <summary>The pairs this call kept, each read against the one the font gave, corrected where left in font units.</summary>
        public static void Add_Postfix(object __instance, object __0, int __state)
        {
            if (__state < 0 || __0 == null) return;
            try
            {
                var s = ShapeOf(__instance);
                object table = Members.Get(s.FeatureTable, __instance);
                object list = Members.Get(s.List, table), lookup = Members.Get(s.Lookup, table);
                int kept = EngineCollections.Length(list);
                if (kept <= __state) return;

                object face = Members.Get(s.FaceInfo, __instance);
                float scale = Convert.ToSingle(Members.Get(s.PointSize, face)) / Convert.ToInt32(Members.Get(s.UnitsPerEm, face));

                // The font's values for each pair given, by the asset's own key (second << 16 | first).
                var fromFont = new Dictionary<uint, float[]>();
                int given = EngineCollections.Length(__0);
                for (int i = 0; i < given; i++)
                {
                    object r = EngineCollections.Item(__0, i);
                    uint key = Key(s, r);
                    if (key == 0) break;   // the end of the records the engine filled
                    if (!fromFont.ContainsKey(key)) fromFont[key] = Values(s, r);
                }

                // Whether this asset type converts on storing: read on the first pair with an advance.
                if (s.Converts == null)
                    for (int i = __state; i < kept && s.Converts == null; i++)
                    {
                        object r = EngineCollections.Item(list, i);
                        if (fromFont.TryGetValue(Key(s, r), out var font) && font[KerningUnits.FirstXAdvance] != 0f && scale != 1f)
                            s.Converts = KerningUnits.ConvertedAdvance(scale, font, Values(s, r));
                    }
                if (s.Converts != true) return;

                int corrected = 0;
                for (int i = __state; i < kept; i++)
                {
                    object r = EngineCollections.Item(list, i);
                    uint key = Key(s, r);
                    if (!fromFont.TryGetValue(key, out var font)) continue;
                    var right = KerningUnits.Corrected(scale, font, Values(s, r), true);
                    if (right == null) continue;
                    object fixedRecord = WithValues(s, r, right);
                    EngineCollections.SetItem(list, i, fixedRecord);
                    EngineCollections.SetEntry(lookup, key, fixedRecord);
                    corrected++;
                }
                if (corrected > 0 && DiagnosticOnce.First("Kerning.corrected", s.Name))
                    TranslatorCore.LogInfo($"[Kerning] {s.Name}: kerning pairs kept with placements in font units — converted to the asset's units ({corrected} in the first batch)");
            }
            catch (Exception ex) { Faults.Say("KerningPairs.postfix", ex.InnerException ?? ex); }
        }

        private static Shape ShapeOf(object asset)
        {
            for (var t = asset.GetType(); t != null; t = t.BaseType)
                lock (_shapes)
                    if (_shapes.TryGetValue(t, out var s)) return s;
            return null;
        }

        private static uint Key(Shape s, object record)
        {
            uint first = Convert.ToUInt32(Members.Get(s.GlyphIndex, Members.Get(s.First, record)));
            uint second = Convert.ToUInt32(Members.Get(s.GlyphIndex, Members.Get(s.Second, record)));
            return second << 16 | first;
        }

        /// <summary>The pair's eight values, first glyph then second (KerningUnits' order).</summary>
        private static float[] Values(Shape s, object record)
        {
            var v = new float[KerningUnits.Count];
            object first = Members.Get(s.ValueRecord, Members.Get(s.First, record));
            object second = Members.Get(s.ValueRecord, Members.Get(s.Second, record));
            for (int i = 0; i < 4; i++)
            {
                v[i] = Convert.ToSingle(Members.Get(s.Values[i], first));
                v[i + 4] = Convert.ToSingle(Members.Get(s.Values[i], second));
            }
            return v;
        }

        /// <summary>
        /// The pair with these values — its structs read, changed and written back from the inside out
        /// (each is a copy, boxed on Mono).
        /// </summary>
        private static object WithValues(Shape s, object record, float[] v)
        {
            object first = Members.Get(s.First, record), second = Members.Get(s.Second, record);
            object firstValue = Members.Get(s.ValueRecord, first), secondValue = Members.Get(s.ValueRecord, second);
            for (int i = 0; i < 4; i++)
            {
                Members.Set(s.Values[i], firstValue, v[i]);
                Members.Set(s.Values[i], secondValue, v[i + 4]);
            }
            Members.Set(s.ValueRecord, first, firstValue);
            Members.Set(s.ValueRecord, second, secondValue);
            Members.Set(s.First, record, first);
            Members.Set(s.Second, record, second);
            return record;
        }
    }
}
