using System;
using UnityGameTranslator.Common;

namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// The one hook after TMP built a text's mesh (GenerateTextMesh, TextMeshProUGUI and TextMeshPro):
    /// what the mod moves in TMP's geometry, in order — a presented field's glyphs into visual order
    /// (RtlInputFields), then the underlines and strikethroughs over their letters as finally placed
    /// (TmpDecorations) — and ONE upload of the vertices for all of it.
    /// </summary>
    internal static class TmpMeshDone
    {
        public static void Tmp_GenerateTextMesh_Postfix(object __instance)
        {
            long tLayout = Perf.Start();
            try
            {
                var shift = RtlInputFields.GenerateTextMeshDone(__instance);
                bool respanned = false;
                try { respanned = TmpDecorations.Respan(__instance, shift); }
                catch (Exception ex) { Faults.Say("TmpDecorations.Respan", ex); }
                if ((shift != null || respanned) && TmpLayout.UpdateVertexData != null)
                    TmpLayout.UpdateVertexData.Invoke(__instance, null);
            }
            finally { Perf.Stop(Perf.TmpLayout, tLayout); }
        }
    }
}
