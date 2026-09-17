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

        /// <summary>How many faults the line under a field names before counting the rest.</summary>
        public const int FaultsShown = 3;

        /// <summary>
        /// A problem line stays a LINE: the first faults and how many more, never the whole list.
        /// A proposal that had dropped sixty placeholders listed sixty sentences under the field
        /// and pushed everything else off the screen (2026-09-17). Three say what kind of thing is
        /// wrong; the count says how much.
        /// </summary>
        public static string Brief(string problem)
        {
            if (string.IsNullOrEmpty(problem)) return problem;
            var faults = problem.Split(new[] { "; " }, System.StringSplitOptions.None);
            if (faults.Length <= FaultsShown) return problem;
            return string.Join("; ", faults, 0, FaultsShown) + $" … and {faults.Length - FaultsShown} more";
        }

        /// <summary>The problem on the line under the field, in red and brief — or the line hidden. True when there is one.</summary>
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
            line.Show(Brief(problem));
            return true;
        }
    }
}
