# Changelog

All notable changes to this package are documented here.
The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.45.0] - 2026-10-08

Production-grade runtime: the techniques AAA motion matching ships with, built the way a
production needs them - measurable, allocation-free, responsive without sliding.

### Added
- **Simulation/animation sync** (Holden, *Code vs Data Driven Displacement*). The controller runs a
  simulated character toward the desired velocity on the same critically damped spring the
  trajectory prediction uses - the character is pulled along exactly the path it searched for - and
  `ProcessRootMotion(ref deltaPosition, ref deltaRotation)` nudges root motion toward it by at most
  `Sync Max Ratio` of the animation's own movement. A standing animation is never dragged; a moving
  one slides by at most that fraction. Leashed to the character so a wall cannot pull it on for ever.
  On by default for motors that call `ProcessRootMotion` (the sample `CharacterMotor` does); an
  Animator applying root motion by itself is untouched. Maths in `LocomotionSimulation`, unit tested
  (frame-rate independence, agreement with the predicted trajectory, the ratio bound).
- Unity Profiler markers: `Kinema.Tick`, `Kinema.Search`, `Kinema.EvaluateGraph`,
  `Kinema.FootLockIK`, `Kinema.GroundAdaptationIK`, `Kinema.CompleteBatchedSearches`.
- `AllocationPlayModeTests`: a ticking character - searches, jumps and transitions included -
  allocates nothing on the managed heap.

