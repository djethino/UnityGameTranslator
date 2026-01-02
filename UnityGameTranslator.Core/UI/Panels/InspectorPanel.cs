using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using UniverseLib;
using UniverseLib.Input;
using UniverseLib.UI;
using UniverseLib.UI.Models;

namespace UnityGameTranslator.Core.UI.Panels
{
    /// <summary>
    /// Inspector panel for visually selecting UI elements to exclude from translation.
    /// Uses GraphicRaycaster (compatible with both Mono and IL2CPP).
    /// </summary>
    public class InspectorPanel : TranslatorPanelBase
    {
        public override string Name => "Element Inspector";
        public override int MinWidth => 400;
        public override int MinHeight => 280;
        public override int PanelWidth => 450;
        public override int PanelHeight => 320;

        protected override int MinPanelHeight => 280;

        // UI elements
        private Text _pathLabel;
        private Text _statusLabel;
        private ButtonRef _excludeThisBtn;
        private ButtonRef _excludePatternBtn;
        private ButtonRef _cancelBtn;

        // State
        private bool _isInspecting = false;
        private string _lastInspectedPath = "";
        private GameObject _lastInspectedObject = null;

        public InspectorPanel(UIBase owner) : base(owner)
        {
        }

        protected override void ConstructPanelContent()
        {
            // Use scrollable layout - content scrolls if needed, buttons stay fixed
            CreateScrollablePanelLayout(out var scrollContent, out var buttonRow, PanelWidth - 40);

            // Title
            var title = CreateTitle(scrollContent, "Title", "Element Inspector");
            RegisterUIText(title);

            UIStyles.CreateSpacer(scrollContent, 5);

            // Main card with all content
            var card = CreateAdaptiveCard(scrollContent, "InspectorCard", PanelWidth - 60, stretchVertically: true);

            // Instructions section
            var instructionTitle = UIStyles.CreateSectionTitle(card, "InstructionsLabel", "Instructions");
            RegisterUIText(instructionTitle);

            var instructionHint = UIStyles.CreateHint(card, "InstructionsHint",
                "Click on any UI element in the game to exclude it from translation.");
            RegisterUIText(instructionHint);

            UIStyles.CreateSpacer(card, 10);

            // Inspected element section
            var pathTitle = UIStyles.CreateSectionTitle(card, "PathSectionLabel", "Inspected Element");
            RegisterUIText(pathTitle);

            // Path display in a styled box
            var pathBox = CreateSection(card, "PathBox");

            _pathLabel = UIFactory.CreateLabel(pathBox, "PathValue", "(click on an element)", TextAnchor.MiddleLeft);
            _pathLabel.color = UIStyles.TextMuted;
            _pathLabel.fontStyle = FontStyle.Italic;
            _pathLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_pathLabel.gameObject, minHeight: UIStyles.RowHeightNormal, flexibleWidth: 9999);

            UIStyles.CreateSpacer(card, 10);

            // Action buttons section
            var actionsTitle = UIStyles.CreateSectionTitle(card, "ActionsLabel", "Actions");
            RegisterUIText(actionsTitle);

            var actionRow1 = UIStyles.CreateFormRow(card, "ActionRow1", UIStyles.ButtonHeight, 5);

            _excludeThisBtn = CreatePrimaryButton(actionRow1, "ExcludeThisBtn", "Exclude This Element");
            _excludeThisBtn.OnClick += OnExcludeThisClicked;
            _excludeThisBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_excludeThisBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_excludeThisBtn.ButtonText);

