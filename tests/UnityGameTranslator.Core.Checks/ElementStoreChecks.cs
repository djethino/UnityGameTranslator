using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityGameTranslator.Core;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// The heads of resumed reveals beside the translation, across a launch (Engine/ElementStore,
    /// section `heads`; TextRouter.Heads): proved in one session, written the moment they are, read back by the next
    /// one — which then holds the text on that component without paying a request to learn it
    /// again. Replayed on a real folder with two routers, one per launch: the moment a file exists
    /// or does not is the whole point.
    /// </summary>
    internal static class ElementStoreChecks
    {
        private const string Head = "The river runs red tonight, and";
        private const string Whole = "The river runs red tonight, and nobody knows why.";

        public static void Run(Action<bool, string, string> check)
        {
            string folder = Path.Combine(Path.GetTempPath(), "ugt-heads-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                string translation = Path.Combine(folder, "translations.json");
                var store = new ElementStore(translation);
                check(store.Path == translation + ".elements", "the file sits beside the translation, by its name", "a companion the uninstall sweep and the eye both find");
                check(store.LoadHeads().Count == 0, "no file reads as nothing", "a first launch knows no head");

                // ── First launch: the recording resumes, the head is proved and written ──
                var first = Launch(store);
                Play(first.Host, first.Box, 0f);
                check(first.Host.Withdrawn.SequenceEqual(new[] { Head }), "the first launch proves the head",
                      "sent once, taken back when the reveal goes on — the one request a finding costs");
                check(File.Exists(store.Path) && new ElementStore(translation).LoadHeads().Count == 1,
                      "and it is written the moment it is proved",
                      "a game closed right after would otherwise pay the same request at the next launch");

                // ── Second launch: read back, held on that component, no request ──
                var second = Launch(store);
                second.Host.GameWrites(second.Box, Head);
                second.Host.Now = 1.0f; second.Host.Frame = 60;
                second.Router.ProcessStabilizedTypewriting();
                check(second.Host.Queued.Count == 0 && second.Host.Withdrawn.Count == 0,
                      "the next launch holds it without a request",
                      "🔴 what the file is for: without it, every launch pays a request to learn the same thing again");

                // ── On another component, the same characters are a text of their own ──
                var label = new ReplayBox { Id = 2 };
                second.Host.Now = 3.0f; second.Host.Frame = 180;
                second.Host.GameWrites(label, Head);
                second.Host.Now = 4.0f; second.Host.Frame = 240;
                second.Router.ProcessStabilizedTypewriting();
                check(second.Host.Queued.Select(q => q.Text).SequenceEqual(new[] { Head }),
                      "the same text on another component is sent",
                      "🔴 a head is a fact about one place: refused by its text, a whole label elsewhere would stay in the source language");

                // ── Held, then replaced without going on: the finding goes, and the file follows ──
                second.Host.Now = 6.0f; second.Host.Frame = 360;
                second.Host.GameWrites(second.Box, "Something else entirely");
                check(!File.Exists(store.Path),
                      "a finding the component contradicts is dropped from the file",
                      "a game updated since, or a sibling at the same place: corrected after one showing, and not read back at the next launch");

                // ── A section this build does not own is written back as it was read ──
                File.WriteAllText(store.Path, "{\"habits\": {\"x\": 1}, \"heads\": [{\"place\": \"box1\", \"text\": \"T\"}]}");
                var mixed = new ElementStore(translation);
                bool readBoth = mixed.LoadHeads().Count == 1;
                mixed.SaveHeads(new List<KeyValuePair<string, string>>());
                string kept = File.Exists(store.Path) ? File.ReadAllText(store.Path) : "";
                check(readBoth && kept.Contains("\"habits\"") && !kept.Contains("\"heads\""),
                      "a section this build does not know is kept, the heads alone are removed",
                      "one file for every kind of finding: a newer build's section must survive an older one writing its own");

                // ── A file that is not what this writes ──
                File.WriteAllText(store.Path, "{\"heads\": {}}");
                bool threw = false;
                try { store.LoadHeads(); } catch (InvalidDataException) { threw = true; }
                check(threw, "a file that is not what this writes is refused, not half read",
                      "the caller says so and goes on without it; each finding then costs one request again");
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        /// <summary>One launch: a router over the file, loading what it holds and writing every change.</summary>
        private static (ReplayHost Host, TextRouter Router, ReplayBox Box) Launch(ElementStore store)
        {
            var host = new ReplayHost();
            var router = new TextRouter(host);
            host.Router = router;
            host.Add(Whole, "La rivière est rouge ce soir, et personne ne sait pourquoi.");
            router.LoadHeads(store.LoadHeads());
            router.HeadsChanged += () => store.SaveHeads(router.HeadsSnapshot());
            return (host, router, new ReplayBox { Id = 1 });
        }

        /// <summary>The recording: its first part set at once, standing still, then the reveal going on.</summary>
        private static void Play(ReplayHost host, ReplayBox box, float from)
        {
            host.Now = from; host.Frame = 1;
            host.GameWrites(box, Head);
            host.Now = from + 1.0f; host.Frame = 60;
            host.Router.ProcessStabilizedTypewriting();
            host.Now = from + 1.5f; host.Frame = 90;
            host.GameWrites(box, Head + " n");
            host.Now = from + 1.54f; host.Frame = 91;
            host.GameWrites(box, Head + " no");
            host.Now = from + 1.58f; host.Frame = 92;
            host.GameWrites(box, Whole);
        }
    }
}
