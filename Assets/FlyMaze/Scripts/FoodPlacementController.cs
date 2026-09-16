using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

namespace FlyMaze
{
    public enum FoodPlacementMode
    {
        Auto,
        Manual
    }

    [DisallowMultipleComponent]
    public sealed class FoodPlacementController : MonoBehaviour
    {
        [SerializeField, Range(1, 30)] private int targetFoodCount = 6;

        public event Action StateChanged;
        public event Action<FoodPlacementMode> ModeChanged;

        public FoodPlacementMode Mode { get; private set; } = FoodPlacementMode.Auto;
        public bool ManualPlacementActive => Mode == FoodPlacementMode.Manual && _mazeReady;
        public int TargetFoodCount => targetFoodCount;
        public int PlacedFoodCount => _foods.Count;
        public int RemainingFoodCount => _foods.Count;
        public bool AllFoodCollected => _mazeReady && _runFoodTotal > 0 && _foods.Count == 0;

        private readonly List<FoodPickup> _foods = new List<FoodPickup>(32);
        private readonly Dictionary<Vector2Int, FoodPickup> _byCell = new Dictionary<Vector2Int, FoodPickup>();
        private readonly Plane _groundPlane = new Plane(Vector3.up, Vector3.zero);

        private RandomMazeGenerator _generator;
        private Camera _camera;
        private Transform _mazeRoot;
        private Transform _foodRoot;
        private bool _mazeReady;
        private int _width;
        private int _height;
        private int _seed;
        private int _runFoodTotal;
        private float _cellSize = 1.8f;

        private Material _fruitMaterial;
        private Material _leafMaterial;
        private Material _glowMaterial;
        private Material _beamInnerMaterial;
        private Material _beamOuterMaterial;

        public void Bind(RandomMazeGenerator generator, Camera targetCamera)
        {
            if (_generator != null)
            {
                _generator.GenerationStarted -= HandleGenerationStarted;
                _generator.MazeBuilt -= HandleMazeBuilt;
            }

            _generator = generator;
            _camera = targetCamera;

            if (_generator != null)
            {
                _generator.GenerationStarted += HandleGenerationStarted;
                _generator.MazeBuilt += HandleMazeBuilt;
            }
        }

        private void Update()
        {
            if (!ManualPlacementActive || _camera == null)
                return;

            Mouse mouse = Mouse.current;
            if (mouse == null || !mouse.leftButton.wasPressedThisFrame)
                return;

            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
                return;

            Vector2 screen = mouse.position.ReadValue();
            Ray ray = _camera.ScreenPointToRay(screen);
            if (!_groundPlane.Raycast(ray, out float distance))
                return;

            Vector3 worldPoint = ray.GetPoint(distance);
            if (!TryWorldToCell(worldPoint, out Vector2Int cell))
                return;

            if (IsReservedCell(cell))
                return;

            if (_byCell.ContainsKey(cell))
                RemoveFoodAt(cell);
            else if (_foods.Count < targetFoodCount)
                PlaceFood(cell);
        }

        public void SetTargetFoodCount(int count)
        {
            int max = Mathf.Max(1, Mathf.Min(30, Mathf.Max(1, _width * _height - 2)));
            targetFoodCount = Mathf.Clamp(count, 1, max);

            if (!_mazeReady)
            {
                StateChanged?.Invoke();
                return;
            }

            if (Mode == FoodPlacementMode.Auto)
            {
                AutoPlaceFoods();
                return;
            }

            while (_foods.Count > targetFoodCount)
            {
                FoodPickup last = _foods[_foods.Count - 1];
                RemoveFoodAt(last.Cell);
            }

            _runFoodTotal = _foods.Count;
            StateChanged?.Invoke();
        }

        public void SetMode(FoodPlacementMode mode)
        {
            if (Mode == mode)
                return;

            Mode = mode;

            if (_mazeReady && Mode == FoodPlacementMode.Auto)
                AutoPlaceFoods();

            ModeChanged?.Invoke(Mode);
            StateChanged?.Invoke();
        }

        public void AutoPlaceFoods()
        {
            if (!_mazeReady)
                return;

            ClearFoodObjects();
            EnsureFoodRoot();

            List<Vector2Int> candidates = new List<Vector2Int>(_width * _height);
            for (int x = 0; x < _width; x++)
            for (int y = 0; y < _height; y++)
            {
                Vector2Int cell = new Vector2Int(x, y);
                if (!IsReservedCell(cell))
                    candidates.Add(cell);
            }

            System.Random rng = new System.Random(unchecked((_seed * 397) ^ (targetFoodCount * 7919) ^ 0x4F4F44));
            for (int i = candidates.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
            }

            int count = Mathf.Min(targetFoodCount, candidates.Count);
            for (int i = 0; i < count; i++)
                PlaceFood(candidates[i], false);

            _runFoodTotal = _foods.Count;
            StateChanged?.Invoke();
        }

        public bool TryGetFoodAt(Vector2Int cell, out FoodPickup food)
        {
            return _byCell.TryGetValue(cell, out food);
        }