            _excludePatternBtn = CreateSecondaryButton(actionRow1, "ExcludePatternBtn", "Exclude Pattern");
            _excludePatternBtn.OnClick += OnExcludePatternClicked;
            _excludePatternBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_excludePatternBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_excludePatternBtn.ButtonText);

            var actionRow2 = UIStyles.CreateFormRow(card, "ActionRow2", UIStyles.ButtonHeight, 5);

            _cancelBtn = CreateSecondaryButton(actionRow2, "CancelBtn", "Clear Selection");
            _cancelBtn.OnClick += OnCancelClicked;
            _cancelBtn.Component.interactable = false;
            UIFactory.SetLayoutElement(_cancelBtn.Component.gameObject, flexibleWidth: 9999);
            RegisterUIText(_cancelBtn.ButtonText);

            // Status label
            UIStyles.CreateSpacer(card, 5);
            _statusLabel = UIFactory.CreateLabel(card, "Status", "", TextAnchor.MiddleLeft);
            _statusLabel.fontSize = UIStyles.FontSizeSmall;
            UIFactory.SetLayoutElement(_statusLabel.gameObject, minHeight: UIStyles.RowHeightSmall);

            // Footer button (fixed at bottom)
            var stopBtn = CreatePrimaryButton(buttonRow, "StopBtn", "Stop Inspecting");
            stopBtn.OnClick += OnStopClicked;
            RegisterUIText(stopBtn.ButtonText);
        }

        public override void SetActive(bool active)
        {
            bool wasActive = Enabled;
            base.SetActive(active);

            if (active)
            {
                _isInspecting = true;
                // Only clear selection when first opening, not when panel is re-focused
                if (!wasActive)
                {
                    ClearSelection();
                    _statusLabel.text = "";
                }
            }
            else
            {
                _isInspecting = false;
            }
        }

        public override void Update()
        {
            base.Update();

            if (!_isInspecting || !Enabled) return;

            // Check for mouse click outside of this panel
            if (InputManager.GetMouseButtonDown(0))
            {
                Vector3 mousePos = InputManager.MousePosition;

                // Skip if mouse is over our panel - let buttons handle the click
                if (Rect != null && IsMouseOverPanel(mousePos))
                {
                    return;
                }

                // Raycast to find UI element under cursor
                var hitObject = RaycastUIElement(mousePos);

                if (hitObject != null)
                {
                    // Check if it's part of our mod UI - if so, ignore
                    var graphic = hitObject.GetComponent<Graphic>();
                    if (TranslatorCore.IsOwnUIByHierarchy(graphic))
                    {
                        return; // Don't select our own UI elements
                    }

                    string path = TranslatorCore.GetGameObjectPath(hitObject);
                    _lastInspectedPath = path;
                    _lastInspectedObject = hitObject;

                    _pathLabel.text = path;
                    _pathLabel.color = UIStyles.TextPrimary;
                    _pathLabel.fontStyle = FontStyle.Normal;
                    _excludeThisBtn.Component.interactable = true;
                    _excludePatternBtn.Component.interactable = true;
                    _cancelBtn.Component.interactable = true;

                    _statusLabel.text = "Element selected";
                    _statusLabel.color = UIStyles.StatusSuccess;
                }
            }
        }

        /// <summary>
        /// Raycast to find UI element under screen position.
        /// Uses GraphicRaycaster which works on both Mono and IL2CPP.
        /// </summary>
        private GameObject RaycastUIElement(Vector3 screenPosition)
        {
            if (EventSystem.current == null) return null;

            // Find all GraphicRaycasters directly (more compatible than finding Canvas)
            var raycasters = UnityEngine.Object.FindObjectsOfType<GraphicRaycaster>();

            foreach (var raycaster in raycasters)
            {
                if (raycaster == null) continue;

                var pointer = new PointerEventData(EventSystem.current)
                {
                    position = screenPosition
                };

                var results = new List<RaycastResult>();
                raycaster.Raycast(pointer, results);

                if (results.Count > 0)
                {
                    // Return first hit (topmost)
                    return results[0].gameObject;
                }
            }

            return null;
        }

        /// <summary>
        /// Check if mouse position is over this panel's rect.
        /// UniverseLib uses ScreenSpaceOverlay, so world corners = screen coords.
        /// </summary>
        private bool IsMouseOverPanel(Vector3 screenPos)
        {
            if (Rect == null) return false;

            // Get panel corners - for ScreenSpaceOverlay, world = screen space
            Vector3[] corners = new Vector3[4];
            Rect.GetWorldCorners(corners);

            float minX = Mathf.Min(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
            float maxX = Mathf.Max(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
            float minY = Mathf.Min(corners[0].y, corners[1].y, corners[2].y, corners[3].y);
            float maxY = Mathf.Max(corners[0].y, corners[1].y, corners[2].y, corners[3].y);

            return screenPos.x >= minX && screenPos.x <= maxX &&
                   screenPos.y >= minY && screenPos.y <= maxY;
        }

        private void ClearSelection()
        {
            _lastInspectedPath = "";
            _lastInspectedObject = null;
            _pathLabel.text = "(click on an element)";
            _pathLabel.color = UIStyles.TextMuted;
            _pathLabel.fontStyle = FontStyle.Italic;
            _excludeThisBtn.Component.interactable = false;
            _excludePatternBtn.Component.interactable = false;
            _cancelBtn.Component.interactable = false;
        }

        private void OnExcludeThisClicked()
        {
            if (string.IsNullOrEmpty(_lastInspectedPath)) return;

            // Add exact path as exclusion
            TranslatorCore.AddExclusion(_lastInspectedPath);

            _statusLabel.text = "Excluded!";
            _statusLabel.color = UIStyles.StatusSuccess;

            ClearSelection();
        }

        private void OnExcludePatternClicked()
        {
            if (string.IsNullOrEmpty(_lastInspectedPath) || _lastInspectedObject == null) return;

            // Create pattern from the object's name with wildcard prefix
            // e.g., "Canvas/ChatPanel/MessageList" -> "**/MessageList"
            string objectName = _lastInspectedObject.name;
            string pattern = "**/" + objectName;

            TranslatorCore.AddExclusion(pattern);
            TranslatorCore.SaveCache();

            _statusLabel.text = $"Excluded: {pattern}";
            _statusLabel.color = UIStyles.StatusSuccess;

            ClearSelection();
        }

        private void OnCancelClicked()
        {
            ClearSelection();
            _statusLabel.text = "";
        }

        private void OnStopClicked()
        {
            SetActive(false);
        }
    }
}
