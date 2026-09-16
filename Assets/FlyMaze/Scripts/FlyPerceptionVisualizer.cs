using System.Collections.Generic;
using UnityEngine;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    public sealed class FlyPerceptionVisualizer : MonoBehaviour
    {
        [Header("Field of View")]
        [SerializeField, Range(30f, 170f)] private float viewAngle = 105f;
        [SerializeField] private float viewRange = 3.4f;
        [SerializeField, Range(8, 64)] private int viewSegments = 28;

        [Header("Trail")]
        [SerializeField] private float trailMaxDistance = 10f;
        [SerializeField] private float trailPointSpacing = 0.12f;
        [SerializeField] private float trailWidth = 0.055f;

        private MaleCNSFlyAgent _agent;
        private Mesh _fovMesh;
        private Material _fovMaterial;
        private LineRenderer _fovOutline;
        private LineRenderer _trail;
        private Material _lineMaterial;
        private readonly List<Vector3> _trailPoints = new List<Vector3>(160);
        private readonly List<Vector3> _outlinePoints = new List<Vector3>(80);

        public void Bind(MaleCNSFlyAgent agent)
        {
            _agent = agent;
            EnsureVisuals();
            ResetTrail();
        }

        private void Awake()
        {
            if (_agent == null)
                _agent = GetComponent<MaleCNSFlyAgent>();
            EnsureVisuals();
        }

        private void LateUpdate()
        {
            if (_agent == null)
                return;

            UpdateFieldOfView();
            UpdateTrail();
        }

        private void EnsureVisuals()
        {
            if (_fovMesh != null)
                return;

            // Sprites/Default reliably honours alpha in URP without runtime blend-keyword setup.
            // The previous URP Unlit material could render the cone nearly opaque on some setups.
            Shader transparent = Shader.Find("Sprites/Default");
            if (transparent == null) transparent = Shader.Find("Universal Render Pipeline/Unlit");
            if (transparent == null) transparent = Shader.Find("Unlit/Color");

            _fovMaterial = new Material(transparent) { name = "Fly FOV Material" };
            Color fovColor = new Color(0.12f, 0.92f, 0.76f, 0.075f);
            if (_fovMaterial.HasProperty("_BaseColor")) _fovMaterial.SetColor("_BaseColor", fovColor);
            if (_fovMaterial.HasProperty("_Color")) _fovMaterial.SetColor("_Color", fovColor);
            if (_fovMaterial.HasProperty("_Surface")) _fovMaterial.SetFloat("_Surface", 1f);
            if (_fovMaterial.HasProperty("_ZWrite")) _fovMaterial.SetFloat("_ZWrite", 0f);
            _fovMaterial.renderQueue = 3000;

            GameObject fov = new GameObject("Vision Cone", typeof(MeshFilter), typeof(MeshRenderer));
            fov.transform.SetParent(transform, false);
            fov.transform.localPosition = new Vector3(0f, 0.06f, 0f);
            _fovMesh = new Mesh { name = "Fly Vision Cone Mesh" };
            fov.GetComponent<MeshFilter>().sharedMesh = _fovMesh;
            MeshRenderer meshRenderer = fov.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = _fovMaterial;
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            meshRenderer.receiveShadows = false;

            Shader lineShader = Shader.Find("Sprites/Default");
            if (lineShader == null) lineShader = Shader.Find("Unlit/Color");
            _lineMaterial = new Material(lineShader) { name = "Fly Sensor Line Material" };

            GameObject outline = new GameObject("Vision Cone Outline", typeof(LineRenderer));
            outline.transform.SetParent(transform, false);
            _fovOutline = outline.GetComponent<LineRenderer>();
            _fovOutline.useWorldSpace = false;
            _fovOutline.loop = false;
            _fovOutline.startWidth = 0.025f;
            _fovOutline.endWidth = 0.025f;
            _fovOutline.sharedMaterial = _lineMaterial;
            _fovOutline.numCapVertices = 2;
            _fovOutline.numCornerVertices = 2;
            Color outlineColor = new Color(0.26f, 1f, 0.86f, 0.65f);
            _fovOutline.startColor = outlineColor;
            _fovOutline.endColor = outlineColor;

            GameObject trailObject = new GameObject("Movement Trail", typeof(LineRenderer));
            trailObject.transform.SetParent(transform, false);
            _trail = trailObject.GetComponent<LineRenderer>();
            _trail.useWorldSpace = true;
            _trail.sharedMaterial = _lineMaterial;
            _trail.startWidth = trailWidth;
            _trail.endWidth = trailWidth * 0.55f;
            _trail.numCapVertices = 3;
            _trail.numCornerVertices = 3;
            _trail.textureMode = LineTextureMode.Stretch;

            Gradient gradient = new Gradient();
            gradient.SetKeys(
                new[]
                {
                    new GradientColorKey(new Color(0.11f, 0.52f, 0.48f), 0f),
                    new GradientColorKey(new Color(0.12f, 0.92f, 0.76f), 1f)
                },
                new[]
                {
                    new GradientAlphaKey(0f, 0f),
                    new GradientAlphaKey(0.18f, 0.22f),
                    new GradientAlphaKey(0.82f, 0.72f),
                    new GradientAlphaKey(0.95f, 1f)
                });
            _trail.colorGradient = gradient;
        }

        private void UpdateFieldOfView()
        {
            int segments = Mathf.Max(8, viewSegments);
            Vector3[] vertices = new Vector3[segments + 2];
            int[] triangles = new int[segments * 3];
            vertices[0] = Vector3.up * 0.015f;

            _outlinePoints.Clear();
            _outlinePoints.Add(Vector3.up * 0.02f);

            Vector3 worldOrigin = transform.position + Vector3.up * 0.26f;
            for (int i = 0; i <= segments; i++)
            {
                float t = i / (float)segments;
                float angle = Mathf.Lerp(-viewAngle * 0.5f, viewAngle * 0.5f, t);
                Vector3 localDirection = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                Vector3 worldDirection = transform.TransformDirection(localDirection);
                float distance = DistanceToWall(worldOrigin, worldDirection, viewRange);
                Vector3 localPoint = localDirection * distance + Vector3.up * 0.015f;
                vertices[i + 1] = localPoint;
                _outlinePoints.Add(localPoint + Vector3.up * 0.01f);

                if (i < segments)
                {
                    int tri = i * 3;
                    triangles[tri] = 0;
                    triangles[tri + 1] = i + 1;
                    triangles[tri + 2] = i + 2;
                }
            }

            _outlinePoints.Add(Vector3.up * 0.02f);
            _fovMesh.Clear();
            _fovMesh.vertices = vertices;
            _fovMesh.triangles = triangles;
            _fovMesh.RecalculateBounds();

            _fovOutline.positionCount = _outlinePoints.Count;
            _fovOutline.SetPositions(_outlinePoints.ToArray());
        }

        private static float DistanceToWall(Vector3 origin, Vector3 direction, float maxDistance)
        {
            float nearest = maxDistance;
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, maxDistance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider != null && collider.gameObject.name == "Wall")
                    nearest = Mathf.Min(nearest, hits[i].distance);
            }
            return nearest;
        }

        private void UpdateTrail()
        {
            Vector3 point = transform.position + Vector3.up * 0.10f;
            if (_trailPoints.Count == 0)
            {
                _trailPoints.Add(point);
            }
            else
            {
                Vector3 last = _trailPoints[_trailPoints.Count - 1];
                float gap = Vector3.Distance(last, point);
                if (gap > 2.8f)
                {
                    ResetTrail();
                    _trailPoints.Add(point);
                }
                else if (gap >= trailPointSpacing)
                {
                    _trailPoints.Add(point);
                }
            }

            TrimTrailByDistance();
            _trail.positionCount = _trailPoints.Count;
            if (_trailPoints.Count > 0)
                _trail.SetPositions(_trailPoints.ToArray());
        }

        private void TrimTrailByDistance()
        {
            float total = 0f;
            for (int i = _trailPoints.Count - 1; i > 0; i--)
            {
                total += Vector3.Distance(_trailPoints[i], _trailPoints[i - 1]);
                if (total <= trailMaxDistance)
                    continue;

                int removeCount = i;
                if (removeCount > 0)
                    _trailPoints.RemoveRange(0, removeCount);
                break;
            }
        }

        public void ResetTrail()
        {
            _trailPoints.Clear();
            if (_trail != null)
                _trail.positionCount = 0;
        }

        private void OnDestroy()
        {
            if (_fovMesh != null)
                Destroy(_fovMesh);
            if (_fovMaterial != null)
                Destroy(_fovMaterial);
            if (_lineMaterial != null)
                Destroy(_lineMaterial);
        }
    }
}
