using UnityEngine;
using UnityEngine.UI;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    public sealed class MaleCNSAutoSetupUI : MonoBehaviour
    {
        private static readonly Color Backdrop = new Color(0.005f, 0.008f, 0.014f, 0.82f);
        private static readonly Color Panel = new Color(0.018f, 0.030f, 0.043f, 0.98f);
        private static readonly Color Surface = new Color(0.055f, 0.075f, 0.095f, 1f);
        private static readonly Color Accent = new Color(0.12f, 0.92f, 0.76f, 1f);
        private static readonly Color Warning = new Color(1f, 0.54f, 0.20f, 1f);
        private static readonly Color TextMain = new Color(0.92f, 0.97f, 1f, 1f);
        private static readonly Color TextMuted = new Color(0.55f, 0.67f, 0.76f, 1f);

        private MaleCNSBridge _brain;
        private GameObject _root;
        private Image _fill;
        private Text _title;
        private Text _stage;
        private Text _detail;
        private Text _percent;
        private Text _foot;
        private Button _retry;
        private float _shownProgress;
        private bool _initialized;

        public void Initialize(MaleCNSBridge brain)
        {
            if (_initialized || brain == null)
                return;

            _initialized = true;
            _brain = brain;
            BuildUI();
            Refresh(true);
        }

        private void Update()
        {
            if (!_initialized || _brain == null || _root == null)
                return;

            bool visible = _brain.ShouldShowSetupUI;
            if (_root.activeSelf != visible)
                _root.SetActive(visible);
            if (!visible)
                return;

            _shownProgress = Mathf.MoveTowards(_shownProgress, _brain.SetupProgress, Time.unscaledDeltaTime * 0.24f);
            Refresh(false);
        }

        private void Refresh(bool snap)
        {
            if (_brain == null || _root == null)
                return;

            if (snap)
                _shownProgress = _brain.SetupProgress;

            float progress = Mathf.Clamp01(_shownProgress);
            if (_fill != null)
                _fill.fillAmount = progress;
            if (_percent != null)
                _percent.text = Mathf.RoundToInt(progress * 100f).ToString("00") + "%";
            if (_stage != null)
                _stage.text = string.IsNullOrEmpty(_brain.SetupStage) ? "PREPARING MALECNS v1.0" : _brain.SetupStage;
            if (_detail != null)
                _detail.text = string.IsNullOrEmpty(_brain.SetupDetail)
                    ? "Preparing the official MaleCNS dataset for this computer..."
                    : _brain.SetupDetail;

            bool failed = !string.IsNullOrEmpty(_brain.SetupError);
            if (_title != null)
            {
                _title.text = failed ? "MALECNS SETUP NEEDS ATTENTION" : "FIRST PLAY / MALECNS v1.0 AUTO SETUP";
                _title.color = failed ? Warning : Accent;
            }
            if (_retry != null)
                _retry.gameObject.SetActive(failed);
            if (_foot != null)
            {
                _foot.text = failed
                    ? "Python 3 is required. Fix the issue, then RETRY. Partial downloads resume automatically."
                    : "ONE-TIME LOCAL SETUP  •  ~1.2 GB  •  Library/MaleCNS  •  DO NOT CLOSE PLAY MODE";
                _foot.color = failed ? Warning : TextMuted;
            }
        }

        private void BuildUI()
        {
            GameObject canvasObject = new GameObject("MaleCNS Auto Setup Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            Canvas canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 1000;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            _root = new GameObject("MaleCNS Auto Setup Overlay", typeof(RectTransform), typeof(Image));
            _root.transform.SetParent(canvasObject.transform, false);
            RectTransform rootRect = _root.GetComponent<RectTransform>();
            rootRect.anchorMin = Vector2.zero;
            rootRect.anchorMax = Vector2.one;
            rootRect.offsetMin = Vector2.zero;
            rootRect.offsetMax = Vector2.zero;
            _root.GetComponent<Image>().color = Backdrop;

            GameObject panelObject = new GameObject("Setup Progress Panel", typeof(RectTransform), typeof(Image));
            panelObject.transform.SetParent(_root.transform, false);
            RectTransform panelRect = panelObject.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0.5f, 0.5f);
            panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(760f, 300f);
            panelObject.GetComponent<Image>().color = Panel;

            Shadow shadow = panelObject.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.75f);
            shadow.effectDistance = new Vector2(12f, -12f);

            _title = CreateText(panelObject.transform, "FIRST PLAY / MALECNS v1.0 AUTO SETUP", 18, FontStyle.Bold, Accent,
                new Vector2(30f, -25f), new Vector2(680f, 28f), TextAnchor.MiddleLeft);
            _stage = CreateText(panelObject.transform, "PREPARING MALECNS v1.0", 28, FontStyle.Bold, TextMain,
                new Vector2(30f, -67f), new Vector2(620f, 44f), TextAnchor.MiddleLeft);
            _percent = CreateText(panelObject.transform, "00%", 28, FontStyle.Bold, Accent,
                new Vector2(645f, -67f), new Vector2(85f, 44f), TextAnchor.MiddleRight);

            GameObject bar = new GameObject("Progress Track", typeof(RectTransform), typeof(Image));
            bar.transform.SetParent(panelObject.transform, false);
            RectTransform barRect = bar.GetComponent<RectTransform>();
            SetTopLeft(barRect, new Vector2(30f, -126f), new Vector2(700f, 18f));
            bar.GetComponent<Image>().color = Surface;

            GameObject fillObject = new GameObject("Progress Fill", typeof(RectTransform), typeof(Image));
            fillObject.transform.SetParent(bar.transform, false);
            RectTransform fillRect = fillObject.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = new Vector2(2f, 2f);
            fillRect.offsetMax = new Vector2(-2f, -2f);
            _fill = fillObject.GetComponent<Image>();
            _fill.color = Accent;
            _fill.type = Image.Type.Filled;
            _fill.fillMethod = Image.FillMethod.Horizontal;
            _fill.fillOrigin = 0;
            _fill.fillAmount = 0f;

            _detail = CreateText(panelObject.transform, "", 12, FontStyle.Normal, TextMuted,
                new Vector2(30f, -162f), new Vector2(700f, 58f), TextAnchor.UpperLeft);
            _foot = CreateText(panelObject.transform, "", 11, FontStyle.Bold, TextMuted,
                new Vector2(30f, -242f), new Vector2(550f, 30f), TextAnchor.MiddleLeft);

            _retry = CreateButton(panelObject.transform, "RETRY", new Vector2(610f, -232f), new Vector2(120f, 40f));
            _retry.onClick.AddListener(() => _brain?.BeginAutomaticSetup());
            _retry.gameObject.SetActive(false);
        }

        private static Text CreateText(Transform parent, string value, int size, FontStyle style, Color color,
            Vector2 position, Vector2 dimensions, TextAnchor alignment)
        {
            GameObject go = new GameObject("Text - " + value, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            SetTopLeft(rect, position, dimensions);
            Text text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = alignment;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private static Button CreateButton(Transform parent, string label, Vector2 position, Vector2 size)
        {
            GameObject go = new GameObject(label + " Button", typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            SetTopLeft(go.GetComponent<RectTransform>(), position, size);
            Image image = go.GetComponent<Image>();
            image.color = new Color(0.10f, 0.36f, 0.32f, 1f);
            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;
            Text text = CreateText(go.transform, label, 12, FontStyle.Bold, TextMain, Vector2.zero, size, TextAnchor.MiddleCenter);
            RectTransform textRect = text.rectTransform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            return button;
        }

        private static void SetTopLeft(RectTransform rect, Vector2 position, Vector2 size)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
        }
    }
}
