using UnityGameTranslator.Common;
using UnityGameTranslator.Core.UI.Components;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// The half of the panel base a panel is allowed to see — and the only half a port to
    /// another engine would have to reproduce.
    ///
    /// Split from <c>TranslatorPanelBase.cs</c> on 2026-09-08 (step 4d of
    /// analyse/plan-prealables-couches.md). Everything here speaks in Hosts, handles, numbers and
    /// words: no engine type crosses this file, and <c>UiBoundaryChecks</c> judges it like any
    /// migrated panel. The other file keeps the rendering — sizing, persistence, the scroll
    /// skeleton, the coroutines — and stays listed there on purpose.
    ///
    /// ⚠ The rule for what goes where is the BODY, not the signature: a member a panel calls
    /// whose body is rendering (<c>RecalculateSize</c>, <c>SetActive</c>) lives in the other
    /// file and is documented there; a member whose body only names the vocabulary lives here.
    /// </summary>
    public abstract partial class TranslatorPanelBase
    {
        // ── What a panel declares about itself ──────────────────────────────────────────────

        /// <summary>Desired width of the panel in pixels.</summary>
        public abstract int PanelWidth { get; }

        /// <summary>Desired height of the panel in pixels.</summary>
        public abstract int PanelHeight { get; }

        /// <summary>
        /// Whether this panel should show the backdrop when active.
        /// Override to false for panels like StatusOverlay that shouldn't dim the screen.
        /// </summary>
        protected virtual bool UseBackdrop => true;

        /// <summary>
        /// Minimum panel height for resize constraints.
        /// Override to set a different minimum per panel.
        /// </summary>
        protected virtual int MinPanelHeight => MinHeight;

        /// <summary>
        /// Whether this panel should use dynamic content-based sizing.
        /// Override to false for fixed-size panels like StatusOverlay.
        /// </summary>
        protected virtual bool UseDynamicSizing => true;

        /// <summary>
        /// Whether this panel should persist window preferences (position, size).
        /// Override to false for temporary panels like dialogs or wizards.
        /// </summary>
        protected virtual bool PersistWindowPreferences => true;

        /// <summary>
        /// Whether this panel uses center anchors for positioning.
        /// Override to false for panels like StatusOverlay that use corner anchors.
        /// </summary>
        protected virtual bool UsesCenterAnchors => true;

        /// <summary>
        /// Whether this panel contains scrollable content that should be allowed to grow
        /// beyond its measured min height (i.e. it has a ScrollView with flexibleHeight).
        /// When true, MaxHeight is bumped up to the screen height so the user can drag the
        /// bottom edge down to give the inner scroll list more room.
        /// Override to true for panels with long lists (Inspector text edit, merge conflicts,
        /// parameters tabs, etc.).
        /// </summary>
        protected virtual bool HasFlexibleContent => false;

        /// <summary>
        /// A height the panel should stay able to show even when the current content is
        /// shorter. Tabbed panels report their tallest tab here so the window keeps one size
        /// across tabs. It sizes the PANEL — the containers inside stay free to be exactly as
        /// tall as what they hold.
        /// </summary>
        protected virtual float ContentHeightFloor => _tallestTabContentHeight;


        /// <summary>
        /// THE RULE FOR EVERY SCROLLING LIST IN A PANEL, written here because getting it wrong
        /// is invisible until someone opens the window on a small screen.
        ///
        /// A list declares three heights: a minimum (the smallest box worth showing), a
        /// PREFERRED height equal to that minimum, and a flexible height so it grows into
        /// whatever room is left.
        ///
        /// The preferred one is the trap. This panel sizes its scrolling area to
        /// max(viewport, sum of the children's preferred heights), so a list that asks for twice
        /// what it needs pushes that sum past the viewport before a single entry exists — a
        /// scrollbar nobody can justify, and boxes standing half empty at the size they demanded.
        /// Declaring no preferred height at all is the mirror mistake: the list is then weighed
        /// at its minimum in that sum, so whatever sits below it is never budgeted for and gets
        /// pushed out of view once the list expands for real.
        /// </summary>
        protected const string ScrollingListHeightRule =
            "minHeight = preferredHeight = smallest useful box, flexibleHeight = 9999";

        // ── What a panel holds ──────────────────────────────────────────────────────────────

        /// <summary>
        /// What this panel's Apply button will act on — every screen with an Apply registers
        /// each of its fields and rows here, once, with the test that says whether it changed.
        /// One list feeds both the "Apply (N)" count and the mark drawn on each waiting element,
        /// so the two cannot disagree (see <see cref="PendingMarks"/>). On the base, like the
        /// scope strip: a screen that gets an Apply later must not have to invent its own.
        /// </summary>
        protected readonly PendingMarks Pending = new PendingMarks();

        /// <summary>The title with its scope switch, once <see cref="ScopedTitle"/> has built it.</summary>
        private ScopedTitleBar _scopedTitle;

        // ── What a panel builds with ────────────────────────────────────────────────────────

        /// <summary>The panel's own content root, as a host.</summary>
        protected Host Content => new Host(ContentRoot);

        /// <summary>
        /// The panel window itself, as a host — for the one thing a panel that positions its own
        /// window (a corner overlay, never the ordinary centred kind) still has to reach: its own
        /// rectangle, through <see cref="Components.Overlays"/> rather than by naming it.
        /// </summary>
        protected Host Window => new Host(UIRoot);

        /// <summary>The title bar, as a host — so a panel with none of its own (the overlay) can hide it.</summary>
        protected Host TitleBarHost => new Host(TitleBar);

        /// <summary>The scrollable body and the fixed footer, as hosts.</summary>
        protected void Layout(out Host scrollContent, out Host buttonRow, int cardWidth = 420)
            => BuildScrollableLayout(cardWidth, out scrollContent, out buttonRow);

        /// <summary>
        /// The help bar, pinned above a footer held as a host. Hovering any control described via
        /// <c>helpZone.Describe(...)</c> shows its explanation there. The zone translates its own
        /// label as it writes it, so there is nothing to register.
        /// </summary>
        protected HelpZone CreateHelpZone(Host buttonRow, string defaultText = "")
            => BuildHelpZone(buttonRow, defaultText);

        /// <summary>
        /// A fixed (non-scrolling) header between the title bar and the scroll area — the top
        /// mirror of the help zone / footer pattern. Panel titles and tab buttons go here so only
        /// the actual content scrolls.
        /// </summary>
        protected Host FixedHeader(string name = "FixedHeader") => BuildFixedHeader(name);

        /// <summary>
        /// A title with the scope switch beside it, as a label a panel may hold.
        ///
        /// ⚠ **On EVERY screen that shows translation lines, without exception** — see
        /// <see cref="ScopedTitleBar"/> for why it is handed out here rather than pasted in.
        /// </summary>
        /// <param name="policy">
        /// UiText for a title fixed at construction; Dynamic (or Excluded) for one the code
        /// rewrites afterwards (a mode announced in the title itself) — <see cref="LabelHandle.Say"/>
        /// throws on a UiText label, so a title that changes must not be created as one.
        /// </param>
        protected LabelHandle ScopedTitle(Host parent, string name, string text, EditSide side,
                                          TextPolicy policy = TextPolicy.UiText)
        {
            _scopedTitle = ScopedTitleBar.Create(parent, name, text, side, WindowWidth, policy);
            return _scopedTitle.Title;
        }

        /// <summary>
        /// Keeps a tabbed panel from changing size when the visitor switches tabs, by measuring
        /// the tallest tab once and holding the window to it.
        ///
        /// Call from SetActive when the panel becomes visible: layouts have no measurable size
        /// until then, which is why the measurement waits a few frames before reading anything.
        ///
        /// Lived in three panels as three copies of the same coroutine, and all three wrote the
        /// measurement as a minHeight on the tab CONTAINER. That says something quite different
        /// from what was meant: it makes every short tab as tall as the tallest one, so its
        /// content stretches, nothing inside stays anchored, and the panel scrolls to show
        /// emptiness that belongs to another tab. The measurement sizes the panel; the
        /// containers are left alone.
        /// </summary>
        protected void KeepPanelHeightAcrossTabs(TabBar tabBar)
        {
            if (tabBar == null) return;
            StartMeasuringTallestTab(tabBar);
        }

        /// <summary>
        /// Translate a FRAGMENT that the caller then concatenates with data
        /// (<c>Tr("Connected as") + " @" + user</c>).
        ///
        /// Deliberately does NOT register anything: on a cache miss the worker writes the finished
        /// translation straight into the component it was given, which for a composed label would
        /// replace the whole line with just this fragment — dropping the username. Without a
        /// component the translation only lands in the cache, and the next refresh renders the
        /// complete line correctly.
        /// </summary>
        protected static string Tr(string english)
        {
            return TranslatorCore.TranslateOwnUIDynamic(english);
        }

        // ── What the manager asks of every open panel ───────────────────────────────────────

        /// <summary>
        /// Re-measures the room the title leaves to its scope strip and changes its form only if
        /// the answer moved. Called on every resize, including the programmatic ones.
        /// </summary>
        public void RefreshScopeStrip() => _scopedTitle?.Refresh();

        /// <summary>
        /// Say which side this window is about now — for a screen that serves two acts and must
        /// carry the mark of whichever one opened it.
        /// </summary>
        protected void AskTitleScope(EditSide side) => _scopedTitle?.Ask(side);

        /// <summary>
        /// Forget the strip's measurements and take them again — after the interface font changes.
        /// Without this a font change leaves the strip reasoning with the OLD font's metrics until
        /// somebody happens to resize the window.
        /// </summary>
        public void InvalidateScopeStrip() => _scopedTitle?.Invalidate();
    }
}
