using System;
using Newtonsoft.Json;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The JSON library on a runtime that refuses to emit methods (Core/JsonRuntime, issue #29): the
    /// config was never read and the setup wizard came back at every launch.
    ///
    /// ⚠ What this can hold from here: the library's switch is still where JsonRuntime reaches for it,
    /// and objects still go through JSON with it off. The refusal itself only exists in the game's
    /// runtime; this machine lets methods be emitted.
    /// </summary>
    internal static class JsonRuntimeChecks
    {
        private sealed class Sample
        {
            public string Name { get; set; }
            public int Count;
        }

        public static void Run(Action<bool, string, string> check)
        {
            var @switch = JsonRuntime.Switch();
            check(@switch != null,
                "Newtonsoft.Json still has the switch JsonRuntime turns off",
                "an update that renames it would leave every object unreadable on a runtime that refuses to emit methods — only a log line would say so");
            if (@switch == null) return;

            object before = @switch.GetValue(null);
            try
            {
                @switch.SetValue(null, (bool?)false);
                string json = JsonConvert.SerializeObject(new Sample { Name = "x", Count = 2 });
                var back = JsonConvert.DeserializeObject<Sample>(json);
                check(back != null && back.Name == "x" && back.Count == 2,
                    "an object goes through JSON with the switch off",
                    $"the reflection path must give the same object — got: {json}");
            }
            finally { @switch.SetValue(null, before); }
        }
    }
}
