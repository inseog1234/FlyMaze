using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace FlyMaze
{
    [RequireComponent(typeof(Image))]
    public sealed class HudButtonFX : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler, IPointerDownHandler, IPointerUpHandler
    {
        private RectTransform _rect;
        private Image _image;
        private Color _baseColor;
        private Color _hoverColor;
        private Vector3 _targetScale = Vector3.one;
        private bool _hovered;

        private void Awake()
        {
            _rect = transform as RectTransform;
            _image = GetComponent<Image>();
            _baseColor = _image.color;
            _hoverColor = Color.Lerp(_baseColor, Color.white, 0.14f);
        }

        private void Update()
        {
            if (_rect != null)
                _rect.localScale = Vector3.Lerp(_rect.localScale, _targetScale, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));

            if (_image != null)
            {
                Color target = _hovered ? _hoverColor : _baseColor;
                _image.color = Color.Lerp(_image.color, target, 1f - Mathf.Exp(-14f * Time.unscaledDeltaTime));
            }
        }

        public void SetBaseColor(Color color)
        {
            _baseColor = color;
            _hoverColor = Color.Lerp(color, Color.white, 0.14f);
            if (!_hovered && _image != null)
                _image.color = color;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _hovered = true;
            _targetScale = Vector3.one * 1.025f;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            _hovered = false;
            _targetScale = Vector3.one;
        }

        public void OnPointerDown(PointerEventData eventData)
        {
            _targetScale = Vector3.one * 0.965f;
        }

        public void OnPointerUp(PointerEventData eventData)
        {
            _targetScale = _hovered ? Vector3.one * 1.025f : Vector3.one;
        }
    }
}
