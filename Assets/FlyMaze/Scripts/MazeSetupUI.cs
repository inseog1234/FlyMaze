using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FlyMaze
{
    public sealed class MazeSetupUI : MonoBehaviour
    {
        private static readonly Color PanelColor = new Color(0.025f, 0.035f, 0.055f, 0.95f);
        private static readonly Color SurfaceColor = new Color(0.065f, 0.085f, 0.115f, 0.96f);
        private static readonly Color AccentColor = new Color(0.12f, 0.92f, 0.76f, 1f);
        private static readonly Color AccentSoft = new Color(0.09f, 0.56f, 0.50f, 1f);
        private static readonly Color TextPrimary = new Color(0.91f, 0.96f, 1f, 1f);
        private static readonly Color TextMuted = new Color(0.48f, 0.60f, 0.70f, 1f);

        private readonly MazeSettings _settings = new MazeSettings();
        private RandomMazeGenerator _generator;
        private Font _font;
        private bool _initialized;
        private float _introTime;

        private CanvasGroup _canvasGroup;
        private RectTransform _panelRect;
        private Image _accentLine;
        private Text _widthValue;
        private Text _heightValue;
        private Text _twistValue;
        private Text _loopValue;
        private Text _seedValue;
        private Text _statusText;
        private Button _generateButton;

        private void Start()
        {
            if (!_initialized)
            {
                RandomMazeGenerator generator = GetComponent<RandomMazeGenerator>();
                if (generator == null)
                    generator = FindFirstObjectByType<RandomMazeGenerator>();
                Initialize(generator);
            }
        }

        public void Initialize(RandomMazeGenerator generator)
        {
            if (_initialized || generator == null)
                return;

            _initialized = true;
            _generator = generator;
            _generator.GenerationStarted += HandleGenerationStarted;
            _generator.MazeBuilt += HandleMazeBuilt;

            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            EnsureEventSystem();
            BuildUI();
            RandomizeSeed();
            RefreshValues();
            _generator.Generate(_settings, true);
        }

        private void Update()
        {
            if (!_initialized || _canvasGroup == null || _panelRect == null)
                return;

            _introTime += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_introTime / 0.55f);
            float eased = 1f - Mathf.Pow(1f - t, 3f);
            _canvasGroup.alpha = eased;
            _panelRect.anchoredPosition = new Vector2(Mathf.Lerp(-46f, 24f, eased), 0f);

            if (_accentLine != null)
            {
                Color c = AccentColor;
                c.a = 0.58f + Mathf.Sin(Time.unscaledTime * 3.1f) * 0.20f;
                _accentLine.color = c;
            }
        }

        private void BuildUI()
        {
            GameObject canvasObject = new GameObject("FlyMaze HUD", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            GameObject panel = CreateImageObject("Setup Panel", canvasObject.transform, PanelColor);
            _panelRect = panel.GetComponent<RectTransform>();
            _panelRect.anchorMin = new Vector2(0f, 0.5f);
            _panelRect.anchorMax = new Vector2(0f, 0.5f);
            _panelRect.pivot = new Vector2(0f, 0.5f);
            _panelRect.sizeDelta = new Vector2(410f, 820f);
            _panelRect.anchoredPosition = new Vector2(-46f, 0f);

            Shadow shadow = panel.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.48f);
            shadow.effectDistance = new Vector2(14f, -14f);
            shadow.useGraphicAlpha = true;

            _canvasGroup = panel.AddComponent<CanvasGroup>();
            _canvasGroup.alpha = 0f;

            Text title = CreateText(panel.transform, "FLY//MAZE", 42, FontStyle.Bold, TextPrimary,
                new Vector2(30f, -30f), new Vector2(340f, 54f), TextAnchor.UpperLeft);
            title.characterSpacingCompat(1.5f);

            CreateText(panel.transform, "CONNECTOME TEST CHAMBER", 14, FontStyle.Bold, AccentColor,
                new Vector2(32f, -83f), new Vector2(330f, 24f), TextAnchor.UpperLeft);

            GameObject lineObject = CreateImageObject("Accent Line", panel.transform, AccentColor);
            RectTransform lineRect = lineObject.GetComponent<RectTransform>();
            lineRect.anchorMin = new Vector2(0f, 1f);
            lineRect.anchorMax = new Vector2(0f, 1f);
            lineRect.pivot = new Vector2(0f, 1f);
            lineRect.anchoredPosition = new Vector2(30f, -119f);
            lineRect.sizeDelta = new Vector2(350f, 3f);
            _accentLine = lineObject.GetComponent<Image>();

            CreateSectionLabel(panel.transform, "MAP DIMENSIONS", -151f);
            CreateText(panel.transform, "WIDTH", 15, FontStyle.Bold, TextMuted,
                new Vector2(32f, -195f), new Vector2(105f, 38f), TextAnchor.MiddleLeft);
            _widthValue = CreateText(panel.transform, "15", 23, FontStyle.Bold, TextPrimary,
                new Vector2(231f, -195f), new Vector2(62f, 38f), TextAnchor.MiddleCenter);
            CreateSmallButton(panel.transform, "−", new Vector2(162f, -195f), () => ChangeWidth(-2));
            CreateSmallButton(panel.transform, "+", new Vector2(306f, -195f), () => ChangeWidth(2));

            CreateText(panel.transform, "HEIGHT", 15, FontStyle.Bold, TextMuted,
                new Vector2(32f, -248f), new Vector2(105f, 38f), TextAnchor.MiddleLeft);
            _heightValue = CreateText(panel.transform, "11", 23, FontStyle.Bold, TextPrimary,
                new Vector2(231f, -248f), new Vector2(62f, 38f), TextAnchor.MiddleCenter);
            CreateSmallButton(panel.transform, "−", new Vector2(162f, -248f), () => ChangeHeight(-2));
            CreateSmallButton(panel.transform, "+", new Vector2(306f, -248f), () => ChangeHeight(2));

            CreateSectionLabel(panel.transform, "MAZE CHARACTER", -311f);
            CreateText(panel.transform, "TWISTINESS", 15, FontStyle.Bold, TextMuted,
                new Vector2(32f, -354f), new Vector2(150f, 28f), TextAnchor.MiddleLeft);
            _twistValue = CreateText(panel.transform, "62%", 15, FontStyle.Bold, TextPrimary,
                new Vector2(305f, -354f), new Vector2(70f, 28f), TextAnchor.MiddleRight);
            Slider twist = CreateSlider(panel.transform, new Vector2(32f, -391f), new Vector2(343f, 24f), 0f, 1f, _settings.twistiness);
            twist.onValueChanged.AddListener(value =>
            {
                _settings.twistiness = value;
                RefreshValues();
            });

            CreateText(panel.transform, "EXTRA LOOPS", 15, FontStyle.Bold, TextMuted,
                new Vector2(32f, -434f), new Vector2(150f, 28f), TextAnchor.MiddleLeft);
            _loopValue = CreateText(panel.transform, "4%", 15, FontStyle.Bold, TextPrimary,
                new Vector2(305f, -434f), new Vector2(70f, 28f), TextAnchor.MiddleRight);
            Slider loops = CreateSlider(panel.transform, new Vector2(32f, -471f), new Vector2(343f, 24f), 0f, 0.18f, _settings.extraLoopChance);
            loops.onValueChanged.AddListener(value =>
            {
                _settings.extraLoopChance = value;
                RefreshValues();
            });

            CreateSectionLabel(panel.transform, "SEED", -526f);
            GameObject seedBox = CreateImageObject("Seed Box", panel.transform, SurfaceColor);
            RectTransform seedRect = seedBox.GetComponent<RectTransform>();
            SetTopLeft(seedRect, new Vector2(32f, -567f), new Vector2(222f, 50f));
            _seedValue = CreateText(seedBox.transform, "#000000", 20, FontStyle.Bold, TextPrimary,
                new Vector2(16f, -5f), new Vector2(190f, 40f), TextAnchor.MiddleLeft);

            Button reroll = CreateButton(panel.transform, "REROLL", new Vector2(268f, -567f), new Vector2(107f, 50f), AccentSoft);
            reroll.onClick.AddListener(() =>
            {
                RandomizeSeed();
                RefreshValues();
            });

            _generateButton = CreateButton(panel.transform, "GENERATE MAZE", new Vector2(32f, -647f), new Vector2(343f, 64f), AccentColor);
            _generateButton.GetComponentInChildren<Text>().color = new Color(0.015f, 0.07f, 0.065f, 1f);
            _generateButton.onClick.AddListener(Generate);

            _statusText = CreateText(panel.transform, "READY", 13, FontStyle.Bold, TextMuted,
                new Vector2(32f, -724f), new Vector2(343f, 42f), TextAnchor.UpperLeft);

            CreateText(panel.transform, "PROCEDURAL / LIVE", 11, FontStyle.Bold, new Color(0.30f, 0.43f, 0.52f, 1f),
                new Vector2(32f, -776f), new Vector2(343f, 22f), TextAnchor.UpperLeft);

            CreateTopRightBadge(canvasObject.transform);
        }

        private void Generate()
        {
            if (_generator == null)
                return;

            _generator.Generate(_settings, true);
        }

        private void ChangeWidth(int delta)
        {
            _settings.width = Mathf.Clamp(_settings.width + delta, 5, 31);
            RefreshValues();
        }

        private void ChangeHeight(int delta)
        {
            _settings.height = Mathf.Clamp(_settings.height + delta, 5, 25);
            RefreshValues();
        }

        private void RandomizeSeed()
        {
            _settings.seed = UnityEngine.Random.Range(100000, 999999);
        }

        private void RefreshValues()
        {
            if (_widthValue != null) _widthValue.text = _settings.width.ToString();
            if (_heightValue != null) _heightValue.text = _settings.height.ToString();
            if (_twistValue != null) _twistValue.text = Mathf.RoundToInt(_settings.twistiness * 100f) + "%";
            if (_loopValue != null) _loopValue.text = Mathf.RoundToInt(_settings.extraLoopChance * 100f) + "%";
            if (_seedValue != null) _seedValue.text = "#" + _settings.seed.ToString("000000");
        }

        private void HandleGenerationStarted()
        {
            if (_statusText != null)
            {
                _statusText.text = "● GENERATING  •  ROUTING NEURAL TEST SPACE...";
                _statusText.color = AccentColor;
            }

            if (_generateButton != null)
                _generateButton.interactable = false;
        }

        private void HandleMazeBuilt(MazeBuildInfo info)
        {
            if (_statusText != null)
            {
                _statusText.text = $"● READY  •  {info.CellCount} CELLS  •  {info.WallCount} WALLS";
                _statusText.color = TextMuted;
            }

            if (_generateButton != null)
                _generateButton.interactable = true;
        }

        private void CreateSectionLabel(Transform parent, string text, float y)
        {
            CreateText(parent, text, 12, FontStyle.Bold, new Color(0.29f, 0.42f, 0.52f, 1f),
                new Vector2(32f, y), new Vector2(340f, 24f), TextAnchor.MiddleLeft);
        }

        private void CreateSmallButton(Transform parent, string label, Vector2 position, UnityEngine.Events.UnityAction action)
        {
            Button button = CreateButton(parent, label, position, new Vector2(52f, 38f), SurfaceColor);
            button.GetComponentInChildren<Text>().fontSize = 25;
            button.onClick.AddListener(action);
        }

        private Button CreateButton(Transform parent, string label, Vector2 position, Vector2 size, Color color)
        {
            GameObject buttonObject = CreateImageObject(label + " Button", parent, color);
            RectTransform rect = buttonObject.GetComponent<RectTransform>();
            SetTopLeft(rect, position, size);

            Button button = buttonObject.AddComponent<Button>();
            button.transition = Selectable.Transition.None;
            button.targetGraphic = buttonObject.GetComponent<Image>();

            HudButtonFX fx = buttonObject.AddComponent<HudButtonFX>();
            fx.SetBaseColor(color);

            CreateText(buttonObject.transform, label, 15, FontStyle.Bold, TextPrimary,
                Vector2.zero, size, TextAnchor.MiddleCenter, true);
            return button;
        }

        private Slider CreateSlider(Transform parent, Vector2 position, Vector2 size, float min, float max, float value)
        {
            GameObject root = new GameObject("Slider", typeof(RectTransform), typeof(Slider));
            root.transform.SetParent(parent, false);
            RectTransform rootRect = root.GetComponent<RectTransform>();
            SetTopLeft(rootRect, position, size);

            GameObject background = CreateImageObject("Background", root.transform, new Color(0.08f, 0.11f, 0.15f, 1f));
            RectTransform bgRect = background.GetComponent<RectTransform>();
            Stretch(bgRect, new Vector2(0f, 7f), new Vector2(0f, -7f));

            GameObject fillArea = new GameObject("Fill Area", typeof(RectTransform));
            fillArea.transform.SetParent(root.transform, false);
            RectTransform fillAreaRect = fillArea.GetComponent<RectTransform>();
            Stretch(fillAreaRect, new Vector2(2f, 8f), new Vector2(-2f, -8f));

            GameObject fill = CreateImageObject("Fill", fillArea.transform, AccentColor);
            RectTransform fillRect = fill.GetComponent<RectTransform>();
            Stretch(fillRect, Vector2.zero, Vector2.zero);

            GameObject handleArea = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleArea.transform.SetParent(root.transform, false);
            RectTransform handleAreaRect = handleArea.GetComponent<RectTransform>();
            Stretch(handleAreaRect, new Vector2(8f, 0f), new Vector2(-8f, 0f));

            GameObject handle = CreateImageObject("Handle", handleArea.transform, TextPrimary);
            RectTransform handleRect = handle.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(16f, 16f);

            Slider slider = root.GetComponent<Slider>();
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = value;
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.targetGraphic = handle.GetComponent<Image>();
            slider.direction = Slider.Direction.LeftToRight;
            return slider;
        }

        private void CreateTopRightBadge(Transform parent)
        {
            GameObject badge = CreateImageObject("Live Badge", parent, new Color(0.025f, 0.035f, 0.055f, 0.82f));
            RectTransform rect = badge.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-26f, -26f);
            rect.sizeDelta = new Vector2(250f, 48f);

            CreateText(badge.transform, "●  CONNECTOME LAB  /  ONLINE", 12, FontStyle.Bold, AccentColor,
                Vector2.zero, new Vector2(250f, 48f), TextAnchor.MiddleCenter, true);
        }

        private Text CreateText(Transform parent, string content, int fontSize, FontStyle style, Color color,
            Vector2 position, Vector2 size, TextAnchor alignment, bool stretchToParent = false)
        {
            GameObject textObject = new GameObject("Text - " + content, typeof(RectTransform), typeof(Text));
            textObject.transform.SetParent(parent, false);
            RectTransform rect = textObject.GetComponent<RectTransform>();

            if (stretchToParent)
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

            Text text = textObject.GetComponent<Text>();
            text.font = _font;
            text.text = content;
            text.fontSize = fontSize;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.raycastTarget = false;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            return text;
        }

        private static GameObject CreateImageObject(string name, Transform parent, Color color)
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

        private static void Stretch(RectTransform rect, Vector2 minOffset, Vector2 maxOffset)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = minOffset;
            rect.offsetMax = maxOffset;
        }

        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null)
                return;

            GameObject eventSystem = new GameObject("EventSystem", typeof(EventSystem));
            Type inputSystemModule = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (inputSystemModule != null)
                eventSystem.AddComponent(inputSystemModule);
            else
                eventSystem.AddComponent<StandaloneInputModule>();
        }

        private void OnDestroy()
        {
            if (_generator != null)
            {
                _generator.GenerationStarted -= HandleGenerationStarted;
                _generator.MazeBuilt -= HandleMazeBuilt;
            }
        }
    }

    internal static class LegacyTextExtensions
    {
        public static void characterSpacingCompat(this Text text, float spacing)
        {
            // Legacy UGUI Text has no character-spacing property. Kept as a no-op so the HUD code
            // stays dependency-free; swapping to TextMeshPro later can add actual tracking here.
        }
    }
}
