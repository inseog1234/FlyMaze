using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Camera))]
    public sealed class MazeCameraController : MonoBehaviour
    {
        [Header("Zoom")]
        [SerializeField] private float zoomSpeed = 0.0032f;
        [SerializeField] private float minOrthographicSize = 2.5f;
        [SerializeField] private float maxOrthographicSize = 90f;

        [Header("UI Safe Area")]
        [SerializeField, Range(0f, 0.45f)] private float reservedLeftViewport = 0.27f;
        [SerializeField, Range(0f, 0.20f)] private float reservedRightViewport = 0.03f;
        [SerializeField, Range(0f, 0.20f)] private float reservedTopViewport = 0.10f;
        [SerializeField, Range(0f, 0.20f)] private float reservedBottomViewport = 0.05f;

        private readonly Plane _groundPlane = new Plane(Vector3.up, Vector3.zero);

        private Camera _camera;
        private RandomMazeGenerator _generator;
        private FoodPlacementController _foodPlacement;
        private bool _framePending;

        private Vector3 _homePosition;
        private Quaternion _homeRotation;
        private float _homeOrthographicSize;
        private bool _hasHome;

        private int _dragButton = -1;
        private Vector3 _dragAnchor;

        private void Awake()
        {
            _camera = GetComponent<Camera>();
        }

        public void Bind(RandomMazeGenerator generator, FoodPlacementController foodPlacement = null)
        {
            if (_generator != generator)
            {
                if (_generator != null)
                {
                    _generator.GenerationStarted -= HandleGenerationStarted;
                    _generator.MazeBuilt -= HandleMazeBuilt;
                }

                _generator = generator;

                if (_generator != null)
                {
                    _generator.GenerationStarted += HandleGenerationStarted;
                    _generator.MazeBuilt += HandleMazeBuilt;
                    _framePending = true;
                }
            }

            _foodPlacement = foodPlacement;
        }

        private void HandleGenerationStarted()
        {
            _framePending = true;
            _dragButton = -1;
        }

        private void HandleMazeBuilt(MazeBuildInfo _)
        {
            _framePending = true;
        }

        private void LateUpdate()
        {
            if (_framePending && TryFrameGeneratedMaze())
                _framePending = false;
        }

        private void Update()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || _camera == null || !_camera.orthographic)
                return;

            bool pointerOverUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            bool manualPlacement = _foodPlacement != null && _foodPlacement.ManualPlacementActive;
            Vector2 screenPosition = mouse.position.ReadValue();

            float scroll = mouse.scroll.ReadValue().y;
            if (!pointerOverUi && Mathf.Abs(scroll) > 0.01f)
                ZoomAt(screenPosition, scroll);

            if (_dragButton < 0)
            {
                if (mouse.middleButton.wasPressedThisFrame)
                    BeginDrag(1, screenPosition);
                else if (mouse.rightButton.wasPressedThisFrame)
                    BeginDrag(2, screenPosition);
                else if (!pointerOverUi && !manualPlacement && mouse.leftButton.wasPressedThisFrame)
                    BeginDrag(0, screenPosition);
            }

            if (_dragButton >= 0)
            {
                bool held = _dragButton switch
                {
                    0 => mouse.leftButton.isPressed,
                    1 => mouse.middleButton.isPressed,
                    2 => mouse.rightButton.isPressed,
                    _ => false
                };

                if (!held)
                {
                    _dragButton = -1;
                }
                else if (TryGetGroundPoint(screenPosition, out Vector3 currentPoint))
                {
                    transform.position += _dragAnchor - currentPoint;
                }
            }

            if (Keyboard.current != null && Keyboard.current.fKey.wasPressedThisFrame)
                ResetView();
        }

        private void BeginDrag(int button, Vector2 screenPosition)
        {
            if (!TryGetGroundPoint(screenPosition, out _dragAnchor))
                return;

            _dragButton = button;
        }

        private void ZoomAt(Vector2 screenPosition, float scrollDelta)
        {
            bool hasBefore = TryGetGroundPoint(screenPosition, out Vector3 before);

            float factor = Mathf.Exp(-scrollDelta * zoomSpeed);
            _camera.orthographicSize = Mathf.Clamp(
                _camera.orthographicSize * factor,
                minOrthographicSize,
                maxOrthographicSize);

            if (hasBefore && TryGetGroundPoint(screenPosition, out Vector3 after))
                transform.position += before - after;
        }

        private bool TryGetGroundPoint(Vector2 screenPosition, out Vector3 point)
        {
            Ray ray = _camera.ScreenPointToRay(screenPosition);
            if (_groundPlane.Raycast(ray, out float distance))
            {
                point = ray.GetPoint(distance);
                return true;
            }

            point = default;
            return false;
        }

        private bool TryFrameGeneratedMaze()
        {
            if (_generator == null || _camera == null)
                return false;

            Transform mazeRoot = _generator.transform.Find("Generated Maze");
            if (mazeRoot == null)
                return false;

            Renderer[] renderers = mazeRoot.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
                return false;

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);

            FrameBounds(bounds);
            CaptureHomeView();
            return true;
        }

        private void FrameBounds(Bounds bounds)
        {
            _camera.orthographic = true;

            Vector3 center = new Vector3(bounds.center.x, 0f, bounds.center.z);
            Vector3 right = transform.right;
            Vector3 up = transform.up;
            Vector3 forward = transform.forward;

            float halfWidthInView = 0f;
            float halfHeightInView = 0f;

            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
            {
                Vector3 corner = new Vector3(
                    x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z);

                Vector3 offset = corner - center;
                halfWidthInView = Mathf.Max(halfWidthInView, Mathf.Abs(Vector3.Dot(offset, right)));
                halfHeightInView = Mathf.Max(halfHeightInView, Mathf.Abs(Vector3.Dot(offset, up)));
            }

            float safeWidth = Mathf.Max(0.30f, 1f - reservedLeftViewport - reservedRightViewport);
            float safeHeight = Mathf.Max(0.40f, 1f - reservedTopViewport - reservedBottomViewport);
            float aspect = Mathf.Max(0.45f, _camera.aspect);

            float verticalFit = halfHeightInView / safeHeight;
            float horizontalFit = halfWidthInView / (aspect * safeWidth);
            float size = Mathf.Clamp(Mathf.Max(verticalFit, horizontalFit) * 1.08f, minOrthographicSize, maxOrthographicSize);
            _camera.orthographicSize = size;

            float safeCenterX = reservedLeftViewport + safeWidth * 0.5f;
            float safeCenterY = reservedBottomViewport + safeHeight * 0.5f;
            float horizontalOffset = (safeCenterX - 0.5f) * (2f * size * aspect);
            float verticalOffset = (safeCenterY - 0.5f) * (2f * size);

            float distance = Mathf.Max(20f, bounds.extents.magnitude * 2.8f);
            transform.position = center
                                 - forward * distance
                                 - right * horizontalOffset
                                 - up * verticalOffset;
        }

        public void CaptureHomeView()
        {
            if (_camera == null)
                _camera = GetComponent<Camera>();

            _homePosition = transform.position;
            _homeRotation = transform.rotation;
            _homeOrthographicSize = _camera.orthographicSize;
            _hasHome = true;
        }

        public void ResetView()
        {
            if (!_hasHome || _camera == null)
                return;

            transform.position = _homePosition;
            transform.rotation = _homeRotation;
            _camera.orthographicSize = _homeOrthographicSize;
            _dragButton = -1;
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
}
