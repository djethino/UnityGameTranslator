using System;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// ⚠ TEMPORARY bench probe — branch feature/text-shaping only, must NOT ship in a release.
    ///
    /// Answers the in-game unknowns listed in analyse/issue-24-rtl-second-look.md §7.3 before any
    /// shaping code is written: what does this game's TMP/TMProOld actually do with
    /// <c>isRightToLeftText</c> (joining is our job, but wrapping, line order and alignment are
    /// TMP's), and does the game re-assert the text behind our back.
    ///
    /// Ctrl+F9 cycles one visible text component through fixed Arabic strings pre-shaped OUTSIDE
    /// the mod (arabic_reshaper + python-bidi, see the analyse doc) — the probe contains no
    /// shaping logic on purpose: it tests the ENGINE, not us.
    ///
    /// 🔴 Refuses to run while translations are active: the scanner would read the probe text,
    /// queue it to the AI and write it into translations.json — exactly what decision D8 forbids
    /// (no shaped form may ever reach disk or server).
    /// </summary>
    internal static class RtlProbe
    {
        // "مرحبا بكم في عالم الترجمة" — Logical = raw codepoints (control: what players get today).
        // Visual = presentation forms in visual order (what an engine with NO RTL support needs).
        // TmpMode = presentation forms, logical order, LTR runs reversed (what TMP wants together
        // with isRightToLeftText = true — the RTLTMPro recipe).
        private const string ShortLogical = "مرحبا بكم في عالم الترجمة";
        private const string ShortVisual = "ﺔﻤﺟﺮﺘﻟﺍ ﻢﻟﺎﻋ ﻲﻓ ﻢﻜﺑ ﺎﺒﺣﺮﻣ";
        private const string ShortTmpMode = "ﻣﺮﺣﺒﺎ ﺑﻜﻢ ﻓﻲ ﻋﺎﻟﻢ ﺍﻟﺘﺮﺟﻤﺔ";

        // "الإصدار 123 من ABC جاهز الآن" — digits and Latin must read forward on screen.
        private const string MixedTmpMode = "ﺍﻹﺻﺪﺍﺭ 321 ﻣﻦ CBA ﺟﺎﻫﺰ ﺍﻵﻥ";

        // Long paragraph, TmpMode — forces automatic wrapping: line ORDER and BREAK POINTS are the
        // whole question (a naive pre-reversed string stacks its lines bottom-up).
        private const string LongTmpMode = "ﻣﺮﺣﺒﺎ ﺑﻜﻢ ﻓﻲ ﻋﺎﻟﻢ ﺍﻟﺘﺮﺟﻤﺔ. ﻫﺬﻩ ﻓﻘﺮﺓ ﻃﻮﻳﻠﺔ ﻛﺘﺒﺖ ﻻﺧﺘﺒﺎﺭ ﺍﻻﻧﺘﻘﺎﻝ ﺍﻟﺘﻠﻘﺎﺋﻲ ﺇﻟﻰ ﺍﻟﺴﻄﺮ ﺍﻟﺘﺎﻟﻲ ﻭﺗﺮﺗﻴﺐ ﺍﻷﺳﻄﺮ ﻋﻨﺪ ﺍﻟﻌﺮﺽ ﻣﻦ ﺍﻟﻴﻤﻴﻦ ﺇﻟﻰ ﺍﻟﻴﺴﺎﺭ ﺩﺍﺧﻞ ﺍﻟﻠﻌﺒﺔ.";

        private static int _step = -1;
        private static Component _target;
        private static string _originalText;
        private static bool? _originalRtl;

        /// <summary>Ctrl+F9 — advance the TMP/TMProOld probe by one step.</summary>
        internal static void Cycle()
        {
            if (TranslatorCore.TranslationsActive)
            {
                TranslatorCore.LogWarning("[RtlProbe] REFUSED: translations are enabled. Turn them off " +
                    "(main panel switch) first — otherwise the scanner queues the probe text to the AI " +
                    "and writes it into translations.json.");
                return;
            }

            try
            {
                if (_target == null || (_target is UnityEngine.Object uo && uo == null))
                {
                    _step = -1;
                    _target = null;
                }

                if (_target == null)
                {
                    _target = FindTarget();
                    if (_target == null)
                    {
                        TranslatorCore.LogWarning("[RtlProbe] No suitable text component found in this scene (visible, 2-80 chars, single line).");
                        return;
                    }
                    _originalText = TypeHelper.GetText(_target);
                    _originalRtl = GetRtl(_target);
                    TranslatorCore.LogInfo($"[RtlProbe] Target acquired: id={TypeHelper.GetInstanceID(_target)} type={_target.GetType().Name} " +
                        $"path='{TranslatorCore.GetGameObjectPath(_target.gameObject)}' rtlProp={(_originalRtl.HasValue ? _originalRtl.Value.ToString() : "ABSENT")} " +
                        $"original='{Preview(_originalText)}'");
                }

                _step = _step + 1;
                switch (_step)
                {
                    case 0:
                        Apply(ShortLogical, rtl: false,
                            "0/5 RAW LOGICAL — expected BROKEN: isolated letters, left-to-right (today's bug, the control case)");
                        break;
                    case 1:
                        Apply(ShortVisual, rtl: false,
                            "1/5 SHAPED VISUAL, no RTL flag — expected: joined and readable on ONE line (what legacy engines would get)");
                        break;
                    case 2:
                        Apply(ShortTmpMode, rtl: true,
                            "2/5 SHAPED LOGICAL + isRightToLeftText — expected: joined, right-to-left, identical to step 1 on screen");
                        break;
                    case 3:
                        Apply(MixedTmpMode, rtl: true,
                            "3/5 MIXED + isRightToLeftText — expected: '123' and 'ABC' read FORWARD inside the RTL sentence");
                        break;
                    case 4:
                        Apply(LongTmpMode, rtl: true,
                            "4/5 LONG + isRightToLeftText — expected: auto-wrap, FIRST words of the sentence on the TOP line, no reversed line stack");
                        break;
                    default:
                        Restore();
                        break;
                }
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[RtlProbe] {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>Ctrl+Shift+F9 — UI Toolkit / ATG probe (Unity 6 games).</summary>
        internal static void CycleUIToolkit()
        {
            if (TranslatorCore.TranslationsActive)
            {
                TranslatorCore.LogWarning("[RtlProbe] REFUSED (UI Toolkit): turn translations off first — see Ctrl+F9 message.");
                return;
            }
            UIToolkitSupport.ProbeAtgCycle(ShortLogical, LongLogical());
        }

        // The logical long string, built from the TmpMode constant's source sentence — kept in one
        // place: for ATG the LOGICAL text is what gets assigned (shaping is the engine's job there).
        private static string LongLogical() =>
            "مرحبا بكم في عالم الترجمة. هذه فقرة طويلة كتبت لاختبار الانتقال التلقائي إلى السطر التالي وترتيب الأسطر عند العرض من اليمين إلى اليسار داخل اللعبة.";

        private static void Apply(string text, bool rtl, string what)
        {
            bool rtlSet = SetRtl(_target, rtl);
            TranslatorPatches.BypassTextPrefix = true;
            try { TypeHelper.SetText(_target, text); }
            finally { TranslatorPatches.BypassTextPrefix = false; }
            try { TypeHelper.ForceMeshUpdate(_target); } catch { }

            string back = null;
            try { back = TypeHelper.GetText(_target); } catch { }
            TranslatorCore.LogInfo($"[RtlProbe] step {what}");
            TranslatorCore.LogInfo($"[RtlProbe]   rtl asked={rtl} propSet={rtlSet} propNow={GetRtl(_target)?.ToString() ?? "ABSENT"} " +
                $"readbackMatches={back == text} lineCount={GetLineCount(_target)?.ToString() ?? "?"}");
        }

        private static void Restore()
        {
            if (_target != null)
            {
                if (_originalRtl.HasValue) SetRtl(_target, _originalRtl.Value);
                TranslatorPatches.BypassTextPrefix = true;
                try { TypeHelper.SetText(_target, _originalText ?? ""); }
                finally { TranslatorPatches.BypassTextPrefix = false; }
                try { TypeHelper.ForceMeshUpdate(_target); } catch { }
                TranslatorCore.LogInfo("[RtlProbe] 5/5 restored original text and flag — press Ctrl+F9 again to probe another component.");
            }
            _target = null;
            _originalText = null;
            _originalRtl = null;
            _step = -1;
        }

        private static Component FindTarget()
        {
            if (TypeHelper.TMP_TextType == null)
            {
                TranslatorCore.LogWarning("[RtlProbe] No TMP type in this game — this probe covers TMP/TMProOld only.");
                return null;
            }
            foreach (var obj in TypeHelper.FindAllObjectsOfType(TypeHelper.TMP_TextType))
            {
                var comp = obj as Component ?? TypeHelper.Il2CppCast(obj, typeof(Component)) as Component;
                if (comp == null) continue;
                try
                {
                    if (!comp.gameObject.activeInHierarchy) continue;
                    if (TranslatorCore.IsOwnUI(comp)) continue;
                    string text = TypeHelper.GetText(comp);
                    if (string.IsNullOrEmpty(text)) continue;
                    if (text.Length < 2 || text.Length > 80) continue;
                    if (text.IndexOf('\n') >= 0) continue;
                    return comp;
                }
                catch { }
            }
            return null;
        }

        private static PropertyInfo RtlProp(Component comp) =>
            comp?.GetType().GetProperty("isRightToLeftText", BindingFlags.Public | BindingFlags.Instance);

        private static bool? GetRtl(Component comp)
        {
            try
            {
                var p = RtlProp(comp);
                if (p?.GetMethod == null) return null;
                return (bool)p.GetValue(comp, null);
            }
            catch { return null; }
        }

        private static bool SetRtl(Component comp, bool value)
        {
            try
            {
                var p = RtlProp(comp);
                if (p?.SetMethod == null) return false;
                p.SetValue(comp, value, null);
                return true;
            }
            catch (Exception ex)
            {
                TranslatorCore.LogWarning($"[RtlProbe] isRightToLeftText setter threw: {ex.Message}");
                return false;
            }
        }

        /// <summary>textInfo.lineCount via reflection — proves the wrap actually happened.</summary>
        private static int? GetLineCount(Component comp)
        {
            try
            {
                var tiProp = comp.GetType().GetProperty("textInfo", BindingFlags.Public | BindingFlags.Instance);
                var ti = tiProp?.GetValue(comp, null);
                if (ti == null) return null;
                var f = ti.GetType().GetField("lineCount", BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return (int)f.GetValue(ti);
                var p = ti.GetType().GetProperty("lineCount", BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return (int)p.GetValue(ti, null);
            }
            catch { }
            return null;
        }

        private static string Preview(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length > 40 ? s.Substring(0, 40) + "..." : s);
    }
}
