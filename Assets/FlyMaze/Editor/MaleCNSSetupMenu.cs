#if UNITY_EDITOR
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace FlyMaze.Editor
{
    public static class MaleCNSSetupMenu
    {
        [MenuItem("Fly Maze/MaleCNS/Setup v1.0")]
        public static void SetupMaleCNS()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

#if UNITY_EDITOR_WIN
            string script = Path.Combine(root, "Tools", "MaleCNS", "setup-malecns.ps1");
            if (!File.Exists(script))
            {
                Debug.LogError("[FlyMaze] Missing setup script: " + script);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -ExecutionPolicy Bypass -File \"{script}\"",
                WorkingDirectory = root,
                UseShellExecute = true
            });
#else
            string script = Path.Combine(root, "Tools", "MaleCNS", "setup-malecns.sh");
            if (!File.Exists(script))
            {
                Debug.LogError("[FlyMaze] Missing setup script: " + script);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"\"{script}\"",
                WorkingDirectory = root,
                UseShellExecute = true
            });
#endif

            Debug.Log("[FlyMaze] MaleCNS v1.0 setup launched. It downloads ~1.2 GB from the official Janelia dataset and builds the runtime cache under Library/MaleCNS.");
        }

        [MenuItem("Fly Maze/MaleCNS/Open Cache Folder")]
        public static void OpenCacheFolder()
        {
            string path = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "Library", "MaleCNS");
            Directory.CreateDirectory(path);
            EditorUtility.RevealInFinder(path);
        }

        [MenuItem("Fly Maze/MaleCNS/Print Status")]
        public static void PrintStatus()
        {
            string cache = Path.Combine(Path.GetFullPath(Path.Combine(Application.dataPath, "..")), "Library", "MaleCNS");
            bool weights = File.Exists(Path.Combine(cache, "malecns_weights.npz"));
            bool meta = File.Exists(Path.Combine(cache, "malecns_meta.npz"));
            bool manifest = File.Exists(Path.Combine(cache, "manifest.json"));
            Debug.Log($"[FlyMaze] MaleCNS cache: weights={weights}, meta={meta}, manifest={manifest}, path={cache}");
        }
    }
}
#endif
