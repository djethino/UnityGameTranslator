namespace UnityGameTranslator.Core.UI.Components
{
    /// <summary>
    /// An area that scrolls inside a height it is given, and can say what it holds — what
    /// <see cref="ListShares"/> divides a body between. A list of rows is one; a field that
    /// scrolls its text is one too, and that is the point: the field somebody is writing in is
    /// not a fixed strip under the lists, it takes its share of the room like they do, by what
    /// it holds.
    /// </summary>
    public interface ISharedArea
    {
        /// <summary>Everything it holds, laid out at its current width.</summary>
        float ContentHeight { get; }

        bool AtTop { get; }
        void ToTop();

        /// <summary>The height posed on it; <paramref name="fill"/> lets it take spare room, which a shared area never does.</summary>
        void SetHeight(int height, bool fill);
    }
}
