using System;
using UnityEngine;

namespace FlyMaze
{
    [Serializable]
    public sealed class MazeSettings
    {
        [Range(5, 31)] public int width = 15;
        [Range(5, 25)] public int height = 11;
        [Range(1.4f, 4.0f)] public float cellSize = 2.6f;
        [Range(0f, 1f)] public float twistiness = 0.42f;
        [Range(0f, 0.18f)] public float extraLoopChance = 0.06f;
        public int seed = 0;

        public MazeSettings Clone()
        {
            return (MazeSettings)MemberwiseClone();
        }

        public void Clamp()
        {
            width = Mathf.Clamp(width, 5, 31);
            height = Mathf.Clamp(height, 5, 25);
            cellSize = Mathf.Clamp(cellSize, 1.4f, 4.0f);
            twistiness = Mathf.Clamp01(twistiness);
            extraLoopChance = Mathf.Clamp(extraLoopChance, 0f, 0.18f);
        }
    }

    public readonly struct MazeBuildInfo
    {
        public readonly int Width;
        public readonly int Height;
        public readonly int Seed;
        public readonly int WallCount;

        public int CellCount => Width * Height;

        public MazeBuildInfo(int width, int height, int seed, int wallCount)
        {
            Width = width;
            Height = height;
            Seed = seed;
            WallCount = wallCount;
        }
    }
}