        internal void NotifyCollected(FoodPickup food)
        {
            if (food == null)
                return;

            _byCell.Remove(food.Cell);
            _foods.Remove(food);
            StateChanged?.Invoke();
        }

        private void HandleGenerationStarted()
        {
            _mazeReady = false;
            _mazeRoot = null;
            _foodRoot = null;
            _foods.Clear();
            _byCell.Clear();
            _runFoodTotal = 0;
            StateChanged?.Invoke();
        }

        private void HandleMazeBuilt(MazeBuildInfo info)
        {
            _width = info.Width;
            _height = info.Height;
            _seed = info.Seed;
            _mazeRoot = _generator != null ? _generator.transform.Find("Generated Maze") : null;
            _foodRoot = null;

            if (_mazeRoot == null)
            {
                _mazeReady = false;
                StateChanged?.Invoke();
                return;
            }

            Transform floor = _mazeRoot.Find("Maze Floor");
            if (floor != null && _width > 0)
                _cellSize = Mathf.Max(0.2f, (floor.localScale.x - 0.9f) / _width);

            _mazeReady = true;
            EnsureMaterials();
            EnsureFoodRoot();

            if (Mode == FoodPlacementMode.Auto)
                AutoPlaceFoods();
            else
                StateChanged?.Invoke();
        }

        private void PlaceFood(Vector2Int cell, bool notify = true)
        {
            if (!_mazeReady || _byCell.ContainsKey(cell) || IsReservedCell(cell))
                return;

            EnsureFoodRoot();
            EnsureMaterials();

            GameObject root = new GameObject($"Food {cell.x:D2}-{cell.y:D2}");
            root.transform.SetParent(_foodRoot, false);
            root.transform.localPosition = CellToLocal(cell);

            SphereCollider trigger = root.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = 0.48f;
            trigger.center = new Vector3(0f, 0.42f, 0f);

            FoodPickup pickup = root.AddComponent<FoodPickup>();

            GameObject visual = new GameObject("Fruit Visual");
            visual.transform.SetParent(root.transform, false);

            CreatePrimitive(PrimitiveType.Sphere, "Fruit L", visual.transform,
                new Vector3(-0.12f, 0.40f, 0.02f), new Vector3(0.34f, 0.34f, 0.34f), _fruitMaterial);
            CreatePrimitive(PrimitiveType.Sphere, "Fruit R", visual.transform,
                new Vector3(0.12f, 0.40f, -0.02f), new Vector3(0.34f, 0.34f, 0.34f), _fruitMaterial);
            CreatePrimitive(PrimitiveType.Sphere, "Fruit Top", visual.transform,
                new Vector3(0f, 0.55f, 0.06f), new Vector3(0.30f, 0.28f, 0.30f), _fruitMaterial);

            GameObject stem = CreatePrimitive(PrimitiveType.Cylinder, "Stem", visual.transform,
                new Vector3(0f, 0.70f, 0f), new Vector3(0.055f, 0.11f, 0.055f), _leafMaterial);
            stem.transform.localRotation = Quaternion.Euler(0f, 0f, -12f);

            GameObject leaf = CreatePrimitive(PrimitiveType.Sphere, "Leaf", visual.transform,
                new Vector3(0.13f, 0.69f, 0f), new Vector3(0.18f, 0.055f, 0.10f), _leafMaterial);
            leaf.transform.localRotation = Quaternion.Euler(0f, 24f, -22f);

            CreatePrimitive(PrimitiveType.Cylinder, "Ground Glow", root.transform,
                new Vector3(0f, 0.025f, 0f), new Vector3(0.48f, 0.018f, 0.48f), _glowMaterial);

            Transform outerBeam = CreatePrimitive(PrimitiveType.Cylinder, "Outer Light Pillar", root.transform,
                new Vector3(0f, 2.65f, 0f), new Vector3(0.25f, 2.65f, 0.25f), _beamOuterMaterial).transform;
            Transform innerBeam = CreatePrimitive(PrimitiveType.Cylinder, "Inner Light Pillar", root.transform,
                new Vector3(0f, 2.45f, 0f), new Vector3(0.095f, 2.45f, 0.095f), _beamInnerMaterial).transform;

            GameObject lightObject = new GameObject("Food Glow Light", typeof(Light));
            lightObject.transform.SetParent(root.transform, false);
            lightObject.transform.localPosition = new Vector3(0f, 0.78f, 0f);
            Light pointLight = lightObject.GetComponent<Light>();
            pointLight.type = LightType.Point;
            pointLight.color = new Color(1f, 0.55f, 0.16f, 1f);
            pointLight.range = Mathf.Max(2.8f, _cellSize * 2.2f);
            pointLight.intensity = 3.4f;
            pointLight.shadows = LightShadows.None;

            pickup.Initialize(this, cell, visual.transform, innerBeam, outerBeam, pointLight,
                (cell.x * 0.71f + cell.y * 1.13f) % 6.28f);

            _foods.Add(pickup);
            _byCell[cell] = pickup;
            _runFoodTotal = Mathf.Max(_runFoodTotal, _foods.Count);

            if (notify)
                StateChanged?.Invoke();
        }

