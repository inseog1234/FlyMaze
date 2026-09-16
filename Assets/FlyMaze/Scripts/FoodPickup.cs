using System.Collections;
using UnityEngine;

namespace FlyMaze
{
    public sealed class FoodPickup : MonoBehaviour
    {
        public Vector2Int Cell { get; private set; }
        public bool IsCollected { get; private set; }

        private FoodPlacementController _owner;
        private Transform _visualRoot;
        private Transform _innerBeam;
        private Transform _outerBeam;
        private Light _glowLight;
        private Vector3 _visualBasePosition;
        private Vector3 _innerBeamBaseScale;
        private Vector3 _outerBeamBaseScale;
        private float _phase;

        internal void Initialize(
            FoodPlacementController owner,
            Vector2Int cell,
            Transform visualRoot,
            Transform innerBeam,
            Transform outerBeam,
            Light glowLight,
            float phase)
        {
            _owner = owner;
            Cell = cell;
            _visualRoot = visualRoot;
            _innerBeam = innerBeam;
            _outerBeam = outerBeam;
            _glowLight = glowLight;
            _phase = phase;

            if (_visualRoot != null)
                _visualBasePosition = _visualRoot.localPosition;
            if (_innerBeam != null)
                _innerBeamBaseScale = _innerBeam.localScale;
            if (_outerBeam != null)
                _outerBeamBaseScale = _outerBeam.localScale;
        }

        private void Update()
        {
            if (IsCollected)
                return;

            float time = Time.unscaledTime + _phase;
            float pulse = 0.5f + 0.5f * Mathf.Sin(time * 3.25f);

            if (_visualRoot != null)
            {
                _visualRoot.localPosition = _visualBasePosition + Vector3.up * (0.055f + Mathf.Sin(time * 2.2f) * 0.055f);
                _visualRoot.Rotate(Vector3.up, 34f * Time.unscaledDeltaTime, Space.Self);
            }

            if (_innerBeam != null)
            {
                Vector3 scale = _innerBeamBaseScale;
                scale.x *= Mathf.Lerp(0.80f, 1.13f, pulse);
                scale.z *= Mathf.Lerp(0.80f, 1.13f, pulse);
                _innerBeam.localScale = scale;
            }

            if (_outerBeam != null)
            {
                Vector3 scale = _outerBeamBaseScale;
                scale.x *= Mathf.Lerp(0.90f, 1.12f, 1f - pulse);
                scale.z *= Mathf.Lerp(0.90f, 1.12f, 1f - pulse);
                _outerBeam.localScale = scale;
            }

            if (_glowLight != null)
                _glowLight.intensity = Mathf.Lerp(2.6f, 4.4f, pulse);
        }

        public void Collect()
        {
            if (IsCollected)
                return;

            IsCollected = true;
            _owner?.NotifyCollected(this);
            StartCoroutine(CollectRoutine());
        }

        private IEnumerator CollectRoutine()
        {
            Vector3 startScale = transform.localScale;
            float startIntensity = _glowLight != null ? _glowLight.intensity : 0f;
            float elapsed = 0f;
            const float duration = 0.18f;

            while (elapsed < duration)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / duration);
                float eased = t * t;
                transform.localScale = Vector3.Lerp(startScale, Vector3.zero, eased);

                if (_glowLight != null)
                    _glowLight.intensity = Mathf.Lerp(startIntensity, 0f, t);

                yield return null;
            }

            Destroy(gameObject);
        }
    }
}
