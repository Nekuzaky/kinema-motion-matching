# TODO / Known Limitations

Honest gap list. The architecture and tooling are AAA-informed and cover a wide feature surface
(see the [wiki](https://github.com/Nekuzaky/Kinema/wiki)), but the project is young: few real play
hours, and the thing no test can settle - whether the motion *reads* right on screen - is still
largely unjudged. This file tracks what's left, roughly in priority order within each section.

## Data

- [x] **Real mocap demo path** - `Tools > Kinema > Demo Scene` bakes an installed Opsive
      OmniAnimation pack (74 clips) when present, auto-tagged and retargeted onto the demo's
      Humanoid rig; falls back to the procedural clips only when no pack and no dropped-in FBX
      clips are available.
- [ ] **Validate mirroring visually.** Baked and wired (trajectory/root X-flip, Left/Right bone
      swap, runtime mirror pose job) but never checked on screen. Keep `Generate Mirrored
      Variants` off until it has been playtested on a real rig.
- [ ] **Validate the v1.13-1.15 matching changes visually.** Live pose query, foot-phase cost,
      spring prediction, deviation search and idle pruning are compile/headless-verified and unit
      tested, but runtime feel (does it read as more or less real) has not been judged on screen.

## Performance

- [x] **Stress-tested at scale** - `Tools > Kinema > Benchmark Search` measures the real Burst
      search against the baked database and synthetic clustered sets up to 400k frames; see the
      README/changelog for numbers. Confirmed: BurstLinear wins below ~25k frames (where the demo
      and most projects sit), the KD-tree only earns its keep past ~100k.
- [x] **Profiled: many characters searching concurrently.** `MotionMatcher.ScheduleSearch`/
      `CompleteSearch` split the existing `Search` into a non-blocking schedule and a completion
      step, so N matchers' Burst jobs can run concurrently instead of each blocking the main thread
      in series.
      `Tools > Kinema > Benchmark Search` now measures both at N = 8/32/128 synthetic characters:
      batched came out ~1.7-1.8x faster than sequential at every N tested here. Correctness (batched
      result == synchronous `Search` result, including tag/ignore-range filters) is unit tested.
      **Adopted**: `MotionMatchingSearchBatch` (add to the scene, assign controllers or let it
      auto-collect) routes every registered controller's periodic search through
      schedule-in-Update / complete-in-LateUpdate, so simultaneous searches overlap on worker
      threads. Trade-off: a batched jump lands one graph evaluation later than the synchronous path
      (documented on `MotionMatchingController.SearchScheduler`). PlayMode-tested: routing,
      soundness under ticking, clean synchronous fallback when the batch is disabled.
- [x] **Standalone-build numbers measured** - the smoke-test player now runs the same clustered
      synthetic benchmark as the editor's Benchmark Search (5k frames x 44 dims, plausible queries,
      warmup absorbing the Burst compile) and logs one `[KinemaSmoke] BENCH` line. Measured locally:
      mean 44.6 us / median 44.7 us / p99 86.1 us in the player, vs 87.4 / 85.5 / 138.3 us in-editor
      for the same configuration - the build is roughly twice as fast as editor measurements, so
      editor numbers are a safe conservative bound.
- [x] **Animation LOD** - `MotionMatchingLOD` degrades `MotionMatchingController.SearchInterval`
      with distance from camera (piecewise-linear multiplier over configurable distance tiers,
      recomputed at a throttled rate, not per frame). The distance-to-multiplier math is unit
      tested (`MotionMatchingLODTests`); whether the degraded cadence still reads as acceptable on
      screen for a given tier configuration has not been judged visually - tune tiers per project
      and playtest.

## Runtime behaviour

- [x] **First PlayMode test coverage** - `Tests/Runtime/MotionMatchingControllerPlayModeTests`
      drives a real controller (live PlayableGraph, real Update loop and searches) on a synthetic
      database wrapped around a procedurally-authored AnimationClip: initialization via
      `SwitchDatabase`, ~60 ticks of scripted intent sweeps with frame/clip mapping asserted in
      range, the `SetMatchingActive` fade surviving ticking, and disable/re-enable teardown/rebuild.
      Run headless: `-runTests -testPlatform PlayMode` (no `-nographics`).
- [x] **PlayMode coverage for motion events** - `Tests/Runtime/MotionEventPlayModeTests`: event
      root-warping measurably lands on its target by contact time (position and yaw), the event ends
      on its own at clip end and matching resumes, and ticking never diverges or goes NaN. All at a
      fixed timestep the test owns (`TickMode.Manual` + `Step`), not a yielded frame.
      Test-authoring gotcha discovered on the way, worth remembering: a synthetic clip must never
      animate the ROOT transform - any root curve on any clip connected to the mixer keeps that
      property graph-owned even at weight 0, and every Evaluate stomps the event warp's transform
      writes (real mocap carries root motion through the Animator instead).
- [ ] Root motion itself is still unasserted: the synthetic rig has no source of it (see above -
      its clips animate a child bone by design, and nothing else drives the transform), so the
      suite can only prove the character does not diverge, never that it travels. Needs the same
      Humanoid-with-Animator rig the IK gap below is waiting on.
- [ ] PlayMode coverage still missing for: inertializer output and foot lock IK - both need a
      Humanoid rig with an IK pass, which the synthetic no-rig setup can't provide.
- [x] **Built and smoke-tested as a standalone Windows player.** `Assets/StandaloneSmokeTest/`:
      an editor build script (`BuildSmokeTest.Build`, headless via `-executeMethod`) builds a
      Win64 player around a bootstrap scene that assembles the synthetic controller setup (same as
      the PlayMode tests), ticks 60 real frames - Burst compiled AOT, live PlayableGraph, real
      searches - and prints a greppable `[KinemaSmoke] PASS/FAIL` verdict before quitting. Verified
      locally: build succeeded, player ran headless, verdict PASS. Two machine-specific hurdles hit
      on the way, worth knowing: (1) URP refuses to build while its GlobalSettings asset is pending
      version migration - the build script now saves the migrated asset first; if the installed
      Editor is *older* than the one that authored the asset (here: 6000.3.2f1 vs .13f1) the strict
      equality check can only be satisfied by hand-editing `m_AssetVersion` (done temporarily for
      this verification, then reverted). (2) FortiClient's real-time scan intermittently breaks
      ILPP with `DirectoryNotFoundException` under `C:\Program Files\Fortinet\...` - a retry got
      past it. Mac/Linux/mobile/console remain unverified; input in a real build (the demo's Input
      System bindings) is not exercised by this smoke test.
- [ ] Events and overlay layers have exactly one demonstrated use case each (vault; none for
      overlays). Untested against overlapping events, event-during-event, or multiple concurrent
      overlay layers.
- [ ] **Judge the v1.44 smoothness fixes on screen.** Inertialization continuity, the jump-frame
      root-motion stall, wrapped loop trajectories (needs a rebake), the same-clip brake and the
      foot-lock re-lock are unit tested or compile-verified only.
- [ ] Found by the v1.44 audit and **not fixed yet**:
      - Mirrored frames play *unmirrored* root motion (`MirrorPose.ProcessRootMotion` is empty), and
        mirrored frames carry no foot phase. Another reason to keep mirroring off.
      - Crossfade mode: interrupting a blend replaces the slot still fading out (a pop of the
        outgoing weight). Inertialization, the default, does not have this.
      - AI agents ask for a speed that ramps to zero over the last metre - below any walk clip -
        so idle and walk can flicker on arrival; the obstacle sensor re-picks its avoidance side every
        frame and can wobble when both sides are close.
      - Demo camera: mouse orbit is multiplied by deltaTime, so its speed depends on frame rate.

## Tooling

- [x] **Snapshot state diffing** - "Pin for diff" in the Debug tab's History section: pin the
      scrubbed decision, scrub elsewhere, read the delta (per-group cost deltas with the
      biggest mover called out, frame/clip-changed flags, jump-flag change, character distance,
      mean desired-trajectory shift). The math (`SearchSnapshotDiff.Compute`, runtime assembly) is
      unit tested; the IMGUI panel itself is eyeball-verified like the rest of the window. Pin ages
      are relative to the newest snapshot, so they only hold still while recording is paused
      (preview mode) - fine for the rewind workflow, noted in the code.
- [ ] Snapshot debugger still can't step frame-by-frame *within* a single decision (sub-decision
      playback interpolation).