### Fixed
- **Per-frame allocations removed** (each one a GC stall waiting to happen, multiplied by the
  number of characters): `AnimationClip.events` was read every frame on every character (the getter
  builds a fresh array each call) - now cached per clip at initialization; every search allocated its
  group-cost array - now owned by the matcher (`MotionMatchResult.GroupCosts` is valid until that
  matcher's next search; copy it to keep it); `ScriptedAIBrain` formatted status strings every frame
  for a label only the editor reads - now built when read; log messages were formatted with logging
  off. Weight changes no longer allocate, and bone lookup walks the hierarchy once instead of once per
  bone.
- **Mirrored frames played unmirrored root motion** for motors using `ProcessRootMotion`: the step
  is now reflected across the character's own side.
- **AI agents flickered between idle and walk on arrival** - the eased speed ran down to nothing the
  data could play - and an agent parked just outside its arrive radius stood there for good. Speed
  now floors at `Min Move Speed`, and arrival has hysteresis (`Rearm Distance`).
- **AI avoidance wobbled into walls** when both feelers read alike: the avoidance side is now kept
  while the way stays blocked, and the slow-down is smoothed like the steer.
- **Crossfade interruption popped** by up to the whole outgoing pose; the new clip now takes the
  lighter of the two slots.
- Demo camera: mouse orbit no longer depends on frame rate (a pixel delta is an angle, a stick is a
  rate), and no longer saturates on a brisk drag.

### Notes
- Verified outside Unity: everything compiles against the Unity 2021 API; the new EditMode tests
  pass against a pure-managed stand-in for Unity's math. The allocation test and the feel of the sync
  have not been run in Unity here.

## [1.44.0] - 2026-10-08

Smoothness, and every LTS from 2021.3. Two audits of the runtime against "it still isn't fluid"
found the stutter was not one bug but several, each firing several times a second: a frozen frame
at every transition, then a lurch at double speed; one frame of zero root motion at every jump; a
run loop baked as if it were stopping, so the matcher kept sending a running character back to the
start of its stride. Each fix below names what it looked like on screen.

### Fixed
- **Inertialization froze the pose for a frame, then kicked it at double speed.** On the frame of a
  switch the offset was captured as "previous output minus new input", so every bone repeated its
  previous pose; and the offset's initial velocity was the outgoing velocity alone, not outgoing
  minus incoming, so on the next frames the limbs moved at roughly the sum of the two motions. Now
  the switch frame extrapolates the outgoing pose along its own velocity, the next frame measures
  the incoming clip's velocity from two real samples, and only then is the offset captured -
  position and velocity are continuous across the seam. A transition requested while time is not
  moving (a paused game) used to divide by a 1e-5 s step: in the new test it moved a bone 44 m in one
  frame; it now waits for a real step. The maths moved to `InertializationMath` and is covered by
  EditMode tests that fail on the old code.
- **Every synchronous jump dropped one frame of root motion.** The search ran after the clocks had
  advanced, so the new clip was created at the matched time with no root-motion delta and stood
  still for that evaluation. The search now runs first and the new clip advances with the frame.
- **Looping clips were baked as if they stopped at their last frame.** Future and past trajectory
  points were clamped to the clip's own frames, so in a 0.7 s run loop every frame in its second
  half "predicted" a stop, and the matcher jumped back to the start of the loop every ~0.25 s - the
  legs never completed a stride. Looping clips are now sampled as the cycle they play as: points
  past the end come from the next cycle (carried by one cycle's travel and turn), velocities at the
  seam are measured across it. **Rebake your databases** to get this.
- **Jumps inside the same clip skipped the clip-change brake**: half a stride back in the same loop
  needed to be 2% better instead of 27%. Every cut now pays it. The continuity window is also
  measured around the loop (0.02 s into a 0.7 s loop is 0.04 s after 0.68 s) and respects mirroring.
- **"Keep playing" was judged on the wrong frame.** Continuing a one-shot clip was costed one search
  interval ahead, against a query built from the pose on screen now - so it paid a pose error no
  candidate paid. It is now judged on the frame playing, with the runway measured at the rate the
  clip actually plays (stride warping included).
- **A character could freeze on the last frame of a clip.** The "already where we are" test ran
  before the "this clip is ending" test, and nothing kept the search from picking the end of a
  one-shot - whose best match is usually that same end. The ending test now comes first, and the
  search no longer selects the last 0.25 s of non-looping clips (`Clip End Exclusion`; playback still
  reaches them).
- **After a vault, matching could stay stuck on the event's last frame**: continuing was costed on
  the stale pre-event frame. With an event clip in the slot, continuing is no longer an option.
- **A crouch had to beat the standing clip by the full brake.** Continuing a frame the tags now
  forbid was still allowed to compete; it no longer is.
- **Foot lock popped up to 0.25 m.** A lock broken by distance re-locked on the next frame, at the
  animated foot, while the release was still blending. A broken lock now stays off until the foot
  lifts, and a contact that starts during a release locks where the foot is drawn.
- **Foot IK pinned a pre-vault foot to the ground in mid-air**: during an event `CurrentContacts`
  reported the contacts of the last locomotion frame. It now reports none.
- **The pelvis bobbed once per step on slopes and stairs**: with no foot planted (every run flight
  phase) its correction snapped back to zero. It now holds through a stride-length gap.
- **Measured velocity was divided by the wrong frame's dt**, so frame-time jitter alone read as
  +-0.6 m/s at a run and tripped deviation searches.
- **A frame hitch was repaid as a search on every frame**; the search debt is now capped at half an
  interval. A zero-length tick no longer searches at all.
- **A vault's position warp was undone by a CharacterController** (it moves from where PhysX last
  saw it); transforms are synced after the warp when auto-sync is off.
- **A batched search finishing after an event started cut the event clip away** under a warp that
  kept running; a search whose question is no longer being asked is now discarded.
- Manual tick mode keys the trajectory history by its own clock, so several `Step` calls in one
  frame record correctly spaced samples.
- `BlendFraction` counted crossfades only and read 0% in the default inertialization mode.

### Changed
- **Unity 2021.3 LTS and later** (was 6000.3). The renamed `FindObjectsByType` family goes through a
  new `UnityCompat` class; nothing else in the runtime or editor needed changing. CI gains an
  EditMode matrix on 2021.3, 2022.3 and 6000.0 (package mode).
- The sample no longer requires the Input System or URP: `DemoInput` reads the Input System when it
  is installed and the legacy Input Manager otherwise, and the URP post-processing volume is only
  built when URP is present.
- Demo camera: the aim follows a focus point that eases in height, so stair steps no longer jerk the
  view. Stick input is rescaled past the deadzone instead of jumping to 20% of top speed.
- The committed demo scene carried 0.12 / 0.1 for the continuity window / clip-change brake from
  before v1.30; it now has the current defaults.

### Notes
- Verified outside Unity only: the runtime, editor and sample compile against the Unity 2021 API
  (with and without the Input System), and the new EditMode tests for the inertializer and the loop
  bake pass against a pure-managed stand-in for Unity's math types. How the result reads on screen
  still needs a play session - and a rebake, for the loop fix.

## [1.43.0] - 2026-07-16

Asset Store readiness, continued: say what is not ours, and prove the runtime runs.

### Added
- `THIRD-PARTY-NOTICES.md`: exactly what a buyer receives and what they do not. The package ships
  only first-party code and Unity's own dependencies - no mocap, no rig, no third-party runtime. The
  Mixamo character used in development and the optional Opsive integration are named as *not shipped*,
  with the reason. This is the first thing an Asset Store reviewer looks for.
- CI now runs the **PlayMode** suite alongside EditMode. EditMode proves the maths; PlayMode drives a
  real controller - live PlayableGraph, real searches, real transitions - and is the only thing that
  proves the runtime loop actually runs. Four PlayMode test files existed and were never run by CI. A
  green EditMode badge over untested runtime code is not a reassurance worth selling.

### Notes
- What is still not code, and still required before submission: a demo character under a
  redistributable license, a visual QA pass on a clean project, and the suites confirmed green once
  the `UNITY_LICENSE` secret is set (CI cannot run without it).

## [1.42.0] - 2026-07-16

Asset Store hygiene: nothing third-party ships or is referenced by default.

### Changed
- The Opsive OmniAnimation integration (`OpsivePackSetup`, and its branch in the demo generator) is
  gated behind a `KINEMA_OPSIVE` scripting define and **off by default**. The published package now
  carries no reference to a third-party product a buyer may not own; an owner of the pack turns it on
  in Project Settings > Player > Scripting Define Symbols. The pack is never redistributed - the code
  only bakes a copy the owner already has.
- With Opsive off, the demo resolves from a Humanoid FBX dropped in the sample's Character folder,
  baking its clips or generating a locomotion set from a bare skin. The "install a mocap pack"
  wording is gone from the default path.

### Removed
- A stray `__pycache__/*.pyc` that was tracked under `Documentation~/Training`, and a gitignore rule
  so it stays gone.

### Notes
- Still required before a store submission, and none of it is code: a demo character the license
  covers (the Mixamo rig used in development is not redistributable), a visual QA pass on a clean
  project, and the test suite run green in CI. See the Asset Store readiness checklist in the commit.
## [1.41.0] - 2026-07-16

Every item here comes from someone integrating the package into an FPS and paying for it in debugging
cycles. Each was a *silent* failure - a warning that scrolls past, or a quiet demotion to a worse
mode - and each cost a full diagnostic round trip to find. Silence is the expensive part, not the
bug.

### Fixed
- **No database, or no Animator, is now an error and disables the component.** It was a warning and a
  `return`: the component sat there looking enabled and alive while doing nothing forever. An
  unticked checkbox is a state you can still see an hour later. The message also names the trap that
  produced it - the Config and the Database are different assets and the bake names them alike.
- **A rig missing the schema's bones is now an error, and reports all of them at once.** It reported
  the first and returned, so a wrong rig was fixed one bone name per play session. It is an error
  rather than a warning because the fallback is a *silent demotion*: the pose query starts copying the
  current database frame, which still animates and still looks alive while matching against a pose
  the character is not in.
- **`AICommandProvider` now has a `Player Target` field, and errors when it has nothing to target.**
  It searched for a `MotionMatchingController` without an `AICommandProvider` - which finds the player
  in this package's demo and nothing at all in a game whose player is a capsule and a camera. The
  failure was invisible: agents stood still holding a Follow goal they could never act on, which reads
  as the AI being broken rather than the AI having nothing to follow.
- **`AICommandProvider` re-resolves on its first tick if `Awake` never ran.** Awake does not run while
  a GameObject is inactive, so a pooled or spawned-disabled agent woke with no brain and no target and
  no way to say so.

### Added
- `Documentation~/index.md`: **Integrating it into a game that is not the demo**. The demo scene is
  not a neutral example - it is one where the player is itself a matched character, and several
  defaults assume that. Covers the above, plus the one that has no error to give: **the controller
  poses a skeleton and moves nothing**. There is no motor in the runtime by design; something has to
  consume the root motion `OnAnimatorMove` delivers, or the character animates in place and nothing
  about it looks wrong.

## [1.40.0] - 2026-07-16

### Added
- Blend spaces are bakeable and playable: `BlendSpaceBaker` (Bake tab → "Bake Blend Space Clips")
  samples the source clips on the rig, blends the resulting POSES by each grid point's Gradient Band
  weights, and writes one real AnimationClip per grid point through `PoseClipBaker`. Add the baked
  clips to the config's clip list and rebake to make the grid matchable - so a blend space fills the
  gaps between the motions you actually captured, and the grid stays reviewable like any other clip.
  Blending in pose space is the point: a feature-blended grid point was matchable but unplayable,
  since playback replays the matched frame's real clip.
- `BlendSpaceMath.BlendRotations` / `BlendPositions`: weighted pose blending. Rotations are
  sign-aligned to the heaviest sample before accumulating - q and -q are the same rotation but
  cancel when summed, which collapses a blend of two visually close poses into nonsense.

### Notes
- Baked grid clips inherit `PoseClipBaker`'s limitation: transform-curve (Generic) clips, which a
  Humanoid Animator ignores in favour of muscle data. The baker warns rather than letting you find
  out on an empty playback.
133/133 EditMode tests (13 new: pose-blend maths, and an end-to-end bake asserting a 3x1 grid
between a 0-degree and a 90-degree source reproduces each end and gives 45 degrees in the middle).

## [1.39.0] - 2026-07-16

Backfilled: 1.33 through 1.39 shipped without entries here. Reconstructed from the tagged commits,
which is why these are shorter than the surrounding releases - see the git history for the full
reasoning each one carries.

### Changed
- One log channel (`KinemaLog`), off by default, reporting transitions only.

## [1.38.0] - 2026-07-16

### Added
- `Tools > Kinema > Copy Diagnostics`: the whole scene's matching setup as pasteable text, from the
  window header on every tab - the moment worth capturing is never the moment to go hunting through
  a menu.

## [1.37.0] - 2026-07-16

### Added
- `MirrorPoseTests`: pins the mirror reflection before a database is doubled with it. Mirroring
  fails quietly, so the reflection is worth pinning on its own.

## [1.36.0] - 2026-07-16

### Added
- Motion-quality measures: a way to put a number on what "robotic" actually is.

## [1.35.0] - 2026-07-16

### Fixed
- The body never turned. The character's only source of rotation was the clips' root motion, and a
  strafe set (like most locomotion captures) has rotation only in its idle turns - so facing a new
  direction meant stopping, playing an idle turn, and setting off again.

### Added
- A rig skeleton toggle.

## [1.34.0] - 2026-07-16

### Fixed
- Ground IK crouched the character every step. `GroundAdaptationIK` never checked which feet were
  planted, so it treated every stride's swing foot - sitting ~0.25 m above the floor - as a planted
  foot that had sunk, and dragged the pelvis down by that much.

## [1.33.0] - 2026-07-16

### Fixed
- "The AI does random things" was the AI crouch-walking and running on jump clips: the demo gave
  `StanceTagController` to the player only, so an AI searched the entire database - crouch and jump
  clips included - and the cost function did exactly its job over a candidate set nobody had
  narrowed.

## [1.32.0] - 2026-07-16

A pose cost that measures the transition instead of guessing at it. From Holden, *Inertialization
Transition Cost* (2022) - see [Research.md](Documentation~/Research.md) for the full reading of the
published sources against this package.

### Added
- **`PoseCostMode.InertializationCost`** on `FeatureSchema`: one composite per bone,
  `2*pos/y + vel/y^2`, replacing the separate bone-position and bone-velocity blocks. It is the
  displacement an inertialized transition onto that bone would actually cause.

  The naive layout - which every motion matching talk shows, and which this package shipped until
  now - has two flaws, and both applied here verbatim:
  - It adds positions to velocities, so the weight balancing them is arbitrary. Nothing derives it:
    the demo carried `BoneVelocity: 0.6` because it was tuned until it looked right, and this package
    ships a weight tuner to find that number by experiment.
  - It evaluates the two independently, so it cannot see that a position offset is *harmless* when
    the velocity offset is set to absorb it. It rejects transitions that would have been smooth.

  Three consequences, in the order they matter: the cost measures what the transition will look like
  rather than a proxy for it; there is no velocity weight left to tune, since it falls out of the
  half-life; and the vector loses `3*B` dimensions - **44 -> 35** on the demo bake (T=6, B=3) - off
  every distance evaluation and off the Learned Motion Matching training set.

  `y` comes from `InertializationHalflife` through Holden's own conversion
  (`damping = 4*ln2/halflife`, `y = damping/2`), because the number only means anything if it is the
  one the spring the transition actually runs would use.

- `FeatureSchema.BonePoseValue` is the single definition of that formula, called by both the baker
  and the live query. They have to produce bit-identical numbers: a formula that drifted between them
  would not look like a bug, it would look like the character simply matching badly.

- `MotionMatchingDatabase.GetBonePoseValues`, replacing `GetBonePositions` (now `[Obsolete]`, still
  forwarding). Under the composite the old name was a lie, and the composite cannot be inverted back
  into a position - one number cannot be solved for the two it was made from.

### Notes
- **Opt-in.** `Naive` stays the default, and a config serialized without the field deserializes to
  it, so no existing bake or scene changes meaning. Switching modes requires a rebake.
- `IsLayoutCompatibleWith` refuses to mix modes, or half-lives within the composite mode. Two schemas
  can agree on every count and still write different numbers into the same slots - the one
  incompatibility that would otherwise pass silently.
- Not measured on real data yet: what it does to the jump rate or to foot slide.

## [1.31.0] - 2026-07-16

### Added
- `ObstacleSensor`: one sensor per character classifying what is ahead - Step, Vault, Gap, Blocked -
  so a character picks an action instead of guessing, and so two consumers act on the same picture of
  the world. Before this each cast its own rays every frame and none of them agreed: the vault
  trigger one, the AI provider four.

  Five casts per sense at 10 Hz (the matcher's own search cadence), and none while the character is
  not moving, since a still character's surroundings are not changing - which is most of the saving
  on a crowd, where most agents are idle at any moment. ~50 casts/second/character against the
  ~60-240 the per-frame probes it replaces were doing.

### Fixed
- The vault path tested only the obstacle's height. It now also has:
  - **Thickness.** A block deep enough to stand on is not something you vault, and the event warped
    the character into the middle of it. Measured by casting back through the obstacle from beyond.
  - **Landing.** Nothing checked there was floor on the far side, so you could vault a wall into a
    pit.
- The old probe had no layer mask, and a trigger volume in front of a wall made it report "not
  vaultable" instead of being ignored.

### Notes
- No Climb kind and no Cover kind. Both are trivial to sense - a tall wall is the easiest thing in
  there to recognise - and neither is included, because motion matching plays the data it has and a
  locomotion capture contains no climb or cover motion. A sensor reporting an affordance the
  character cannot perform is a lie with extra steps. They slot in as new kinds when the clips exist.

## [1.30.0] - 2026-07-16

The clip-change brake never worked. This is the bug behind every "it moves on its own", "not fluid"
and "robotic" report so far.

### Fixed
- **`_clipChangeCost` was an absolute constant in a cost space that is neither normalized nor
  bounded.** It is capped at 1.0 by its own `Range`, while real costs on the demo run 5 (a satisfied
  query) to 200 (one asking for a speed the bake barely holds). At 0.25 it was a 4% brake at best and
  a 0.1% brake exactly when flicker was worst - its tooltip promised "raise to reduce clip flicker",
  which it could not do at any value. It is now a **fraction of what continuing the current clip
  costs**, so it means the same thing at every scale.

  Measured on the demo before the fix: **93% of searches jumped** - against a stats label whose own
  comment reads "a calm character sits near 0". Since `CharacterMotor` moves the body by the clip's
  root motion and nothing else, that churn walked an idle character off at **1.2 m/s with no input
  touched** (`speed 1.2 / 0.0 m/s` on the label), with foot slide at 0.68-1.47 m/s. It also explains
  the long-standing report that ghosts looked fluid while the player did not: a ghost replays a
  recorded frame sequence, so it never searches, so it cannot thrash.

  Default raised 0.1 -> 0.25 now that the value can brake anything. Scenes carrying a serialized
  0.25 get the fix without rebaking or regenerating.
- `ScriptedAIBrain._runScale` defaulted to 1.0 - the provider's top speed, which is itself the edge
  of what the bake covers. A player only touches that edge in bursts; an agent chasing a far goal sat
  there permanently and starved the search the whole time. Measured: the follower's cost hit **202
  (trajectory 159)** while sprinting and fell to **8** the moment it closed in and slowed, and it was
  picking crouch and jump clips to run with. Now 0.8.
- AI agents ran off ledges. The feelers are horizontal rays, so a hole in the floor has nothing to
  hit - obstacle avoidance reported a clear path into a gap *because* the gap was empty. The ground
  ahead is now probed too (`_stopAtLedges`, `_groundCheckAhead`, `_ledgeDrop`), and a missing floor
  reads as a wall at the same distance so the existing steering rounds it. Stopping dead instead
  would strand the agent: a brain only re-picks on reaching a goal, so one frozen at an edge stays
  frozen. A hard stop remains as a backstop, since smoothed steering can fail to come round in time.

  Note: this stops an agent falling in; it does not let it cross. Jumping a gap is a capability the
  AI does not have - the jump events are player-triggered.

## [1.29.0] - 2026-07-16

### Fixed
- The AI demo scenes ran every character's search independently at full rate. `Sandbox` puts seven
  matchers on screen (six wanderers plus the player) and neither of the two systems built for exactly
  that case was in the scene:
  - `MotionMatchingSearchBatch` is now in `Parkour` and `Sandbox`, so their searches are scheduled in
    Update and completed together in LateUpdate, overlapping on Burst's worker threads instead of
    each blocking on its own (~1.7x at 8-128 characters in local measurements). It auto-collects the
    controllers in the scene when it enables, so runtime-spawned ghosts are picked up too.
  - `MotionMatchingLOD` is now on every AI character, keyed to the camera: a distant wanderer
    re-decides a quarter as often. Only AI gets it - the player is the thing you are looking at, so
    degrading its cadence would show. This stretches the search interval only; playback and IK are
    untouched, so a distant agent still moves smoothly.
  Both shipped in v1.0 and were reachable only by hand-wiring them, which is to say the demo that
  exists to show the toolkit scaling was the one place not using it.

## [1.28.0] - 2026-07-16

AI agents that read the world and vary their gait. The agents already drove the same motion matching
stack as the player - what they lacked was anything to say about obstacles, and any reason to change
speed.

### Added
- Obstacle avoidance on `AICommandProvider`: three feelers (centre and one each side) bend the
  desired velocity around walls, steering toward the roomier side and slowing into the turn, so an
  agent rounds an obstacle instead of grinding along it. Four things read as passable on purpose:
  the agent's own colliders, anything under `_passableHeight`, walkable slopes (by face angle - a
  ramp's collider reaches metres up, so judging it by height would read it as a wall and the agent
  would refuse to climb), and the target a Follow command is chasing (it stands closer than the
  feelers reach; treating it as an obstacle would make the agent orbit the very thing it is closing
  on). Off with `_avoidObstacles`, gizmos on selection, "Steering" count and per-agent pill in the AI
  tab.
- The steer angle is smoothed rather than applied raw: the matcher predicts a trajectory from this
  velocity and searches against it, so a steer that snaps between frames rewrites the prediction each
  frame and the search flips between clips - the same stutter jittery input causes.
- `_passableHeight` is per-agent because how high a character can get over unaided genuinely differs:
  the demo's auto-vaulting follower carries 1.2 (clearing the vault trigger's 1.15 ceiling) so
  avoidance does not steer it around the walls it exists to vault, while a wanderer that cannot vault
  keeps the low default and walks around them.
