using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public sealed class MaleCNSFlyAgent : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float moveSpeed = 1.9f;
        [SerializeField] private float turnSpeed = 250f;
        [SerializeField, Range(0f, 0.8f)] private float locomotorFloor = 0.20f;

        [Header("Vision")]
        [SerializeField] private float obstacleRange = 2.8f;
        [SerializeField] private float sensorHeight = 0.26f;

        [Header("Olfaction")]
        [SerializeField] private float antennaSeparation = 0.52f;
        [SerializeField] private float antennaForward = 0.30f;
        [SerializeField, Range(0.1f, 0.9f)] private float corridorDiffusionFalloff = 0.52f;
        [SerializeField, Range(0.5f, 2.0f)] private float localGradientGain = 1.25f;

        [Header("Reinforcement")]
        [SerializeField] private float stuckDetectionSeconds = 0.55f;
        [SerializeField] private float punishmentCooldown = 1.25f;
        [SerializeField] private float foodReward = 1.0f;
        [SerializeField] private float goalReward = 1.5f;

        public bool Finished { get; private set; }
        public bool RecoveryActive => false;
        public Vector3 CurrentTarget { get; private set; }
        public int CurrentTargetKind { get; private set; } = -1;
        public float VisionFront { get; private set; }
        public float VisionLeft { get; private set; }
        public float VisionRight { get; private set; }
        public float FoodSmellLeft { get; private set; }
        public float FoodSmellRight { get; private set; }

        private RandomMazeGenerator _generator;
        private FoodPlacementController _foodPlacement;
        private MaleCNSBridge _brain;
        private Rigidbody _body;
        private Transform _goal;
        private Transform _leftWing;
        private Transform _rightWing;
        private float _wingPhase;
        private bool _initialized;
        private float _warmupUntil;
        private float _stuckTimer;
        private float _nextPunishmentTime;
        private Vector3 _lastFixedPosition;

        private Transform _mazeRoot;
        private int _gridWidth;
        private int _gridHeight;
        private float _gridCellSize = 2.6f;
        private Vector3 _gridOriginLocal;
        private byte[] _neighborMask;
        private float[] _cueField;
        private int _cueSignature = int.MinValue;
        private int _cueKind = -1;

        private Material _bodyMaterial;
        private Material _eyeMaterial;
        private Material _wingMaterial;

        private static readonly int[] GridDx = { 0, 1, 0, -1 };
        private static readonly int[] GridDy = { 1, 0, -1, 0 };
        private static readonly byte[] GridBit = { 1, 2, 4, 8 };
        private static readonly int[] GridOpposite = { 2, 3, 0, 1 };

        public void Initialize(RandomMazeGenerator generator, FoodPlacementController foodPlacement, MaleCNSBridge brain)
        {
            if (_initialized)
                return;

            _initialized = true;
            _generator = generator;
            _foodPlacement = foodPlacement;
            _brain = brain;

            _body = GetComponent<Rigidbody>();
            _body.useGravity = false;
            _body.mass = 0.08f;
            _body.linearDamping = 4.5f;
            _body.angularDamping = 10f;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.constraints = RigidbodyConstraints.FreezePositionY |
                                RigidbodyConstraints.FreezeRotationX |
                                RigidbodyConstraints.FreezeRotationZ;

            SphereCollider collider = GetComponent<SphereCollider>();
            collider.radius = 0.19f;
            collider.center = new Vector3(0f, 0.23f, 0f);

            BuildVisual();

            FlyPerceptionVisualizer visualizer = GetComponent<FlyPerceptionVisualizer>();
            if (visualizer == null)
                visualizer = gameObject.AddComponent<FlyPerceptionVisualizer>();
            visualizer.Bind(this);

            if (_generator != null)
            {
                _generator.GenerationStarted += HandleGenerationStarted;
                _generator.MazeBuilt += HandleMazeBuilt;
                if (_generator.LastBuildInfo.CellCount > 0)
                    HandleMazeBuilt(_generator.LastBuildInfo);
            }
        }

        private void HandleGenerationStarted()
        {
            Finished = false;
            _goal = null;
            CurrentTargetKind = -1;
            CurrentTarget = transform.position;
            _stuckTimer = 0f;
            _nextPunishmentTime = 0f;
            InvalidateCueField();

            if (_body != null)
                _body.linearVelocity = Vector3.zero;

            _brain?.ResetNetwork();
        }

        private void HandleMazeBuilt(MazeBuildInfo info)
        {
            _mazeRoot = _generator != null ? _generator.transform.Find("Generated Maze") : null;
            if (_mazeRoot == null)
                return;

            Transform start = _mazeRoot.Find("START");
            _goal = _mazeRoot.Find("GOAL");
            BuildMazeGraph(info);

            if (start != null)
            {
                Vector3 spawn = start.position + Vector3.up * 0.32f;
                Physics.SyncTransforms();
                Quaternion heading = FindBestSpawnHeading(spawn);
                transform.SetPositionAndRotation(spawn, heading);

                if (_body != null)
                {
                    _body.position = spawn;
                    _body.rotation = heading;
                    _body.linearVelocity = Vector3.zero;
                    _body.angularVelocity = Vector3.zero;
                }

                _lastFixedPosition = spawn;
            }

            _stuckTimer = 0f;
            _nextPunishmentTime = 0f;
            _warmupUntil = Time.fixedTime + 0.55f;
            Finished = false;
            _brain?.ResetNetwork();
        }

        private Quaternion FindBestSpawnHeading(Vector3 spawn)
        {
            Vector3[] directions = { Vector3.forward, Vector3.right, Vector3.back, Vector3.left };
            Vector3 origin = spawn + Vector3.up * sensorHeight;
            float bestDistance = -1f;
            Vector3 best = Vector3.forward;

            for (int i = 0; i < directions.Length; i++)
            {
                float distance = DistanceToWall(origin, directions[i], obstacleRange * 2.2f);
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    best = directions[i];
                }
            }

            return Quaternion.LookRotation(best, Vector3.up);
        }

        private void Update()
        {
            AnimateWings();
        }

        private void FixedUpdate()
        {
            if (!_initialized || _body == null || _brain == null || Finished)
                return;

            Vector3 delta = transform.position - _lastFixedPosition;
            delta.y = 0f;
            float movedThisStep = delta.magnitude;
            _lastFixedPosition = transform.position;

            Vector3 origin = transform.position + Vector3.up * sensorHeight;
            VisionFront = SenseDirection(origin, transform.forward);
            VisionLeft = Mathf.Max(
                SenseDirection(origin, Quaternion.Euler(0f, -28f, 0f) * transform.forward),
                SenseDirection(origin, Quaternion.Euler(0f, -58f, 0f) * transform.forward));
            VisionRight = Mathf.Max(
                SenseDirection(origin, Quaternion.Euler(0f, 28f, 0f) * transform.forward),
                SenseDirection(origin, Quaternion.Euler(0f, 58f, 0f) * transform.forward));

            Vector3 leftAntenna = transform.position +
                                  transform.forward * antennaForward -
                                  transform.right * (antennaSeparation * 0.5f);
            Vector3 rightAntenna = transform.position +
                                   transform.forward * antennaForward +
                                   transform.right * (antennaSeparation * 0.5f);
            leftAntenna.y += sensorHeight;
            rightAntenna.y += sensorHeight;

            FoodPickup[] foods = FindObjectsByType<FoodPickup>(FindObjectsSortMode.None);
            bool hasFood = TryPrepareFoodCueField(foods, out Vector3 displayTarget);

            int targetKind;
            float leftSignal;
            float rightSignal;

            if (hasFood)
            {
                targetKind = 0;
                CurrentTarget = displayTarget;
                leftSignal = SampleDiffusedCue(leftAntenna);
                rightSignal = SampleDiffusedCue(rightAntenna);
            }
            else if (_goal != null)
            {
                targetKind = 1;
                CurrentTarget = _goal.position;
                EnsureGoalCueField();
                leftSignal = SampleDiffusedCue(leftAntenna);
                rightSignal = SampleDiffusedCue(rightAntenna);
            }
            else
            {
                targetKind = -1;
                leftSignal = 0f;
                rightSignal = 0f;
                CurrentTarget = transform.position;
            }

            CurrentTargetKind = targetKind;
            FoodSmellLeft = Mathf.Clamp01(leftSignal);
            FoodSmellRight = Mathf.Clamp01(rightSignal);

            float totalSignal = Mathf.Clamp01((leftSignal + rightSignal) * 0.72f);
            float sum = Mathf.Abs(leftSignal) + Mathf.Abs(rightSignal);
            float bearing = sum > 0.0001f
                ? Mathf.Clamp(((rightSignal - leftSignal) / (sum + 0.0001f)) * 1.8f, -1f, 1f)
                : 0f;

            _brain.SubmitSensory(new MaleCNSSensoryFrame(
                VisionFront, VisionLeft, VisionRight, bearing, totalSignal, targetKind));

            if (!_brain.IsReady || Time.fixedTime < _warmupUntil)
            {
                _body.linearVelocity = Vector3.zero;
                return;
            }

            bool pressingWall = VisionFront > 0.68f && movedThisStep < 0.006f;
            if (pressingWall)
                _stuckTimer += Time.fixedDeltaTime;
            else
                _stuckTimer = Mathf.Max(0f, _stuckTimer - Time.fixedDeltaTime * 2.5f);

            if (_stuckTimer >= stuckDetectionSeconds && Time.fixedTime >= _nextPunishmentTime)
            {
                _brain.GivePunishment(1f);
                _nextPunishmentTime = Time.fixedTime + punishmentCooldown;
                _stuckTimer = 0f;
            }

            float turn = _brain.TurnOutput;
            float speedDrive = Mathf.Lerp(locomotorFloor, 1f, _brain.ForwardOutput);
            Quaternion rotation =
                Quaternion.Euler(0f, turn * turnSpeed * Time.fixedDeltaTime, 0f) * _body.rotation;
            _body.MoveRotation(rotation);

            Vector3 velocity = (rotation * Vector3.forward) * (moveSpeed * speedDrive);
            velocity.y = 0f;
            _body.linearVelocity = velocity;

            if (targetKind == 1 && _goal != null &&
                Vector3.Distance(transform.position, _goal.position) < 0.58f)
            {
                Finished = true;
                _body.linearVelocity = Vector3.zero;
                _brain.GiveReward(goalReward);
                Debug.Log(
                    $"[FlyMaze] MaleCNS fly cleared the maze with sensory-only control. " +
                    $"Last brain tick: {_brain.LastSpikeCount:N0} spikes.");
            }
        }

        private void BuildMazeGraph(MazeBuildInfo info)
        {
            InvalidateCueField();

            _gridWidth = Mathf.Max(1, info.Width);
            _gridHeight = Mathf.Max(1, info.Height);

            Transform floor = _mazeRoot != null ? _mazeRoot.Find("Maze Floor") : null;
            if (floor != null)
                _gridCellSize = Mathf.Max(0.5f, (floor.localScale.x - 0.9f) / _gridWidth);
            else
                _gridCellSize = 2.6f;

            _gridOriginLocal = new Vector3(
                -(_gridWidth - 1) * 0.5f * _gridCellSize,
                0f,
                -(_gridHeight - 1) * 0.5f * _gridCellSize);

            _neighborMask = new byte[_gridWidth * _gridHeight];
            Physics.SyncTransforms();

            for (int y = 0; y < _gridHeight; y++)
            {
                for (int x = 0; x < _gridWidth; x++)
                {
                    int index = GridIndex(x, y);
                    Vector3 center = GridCellWorld(x, y) + Vector3.up * sensorHeight;

                    for (int dir = 0; dir < 2; dir++)
                    {
                        int nx = x + GridDx[dir];
                        int ny = y + GridDy[dir];
                        if (!GridInBounds(nx, ny))
                            continue;

                        Vector3 other = GridCellWorld(nx, ny) + Vector3.up * sensorHeight;
                        if (WallBlocksSegment(center, other))
                            continue;

                        _neighborMask[index] |= GridBit[dir];
                        _neighborMask[GridIndex(nx, ny)] |= GridBit[GridOpposite[dir]];
                    }
                }
            }
        }

        private bool TryPrepareFoodCueField(FoodPickup[] foods, out Vector3 displayTarget)
        {
            displayTarget = transform.position;
            if (_neighborMask == null || foods == null)
                return false;

            List<int> sourceCells = new List<int>(foods.Length);
            int signature = 17;
            float nearestSqr = float.PositiveInfinity;
            bool found = false;

            unchecked
            {
                for (int i = 0; i < foods.Length; i++)
                {
                    FoodPickup food = foods[i];
                    if (food == null || food.IsCollected)
                        continue;

                    found = true;
                    int cell = WorldToGridIndex(food.transform.position);
                    if (cell >= 0 && !sourceCells.Contains(cell))
                        sourceCells.Add(cell);

                    signature = signature * 31 + i;
                    signature = signature * 31 + cell;

                    float sqr = (food.transform.position - transform.position).sqrMagnitude;
                    if (sqr < nearestSqr)
                    {
                        nearestSqr = sqr;
                        displayTarget = food.transform.position;
                    }
                }
            }

            if (!found || sourceCells.Count == 0)
                return false;

            if (_cueKind != 0 || _cueSignature != signature || _cueField == null)
            {
                RebuildCueField(sourceCells);
                _cueKind = 0;
                _cueSignature = signature;
            }

            return true;
        }

        private void EnsureGoalCueField()
        {
            if (_goal == null || _neighborMask == null)
                return;

            int goalCell = WorldToGridIndex(_goal.position);
            if (goalCell < 0)
                return;

            int signature = 1000003 + goalCell;
            if (_cueKind == 1 && _cueSignature == signature && _cueField != null)
                return;

            RebuildCueField(new List<int> { goalCell });
            _cueKind = 1;
            _cueSignature = signature;
        }

        private void RebuildCueField(List<int> sourceCells)
        {
            int count = _gridWidth * _gridHeight;
            _cueField = new float[count];

            Queue<int> queue = new Queue<int>(count);
            for (int i = 0; i < sourceCells.Count; i++)
            {
                int source = sourceCells[i];
                if (source < 0 || source >= count)
                    continue;

                _cueField[source] = 1f;
                queue.Enqueue(source);
            }

            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                int x = current % _gridWidth;
                int y = current / _gridWidth;
                float nextValue = _cueField[current] * corridorDiffusionFalloff;

                if (nextValue < 0.0025f)
                    continue;

                for (int dir = 0; dir < 4; dir++)
                {
                    if ((_neighborMask[current] & GridBit[dir]) == 0)
                        continue;

                    int nx = x + GridDx[dir];
                    int ny = y + GridDy[dir];
                    if (!GridInBounds(nx, ny))
                        continue;

                    int next = GridIndex(nx, ny);
                    if (nextValue <= _cueField[next] + 0.0001f)
                        continue;

                    _cueField[next] = nextValue;
                    queue.Enqueue(next);
                }
            }
        }

        private float SampleDiffusedCue(Vector3 worldPoint)
        {
            if (_cueField == null || _neighborMask == null || _mazeRoot == null)
                return 0f;

            Vector3 local = _mazeRoot.InverseTransformPoint(worldPoint);
            float gx = (local.x - _gridOriginLocal.x) / _gridCellSize;
            float gy = (local.z - _gridOriginLocal.z) / _gridCellSize;
            int x = Mathf.Clamp(Mathf.RoundToInt(gx), 0, _gridWidth - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(gy), 0, _gridHeight - 1);
            int index = GridIndex(x, y);

            float center = _cueField[index];

            float east = NeighborCue(index, x, y, 1, center);
            float west = NeighborCue(index, x, y, 3, center);
            float north = NeighborCue(index, x, y, 0, center);
            float south = NeighborCue(index, x, y, 2, center);

            Vector3 centerLocal = _gridOriginLocal +
                                  new Vector3(x * _gridCellSize, 0f, y * _gridCellSize);
            float localX = Mathf.Clamp(
                (local.x - centerLocal.x) / (_gridCellSize * 0.5f), -1f, 1f);
            float localZ = Mathf.Clamp(
                (local.z - centerLocal.z) / (_gridCellSize * 0.5f), -1f, 1f);

            float gradX = 0.5f * (east - west);
            float gradZ = 0.5f * (north - south);
            float sampled = center +
                            (gradX * localX + gradZ * localZ) * localGradientGain * 0.75f;

            return Mathf.Clamp01(sampled);
        }

        private float NeighborCue(int index, int x, int y, int dir, float fallback)
        {
            if ((_neighborMask[index] & GridBit[dir]) == 0)
                return fallback;

            int nx = x + GridDx[dir];
            int ny = y + GridDy[dir];
            if (!GridInBounds(nx, ny))
                return fallback;

            return _cueField[GridIndex(nx, ny)];
        }

        private int WorldToGridIndex(Vector3 world)
        {
            if (_mazeRoot == null || _gridWidth <= 0 || _gridHeight <= 0)
                return -1;

            Vector3 local = _mazeRoot.InverseTransformPoint(world);
            int x = Mathf.RoundToInt((local.x - _gridOriginLocal.x) / _gridCellSize);
            int y = Mathf.RoundToInt((local.z - _gridOriginLocal.z) / _gridCellSize);

            if (!GridInBounds(x, y))
                return -1;

            return GridIndex(x, y);
        }

        private Vector3 GridCellWorld(int x, int y)
        {
            Vector3 local = _gridOriginLocal +
                            new Vector3(x * _gridCellSize, 0f, y * _gridCellSize);
            return _mazeRoot != null ? _mazeRoot.TransformPoint(local) : local;
        }

        private int GridIndex(int x, int y)
        {
            return y * _gridWidth + x;
        }

        private bool GridInBounds(int x, int y)
        {
            return x >= 0 && y >= 0 && x < _gridWidth && y < _gridHeight;
        }

        private void InvalidateCueField()
        {
            _neighborMask = null;
            _cueField = null;
            _cueSignature = int.MinValue;
            _cueKind = -1;
            _gridWidth = 0;
            _gridHeight = 0;
        }

        private bool WallBlocksSegment(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= 0.01f)
                return false;

            RaycastHit[] hits = Physics.RaycastAll(
                from, delta / distance, distance, ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider != null &&
                    hits[i].collider.gameObject.name == "Wall")
                    return true;
            }

            return false;
        }

        private float SenseDirection(Vector3 origin, Vector3 direction)
        {
            float distance = DistanceToWall(origin, direction, obstacleRange);
            if (distance >= obstacleRange)
                return 0f;

            return 1f - Mathf.Clamp01(distance / obstacleRange);
        }

        private static float DistanceToWall(Vector3 origin, Vector3 direction, float maxDistance)
        {
            float nearest = maxDistance;
            RaycastHit[] hits = Physics.RaycastAll(
                origin, direction, maxDistance, ~0, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider != null &&
                    hits[i].collider.gameObject.name == "Wall")
                    nearest = Mathf.Min(nearest, hits[i].distance);
            }

            return nearest;
        }

        private void OnTriggerEnter(Collider other)
        {
            FoodPickup food = other.GetComponent<FoodPickup>();
            if (food == null || food.IsCollected)
                return;

            food.Collect();
            _brain?.GiveReward(foodReward);
            _cueSignature = int.MinValue;
        }

        private void BuildVisual()
        {
            _bodyMaterial = MakeMaterial(
                "Fly Body",
                new Color(0.055f, 0.065f, 0.075f, 1f),
                new Color(0.01f, 0.01f, 0.012f, 1f));
            _eyeMaterial = MakeMaterial(
                "Fly Eyes",
                new Color(0.72f, 0.035f, 0.055f, 1f),
                new Color(0.42f, 0.01f, 0.02f, 1f) * 1.6f);
            _wingMaterial = MakeTransparentMaterial(
                "Fly Wings",
                new Color(0.65f, 0.91f, 1f, 0.30f));

            GameObject visual = new GameObject("Fly Visual");
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = new Vector3(0f, 0.23f, 0f);
            visual.transform.localScale = Vector3.one * 0.72f;

            CreatePrimitive(
                PrimitiveType.Sphere, "Thorax", visual.transform,
                new Vector3(0f, 0f, 0f), new Vector3(0.42f, 0.28f, 0.56f), _bodyMaterial);
            CreatePrimitive(
                PrimitiveType.Sphere, "Abdomen", visual.transform,
                new Vector3(0f, -0.01f, -0.28f), new Vector3(0.34f, 0.24f, 0.52f), _bodyMaterial);
            CreatePrimitive(
                PrimitiveType.Sphere, "Head", visual.transform,
                new Vector3(0f, 0.02f, 0.30f), new Vector3(0.34f, 0.28f, 0.30f), _bodyMaterial);
            CreatePrimitive(
                PrimitiveType.Sphere, "Eye L", visual.transform,
                new Vector3(-0.16f, 0.055f, 0.39f), new Vector3(0.14f, 0.16f, 0.09f), _eyeMaterial);
            CreatePrimitive(
                PrimitiveType.Sphere, "Eye R", visual.transform,
                new Vector3(0.16f, 0.055f, 0.39f), new Vector3(0.14f, 0.16f, 0.09f), _eyeMaterial);

            _leftWing = CreatePrimitive(
                PrimitiveType.Sphere, "Wing L", visual.transform,
                new Vector3(-0.28f, 0.08f, -0.05f), new Vector3(0.48f, 0.055f, 0.68f),
                _wingMaterial).transform;
            _rightWing = CreatePrimitive(
                PrimitiveType.Sphere, "Wing R", visual.transform,
                new Vector3(0.28f, 0.08f, -0.05f), new Vector3(0.48f, 0.055f, 0.68f),
                _wingMaterial).transform;

            _leftWing.localRotation = Quaternion.Euler(0f, -25f, -14f);
            _rightWing.localRotation = Quaternion.Euler(0f, 25f, 14f);
        }

        private void AnimateWings()
        {
            if (_leftWing == null || _rightWing == null)
                return;

            _wingPhase += Time.unscaledDeltaTime * 38f;
            float flap = Mathf.Sin(_wingPhase) * 17f;
            _leftWing.localRotation = Quaternion.Euler(0f, -25f, -14f - flap);
            _rightWing.localRotation = Quaternion.Euler(0f, 25f, 14f + flap);
        }

        private static GameObject CreatePrimitive(
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

            Collider childCollider = go.GetComponent<Collider>();
            if (childCollider != null)
                childCollider.enabled = false;

            Renderer renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                renderer.sharedMaterial = material;
                renderer.shadowCastingMode = ShadowCastingMode.Off;
                renderer.receiveShadows = false;
            }

            return go;
        }

        private static Material MakeMaterial(string name, Color baseColor, Color emission)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            Material material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", baseColor);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", baseColor);

            if (material.HasProperty("_EmissionColor"))
            {
                material.SetColor("_EmissionColor", emission);
                material.EnableKeyword("_EMISSION");
            }

            return material;
        }

        private static Material MakeTransparentMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Sprites/Default");
            if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Unlit/Color");

            Material material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor"))
                material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color"))
                material.SetColor("_Color", color);
            if (material.HasProperty("_Surface"))
                material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_ZWrite"))
                material.SetFloat("_ZWrite", 0f);

            material.renderQueue = 3000;
            return material;
        }

        private void OnDestroy()
        {
            if (_generator != null)
            {
                _generator.GenerationStarted -= HandleGenerationStarted;
                _generator.MazeBuilt -= HandleMazeBuilt;
            }

            DestroyMaterial(_bodyMaterial);
            DestroyMaterial(_eyeMaterial);
            DestroyMaterial(_wingMaterial);
        }

        private static void DestroyMaterial(Material material)
        {
            if (material != null)
                Destroy(material);
        }
    }
}
