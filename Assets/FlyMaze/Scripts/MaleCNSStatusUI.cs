using UnityEngine;
using UnityEngine.UI;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    public sealed class MaleCNSStatusUI : MonoBehaviour
    {
        private static readonly Color Panel = new Color(0.018f, 0.028f, 0.040f, 0.90f);
        private static readonly Color Accent = new Color(0.12f, 0.92f, 0.76f, 1f);
        private static readonly Color Muted = new Color(0.48f, 0.62f, 0.72f, 1f);
        private static readonly Color Warning = new Color(1.00f, 0.62f, 0.22f, 1f);

        private MaleCNSBridge _brain;
        private MaleCNSFlyAgent _agent;
        private Text _status;
        private Text _activity;
        private Text _hint;
        private float _nextRefresh;
        private bool _initialized;

        public void Initialize(MaleCNSBridge brain, MaleCNSFlyAgent agent)
        {
            if (_initialized)
                return;

            _initialized = true;
            _brain = brain;
            _agent = agent;
            BuildUI();
        }

        private void Update()
        {
            if (!_initialized || _brain == null || Time.unscaledTime < _nextRefresh)
                return;

            _nextRefresh = Time.unscaledTime + 0.12f;
            if (_status != null)
            {
                _status.text = _brain.Status;
                _status.color = _brain.IsReady ? Accent : Warning;
            }

            if (_activity != null)
            {
                if (_brain.IsReady)
                {
                    string objective = _agent == null ? "--" : _agent.CurrentTargetKind == 0 ? "FOOD" : _agent.CurrentTargetKind == 1 ? "GOAL" : "NONE";
                    _activity.text =
                        $"SPIKES / TICK  {_brain.LastSpikeCount:N0}\n" +
                        $"DNa02  L {_brain.DNa02Left:0.00}  /  R {_brain.DNa02Right:0.00}\n" +
                        $"MOTOR  FWD {_brain.ForwardOutput:0.00}  /  TURN {_brain.TurnOutput:+0.00;-0.00;0.00}\n" +
                        $"OBJECTIVE  {objective}";
                }
                else
                {
                    _activity.text = "OFFICIAL CONNECTOME CACHE NOT ACTIVE\nRUN SETUP ONCE, THEN PRESS PLAY AGAIN";
                }
            }

            if (_hint != null)
                _hint.gameObject.SetActive(!_brain.IsReady);
        }

        private void BuildUI()
        {
            Canvas canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null)
                return;

            GameObject panel = new GameObject("MaleCNS Status Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(canvas.transform, false);
            RectTransform rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-26f, 26f);
            rect.sizeDelta = new Vector2(390f, 174f);
            panel.GetComponent<Image>().color = Panel;

            _status = CreateText(panel.transform, "MALECNS v1.0 / CHECKING...", 14, FontStyle.Bold, Accent,
                new Vector2(18f, -16f), new Vector2(354f, 26f));
            _activity = CreateText(panel.transform, "", 12, FontStyle.Bold, Muted,
                new Vector2(18f, -50f), new Vector2(354f, 82f));
            _hint = CreateText(panel.transform, "UNITY MENU:  FLY MAZE > MALECNS > SETUP v1.0", 11, FontStyle.Bold, Warning,
                new Vector2(18f, -139f), new Vector2(354f, 22f));
        }

        private static Text CreateText(Transform parent, string value, int size, FontStyle style, Color color, Vector2 pos, Vector2 dimensions)
        {
            GameObject go = new GameObject("Text - " + value, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = pos;
            rect.sizeDelta = dimensions;

            Text text = go.GetComponent<Text>();
            text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            text.text = value;
            text.fontSize = size;
            text.fontStyle = style;
            text.color = color;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }
    }
}
