namespace UnityGameTranslator.Core.TextShaping
{
    /// <summary>
    /// Stage B of the shaping pipeline: codepoints in, glyph-correct codepoints out — nothing
    /// else. No reordering (stage C), no line work (stage D), no component knowledge.
    ///
    /// 🔴 The ONE deliberately replaceable stage, and it sits behind an interface from day one
    /// (decision D1, 06/08 analysis §4 bis): the day an OpenType/GSUB shaper exists for syllabic
    /// scripts, it becomes a second implementation chosen per run — and
    /// <see cref="PresentationFormsShaper"/> is NOT retired then: presentation forms are plain
    /// codepoints, the only technique that traverses engines whose font we do not control.
    /// </summary>
    internal interface ITextShaper
    {
        /// <summary>
        /// Shape one run of text IN PLACE, logical order in, logical order out — only the
        /// codepoints change (contextual forms, ligatures). Main thread only: implementations
        /// may sit on shared buffers.
        /// </summary>
        string Shape(string run);
    }
}
