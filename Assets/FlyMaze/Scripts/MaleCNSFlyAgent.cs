using UnityEngine;
using UnityEngine.Rendering;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
    public sealed class MaleCNSFlyAgent : MonoBehaviour
    {
        [Header("Movement")]
        [SerializeField] private float moveSpeed = 2.0f;
        [SerializeField] private float turnSpeed = 220f;
        [SerializeField, Range(0f, 0.8f)] private float locomotorFloor = 0.24f;

        [Header("Sensing")]
        [SerializeField] private float obstacleRange = 2.5f;
        [SerializeField] private float targetSenseFalloff = 0.075f;
        [SerializeField] private float sensorHeight = 0.34f;

        [Header("Collision Recovery")]
        [SerializeField] private float stuckDetectionSeconds = 0.28f;
        [SerializeField] private float recoverySeconds = 0.72f;
        [SerializeField] private float recoveryTurnSpeed = 300f;
        [SerializeField] private float recoveryReverseSpeed = 1.35f;

        public bool Finished { get; private set; }
        public bool RecoveryActive => _recoveryTimer > 0f;
        public Vector3 CurrentTarget { get; private set; }
        public int CurrentTargetKind { get; private set; } = -1;

        private RandomMazeGenerator _generator;
        private FoodPlacementController _foodPlacement;
        private MaleCNSBridge _brain;
        private Rigidbody _body;
        private Transform _goal;
        private Transform _leftWing;
        private Transform _rightWing;
        private float _wingPhase;
        private bool _initialized;

        private float _stuckTimer;
        private float _recoveryTimer;
        private float _recoveryTurnSign = 1f;
        private float _warmupUntil;
        private Vector3 _lastFixedPosition;

        private Material _bodyMaterial;
        private Material _eyeMaterial;
        private Material _wingMaterial;

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
            _body.linearDamping = 5f;
            _body.angularDamping = 12f;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

            SphereCollider collider = GetComponent<SphereCollider>();
            collider.radius = 0.27f;
            collider.center = new Vector3(0f, 0.32f, 0f);

            BuildVisual();

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
            _stuckTimer = 0f;
            _recoveryTimer = 0f;
            if (_body != null)
                _body.linearVelocity = Vector3.zero;
            _brain?.ResetNetwork();
        }

        private void HandleMazeBuilt(MazeBuildInfo _)
        {
            Transform maze = _generator != null ? _generator.transform.Find("Generated Maze") : null;
            if (maze == null)
                return;

            Transform start = maze.Find("START");
            _goal = maze.Find("GOAL");
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
            _recoveryTimer = 0f;
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
                float distance = obstacleRange * 2.2f;
                if (Physics.Raycast(origin, directions[i], out RaycastHit hit, distance, ~0, QueryTriggerInteraction.Ignore) &&
                    hit.collider != null && hit.collider.gameObject.name == "Wall")
                {
                    distance = hit.distance;
                }

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

            ResolveTarget(out Vector3 target, out int targetKind);
            CurrentTarget = target;
            CurrentTargetKind = targetKind;

            Vector3 origin = transform.position + Vector3.up * sensorHeight;
            float front = SenseDirection(origin, transform.forward);
            float left = Mathf.Max(
                SenseDirection(origin, Quaternion.Euler(0f, -25f, 0f) * transform.forward),
                SenseDirection(origin, Quaternion.Euler(0f, -58f, 0f) * transform.forward));
            float right = Mathf.Max(
                SenseDirection(origin, Quaternion.Euler(0f, 25f, 0f) * transform.forward),
                SenseDirection(origin, Quaternion.Euler(0f, 58f, 0f) * transform.forward));

            float bearing = 0f;
            float strength = 0f;
            if (targetKind >= 0)
            {
                Vector3 planar = target - transform.position;
                planar.y = 0f;
                float distance = planar.magnitude;
                if (distance > 0.001f)
                {
                    bearing = Mathf.Clamp(Vector3.SignedAngle(transform.forward, planar / distance, Vector3.up) / 90f, -1f, 1f);
                    strength = 1f / (1f + distance * targetSenseFalloff);
                }
            }

            _brain.SubmitSensory(new MaleCNSSensoryFrame(front, left, right, bearing, strength, targetKind));

            if (!_brain.IsReady || Time.fixedTime < _warmupUntil)
            {
                _body.linearVelocity = Vector3.zero;
                return;
            }

            if (_recoveryTimer > 0f)
            {
                RunRecovery();
                return;
            }

            // Only intervene after the connectome has physically failed to make progress into a wall.
            // Normal navigation remains driven by MaleCNS outputs; this is a last-resort collision reflex.
            bool pressedIntoWall = front > 0.58f && movedThisStep < 0.008f;
            if (pressedIntoWall)
                _stuckTimer += Time.fixedDeltaTime;
            else
                _stuckTimer = Mathf.Max(0f, _stuckTimer - Time.fixedDeltaTime * 2f);

            if (_stuckTimer >= stuckDetectionSeconds)
            {
                BeginRecovery(left, right, bearing);
                RunRecovery();
                return;
            }

            float turn = _brain.TurnOutput;
            float speedDrive = Mathf.Lerp(locomotorFloor, 1f, _brain.ForwardOutput);
            if (_brain.EscapeOutput > 0.15f)
                speedDrive = Mathf.Clamp01(speedDrive + _brain.EscapeOutput * 0.30f);

            // Near a wall, give the actual connectome turn output more leverage and reduce forward shove.
            float wallPressure = Mathf.Clamp01(front);
            float effectiveTurnSpeed = Mathf.Lerp(turnSpeed, turnSpeed * 1.35f, wallPressure);
            speedDrive *= Mathf.Lerp(1f, 0.46f, wallPressure);

            Quaternion rotation = Quaternion.Euler(0f, turn * effectiveTurnSpeed * Time.fixedDeltaTime, 0f) * _body.rotation;
            _body.MoveRotation(rotation);

            Vector3 velocity = (rotation * Vector3.forward) * (moveSpeed * speedDrive);
            velocity.y = 0f;
            _body.linearVelocity = velocity;

            if (targetKind == 1 && _goal != null && Vector3.Distance(transform.position, _goal.position) < 0.55f)
            {
                Finished = true;
                _body.linearVelocity = Vector3.zero;
                Debug.Log($"[FlyMaze] MaleCNS fly cleared the maze. Last brain tick: {_brain.LastSpikeCount:N0} spikes.");
            }
        }

        private void BeginRecovery(float leftObstacle, float rightObstacle, float targetBearing)
        {
            _stuckTimer = 0f;
            _recoveryTimer = recoverySeconds;

            if (leftObstacle > rightObstacle + 0.05f)
                _recoveryTurnSign = 1f;
            else if (rightObstacle > leftObstacle + 0.05f)
                _recoveryTurnSign = -1f;
            else if (Mathf.Abs(targetBearing) > 0.08f)
                _recoveryTurnSign = Mathf.Sign(targetBearing);
            else
                _recoveryTurnSign = -_recoveryTurnSign;
        }

        private void RunRecovery()
        {
            _recoveryTimer = Mathf.Max(0f, _recoveryTimer - Time.fixedDeltaTime);
            Quaternion rotation = Quaternion.Euler(0f, _recoveryTurnSign * recoveryTurnSpeed * Time.fixedDeltaTime, 0f) * _body.rotation;
            _body.MoveRotation(rotation);

            float reverseFactor = Mathf.Clamp01(_recoveryTimer / Mathf.Max(0.01f, recoverySeconds));
            Vector3 velocity = -(rotation * Vector3.forward) * (recoveryReverseSpeed * Mathf.Lerp(0.35f, 1f, reverseFactor));
            velocity.y = 0f;
            _body.linearVelocity = velocity;
        }

        private void ResolveTarget(out Vector3 target, out int targetKind)
        {
            FoodPickup[] foods = FindObjectsByType<FoodPickup>(FindObjectsSortMode.None);
            float bestSqr = float.PositiveInfinity;
            FoodPickup best = null;

            for (int i = 0; i < foods.Length; i++)
            {
                FoodPickup food = foods[i];
                if (food == null || food.IsCollected)
                    continue;

                float sqr = (food.transform.position - transform.position).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = food;
                }
            }

            if (best != null)
            {
                target = best.transform.position;
                targetKind = 0;
                return;
            }

            if (_goal != null)
            {
                target = _goal.position;
                targetKind = 1;
                return;
            }

            target = transform.position;
            targetKind = -1;
        }

        private float SenseDirection(Vector3 origin, Vector3 direction)
        {
            if (!Physics.Raycast(origin, direction, out RaycastHit hit, obstacleRange, ~0, QueryTriggerInteraction.Ignore))
                return 0f;

            if (hit.collider == null || hit.collider.gameObject.name != "Wall")
                return 0f;

            return 1f - Mathf.Clamp01(hit.distance / obstacleRange);
        }

        private void OnTriggerEnter(Collider other)
        {
            FoodPickup food = other.GetComponent<FoodPickup>();
            if (food != null && !food.IsCollected)
                food.Collect();
        }

        private void BuildVisual()
        {
            _bodyMaterial = MakeMaterial("Fly Body", new Color(0.055f, 0.065f, 0.075f, 1f), new Color(0.01f, 0.01f, 0.012f, 1f));
            _eyeMaterial = MakeMaterial("Fly Eyes", new Color(0.72f, 0.035f, 0.055f, 1f), new Color(0.42f, 0.01f, 0.02f, 1f) * 1.6f);
            _wingMaterial = MakeTransparentMaterial("Fly Wings", new Color(0.65f, 0.91f, 1f, 0.30f));

            GameObject visual = new GameObject("Fly Visual");
            visual.transform.SetParent(transform, false);
            visual.transform.localPosition = new Vector3(0f, 0.34f, 0f);

            CreatePrimitive(PrimitiveType.Sphere, "Thorax", visual.transform, new Vector3(0f, 0f, 0f), new Vector3(0.42f, 0.28f, 0.56f), _bodyMaterial);
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
            if (shader == null) shader = Shader.Find("Unlit/Color");
            Material material = new Material(shader) { name = name };
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Surface")) material.SetFloat("_Surface", 1f);
            if (material.HasProperty("_ZWrite")) material.SetFloat("_ZWrite", 0f);
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
