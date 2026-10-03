using System;
using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// NGUI's [codes] read as NGUI reads them (NGUIText.ParseSymbol, the open-source NGUI of
    /// 2025-10), and a right-to-left line composed with them as structure: each code wrapped around
    /// the words it styled, never travelling with the words (bench, real NGUI, 2026-10-03).
    /// </summary>
    internal static class NguiMarkupChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            foreach (var code in new[] { "[b]", "[/b]", "[I]", "[/u]", "[s]", "[c]", "[/c]", "[-]", "[FF0000]", "[ff0000aa]", "[80]", "[sub]", "[/sup]", "[sub=0.5]", "[y=0.75]", "[/y]", "[url=http://x]", "[/url]" })
                check(NguiMarkup.LengthAt(code + "x", 0) == code.Length, "an NGUI code: " + code, NguiMarkup.LengthAt(code + "x", 0).ToString());
            foreach (var text in new[] { "[1]", "[x]", "[ag]", "[FF00]", "[b", "[GG0000]", "[url=x", "[y=a]", "[]" })
                check(NguiMarkup.LengthAt(text, 0) == 0, "not an NGUI code: " + text, "text, as NGUI shows it");

            check(NguiMarkup.PairName("[b]") == "b" && NguiMarkup.PairName("[/B]") == "b", "[b] pairs with [/b]", "");
            check(NguiMarkup.PairName("[FF0000]") == "color" && NguiMarkup.PairName("[-]") == "color" && NguiMarkup.IsClosing("[-]"),
                "a colour pairs with [-]", "");
            check(NguiMarkup.PairName("[url=x]") == "url" && NguiMarkup.PairName("[/url]") == "url", "[url=…] pairs with [/url]", "");
            check(NguiMarkup.PairName("[80]") == null, "an alpha closes nothing", "");

            const string logical = "זה [b]הכוח[/b] של [u]הקמעות[/u] [00FF00]הגדולות[-].";
            string visual;
            using (RtlComposer.UseMarkup(MarkupSyntax.NguiCodes))
                visual = RtlComposer.Compose(logical, RtlOutput.VisualOrder);
            check(visual == ".[00FF00]תולודגה[-] [u]תועמקה[/u] לש [b]חוכה[/b] הז", "codes wrapped around their words in visual order", visual);

            string asText;
            using (RtlComposer.UseMarkup(MarkupSyntax.None))
                asText = RtlComposer.Compose("זה [b]הכוח", RtlOutput.VisualOrder);
            check(asText == "חוכה[b] הז", "encoding off: the brackets are text, laid out with it", asText);
            check(RtlComposer.Compose("זה <b>הכוח</b>", RtlOutput.VisualOrder) == "<b>חוכה</b> הז", "outside a scope, tags are angle tags again", "");
        }
    }
}
