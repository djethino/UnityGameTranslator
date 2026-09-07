namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// Elapsed real time, in seconds — for anything on screen that must expire on its own (a
    /// toast, a countdown) without a panel having to name Unity's clock to read it.
    /// </summary>
    public static class Clock
    {
        public static float Now => UnityEngine.Time.realtimeSinceStartup;
    }
}
