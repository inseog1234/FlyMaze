using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FlyMaze
{
    public sealed class RandomMazeGenerator : MonoBehaviour
    {
        private sealed class Cell
        {
            public bool visited;
            public readonly bool[] walls = { true, true, true, true }; // N, E, S, W
            public int enteredDirection = -1;
        }

        private struct Candidate
        {
            public int x;
            public int y;
            public int dir;

            public Candidate(int x, int y, int dir)
            {
                this.x = x;
                this.y = y;
                this.dir = dir;
            }
        }

        private struct WallVisual
        {
            public Transform transform;
            public Vector3 targetScale;
            public float delay;

            public WallVisual(Transform transform, Vector3 targetScale, float delay)
            {
                this.transform = transform;
                this.targetScale = targetScale;
                this.delay = delay;
            }
        }

        private static readonly int[] Dx = { 0, 1, 0, -1 };
        private static readonly int[] Dy = { 1, 0, -1, 0 };
        private static readonly int[] Opposite = { 2, 3, 0, 1 };

        public event Action GenerationStarted;
        public event Action<MazeBuildInfo> MazeBuilt;

        public bool IsGenerating { get; private set; }
        public MazeBuildInfo LastBuildInfo { get; private set; }

        private GameObject _mazeRoot;
        private readonly List<WallVisual> _wallVisuals = new List<WallVisual>(512);
        private Material _floorMaterial;
        private Material _wallMaterial;
        private Material _startMaterial;
        private Material _goalMaterial;

        public void Generate(MazeSettings sourceSettings, bool animate = true)
        {
            if (sourceSettings == null)
                return;

            StopAllCoroutines();
            StartCoroutine(GenerateRoutine(sourceSettings.Clone(), animate));
        }

        public void ClearMaze()
        {
            StopAllCoroutines();
            IsGenerating = false;

            if (_mazeRoot != null)
            {
                _mazeRoot.SetActive(false);
                Destroy(_mazeRoot);
                _mazeRoot = null;
            }

            ReleaseMaterials();
            _wallVisuals.Clear();
        }

        private IEnumerator GenerateRoutine(MazeSettings settings, bool animate)
        {
            settings.Clamp();
            IsGenerating = true;
            GenerationStarted?.Invoke();

            if (_mazeRoot != null)
            {
                _mazeRoot.SetActive(false);
                Destroy(_mazeRoot);
            }

            ReleaseMaterials();
            _wallVisuals.Clear();

            int seed = settings.seed == 0 ? Environment.TickCount : settings.seed;
            settings.seed = seed;
            System.Random rng = new System.Random(seed);
            Cell[,] cells = CarveMaze(settings, rng);
            AddExtraLoops(cells, settings, rng);

            CreateMaterials();
            BuildVisuals(cells, settings);
            FrameCamera(settings);

            if (animate)
                yield return AnimateWalls();
            else
                SnapWallsToFinalScale();

            LastBuildInfo = new MazeBuildInfo(settings.width, settings.height, seed, _wallVisuals.Count);
            IsGenerating = false;
            MazeBuilt?.Invoke(LastBuildInfo);
        }

        private static Cell[,] CarveMaze(MazeSettings settings, System.Random rng)
        {
            int width = settings.width;
            int height = settings.height;
            Cell[,] cells = new Cell[width, height];

            for (int x = 0; x < width; x++)
            for (int y = 0; y < height; y++)
                cells[x, y] = new Cell();

            Stack<Vector2Int> stack = new Stack<Vector2Int>(width * height);
            Vector2Int current = Vector2Int.zero;
            cells[0, 0].visited = true;
            stack.Push(current);

            List<Candidate> candidates = new List<Candidate>(4);

            while (stack.Count > 0)
            {
                current = stack.Peek();
                candidates.Clear();

                for (int dir = 0; dir < 4; dir++)
                {
                    int nx = current.x + Dx[dir];
                    int ny = current.y + Dy[dir];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height || cells[nx, ny].visited)
                        continue;

                    candidates.Add(new Candidate(nx, ny, dir));
                }

                if (candidates.Count == 0)
                {
                    stack.Pop();
                    continue;
                }

                int chosenIndex = rng.Next(candidates.Count);
                int enteredDir = cells[current.x, current.y].enteredDirection;

                if (enteredDir >= 0 && rng.NextDouble() > settings.twistiness)
                {
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (candidates[i].dir == enteredDir)
                        {
                            chosenIndex = i;
                            break;
                        }
                    }
                }

                Candidate chosen = candidates[chosenIndex];
                cells[current.x, current.y].walls[chosen.dir] = false;
                cells[chosen.x, chosen.y].walls[Opposite[chosen.dir]] = false;
                cells[chosen.x, chosen.y].visited = true;
                cells[chosen.x, chosen.y].enteredDirection = chosen.dir;
                stack.Push(new Vector2Int(chosen.x, chosen.y));
            }

            return cells;
        }

        private static void AddExtraLoops(Cell[,] cells, MazeSettings settings, System.Random rng)
        {
            if (settings.extraLoopChance <= 0f)
                return;

            for (int x = 0; x < settings.width; x++)
            for (int y = 0; y < settings.height; y++)
            {
                if (x + 1 < settings.width && cells[x, y].walls[1] && rng.NextDouble() < settings.extraLoopChance)
                {
                    cells[x, y].walls[1] = false;
                    cells[x + 1, y].walls[3] = false;
                }

                if (y + 1 < settings.height && cells[x, y].walls[0] && rng.NextDouble() < settings.extraLoopChance)
                {
                    cells[x, y].walls[0] = false;
                    cells[x, y + 1].walls[2] = false;
                }
            }
        }

        private void BuildVisuals(Cell[,] cells, MazeSettings settings)
        {
            _mazeRoot = new GameObject("Generated Maze");
            _mazeRoot.transform.SetParent(transform, false);

            float boardWidth = settings.width * settings.cellSize;
            float boardHeight = settings.height * settings.cellSize;

            GameObject floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "Maze Floor";
            floor.transform.SetParent(_mazeRoot.transform, false);
            floor.transform.position = new Vector3(0f, -0.09f, 0f);
            floor.transform.localScale = new Vector3(boardWidth + 0.9f, 0.18f, boardHeight + 0.9f);
            floor.GetComponent<Renderer>().sharedMaterial = _floorMaterial;

            for (int x = 0; x < settings.width; x++)
            for (int y = 0; y < settings.height; y++)
            {
                Cell cell = cells[x, y];
                Vector3 center = CellToWorld(x, y, settings);

                if (cell.walls[0])
                    CreateWall(center + new Vector3(0f, 0f, settings.cellSize * 0.5f), true, settings, x, y);
                if (cell.walls[1])
                    CreateWall(center + new Vector3(settings.cellSize * 0.5f, 0f, 0f), false, settings, x, y);
                if (y == 0 && cell.walls[2])
                    CreateWall(center - new Vector3(0f, 0f, settings.cellSize * 0.5f), true, settings, x, y);
                if (x == 0 && cell.walls[3])
                    CreateWall(center - new Vector3(settings.cellSize * 0.5f, 0f, 0f), false, settings, x, y);
            }

            CreateMarker("START", CellToWorld(0, 0, settings), settings.cellSize, _startMaterial);
            CreateMarker("GOAL", CellToWorld(settings.width - 1, settings.height - 1, settings), settings.cellSize, _goalMaterial);
        }

        private void CreateWall(Vector3 position, bool horizontal, MazeSettings settings, int x, int y)
        {
            const float wallHeight = 1.45f;
            const float wallThickness = 0.14f;

            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.transform.SetParent(_mazeRoot.transform, false);
            wall.transform.position = position + Vector3.up * wallHeight * 0.5f;

            Vector3 targetScale = horizontal
                ? new Vector3(settings.cellSize + wallThickness, wallHeight, wallThickness)
                : new Vector3(wallThickness, wallHeight, settings.cellSize + wallThickness);

            wall.transform.localScale = new Vector3(targetScale.x, 0.01f, targetScale.z);
            wall.GetComponent<Renderer>().sharedMaterial = _wallMaterial;

            float normalized = (x + y) / Mathf.Max(1f, settings.width + settings.height - 2f);
            float delay = normalized * 0.24f + UnityEngine.Random.Range(0f, 0.025f);
            _wallVisuals.Add(new WallVisual(wall.transform, targetScale, delay));
        }

        private void CreateMarker(string markerName, Vector3 position, float cellSize, Material material)
        {
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            marker.name = markerName;
            marker.transform.SetParent(_mazeRoot.transform, false);
            marker.transform.position = position + Vector3.up * 0.035f;
            marker.transform.localScale = new Vector3(cellSize * 0.24f, 0.035f, cellSize * 0.24f);
            marker.GetComponent<Renderer>().sharedMaterial = material;

            Collider collider = marker.GetComponent<Collider>();
            if (collider != null)
                collider.enabled = false;
        }

        private IEnumerator AnimateWalls()
        {
            const float duration = 0.38f;
            const float totalTail = 0.28f;
            float elapsed = 0f;

            while (elapsed < duration + totalTail)
            {
                elapsed += Time.unscaledDeltaTime;

                for (int i = 0; i < _wallVisuals.Count; i++)
                {
                    WallVisual item = _wallVisuals[i];
                    if (item.transform == null)
                        continue;

                    float t = Mathf.Clamp01((elapsed - item.delay) / duration);
                    float eased = EaseOutBack(t);
                    Vector3 scale = item.targetScale;
                    scale.y = Mathf.Max(0.01f, item.targetScale.y * eased);
                    item.transform.localScale = scale;
                }

                yield return null;
            }

            SnapWallsToFinalScale();
        }

        private void SnapWallsToFinalScale()
        {
            for (int i = 0; i < _wallVisuals.Count; i++)
            {
                if (_wallVisuals[i].transform != null)
                    _wallVisuals[i].transform.localScale = _wallVisuals[i].targetScale;
            }
        }

        private static float EaseOutBack(float t)
        {
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float p = t - 1f;
            return 1f + c3 * p * p * p + c1 * p * p;
        }

        private static Vector3 CellToWorld(int x, int y, MazeSettings settings)
        {
            float worldX = (x - (settings.width - 1) * 0.5f) * settings.cellSize;
            float worldZ = (y - (settings.height - 1) * 0.5f) * settings.cellSize;
            return new Vector3(worldX, 0f, worldZ);
        }

        private void FrameCamera(MazeSettings settings)
        {
            Camera camera = Camera.main;
            if (camera == null)
                return;

            float boardWidth = settings.width * settings.cellSize;
            float boardHeight = settings.height * settings.cellSize;
            float span = Mathf.Max(boardWidth, boardHeight);
            Vector3 target = new Vector3(boardWidth * 0.08f, 0f, 0f);

            camera.orthographic = true;
            camera.transform.position = target + new Vector3(boardWidth * 0.40f, span * 0.92f, -boardHeight * 0.72f - 2f);
            camera.transform.LookAt(target);
            camera.orthographicSize = Mathf.Max(boardHeight * 0.68f, boardWidth / Mathf.Max(0.45f, camera.aspect) * 0.60f) + 1.4f;
        }

        private void CreateMaterials()
        {
            _floorMaterial = MakeMaterial("Maze Floor Material", new Color(0.028f, 0.036f, 0.055f), Color.black);
            _wallMaterial = MakeMaterial("Maze Wall Material", new Color(0.10f, 0.16f, 0.22f), new Color(0.02f, 0.14f, 0.19f));
            _startMaterial = MakeMaterial("Start Material", new Color(0.12f, 0.92f, 0.76f), new Color(0.06f, 0.75f, 0.58f));
            _goalMaterial = MakeMaterial("Goal Material", new Color(1.00f, 0.42f, 0.62f), new Color(0.85f, 0.12f, 0.36f));
        }

        private static Material MakeMaterial(string materialName, Color baseColor, Color emission)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("HDRP/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            Material material = new Material(shader) { name = materialName };

            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            if (material.HasProperty("_Color")) material.SetColor("_Color", baseColor);

            if (emission.maxColorComponent > 0f)
            {
                if (material.HasProperty("_EmissionColor"))
                {
                    material.SetColor("_EmissionColor", emission * 1.7f);
                    material.EnableKeyword("_EMISSION");
                }
                if (material.HasProperty("_EmissiveColor"))
                    material.SetColor("_EmissiveColor", emission * 1.7f);
            }

            return material;
        }

        private void ReleaseMaterials()
        {
            DestroyMaterial(_floorMaterial);
            DestroyMaterial(_wallMaterial);
            DestroyMaterial(_startMaterial);
            DestroyMaterial(_goalMaterial);

            _floorMaterial = null;
            _wallMaterial = null;
            _startMaterial = null;
            _goalMaterial = null;
        }

        private static void DestroyMaterial(Material material)
        {
            if (material != null)
                Destroy(material);
        }

        private void OnDestroy()
        {
            ReleaseMaterials();
        }
    }
}
