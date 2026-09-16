using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    public sealed class MaleCNSStatusUI : MonoBehaviour
    {
        private sealed class NodeView
        {
            public Image Background;
            public Image Pulse;
            public Text Value;
        }

        private static readonly Color Panel = new Color(0.018f, 0.028f, 0.040f, 0.94f);
        private static readonly Color Surface = new Color(0.045f, 0.065f, 0.085f, 0.98f);
        private static readonly Color Accent = new Color(0.12f, 0.92f, 0.76f, 1f);
        private static readonly Color Cyan = new Color(0.30f, 0.82f, 1f, 1f);
        private static readonly Color Orange = new Color(1.00f, 0.57f, 0.18f, 1f);
        private static readonly Color Pink = new Color(1.00f, 0.37f, 0.60f, 1f);
        private static readonly Color Muted = new Color(0.48f, 0.62f, 0.72f, 1f);
        private static readonly Color Warning = new Color(1.00f, 0.62f, 0.22f, 1f);
        private static readonly Color LineOff = new Color(0.17f, 0.25f, 0.31f, 0.58f);

        private readonly List<Image> _spikeDots = new List<Image>(24);
        private readonly List<Image> _afferentLines = new List<Image>(4);
        private readonly List<Image> _motorLines = new List<Image>(3);

        private MaleCNSBridge _brain;
        private MaleCNSFlyAgent _agent;
        private Text _status;
        private Text _activity;
        private Text _mode;
        private Text _hint;
        private Text _spikeLabel;
        private NodeView _visualLeft;
        private NodeView _visualRight;
        private NodeView _targetLeft;
        private NodeView _targetRight;
        private NodeView _dNa02;
        private NodeView _dNa01;
        private NodeView _forward;
        private Image _brainCore;
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
            if (!_initialized || _brain == null)
                return;

            AnimateSpikeField();

            if (Time.unscaledTime < _nextRefresh)
                return;

            _nextRefresh = Time.unscaledTime + 0.08f;
            RefreshTextAndPathway();
        }

        private void RefreshTextAndPathway()
        {
            if (_status != null)
            {
                _status.text = _brain.Status;
                _status.color = _brain.IsReady ? Accent : Warning;
            }

            MaleCNSSensoryFrame s = _brain.LatestSensoryFrame;
            float visualL = Mathf.Clamp01(s.LeftObstacle * 0.85f + s.FrontObstacle * 0.55f);
            float visualR = Mathf.Clamp01(s.RightObstacle * 0.85f + s.FrontObstacle * 0.55f);
            float leftBias = Mathf.Max(0f, -s.TargetBearing);
            float rightBias = Mathf.Max(0f, s.TargetBearing);
            float targetL = s.TargetStrength * Mathf.Clamp01(0.18f + leftBias * 0.82f);
            float targetR = s.TargetStrength * Mathf.Clamp01(0.18f + rightBias * 0.82f);

            SetNode(_visualLeft, visualL, Cyan, $"{visualL:0.00}");
            SetNode(_visualRight, visualR, Cyan, $"{visualR:0.00}");
            SetNode(_targetLeft, targetL, Orange, $"{targetL:0.00}");
            SetNode(_targetRight, targetR, Orange, $"{targetR:0.00}");

            float d02 = Mathf.Clamp01((_brain.DNa02Left + _brain.DNa02Right) * 0.5f);
            float d01 = Mathf.Clamp01((_brain.DNa01Left + _brain.DNa01Right) * 0.5f);
            float fwd = Mathf.Clamp01((_brain.ForwardLeft + _brain.ForwardRight) * 0.5f);
            SetNode(_dNa02, d02, Pink, $"L {_brain.DNa02Left:0.00}  R {_brain.DNa02Right:0.00}");
            SetNode(_dNa01, d01, Accent, $"L {_brain.DNa01Left:0.00}  R {_brain.DNa01Right:0.00}");
            SetNode(_forward, fwd, Orange, $"L {_brain.ForwardLeft:0.00}  R {_brain.ForwardRight:0.00}");

            float brainActivity = Mathf.Clamp01(Mathf.Log10(_brain.LastSpikeCount + 1f) / 4.3f);
            if (_brainCore != null)
                _brainCore.color = Color.Lerp(Surface, new Color(0.09f, 0.55f, 0.48f, 0.98f), brainActivity);

            SetLines(_afferentLines, Mathf.Max(Mathf.Max(visualL, visualR), Mathf.Max(targetL, targetR)), Cyan);
            SetLines(_motorLines, Mathf.Max(d02, Mathf.Max(d01, fwd)), Accent);

            if (_spikeLabel != null)
                _spikeLabel.text = $"LIVE SPIKE FIELD  /  {_brain.LastSpikeCount:N0} SPIKES";

            if (_activity != null)
            {
                string objective = _agent == null ? "--" : _agent.CurrentTargetKind == 0 ? "FOOD" : _agent.CurrentTargetKind == 1 ? "GOAL" : "NONE";
                _activity.text =
                    $"TURN {_brain.TurnOutput,+6:0.00;-0.00;0.00}    FWD {_brain.ForwardOutput:0.00}    ESC {_brain.EscapeOutput:0.00}\n" +
                    $"TARGET {objective}    BEARING {s.TargetBearing,+6:0.00;-0.00;0.00}    SIGNAL {s.TargetStrength:0.00}";
            }

            if (_mode != null)
            {
                bool recovery = _agent != null && _agent.RecoveryActive;
                _mode.text = recovery ? "● COLLISION REFLEX / RECOVERING" : "● CONNECTOME CONTROL / LIVE";
                _mode.color = recovery ? Warning : Accent;
            }

            if (_hint != null)
                _hint.gameObject.SetActive(!_brain.IsReady);
        }

        private void AnimateSpikeField()
        {
            if (_spikeDots.Count == 0 || _brain == null)
                return;

            float activity = Mathf.Clamp01(Mathf.Log10(_brain.LastSpikeCount + 1f) / 4.0f);
            float time = Time.unscaledTime;
            for (int i = 0; i < _spikeDots.Count; i++)
            {
                float wave = 0.5f + 0.5f * Mathf.Sin(time * (4.1f + (i % 5) * 0.43f) + i * 1.73f);
                float on = Mathf.SmoothStep(0.62f, 0.95f, wave + activity * 0.45f);
                Color c = Color.Lerp(new Color(0.10f, 0.16f, 0.20f, 0.45f), Accent, on);
                c.a = Mathf.Lerp(0.32f, 0.98f, on);
                _spikeDots[i].color = c;
            }
        }

        private void BuildUI()
        {
            Canvas canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null)
                return;

            GameObject panel = new GameObject("MaleCNS Neural Monitor", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(canvas.transform, false);
            RectTransform rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(1f, 0f);
            rect.anchoredPosition = new Vector2(-26f, 26f);
            rect.sizeDelta = new Vector2(570f, 402f);
            panel.GetComponent<Image>().color = Panel;

            _status = CreateText(panel.transform, "MALECNS v1.0 / CHECKING...", 14, FontStyle.Bold, Accent,
                new Vector2(18f, -14f), new Vector2(530f, 24f));
            _mode = CreateText(panel.transform, "● CONNECTOME CONTROL / LIVE", 11, FontStyle.Bold, Accent,
                new Vector2(18f, -39f), new Vector2(530f, 20f));

            CreateText(panel.transform, "SENSORY AFFERENTS", 10, FontStyle.Bold, Muted,
                new Vector2(18f, -70f), new Vector2(150f, 18f));
            CreateText(panel.transform, "MALECNS v1.0", 10, FontStyle.Bold, Muted,
                new Vector2(216f, -70f), new Vector2(145f, 18f));
            CreateText(panel.transform, "DESCENDING MOTOR", 10, FontStyle.Bold, Muted,
                new Vector2(396f, -70f), new Vector2(155f, 18f));

            _visualLeft = CreateNode(panel.transform, "LC4/LPLC2  L", new Vector2(18f, -96f), new Vector2(145f, 48f));
            _visualRight = CreateNode(panel.transform, "LC4/LPLC2  R", new Vector2(18f, -151f), new Vector2(145f, 48f));
            _targetLeft = CreateNode(panel.transform, "ORN / LC10  L", new Vector2(18f, -206f), new Vector2(145f, 48f));
            _targetRight = CreateNode(panel.transform, "ORN / LC10  R", new Vector2(18f, -261f), new Vector2(145f, 48f));

            GameObject core = new GameObject("Connectome Core", typeof(RectTransform), typeof(Image));
            core.transform.SetParent(panel.transform, false);
            RectTransform coreRect = core.GetComponent<RectTransform>();
            SetTopLeft(coreRect, new Vector2(205f, -101f), new Vector2(150f, 202f));
            _brainCore = core.GetComponent<Image>();
            _brainCore.color = Surface;

            CreateText(core.transform, "166,700\nNEURONS", 18, FontStyle.Bold, new Color(0.88f, 0.96f, 1f, 1f),
                new Vector2(14f, -13f), new Vector2(122f, 48f));
            CreateText(core.transform, "25.58M SYNAPTIC\nCONNECTIONS", 10, FontStyle.Bold, Muted,
                new Vector2(14f, -61f), new Vector2(122f, 34f));
            _spikeLabel = CreateText(core.transform, "LIVE SPIKE FIELD", 9, FontStyle.Bold, Accent,
                new Vector2(14f, -103f), new Vector2(122f, 18f));
            BuildSpikeField(core.transform);

            _dNa02 = CreateNode(panel.transform, "DNa02 / HIGH STEER", new Vector2(397f, -105f), new Vector2(155f, 54f));
            _dNa01 = CreateNode(panel.transform, "DNa01 / LOW STEER", new Vector2(397f, -169f), new Vector2(155f, 54f));
            _forward = CreateNode(panel.transform, "DNg100 / FORWARD", new Vector2(397f, -233f), new Vector2(155f, 54f));

            _afferentLines.Add(CreateLine(panel.transform, new Vector2(163f, -120f), new Vector2(205f, -142f), LineOff));
            _afferentLines.Add(CreateLine(panel.transform, new Vector2(163f, -175f), new Vector2(205f, -167f), LineOff));
            _afferentLines.Add(CreateLine(panel.transform, new Vector2(163f, -230f), new Vector2(205f, -218f), LineOff));
            _afferentLines.Add(CreateLine(panel.transform, new Vector2(163f, -285f), new Vector2(205f, -243f), LineOff));

            _motorLines.Add(CreateLine(panel.transform, new Vector2(355f, -145f), new Vector2(397f, -132f), LineOff));
            _motorLines.Add(CreateLine(panel.transform, new Vector2(355f, -192f), new Vector2(397f, -196f), LineOff));
            _motorLines.Add(CreateLine(panel.transform, new Vector2(355f, -239f), new Vector2(397f, -260f), LineOff));

            _activity = CreateText(panel.transform, "", 11, FontStyle.Bold, Muted,
                new Vector2(18f, -326f), new Vector2(534f, 43f));
            _hint = CreateText(panel.transform, "RUN: FLY MAZE > MALECNS > SETUP v1.0", 10, FontStyle.Bold, Warning,
                new Vector2(18f, -374f), new Vector2(534f, 18f));
        }

        private void BuildSpikeField(Transform parent)
        {
            const int columns = 6;
            const int rows = 4;
            for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
            {
                GameObject dot = new GameObject($"Spike {x}-{y}", typeof(RectTransform), typeof(Image));
                dot.transform.SetParent(parent, false);
                RectTransform r = dot.GetComponent<RectTransform>();
                SetTopLeft(r, new Vector2(16f + x * 20f, -132f - y * 15f), new Vector2(7f, 7f));
                Image image = dot.GetComponent<Image>();
                image.color = new Color(0.10f, 0.16f, 0.20f, 0.45f);
                _spikeDots.Add(image);
            }
        }

        private NodeView CreateNode(Transform parent, string label, Vector2 pos, Vector2 size)
        {
            GameObject go = new GameObject(label, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            SetTopLeft(rect, pos, size);
            Image bg = go.GetComponent<Image>();
            bg.color = Surface;

            CreateText(go.transform, label, 9, FontStyle.Bold, Muted,
                new Vector2(10f, -7f), new Vector2(size.x - 20f, 17f));
            Text value = CreateText(go.transform, "0.00", 11, FontStyle.Bold, new Color(0.91f, 0.97f, 1f, 1f),
                new Vector2(10f, -25f), new Vector2(size.x - 34f, 18f));

            GameObject pulse = new GameObject("Activity", typeof(RectTransform), typeof(Image));
            pulse.transform.SetParent(go.transform, false);
            RectTransform pulseRect = pulse.GetComponent<RectTransform>();
            SetTopLeft(pulseRect, new Vector2(size.x - 19f, -26f), new Vector2(8f, 8f));
            Image pulseImage = pulse.GetComponent<Image>();
            pulseImage.color = LineOff;

            return new NodeView { Background = bg, Pulse = pulseImage, Value = value };
        }

        private static void SetNode(NodeView node, float value, Color active, string text)
        {
            if (node == null)
                return;
            value = Mathf.Clamp01(value);
            if (node.Background != null)
                node.Background.color = Color.Lerp(Surface, new Color(active.r * 0.24f, active.g * 0.24f, active.b * 0.24f, 0.98f), value);
            if (node.Pulse != null)
                node.Pulse.color = Color.Lerp(LineOff, active, value);
            if (node.Value != null)
                node.Value.text = text;
        }

        private static void SetLines(List<Image> lines, float activity, Color active)
        {
            activity = Mathf.Clamp01(activity);
            for (int i = 0; i < lines.Count; i++)
            {
                Color c = Color.Lerp(LineOff, active, activity);
                c.a = Mathf.Lerp(0.35f, 0.96f, activity);
                lines[i].color = c;
            }
        }

        private static Image CreateLine(Transform parent, Vector2 a, Vector2 b, Color color)
        {
            GameObject go = new GameObject("Neural Path", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            Vector2 delta = b - a;
            rect.anchoredPosition = (a + b) * 0.5f;
            rect.sizeDelta = new Vector2(delta.magnitude, 2f);
            rect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
            Image image = go.GetComponent<Image>();
            image.color = color;
            return image;
        }

        private static Text CreateText(Transform parent, string value, int size, FontStyle style, Color color, Vector2 pos, Vector2 dimensions)
        {
            GameObject go = new GameObject("Text - " + value, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            SetTopLeft(rect, pos, dimensions);

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
