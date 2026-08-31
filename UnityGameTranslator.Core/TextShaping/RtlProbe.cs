using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// ⚠ TEMPORARY bench probe — branch feature/text-shaping only, must NOT ship in a release.
    ///
    /// Answers the in-game unknowns listed in analyse/issue-24-rtl-second-look.md §7.3 before any
    /// shaping code is written: what does each engine actually do with RTL text —
    /// <c>isRightToLeftText</c> on TMP/TMProOld (joining is our job, but wrapping, line order and
    /// alignment are the engine's), nothing at all on UI.Text, the Advanced Text Generator on
    /// UI Toolkit.
    ///
    /// 🔴 The TESTER chooses the target: Ctrl+F9 probes the text UNDER THE MOUSE CURSOR, through
    /// the same picking the inspector uses (GraphicRaycaster for uGUI, panel Pick for UI Toolkit),
    /// resolved to a text component via TextTargets — every engine, one enumeration. An auto-pick
    /// was tried first and rejected: a component chosen by the probe can be off-screen or covered,
    /// and then nobody can say what the screen should show.
    ///
    /// Each press advances one step; strings are pre-shaped OUTSIDE the mod (arabic_reshaper +
    /// python-bidi, see the analyse doc) — the probe contains no shaping logic on purpose: it
    /// tests the ENGINE, not us.
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

        // The logical long paragraph — for ATG the LOGICAL text is what gets assigned (shaping is
        // the engine's job there).
        private const string LongLogical = "مرحبا بكم في عالم الترجمة. هذه فقرة طويلة كتبت لاختبار الانتقال التلقائي إلى السطر التالي وترتيب الأسطر عند العرض من اليمين إلى اليسار داخل اللعبة.";

        private static int _step = -1;
        private static Component _target;
        private static string _originalText;
        private static bool? _originalRtl;
        private static bool _uitkCycle;

        /// <summary>Ctrl+F9 — probe the text under the mouse cursor; each press = one step.</summary>
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
                // A UI Toolkit cycle in flight — the element lives inside UIToolkitSupport.
                if (_uitkCycle)
                {
                    _uitkCycle = UIToolkitSupport.ProbeAtgCycle(null, ShortLogical, LongLogical);
                    return;
                }

                // A component cycle in flight.
                if (_target is UnityEngine.Object uo && uo == null) { _target = null; _step = -1; }
                if (_target != null)
                {
                    Advance();
                    return;
                }

                // No cycle — acquire whatever text is under the cursor.
                Vector3 mousePos = UniverseLib.Input.InputManager.MousePosition;
                var comp = PickComponentUnderCursor(mousePos, out string enginePicked, out object uitkElement);

                if (uitkElement != null)
                {
                    _uitkCycle = UIToolkitSupport.ProbeAtgCycle(uitkElement, ShortLogical, LongLogical);
                    return;
                }
                if (comp == null)
                {
                    TranslatorCore.LogWarning("[RtlProbe] No text under the cursor. Point the mouse at a " +
                        "visible text (uGUI or UI Toolkit) and press Ctrl+F9 again. World-space TextMesh " +
                        "cannot be picked this way.");
                    return;
                }
                if (TranslatorCore.IsOwnUI(comp))
                {
                    TranslatorCore.LogWarning("[RtlProbe] That is the mod's own UI — point at a GAME text.");
                    return;
                }

                _target = comp;
                _originalText = TypeHelper.GetText(comp);
                _originalRtl = GetRtl(comp);
                TranslatorCore.LogInfo($"[RtlProbe] Target acquired under cursor: engine={enginePicked} " +
                    $"id={TypeHelper.GetInstanceID(comp)} type={comp.GetType().Name} " +
                    $"path='{TranslatorCore.GetGameObjectPath(comp.gameObject)}' " +
                    $"rtlProp={(_originalRtl.HasValue ? _originalRtl.Value.ToString() : "ABSENT")} " +
                    $"original='{Preview(_originalText)}'");
                Advance();
            }
            catch (Exception ex)
            {
                TranslatorCore.LogError($"[RtlProbe] {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void Advance()
        {
            _step++;
            switch (_step)
            {
                case 0:
                    Apply(ShortLogical, rtl: false,
                        "1/6 RAW LOGICAL — expected BROKEN: isolated letters, left-to-right (today's bug, the control case)");
                    break;
                case 1:
                    Apply(ShortVisual, rtl: false,
                        "2/6 SHAPED VISUAL, no RTL flag — expected: joined and readable on ONE line (what legacy engines would get)");
                    break;
                case 2:
                    Apply(ShortTmpMode, rtl: true,
                        "3/6 SHAPED LOGICAL + isRightToLeftText — expected on TMP: joined, right-to-left, identical to step 2 on screen. On UI.Text the flag logs ABSENT and the text reads backwards — that is the answer, not a failure");
                    break;
                case 3:
                    Apply(MixedTmpMode, rtl: true,
                        "4/6 MIXED + isRightToLeftText — expected on TMP: '123' and 'ABC' read FORWARD inside the RTL sentence");
                    break;
                case 4:
                    Apply(LongTmpMode, rtl: true,
                        "5/6 LONG + isRightToLeftText — expected on TMP: auto-wrap, FIRST words of the sentence on the TOP line, no reversed line stack");
                    break;
                default:
                    Restore();
                    break;
            }
        }

        /// <summary>
        /// The text component under the cursor: the inspector's raycast gives the hit GameObject,
        /// TextTargets resolves it (or a close parent) to a text component of whatever engine.
        /// A UI Toolkit interface answers through PickAt instead — returned via uitkElement.
        /// </summary>
        private static Component PickComponentUnderCursor(Vector3 mousePos, out string engine, out object uitkElement)
        {
            engine = null;
            uitkElement = null;

            var inspector = UI.TranslatorUIManager.InspectorPanel;
            GameObject hit = inspector != null ? inspector.ProbeRaycastAt(mousePos) : null;

            if (hit == null)
            {
                uitkElement = UIToolkitSupport.PickAt(mousePos, out _);
                return null;
            }

            var targets = TextTargets.All();
            string path = TranslatorCore.GetGameObjectPath(hit);

            // The hit is often a container or a background Image: try the hit's own subtree first,
            // then climb — the label usually sits beside or above what the raycast returns.
            for (int climb = 0; climb < 4 && !string.IsNullOrEmpty(path); climb++)
            {
                TextTarget best = null;
                foreach (var t in targets)
                {
                    if (t?.Path == null || !(t.Owner is Component)) continue;
                    if (t.Path != path && !t.Path.StartsWith(path + "/", StringComparison.Ordinal)) continue;
                    if (string.IsNullOrEmpty(t.Text) || t.Text.Length < 2 || t.Text.Length > 200) continue;
                    if (best == null || t.Path.Length < best.Path.Length) best = t;
                }
                if (best != null)
                {
                    engine = best.Engine;
                    return best.Owner as Component;
                }
                int cut = path.LastIndexOf('/');
                path = cut > 0 ? path.Substring(0, cut) : null;
            }

            TranslatorCore.LogInfo($"[RtlProbe] Hit '{TranslatorCore.GetGameObjectPath(hit)}' but no text " +
                $"component resolved around it ({targets.Count} text target(s) in scene).");
            return null;
        }

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
                TranslatorCore.LogInfo("[RtlProbe] 6/6 restored original text and flag — point at another text and press Ctrl+F9 to probe it.");
            }
            _target = null;
            _originalText = null;
            _originalRtl = null;
            _step = -1;
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
