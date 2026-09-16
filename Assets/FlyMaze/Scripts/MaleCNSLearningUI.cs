using UnityEngine;
using UnityEngine.UI;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    public sealed class MaleCNSLearningUI : MonoBehaviour
    {
        private static readonly Color Panel = new Color(0.018f, 0.028f, 0.040f, 0.94f);
        private static readonly Color Surface = new Color(0.045f, 0.065f, 0.085f, 0.98f);
        private static readonly Color Accent = new Color(0.12f, 0.92f, 0.76f, 1f);
        private static readonly Color Reward = new Color(1.00f, 0.74f, 0.18f, 1f);
        private static readonly Color Punish = new Color(1.00f, 0.32f, 0.42f, 1f);
        private static readonly Color TextMain = new Color(0.92f, 0.97f, 1f, 1f);
        private static readonly Color Muted = new Color(0.50f, 0.62f, 0.72f, 1f);

        private MaleCNSBridge _brain;
        private Text _reward;
        private Text _punish;
        private Text _plasticity;
        private Text _events;
        private Image _rewardBar;
        private Image _punishBar;
        private float _nextRefresh;
        private bool _initialized;

        public void Initialize(MaleCNSBridge brain)
        {
            if (_initialized || brain == null)
                return;
            _initialized = true;
            _brain = brain;
            BuildUI();
        }

        private void Update()
        {
            if (!_initialized || _brain == null || Time.unscaledTime < _nextRefresh)
                return;
            _nextRefresh = Time.unscaledTime + 0.08f;

            if (_reward != null)
                _reward.text = $"PAM REWARD     {_brain.PamActivity:0.00}" + (_brain.LastRewardPulse > 0f ? $"   BURST +{_brain.LastRewardPulse:0.0}" : string.Empty);
            if (_punish != null)
                _punish.text = $"PPL1 AVERSIVE  {_brain.Ppl1Activity:0.00}" + (_brain.LastPunishmentPulse > 0f ? $"   BURST +{_brain.LastPunishmentPulse:0.0}" : string.Empty);
            if (_plasticity != null)
                _plasticity.text = $"KC→MBON PERSISTENT PLASTICITY\n{_brain.LearnedSynapseCount:N0} SYNAPSES   |   Σ|ΔW| {_brain.PlasticityMagnitude:0.000}";
            if (_events != null)
                _events.text = $"REINFORCEMENT EVENTS  {_brain.ReinforcementEventCount:N0}   •   SAVED LOCALLY";

            if (_rewardBar != null)
                _rewardBar.fillAmount = Mathf.Clamp01(Mathf.Max(_brain.PamActivity, _brain.LastRewardPulse * 0.7f));
            if (_punishBar != null)
                _punishBar.fillAmount = Mathf.Clamp01(Mathf.Max(_brain.Ppl1Activity, _brain.LastPunishmentPulse * 0.7f));
        }

        private void BuildUI()
        {
            Canvas canvas = FindFirstObjectByType<Canvas>();
            if (canvas == null)
                return;

            GameObject panel = new GameObject("MaleCNS Learning Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(canvas.transform, false);
            RectTransform rect = panel.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.anchoredPosition = new Vector2(-26f, -96f);
            rect.sizeDelta = new Vector2(430f, 206f);
            panel.GetComponent<Image>().color = Panel;

            Shadow shadow = panel.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.50f);
            shadow.effectDistance = new Vector2(8f, -8f);

            CreateText(panel.transform, "REINFORCEMENT / LONG-TERM MEMORY", 12, FontStyle.Bold, Accent,
                new Vector2(18f, -16f), new Vector2(390f, 22f), TextAnchor.MiddleLeft);

            _reward = CreateText(panel.transform, "PAM REWARD", 11, FontStyle.Bold, Reward,
                new Vector2(18f, -50f), new Vector2(394f, 20f), TextAnchor.MiddleLeft);
            _rewardBar = CreateBar(panel.transform, new Vector2(18f, -75f), new Vector2(394f, 8f), Reward);

            _punish = CreateText(panel.transform, "PPL1 AVERSIVE", 11, FontStyle.Bold, Punish,
                new Vector2(18f, -93f), new Vector2(394f, 20f), TextAnchor.MiddleLeft);
            _punishBar = CreateBar(panel.transform, new Vector2(18f, -118f), new Vector2(394f, 8f), Punish);

            _plasticity = CreateText(panel.transform, "KC→MBON PERSISTENT PLASTICITY\n0 SYNAPSES   |   Σ|ΔW| 0.000", 11, FontStyle.Bold, TextMain,
                new Vector2(18f, -137f), new Vector2(394f, 42f), TextAnchor.UpperLeft);
            _events = CreateText(panel.transform, "REINFORCEMENT EVENTS  0   •   SAVED LOCALLY", 9, FontStyle.Bold, Muted,
                new Vector2(18f, -181f), new Vector2(394f, 17f), TextAnchor.MiddleLeft);
        }

        private static Image CreateBar(Transform parent, Vector2 position, Vector2 size, Color color)
        {
            GameObject track = new GameObject("Track", typeof(RectTransform), typeof(Image));
            track.transform.SetParent(parent, false);
            SetTopLeft(track.GetComponent<RectTransform>(), position, size);
            track.GetComponent<Image>().color = Surface;

            GameObject fill = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fill.transform.SetParent(track.transform, false);
            RectTransform fillRect = fill.GetComponent<RectTransform>();
            fillRect.anchorMin = Vector2.zero;
            fillRect.anchorMax = Vector2.one;
            fillRect.offsetMin = Vector2.zero;
            fillRect.offsetMax = Vector2.zero;
            Image image = fill.GetComponent<Image>();
            image.color = color;
            image.type = Image.Type.Filled;
            image.fillMethod = Image.FillMethod.Horizontal;
            image.fillOrigin = 0;
            image.fillAmount = 0f;
            return image;
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
