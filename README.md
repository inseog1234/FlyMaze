# FlyMaze

A fast Unity prototype for the fruit-fly connectome maze experiment.

## Current milestone

This first pass focuses on the presentation layer and procedural test arena:

- animated, game-like map setup HUD
- adjustable maze width / height
- twistiness control
- optional extra loops
- visible six-digit seed + reroll
- randomized DFS/backtracker maze generation
- animated wall-rise generation effect
- automatic START / GOAL markers
- automatic isometric camera framing
- URP/HDRP/Standard shader fallback without external art assets

The fly/connectome agent is intentionally not implemented yet. This keeps the first milestone clean: generate a readable test maze first, then wire the brain simulation into it.

## Fast setup

1. Pull the repository into your Unity project.
2. Wait for Unity to compile the scripts.
3. In the Unity top menu choose **Fly Maze > Create Demo Scene**.
4. Open `Assets/FlyMaze/Scenes/FlyMazeDemo.unity` if Unity did not open it automatically.
5. Press **Play**.

The whole demo is created from code, so there are no required prefabs, textures, or third-party packages beyond Unity UI.

You can also use **Fly Maze > Add To Current Scene** to add the system to an existing scene.

## Runtime controls

The left HUD lets you change:

- `WIDTH`: 5–31 cells
- `HEIGHT`: 5–25 cells
- `TWISTINESS`: how strongly the carving path tends to turn
- `EXTRA LOOPS`: chance to remove extra internal walls after carving
- `SEED`: press `REROLL` for a new deterministic layout

Press **GENERATE MAZE** to rebuild the arena. Walls animate upward and the camera reframes automatically.

## Code layout

```text
Assets/FlyMaze/
├─ Scripts/
│  ├─ FlyMazeBootstrap.cs
│  ├─ MazeSettings.cs
│  ├─ RandomMazeGenerator.cs
│  ├─ MazeSetupUI.cs
│  └─ HudButtonFX.cs
└─ Editor/
   └─ FlyMazeSceneCreator.cs
```

## Next milestone

The intended next step is:

```text
maze sensors
   ↓
connectome input neurons
   ↓
lightweight neural signal propagation
   ↓
left / right / forward motor outputs
   ↓
fly agent movement
```

That can be added without rewriting the maze or HUD systems in this commit.
