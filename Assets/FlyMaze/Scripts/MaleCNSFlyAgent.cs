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
        private float _gridCellSize;
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
            _body.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

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

            Vector3 leftAntenna = transform.position + transform.forward * antennaForward - transform.right * (antennaSeparation * 0.5f);
            Vector3 rightAntenna = transform.position + transform.forward * antennaForward + transform.right * (antennaSeparation * 0.5f);
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
            float lateral = rightSignal - leftSignal;
            float sum = Mathf.Abs(leftSignal) + Mathf.Abs(rightSignal);
            float bearing = sum > 0.0001f
                ? Mathf.Clamp((lateral / (sum + 0.0001f)) * 1.8f, -1f, 1f)
                : 0f;

            // No route/waypoint is sent to the motor controller. The maze graph below is used only
            // to model an odor/cue field diffusing through open corridors. The fly receives the
            // concentration sampled at its two antennae, then MaleCNS decides the motor output.
            _brain.SubmitSensory(new MaleCNSSensoryFrame(VisionFront, VisionLeft, VisionRight, bearing, totalSignal, targetKind));

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
            Quaternion rotation = Quaternion.Euler(0f, turn * turnSpeed * Time.fixedDeltaTime, 0f) * _body.rotation;
            _body.MoveRotation(rotation);

            Vector3 velocity = (rotation * Vector3.forward) * (moveSpeed * speedDrive);
            velocity.y = 0f;
            _body.linearVelocity = velocity;

            if (targetKind == 1 && _goal != null && Vector3.Distance(transform.position, _goal.position) < 0.58f)
            {
                Finished = true;
                _body.linearVelocity = Vector3.zero;
                _brain.GiveReward(goalReward);
                Debug.Log($"[FlyMaze] MaleCNS fly cleared the maze with sensory-only control. Last brain tick: {_brain.LastSpikeCount:N,0} spikes.");
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

        private bool TryPrepareFoodCueField(FoodPickup[] foods, out Vector3 displayTarget)
        {
            displayTarget = transform.position;
            if (_neighborMask == null || foods == null)
                return false;

            List<Vector2Int> sources = new List<Vector2Int>(foods.Length);
            int signature = 17;
            float nearestSqr = float.PositiveInfinity;

            for (int i = 0; i < foods.Length; i++)
            {
                FoodPickup food = foods[i];
                if (food == null || food.IsCollected)
                    continue;

                if (TryWorldToCell(food.transform.position, out int x, out int y))
                    sources.Add(new Vector2Int(x, y));

                signature = unchecked(signature * 31 + food.GetInstanceID());
                float sqr = (food.transform.position - transform.position).sqrMagnitude;
                if (sqr < nearestSqr)
                {
                    nearestSqr = sqr;
                    displayTarget = food.transform.position;
                }
            }

            if (sources.Count == 0)
                return false;

            signature = unchecked(signature * 31 + sources.Count);
            if (_cueField == null || _cueKind != 0 || _cueSignature != signature)
            {
                BuildDiffusionField(sources);
                _cueKind = 0;
                _cueSignature = signature;
            }

            return true;
        }

        private void EnsureGoalCueField()
        {
             if (_goal == null || _neighborMask == null)
                return;
            if (!TryWorldToCell(_goal.position, out int x, out int y))
                return;

            int signature = unchecked(991 * 31 + GridIndex(x, y));
            if (_cueField != null && _cueKind == 1 && _cueSignature == signature)
                return;

            BuildDiffusionField(new List<Vector2Int> { new Vector2Int(x, y) });
            _cueKind = 1;
            _cueSignature = signature;
        }

        private void BuildDiffusionField(List<Vector2Int> sources)
        {
            int count = _gridWidth * _gridHeight;
            int[] distance = new int[count];
            for (int i = 0; i < count; i++)
                distance[i] = int.MaxValue;

            Queue<int> queue = new Queue<int>(count);
            for (int i = 0; i < sources.Count; i++)
            {
                int source = GridIndex(sources[i].x, sources[i].y);
                if (distance[source] == 0)
                    continue;
                distance[source] = 0;
                queue.Enqueue(source);
            }

            while (queue.Count > 0)
            {
                int current = queuee.Dequeue();
                int cx = current % _gridWidth;
                int cy = current / _gridWidth;
                int nextDistance = distance[current] + 1;
                byte open = _neighborMask[current];

                for (int dir = 0; dir < 4; dir++)
                {
                    if ((open & GridBit[dir]) == 0)
                        continue;
                    int nx = cx + GridDx[dir];
                    int ny = cy + GridDy[dir];
                    if (!GridInBounds(nx, ny))
                        continue;
                    int next = GridIndex(nx, ny);
                    if (distance[next] <= nextDistance)
                        continue;
                    distance[next] = nextDistance;
                    queue.Enqueue(next);
                }
            }

            _cueField = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (distance[i] == int.MaxValue)
                    _cueField[i] = 0f;
                else
                    _cueField[i] = Mathf.Exp(-distance[i] * corridorDiffusionFalloff);
            }
        }

        private float SampleDiffusedCue(Vector3 worldPoint)
        {
             if (_cueField == null || _neighborMask == null || !TryWorldToCell(worldPoint, out int x, out int y))
                return 0f;

            int index = GridIndex(x, y);
            float center = _cueField[index];
            byte open = _neighborMask[index];

            float north = NeighborCueOrCenter(x, y, 0, center, open);
            float east = NeighborCueOrCenter(x, y, 1, center, open);
            float south = NeighborCueOrCenter(x, y, 2, center, open);
            float west = NeighborCueOrCenter(x, y, 3, center, open);

            Vector3 local = _mazeRoot.InverseTransformPoint(worldPoint);
            Vector3 cellCenter = _gridOriginLocal + new Vector3(x * _gridCellSize, 0f, y * _gridCellSize);
            float localX = Mathf.Clamp((local.x - cellCenter.x) / (_gridCellSize * 0.5f), -1f, 1f);
            float localZ = Mathf.Clamp((local.z - cellCenter.z) / (_gridCellSize * 0.5f), -1f, 1f);

            float gradientX = (east - west) * 0.5f;
            float gradientZ = (north - south) * 0.5f;
            float value = center + (gradientX * localX + gradientZ * localZ) * localGradientGain;
            return Mathf.Clamp01(value);
        }

        private float NeighborCueOrCenter(int x, int y, int dir, float center, byte open)
        {
            if ((open & GridBit[dir]) == 0)
                return center;
            int nx = x + GridDx[dir];
             int ny = y + GridDy[dir];
            if (!GridInBounds(nx, ny))
                return center;
            return _cueField[GridIndex(nx, ny)];
        }

        private bool TryWorldToCell(Vector3 world, out int x, out int y)
        {
            x = y = 0;
            if (_mazeRoot == null || _gridCellSize <= 0f || _gridWidth <= 0 || _gridHeight <= 0)
                return false;

            Vector3 local = _mazeRoot.InverseTransformPoint(world);
            x = Mathf.RoundToInt((local.x - _gridOriginLocal.x) / _gridCellSize);
            y = Mathf.RoundToInt((local.z - _gridOriginLocal.z) / _gridCellSize);
            x = Mathf.Clamp(x, 0, _gridWidth - 1);
            y = Mathf.Clamp(y, 0, _gridHeight - 1);
            return true;
        }

        private Vector3 GridCellWorld(int x, int y)
        {
            Vector3 local = _gridOriginLocal + new Vector3(x * _gridCellSize, 0f, y * _gridCellSize);
            return _mazeRoot != null ? _mazeRoot.TransformPoint(local) : local;
        }

        private int GridIndex(int x, int y) => y * _gridWidth + x;
        private bool GridInBounds(int x, int y) => x >= 0 && y >= 0 && x < _gridWidth && y < _gridHeight;

        private void InvalidateCueField()
        {
            _cueField = null;
            _cueSignature = int.MinValue;
            _cueKind = -1;
        }

        private bool WallBlocksSegment(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance <= 0.01f)
                return false;

            RaycastHit[] hits = Physics.RaycastAll(from, delta / distance, distance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider != null && collider.gameObject.name == "Wall")
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
            RaycastHit[] hits = Physics.RaycastAll(origin, direction, maxDistance, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                if (hits[i].collider != null && hits[i].collider.gameObject.name == "Wall")
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
            InvalidateCueField();
            _brain?.GiveReward(foodReward);
        }

        private void BuildVisual()
        {
            _bodyMaterial = MakeMaterial("Fly Body", new Color(0.055f, 0.065f, 0.075f, 1f), new Color(0.01f, 0.01f, 0.012f, 1f));
            _eyeMaterial = MakeMaterial("Fly Eyes", new Color(0.72f, 0.035f, 0.055f, 1f), new Color(0.42f, 0.01f, 0.02f, 1f) * 1.6f);
            _wingMaterial = MakeTransparentMaterial("Fly Wings", new Color(0.65f, 0.91f, 1f, 0.30f));

            GameObject visual = new GameObject("Fly Visual");
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = new Vector3(0f, 0.245f, 0f);
            visual.transform.localScale = Vector3.one * 0.72f;

            CreatePrimitive(PrimitiveType.Sphere, "Thorax", visual.transform, Vector3.zero, new Vector3(0.42f, 0.28f, 0.56f), _bodyMaterial);
            CreatePrimitive(PrimitiveType.Sphere, "Abdomen", visual.transform, new Vector3(0f, -0.01f, -0.28f), new Vector3(0.34f, 0.24f, 0.52f), _bodyMaterial);
            CreatePrimitive(PrimitiveType.Sphere, "Head", visual.transform, new Vector3(0f, 0.02f, 0.30f), new Vector3(0.34f, 0.28f, 0.30f), _bodyMaterial);
            CreatePrimitive(PrimitiveType.Sphere, "Eye L", visual.transform, new Vector3(-0.16f, 0.055f, 0.39f), new Vector3(0.14f, 0.16f, 0.09f), _eyeMaterial);
            CreatePrimitive(PrimitiveType.Sphere, "Eye R", visual.transform, new Vector3(0.16f, 0.055f, 0.39f), new Vector3(0.14f, 0.16f, 0.09f), _eyeMaterial);

            _leftWing = CreatePrimitive(PrimitiveType.Sphere, "Wing L", visual.transform, new Vector3(-0.28f, 0.08f, -0.05f), new Vector3(0.48f, 0.055f, 0.68f), _wingMaterial).transform;
            _rightWing = CreatePrimitive(PrimitiveType.Sphere, "Wing R", visual.transform, new Vector3(0.28f, 0.08f, -0.05f), new Vector3(0.48f, 0.055f, 0.68f), _wingMaterial).transform;
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

        private static GameObject CreatePrimitive(PrimitiveType type, string objectName, Transform parent, Vector3 localPosition, Vector3 localScale, Material material)
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
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", baseColor);
            if (material.HasProperty("_Color")) material.SetRColor("_Color", baseColor);
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
            if (shader == null¤Í¡…‘•È€ôM¡…‘•È¹¥¹ ‰U¹±¥Ğ½½±½Èˆ¤ì(€€€€€€€€€€€5…Ñ•É¥…°µ…Ñ•É¥…°€ô¹•Ü5…Ñ•É¥…°¡Í¡…‘•È¤ì¹…µ”€ô¹…µ”ôì(€€€€€€€€€€€¥˜€¡µ…Ñ•É¥…°¹!…ÍAÉ½Á•ÉÑä ‰}	…Í•½±½Èˆ¤¤µ…Ñ•É¥…°¹M•ÑI½±½È ‰}	…Í•½±½Èˆ°½±½È¤ì(€€€€€€€€€€€¥˜€¡µ…Ñ•É¥…°¹!…ÍAÉ½Á•ÉÑä ‰}½±½Èˆ¤¤µ…Ñ•É¥…°¹M•Ñ½±½È ‰}½±½Èˆ°½±½È¤ì(€€€€€€€€€€€¥˜€¡µ…Ñ•É¥…°¹!…ÍAÉ½Á•ÉÑä ‰}MÕÉ™…”ˆ¤¤µ…Ñ•É¥…°¹M•Ñ±½…Ğ ‰}MÕÉ™…”ˆ°€Å˜¤ì(€€€€€€€€€€€¥˜€¡µ…Ñ•É¥…°¹!…ÍAÉ½Á•ÉÑä ‰}i]É¥Ñ”ˆ¤¤µ…Ñ•É¥…°¹M•Ñ±½…Ğ ‰}i]É¥Ñ”ˆ°€Á˜¤ì(€€€€€€€€€€€µ…Ñ•É¥…°¹É•¹‘•ÉEÕ•Õ”€ô€ÌÀÀÀì(€€€€€€€€€€€É•ÑÕÉ¸µ…Ñ•É¥…°ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”Ù½¥=¹•ÍÑÉ½ä ¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡}•¹•É…Ñ½È€„ô¹Õ±°¤(€€€€€€€€€€€ì(€€€€€€€€€€€€€€€}•¹•É…Ñ½È¹•¹•É…Ñ¥½¹MÑ…ÉÑ•€´ô!…¹‘±••¹•É…Ñ¥½¹MÑ…ÉÑ•ì(€€€€€€€€€€€€€€€}•¹•É…Ñ½È¹5…é•	Õ¥±Ğ€´ô!…¹‘±•5…é•	Õ¥±Ğì(€€€€€€€€€€€ô((€€€€€€€€€€€•ÍÑÉ½å5…Ñ•É¥…°¡}‰½‘å5…Ñ•É¥…°¤ì(€€€€€€€€€€€•ÍÑÉ½å5…Ñ•É¥…°¡}•å•5…Ñ•É¥…°¤ì(€€€€€€€€€€€•ÍÑÉ½å5…Ñ•É¥…°¡}İ¥¹5…Ñ•É¥…°¤ì(€€€€€€€ô((€€€€€€€ÁÉ¥Ù…Ñ”ÍÑ…Ñ¥ŒÙ½¥•ÍÑÉ½å5…Ñ•É¥…°¡5…Ñ•É¥…°µ…Ñ•É¥…°¤(€€€€€€€ì(€€€€€€€€€€€¥˜€¡µ…Ñ•É¥…°€„ô¹Õ±°¤(€€€€€€€€€€€€€€€•ÍÑÉ½ä¡µ…Ñ•É¥…°¤ì(€€€€€€€ô(€€€ô)ô(