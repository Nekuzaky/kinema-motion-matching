# Kinema Motion Matching

[![Unity 2021.3 LTS+](https://img.shields.io/badge/Unity-2021.3%20LTS%20%E2%86%92%206000.x-black)](#unity-version-support)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](Packages/com.nekuzaky.kinema/LICENSE.md)
[![UPM](https://img.shields.io/badge/UPM-com.nekuzaky.kinema-informational)](Packages/com.nekuzaky.kinema)
[![Sponsor](https://img.shields.io/badge/%E2%9D%A4-Sponsor-ff69b4?logo=githubsponsors&logoColor=white)](https://github.com/sponsors/Nekuzaky)

[![X (Twitter)](https://img.shields.io/badge/X-@nekuzaky-000000?logo=x)](https://twitter.com/nekuzaky)
[![Bluesky](https://img.shields.io/badge/Bluesky-@nekuzaky-0285FF?logo=bluesky&logoColor=white)](https://bsky.app/profile/nekuzaky.bsky.social)
[![TikTok](https://img.shields.io/badge/TikTok-@nekuzaky-000000?logo=tiktok&logoColor=white)](https://www.tiktok.com/@nekuzaky)
[![Website](https://img.shields.io/badge/Website-nekuzaky.com-000000?logo=googlechrome&logoColor=white)](https://nekuzaky.com/)

Data-driven motion matching locomotion for Unity. An offline bake pipeline turns `AnimationClip`s
into a normalized feature database; a Burst-compiled runtime queries it every few frames and blends
to the best pose through a `PlayableGraph`; an integrated editor window drives, records and debugs
the whole loop.

The toolkit is packaged as an installable UPM package (`com.nekuzaky.kinema`) and this repository is
also a Unity project you can open to try the demo directly.

## Support this project

Kinema is free and open source. If it saves you time on your project, consider [becoming a sponsor](https://github.com/sponsors/Nekuzaky) or checking out [my website](https://nekuzaky.com/). Sponsorships directly fund continued development, bug fixes, and new features. See [FUNDING.yml](.github/FUNDING.yml) for all supported ways to contribute.

Follow development and updates on [X/Twitter](https://twitter.com/nekuzaky), [Bluesky](https://bsky.app/profile/nekuzaky.bsky.social), and [TikTok](https://www.tiktok.com/@nekuzaky).

## Table of contents

- [Features](#features)
- [Installation](#installation)
- [Quick start](#quick-start)
- [How it works](#how-it-works)
- [Working with AI coding assistants](#working-with-ai-coding-assistants)
- [Repository layout](#repository-layout)
- [Testing](#testing)
- [Demo assets](#demo-assets)
- [Contact](#contact)
- [License](#license)

## Features

**Matching core**
- Weighted nearest-neighbour search over baked feature vectors: past+future trajectory, per-bone
pose and velocity, root velocity, gait phase - Burst-compiled parallel job with a KD-tree option
for very large databases.
- Live pose query: the query's pose half is sampled off the rendered skeleton (after IK), not copied
from a database row, so the cost function judges what is actually on screen.
- Foot-phase cost term keeps candidates on-cycle, so a jump cannot cut a stride mid-step.
- Critically damped spring trajectory prediction (selectable), deviation-triggered search on top of
the timed interval, idle-duplicate pruning at bake time.
- Inertialization transitions (Burst animation job) or two-slot crossfades, foot-lock + ground
adaptation IK, stride warping, semantic tags (64-bit masks, filtered in the search job), motion
events with root warping, mirrored variants, calibration profiles, multi-database switching,
Mecanim interop.
- `MotionMatchingLOD`: degrades a character's search cadence with distance from camera (piecewise-
linear multiplier over configurable distance tiers) for crowds of matched characters. Only touches
search interval, so playback and IK are unaffected.
- Timeline integration (optional assembly, needs `com.unity.timeline`): a `MotionMatchingTrack`
fades matching in for the duration of its clips and restores the prior state after - cutscene to
gameplay handoff without scripting.
- Cross-character search batching: drop a `MotionMatchingSearchBatch` in the scene and registered
controllers schedule their searches in Update and complete them together in LateUpdate, so
simultaneous searches overlap on Burst worker threads (~1.7x faster at 8-128 characters in local
measurements; see the benchmark). Built on `MotionMatcher.ScheduleSearch`/`CompleteSearch`.
- `GaitClassifier`: proposes idle/walk/run/turn tag ranges from the baked motion itself
(`Tools > Kinema > Log Auto-Tag Suggestions`), no naming conventions involved.
- Blend spaces (MxM-style): place source clips on a 2D plane and bake a grid of blended clips from
them (Bake tab), filling the gaps between the motions you actually captured. The blend runs in pose
space and produces real AnimationClips, so a grid point is playable, not just matchable.
- Manual ticking: set `TickMode.Manual` and call `Step(dt)` to own the clock - fixed-step or
server-authoritative simulation, and deterministic tests. Automatic (self-ticking) is the default.

**AI**
- Two layers: a brain decides *what to do* (high-level goals), an `AICommandProvider` turns that into
the same locomotion intent player input produces - so an NPC drives the identical matching stack,
and the brain is swappable without touching either.
- Agents read the world: three feelers bend the desired velocity around walls, steering toward the
roomier side and slowing into the turn. Walkable slopes, anything under the agent's own passable
height, and a Follow target all read as clear - so an agent climbs ramps, vaults what it can vault
instead of circling it, and closes on the player rather than orbiting them. The steer is smoothed,
because the search reads this velocity as intent and a jittery one makes it flip between clips.
- Agents vary their gait: the scripted brain asks for a speed that tracks the distance left, so it
runs the long leg and walks the last few metres - pulling the walk, run and start/stop clips out of
the same database the player uses, instead of jogging at one fixed speed forever.
- `ScriptedAIBrain` (deterministic Wander / Patrol / FollowPlayer) is the default. `LLMAIBrain`
(sample) asks an OpenAI-compatible endpoint what the character should do next and maps the JSON
reply to a goal - endpoint, model, key and persona are all serialized, consulted on a timer or when
a goal completes (a few calls a minute per agent, never per frame), async, with a wander fallback
on no key or any error. Networking stays out of the runtime.

**Editor window** (`Tools > Kinema > Motion Matching Window`, Ctrl+Shift+M)
- Overview / Database / Bake / Tags / Director / AI / Debug / Analysis / Settings.
- AI tab: every agent's brain, goal, status and reason, with manual commands (per agent or all) that
nudge it while its brain keeps running underneath.
- Debug tab: scrub recorded matching decisions, visually rewind any of them onto the live
character, and pin one to diff against another (per-group cost deltas, what moved most, intent
shift).
- Director tab: play any baked clip on the live character like a custom Animator (scrubbable
timeline with per-foot contact lanes), record intent + pose, spawn ghost NPCs that replay a
recording through their own matching, bake a performance to a real `AnimationClip`, swap the
character's rig in one click.
- `Tools > Kinema > Benchmark Search`: measures the real search cost (mean/median/p99, order-checked
against Burst's async compile) and converts it into characters-per-frame at a given search rate.

**Learned Motion Matching** (in progress)
- Step 1, shipping: `Tools > Kinema > Learned MM > Export Training Dataset` writes a baked database as
a training set - float32 feature matrix, mean/std, per-frame clip/time, gait phase - as flat
binaries with a self-describing manifest and a numpy loader. Clip boundaries are exported so a
stepper never trains across a cut.
- Step 2, shipping: the PyTorch pipeline (`Documentation~/Training/`) and `ILearnedMotionModel`, the
backend-agnostic runtime seam (Project / Step / Decompress).
- Step 3, not built: a Unity Sentis backend running the exported ONNX behind that interface. The
package takes no ML dependency until then.

**Demo scenes** (`Tools > Kinema > Demo Scene`, `Tools > Kinema > Scenes > Parkour | Sandbox`)
- One generator resolves its own source - an installed mocap pack, otherwise a dropped-in FBX (using
its clips or generating a procedural set) - bakes it, and builds the scene: full keyboard + gamepad
input, camera orbit, URP post-processing, vault and free-jump events.
- **Test**: a terrain that provokes every subsystem (ramps, steps, ledge, a long lane).
- **Parkour**: a vault-wall run, gap-jump gauntlet and ascending platforms as a circuit, with an AI
follower chasing and auto-vaulting.
- **Sandbox**: an open arena with six AI wanderers - the matcher running on seven characters at once.

## Installation

Unity Package Manager, "Add package from git URL":

```
https://github.com/Nekuzaky/kinema_motion_matching.git?path=/Packages/com.nekuzaky.kinema
```

Requires Unity 2021.3 LTS or newer. The runtime depends on Burst and Mathematics, both resolved
automatically by the Package Manager. The sample uses the Input System and URP when the project has
them, and falls back to the legacy Input Manager and the built-in pipeline when it does not.

### Unity version support

| Unity | Status |
| --- | --- |
| 6000.3 | Supported - the version this repository's project is developed in |
| 6000.0 LTS | Supported - EditMode suite in CI |
| 2022.3 LTS | Supported - EditMode suite in CI |
| 2021.3 LTS | Supported (minimum) - EditMode suite in CI |
| 2020.3 and older | Not supported: the code needs C# 9 and .NET Standard 2.1, and the Burst release it depends on does not target them |

The few APIs Unity renamed across these versions (the `FindObjectsByType` family) go through one
`UnityCompat` class, so supporting an LTS is one `#if` per API rather than one per call site. The
runtime, editor and sample also compile cleanly against the Unity 2021 API with and without the Input
System. The CI compatibility jobs need the same `UNITY_LICENSE` secret as the other test jobs.

## Quick start

1. Open the tool: `Tools > Kinema > Motion Matching Window` (Ctrl+Shift+M).
2. Create a config and assign a rig plus locomotion clips.
3. Bake the database (Bake tab).
4. Add a `MotionMatchingController` to your character and assign the database.

Or import the "Locomotion Demo" sample and run `Tools > Kinema > Demo Scene` for a fully wired scene
- drop a Humanoid FBX in the sample's Character folder first if you have no mocap pack installed.

## How it works

Each frame is baked into one normalized feature vector:

```
[ TrajectoryPosition 2*T | TrajectoryDirection 2*T | BonePosition 3*B | BoneVelocity 3*B | RootVelocity 2 ]
```

plus a parallel gait-phase channel used as an extra cost term. Matching is a weighted squared
distance over these vectors, evaluated by the query's predicted future trajectory and its live pose
sampled off the rendered skeleton. The controller only cuts to a new frame when it clearly beats
continuing the current clip, then crossfades or inertializes. See
[the documentation](Packages/com.nekuzaky.kinema/Documentation~/index.md) for detail.

## Working with AI coding assistants

Pairing an AI coding assistant such as Claude Code with Kinema can save you significant tokens and
time. The hard parts of character locomotion - animation blending, IK, and transitions - would
otherwise take weeks to write by hand. Kinema already ships them, so the assistant's job shrinks to
wiring: add a `MotionMatchingController`, hook up an `AICommandProvider`, feed it an intent
(direction and speed), and Kinema returns the mocap pose that matches. Instead of reimplementing a
locomotion system, the assistant spends its budget on high-level integration.

Expect the usual cost of any third-party package: some integration friction - rig/bone naming
mismatches, a database that still needs assigning, root motion that needs applying. That is the
price of any dependency, not a shortcoming of Kinema itself. The payoff is real: going from
placeholder primitives to animated humanoids (walking, turning, steering) in a fraction of the time
it would take to build the locomotion stack from scratch.

## Repository layout

```
Packages/com.nekuzaky.kinema/ The package (Runtime, Editor, Samples~, docs, license)
Assets/ Unity project shell and the imported demo
```

## Testing

`Packages/com.nekuzaky.kinema/Tests/Editor` holds EditMode tests covering the feature schema layout
and pose cost modes, the Timeline mixer, blend-space math and the blend-space bake, mirroring,
snapshot diffing, gait classification and auto-tag apply, search LOD, the batched search path,
`CharacterSpace` round-trips and altitude invariance, the trajectory history ring buffer, the
database's normalization/accessors, idle-duplicate pruning, config-to-database identity, and
end-to-end matcher correctness (nearest-neighbour, tag filtering, ignore ranges, phase cost) against
synthetic databases built directly through `SetBakedData` - no rig or bake pipeline required. Run
them from Unity's Test Runner window (`Window > General > Test Runner`, EditMode tab) or headless:

```
Unity -batchmode -projectPath . -runTests -testPlatform EditMode -testResults results.xml
```

`Tests/Runtime` holds PlayMode tests that drive a real controller (live PlayableGraph, real searches)
on a synthetic database, plus AI agents steering against real colliders - run with
`-testPlatform PlayMode` (drop `-nographics`). They own the clock through `TickMode.Manual` +
`Step(dt)` rather than yielding frames, so each test's timeline is exactly the steps it takes: plain
`[Test]`, no coroutines, no frame-pacing dependency. That is load-bearing, not tidiness: the AI's
steer is smoothed against dt, and a headless frame lasts microseconds, so yielded frames made every
agent read as "not steering" whatever it had decided.
`Assets/StandaloneSmokeTest/` builds a headless Win64 player that ticks a controller for 60 real
frames and prints a `[KinemaSmoke]` verdict.

`Tools > Kinema > Benchmark Search` measures search performance directly (headless-runnable),
including N characters searching concurrently; see [TODO.md](TODO.md) for the gaps that remain
(runtime *feel* is still judged by eye, not automation).

## Demo assets

The demo character is provided by [Adobe Mixamo](https://www.mixamo.com/) and remains subject to
Adobe's Mixamo license. It is included only to make the demo runnable and is not covered by this
project's license. A richer demo path exists for the (separately licensed, not redistributed) Opsive
OmniAnimation mocap pack - see [the documentation](Packages/com.nekuzaky.kinema/Documentation~/index.md).

## Contact

Found a bug, have a question, or just want to get in touch? Reach out via [nekuzaky.com/contact](https://nekuzaky.com/contact).

## License

MIT. See [LICENSE](Packages/com.nekuzaky.kinema/LICENSE.md).

Author: [Nekuzaky](https://github.com/Nekuzaky).
