using UnityGameTranslator.Core.TextShaping;

namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// The check a translation typed by hand gets WHILE it is typed — one rule for every editor
    /// that writes a line: the in-game text editor and the Failures tab. What is known before
    /// the click is said before the click: the button greys and the line under the field says
    /// which token, how many times.
    ///
    /// ⚠ Two things, in this order. The first is known from the KEY alone, before a character
    /// is typed: a key in the RTL pipeline's presentation forms is its own display output read
    /// back, and nothing typed against it can ever be saved (D8). The second is the text's:
    /// a placeholder dropped or doubled, judged by <see cref="TranslatorCore.ValidateEditedPlaceholders"/>.
    /// </summary>
    public static class EditChecks
    {
        public const string DisplayShapedKey = "this row's key is display-shaped text, not a source text — nothing typed here can be saved";

        /// <summary>Why the field cannot be saved as it stands, or null when it can.</summary>
        public static string Problem(string key, string field, bool changed)
        {
            if (RtlText.ContainsPresentationForms(key)) return DisplayShapedKey;
            return changed ? TranslatorCore.ValidateEditedPlaceholders(key, field ?? "") : null;
        }

        /// <summary>The problem on the line under the field, in red — or the line hidden. True when there is one.</summary>
        public static bool Show(LabelHandle line, string problem)
        {
            if (line == null) return problem != null;
            if (problem == null)
            {
                line.Visible = false;
                return false;
            }
            line.Visible = true;
            line.Tone = Tone.Error;
            line.Show(problem);
            return true;
        }
    }
}