- `ScriptedAIBrain` now asks for a speed that tracks the distance left (`_walkScale`, `_runScale`,
  `_runDistance`), so an agent runs the long leg and walks the last few metres, and its status line
  says which.

### Fixed
- `ScriptedAIBrain` held one fixed `_speedScale` of 0.8, so every agent jogged at a constant 2.8 m/s
  for its whole life - never walking, never running. That pinned the search inside a single band of
  the data and left every other stride length in the bake unused.
- `AICommandProvider._maxSpeed` defaulted to 3.5, above the 3 m/s ceiling the baked set actually
  covers (the player's input provider caps at 3 for that measured reason). Any brain asking for
  `SpeedScale = 1` starved the search and slid the feet. Now 3.

## [1.27.0] - 2026-07-16

### Added
- Learned Motion Matching, step 2 prep (the training side + the runtime seam):
  - `Documentation~/Training/train_lmm.py`: the PyTorch pipeline (Holden et al. 2020) - a
    compressor/decompressor autoencoder, a stepper trained only on within-clip frame pairs, and a
    projector, exporting `decompressor/stepper/projector.onnx`. Loads the dataset from step 1. Valid,
    documented; not run here (no torch in this environment), syntax-checked.
  - `ILearnedMotionModel` (runtime): the backend-agnostic seam the ONNX weights will sit behind -
    Project (query -> latent, replacing the search), Step (latent -> next latent, no search most
    frames), Decompress (latent -> pose features). No Sentis dependency: the package takes none until
    a backend is added.
  - `Documentation~/Training/README.md`: the bake -> export -> train -> Sentis pipeline and how the
    three networks map to the interface.
- Next (step 3, not built): a Unity Sentis backend implementing `ILearnedMotionModel` from the
  exported ONNX, then the controller path that uses it in place of the database search.

## [1.26.0] - 2026-07-16

Brain-driven AI, LLM-ready, all visible from the window.

### Added
- Two-layer AI: a brain decides *what to do* (high-level goals), a provider turns that into the same
  locomotion intent a player's input would, so an NPC drives the identical motion matching stack.
  - `AIAgentCommand` / `AIGoal` (Idle/MoveTo/Follow/Flee/Patrol/Wander) + `IAIBrain` + `AIContext`:
    the contract between deciding and moving (Runtime).
  - `AICommandProvider` (`ILocomotionProvider`): resolves a command into desired velocity with
    arrival easing and standoff, and takes a runtime override from the window (a manual nudge; the
    brain resumes when it lapses).
  - `ScriptedAIBrain`: deterministic Wander / Patrol / FollowPlayer - the always-available default
    and the fallback for any smarter brain.
- `LLMAIBrain` (sample): asks an OpenAI-compatible chat endpoint what the NPC should do next and maps
  the JSON answer to a command. Endpoint, model, key and persona are all serialized - nothing
  hardcoded, the key is yours to supply and never commit. Consulted on a timer or when a goal
  completes (a few calls a minute per agent, never per frame); async; falls back to a built-in
  wander with no key or on any error. Networking stays out of the runtime.
- AI window tab: every agent's brain, goal, status and reason in one list, with a manual command
  picker (MoveTo origin / Follow / Flee / Idle, per-agent or all-at-once). The dashboard view of the
  AI - what each NPC is doing and why, controllable from the window.
- Sandbox and Parkour scenes now use the brain-driven stack (six wanderers, one follower), so the AI
  tab has agents to show out of the box.

### Notes
- Swapping `ScriptedAIBrain` for `LLMAIBrain` on an agent is the only change needed to put a model in
  charge of it - the provider, the matcher and the window are identical either way.

101/101 EditMode tests. Verified headless: Sandbox carries 6 AICommandProvider agents (brain +
controller + motor each), Parkour 1. The LLM request/response path needs a live endpoint and is not
exercised here; the scripted path and the whole architecture are.

## [1.25.0] - 2026-07-16

### Added
- Learned Motion Matching, step 1: `Tools > Kinema > Learned MM > Export Training Dataset` turns a
  baked database into a training dataset - the normalized float32 feature matrix, per-dimension
  mean/std, per-frame clip index and time, and gait phase, as flat little-endian binaries plus a
  self-describing `manifest.json` and a ready-to-run numpy `load_dataset.py`. Binary, not CSV, since
  a real mocap set is millions of floats. Clip boundaries are exported explicitly so the stepper
  network can build sequences that never cross a clip cut.
  - Verified against the demo set: 4,705 frames x 44 dims, features.f32 exactly frames*dim*4 bytes,
    values byte-identical to the database, 74 clips, 4,631 valid step pairs.
  - This is the foundation for the decompressor/stepper/projector networks that replace the database
    at runtime (Holden et al., SIGGRAPH 2020). Next steps: Unity Sentis runtime inference, then the
    stepper. Sentis is not yet a package dependency - it is only needed at step 2.

## [1.24.0] - 2026-07-16

### Added
- Two more demo scenes under `Tools > Kinema > Scenes`, each generated from the same bake:
  - **Parkour**: a continuous circuit - a run of vault walls, a gap-jump gauntlet, an ascending
    platform stair, a return straight - with an AI follower chasing the player and auto-vaulting.
  - **Sandbox**: an open arena of scattered vaultable blocks and ramps with six AI wanderers, so
    the matcher visibly runs on seven characters at once.
- `AIWanderProvider`: AI locomotion intent that walks to random points in a radius, pauses, and
  wanders on - the same `ILocomotionProvider` the player uses, so it drives the identical stack with
  no input, and its stop/turn/start cycle exercises the hardest transitions. Joins the existing
  `AIFollowProvider`; both keep their collision motor (unlike ghosts) and auto-vault.
- The character build is split into a shared body (matching stack, motor, IK, tuned weights, vault)
  and the driver on top - input for the player, an AI provider for NPCs - which is the point: the
  controller is input-agnostic, so player and AI run byte-identical locomotion.

101/101 EditMode tests. Verified headless: Parkour carries 2 matched characters (player + follower
with vault and motor), Sandbox 7 (player + six wanderers), and the player keeps input, recording and
ghosts in both.

## [1.23.1] - 2026-07-16

### Fixed
- The post-processing volume referenced no profile in the saved scene, so grading did nothing
  despite the camera having post enabled. The builder set `volume.profile` - whose getter
  instantiates a runtime copy the scene never serializes - instead of `volume.sharedProfile`, which
  assigns the asset. Caught by a full pre-launch verification pass; the loaded scene now resolves the
  profile with all four overrides active.

## [1.23.0] - 2026-07-16

### Added
- Demo presentation pass (`DemoPresentation`), all generated - no imported art:
  - URP post-processing volume: ACES tonemapping, bloom, colour adjustments (exposure/contrast/
    saturation) and vignette, written into a generated `VolumeProfile` asset, with the camera's
    post-processing and SMAA enabled. The single biggest visual lift.
  - Lighting: a low warm key light with soft shadows, and gradient (trilight) ambient so the shaded
    side of the character reads as bounce light rather than a flat fill.
  - Materials tuned as surfaces - matte concrete ground, muted-slate obstacles (no more neon cyan),
    and a skin material on the character instead of flat white.

### Notes
- Motion fidelity (foot slide, planting) was left as-is this pass: FootLockIK is already at full
  weight and the feet are weighted 1.4x in the bake, and the remaining tuning needs a play-mode
  foot-slide measurement (the Analysis tab) that a headless build cannot take. The scene-view stats
  label from v1.21 surfaces it.
- Re-run Tools > Kinema > Demo Scene to regenerate the scene with the new look.

101/101 EditMode tests. Verified headless: the volume profile persists its four overrides as
sub-assets (the first attempt serialized them as null until each was added to the profile asset),
the camera has post-processing on, ambient is trilight, and the character carries the skin material.

## [1.22.0] - 2026-07-16

### Added
- Exact ghost replay (default). A ghost now reproduces the recording frame-for-frame - the recorded
  transform and the exact database frame that was selected then - instead of re-matching the
  recorded intent through its own search. That is the difference the report asked for: ghosts follow
  the player precisely rather than making their own (equally valid, slightly different) decisions.
  - `MotionMatchingController.ShowDatabaseFrame(int)`: force-display one baked frame, matching
    suspended - the mechanism the exact replay drives each step.
  - `ReplayLocomotionProvider.ExactReplay`: when set, snaps the transform to the recorded
    position/rotation and shows the recorded selected frame every step (and on scrub). Turns the
    Animator's root motion off so it does not fight the recorded transform.
  - Toggle exposed both in-game (`GhostReplayDirector`, Exact Ghosts) and in the Director tab
    (Exact checkbox). Off is the previous behaviour - re-match the intent, "the AI redoes your
    trajectory".
- A ghost built on a *different* rig stays on intent replay: it cannot show the source database's
  frames, and retargeting the recorded intent onto the new body is the whole point there.

101/101 EditMode tests. All four assemblies Roslyn-compiled clean (editor holds the Unity lock);
exact replay is not playtested here.

## [1.21.1] - 2026-07-16

### Fixed
- Spawned ghosts threw three exceptions per frame (`IndexOutOfRangeException` in
  `TrajectoryDeviation`, `NullReferenceException` in `FootLockIK` and `GroundAdaptationIK`). A ghost
  is `Instantiate`d from an active character, so its components initialize once during the clone,
  then again after the spawner strips and re-enables them - and for a frame the controller and its
  IK run against half-rebuilt state. Now guarded at the three sites: the deviation loop is bounded
  by every array it reads (not just the schema's), `FootLockIK` skips an out-of-range or
  not-yet-searched frame and a null leg set, and `GroundAdaptationIK` null-checks its controller (it
  never did) and its leg set. All three are correct defensive checks independent of ghosts.

### Notes
- The "Can't remove SessionRecorder because GhostReplayDirector depends on it" line is not an error -
  it is Unity refusing a component removal blocked by `[RequireComponent]`, expected.

## [1.21.0] - 2026-07-16

### Added
- Scene-view gizmos overhauled: the desired and candidate trajectories now draw as flat arrows on
  the ground with facing arrowheads at each sample (a route on the floor, not a ribbon at hip
  height), plus two velocity arrows from the character - actual (green) vs desired (cyan) - whose
  gap is the responsiveness at a glance.
- Scene-view stats label above the character (editor-only, stripped from builds): current clip and
  frame, speed vs requested, stride warp, cost split (trajectory vs pose), jump rate, and foot slide
  when a Motion Quality Probe is present. The label turns orange on a jump frame. This is the live
  read the in-game overlay used to show, now drawn like a gizmo so the game view stays clean.

### Changed
- Demo weights are trajectory-dominant (TrajectoryPosition 1.6, TrajectoryDirection 1.3). The
  default pose half slightly outweighs the trajectory half; on a mostly-idle mocap set that let a
  momentarily-slow pose pull the search into the large idle basin while forward was held down - the
  character stuttered idle/walk/idle. Weighting intent over the exact current pose keeps the walk
  while moving. Demo-only, applied by the scene builder; re-run Tools > Kinema > Demo Scene.
- Welcome popup opens with "Thanks for using Kinema Motion Matching" and a one-line sponsor note.

## [1.20.3] - 2026-07-16

### Fixed
- Character walked off on its own at Play with no input touched. `LocomotionInputProvider` read
  `<Gamepad>/leftStick` with no deadzone gate, so any resting drift (a controller rarely centres at
  exactly zero) fed a constant desired velocity. A `_stickDeadzone` (default 0.2) now zeroes stick
  magnitude below the threshold; keyboard input is 0 or 1 so it is unaffected. Unplugging the
  controller was the other workaround.

## [1.20.2] - 2026-07-16

### Fixed
- Demo character jittered while ghosts stayed smooth - the reporter's differential, and the thing
  that identified it: same rig, same database, same matcher, so the cause had to be what the player
  has and a ghost does not. That is the collision motor. Its `CharacterController` kept Unity's
  default `skinWidth` (0.08), tuned for the default 0.5 radius; on this 0.3-radius capsule that is
  27% of the radius, which the CharacterController docs name as a cause of jitter. The motor also
  pushes the body down every frame to stay grounded, so it settled by oscillating inside that skin.
  Skin width is now 10% of the radius and `minMoveDistance` is 0 (its documented anti-jitter value).
  Ghosts were never affected: no motor, root motion goes straight onto the transform.

## [1.20.1] - 2026-07-16

### Fixed
- Ghosts collided with everything (reported as "the character moves on its own" and "invisible
  walls" - one bug, both symptoms). `Strip` only removed MonoBehaviours, and `CharacterController`
  is a Collider, not a MonoBehaviour: every ghost kept the source character's capsule. A ghost is
  instantiated exactly on the player and spawns automatically when a recording stops, so two
  overlapping CharacterControllers got depenetrated and shoved the player sideways; the ghost then
  drifted through the level with no motor to resolve its own movement, a roaming capsule the player
  walked into. Ghost colliders are now disabled (not destroyed - CharacterController is a
  RequireComponent dependency of components the ghost keeps).

### Changed
- Idle pruning is off in the demo setup. It leaves surviving frames unevenly spaced in time, while
  the controller's jump decisions reason in seconds and resolve through frames: the continuity
  window that suppresses "the candidate is where we already are" compares timestamps, and with
  sparse frames that gap exceeds the window even when nothing should move - suppressed jumps fire
  anyway and the result reads as mechanical. It bought ~0.35 MB on this pack. The feature stays,
  off by default; the demo rebakes to the full 4,705 frames.
- Welcome popup's Documentation button now opens the wiki.

## [1.20.0] - 2026-07-16

### Added
- `MotionMatchingController.TickMode` (Automatic/Manual) + `Step(dt)`: in Manual the component never
  advances itself and the caller owns the clock - fixed-step or server-authoritative simulation, and
  deterministic testing. Automatic (self-ticking via Update) stays the default; calling `Step` in
  Automatic warns and no-ops rather than double-advancing the clocks.
- `MotionMatchingSearchBatch.CompletePendingSearches` is public: batch manually-stepped controllers
  by stepping them all, then completing - the same schedule-then-complete order LateUpdate produces.

### Changed
- PlayMode tests no longer use coroutines: `TickMode.Manual` + `Step` replaced `[UnityTest]` /
  `yield return null` / `Time.captureDeltaTime`, so every test is a plain `[Test]` whose timeline is
  exactly the steps it takes - no frame-pacing dependency, fully deterministic. Shared setup moved to
  `PlayModeTestRig`.

101/101 EditMode tests, 11/11 PlayMode tests.

## [1.19.1] - 2026-07-16

### Fixed
- Batched search + teardown race: a controller disabled (or database-switched) between scheduling
  its batched search in Update and the batch's LateUpdate disposed the matcher's NativeArrays while
  the Burst job still read them - a safety-system error in the editor, a race in a build. Teardown
  now completes the pending handle first, and a stale handle arriving after re-initialization is
  completed but its outcome discarded (it says nothing about the new matcher's buffers). PlayMode
  regression test covers the exact window (coroutine resumes between Update and LateUpdate).
- `MotionMatcher.Dispose` now defensively completes the last `ScheduleSearch` handle, so direct API
  users can't dispose under a running job either.
- Timeline mixer restored the state captured before the FIRST control span forever: a script
  toggling matching between two clips was overwritten by the second clip's restore. State is now
  captured fresh at each span's start (EditMode regression test).

### Changed
- `MotionMatchingLOD.BaseInterval` is now public: runtime code changing a LOD-managed character's
  cadence must write it here - writing `SearchInterval` directly was silently stomped on the next
  recompute.
- `MotionMatchingSearchBatch.Register`/`Unregister`: route controllers spawned after the batch
  enabled (the OnEnable auto-collect only sees what already exists).
- Removed a duplicate undo step in the Tags tab's auto-tag button (`ApplyModifiedProperties`
  already registers one).

101/101 EditMode tests, 10/10 PlayMode tests.

## [1.19.0] - 2026-07-16

### Added
- Auto-tag apply: "Detect and apply gait tags" button in the Tags tab writes `GaitClassifier`
  suggestions into the config as real `ClipTagTrack` ranges via `AutoTagApplier` - same
  SerializedObject path as hand-authoring (undo works), tag names created in the vocabulary as
  needed, re-applying replaces the touched clips' ranges instead of stacking, hand-authored tracks
  on untouched clips are preserved. Rebake afterwards to bake the tags into the database. Unit
  tested (5 tests, read back through the config's public API and `MaskAt`).
- In-player search benchmark: the standalone smoke test now runs the editor benchmark's clustered
  synthetic measurement (5k frames x 44 dims) inside the built player and logs a
  `[KinemaSmoke] BENCH` line. Measured locally: mean 44.6 us / p99 86.1 us in the player vs
  87.4 / 138.3 us in-editor - build is ~2x faster, editor numbers are a conservative bound.

### Fixed
- Smoke-test build script left its temp scene in Assets: `EditorApplication.Exit` terminates the
  process before `finally` runs; the scene is now deleted before exiting.

100/100 EditMode tests (5 new), 9/9 PlayMode tests.

## [1.18.0] - 2026-07-15

### Added
- `Kinema.MotionMatching.Timeline` (new optional assembly, only compiles with `com.unity.timeline`
  installed): `MotionMatchingTrack` + `MotionMatchingClipAsset` extend `SetMatchingActive` to
  Timeline - a clip on the track fades matching in for its duration, restoring the prior state once
  no clip on the track is active; stays active across an overlapping crossfade between two clips.
  `MotionMatchingController.IsMatchingActive` added (previously no way to read the current
  active/inactive target). Mixer logic verified by building a real `PlayableGraph` and calling
  `Evaluate()` directly - no Timeline asset or window needed.
- `BlendSpaceMath` + `MotionMatchingBlendSpace`: 2D Gradient Band Interpolation weighting, grid
  generation and feature-row blending for MxM-style blend spaces - unit tested, **not yet wired
  into the baker** (playback replays the matched frame's original `AnimationClip`, so a synthetic
  grid point needs a real baked clip to be playable, not just a blended feature row to be matchable;
  see TODO.md).
- `MotionMatcher.ScheduleSearch`/`CompleteSearch`: non-blocking split of `Search`, so several
  matchers' Burst jobs can be scheduled together and completed together instead of each blocking
  the main thread in series. Addresses the "many characters searching concurrently" profiling gap -
  `Tools > Kinema > Benchmark Search` now measures it (8/32/128 synthetic characters; ~1.7-1.8x
  faster batched than sequential in local measurements). No controller uses this yet.

- First PlayMode tests (`Tests/Runtime`, 4 tests): a real controller on a synthetic database wrapped
  around a procedurally-authored `AnimationClip` - initialization via `SwitchDatabase`, scripted
  intent sweeps with frame/clip mapping asserted in range, the `SetMatchingActive` fade surviving
  ticking, disable/re-enable teardown. Headless: `-runTests -testPlatform PlayMode` (no
  `-nographics`).
- `GaitClassifier` + `Tools > Kinema > Log Auto-Tag Suggestions`: speed/turn/idle detection from
  the motion itself (denormalized root velocity: speed thresholds for idle/walk/run, direction
  change in deg/s for turning, flicker smoothing, consolidated per-clip ranges). Suggestions are
  logged, never written to a config. Verified against the baked Opsive set: detected turn spans
  line up with the turn-named clips without reading any names.
- Snapshot diffing (Debug tab > History): "Pin for diff" pins the scrubbed decision; scrubbing
  elsewhere then shows deltas - per-group costs with the biggest mover called out, frame/clip/jump
  changes, character distance, mean intent shift. Math in `SearchSnapshotDiff.Compute` (runtime,
  unit tested); panel is plain read-only IMGUI.
- First standalone-player verification (`Assets/StandaloneSmokeTest/` in the repo, not the package):
  headless Win64 build + a bootstrap that ticks a real controller 60 frames in the built player and
  prints a `[KinemaSmoke]` verdict. Verified locally: PASS (Burst AOT, live PlayableGraph, real
  searches, outside the Editor). See TODO.md for the URP-migration and antivirus hurdles hit on the
  way.

- `MotionMatchingSearchBatch` + `MotionMatchingController.SearchScheduler`: opt-in cross-character
  search batching. Controllers registered with the batch schedule their periodic search in Update
  and the batch completes them all in LateUpdate, so simultaneous searches overlap on Burst worker
  threads (the ~1.7x the benchmark measures) instead of each blocking the main thread in turn.
  Batched jumps land one graph evaluation later than synchronous ones - documented on the hook.
  Internally `RunSearch` split into `PrepareSearchQuery`/`ApplySearchOutcome`, shared by both paths.
- PlayMode tests for motion events: root-warping lands on its target by contact time (position +
  yaw, fixed timestep), events end on their own and matching resumes, root motion stays bounded.

95/95 EditMode tests (28 new: `MotionMatchingTimelineTests` builds a real `PlayableGraph` and
evaluates it; `BlendSpaceMathTests` covers the interpolation/grid/blend math;
`MotionMatcherScheduleSearchTests` checks the batched path against synchronous `Search`;
`SearchSnapshotDiffTests` covers the diff math; `GaitClassifierTests` covers gait classification on
hand-authored velocity tracks) plus 9/9 PlayMode tests (controller lifecycle, motion-event warping,
search-batch routing and fallback).

## [1.17.0] - 2026-07-15

### Added
- `MotionMatchingLOD`: degrades `MotionMatchingController.SearchInterval` with distance from a
  reference transform (`Camera.main` by default) via a piecewise-linear multiplier over configurable
  distance tiers, recomputed at a throttled rate rather than every frame. Addresses the "Animation
  LOD" gap in TODO.md. `MotionMatchingController.SearchInterval` is now also a public read/write
  property (previously only a serialized field clamped to the inspector's 0.02-0.5 slider range) so
  LOD and similar systems can drive it outside that range.
- Code coverage: `com.unity.testtools.codecoverage` added to `Packages/manifest.json`. Verified
  locally via `-enableCodeCoverage -coverageOptions "generateAdditionalMetrics;generateHtmlReport"`
  (OpenCover XML + HTML report); wired into the CI workflow below.
- `.github/workflows/tests.yml` re-added (EditMode tests + coverage report as build artifacts).
  Needs a `UNITY_LICENSE` repository secret set on the GitHub repo before it runs green - documented
  in the workflow file; not verifiable from a local checkout.
- `CONTRIBUTING.md`: setup, pre-PR checklist (run EditMode tests; say what you verified by eye vs.
  couldn't for the areas with no automated coverage), where things live in the package.

63/63 EditMode tests (10 new: `MotionMatchingLODTests` covers the distance-to-multiplier math
directly - the only part of the LOD system judgeable without a running scene;
`MotionMatchingControllerTests` covers the new `SearchInterval` clamp). Whether a given distance-tier
configuration still reads as acceptable on screen has not been judged visually - tune per project and
playtest; flagged in TODO.md.

## [1.16.0] - 2026-07-15

### Added
- GitHub Sponsors button on the repo (`.github/FUNDING.yml` -> github.com/sponsors/Nekuzaky).
- Welcome popup: opens once per installed/upgraded version (tracked in `EditorPrefs` by version, so
  it reappears on upgrade but stays quiet otherwise), brief quick-start, and three buttons -
  Documentation, Sponsor, Website (nekuzaky.com) - plus Close. Reopen anytime via
  `Tools > Kinema > Welcome`.

## [1.15.1] - 2026-07-15

### Fixed
- Crash right after any motion event ended (vault, and the new free jump - the latter is easy to
  spam, which is how this surfaced): `IndexOutOfRangeException` in `MapClipTimeToFrame`, thrown on
  the very next search. `PlayEvent` marks the slot with clip index -1 (external clip, not in the
  database). On end it resumes matching immediately, but `ContinuationFrame` was still reading that
  stale `-1` out of the slot instead of the frame actually being continued from, and indexed the
  clip array with it. Fixed to derive both the clip and the continuation time from the frame being
  continued, falling back to the slot's own precise clock only when the slot is confirmed to be
  playing that same clip (normal play keeps the sub-frame precision it had).
- `MapClipTimeToFrame` now rejects an invalid clip index with a clear `ArgumentOutOfRangeException`
  instead of raw `IndexOutOfRangeException` - defense in depth, so a similar mistake fails at the
  call site with a message instead of a few frames later with none.

52/52 EditMode tests (2 new, pinning the guard). The crash path itself needs a live PlayableGraph
(events, IK) to reproduce, which EditMode cannot drive - not covered by an automated regression
test; flagged in TODO.md.

## [1.15.0] - 2026-07-15

### Added
- Free jump: Space (gamepad South) with no obstacle ahead now plays a jump event - RunJumpRight
  while moving, IdleJump from standstill - unwarped, so the arc is the clip's own root motion.
  Previously the button only vaulted, and only inside the 0.35-1.15 m obstacle window; open-ground
  presses were eaten silently.
- Every jump press now logs what it decided (vault over X / free jump / nothing bound and why), so
  "it does not jump" becomes diagnosable from the Console.
- Traversal course in the demo scene: three vault walls across the height window, gapped platforms
  sized to the run-jump's travel, and rising steps - one lane that exercises the whole event system
  in a single run.

### Notes
- With the v1.14.2 pruner fix actually comparing the right frames, idle pruning now removes 2,080
  frames (4,705 -> 2,625, -44%); the buggy comparison had been under-pruning at -21%.

## [1.14.2] - 2026-07-15

Audit pass over the v1.13-v1.14 code, adversarial reading rather than new features.

### Fixed
- Idle pruning compared each candidate frame against the wrong frame: the "last kept" index belongs
  to the output list but was dereferenced into the input list. The two agree until the first removal,
  then drift further apart with every one, so keep/drop decisions were made against increasingly
  unrelated poses. Now reads the output list; three tests pin the semantics (identical idles
  collapse, comparison target is the last KEPT frame, moving frames are never pruned).

### Audited clean
- Live pose query: normalization and character-space handling match the bake exactly (both sides
  include root velocity in bone velocities).
- Foot phase: circular distance and absence gates correct in the Burst job; lead-in/tail extension
  handles negative offsets; -1 sentinel respected end to end.
- Critical-spring prediction: transient and its closed-form integral verified against the math.
- KD-tree invalidation on weight change; bake pipeline ordering (prune -> mirror -> phases) sound;
  time-to-frame mapping is a binary search on real frame times, correct under pruning gaps.

### Known bias (documented, unchanged)
- Deviation-triggered search measures deviation in the character's local frame, so self-rotation
  contributes to it; bounded by the v1.14.1 cooldown. Left alone until measured.

## [1.14.1] - 2026-07-15

### Fixed
- Pose flicker while moving (v1.13 regression, reported on video). Two causes, both mine:
  - The live pose query sampled the skeleton at the start of Update - AFTER the previous frame's
    foot lock and ground adaptation IK had moved the feet. IK-displaced feet resemble no baked
    frame, so every search found something "better", jumped, blended into an even stranger pose,
    and jumped again: a feedback loop. The pose is now sampled straight after the graph evaluates,
    before the LateUpdate IK passes touch it.
  - Deviation-triggered searches had no floor: a sustained turn deviates continuously, so the
    search fired every frame and each search was a chance to jump. A cooldown (default 60 ms) now
    bounds the rate; the timer-based interval is unchanged.
- Both features remain serialized toggles on the controller (Live Pose Query, Search On Deviation)
  and can be disabled outright if needed.

## [1.14.0] - 2026-07-15

### Added
- `Tools > Kinema > Benchmark Search`: measures the real Burst search and reports mean/median/p99
  per search, plus the number a game actually asks - how many characters can search at 10 Hz inside
  a 60 fps frame. Queries are real database rows with noise, not random vectors, because the
  branch-and-bound early-out makes a plausible query and a nonsense one take different paths.
  Synthetic clustered databases (5k to 400k frames) chart the growth curve past whatever is baked
  locally, and the baked database is measured twice - first and last - so an order-dependent result
  is visible rather than believed.

### Measured (Ryzen, 20 workers, Burst on, 44 dims)

| database | search | p99 | characters @10 Hz in a 60 fps frame |
|---|---|---|---|
| demo pack, 3,699 frames | 82 us | 128 us | ~780 |
| synthetic 25,000 | 337 us | 459 us | ~218 |
| synthetic 100,000 | 1,194 us | 1,422 us | ~70 |
| synthetic 100,000, KD-tree | 267 us | 450 us | ~222 |
| synthetic 400,000 | 4,694 us | 5,290 us | ~18 |
| synthetic 400,000, KD-tree | 942 us | 1,680 us | ~59 |

The linear scan grows linearly, as expected, and the KD-tree earns its keep past ~100k frames
(3x). Below ~25k frames the linear scan wins on both latency and simplicity - which is where the
demo, and most projects, live.

### Notes
- The first version of this benchmark was wrong and said so under testing: whichever database was
  measured first reported ~10x its true cost, because Burst compiles asynchronously in the editor
  and the early samples ran as managed IL. Reordering the runs moved the slow result with the
  order. Fixed by forcing synchronous Burst compilation; the first/last re-measurement of the same
  database now agrees within 8%, and that check ships with the benchmark.

## [1.13.0] - 2026-07-15

Core matching release: six improvements to the matcher itself, the first since v1.4.

### Added
- Live pose query. The query's pose half was copied from the database row being played, but the
  rendered pose is not that frame any more - inertialization, stride warping and IK all moved it.
  That is why the pose cost read exactly 0.000. The query now samples the schema's bones off the
  rig as rendered (positions and velocities in character space, the baker's own math), verified by
  a test feeding a skeleton standing exactly on a baked frame. Falls back to the frame copy when a
  schema bone is missing on the rig.
- Foot-phase cost term. Gait phase (0..1 through a step cycle) is baked from the contacts, stored
  as a parallel channel - the feature layout, KD-tree and old databases stay intact - and the
  matcher adds a circular phase distance so candidates that would cut a stride mid-step cost more.
  Inert at weight 0 (every existing config) and on frames with no cycle (idles carry -1).
- Critically damped spring prediction (default; the old exponential lag stays selectable). Velocity
  starts changing with zero acceleration and ramps in, the way a body with mass commits to a turn.
  Closed-form, verified against numerical integration.
- Deviation-triggered search: prediction runs every frame, and when the predicted future points
  drift beyond a threshold from what the last search answered, the search fires that frame instead
  of up to a full interval later. Character space keeps the criterion stable in straight lines.
- Idle pruning (opt-in on the config): near-identical consecutive idle frames are dropped at bake
  time. On the demo pack: 4,705 -> 3,699 frames (-21%), all of it interchangeable standing. Frame
  mapping switched to a binary search over actual frame times, correct on non-uniform grids.
- `WeightTuner`: coordinate descent over the matching weights, one deterministic replay of a
  recorded take per evaluation, scored by foot slide + jump rate. Record a lap, add the component
  next to a ReplayLocomotionProvider, call StartTuning, wait; best weights are applied and logged.

### Notes
- The demo config now bakes with phases, pruning and a 0.5 FootPhase weight. Existing configs are
  untouched: the phase term needs a rebake plus a nonzero weight to do anything.
- 47/47 EditMode tests (8 new across query, predictor and phase). Play-mode feel is not visually
  verified; the deterministic replay + Analysis tab is the intended way to measure it.

## [1.12.0] - 2026-07-15

### Added
- Ghosts on any body: hand a different Humanoid model to the spawner (Director > Ghosts > Ghost rig,
  or the sample director's Ghost Rig field) and the ghost is built on it - controller settings and
  database references copied over, Humanoid retargeting mapping the recorded performance onto the
  new proportions. A non-Humanoid rig is refused with a console warning, since the database cannot
  retarget onto it.
- Every ghost records its own pose from its first frame. Director > Ghosts > Bake Ghost Clip turns
  that performance into an AnimationClip - swap the rig, spawn, bake, and the take exists as a clip
  on the new character.
- `MotionMatchingController.SetAnimator`: re-point the controller before it initializes; the
  ghost-on-a-different-rig path needs it because the copied settings still name the source Animator.

## [1.11.0] - 2026-07-15

### Removed
- The in-game browser overlay (`AnimationBrowser`). Everything it did lives in the window's
  Director tab now, so the game view carries no UI at all. The in-game shortcuts stay: R/Select
  records, G/right-shoulder spawns a ghost, K/dpad-down clears, C/Ctrl/East crouches, Space/South
  vaults. Regenerate the demo scene to drop the now-missing component from existing scenes.

## [1.10.0] - 2026-07-15

Clean-game-view release: everything the in-game overlay did now lives in the window.

### Added
- Director > Tags: the require/exclude stance filter moved into the window.
- Director > Take timeline: CapCut-style navigation over a recorded take. The speed curve is the
  filmstrip, the playhead scrubs the newest ghost along the recorded trajectory (transport buttons
  and frame stepping included). Scrubbing snaps the ghost's root to the recorded transform; its
  matcher re-solves the pose from there, so the trajectory is exact and the animation approximate.
  Takes recorded with the in-game hotkeys appear too - the timeline reads them off the ghost.
- Director > Character: swap the character's rig in one click (edit mode). Components, settings and
  database references move to the new body via the component clipboard, the controller's Animator is
  re-pointed, and anything following the old transform (camera, AI targets) is retargeted. Humanoid
  retargeting maps the data onto the new proportions.
- `ReplayLocomotionProvider.Paused` / `ScrubTo(frame)`: the runtime side of the take timeline.

### Changed
- The in-game browser overlay is hidden by default: the game view starts clean; Tab or gamepad
  Start opens it. Regenerate the demo scene (or untick Visible On Start) for existing scenes.

## [1.9.0] - 2026-07-15

Director release: the record/ghost/playback controls move into the editor window, and the demo
gets full gamepad coverage.

### Added
- Director tab in the Motion Matching window. Plays any database clip on the live character like a
  custom Animator: transport controls, a scrubbable timeline with per-foot contact lanes drawn from
  the baked data, pause, and a filterable clip list. Playback is the clip-override path, so what is
  on screen is the baked clip through the real graph - IK and retargeting included.
- Recording from the window: capture intent and pose together, save the session as an asset, bake
  the pose take to an AnimationClip - same recorders the in-game overlay drives.
- Ghost direction from the window: spawn and clear ghosts without touching the game overlay.
- `GhostSpawner` (runtime): ghost creation shared by the window and the sample hotkeys. Strips the
  clone by whitelist (controller, IK) rather than blacklist, so project gameplay scripts are removed
  without the runtime knowing their types. Ghosts keep no collision motor - the traditional rule.
- `MotionMatchingController.OverridePaused` / `SetClipOverrideTime`: freeze and scrub the override
  clock, which is what makes the timeline a timeline.
- Full gamepad map: record on Select, ghost on right shoulder, clear on dpad-down, browser overlay
  on Start (all alongside the existing keyboard keys), and the follow camera now orbits with the
  right stick or middle-mouse drag - identical framing to before until the stick is touched.
- Example take shipped with the sample (`Takes/ExampleTake.asset`): a recorded session - intent
  only, velocities and trajectory - replayable by ghosts out of the box.

### Changed
- Window header shows the package version and a LIVE pill during play.

### Notes
- Recorded pose takes made from the Opsive pack are resampled licensed mocap and stay out of the
  repository; the example take carries intent only, which is why it can ship.

## [1.8.1] - 2026-07-15

### Changed
- Demo locomotion tuned to reduce foot sliding, which the recording surfaced (foot slide 0.17-0.27
  m/s, 3 clip changes per second). Two evidence-based causes:
  - `LocomotionInputProvider` top speed 4 -> 3 m/s. Measured against the Opsive set, ~6.5% of frames
    sit within stride-warp range of a 3 m/s request versus ~2.8% at 4 m/s; 66% of the set is below
    0.5 m/s, so run data is thin and asking for 4 m/s lands in its sparsest region. A starved search
    flickers between clips and pins the stride warp at its 1.3x ceiling, both of which slide the
    planted foot.
  - The demo character's clip-change cost is set to 0.25 (from the 0.1 default) so the search stops
    hopping between clips several times a second; each hop blends, and a blend drags the planted foot
    far enough to break the foot lock.
- Both are demo-scene values, not package defaults - the runtime defaults are unchanged.

### Notes
- Measured headlessly: the database speed histogram and per-speed stride-warp reachability. The foot
  slide itself could not be measured headless - it needs the PlayableGraph's animation jobs, which do
  not initialise under -batchmode -nographics - so the on-screen improvement is reasoned from those
  numbers, not yet confirmed on a running character.

## [1.8.0] - 2026-07-15

### Changed
- One demo generator: `Tools > Kinema > Demo Scene`. It picks its own source, best first - an
  installed mocap pack, otherwise an FBX in the sample's Character folder (using its clips, or
  generating a procedural set if it is only a skin) - bakes it, and builds the scene. There were four
  generators before (`Demo Scene`, `Setup > Demo From FBX`, `Setup > Demo From Opsive Pack`,
  `Setup > Placeholder Scene`), each with its own idea of what the demo scene was.
- Every source now funnels through one bake contract (`DemoBake`: rig, database and vault event
  paths) and one scene builder, so the demo means one thing rather than one thing per source.

### Removed
- The `Setup` submenu and the placeholder-scene generator. A capsule with no data has nothing left to
  demonstrate now that the generator resolves its own source.

## [1.7.1] - 2026-07-15

### Fixed
- Every small label in the window rendered as nothing: stat cards showed a number with no caption,
  clip bars had no names, subsystem rows were bare dots, and Summary collapsed to an empty box. The
  key/value styles were built from `EditorStyles.label` with `fontSize` forced down, which leaves the
  style without a font at that size - the text then measures and draws as nothing, which is why rows
  collapsed while the bars and numbers beside them kept rendering. They now derive from
  `EditorStyles.miniLabel`, which is already small, and are rebuilt when the editor skin changes.
- The window opened on whichever database the project returned first - typically the small starter
  set (187 frames) rather than the real one (4,705). It now opens on the richest baked database and
  pairs the matching config with it.

## [1.7.0] - 2026-07-15

Recording release, plus one tool instead of three.

### Added
- `PoseRecorder` + `PoseClipBaker`: record the skeleton as actually posed on screen - after matching,
  blending, stride warping and IK - and bake it into a real AnimationClip asset via
  `Tools > Kinema > Save Last Take As Animation Clip`. This is a different capture from
  `SessionRecorder`, which stores intent: replaying intent re-runs the matcher and can produce a
  different (equally valid) performance, while a pose take is the performance itself.
- `GhostReplayDirector`: record with R, and an NPC clone is sent out to redo your trajectory. The
  ghost is driven by your recorded *intent* and runs its own matching against it, so it reproduces
  where you went rather than replaying a video of you. G spawns another, K clears them.
- Vault event is now authored from the mocap pack's running jump, so Space works in the pack scene.
- Browser overlay gained a recording section (take length, ghost count, record/ghost/clear).

### Changed
- `Tools > Kinema > Demo Scene` now runs the entire setup itself: it bakes every clip in the
  installed mocap pack and then builds the scene. No separate setup trip first.
- One scene builder for every entry point, instead of each setup defining its own demo scene.

### Fixed
- Space did nothing in the pack scene: `VaultTrigger` was only ever added by the procedural FBX
  setup, so the pack scene never carried it and no vault event existed to trigger. Not an Input
  System problem, which is where it looked like it pointed.
- Scene building lost every asset reference wired before the scene was created. `NewScene` unloads
  unreferenced assets and destroys their instances, leaving Unity fake-nulls: the C# reference is
  alive, `== null` is true, and the wiring silently drops. The builder now takes paths and loads from
  disk after the scene exists.
- Ghost clones kept the controller's cached input provider after it was stripped, so they would have
  stood still; `SetLocomotionProvider` rebinds them to the replay.
- `ReplayLocomotionProvider.ForceRecordedTimestep` is now settable. It drives the engine clock, which
  is right for deterministic analysis and wrong for a ghost running next to a live player - it would
  dictate the player's frame rate too.

## [1.6.1] - 2026-07-15

### Fixed
- Opsive setup baked and played on the pack's own rig, which is a bare skeleton: 68 joints, a valid
  Humanoid avatar and no mesh at all. The character animated correctly and rendered nothing, which
  looks exactly like the scene failing to build. The setup now detects a skinless pack rig and falls
  back to the demo's skinned character, letting Humanoid retargeting map the mocap onto it. Baking
  happens on that same rig, so the features describe the body on screen rather than a proxy of it.
- The demo model is forced to Humanoid on import when used this way; retargeting only exists in
  muscle space, so a Generic import silently made the mocap unusable.
- `Tools > Kinema > Demo Scene` now warns when the resolved rig carries no skinned mesh, instead of
  handing over an empty-looking viewport.

### Changed
- All menus consolidated under `Tools > Kinema`. There were two entry points (a top-level `Kinema`
  menu and `Tools > Kinema`), which is one too many.

### Notes
- Retargeted bake vs the skeleton bake: peak root speed 5.92 m/s (was 6.47 - proportions differ),
  38% of frames travelling (was 40%), foot contact on 82% of frames (was 53%). The contact jump is
  the more plausible figure: outside a sprint flight phase a foot should almost always be down, so
  the skeleton bake was under-detecting contacts against the 0.15 m height threshold.

## [1.6.0] - 2026-07-15

Data inspection release. The demo answers "does locomotion feel right"; nothing answered "is my
data any good". The matcher only ever shows frames that fit what you happened to be doing, so a
broken, mis-tagged or badly baked clip can sit in a database indefinitely without ever appearing.

### Added
- `Tools > Kinema > Demo Scene`: builds a scene for exercising a whole database. Picks the richest
  baked config in the project automatically, so importing a bigger pack and rerunning picks it up
  with no arguments. Terrain is built to provoke the subsystems flat ground never touches - slopes
  and steps for ground adaptation, a low ledge for the vault event, a long lane for stride warping.
- `AnimationBrowser` sample: in-game overlay (Tab) listing every clip with a name filter, playing any
  of them on demand, toggling every tag as required or excluded, and showing the live frame, contact
  state, stride warp and foot-slide numbers alongside.
- `MotionMatchingController.PlayClipOverride` / `StopClipOverride` / `IsOverridingClip`: force-play a
  database clip with matching suspended. Stride warping is held at 1 for the duration, so what is on
  screen is the clip as baked rather than a scaled version of it.

## [1.5.0] - 2026-07-15

Mocap data release. The demo could previously only be driven by procedurally authored clips, which
capped how real the result could look no matter how good the runtime was. This release adds a setup
path for a real motion capture locomotion pack.

### Added
- `OpsivePackSetup`: one-click setup that builds a config, database and scene from an Opsive
  OmniAnimation locomotion pack. Bakes on the pack's own rig, so features, contact bone names and
  playback all stay on the skeleton the capture was authored for, with no retargeting guesswork.
- Clip auto-tagging from the pack's naming convention: 74 clips are sorted into 12 tags
  (Crouch, Strafe, Backward, Diagonal, Idle, Walk, Run, Sprint, Turn, Start, Stop, Jump) without
  painting a single range by hand.
- `StanceTagController` sample: drives `RequiredTags` / `ExcludedTags` from gameplay stance. A mixed
  pack needs this - nothing in the feature vector says "this pose is crouched", so an unfiltered
  search will answer a standing query with a crouched frame whose feet happen to line up.

### Notes
- The pack itself is per-seat licensed Asset Store content and is not redistributed here. Import it
  into your own project, then run Kinema > Motion Matching > Setup Demo From Opsive Pack. The
  generated config, database and scene are git-ignored for the same reason.
- Measured on the baked set: peak root speed 6.47 m/s, 40% of frames travelling above 0.3 m/s, a
  foot contact detected on 53% of frames.

## [1.4.0] - 2026-07-15

Movement realism release.

### Added
- Stride warping: clip playback is scaled by requested/baked root speed, so a database holding a
  1.4 m/s walk and a 4 m/s run can travel at any speed between them with the legs cycling in sync
  with the travel. Without it, root-motion locomotion can only move at the discrete speeds present
  in the data and the feet slide the difference. Range-clamped, smoothed, and skipped for idle and
  motion events. Exposed as `CurrentStrideWarp` and surfaced in the Analysis tab.
- `GroundAdaptationIK`: clips are authored on a flat floor, so on ramps and steps the feet float or
  sink. This probes under each foot, plants it on the real surface, tilts it to the slope, and drops
  the pelvis so the lower foot can still reach - height and orientation only, complementing
  `FootLockIK`'s horizontal pinning.
- The demo character now ships with ground adaptation, the quality probe and the session recorder
  wired in.

### Note on realism
System work has a ceiling: the procedural demo clips are sine-wave approximations. Stride warping
and IK remove the mechanical artefacts, but human-looking motion needs mocap data. Drop real
locomotion clips into the sample's `Character/Animations/` folder and re-run the setup.

## [1.3.0] - 2026-07-15

Instrumentation and authoring release: measure the system instead of eyeballing it.

### Added
- Session recording and deterministic replay: `SessionRecorder` captures a play session's
  locomotion intent frame by frame into a `SessionRecording` asset, and
  `ReplayLocomotionProvider` feeds that identical intent back through the controller as an ordinary
  `ILocomotionProvider`. With Force Recorded Timestep the recorded per-frame delta drives
  `Time.captureDeltaTime`, so matching decisions reproduce exactly - two tuning setups can finally
  be compared on the same performance rather than on two different human runs.
- Foot-slide measurement (`MotionQualityProbe`): while the baked contacts flag a foot grounded, any
  world-space travel is slide - the metric animation teams judge locomotion by. Kinema can measure
  it precisely because contacts are baked data rather than a guess. Also reports jump rate (flicker)
  and average/peak matching cost.
- Database coverage analysis (`CoverageReport`, controller frame-usage telemetry): which frames the
  matcher actually reaches for, how much of the database is dead weight, and which clips are never
  selected at all - wasted memory and mocap budget made visible.
- Analysis tab: quality stat cards with plain-English diagnosis, per-clip coverage bars with dead
  clips flagged, and record/save/replay controls in one place.
- Six coverage tests (39 total, all passing).

## [1.2.1] - 2026-07-14

### Fixed
- Settings tab logged "You can't nest Foldout Headers" once per List field: the runtime and config
  parameter groups used `BeginFoldoutHeaderGroup`, which cannot contain the reorderable-list
  drawers those objects expose. Replaced with plain framed sections.

## [1.2.0] - 2026-07-14

Quality-of-engineering release: real state rewind, automated tests, CI.

### Added
- Full-state snapshot rewind: `SearchSnapshot` now captures the character transform and both
  playback slots (clip, time, blend, mirror flag), and `MotionMatchingController.PreviewSnapshot`
  / `StopPreview` replay that exact state through the live graph - the Debug tab's history scrubber
  gained a Preview toggle that visually rewinds the character, not just the recorded numbers.
- EditMode test suite (`Tests/Editor`, 33 tests): feature schema layout and weight expansion,
  `CharacterSpace` round-trips, the trajectory history ring buffer, database normalization and
  accessors, and end-to-end matcher correctness (nearest-neighbour, tag filtering, ignore ranges)
  against synthetic databases built directly via `SetBakedData`.
- GitHub Actions CI (`.github/workflows/tests.yml`) running the suite on every push via
  game-ci/unity-test-runner; needs a `UNITY_LICENSE` repository secret to activate.
- `TODO.md`: the honest remaining-gaps list (PlayMode coverage, standalone build validation,
  large-scale performance, mirroring visual validation, and the not-yet-started feature scope).

## [1.1.0] - 2026-07-14

Tooling and demo-gameplay release.

### Added
- Overview dashboard: stat cards (frames / clips / dims / memory) and a subsystem status board
  (database, contacts, tags, mirroring, profiles, storage) with actionable detail per row.
- Frame Inspector in the Database tab: scrub any baked frame and read back its denormalized data —
  root speed, per-bone positions and weights, grounded feet, active tags.
- Cost sparkline in the Debug tab: total-cost history over the last 120 searches with jump markers.
- Bake pre-flight: rig/clips/schema checklist plus estimated frame count and asset size
  (mirroring and 16-bit storage factored in) before committing to a bake.
- Full parameter surface in Settings: every controller field (live in play mode, immediate weight
  resync) and every config field, plus one-click calibration profile buttons.
- Demo vault: procedural vault clip + motion event with warped contact, VaultTrigger (chest-ray
  probe, obstacle top 0.35-1.15 m, Space / gamepad South), optional Auto Vault for AI.
- AIFollowProvider sample: target-seeking intent through the same ILocomotionProvider contract as
  the player, with arrival slow-down.

### Fixed
- Backward teleport on clip loop: looping slot clocks are now monotonic (manual wrapping made the
  Animator emit a negative root-motion delta every loop).
- Procedural gait: human cadence (one leg cycle per clip), arms lowered from the T-pose with
  chain-composed elbows, level feet, pelvis sway, speed-scaled torso lean.
- FollowCamera auto-targets the matched character when its serialized reference is missing.
- Editor-time fake-null after AssetDatabase.Refresh when wiring freshly created event assets.

## [1.0.0] - 2026-07-14

Scale release.

### Added
- Calibration profiles: named weight presets on the config, baked into the database, applied at
  runtime with `controller.SetCalibrationProfile(name)` (no rebake).
- 16-bit feature storage (opt-in): features serialized as halves, halving the asset size; decoded
  once at load. Negligible quality impact on normalized features.
- Multi-database: an additional-databases list on the controller plus
  `SwitchDatabase(database | index)`; the next search re-anchors in the new set and the
  inertializer absorbs the transition.
- Optional KD-tree search (`SearchAcceleration.KdTree`): a tree over weight-scaled features for
  very large databases. Falls back to the Burst linear job whenever tag masks or ignore ranges are
  active. BurstLinear remains the default and the right choice below ~50k frames.
- Mecanim interop: `SetMatchingActive(bool, fade)` fades the matching output against the
  Animator's own controller, so scripted states and cinematics can take over and hand back.

## [0.5.0] - 2026-07-14

Actions release.

### Added
- Motion events (`MotionEventDefinition` asset + `controller.PlayEvent`): triggered action clips
  (vault, interaction) played outside the matching loop, with root warping so the clip's contact
  moment lands exactly on the requested position/rotation. Matching resumes with an immediate
  search when the clip ends; the inertializer absorbs both seams.
- Overlay layers: an `AnimationLayerMixerPlayable` above the matching chain.
  `PlayOverlay(clip, avatarMask, weight, fade)` / `StopOverlay()` for upper-body actions while
  locomotion keeps matching; one-shot overlays fade out on their own.
- Mirroring (experimental, opt-in on the config): every frame baked as a mirrored variant
  (trajectory/root X-flip, Left/Right bone slots swapped, contacts bit-swapped) and played through a
  runtime mirror pose job (name-paired Left/Right transforms, X-plane reflection). Doubles coverage
  without new mocap. Requires a symmetric rig; off by default.
- Snapshot debugger: the last 240 matching decisions recorded in a preallocated ring buffer
  (costs, breakdown, trajectories) with a scrubber in the Debug tab.

### Honest status
- Mirroring is baked-and-wired but unvalidated visually; keep it off until playtested.

## [0.4.0] - 2026-07-14

Control release.

### Added
- Semantic tags: a 64-tag vocabulary on the config, per-clip tag ranges, baked into the database as
  per-frame bitmasks. The search filters candidates with `RequiredTags` / `ExcludedTags` masks
  (controller properties, plus `RequireTag(name)` helper); filtering happens inside the Burst job.
- Tags tab in the editor window: visual per-clip timeline (one lane per tag, colored range blocks)
  with numeric editing, stored on the config.
- Strafe mode in the sample input provider (hold right mouse / gamepad left trigger): the character
  faces the camera while moving in any direction. Pair with a "Strafe" tag on strafe clips.
- AnimationEvent relay: events of the active clip fire via SendMessage with Mecanim semantics
  (manual-clock playback bypasses Unity's own dispatch), loop-wrap safe. Toggleable.

## [0.3.0] - 2026-07-14

Motion quality release.

### Added
- Inertialization transitions (`PoseInertializer`, Burst-compiled animation job): the graph
  hard-switches clips and a per-bone pose/velocity offset decays over the blend time with a cubic
  Hermite curve (Gears of War 4 style). New `TransitionMode` on the controller; inertialization is
  the default, crossfade remains available.
- Foot contacts baked into the database (per-frame bitmask; feet auto-detected from schema bone
  names) and `FootLockIK`: analytic two-bone IK in LateUpdate that pins grounded feet, with break
  distance and smooth release.
- Burst/Jobs search: features live in a persistent `NativeArray` and the weighted nearest-neighbour
  scan runs as a Burst-compiled parallel job (chunked, branch-and-bound early-out per chunk).
  `MotionMatcher` is now `IDisposable`.

### Changed
- New package dependencies: `com.unity.burst`, `com.unity.mathematics`.
- Databases must be rebaked to include contact data (older databases still load; foot lock disables itself).

### Honest status
- Compiles and bakes headless; inertialization and foot lock have not yet been visually playtested.
  Both can be disabled per component if something looks off (`TransitionMode.Crossfade`, FootLockIK weight 0).

## [0.2.0] - 2026-07-14

Matching quality and tooling pass, informed by JLPM22/MxM-style systems.

### Added
- Past + future trajectory: `TrajectoryTimes` now accepts negative offsets, sampled from a runtime
  `TrajectoryHistory` ring buffer, so matching considers where the character came from, not only where it is going.
- Per-bone feature weights (`FeatureSchema.BoneWeights`), parallel to `BoneNames`, to weight feet/hands
  above the hips (MxM-style joint weighting).
- Transition (clip-change) cost penalty on the controller to reduce clip flicker while preserving responsiveness.
- Scene-view debug gizmos for the matched frame's sampled bone positions, plus the bone-weights field in the Settings tab.

### Changed
- Default schema now samples two past and four future trajectory points. Databases baked with 0.1.0 must be rebaked.

### Roadmap
- Inertialization-based pose transitions and Burst/Jobs-accelerated search are the next steps; they change
  runtime behaviour and are best landed with in-editor playtesting.

## [0.1.0] - 2026-07-14

Initial release.

### Added
- Runtime motion matching for locomotion built on a manually clocked `PlayableGraph`
  (two-slot crossfade), input-agnostic via `ILocomotionProvider`.
- Data model: `FeatureSchema`, `TrajectorySample`, `MotionFrameInfo`, `MotionClipEntry`,
  per-group `FeatureWeights`, and a `CharacterSpace` reference frame shared by bake, runtime and debug.
- Offline bake pipeline (`MotionMatchingBaker`, `PoseExtractor`) sampling `AnimationClip`s into a
  normalized feature database (`MotionMatchingDatabase`) with per-dimension mean/std statistics.
- Configurable weighted nearest-neighbour scorer (`MotionMatcher`) with a per-group cost breakdown.
- Editor window with Overview, Database, Bake, Debug and Settings tabs, plus custom inspectors
  and scene-view gizmos/handles for the desired and candidate trajectories.
- Sample "Locomotion Demo": collision-aware `CharacterMotor`, `LocomotionInputProvider`,
  `FollowCamera`, a one-click scene setup, and a procedural locomotion generator used when no
  clips are supplied.

### Known limitations
- Search is a linear scan (fine for a few thousand frames; a spatial index is a future step).
- In-place clips produce flat trajectories; clips with root motion are recommended.
