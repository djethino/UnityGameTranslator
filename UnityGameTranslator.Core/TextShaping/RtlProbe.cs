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
    /// 🔴 D8 (no shaped form may ever reach disk or server) is held by REGISTRATION, not by a
    /// ban: every probe string is registered in the read-back index up front, so the scanner and
    /// every gate treat them as our own output and never queue them. Translations stay ON — they
    /// must: font replacement lives inside the same pipeline, and a TMP game whose font lacks
    /// Arabic needs its fallback active for the probe to show anything but squares.
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
        private const string MixedVisual = "ﻥﻵﺍ ﺰﻫﺎﺟ ABC ﻦﻣ 123 ﺭﺍﺪﺻﻹﺍ";

        // Long paragraph in VISUAL order — for engines with no RTL flag. On a multi-line component
        // this is EXPECTED to break (wrong break points, lines stacked in reverse reading order):
        // demonstrating that is the point, it is what the generator-readback emission must fix.
        private const string LongVisual = ".ﺔﺒﻌﻠﻟﺍ ﻞﺧﺍﺩ ﺭﺎﺴﻴﻟﺍ ﻰﻟﺇ ﻦﻴﻤﻴﻟﺍ ﻦﻣ ﺽﺮﻌﻟﺍ ﺪﻨﻋ ﺮﻄﺳﻷﺍ ﺐﻴﺗﺮﺗﻭ ﻲﻟﺎﺘﻟﺍ ﺮﻄﺴﻟﺍ ﻰﻟﺇ ﻲﺋﺎﻘﻠﺘﻟﺍ ﻝﺎﻘﺘﻧﻻﺍ ﺭﺎﺒﺘﺧﻻ ﺖﺒﺘﻛ ﺔﻠﻳﻮﻃ ﺓﺮﻘﻓ ﻩﺬﻫ .ﺔﻤﺟﺮﺘﻟﺍ ﻢﻟﺎﻋ ﻲﻓ ﻢﻜﺑ ﺎﺒﺣﺮﻣ";

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
        private static bool _rtlCapable;   // isRightToLeftText exists on the target
        private static bool _uitkCycle;

        /// <summary>Ctrl+F9 — probe the text under the mouse cursor; each press = one step.</summary>
        internal static void Cycle()
        {
            EnsureProbeTextsRegistered();

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
                    // The one engine the cursor cannot reach on the bench: TMProOld lives in
                    // gameplay screens whose text does not answer the raycast (tried and failed
                    // with the panel cursor). Fall back to the longest TMProOld text on screen —
                    // during a dialogue that IS the dialogue line — and NAME it in the log so the
                    // tester can confirm which text changed.
                    comp = PickAlternateTmpFallback(out string fbPath, out string fbPreview);
                    if (comp != null)
                    {
                        enginePicked = "TMProOld, fallback: longest on-screen text (no cursor hit)";
                        TranslatorCore.LogInfo($"[RtlProbe] TMProOld fallback target: path='{fbPath}' text='{fbPreview}'");
                    }
                }
                if (comp == null)
                {
                    TranslatorCore.LogWarning("[RtlProbe] No text under the cursor (and no TMProOld text " +
                        "on screen). Point the mouse at a visible text (uGUI or UI Toolkit) and press " +
                        "Ctrl+F9 again. World-space TextMesh cannot be picked this way.");
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
                _rtlCapable = RtlProp(comp) != null;
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
                    // Engine-aware: a component without the RTL flag has nothing to do with the
                    // TMP-mode string (proved on the bench: it reads backwards) — it gets the
                    // VISUAL form, which is what our emission would actually feed it.
                    if (_rtlCapable)
                        Apply(MixedTmpMode, rtl: true,
                            "4/6 MIXED + isRightToLeftText — expected on TMP: '123' and 'ABC' read FORWARD inside the RTL sentence");
                    else
                        Apply(MixedVisual, rtl: false,
                            "4/6 MIXED VISUAL (no RTL flag on this engine) — expected: '123' and 'ABC' read FORWARD inside the RTL sentence");
                    break;
                case 4:
                    if (_rtlCapable)
                        Apply(LongTmpMode, rtl: true,
                            "5/6 LONG + isRightToLeftText — expected on TMP: auto-wrap, FIRST words of the sentence on the TOP line, no reversed line stack");
                    else
                        Apply(LongVisual, rtl: false,
                            "5/6 LONG VISUAL (no RTL flag) — single line: correct. Multi-line: EXPECTED broken (wrong breaks, reversed line order) — that is what the generator-readback emission will fix. A best-fit component shrinks instead of wrapping: also worth noting");
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

        private static bool _registered;

        /// <summary>
        /// Register every probe string in the read-back index BEFORE the first one is shown: the
        /// gates then treat them as our own output — never queued to the AI, never written to
        /// translations.json (D8) — while translations and font replacement stay on.
        /// </summary>
        private static void EnsureProbeTextsRegistered()
        {
            if (_registered) return;
            _registered = true;
            foreach (var s in new[] { ShortLogical, ShortVisual, ShortTmpMode, MixedTmpMode,
                                      MixedVisual, LongTmpMode, LongVisual, LongLogical })
                TranslatorCore.RegisterPresentedText(s);
        }

        /// <summary>
        /// Longest live TMProOld text — the dialogue line, when one is on screen.
        ///
        /// ⚠ Only in a TMProOld game (<see cref="TypeHelper.UseAlternateTMP"/>), and matched on
        /// the "TMP" engine label: there, TMProOld IS the main TMP type the scanner registers —
        /// the first version filtered on "TMP (alt)", a label such a game never produces, and
        /// reported "no TMProOld text on screen" in front of a screen full of them.
        /// </summary>
        private static Component PickAlternateTmpFallback(out string path, out string preview)
        {
            path = null;
            preview = null;
            if (!TypeHelper.UseAlternateTMP) return null;
            TextTarget best = null;
            bool bestVisible = false;
            foreach (var t in TextTargets.All())
            {
                if (t?.Text == null || t.Engine == null) continue;
                if (!t.Engine.StartsWith("TMP", StringComparison.Ordinal)) continue;
                var comp = t.Owner as Component;
                if (comp == null) continue;
                if (t.Text.Length < 2) continue;

                // A renderer some camera actually draws beats any longer text that nothing shows:
                // the first pick landed on a quest description pane that no reachable screen
                // displays, and the tester rightly saw nothing change. activeInHierarchy alone
                // proved insufficient — alive is not shown.
                bool active = false, visible = false;
                try
                {
                    active = comp.gameObject.activeInHierarchy;
                    // Generic single-parameter form: safe on IL2CPP (same reasoning as the
                    // GetComponentInParent<Canvas>() precedent), and the build check enforces it.
                    var r = comp.GetComponent<Renderer>();
                    visible = r != null && r.enabled && r.isVisible;
                }
                catch { }
                if (!active) continue;

                if (best == null
                    || (visible && !bestVisible)
                    || (visible == bestVisible && t.Text.Length > best.Text.Length))
                {
                    best = t;
                    bestVisible = visible;
                }
            }
            if (best == null) return null;
            path = best.Path + (bestVisible ? "  [renderer visible]" : "  [⚠ no visible renderer found — may be off-screen]");
            preview = Preview(best.Text);
            return best.Owner as Component;
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