        private void RemoveFoodAt(Vector2Int cell)
        {
            if (!_byCell.TryGetValue(cell, out FoodPickup food))
                return;

            _byCell.Remove(cell);
            _foods.Remove(food);
            if (food != null)
                Destroy(food.gameObject);

            _runFoodTotal = _foods.Count;
            StateChanged?.Invoke();
        }

        private void ClearFoodObjects()
        {
            if (_foodRoot != null)
            {
                _foodRoot.gameObject.SetActive(false);
                Destroy(_foodRoot.gameObject);
                _foodRoot = null;
            }

            _foods.Clear();
            _byCell.Clear();
            _runFoodTotal = 0;
        }

        private void EnsureFoodRoot()
        {
            if (_foodRoot != null || _mazeRoot == null)
                return;

            GameObject go = new GameObject("Food Items");
            _foodRoot = go.transform;
            _foodRoot.SetParent(_mazeRoot, false);
        }

        private Vector3 CellToLocal(Vector2Int cell)
        {
            float x = (cell.x - (_width - 1) * 0.5f) * _cellSize;
            float z = (cell.y - (_height - 1) * 0.5f) * _cellSize;
            return new Vector3(x, 0f, z);
        }

        private bool TryWorldToCell(Vector3 world, out Vector2Int cell)
        {
            if (_generator == null || !_mazeReady)
            {
                cell = default;
                return false;
            }

            Vector3 local = _generator.transform.InverseTransformPoint(world);
            int x = Mathf.RoundToInt(local.x / _cellSize + (_width - 1) * 0.5f);
            int y = Mathf.RoundToInt(local.z / _cellSize + (_height - 1) * 0.5f);

            if (x < 0 || y < 0 || x >= _width || y >= _height)
            {
                cell = default;
                return false;
            }

            Vector3 center = CellToLocal(new Vector2Int(x, y));
            float half = _cellSize * 0.47f;
            if (Mathf.Abs(local.x - center.x) > half || Mathf.Abs(local.z - center.z) > half)
            {
                cell = default;
                return false;
            }

            cell = new Vector2Int(x, y);
            return true;
        }

        private bool IsReservedCell(Vector2Int cell)
        {
            return cell == Vector2Int.zero || cell == new Vector2Int(_width - 1, _height - 1);
        }

        private GameObject CreatePrimitive(
            PrimitiveType type,
            string objectName,
            Transform parent,
            Vector3 localPosition,
            Vector3 localScale,
            Material material)
        {
            GameObject go = GameObject.CreatePrimitive(type);
            go.name = objectName;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            go.transform.localScale = localScale;

            Collider collider = go.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return go;
        }

        private void EnsureMaterials()
        {
            if (_fruitMaterial != null)
                return;

            _fruitMaterial = MakeLitMaterial("Food Fruit Material",
                new Color(1.00f, 0.27f, 0.08f, 1f), new Color(1.0f, 0.12f, 0.025f, 1f) * 3.2f);
            _leafMaterial = MakeLitMaterial("Food Leaf Material",
                new Color(0.20f, 0.90f, 0.43f, 1f), new Color(0.03f, 0.42f, 0.11f, 1f) * 1.8f);
            _glowMaterial = MakeLitMaterial("Food Ground Glow",
                new Color(1f, 0.48f, 0.08f, 1f), new Color(1f, 0.24f, 0.03f, 1f) * 4f);
            _beamInnerMaterial = MakeTransparentMaterial("Food Beam Inner", new Color(1f, 0.70f, 0.24f, 0.31f));
            _beamOuterMaterial = MakeTransparentMaterial("Food Beam Outer", new Color(1f, 0.35f, 0.08f, 0.09f));
        }

        private static Material MakeLitMaterial(string name, Color baseColor, Color emission)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            Material material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);
            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", emission);
                material.EnableKeyword("_EMISSION");
            }
            return material;
        }

        private static Material MakeTransparentMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");

            Material material = new Material(shader) { name = name, renderQueue = 3000 };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
            if (material.HasProperty("_SrcBlend")) material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (material.HasProperty("_DstBlend")) material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            return material;
        }

        private void ReleaseMaterials()
        {
            DestroyMaterial(_fruitMaterial);
            DestroyMaterial(_leafMaterial);
            DestroyMaterial(_glowMaterial);
            DestroyMaterial(_beamInnerMaterial);
            DestroyMaterial(_beamOuterMaterial);
            _fruitMaterial = null;
            _leafMaterial = null;
            _glowMaterial = null;
            _beamInnerMaterial = null;
            _beamOuterMaterial = null;
        }

        private static void DestroyMaterial(Material material)
        {
            if (material != null)
                Destroy(material);
        }

        private void OnDestroy()
        {
            if (_generator != null)
            {
                _generator.GenerationStarted -= HandleGenerationStarted;
                _generator.MazeBuilt -= HandleMazeBuilt;
            }

            ReleaseMaterials();
        }
    }
}
