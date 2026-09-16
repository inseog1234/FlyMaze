using UnityEngine;

namespace FlyMaze
{
    [DisallowMultipleComponent]
    public sealed class FlyMazeBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            Camera camera = EnsureCamera();
            EnsureLighting();

            RandomMazeGenerator generator = GetComponent<RandomMazeGenerator>();
            if (generator == null)
                generator = gameObject.AddComponent<RandomMazeGenerator>();

            FoodPlacementController foodPlacement = GetComponent<FoodPlacementController>();
            if (foodPlacement == null)
                foodPlacement = gameObject.AddComponent<FoodPlacementController>();
            foodPlacement.Bind(generator, camera);

            MazeCameraController cameraController = camera.GetComponent<MazeCameraController>();
            if (cameraController == null)
                cameraController = camera.gameObject.AddComponent<MazeCameraController>();
            cameraController.Bind(generator, foodPlacement);

            MaleCNSBridge brain = GetComponent<MaleCNSBridge>();
            if (brain == null)
                brain = gameObject.AddComponent<MaleCNSBridge>();

            MaleCNSFlyAgent flyAgent = EnsureFlyAgent(generator, foodPlacement, brain);

            MazeSetupUI ui = GetComponent<MazeSetupUI>();
            if (ui == null)
                ui = gameObject.AddComponent<MazeSetupUI>();
            ui.Initialize(generator);

            FoodPlacementUI foodUi = GetComponent<FoodPlacementUI>();
            if (foodUi == null)
                foodUi = gameObject.AddComponent<FoodPlacementUI>();
            foodUi.Initialize(foodPlacement);

            MaleCNSStatusUI brainUi = GetComponent<MaleCNSStatusUI>();
            if (brainUi == null)
                brainUi = gameObject.AddComponent<MaleCNSStatusUI>();
            brainUi.Initialize(brain, flyAgent);

            MaleCNSLearningUI learningUi = GetComponent<MaleCNSLearningUI>();
            if (learningUi == null)
                learningUi = gameObject.AddComponent<MaleCNSLearningUI>();
            learningUi.Initialize(brain);

            MaleCNSAutoSetupUI setupUi = GetComponent<MaleCNSAutoSetupUI>();
            if (setupUi == null)
                setupUi = gameObject.AddComponent<MaleCNSAutoSetupUI>();
            setupUi.Initialize(brain);

            GameObject liveBadge = GameObject.Find("Live Badge");
            if (liveBadge != null)
                liveBadge.SetActive(false);

            HudWindowController.AttachNamed("Setup Panel", "MAP SETUP");
            HudWindowController.AttachNamed("Food Objective Bar", "FOOD OBJECTIVES");
            HudWindowController.AttachNamed("MaleCNS Neural Monitor", "MALECNS NEURAL MONITOR");
            HudWindowController.AttachNamed("MaleCNS Learning Panel", "LEARNING / PLASTICITY");

            HudTextQuality.Apply(transform);
        }

        private MaleCNSFlyAgent EnsureFlyAgent(RandomMazeGenerator generator, FoodPlacementController foodPlacement, MaleCNSBridge brain)
        {
            Transform existing = transform.Find("MaleCNS Fly Agent");
            GameObject fly = existing != null ? existing.gameObject : new GameObject("MaleCNS Fly Agent");
            if (existing == null)
                fly.transform.SetParent(transform, false);

            if (fly.GetComponent<Rigidbody>() == null)
                fly.AddComponent<Rigidbody>();
            if (fly.GetComponent<SphereCollider>() == null)
                fly.AddComponent<SphereCollider>();

            MaleCNSFlyAgent agent = fly.GetComponent<MaleCNSFlyAgent>();
            if (agent == null)
                agent = fly.AddComponent<MaleCNSFlyAgent>();
            agent.Initialize(generator, foodPlacement, brain);
            return agent;
        }

        private static Camera EnsureCamera()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                GameObject cameraObject = new GameObject("Main Camera", typeof(Camera), typeof(AudioListener));
                cameraObject.tag = "MainCamera";
                camera = cameraObject.GetComponent<Camera>();
            }

            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.008f, 0.012f, 0.022f, 1f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 500f;
            camera.allowHDR = true;
            return camera;
        }

        private static void EnsureLighting()
        {
            Light existingLight = FindFirstObjectByType<Light>();
            if (existingLight == null)
            {
                GameObject lightObject = new GameObject("Maze Key Light", typeof(Light));
                existingLight = lightObject.GetComponent<Light>();
                existingLight.type = LightType.Directional;
                existingLight.intensity = 1.35f;
                existingLight.color = new Color(0.78f, 0.90f, 1f, 1f);
                lightObject.transform.rotation = Quaternion.Euler(52f, -32f, 0f);
            }

            RenderSettings.ambientLight = new Color(0.11f, 0.14f, 0.20f, 1f);
        }
    }
}
