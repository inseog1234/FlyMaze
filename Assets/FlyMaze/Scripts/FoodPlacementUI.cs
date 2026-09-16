using UnityEngine;
using UnityEngine.UI;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    public sealed class FoodPlacementUI : MonoBehaviour
    {
        private static readonly Color PanelColor = new Color(0.025f, 0.035f, 0.055f, 0.94f);
        private static readonly Color SurfaceColor = new Color(0.065f, 0.085f, 0.115f, 0.98f);
        private static readonly Color Accent = new Color(1.00f, 0.48f, 0.12f, 1f);
        private static readonly Color AccentSoft = new Color(0.48f, 0.18f, 0.06f, 1f);
        private static readonly Color Mint = new Color(0.12f, 0.92f, 0.76f, 1f);
        private static readonly Color TextPrimary = new Color(0.93f, 0.97f, 1f, 1f);
        private static readonly Color TextMuted = new Color(0.50f, 0.62f, 0.72f, 1f);

        private FoodPlacementController _controller;
        private Font _font;
        private CanvasGroup _group;
        private RectTransform _panel;
        private Text _countText;
        private Text _statusText;
        private Button _autoButton;
        private Button _manualButton;
        private float _intro;

        public void Initialize(FoodPlacementController controller)
        {
            if (_controller != null || controller == null)
                return;

            _controller = controller;
            _controller.StateChanged += Refresh;
            _controller.ModeChanged += HandleModeChanged;
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            BuildUI();
            Refresh();
        }

        private void Update()
        {
            if (_group == null || _panel == null)
                return;

            _intro += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_intro / 0.42f);
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            _group.alpha = eased;
            _panel.anchoredPosition = new Vector2(60f, Mathf.Lerp(16f, -24f, eased));
        }

        private void BuildUI()
        {
            GameObject canvasObject = new GameObject("Food Objective HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 105;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            GameObject panelObject = CreateImage("Food Objective Bar", canvasObject.transform, PanelColor);
            _panel = panelObject.GetComponent<RectTransform>();
            _panel.anchorMin = new Vector2(0.5f, 1f);
            _panel.anchorMax = new Vector2(0.5f, 1f);
            _panel.pivot = new Vector2(0.5f, 1f);
            _panel.anchoredPosition = new Vector2(60f, 16f);
            _panel.sizeDelta = new Vector2(650f, 94f);

            Shadow shadow = panelObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.46f);
            shadow.effectDistance = new Vector2(10f, -10f);

            _group = panelObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;

            CreateText(panelObject.transform, "FOOD OBJECTIVES", 16, FontStyle.Bold, TextPrimary,
                new Vector2(18f, -12f), new Vector2(175f, 24f), TextAnchor.MiddleLeft);
            CreateText(panelObject.transform, "COLLECT ALL BEFORE EXIT", 10, FontStyle.Bold, Accent,
                new Vector2(18f, -39f), new Vector2(180f, 18f), TextAnchor.MiddleLeft);

            CreateText(panelObject.transform, "COUNT", 11, FontStyle.Bold, TextMuted,
                new Vector2(205f, -13f), new Vector2(58f, 34f), TextAnchor.MiddleLeft);

            Button minus = CreateButton(panelObject.transform, "−", new Vector2(262f, -12f), new Vector2(38f, 36f), SurfaceColor);
            minus.onClick.AddListener(() => _controller.SetTargetFoodCount(_controller.TargetFoodCount - 1));

            GameObject countBox = CreateImage("Count Box", panelObject.transform, new Color(0.045f, 0.060f, 0.082f, 1f));
            RectTransform countRect = countBox.GetComponent<RectTransform>();
            SetTopLeft(countRect, new Vector2(307f, -12f), new Vector2(48f, 36f));
            _countText = CreateText(countBox.transform, "06", 17, FontStyle.Bold, TextPrimary,
                Vector2.zero, new Vector2(48f, 36f), TextAnchor.MiddleCenter, true);

            Button plus = CreateButton(panelObject.transform, "+", new Vector2(362f, -12f), new Vector2(38f, 36f), SurfaceColor);
            plus.onClick.AddListener(() => _controller.SetTargetFoodCount(_controller.TargetFoodCount + 1));

            _autoButton = CreateButton(panelObject.transform, "AUTO", new Vector2(420f, -12f), new Vector2(86f, 36f), AccentSoft);
            _autoButton.onClick.AddListener(() => _controller.SetMode(FoodPlacementMode.Auto));

            _manualButton = CreateButton(panelObject.transform, "MANUAL", new Vector2(516f, -12f), new Vector2(116f, 36f), SurfaceColor);
            _manualButton.onClick.AddListener(() => _controller.SetMode(FoodPlacementMode.Manual));

            _statusText = CreateText(panelObject.transform, "", 11, FontStyle.Bold, TextMuted,
                new Vector2(205f, -55f), new Vector2(427f, 22f), TextAnchor.MiddleLeft);

            GameObject accentLine = CreateImage("Food Accent", panelObject.transform, Accent);
            RectTransform accentRect = accentLine.GetComponent<RectTransform>();
            SetTopLeft(accentRect, new Vector2(18f, -73f), new Vector2(165f, 2f));
        }

        private void HandleModeChanged(FoodPlacementMode _)
        {
            Refresh();
        }

        private void Refresh()
        {
            if (_controller == null)
                return;

            if (_countText != null)
                _countText.text = _controller.TargetFoodCount.ToString("00");

            if (_statusText != null)
            {
                if (_controller.Mode == FoodPlacementMode.Manual)
                {
                    _statusText.text = $"PLACED {_controller.PlacedFoodCount}/{_controller.TargetFoodCount}  •  LMB CELL: PLACE/REMOVE  •  MMB/RMB: PAN";
                    _statusText.color = Accent;
                }
                else
                {
                    _statusText.text = $"AUTO DISTRIBUTION  •  {_controller.PlacedFoodCount}/{_controller.TargetFoodCount} PLACED";
                    _statusText.color = TextMuted;
                }
            }

            SetButtonVisual(_autoButton, _controller.Mode == FoodPlacementMode.Auto);
            SetButtonVisual(_manualButton, _controller.Mode == FoodPlacementMode.Manual);
        }

        private static void SetButtonVisual(Button button, bool selected)
        {
            if (button == null)
                return;

            Color color = selected ? Accent : SurfaceColor;
            Image image = button.GetComponent<Image>();
            if (image != null)
                image.color = color;

            HudButtonFX fx = button.GetComponent<HudButtonFX>();
            if (fx != null)
                fx.SetBaseColor(color);

            Text text = button.GetComponentInChildren<Text>();
            if (text != null)
                text.color = selected ? new Color(0.12f, 0.045f, 0.01f, 1f) : TextPrimary;
        }

        private Button CreateButton(Transform parent, string label, Vector2 position, Vector2 size, Color color)
        {
            GameObject go = CreateImage(label + " Button", parent, color);
            SetTopLeft(go.GetComponent<RectTransform>(), position, size);

            Button button = go.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = go.GetComponent<Image>();

            HudButtonFX fx = go.AddComponent<HudButtonFX>();
            fx.SetBaseColor(color);

            CreateText(go.transform, label, 12, FontStyle.Bold, TextPrimary,
                Vector2.zero, size, TextAnchor.MiddleCenter, true);
            return button;
        }

        private Text CreateText(Transform parent, string value, int fontSize, FontStyle style, Color color,
            Vector2 position, Vector2 size, TextAnchor alignment, bool stretch = false)
        {
            GameObject go = new GameObject("Text - " + value, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();

            if (stretch)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }
            else
            {
                SetTopLeft(rect, position, size);
            }

            Text text = go.GetComponent<Text>();
            text.font = _font;
            text.text = value;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static GameObject CreateImage(string name, Transform parent, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = color;
            return go;
        }

        private static void SetTopLeft(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }

        private void OnDestroy()
        {
            if (_controller != null)
            {
                _controller.StateChanged -= Refresh;
                _controller.ModeChanged -= HandleModeChanged;
            }
        }
    }
}
