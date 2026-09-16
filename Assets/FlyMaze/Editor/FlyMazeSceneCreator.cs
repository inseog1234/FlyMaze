#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FlyMaze.Editor
{
    public static class FlyMazeSceneCreator
    {
        private const string ScenePath = "Assets/FlyMaze/Scenes/FlyMazeDemo.unity";

        [MenuItem("Fly Maze/Create Demo Scene")]
        public static void CreateDemoScene()
        {
            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            GameObject root = CreateSystemObject();

            Directory.CreateDirectory("Assets/FlyMaze/Scenes");
            AssetDatabase.Refresh();
            EditorSceneManager.SaveScene(scene, ScenePath);
            Selection.activeGameObject = root;

            Debug.Log($"[FlyMaze] Demo scene created: {ScenePath}. Press Play to open the animated maze setup UI.");
        }

        [MenuItem("Fly Maze/Add To Current Scene")]
        public static void AddToCurrentScene()
        {
            FlyMazeBootstrap existing = Object.FindFirstObjectByType<FlyMazeBootstrap>();
            if (existing != null)
            {
                Selection.activeGameObject = existing.gameObject;
                Debug.Log("[FlyMaze] A FlyMaze system already exists in this scene.");
                return;
            }

            GameObject root = CreateSystemObject();
            Undo.RegisterCreatedObjectUndo(root, "Add FlyMaze System");
            Selection.activeGameObject = root;
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[FlyMaze] Added to the current scene. Press Play to generate the maze.");
        }

        private static GameObject CreateSystemObject()
        {
            GameObject root = new GameObject("FlyMaze System");
            root.AddComponent<FlyMazeBootstrap>();
            return root;
        }
    }
}
#endif
