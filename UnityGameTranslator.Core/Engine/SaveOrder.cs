namespace UnityGameTranslator.Core
{
    /// <summary>
    /// Which save may write: the one taken last. Saves are prepared in order but can finish out
    /// of it — the tick's save serialises off the main thread while an act on the main thread
    /// saves at once — and a slower, older save landing after a newer one would put the older
    /// content back on disk. Each save takes a serial when its content is snapshotted; a serial
    /// below the last one written is refused. Pure, held by the Core.Checks.
    /// </summary>
    public sealed class SaveOrder
    {
        private readonly object _gate = new object();
        private int _issued;
        private int _written;

        /// <summary>The serial of a save whose content was just snapshotted.</summary>
        public int Take()
        {
            lock (_gate) return ++_issued;
        }

        /// <summary>
        /// True when this save is newer than anything written so far — and then it counts as
        /// written, so nothing older lands after it. False means: skip, the disk already holds
        /// something more recent.
        /// </summary>
        public bool MayWrite(int serial)
        {
            lock (_gate)
            {
                if (serial <= _written) return false;
                _written = serial;
                return true;
            }
        }
    }
}