- [ ] Frame Inspector and Tag timeline have no keyboard navigation or search/filter for large
      clip counts.

## Feature scope not started

- [x] **Blend space integration (MxM-style)** - `BlendSpaceBaker` (Bake tab → "Bake Blend Space
      Clips") samples the source clips on the rig at each time step, blends the resulting POSES by
      each grid point's Gradient Band weights, and writes one real AnimationClip per grid point via
      `PoseClipBaker`. Blending in pose space rather than feature space is what closes the old gap:
      a feature-blended grid point was matchable but unplayable, because playback replays the matched
      frame's actual clip. The baked clips are added to the config's clip list and baked into the
      database through the normal path, so the grid is reviewable and removable like any other clip.
      Verified end-to-end in tests: a 3x1 grid between a 0-degree and a 90-degree source reproduces
      each source at the ends and gives 45 degrees in the middle, and the middle clip is a real saved
      asset with curves and length. Inherits PoseClipBaker's limitation (transform-curve/Generic
      clips - a Humanoid Animator ignores them); surfaced as a warning rather than left to discover.
      Not verified: how a baked grid reads on screen with real mocap, and whether Gradient Band
      weights are the right blend for a given clip set - tune per project and playtest.
- [x] Retargeting through Humanoid so one database serves multiple skeletons - the ghost-on-a-
      different-rig path and the Director's one-click rig swap both do this (copy settings, keep
      the database, retarget onto the new body).
