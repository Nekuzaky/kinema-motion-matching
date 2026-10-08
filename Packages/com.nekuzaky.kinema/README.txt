CHROMOVEMENT MOTION MATCHING
com.nekuzaky.kinema

Data-driven motion matching locomotion for Unity. Offline bake pipeline, normalized feature
database, configurable weighted scorer, PlayableGraph runtime, and an integrated editor tool
with live debug.

REQUIREMENTS
- Unity 2021.3 LTS or newer (2021.3, 2022.3, Unity 6).
- The runtime depends only on Burst and Mathematics. The sample uses the Input System and URP when
  installed, and falls back to the legacy Input Manager and the built-in pipeline when not.

INSTALLATION
Package Manager > Add package from git URL:
  https://github.com/Nekuzaky/kinema.git?path=/Packages/com.nekuzaky.kinema

QUICK START
1. Open Kinema > Motion Matching > Window (Ctrl+Shift+M).
2. Create a config, assign a rig and locomotion clips.
3. Bake the database (Bake tab).
4. Add a MotionMatchingController to your character and assign the database.

See the "Locomotion Demo" sample for a wired scene and one-click setup.

LICENSE
MIT. See LICENSE.md.

Full documentation: Documentation~/index.md
