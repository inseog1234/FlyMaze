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
        [SerializeField] private float sensorHeight = 0.34f;

        [Header("Olfaction")]
        [SerializeField] private float antennaSeparation = 0.34f;
        [SerializeField] private float antennaForward = 0.28f;
        [SerializeField] private float smellDistanceFalloff = 0.19f;
        [SerializeField, Range(0f, 1f)] private float wallScentTransmission = 0.34f;

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
            _body.linearDamping = 4.5f;
            _body.angularDamping = 10f;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.constraints = RigidbodyConstraints.FreezePositionY | RigidbodyConstraints.FreezeRotationX | RigidbodyConstraints.FreezeRotationZ;

            SphereCollider collider = GetComponent<SphereCollider>();
            collider.radius = 0.26f;
            collider.center = new Vector3(0f, 0.32f, 0f);

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

            bool hasFood = SampleFoodField(leftAntenna, rightAntenna, out float leftSignal, out float rightSignal, out Vector3 displayTarget);
            int targetKind;
            if (hasFood)
            {
                targetKind = 0;
                CurrentTarget = displayTarget;
            }
            else if (_goal != null)
            {
                targetKind = 1;
                SampleSingleCue(leftAntenna, rightAntenna, _goal.position + Vector3.up * sensorHeight, 0.26f, out leftSignal, out rightSignal);
                CurrentTarget = _goal.position;
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

            float totalSignal = Mathf.Clamp01((leftSignal + rightSignal) * 0.62f);
            float lateral = rightSignal - leftSignal;
            float bearing = Mathf.Abs(leftSignal) + Mathf.Abs(rightSignal) > 0.0001f
                ? Mathf.Clamp(lateral / (Mathf.Abs(leftSignal) + Mathf.Abs(rightSignal) + 0.0001f), -1f, 1f)
                : 0f;

            // Important: this is the whole navigation input. No A*, BFS, waypoint, target-vector
            // steering, or scripted obstacle turn is mixed into the motor command. Unity only turns
            // the environment into local visual/olfactory sensory signals.
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

            // Motor behavior is decoded only from MaleCNS descending-neuron activity.
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
                Debug.Log($"[FlyMaze] MaleCNS fly cleared the maze with sensory-only control. Last brain tick: {_brain.LastSpikeCount:N0} spikes.");
            }
        }

        private bool SampleFoodField(Vector3 leftAntenna, Vector3 rightAntenna, out float leftSignal, out float rightSignal, out Vector3 displayTarget)
        {
            leftSignal = 0f;
            rightSignal = 0f;
            displayTarget = transform.position;

            FoodPickup[] foods = FindObjectsByType<FoodPickup>(FindObjectsSortMode.None);
            bool found = false;
            float nearestSqr = float.PositiveInfinity;

            for (int i = 0; i < foods.Length; i++)
            {
                FoodPickup food = foods[i];
                if (food == null || food.IsCollected)
                    continue;

                found = true;
                Vector3 source = food.transform.position + Vector3.up * sensorHeight;
                leftSignal += SampleScent(leftAntenna, source);
                rightSignal += SampleScent(rightAntenna, source);

                float sqr = (food.transform.position - transform.position).sqrMagnitude;
                if (sqr < nearestSqr)
                {
                    nearestSqr = sqr;
                    displayTarget = food.transform.position;
                }
            }

            leftSignal = Mathf.Clamp01(leftSignal);
            rightSignal = Mathf.Clamp01(rightSignal);
            return found;
        }

        private float SampleScent(Vector3 samplePoint, Vector3 source)
        {
            float distance = Vector3.Distance(samplePoint, source);
            float intensity = 1f / (1f + distance * distance * smellDistanceFalloff);
            if (WallBlocksSegment(samplePoint, source))
                intensity *= wallScentTransmission;
            return intensity;
        }

        private void SampleSingleCue(Vector3 leftSample, Vector3 rightSample, Vector3 source, float falloff,
            out float leftSignal, out float rightSignal)
        {
            leftSignal = CueIntensity(leftSample, source, falloff);
            rightSignal = CueIntensity(rightSample, source, falloff);
        }

        private float CueIntensity(Vector3 samplePoint, Vector3 source, float falloff)
        {
            float distance = Vector3.Distance(samplePoint, source);
            float intensity = 1f / (1f + distance * distance * falloff);
            if (WallBlocksSegment(samplePoint, source))
                intensity *= 0.18f;
            return Mathf.Clamp01(intensity);
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
                if (hits[i].collider != null && hits[i].collider.gameObject.name == "Wall")
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
            _brain?.GiveReward(foodReward);
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
