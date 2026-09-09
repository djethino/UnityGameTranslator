using System;
using UnityGameTranslator.Core.Engine;

namespace UnityGameTranslator.Core.Checks
{
    /// <summary>
    /// Saying that the model is still getting ready — and, far more often, not saying it.
    ///
    /// 🔴 **Two ways to be wrong, and they are not symmetrical.** Staying quiet leaves somebody
    /// watching a game that looks broken for a minute; speaking when nothing is wrong is the
    /// notice that cries wolf, and after the second time nobody reads it again. The second is the
    /// one most of these cases are about.
    /// </summary>
    internal static class ModelWarmupChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            TimeSpan worth = ModelWarmup.WorthSaying;
            TimeSpan longEnough = worth + TimeSpan.FromSeconds(1);
            TimeSpan tooShort = TimeSpan.FromMilliseconds(worth.TotalMilliseconds / 2);

            check(worth > TimeSpan.Zero,
                $"a wait is worth saying after {worth.TotalSeconds:0.#} s",
                "zero would draw on every call, including the ones that answer instantly — which is the flicker this exists to avoid");

            // ── The case it exists for ────────────────────────────────────
            check(ModelWarmup.ShouldSay(localModel: true, preloading: false, awaitingBackend: true,
                                        hasTranslated: false, waited: longEnough),
                "🔴 the first translation, still unanswered, is said",
                "a local model that is not in memory yet can take a minute, and the notice beside it read \"Translating: …\" the whole time");

            check(ModelWarmup.ShouldSay(true, preloading: true, awaitingBackend: false,
                                        hasTranslated: false, waited: longEnough),
                "and so is a startup warm-up still in flight",
                "the weights are being pulled in; anything queued behind it waits on the same thing");

            // ── Everything that must stay quiet ───────────────────────────
            check(!ModelWarmup.ShouldSay(localModel: false, preloading: true, awaitingBackend: true,
                                         hasTranslated: false, waited: longEnough),
                "🔴 nothing is said for a backend out on the internet",
                "Google and DeepL load no model at all, and a slow cloud answer is slow for reasons nobody at this end can act on");

            check(!ModelWarmup.ShouldSay(true, preloading: false, awaitingBackend: true,
                                         hasTranslated: true, waited: longEnough),
                "🔴 nor once a translation has come back",
                "from then on a wait is the model working, not loading — and saying it would be the notice that cries wolf");

            check(!ModelWarmup.ShouldSay(true, preloading: false, awaitingBackend: true,
                                         hasTranslated: false, waited: tooShort),
                "nor for a wait too short to be noticed",
                "a warm start answers in a fraction of a second: drawn there, the notice is a flicker and reads as a fault");

            check(!ModelWarmup.ShouldSay(true, preloading: false, awaitingBackend: false,
                                         hasTranslated: false, waited: longEnough),
                "nor when nothing is in flight at all",
                "a session where nobody has translated anything yet is not a session that is waiting");

            check(!ModelWarmup.ShouldSay(true, preloading: false, awaitingBackend: false,
                                         hasTranslated: false, waited: TimeSpan.Zero),
                "and an idle mod says nothing, whatever the clock reads",
                "the duration only qualifies a wait; it never creates one");

            // ── The boundary, on purpose ──────────────────────────────────
            check(ModelWarmup.ShouldSay(true, false, true, false, worth),
                "exactly at the threshold, it is said",
                "a rule with a soft boundary is a rule nobody can predict, here or in a case");

            // ── 🔴 The subtle one: a preload that answered is not warm ────
            // The preload sends "Hi" with one token: it pulls the WEIGHTS in and says nothing
            // about processing a real prompt, which on a large model is most of the wait and is
            // cached only after the first time.
            check(ModelWarmup.ShouldSay(localModel: true, preloading: false, awaitingBackend: true,
                                        hasTranslated: false, waited: longEnough),
                "🔴 a finished preload does not count as a translation",
                "watching the preload instead of the first real translation would hide the very case this exists for");
        }
    }
}
