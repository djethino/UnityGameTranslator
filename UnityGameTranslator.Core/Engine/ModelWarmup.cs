using System;

namespace UnityGameTranslator.Core.Engine
{
    /// <summary>
    /// Whether to tell somebody the model behind the translation is still getting ready.
    ///
    /// 🔴 **The first translation of a session can take a minute, and the tenth is instant.**
    /// Reported from a real game with a large model on a local server. Two different warm-ups are
    /// behind that, and only one of them is the one people think of: loading the WEIGHTS, and
    /// processing the PROMPT, which is long and which every local server caches after the first
    /// time. Meanwhile the mod's own notice sat on "Translating: …", so it looked broken or slow
    /// when it was waiting exactly as the player was.
    ///
    /// 🔴 **A preload that answered does NOT mean warm.** The mod sends "Hi" at startup to pull the
    /// weights in; that says nothing about the wait a real prompt is about to cost. Watching the
    /// preload instead of the first real translation would hide the very case this exists for —
    /// which is why <see cref="ShouldSay"/> takes them as two different things.
    ///
    /// ⚠ **Nothing is said for a backend out on the internet.** Google and DeepL load nothing at
    /// all, and a cloud model that is slow is slow for reasons nobody at this end can act on. The
    /// caller answers where the server lives; the socle's Endpoints is what it asks.
    ///
    /// ⚠ Pure by contract: no Unity, no clock of its own, no config — every input is handed in.
    /// </summary>
    public static class ModelWarmup
    {
        /// <summary>
        /// How long a wait must last before it is worth drawing at all.
        ///
        /// 🔴 **The only constant here, and it is perceptual rather than a guess.** Under about a
        /// second a delay is not experienced as one, so a warm start would flash the notice for a
        /// quarter of a second — which is exactly the "appearing for nothing" this must not do.
        ///
        /// ⚠ It governs when to DRAW, never what the mod does. A threshold that changed behaviour
        /// on a clock would be the anti-pattern this project has already paid for once.
        /// </summary>
        public static readonly TimeSpan WorthSaying = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Whether a screen should say the model is still getting ready.
        /// </summary>
        /// <param name="localModel">
        /// The backend is a model somebody runs themselves — an LLM, on this machine or their own
        /// network. False for Google, for DeepL, and for anything out on the internet.
        /// </param>
        /// <param name="preloading">The startup warm-up request is in flight.</param>
        /// <param name="awaitingBackend">A translation request is in flight right now.</param>
        /// <param name="hasTranslated">A real translation has already come back this session.</param>
        /// <param name="waited">How long the current wait has lasted.</param>
        public static bool ShouldSay(bool localModel, bool preloading, bool awaitingBackend,
                                     bool hasTranslated, TimeSpan waited)
        {
            if (!localModel) return false;

            // ⚠ Once one has come back, every later wait is the model working, not loading. A
            // server that drops an idle model reloads it later and this will not notice — that is
            // a different question, and answering it needs a measured normal to compare against
            // rather than a number chosen here.
            bool waiting = preloading || (awaitingBackend && !hasTranslated);
            if (!waiting) return false;

            return waited >= WorthSaying;
        }
    }
}
