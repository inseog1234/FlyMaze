using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    public sealed class HudWindowController : MonoBehaviour
    {
        private static readonly Color ChromeColor = new Color(0.020f, 0.034f, 0.046f, 0.97f);
        private static readonly Color ChromeAccent = new Color(0.12f, 0.92f, 0.76f, 1f);
        private static readonly Color ChromeText = new Color(0.78f, 0.89f, 0.95f, 1f);

        private readonly List<GameObject> _content = new List<GameObject>(32);

        private RectTransform _rect;
        private Canvas _canvas;
        private float _expandedHeight;
        private float _collapsedBodyHeight = 8f;
        private bool _collapsed;
        private bool _configured;
        private bool _userMoved;
        private Vector2 _userPosition;
        private Text _toggleText;

        public bool IsCollapsed => _collapsed;
        public bool UserMoved => _userMoved;

        public static HudWindowController AttachNamed(string objectName, string title)
        {
            GameObject target = GameObject.Find(objectName);
            if (target == null)
                return null;

            HudWindowController controller = target.GetComponent<HudWindowController>();
            if (controller == null)
                controller = target.AddComponent<HudWindowController>();
            controller.Configure(title);
            return controller;
        }

        public void Configure(string title)
        {
            if (_configured)
                return;

            _configured = true;
            _rect = transform as RectTransform;
            if (_rect == null)
                return;

            _canvas = GetComponentInParent<Canvas>();
            _expandedHeight = Mathf.Max(24f, _rect.sizeDelta.y);

            // Capture the HUD contents before adding the window chrome so collapse can hide
            // everything except the title bar itself.
            for (int i = 0; i < transform.childCount; i++)
            {
                Transform child = transform.GetChild(i);
                if (child != null)
                    _content.Add(child.gameObject);
            }

            CreateChrome(string.IsNullOrWhiteSpace(title) ? gameObject.name : title);
        }

        private void CreateChrome(string title)
        {
            GameObject chrome = new GameObject("Window Chrome", typeof(RectTransform), typeof(Image), typeof(HudWindowDragHandle));
            chrome.transform.SetParent(transform, false);
            RectTransform chromeRect = chrome.GetComponent<RectTransform>();
            chromeRect.anchorMin = new Vector2(0f, 1f);
            chromeRect.anchorMax = new Vector2(1f, 1f);
            chromeRect.pivot = new Vector2(0.5f, 0f);
            chromeRect.anchoredPosition = new Vector2(0f, 4f);
            chromeRect.sizeDelta = new Vector2(0f, 30f);
            chrome.GetComponent<Image>().color = ChromeColor;
            chrome.GetComponent<HudWindowDragHandle>().Bind(this);

            GameObject accent = new GameObject("Chrome Accent", typeof(RectTransform), typeof(Image));
            accent.transform.SetParent(chrome.transform, false);
            RectTransform accentRect = accent.GetComponent<RectTransform>();
            accentRect.anchorMin = new Vector2(0f, 0f);
            accentRect.anchorMax = new Vector2(0f, 1f);
            accentRect.pivot = new Vector2(0f, 0.5f);
            accentRect.anchoredPosition = Vector2.zero;
            accentRect.sizeDelta = new Vector2(3f, 0f);
            accent.GetComponent<Image>().color = ChromeAccent;

            GameObject titleObject = new GameObject("Window Title", typeof(RectTransform), typeof(Text));
            titleObject.transform.SetParent(chrome.transform, false);
            RectTransform titleRect = titleObject.GetComponent<RectTransform>();
            titleRect.anchorMin = Vector2.zero;
            titleRect.anchorMax = Vector2.one;
            titleRect.offsetMin = new Vector2(12f, 0f);
            titleRect.offsetMax = new Vector2(-48f, 0f);
            Text titleText = titleObject.GetComponent<Text>();
            titleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            titleText.text = title.ToUpperInvariant() + "   // DRAG";
            titleText.fontSize = 10;
            titleText.fontStyle = FontStyle.Bold;
            titleText.color = ChromeText;
            titleText.alignment = TextAnchor.MiddleLeft;
            titleText.raycastTarget = false;

            GameObject buttonObject = new GameObject("Collapse Button", typeof(RectTransform), typeof(Image), typeof(Button));
            buttonObject.transform.SetParent(chrome.transform, false);
            RectTransform buttonRect = buttonObject.GetComponent<RectTransform>();
            buttonRect.anchorMin = new Vector2(1f, 0.5f);
            buttonRect.anchorMax = new Vector2(1f, 0.5f);
            buttonRect.pivot = new Vector2(1f, 0.5f);
            buttonRect.anchoredPosition = new Vector2(-5f, 0f);
            buttonRect.sizeDelta = new Vector2(36f, 22f);
            Image buttonImage = buttonObject.GetComponent<Image>();
            buttonImage.color = new Color(0.06f, 0.10f, 0.13f, 1f);
            Button button = buttonObject.GetComponent<Button>();
            button.targetGraphic = buttonImage;
            button.onClick.AddListener(ToggleCollapsed);

            GameObject labelObject = new GameObject("Label", typeof(RectTransform), typeof(Text));
            labelObject.transform.SetParent(buttonObject.transform, false);
            RectTransform labelRect = labelObject.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            _toggleText = labelObject.GetComponent<Text>();
            _toggleText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            _toggleText.text = "−";
            _toggleText.fontSize = 16;
            _toggleText.fontStyle = FontStyle.Bold;
            _toggleText.color = ChromeAccent;
            _toggleText.alignment = TextAnchor.MiddleCenter;
            _toggleText.raycastTarget = false;
        }

        public void ToggleCollapsed()
        {
            SetCollapsed(!_collapsed);
        }

        public void SetCollapsed(bool collapsed)
        {
            if (_rect == null || _collapsed == collapsed)
                return;

            _collapsed = collapsed;
            for (int i = 0; i < _content.Count; i++)
            {
                if (_content[i] != null)
                    _content[i].SetActive(!collapsed);
            }

            SetHeightPreserveTop(collapsed ? _collapsedBodyHeight : _expandedHeight);
            if (_toggleText != null)
                _toggleText.text = collapsed ? "+" : "−";
        }

        private void SetHeightPreserveTop(float newHeight)
        {
            float oldHeight = _rect.sizeDelta.y;
            if (Mathf.Approximately(oldHeight, newHeight))
                return;

            Vector2 position = _rect.anchoredPosition;
            position.y += (1f - _rect.pivot.y) * (oldHeight - newHeight);
            Vector2 size = _rect.sizeDelta;
            size.y = newHeight;
            _rect.sizeDelta = size;
            _rect.anchoredPosition = position;

            if (_userMoved)
                _userPosition = position;
        }

        internal void BeginUserDrag()
        {
            if (_rect == null)
                return;
            _userMoved = true;
            _userPosition = _rect.anchoredPosition;
        }

        internal void DragBy(Vector2 screenDelta)
        {
            if (_rect == null)
                return;

            float scale = _canvas != null ? Mathf.Max(0.01f, _canvas.scaleFactor) : 1f;
            _userMoved = true;
            _userPosition += screenDelta / scale;
            _rect.anchoredPosition = _userPosition;
        }

        private void LateUpdate()
        {
            // Some HUDs have intro animations that still write anchoredPosition in Update().
            // Once the player drags a window, their chosen position wins from this point onward.
            if (_userMoved && _rect != null)
                _rect.anchoredPosition = _userPosition;
        }
    }

    internal sealed class HudWindowDragHandle : MonoBehaviour, IBeginDragHandler, IDragHandler
    {
        private HudWindowController _owner;

        public void Bind(HudWindowController owner)
        {
            _owner = owner;
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
                return;
            _owner?.BeginUserDrag();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left)
                return;
            _owner?.DragBy(eventData.delta);
        }
    }
}
