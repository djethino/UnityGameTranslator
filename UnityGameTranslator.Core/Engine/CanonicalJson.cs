using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Writing a piece of JSON the same way every time, so the same content fingerprints the same.
    ///
    /// 🔴 **What it decides is whether somebody's work is recognised as theirs.** The fingerprint
    /// it feeds answers "is this still the file I copied" across a change of lineage — which is
    /// exactly what a fork is. Unstable, the answer flips for no reason: somebody who reworked a
    /// translation's fonts and images is told they have changed nothing and the one button they
    /// came for is greyed out, or the reverse — an untouched copy is announced as original work.
    ///
    /// 🔴 **Property names sorted, list order kept, and the difference is not a taste.** A settings
    /// section is rebuilt from dictionaries whose enumeration order no runtime promises, so writing
    /// it in the order it happens to come out makes the fingerprint change while the content does
    /// not. A list of font rules is applied IN SEQUENCE — first match wins — so its order IS
    /// content, and sorting it would call two different setups the same.
    ///
    /// ⚠ **Never compared across languages, and that is what makes it affordable.** It answers a
    /// question only this side asks; the website has its own way of answering its own. So numbers
    /// and text may be written however this runtime writes them, with no byte agreement to
    /// maintain — a size multiplier of 1.0 alone would break one.
    ///
    /// ⚠ The hash the SERVER compares is a different thing entirely and lives in the socle
    /// (`ContentHash.Of`), where the website's rules are followed to the byte.
    ///
    /// ⚠ **Pure by contract**: a parsed token in, one string out. No Unity, no disk, no clock.
    ///
    /// Cut out of TranslatorCore on 2026-09-08 (step 6 of analyse/plan-prealables-couches.md).
    /// </summary>
    public static class CanonicalJson
    {
        /// <summary>One JSON token, written the same way every time.</summary>
        public static string Of(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null) return "null";

            var obj = token as JObject;
            if (obj != null)
            {
                // Sorted: what a dictionary hands over is not a promise, and the fingerprint must
                // not move because a section was rebuilt in a different order.
                var names = new List<string>();
                foreach (var property in obj.Properties()) names.Add(property.Name);
                names.Sort(System.StringComparer.Ordinal);

                var sb = new StringBuilder("{");
                for (int i = 0; i < names.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonConvert.ToString(names[i])).Append(':').Append(Of(obj[names[i]]));
                }
                return sb.Append('}').ToString();
            }

            var array = token as JArray;
            if (array != null)
            {
                // NOT sorted: a rule list is applied in sequence, so its order is content.
                var sb = new StringBuilder("[");
                for (int i = 0; i < array.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(Of(array[i]));
                }
                return sb.Append(']').ToString();
            }

            var value = token as JValue;
            return value == null ? "null" : JsonConvert.ToString(value.Value);
        }
    }
}
