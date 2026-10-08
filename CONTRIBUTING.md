# Contributing

## Scope check first

Read [TODO.md](TODO.md) before starting anything nontrivial - it is the honest gap list and priority
order. If what you want to do isn't there, open an issue describing it before writing code; this
avoids duplicate or conflicting work, and some areas (matching cost function, feature schema layout)
have deliberate tradeoffs that aren't obvious from the code alone.

## Setup

1. Unity 6000.3+ to open this repository's project. The package itself supports 2021.3 LTS and later
   (`Packages/com.nekuzaky.kinema/package.json`), so a change must not use an API newer than that
   without going through `UnityCompat` - CI runs the EditMode suite on 2021.3, 2022.3 and 6000.0.
2. Open the repository root as a Unity project - it already contains the package via a local
   reference, so no separate install step is needed to work on the package itself.
3. `Tools > Kinema > Motion Matching Window` (Ctrl+Shift+M) to explore the tool; `Tools > Kinema >
   Demo Scene` to get a runnable character (drop a Humanoid FBX in the sample's Character folder
   first if you have no mocap pack installed).

## Before opening a PR

- **Run the EditMode tests.** `Window > General > Test Runner` (EditMode tab) in the Editor, or
  headless:
  ```
  Unity -batchmode -projectPath . -runTests -testPlatform EditMode -testResults results.xml
  ```
  New logic in `Runtime/Core`, `Runtime/Data` or `Runtime/Runtime` that can be expressed as a pure
  function (no `MonoBehaviour` lifecycle, no scene) should get an EditMode test alongside it - see
  `Tests/Editor` for the established pattern: one file per component, built against synthetic data
  via `TestDatabaseFactory` or direct construction, no baked rig required.
- **Say what you could and couldn't verify.** Several areas (mirroring, transition feel, IK under
  load, anything judged by how it looks on screen) have no automated coverage yet - see TODO.md's
  "Runtime behaviour" section. If your change touches one of them, say in the PR description what
  you tested by eye in Play Mode and what you didn't get to. Silent gaps are worse than named ones.
- **Keep the runtime hot path free of GC allocation.** `MotionMatchingController.Tick`, the matcher's
  search job and anything reached from `Update` should not allocate per frame - the Burst job and
  flat-array database layout exist specifically to keep this true.
- Match the existing doc-comment density: public API gets an XML `<summary>` explaining *why*, not
  just what; private implementation detail generally doesn't need one unless the reasoning is
  non-obvious (a workaround, an invariant, a subtle cost tradeoff).

## Where things live

See the README's [Repository layout](README.md#repository-layout) and the exploration notes below
for the package's internal structure:

- `Runtime/Core`, `Runtime/Data` - matching math and baked data structures. Unity-independent where
  possible; this is where most unit-testable logic belongs.
- `Runtime/Runtime` - `MotionMatchingController` and friends: the `MonoBehaviour` layer that owns the
  `PlayableGraph` and drives the above every frame.
- `Runtime/Playback`, `Runtime/IK` - transition smoothing and foot/ground IK.
- `Editor/Baking`, `Editor/Window` - the offline bake pipeline and the editor tooling (IMGUI; no
  automated coverage is possible for interactive `OnGUI` code, so changes here lean more heavily on
  the "say what you verified by eye" rule above).
- `Tests/Editor` - EditMode tests, one file per component under test.

## Reporting gaps vs. fixing them

If you find a gap that isn't in TODO.md and don't have time to fix it, add it there rather than
leaving it undocumented - the file's value comes from being a complete, honest list, not just a
todo for the maintainer.