- [x] Auto-tagging from clip naming conventions (Opsive pack setup: 74 clips sorted into 12 tags
      from their file names, no ranges painted by hand).
- [x] **Speed/turn/idle detection from motion itself** - `GaitClassifier` (runtime, unit tested)
      classifies every baked frame from the database's denormalized root velocity (idle/walk/run by
      speed thresholds, turning by direction change in deg/s), smooths single-frame flicker and
      returns consolidated per-clip ranges. `Tools > Kinema > Log Auto-Tag Suggestions`
      (headless-runnable) logs the proposals for the richest baked database - verified on the baked
      Opsive set: detected `Walk+Turn` spans line up with the `...TurnLeft180`-style clip names it
      never reads. **Apply path done too**: "Detect and apply gait tags" in the Tags tab writes the
      ranges into the config via `AutoTagApplier` (same SerializedObject path as hand-authoring, so
      undo works; re-applying replaces instead of stacking; unit tested). Rebake afterwards to get
      the tags into the database.
- [~] Learned Motion Matching (Ubisoft La Forge): decompressor / stepper / projector networks
      replacing the database at runtime for large memory savings. **Step 1 done**: a baked database
      exports to a training dataset (`Tools > Kinema > Learned MM > Export Training Dataset`) -
      float32 feature matrix + mean/std + per-frame clip/time + gait phase + a self-describing
      manifest and a numpy loader; clip boundaries exported so the stepper never trains across a cut.
      **Step 2 prep done**: the PyTorch training pipeline (`Documentation~/Training/train_lmm.py` -
      compressor/decompressor autoencoder, stepper, projector, ONNX export) and the backend-agnostic
      runtime seam `ILearnedMotionModel` (Project/Step/Decompress). Next: the Unity Sentis backend
      that loads the exported ONNX behind that interface, then wiring the controller to it. Sentis is
      still not a package dependency - added only when the backend lands.
- [x] **Timeline / cutscene interop** - `Kinema.MotionMatching.Timeline` (separate optional assembly,
      only compiles with `com.unity.timeline` installed): `MotionMatchingTrack` + `MotionMatchingClipAsset`
      extend `SetMatchingActive` to Timeline - drop a clip on the track to fade matching in for its
      duration, restoring the prior state when no clip on the track is active. Mixer logic (activation,
      restore, and staying active across an overlapping crossfade between two clips) is tested by
      building a real `PlayableGraph` and calling `Evaluate()` directly, no Timeline asset/window
      needed. Not verified: how an authored clip's ease-in/ease-out reads in the Timeline window itself.

## Process

- [x] EditMode automated tests (140 tests, `Tests/Editor`) plus 17 PlayMode tests (`Tests/Runtime`) - **done**.
- [x] **CI workflow re-added** - `.github/workflows/tests.yml`, GitHub Actions via game-ci,
      EditMode tests + coverage report as artifacts. Still needs a `UNITY_LICENSE` repository
      secret set by whoever owns the GitHub repo before it will run green (documented in the
      workflow file) - cannot be verified from this environment.
- [x] **Code coverage measurement/reporting** - `com.unity.testtools.codecoverage` added to
      `Packages/manifest.json`. Verified locally: `-enableCodeCoverage -coverageOptions
      "generateAdditionalMetrics;generateHtmlReport"` produces an OpenCover XML + HTML report
      (`CodeCoverage/`, gitignored - it's run output, not source). Also wired into the CI workflow
      above.
- [x] **Contribution guidelines** - [CONTRIBUTING.md](CONTRIBUTING.md): setup, what to run before a
      PR, where things live, and the "say what you verified by eye vs. couldn't" rule for the areas
      that have no automated coverage.
