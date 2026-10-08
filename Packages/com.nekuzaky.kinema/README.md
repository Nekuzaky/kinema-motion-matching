# Kinema Motion Matching

`com.nekuzaky.kinema`

Data-driven motion matching locomotion for Unity: an offline bake pipeline, a normalized feature
database (pose, trajectory, gait phase), a Burst-compiled weighted search, a `PlayableGraph`
runtime, and an integrated editor window with live debug, in-window recording/ghost replay, a
one-click rig swap, and a search-performance benchmark.

## Requirements

- Unity 2021.3 LTS or newer (2021.3, 2022.3, Unity 6).
- The runtime depends only on Burst and Mathematics, resolved automatically. The sample uses the Input
  System and URP when installed, and falls back to the legacy Input Manager and the built-in pipeline
  when not.

## Installation

Package Manager, "Add package from git URL":

```
https://github.com/Nekuzaky/kinema.git?path=/Packages/com.nekuzaky.kinema
```

Or add to `Packages/manifest.json`:

```json
"com.nekuzaky.kinema": "https://github.com/Nekuzaky/kinema.git?path=/Packages/com.nekuzaky.kinema"
```

## Quick start

1. Open the tool: `Tools > Kinema > Motion Matching Window` (Ctrl+Shift+M).
2. Create a config (Overview tab) and assign a Humanoid or Generic rig plus locomotion clips.
3. Bake the database (Bake tab).
4. Add a `MotionMatchingController` to your character, assign the database, and drive it with an
   `ILocomotionProvider` (the sample ships one).

See the "Locomotion Demo" sample and run `Tools > Kinema > Demo Scene` for a fully wired scene built
in one click.

## Layout

- `Runtime/` - data, database, matcher, controller, recording/replay/ghosting. No package dependencies.
- `Editor/` - bake pipeline, editor window (Overview/Database/Bake/Tags/Director/Debug/Analysis/Settings),
  inspectors, rig swap, search benchmark.
- `Samples~/Locomotion Demo/` - collision motor, input provider (keyboard + gamepad), orbit follow
  camera, vault/free-jump trigger, demo generator.

## Documentation

Full documentation lives in `Documentation~/index.md`. License: MIT (`LICENSE.md`).
