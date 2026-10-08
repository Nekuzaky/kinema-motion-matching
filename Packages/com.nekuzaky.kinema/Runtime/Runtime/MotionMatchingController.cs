using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Kinema.MotionMatching
{
    /// <summary>
    /// Drives a character with motion matching. Every <see cref="_searchInterval"/> seconds it
    /// builds a query from the desired trajectory (locomotion intent) and the current pose, searches
    /// the database for the best matching frame, and crossfades to it through a two-slot
    /// <see cref="PlayableGraph"/> whose clock this component owns.
    ///
    /// The component is deliberately input-agnostic: it reads intent from an
    /// <see cref="ILocomotionProvider"/> if one is present, otherwise from <see cref="DesiredVelocity"/>.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Motion Matching Controller")]
    [RequireComponent(typeof(Animator))]
    [DisallowMultipleComponent]
    public sealed class MotionMatchingController : MonoBehaviour
    {
        #region Public

        [Header("Data")]
        [SerializeField] private MotionMatchingDatabase _database;

        [Tooltip("Optional extra databases (stances, states). Switch at runtime with SwitchDatabase.")]
        [SerializeField] private MotionMatchingDatabase[] _additionalDatabases = System.Array.Empty<MotionMatchingDatabase>();

        [SerializeField] private Animator _animator;

        [Tooltip("Search strategy. BurstLinear is the right default; KdTree targets very large databases.")]
        [SerializeField] private SearchAcceleration _searchAcceleration = SearchAcceleration.BurstLinear;

        [Header("Matching")]
        [Tooltip("Per-group weights. Initialized from the database default; tweak live to feel the effect.")]
        [SerializeField] private FeatureWeights _weights = FeatureWeights.Default;

        [Tooltip("Seconds between database searches. 0.1 (10 Hz) is a good default.")]
        [SerializeField, Range(0.02f, 0.5f)] private float _searchInterval = 0.1f;

        [Tooltip("A candidate must beat continuing the current clip by this fraction to trigger a jump.")]
        [SerializeField, Range(0f, 0.5f)] private float _jumpImprovementThreshold = 0.02f;

        // 0.2 is Clavet's number (GDC 2016): his hysteresis is "same anim && abs(dt) < 0.2", and it is
        // the whole of what keeps his system stable - he carries no clip-change penalty at all. Ours
        // sat at 0.12 for no reason anyone wrote down.
        [Tooltip("Candidates within this many seconds of the current frame (same clip) count as 'keep playing'.")]
        [SerializeField, Range(0f, 0.3f)] private float _continuityWindow = 0.2f;

        // Applies to every cut, including a jump inside the clip already playing. It used to apply to
        // a change of clip only, so a jump half a stride back in the same loop - just as visible a
        // cut - needed to be 2% better instead of 27%, and was most of the cuts there were.
        [Tooltip("Penalty on cutting away from the current playback (another clip, or another part " +
                 "of this one), as a fraction of what continuing costs. 0.25 = only cut for " +
                 "something 25% better. Raise to reduce flicker; lower for snappier switching.")]
        [SerializeField, Range(0f, 1f)] private float _clipChangeCost = 0.25f;

        [Tooltip("Seconds at the end of every non-looping clip that a search never jumps into. A " +
                 "frame there has nowhere to go: the clip ends, the pose freezes and the root " +
                 "stops until a later search finds a way out. Playback still reaches those frames.")]
        [SerializeField, Range(0f, 0.5f)] private float _clipEndExclusion = 0.25f;

        public enum TransitionMode
        {
            /// <summary>Two-slot mixer crossfade. Simple, always safe.</summary>
            Crossfade,
            /// <summary>Hard switch + decaying pose offset (AAA standard). Velocity-continuous transitions.</summary>
            Inertialization
        }

        public enum TickMode
        {
            /// <summary>The component advances itself every Update with Time.deltaTime.</summary>
            Automatic,
            /// <summary>The component never advances itself; call <see cref="Step"/>. For fixed-step
            /// or server-authoritative simulation, and for deterministic tests.</summary>
            Manual
        }

        [Header("Ticking")]
        [Tooltip("Automatic: advances itself every Update. Manual: call Step(dt) yourself - fixed-step or server-driven simulation, deterministic tests.")]
        [SerializeField] private TickMode _tickMode = TickMode.Automatic;

        /// <summary>How the character is advanced. See <see cref="TickMode"/> and <see cref="Step"/>.</summary>
        public TickMode Ticking
        {
            get => _tickMode;
            set => _tickMode = value;
        }

        [Header("Playback")]
        [Tooltip("How jumps are smoothed. Inertialization = hard switch + decaying pose offset (AAA standard).")]
        [SerializeField] private TransitionMode _transitionMode = TransitionMode.Inertialization;

        [Tooltip("Crossfade duration when jumping to a new frame.")]
        [SerializeField, Range(0f, 0.4f)] private float _blendTime = 0.15f;

        [SerializeField, Range(0.1f, 2f)] private float _playbackSpeed = 1f;

        [Header("Stride Warping")]
        [Tooltip("Scale clip playback so the baked stride delivers exactly the requested speed. A database with a 1.4 m/s walk and a 4 m/s run can then hit any speed between them, and the legs cycle in sync with the travel instead of sliding.")]
        [SerializeField] private bool _strideWarping = true;

        [Tooltip("How far playback may be scaled (min, max). Beyond ~±30% the motion starts reading as slow-motion or fast-forward.")]
        [SerializeField] private Vector2 _strideWarpRange = new Vector2(0.75f, 1.3f);

        [Tooltip("Below this speed (m/s) warping is meaningless - idle and near-idle play at their authored rate.")]
        [SerializeField, Range(0.05f, 1f)] private float _strideWarpMinSpeed = 0.25f;

        [Tooltip("How quickly the warp factor follows a speed change. Higher = snappier, lower = smoother.")]
        [SerializeField, Range(1f, 30f)] private float _strideWarpSharpness = 10f;

        [Header("Facing")]
        // Without this the body's only source of rotation is the clips' own root motion, which is
        // correct only if the capture contains turns at every speed. Most locomotion sets do not:
        // they are strafe sets, with turns captured from a standstill and none while moving. On one
        // of those a character physically cannot change direction without stopping first, because
        // the only clips carrying any rotation are the idle turns.
        [Tooltip("Turn the body toward where it is going. Off = facing comes only from the clips' " +
                 "root rotation, which needs a capture containing turns at every speed.")]
        [SerializeField] private bool _turnToFacing = true;

        [Tooltip("Fastest the body turns (degrees/second).")]
        [SerializeField, Range(0f, 720f)] private float _maxTurnRate = 360f;

        [Tooltip("Below this speed (m/s) the body does not turn itself: there is no travel direction " +
                 "to face, and turning on the spot is what the idle-turn clips are for - their root " +
                 "rotation would fight this one.")]
        [SerializeField, Range(0f, 2f)] private float _turnMinSpeed = 0.3f;

        [Header("Trajectory Prediction")]
        [SerializeField] private TrajectoryPredictionSettings _prediction = TrajectoryPredictionSettings.Default;

        [Header("Pose Query")]
        [Tooltip("Build the query's pose half from the skeleton actually rendered (after inertialization, stride warp and IK) instead of copying the current database frame. Honest pose costs; falls back to the frame copy when the schema's bones are missing on the rig.")]
        [SerializeField] private bool _livePoseQuery = true;

        [Header("Search Triggering")]
        [Tooltip("Search immediately when the predicted trajectory deviates from the last searched one, instead of waiting out the timer. Turns get answered the frame they happen; straight lines still search at the regular interval.")]
        [SerializeField] private bool _searchOnDeviation = true;

        [Tooltip("Average future-point deviation (meters) that triggers an immediate search.")]
        [SerializeField, Range(0.05f, 1f)] private float _deviationThreshold = 0.2f;

        [Tooltip("Shortest gap between deviation-triggered searches (seconds). Without it a sustained turn fires a search every frame, and every search is a chance to jump - the pose flickers.")]
        [SerializeField, Range(0.02f, 0.2f)] private float _deviationCooldown = 0.06f;

        [Header("Events")]
        [Tooltip("Relay AnimationEvents of the active clip via SendMessage, matching Mecanim semantics.")]
        [SerializeField] private bool _relayAnimationEvents = true;

        [Header("Manual Intent (used when no ILocomotionProvider is present)")]
        [SerializeField] private Vector3 _desiredVelocity;

        [Header("Debug")]
        [SerializeField] private bool _drawGizmos = true;

        [Tooltip("Draw the rig's bone hierarchy in the scene view. Toggleable from the window's Debug tab.")]
        [SerializeField] private bool _drawSkeleton;

        [Tooltip("Skeleton colour. The schema's own bones are drawn thicker, in Bone Color.")]
        [SerializeField] private Color _skeletonColor = new Color(1f, 1f, 1f, 0.35f);
        [SerializeField] private Color _desiredTrajectoryColor = new Color(0.2f, 0.8f, 1f);
        [SerializeField] private Color _candidateTrajectoryColor = new Color(1f, 0.7f, 0.1f);
        [SerializeField] private Color _boneColor = new Color(0.4f, 1f, 0.5f);

        public MotionMatchingDatabase Database => _database;
        public bool IsInitialized => _initialized;
        public MotionMatchingDebugData LastDebug => _debug;

        public int CurrentFrame => _initialized ? MapCurrentFrame() : -1;

        /// <summary>
        /// Contact bitmask of the frame currently playing (bit b = contact bone b grounded). Zero while
        /// an event clip plays (or its last frame still holds the slot): the frame index then falls
        /// back to the last database frame before the event, whose contacts say nothing about a vault
        /// in mid-air - and foot IK reading them pinned that frame's planted foot to the ground.
        /// </summary>
        public byte CurrentContacts => _initialized && _activeEvent == null && _slotClipIndex[_activeSlot] >= 0
            ? _database.GetContacts(MapCurrentFrame())
            : (byte)0;

        /// <summary>Candidate frames must carry ALL these tag bits. 0 = no requirement.</summary>
        public ulong RequiredTags { get; set; }

        /// <summary>Frames carrying ANY of these tag bits are skipped. 0 = nothing excluded.</summary>
        public ulong ExcludedTags { get; set; }

        /// <summary>Convenience: resolves a tag name against the database and adds it to the required mask.</summary>
        public void RequireTag(string tagName, bool required = true)
        {
            if (_database == null) return;
            ulong mask = _database.GetTagMask(tagName);
            if (required) RequiredTags |= mask; else RequiredTags &= ~mask;
        }

        /// <summary>True while a triggered event clip is playing (matching suspended).</summary>
        public bool IsPlayingEvent => _activeEvent != null;

        /// <summary>True while a clip is being force-played by <see cref="PlayClipOverride"/>.</summary>
        public bool IsOverridingClip => _clipOverride;

        /// <summary>Freezes the override clock so an editor timeline can scrub. Only read while overriding.</summary>
        public bool OverridePaused { get; set; }

        /// <summary>Ring buffer of the last matching decisions, for the snapshot debugger.</summary>
        public SearchSnapshotRecorder Snapshots => _snapshots;

        /// <summary>True while a recorded snapshot is being previewed (live ticking is paused).</summary>
        public bool IsPreviewing => _previewing;

        /// <summary>
        /// Selection count per database frame, accumulated since the last <see cref="ResetTelemetry"/>.
        /// Feed it to <see cref="CoverageReport"/> to see which data the matcher actually uses.
        /// </summary>
        public int[] FrameUsage => _frameUsage;

        public int TotalSearches => _totalSearches;
        public int TotalJumps => _totalJumps;

        /// <summary>Current clip-playback scale applied by stride warping (1 = authored rate).</summary>
        public float CurrentStrideWarp => _currentStrideWarp;

        /// <summary>
        /// How fast the root is actually travelling, measured from the transform. Read it against
        /// <see cref="DesiredVelocity"/>: the two disagreeing while nothing is asked of the character
        /// is the signature of a drift, and the pair is the first thing worth knowing about a
        /// character that "moves on its own".
        /// </summary>
        public Vector3 MeasuredVelocity => _measuredVelocity;
        public float AverageCost => _totalSearches > 0 ? _costSum / _totalSearches : 0f;

        /// <summary>
        /// Fraction of live time spent mid-transition, blending two clips together rather than
        /// playing one. The number "robotic" is really about: a jump rate counts decisions, this
        /// counts their cost. Cutting every 0.28s with a 0.15s blend puts this at 54%, and a
        /// character that is the average of two motions for half its life never shows a whole stride.
        /// Cheap to drive down the wrong way - a shorter blend hides it and pops instead - so read it
        /// beside the jump rate, not instead of it. Counts inertialization decays as well as
        /// crossfades: either way the pose on screen is not yet the clip that is playing.
        /// </summary>
        public float BlendFraction => _liveSeconds > 1e-4f ? _blendingSeconds / _liveSeconds : 0f;

        /// <summary>Clears coverage counts and cost accumulators.</summary>
        public void ResetTelemetry()
        {
            if (_frameUsage != null) System.Array.Clear(_frameUsage, 0, _frameUsage.Length);
            _totalSearches = 0;
            _totalJumps = 0;
            _costSum = 0f;
            _blendingSeconds = 0f;
            _liveSeconds = 0f;
        }

        /// <summary>Applies a named weight preset baked into the database. Returns false when unknown.</summary>
        public bool SetCalibrationProfile(string profileName)
        {
            CalibrationProfile profile = _database != null ? _database.FindProfile(profileName) : null;
            if (profile == null) return false;
            Weights = profile.Weights;
            return true;
        }

        /// <summary>
        /// Switches to another database (stance/state change). Rebuilds the runtime; the next search
        /// picks the best frame in the new set and the inertializer absorbs the transition.
        /// </summary>
        public bool SwitchDatabase(MotionMatchingDatabase database)
        {
            if (database == null || !database.IsValid) return false;
            _database = database;
            Initialize();
            return true;
        }

        /// <summary>Switch to a database from the additional list by index.</summary>
        public bool SwitchDatabase(int additionalIndex)
        {
            if (additionalIndex < 0 || additionalIndex >= _additionalDatabases.Length) return false;
            return SwitchDatabase(_additionalDatabases[additionalIndex]);
        }

        /// <summary>
        /// Fades matching in or out against the Animator's own controller (Mecanim interop): at
        /// weight 0 the AnimatorController underneath drives the character (cinematics, scripted
        /// states), at 1 motion matching does. The graph keeps running either way.
        /// </summary>
        public void SetMatchingActive(bool active, float fadeTime = 0.2f)
        {
            _outputTarget = active ? 1f : 0f;
            _outputFade = Mathf.Max(0.01f, fadeTime);
        }
        /// <summary>Whether <see cref="SetMatchingActive"/> was last told to fade in (target weight 1)
        /// rather than out. Reads the target, not the currently-blended weight, so it flips the
        /// instant a fade starts rather than only once it completes.</summary>
        public bool IsMatchingActive => _outputTarget > 0.5f;

        public int CurrentClipIndex => _initialized ? _slotClipIndex[_activeSlot] : -1;
        public float CurrentClipTime => _initialized ? LocalClipTime(_slotClipIndex[_activeSlot], _slotTime[_activeSlot]) : 0f;

        public FeatureWeights Weights
        {
            get => _weights;
            set
            {
                _weights = value;
                _matcher?.UpdateWeights(_weights);
            }
        }

        /// <summary>
        /// Seconds between database searches, live-adjustable. Wider than the inspector's 0.02-0.5
        /// range so LOD systems (e.g. <see cref="MotionMatchingLOD"/>) can push a distant character's
        /// cadence well below 2 Hz without fighting the serialized field's slider clamp.
        /// </summary>
        public float SearchInterval
        {
            get => _searchInterval;
            set => _searchInterval = Mathf.Clamp(value, 0.02f, 2f);
        }

        /// <summary>World-space desired horizontal velocity, used when no provider is attached.</summary>
        public Vector3 DesiredVelocity
        {
            get => _locomotion != null ? _locomotion.DesiredVelocity : _desiredVelocity;
            set => _desiredVelocity = value;
        }

        #endregion

        #region Private and Protected

        private const int SlotCount = 2;

        private ILocomotionProvider _locomotion;
        private MotionMatcher _matcher;
        private MotionMatchingQuery _query;
        private readonly MotionMatchingDebugData _debug = new MotionMatchingDebugData();

        private PlayableGraph _graph;
        private AnimationMixerPlayable _mixer;
        private AnimationLayerMixerPlayable _layerMixer;
        private AnimationClipPlayable _overlayPlayable;
        private float _overlayWeight, _overlayTarget, _overlayFade;
        private PoseInertializer _inertializer;
        private MirrorPose _mirror;
        private bool _playingMirrored;

        // Event playback state (matching suspended while active).
        private MotionEventDefinition _activeEvent;
        private Vector3 _eventTargetPosition;
        private Quaternion _eventTargetRotation;
        private float _eventClipLength;

        private SearchSnapshotRecorder _snapshots;

        // Coverage telemetry: which frames the matcher actually reaches for.
        private int[] _frameUsage;
        private int _totalSearches, _totalJumps;
#if UNITY_EDITOR
        private MotionQualityProbe _probe; // scene-view stats label only
#endif
        private float _costSum;
        private float _currentStrideWarp = 1f;

        // Live state saved while previewing a snapshot, restored by StopPreview.
        private bool _previewing;
        private bool _clipOverride;
        private Vector3 _liveCharacterPosition;
        private Quaternion _liveCharacterRotation;
        private int _liveActiveSlot;
        private int _liveSlot0ClipIndex; private double _liveSlot0Time;
        private int _liveSlot1ClipIndex; private double _liveSlot1Time;
        private float _liveBlend01;
        private bool _liveMirrored;

        private AnimationPlayableOutput _output;
        private float _outputWeight = 1f, _outputTarget = 1f, _outputFade = 0.2f;
        private readonly AnimationClipPlayable[] _slots = new AnimationClipPlayable[SlotCount];
        private readonly int[] _slotClipIndex = new int[SlotCount];
        private readonly double[] _slotTime = new double[SlotCount];
        private int _activeSlot;

        private bool _blending;
        private float _blend01;
        private float _blendingSeconds, _liveSeconds;

        private float _searchTimer;
        private bool _initialized;

        // Seconds left on the inertialization decay started by the last jump (telemetry only).
        private float _inertialBlendRemaining;

        private Vector3 _previousPosition;
        private float _lastTickDt;
        // The controller's own clock: the trajectory history is keyed by it, so several Manual Steps
        // inside one frame still record distinct, correctly spaced samples (Time.time would not move).
        private float _simTime;
        private Vector3 _measuredVelocity;
        private TrajectorySample[] _desiredTrajectory;
        private TrajectorySample[] _candidateTrajectory;

        private TrajectoryHistory _history;
        private int _lastCurrentFrame = -1;
        private Vector3[] _currentBones;

        // Live pose sampling: the schema's bones resolved on this rig, plus last-frame world
        // positions for finite-difference velocities.
        private Transform[] _queryBones;
        private Vector3[] _queryBoneWorldPositions;
        private Vector3[] _queryBonePreviousPositions;
        private Vector3[] _queryBoneVelocities;
        private bool _queryBonesValid;
        private bool _queryBonesPrimed;

        // Future points the last search answered, for deviation-triggered searching.
        private Vector2[] _lastSearchedTrajectory;
        private float _timeSinceSearch;
        private Vector3[] _candidateBones;

        #endregion

        #region Unity API

        private void Reset()
        {
            _animator = GetComponent<Animator>();
        }

        private void Awake()
        {
            if (_animator == null) _animator = GetComponent<Animator>();
            _locomotion = GetComponent<ILocomotionProvider>();
        }

        private void OnEnable()
        {
            Initialize();
        }

        private void OnDisable()
        {
            Teardown();
        }

        private void Update()
        {
            if (_tickMode != TickMode.Automatic) return;
            if (!_initialized || _previewing) return;
            Tick(Time.deltaTime);
        }

        /// <summary>
        /// Advances the character by <paramref name="dt"/> seconds - prediction, search, playback
        /// clocks and one graph evaluation - exactly as an automatic Update tick would. Only does
        /// anything in <see cref="TickMode.Manual"/>: in Automatic the component ticks itself and an
        /// extra Step would double-advance the clocks. No-op while previewing a snapshot (the debug
        /// rewind owns the pose) or before initialization.
        /// </summary>
        public void Step(float dt)
        {
            if (_tickMode != TickMode.Manual)
            {
                Debug.LogWarning($"[MotionMatching] '{name}': Step ignored - Tick Mode is Automatic, " +
                                 "so the component already ticks itself. Set Tick Mode to Manual to drive it.", this);
                return;
            }
            if (!_initialized || _previewing) return;
            Tick(dt);
        }

        private void OnDrawGizmos()
        {
            // Independent of the matching gizmos: the skeleton is what you turn on when you suspect
            // the rig rather than the search - a bone the schema names but the rig does not have,
            // retargeting landing somewhere unexpected, IK bending a leg the wrong way. None of that
            // needs the matcher to be initialized, and it is often exactly why it is not.
            if (_drawSkeleton && _animator != null) DrawSkeleton();

            if (!_drawGizmos || !_initialized || !_debug.HasData) return;
            DrawDebugGizmos();
        }

        #endregion

        #region Main API

        /// <summary>Builds the runtime state and the playable graph. Safe to call again after a rebake.</summary>
        public void Initialize()
        {
            if (_initialized) Teardown();
            if (!Application.isPlaying) return;

            // Errors, and the component switches itself off. These are not preferences that can be
            // warned about and worked around - without them nothing can happen at all, and a warning
            // scrolls out of the console while the component sits there looking enabled and alive.
            // An unticked checkbox in the inspector is a state you can see an hour later.
            if (_database == null || !_database.IsValid)
            {
                Debug.LogError($"[Kinema] '{name}': no valid database, so this controller does nothing. " +
                               "Assign a baked MotionMatchingDatabase - note that is the *Database* " +
                               "asset, not the Config it was baked from; the two are named alike.", this);
                enabled = false;
                return;
            }
            if (_animator == null)
            {
                Debug.LogError($"[Kinema] '{name}': no Animator, so this controller does nothing. " +
                               "It poses a skeleton through a PlayableGraph and needs one to pose.", this);
                enabled = false;
                return;
            }

            _matcher = new MotionMatcher(_database, _weights) { Acceleration = _searchAcceleration };
            _matcher.SetClipEndExclusion(_clipEndExclusion);
            _query = new MotionMatchingQuery(_database.Schema);
            _desiredTrajectory = new TrajectorySample[_database.Schema.TrajectoryPointCount];
            _candidateTrajectory = new TrajectorySample[_database.Schema.TrajectoryPointCount];
            int boneCount = _database.Schema.BoneCount;
            _currentBones = new Vector3[boneCount];
            _candidateBones = new Vector3[boneCount];
            ResolveQueryBones();
            _history = new TrajectoryHistory(128);
            _snapshots = new SearchSnapshotRecorder(240, FeatureGroupExtensions.Count, _database.Schema.TrajectoryPointCount);
            _frameUsage = new int[_database.FrameCount];
            _totalSearches = 0; _totalJumps = 0; _costSum = 0f;
            _blendingSeconds = 0f; _liveSeconds = 0f;
            _debug.DesiredTrajectory = _desiredTrajectory;
            _debug.CandidateTrajectory = _candidateTrajectory;
            _debug.Clear();

            CreateGraph();

            _previousPosition = transform.position;
            _lastTickDt = 0f;
            _inertialBlendRemaining = 0f;
            _searchTimer = 0f;
            _initialized = true;
        }

        /// <summary>Re-applies the database's default weights onto this controller.</summary>
        public void ResetWeightsToDatabaseDefault()
        {
            if (_database != null) Weights = _database.DefaultWeights;
        }

        /// <summary>
        /// Re-syncs runtime state after serialized fields were edited externally (editor window,
        /// inspector during play): rebuilds the weight table and propagates the acceleration mode.
        /// </summary>
        public void NotifySerializedFieldsChanged()
        {
            if (_matcher == null) return;
            _matcher.UpdateWeights(_weights);
            _matcher.Acceleration = _searchAcceleration;
            if (!Mathf.Approximately(_matcher.ClipEndExclusion, _clipEndExclusion))
                _matcher.SetClipEndExclusion(_clipEndExclusion);
        }

        /// <summary>
        /// Plays a triggered action clip, warping the root so the event's contact moment lands on
        /// the given target. Matching is suspended until the clip finishes, then resumes with an
        /// immediate search (the inertializer absorbs the seam).
        /// </summary>
        public bool PlayEvent(MotionEventDefinition definition, Vector3 targetPosition, Quaternion targetRotation)
        {
            if (!_initialized || definition == null || !definition.IsValid) return false;

            _activeEvent = definition;
            _eventTargetPosition = targetPosition;
            _eventTargetRotation = targetRotation;
            _eventClipLength = definition.Clip.length;

            int newSlot = 1 - _activeSlot;
            SetSlotClip(newSlot, definition.Clip, -1, 0f); // -1: external clip, not a database index.
            _activeSlot = newSlot;
            _blending = false;
            _mixer.SetInputWeight(_activeSlot, 1f);
            _mixer.SetInputWeight(1 - _activeSlot, 0f);
            _inertializer?.RequestTransition(definition.BlendIn);
            _mirror?.SetMirrored(false);
            _playingMirrored = false;
            return true;
        }

        /// <summary>
        /// Plays an overlay clip on a separate layer (upper body via avatar mask, for example)
        /// while matching keeps driving the base layer.
        /// </summary>
        public void PlayOverlay(AnimationClip clip, AvatarMask mask, float weight = 1f, float fadeTime = 0.15f)
        {
            if (!_initialized || clip == null || !_layerMixer.IsValid()) return;

            if (_overlayPlayable.IsValid())
            {
                _graph.Disconnect(_layerMixer, 1);
                _graph.DestroyPlayable(_overlayPlayable);
            }

            _overlayPlayable = AnimationClipPlayable.Create(_graph, clip);
            _overlayPlayable.SetApplyFootIK(false);
            _graph.Connect(_overlayPlayable, 0, _layerMixer, 1);
            if (mask != null) _layerMixer.SetLayerMaskFromAvatarMask(1, mask);

            _overlayTarget = Mathf.Clamp01(weight);
            _overlayFade = Mathf.Max(0.01f, fadeTime);
        }

        /// <summary>Fades the overlay layer out.</summary>
        public void StopOverlay(float fadeTime = 0.15f)
        {
            _overlayTarget = 0f;
            _overlayFade = Mathf.Max(0.01f, fadeTime);
        }

        public void SetLocomotionProvider(ILocomotionProvider provider)
        {
            _locomotion = provider;
        }

        /// <summary>
        /// Points the controller at another Animator before it initializes - the ghost-on-a-different-rig
        /// path, where the serialized reference still names the source character's Animator.
        /// </summary>
        public void SetAnimator(Animator animator)
        {
            _animator = animator;
        }

        #endregion

        #region Tools and Utilities

        private void Tick(float dt)
        {
            // How fast the root actually moved over the LAST tick: the displacement since then is
            // that tick's root motion, so it divides by that tick's dt. Dividing by this one made the
            // measurement swing with every frame-time wobble (+-20% frame time read as +-0.6 m/s at a
            // 3 m/s run), which on its own was enough to trip deviation searches.
            if (_lastTickDt > 0f)
                _measuredVelocity = (transform.position - _previousPosition) / _lastTickDt;
            _previousPosition = transform.position;
            _lastTickDt = dt;
            _simTime += dt;
            _history.Record(_simTime, transform.position, transform.forward);

            // Matching runs BEFORE the clocks advance. A jump creates the new slot primed at the
            // matched time with no root-motion delta; advancing the clocks afterwards gives it this
            // tick's step like any other frame. Searching after the advance (as this used to) left
            // the new clip standing still for the evaluation that followed - one frame of zero root
            // motion and a frozen pose on every synchronous jump, several times a second.
            // A zero-length tick (a paused game) decides nothing: there is no time to act in.
            if (!_clipOverride && _activeEvent == null && dt > 0f)
            {
                // Before the prediction, which is expressed in character space: turning the body
                // changes that space, so predicting first would answer for the facing of the frame
                // just gone.
                ApplyFacing(dt);

                // Prediction runs every frame (it is a handful of exponentials); the search itself
                // is the expensive part and stays gated.
                PredictDesiredTrajectory();

                // A turn should be answered the frame it happens, not up to a full interval later -
                // but never faster than the cooldown: a sustained turn deviates continuously, and
                // searching every frame turns each search into a chance to flicker.
                _timeSinceSearch += dt;
                if (_searchOnDeviation && _timeSinceSearch >= _deviationCooldown &&
                    TrajectoryDeviation() > _deviationThreshold)
                    _searchTimer = 0f;

                _searchTimer -= dt;
                if (_searchTimer <= 0f)
                {
                    // Keep the cadence, but never owe more than half an interval: after a long hitch
                    // the debt would otherwise be paid as a search on every frame for several frames,
                    // each one a chance to cut.
                    _searchTimer = Mathf.Max(_searchTimer + _searchInterval, 0.5f * _searchInterval);
                    _timeSinceSearch = 0f;
                    RunSearch();
                }
            }

            UpdateStrideWarp(dt);
            AdvanceClocks(dt);

            // After the clocks: the warp is planned against the time the event clip has reached.
            if (!_clipOverride && _activeEvent != null)
                TickEvent(dt);

            UpdateBlend(dt);
            UpdateOverlay(dt);
            ApplySlotTimes();
            _graph.Evaluate(dt);

            // Sample the pose here - straight out of the graph, BEFORE the LateUpdate IK passes
            // touch it. Sampling after IK poisoned the query: feet moved by foot lock and ground
            // adaptation resemble no baked frame, so every search found something "better" and the
            // pose flickered in a feedback loop (jump -> blend -> stranger pose -> jump).
            if (_livePoseQuery && _queryBonesValid && dt > 0f) SampleQueryBones(dt);
        }

        /// <summary>Warps the root toward the event target until contact, then ends the event at clip end.</summary>
        private void TickEvent(float dt)
        {
            float time = (float)_slotTime[_activeSlot];
            float remainingToContact = _activeEvent.ContactTime - time;

            if (remainingToContact > dt)
            {
                float step = dt / remainingToContact;
                if (_activeEvent.WarpPosition)
                {
                    Vector3 error = _eventTargetPosition - transform.position;
                    error.y = 0f;
                    transform.position += error * step;
                }
                if (_activeEvent.WarpRotation)
                {
                    transform.rotation = Quaternion.Slerp(transform.rotation, _eventTargetRotation, step);
                }

                // A CharacterController moves from where PhysX last saw it, not from the transform:
                // with auto-sync off (Unity's default) the root motion applied in this tick's
                // evaluation would start from the pre-warp position and write it back, silently
                // undoing the warp - the vault would never land on its target.
                if (_activeEvent.WarpPosition && !Physics.autoSyncTransforms)
                    Physics.SyncTransforms();
            }

            if (time >= _eventClipLength - 1e-3f)
            {
                _activeEvent = null;
                _searchTimer = 0f; // resume matching immediately; inertialization absorbs the seam.
            }
        }

        private void UpdateOverlay(float dt)
        {
            // Mecanim interop: fade the whole matching output against the AnimatorController.
            if (!Mathf.Approximately(_outputWeight, _outputTarget))
            {
                _outputWeight = Mathf.MoveTowards(_outputWeight, _outputTarget, dt / _outputFade);
                _output.SetWeight(_outputWeight);
            }

            if (!_layerMixer.IsValid()) return;
            _overlayWeight = Mathf.MoveTowards(_overlayWeight, _overlayTarget, dt / _overlayFade);
            _layerMixer.SetInputWeight(1, _overlayWeight);

            // One-shot overlays fade out on their own once the clip ends.
            if (_overlayPlayable.IsValid() && _overlayTarget > 0f)
            {
                AnimationClip clip = _overlayPlayable.GetAnimationClip();
                if (clip != null && !clip.isLooping && _overlayPlayable.GetTime() >= clip.length)
                    _overlayTarget = 0f;
            }
        }

        /// <summary>
        /// Stride warping: the matcher can only pick speeds that exist in the data, so a database
        /// holding a 1.4 m/s walk and a 4 m/s run can otherwise never travel at 2.5 m/s - it snaps
        /// to one of the two and the feet slide the difference. Scaling clip playback by
        /// requested/baked speed makes the baked stride deliver exactly the requested speed, so the
        /// legs cycle in sync with the travel. This is what lets a handful of clips cover a
        /// continuous speed range.
        /// </summary>
        private void UpdateStrideWarp(float dt)
        {
            float target = 1f;

            if (_strideWarping && _activeEvent == null && !_clipOverride)
            {
                float clipSpeed = _database.GetRootVelocity(MapCurrentFrame()).magnitude;
                Vector3 desired = _locomotion != null ? _locomotion.DesiredVelocity : _desiredVelocity;
                float desiredSpeed = new Vector2(desired.x, desired.z).magnitude;

                // Only warp when both the clip and the request carry real speed; scaling an idle
                // clip toward a walk would just play idle fast.
                if (clipSpeed >= _strideWarpMinSpeed && desiredSpeed >= _strideWarpMinSpeed)
                    target = Mathf.Clamp(desiredSpeed / clipSpeed, _strideWarpRange.x, _strideWarpRange.y);
            }

            float t = 1f - Mathf.Exp(-_strideWarpSharpness * dt);
            _currentStrideWarp = Mathf.Lerp(_currentStrideWarp, target, t);
        }

        private void AdvanceClocks(float dt)
        {
            float step = dt * _playbackSpeed * _currentStrideWarp;
            if (_clipOverride && OverridePaused) step = 0f;
            double before = _slotTime[_activeSlot];
            _slotTime[_activeSlot] = WrapTime(_slotClipIndex[_activeSlot], before + step);
            if (_blending)
            {
                int other = 1 - _activeSlot;
                _slotTime[other] = WrapTime(_slotClipIndex[other], _slotTime[other] + step);
            }

            if (_relayAnimationEvents && step > 0f && _slotClipIndex[_activeSlot] >= 0)
                RelayAnimationEvents(_slotClipIndex[_activeSlot], (float)before, (float)_slotTime[_activeSlot]);
        }

        /// <summary>
        /// Fires the active clip's AnimationEvents crossed this frame via SendMessage (Mecanim
        /// semantics), handling loop wrap. The manual-clock playback bypasses Unity's own dispatch.
        /// </summary>
        private void RelayAnimationEvents(int clipIndex, float from, float to)
        {
            AnimationClip clip = _database.GetClip(clipIndex).Clip;
            if (clip == null) return;
            AnimationEvent[] events = clip.events;
            if (events == null || events.Length == 0) return;

            // The slot clock is monotonic; map to clip-local time before matching event times.
            float fromLocal = LocalClipTime(clipIndex, from);
            float toLocal = LocalClipTime(clipIndex, to);

            if (toLocal >= fromLocal)
            {
                DispatchEventsInRange(events, fromLocal, toLocal);
            }
            else // crossed the loop point this frame
            {
                DispatchEventsInRange(events, fromLocal, clip.length + 1e-4f);
                DispatchEventsInRange(events, -1e-4f, toLocal);
            }
        }

        private void DispatchEventsInRange(AnimationEvent[] events, float from, float to)
        {
            for (int i = 0; i < events.Length; i++)
            {
                AnimationEvent e = events[i];
                if (e.time > from && e.time <= to && !string.IsNullOrEmpty(e.functionName))
                    gameObject.SendMessage(e.functionName, e, SendMessageOptions.DontRequireReceiver);
            }
        }

        /// <summary>
        /// Turns the body toward where it is going, bounded by <see cref="_maxTurnRate"/>.
        ///
        /// This is deliberately not left to the animation. A clip only rotates the character if the
        /// actor turned while it was captured, and a locomotion set that has turns at every speed is
        /// rare - most are strafe sets, with the turns shot from a standstill. On one of those,
        /// relying on root rotation alone means the character can only change direction by stopping,
        /// playing an idle turn, and setting off again, because those are the only clips with any
        /// rotation in them.
        ///
        /// Below <see cref="_turnMinSpeed"/> it does nothing: there is no travel direction to face,
        /// and turning on the spot is exactly what the idle-turn clips do - both rotating at once
        /// would double the turn.
        /// </summary>
        private void ApplyFacing(float dt)
        {
            if (!_turnToFacing || dt <= 0f) return;

            Vector3 facing = _locomotion != null ? _locomotion.DesiredFacing : Vector3.zero;
            facing.y = 0f;

            if (facing.sqrMagnitude < 1e-6f)
            {
                // No explicit facing (the usual case): face where we are trying to go.
                Vector3 velocity = _locomotion != null ? _locomotion.DesiredVelocity : _desiredVelocity;
                velocity.y = 0f;
                if (velocity.magnitude < _turnMinSpeed) return;
                facing = velocity;
            }

            transform.rotation = Quaternion.RotateTowards(
                transform.rotation,
                Quaternion.LookRotation(facing.normalized, Vector3.up),
                _maxTurnRate * dt);
        }

        private void PredictDesiredTrajectory()
        {
            CharacterSpace space = CharacterSpace.FromTransform(transform);
            Vector3 desiredVelocity = _locomotion != null ? _locomotion.DesiredVelocity : _desiredVelocity;
            Vector3 desiredFacing = _locomotion != null ? _locomotion.DesiredFacing : Vector3.zero;

            TrajectoryPredictor.Predict(
                space, _measuredVelocity, desiredVelocity, desiredFacing,
                _database.Schema.TrajectoryTimes, _prediction, _history, _simTime, _desiredTrajectory);
        }

        /// <summary>
        /// Average distance (meters) between the future points predicted now and those the last
        /// search answered. Character space makes this stable: cruising in any straight line keeps
        /// local future points constant, so only genuine intent changes register.
        /// </summary>
        private float TrajectoryDeviation()
        {
            if (_lastSearchedTrajectory == null || _desiredTrajectory == null) return float.MaxValue;

            float[] times = _database.Schema.TrajectoryTimes;
            // Bound by every array, not just times: a controller re-initialized mid-life (a spawned
            // ghost double-initializes) can briefly hold a _lastSearchedTrajectory sized for the
            // previous schema, and indexing past it threw here.
            int count = Mathf.Min(times.Length, Mathf.Min(_desiredTrajectory.Length, _lastSearchedTrajectory.Length));
            float sum = 0f;
            int futureCount = 0;
            for (int i = 0; i < count; i++)
            {
                if (times[i] <= 0f) continue;
                sum += (_desiredTrajectory[i].Position - _lastSearchedTrajectory[i]).magnitude;
                futureCount++;
            }
            return futureCount > 0 ? sum / futureCount : 0f;
        }

        private void RunSearch()
        {
            PrepareSearchQuery();

            if (SearchScheduler != null)
            {
                // Batched path: hand the scheduled job to the scheduler, which completes it later
                // this frame (after every registered controller has scheduled its own). A jump
                // decided by a batched search therefore lands one graph evaluation later than the
                // synchronous path - at a 10 Hz search rate that is a sub-two-millisecond delay in
                // exchange for the searches running concurrently instead of serially.
                Unity.Jobs.JobHandle handle = _matcher.ScheduleSearch(
                    _query, requiredTags: RequiredTags, excludedTags: ExcludedTags);
                // Keep our own copy of the handle: if this controller tears down (disable, rebake,
                // SwitchDatabase) before the scheduler's LateUpdate, Teardown must complete the job
                // before disposing the matcher's NativeArrays - disposing under a running job is a
                // safety-system error in the editor and a race in a build.
                _pendingBatchedSearch = handle;
                _hasPendingBatchedSearch = true;
                SearchScheduler.EnqueueScheduledSearch(this, handle);
                return;
            }

            MotionMatchResult result = _matcher.Search(_query, requiredTags: RequiredTags, excludedTags: ExcludedTags);
            ApplySearchOutcome(result);
        }

        /// <summary>
        /// Optional batching hook. When set, the controller's periodic search is scheduled through
        /// <see cref="MotionMatcher.ScheduleSearch"/> and handed to this scheduler instead of being
        /// completed synchronously inside Tick - the scheduler (see
        /// <see cref="MotionMatchingSearchBatch"/>) completes every registered controller's job after
        /// all of them have been scheduled, so N characters' Burst searches run concurrently.
        /// </summary>
        public IMotionSearchScheduler SearchScheduler { get; set; }

        /// <summary>Completes a search this controller previously scheduled through
        /// <see cref="SearchScheduler"/> and applies its outcome. Called by the scheduler, same
        /// frame, after all registered controllers have scheduled.</summary>
        public void CompleteScheduledSearch(Unity.Jobs.JobHandle handle)
        {
            // No pending flag: this controller tore down (and completed the job itself) between
            // scheduling and now - possibly re-initializing with a NEW matcher whose chunk buffers
            // this stale handle says nothing about. Completing the handle again is a harmless no-op;
            // applying an outcome from it would not be.
            if (!_hasPendingBatchedSearch || !_initialized) { handle.Complete(); _hasPendingBatchedSearch = false; return; }
            _hasPendingBatchedSearch = false;
            // Something took the pose over between scheduling and now - an event started from a
            // script that ran after this controller's Update, a clip override, a snapshot preview.
            // The search answered a question nobody is asking any more; applying it would cut the
            // event clip away under a root warp that keeps running.
            if (_activeEvent != null || _clipOverride || _previewing)
            {
                handle.Complete();
                return;
            }
            MotionMatchResult result = _matcher.CompleteSearch(handle, _query);
            ApplySearchOutcome(result);
        }

        private Unity.Jobs.JobHandle _pendingBatchedSearch;
        private bool _hasPendingBatchedSearch;

        /// <summary>Builds the query for the current tick: trajectory intent, gait phase, and the
        /// live (or frame-copied) pose. Shared by the synchronous and batched search paths.</summary>
        private void PrepareSearchQuery()
        {
            CharacterSpace space = CharacterSpace.FromTransform(transform);

            // Remember what this search answered, so deviation from it can trigger the next one.
            if (_lastSearchedTrajectory == null || _lastSearchedTrajectory.Length != _desiredTrajectory.Length)
                _lastSearchedTrajectory = new Vector2[_desiredTrajectory.Length];
            for (int i = 0; i < _desiredTrajectory.Length; i++)
                _lastSearchedTrajectory[i] = _desiredTrajectory[i].Position;

            int currentFrame = MapCurrentFrame();
            _lastCurrentFrame = currentFrame;
            // The clock is authoritative for gait phase: the playing frame IS the phase state.
            _query.FootPhase = _database.GetFootPhase(currentFrame);
            _query.SetTrajectory(_database, _desiredTrajectory);

            // The honest pose: what is on screen, not the frame the clock is on. Needs one primed
            // sample so velocities exist; until then (and when bones are missing) copy the frame.
            if (_livePoseQuery && _queryBonesValid && _queryBonesPrimed)
                _query.SetPoseFromSkeleton(_database, space, _queryBoneWorldPositions, _queryBoneVelocities, _measuredVelocity);
            else
                _query.SetPoseFromFrame(_database, currentFrame);
        }

        /// <summary>Jump-or-continue decision on a search result. Shared tail of the synchronous and
        /// batched paths; uses <see cref="_lastCurrentFrame"/> captured by
        /// <see cref="PrepareSearchQuery"/> so both paths judge against the same reference frame.</summary>
        private void ApplySearchOutcome(MotionMatchResult result)
        {
            int currentFrame = _lastCurrentFrame;

            // Cost of simply continuing the current clip.
            int continuationFrame = CanContinue() ? ContinuationFrame(currentFrame) : -1;
            float continuationCost = continuationFrame >= 0
                ? _matcher.EvaluateCost(_query, continuationFrame)
                : float.MaxValue;

            bool jumped = false;
            if (result.IsValid && ShouldJump(result, currentFrame, continuationCost))
            {
                StartTransitionTo(result.FrameIndex);
                jumped = true;
            }

            UpdateDebug(result, continuationCost, jumped);
        }

        /// <summary>Finds the schema's bones on this rig by name; missing bones disable live sampling.</summary>
        private void ResolveQueryBones()
        {
            string[] names = _database.Schema.BoneNames;
            int count = names?.Length ?? 0;
            _queryBones = new Transform[count];
            _queryBoneWorldPositions = new Vector3[count];
            _queryBonePreviousPositions = new Vector3[count];
            _queryBoneVelocities = new Vector3[count];
            _queryBonesPrimed = false;
            _queryBonesValid = count > 0;

            // Every missing bone, in one message. Reporting the first and returning means fixing the
            // rig one name per play session, and each of those is a full diagnostic round trip.
            System.Collections.Generic.List<string> missing = null;
            for (int b = 0; b < count; b++)
            {
                _queryBones[b] = FindDeepChild(transform, names[b]);
                if (_queryBones[b] != null) continue;

                _queryBonesValid = false;
                (missing ??= new System.Collections.Generic.List<string>()).Add(names[b]);
            }

            if (missing == null) return;

            // An error even though there is a fallback. A rig that does not carry the bones the
            // database was baked against is the wrong rig, and the fallback quietly demotes the pose
            // query to copying the current frame - which still animates, still looks alive, and
            // matches against a pose nobody is standing in. Silent degradation to a worse mode is
            // the single most expensive thing this package can do to someone.
            Debug.LogError($"[Kinema] '{name}': this rig is missing {missing.Count} of the " +
                           $"{count} bones the database was baked against: {string.Join(", ", missing)}. " +
                           "Either it is the wrong rig, or the database was baked from a different one. " +
                           "The pose query is falling back to copying the current database frame, so " +
                           "matching still runs - against a pose the character is not in.", this);
        }

        private void SampleQueryBones(float dt)
        {
            for (int b = 0; b < _queryBones.Length; b++)
            {
                Vector3 position = _queryBones[b].position;
                _queryBoneVelocities[b] = _queryBonesPrimed ? (position - _queryBonePreviousPositions[b]) / dt : Vector3.zero;
                _queryBonePreviousPositions[b] = position;
                _queryBoneWorldPositions[b] = position;
            }
            _queryBonesPrimed = true;
        }

        private static Transform FindDeepChild(Transform root, string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return null;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == boneName) return t;
            return null;
        }

        private bool ShouldJump(MotionMatchResult result, int currentFrame, float continuationCost)
        {
            MotionFrameInfo candidate = _database.GetFrame(result.FrameIndex);

            // Nowhere to continue (a non-looping clip running out, an event's last frame, a frame the
            // tags now forbid): cut. This has to come before the "already there" test below - the
            // best match for a character standing on the end of a clip is that same end, and calling
            // it continuity froze the character there.
            if (continuationCost >= float.MaxValue) return true;

            // Candidate is essentially "where we already are" -> keep playing, no visible cut.
            if (IsWhereWeAre(candidate)) return false;

            // Every cut pays the brake, inside the current clip as much as out of it: a jump half a
            // stride back in the same loop is as visible as a change of clip.
            //
            // As a FRACTION of what continuing costs, not a constant. The cost space is data-driven
            // and unbounded - a satisfied query sits near 5, one asking for a speed the bake barely
            // holds hits 200 - so a constant is 5% of the first and 0.1% of the second: it brakes
            // nothing exactly when flicker is worst. Measured on the demo before this: 93% of
            // searches jumped, and since the motor moves the body by the clip's root motion, that
            // churn walked an idle character off at 1.2 m/s with no input touched.
            float candidateCost = result.TotalCost + continuationCost * _clipChangeCost;
            return candidateCost <= continuationCost * (1f - _jumpImprovementThreshold);
        }

        /// <summary>
        /// The candidate is the moment already playing, give or take the continuity window - same
        /// clip, same mirroring, measured around the loop for looping clips (0.02 s into a 0.7 s loop
        /// is 0.04 s after 0.68 s, not 0.66 s before it).
        /// </summary>
        private bool IsWhereWeAre(MotionFrameInfo candidate)
        {
            int clipIndex = _slotClipIndex[_activeSlot];
            if (candidate.ClipIndex != clipIndex || candidate.IsMirrored != _playingMirrored) return false;

            MotionClipEntry clip = _database.GetClip(clipIndex);
            float local = LocalClipTime(clipIndex, _slotTime[_activeSlot]);
            float distance = Mathf.Abs(candidate.Time - local);
            if (clip.IsLooping && clip.Length > 0f) distance = Mathf.Min(distance, clip.Length - distance);
            return distance <= _continuityWindow;
        }

        /// <summary>
        /// Whether "keep playing" is a real option: the active slot holds a database clip (not the
        /// event clip a finished event leaves behind, clamped on its last frame), and the frame it is
        /// on is one the search itself would be allowed to pick. Without the tag test a clip the
        /// stance had just forbidden kept competing as the continuation, and a crouch had to beat it
        /// by the whole clip-change brake before the character would go down.
        /// </summary>
        private bool CanContinue()
        {
            if (_slotClipIndex[_activeSlot] < 0) return false;
            if (!_database.HasTags || (RequiredTags == 0ul && ExcludedTags == 0ul)) return true;
            ulong tags = _database.GetFrameTags(_lastCurrentFrame);
            return (tags & RequiredTags) == RequiredTags && (tags & ExcludedTags) == 0ul;
        }

        private void StartTransitionTo(int frameIndex)
        {
            MotionFrameInfo frame = _database.GetFrame(frameIndex);
            MotionClipEntry clip = _database.GetClip(frame.ClipIndex);

            _playingMirrored = frame.IsMirrored;
            _mirror?.SetMirrored(_playingMirrored);

            int newSlot = 1 - _activeSlot;
            SetSlotClip(newSlot, clip.Clip, frame.ClipIndex, frame.Time);

            _activeSlot = newSlot;

            if (_transitionMode == TransitionMode.Inertialization)
            {
                // Hard switch; the inertializer carries the discontinuity as a decaying offset.
                _blending = false;
                _mixer.SetInputWeight(_activeSlot, 1f);
                _mixer.SetInputWeight(1 - _activeSlot, 0f);
                _inertializer?.RequestTransition(_blendTime);
                _inertialBlendRemaining = _inertializer != null ? _blendTime : 0f;
                return;
            }

            _blending = _blendTime > 0f;
            // Resume the blend from the incoming slot's current weight to avoid a pop on interruption.
            _blend01 = _blending ? _mixer.GetInputWeight(newSlot) : 1f;
            if (!_blending)
            {
                _mixer.SetInputWeight(_activeSlot, 1f);
                _mixer.SetInputWeight(1 - _activeSlot, 0f);
            }
        }

        private void UpdateBlend(float dt)
        {
            _liveSeconds += dt;

            if (!_blending)
            {
                if (_inertialBlendRemaining > 0f)
                {
                    _blendingSeconds += Mathf.Min(dt, _inertialBlendRemaining);
                    _inertialBlendRemaining -= dt;
                }
                _mixer.SetInputWeight(_activeSlot, 1f);
                _mixer.SetInputWeight(1 - _activeSlot, 0f);
                return;
            }

            // What "robotic" actually measures. A jump rate says how often the character changes its
            // mind; this says how much of the time it is the average of two animations instead of
            // being one of them - and an average of two motions is neither. Cuts every 0.28s with a
            // 0.15s blend leaves the character mid-transition 54% of the time, and no whole stride is
            // ever visible. The jump rate can look survivable while this one is not.
            _blendingSeconds += dt;

            _blend01 += _blendTime > 0f ? dt / _blendTime : 1f;
            float w = Mathf.Clamp01(_blend01);
            _mixer.SetInputWeight(_activeSlot, w);
            _mixer.SetInputWeight(1 - _activeSlot, 1f - w);

            if (_blend01 >= 1f) _blending = false;
        }

        /// <summary>
        /// Force-plays one database clip and suspends matching until <see cref="StopClipOverride"/>.
        /// The clock keeps running, so the clip plays at its authored rate through the normal graph -
        /// this is inspection of the data as baked, not a matching decision. Stride warping is held
        /// at 1 for the duration so what is on screen is the clip, not a scaled version of it.
        /// </summary>
        public void PlayClipOverride(int clipIndex, float time = 0f)
        {
            if (!_initialized || _database == null) return;
            if (clipIndex < 0 || clipIndex >= _database.ClipCount) return;

            _clipOverride = true;
            _activeEvent = null;
            _blending = false;
            _blend01 = 1f;

            // Drive both slots from the same clip: no stale pose can bleed through the mixer.
            ApplySlotState(_activeSlot, clipIndex, time);
            ApplySlotState(1 - _activeSlot, clipIndex, time);
            _mixer.SetInputWeight(_activeSlot, 1f);
            _mixer.SetInputWeight(1 - _activeSlot, 0f);

            ApplySlotTimes();
        }

        /// <summary>
        /// Force-displays one baked database frame - the exact pose it carries, matching suspended.
        /// An exact ghost replay calls this every frame with the recorded selection so the copy is
        /// frame-identical to the original instead of re-matched from intent.
        /// </summary>
        public void ShowDatabaseFrame(int frameIndex)
        {
            if (!_initialized || _database == null) return;
            if (frameIndex < 0 || frameIndex >= _database.FrameCount) return;

            MotionFrameInfo info = _database.GetFrame(frameIndex);
            _clipOverride = true;
            OverridePaused = true;
            _activeEvent = null;
            _blending = false;

            ApplySlotState(_activeSlot, info.ClipIndex, info.Time);
            ApplySlotState(1 - _activeSlot, info.ClipIndex, info.Time);
            _mixer.SetInputWeight(_activeSlot, 1f);
            _mixer.SetInputWeight(1 - _activeSlot, 0f);
            ApplySlotTimes();
            _graph.Evaluate(0f);
        }

        /// <summary>Hands control back to the matcher; the next search runs immediately.</summary>
        public void StopClipOverride()
        {
            if (!_clipOverride) return;
            _clipOverride = false;
            OverridePaused = false;
            _searchTimer = 0f;
        }

        /// <summary>Scrubs the override clip to a local time. Timeline support; no-op unless overriding.</summary>
        public void SetClipOverrideTime(float time)
        {
            if (!_clipOverride) return;
            int clipIndex = _slotClipIndex[_activeSlot];
            if (clipIndex < 0) return;

            ApplySlotState(_activeSlot, clipIndex, time);
            ApplySlotState(1 - _activeSlot, clipIndex, time);
            ApplySlotTimes();
            _graph.Evaluate(0f);
        }

        /// <summary>
        /// Full-state rewind: replays a recorded <see cref="SearchSnapshot"/> by driving the graph
        /// with its captured transform, slot clip/time, blend and mirror state, then forcing one
        /// evaluation - the exact pose that was on screen at that decision reappears. Live ticking
        /// pauses until <see cref="StopPreview"/>; call it again with a different snapshot to scrub.
        /// </summary>
        public void PreviewSnapshot(SearchSnapshot snapshot)
        {
            if (!_initialized || snapshot == null) return;

            if (!_previewing)
            {
                _previewing = true;
                _liveCharacterPosition = transform.position;
                _liveCharacterRotation = transform.rotation;
                _liveActiveSlot = _activeSlot;
                _liveSlot0ClipIndex = _slotClipIndex[0]; _liveSlot0Time = _slotTime[0];
                _liveSlot1ClipIndex = _slotClipIndex[1]; _liveSlot1Time = _slotTime[1];
                _liveBlend01 = _blend01;
                _liveMirrored = _playingMirrored;
            }

            transform.SetPositionAndRotation(snapshot.CharacterPosition, snapshot.CharacterRotation);
            _activeSlot = snapshot.ActiveSlot;
            ApplySlotState(0, snapshot.Slot0ClipIndex, (float)snapshot.Slot0Time);
            ApplySlotState(1, snapshot.Slot1ClipIndex, (float)snapshot.Slot1Time);
            _blend01 = snapshot.Blend01;
            _blending = false; // hold the exact recorded blend weight instead of continuing to interpolate.
            _mixer.SetInputWeight(_activeSlot, Mathf.Clamp01(_blend01));
            _mixer.SetInputWeight(1 - _activeSlot, 1f - Mathf.Clamp01(_blend01));
            _playingMirrored = snapshot.Mirrored;
            _mirror?.SetMirrored(_playingMirrored);

            ApplySlotTimes();
            _graph.Evaluate(0f);
        }

        /// <summary>Restores live playback exactly where <see cref="PreviewSnapshot"/> paused it.</summary>
        public void StopPreview()
        {
            if (!_previewing) return;
            _previewing = false;

            transform.SetPositionAndRotation(_liveCharacterPosition, _liveCharacterRotation);
            _activeSlot = _liveActiveSlot;
            ApplySlotState(0, _liveSlot0ClipIndex, (float)_liveSlot0Time);
            ApplySlotState(1, _liveSlot1ClipIndex, (float)_liveSlot1Time);
            _blend01 = _liveBlend01;
            _playingMirrored = _liveMirrored;
            _mirror?.SetMirrored(_playingMirrored);

            ApplySlotTimes();
            _graph.Evaluate(0f);
        }

        /// <summary>
        /// Restores one slot to a recorded (clipIndex, time). Database clips are recreated exactly;
        /// event clips (index -1) aren't recoverable from a bare index, so the slot is only
        /// re-timed - previewing mid-event lands close but not pixel-exact.
        /// </summary>
        private void ApplySlotState(int slot, int clipIndex, float time)
        {
            if (clipIndex >= 0)
            {
                SetSlotClip(slot, _database.GetClip(clipIndex).Clip, clipIndex, time);
            }
            else if (_slots[slot].IsValid())
            {
                _slots[slot].SetTime(time);
                _slotTime[slot] = time;
            }
        }

        private void ApplySlotTimes()
        {
            _slots[_activeSlot].SetTime(_slotTime[_activeSlot]);
            if (_blending)
            {
                int other = 1 - _activeSlot;
                if (_slots[other].IsValid())
                    _slots[other].SetTime(_slotTime[other]);
            }
        }

        private void CreateGraph()
        {
            _graph = PlayableGraph.Create($"MotionMatching ({name})");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.Manual);

            _output = AnimationPlayableOutput.Create(_graph, "MM Output", _animator);
            var output = _output;
            _output.SetWeight(_outputWeight);
            _mixer = AnimationMixerPlayable.Create(_graph, SlotCount);

            // Chain: mixer -> [mirror] -> [inertializer] -> layerMixer -> output.
            Playable head = _mixer;

            if (_database.HasMirroredFrames)
            {
                _mirror = new MirrorPose();
                var mirrorNode = _mirror.Create(_graph, _animator);
                _graph.Connect(head, 0, mirrorNode, 0);
                mirrorNode.SetInputWeight(0, 1f);
                head = mirrorNode;
            }

            if (_transitionMode == TransitionMode.Inertialization)
            {
                _inertializer = new PoseInertializer();
                var node = _inertializer.Create(_graph, _animator);
                _graph.Connect(head, 0, node, 0);
                node.SetInputWeight(0, 1f);
                head = node;
            }

            _layerMixer = AnimationLayerMixerPlayable.Create(_graph, 2);
            _graph.Connect(head, 0, _layerMixer, 0);
            _layerMixer.SetInputWeight(0, 1f);
            _layerMixer.SetInputWeight(1, 0f);
            output.SetSourcePlayable(_layerMixer);

            MotionClipEntry first = _database.GetClip(0);
            SetSlotClip(0, first.Clip, 0, 0f);
            SetSlotClip(1, first.Clip, 0, 0f);
            _mixer.SetInputWeight(0, 1f);
            _mixer.SetInputWeight(1, 0f);
            _activeSlot = 0;
            _blending = false;
        }

        private void SetSlotClip(int slot, AnimationClip clip, int clipIndex, float time)
        {
            if (_slots[slot].IsValid())
            {
                _graph.Disconnect(_mixer, slot);
                _graph.DestroyPlayable(_slots[slot]);
            }

            var playable = AnimationClipPlayable.Create(_graph, clip);
            playable.SetApplyFootIK(false);
            playable.SetSpeed(0d); // We own the clock; the playable never auto-advances.
            playable.SetTime(time);
            playable.SetTime(time); // Second set primes the pose immediately (avoids a one-frame lag).

            _graph.Connect(playable, 0, _mixer, slot);
            _slots[slot] = playable;
            _slotClipIndex[slot] = clipIndex;
            _slotTime[slot] = time;
        }

        private void Teardown()
        {
            // A batched search may still be in flight (scheduled in Update, scheduler completes in
            // LateUpdate); its job reads the matcher's NativeArrays, so it must finish before they
            // are disposed below. JobHandle.Complete is idempotent - the scheduler completing the
            // same handle later is a no-op, and CompleteScheduledSearch sees the cleared flag and
            // skips applying the stale outcome.
            if (_hasPendingBatchedSearch)
            {
                _pendingBatchedSearch.Complete();
                _hasPendingBatchedSearch = false;
            }

            if (_graph.IsValid()) _graph.Destroy();
            _inertializer?.Dispose();
            _inertializer = null;
            _mirror?.Dispose();
            _mirror = null;
            _matcher?.Dispose();
            _matcher = null;
            _lastSearchedTrajectory = null;
            _activeEvent = null;
            _playingMirrored = false;
            _initialized = false;
        }

        /// <summary>
        /// Advances a slot clock. Looping clips keep a MONOTONIC time: wrapping it manually would
        /// make the Animator see a length-to-zero jump and emit a negative root-motion delta (a
        /// backward teleport every loop). Unity's own loopTime handling produces continuous root
        /// motion from a growing time. Non-looping and event clips clamp at their end.
        /// </summary>
        private double WrapTime(int clipIndex, double time)
        {
            if (clipIndex < 0)
                return System.Math.Min(time, _eventClipLength); // external event clip

            MotionClipEntry clip = _database.GetClip(clipIndex);
            if (clip.Length <= 0f) return 0d;
            if (clip.IsLooping) return System.Math.Max(time, 0d);
            return System.Math.Min(time, clip.Length);
        }

        /// <summary>Clip-local time in [0, length] regardless of the monotonic slot clock.</summary>
        private float LocalClipTime(int clipIndex, double time)
        {
            if (clipIndex < 0) return (float)time;
            MotionClipEntry clip = _database.GetClip(clipIndex);
            if (clip.Length <= 0f) return 0f;
            return clip.IsLooping ? Mathf.Repeat((float)time, clip.Length) : Mathf.Min((float)time, clip.Length);
        }

        private int MapCurrentFrame()
        {
            int clipIndex = _slotClipIndex[_activeSlot];
            if (clipIndex < 0) return _lastCurrentFrame >= 0 ? _lastCurrentFrame : 0; // event clip: keep last known.

            int frame = _database.MapClipTimeToFrame(clipIndex, LocalClipTime(clipIndex, _slotTime[_activeSlot]));
            return _playingMirrored ? _database.GetMirroredTwin(frame) : frame;
        }

        /// <summary>
        /// The frame "keep playing" is judged on - the one playing now, which is what the query's
        /// live pose half describes - or -1 when a non-looping clip will run out before the next
        /// search comes round. This used to cost the frame one interval AHEAD: a pose 0.1 s away from
        /// the one on screen, compared against a query built from the one on screen, so continuing
        /// paid a pose error no candidate did and lost searches it should have won.
        /// </summary>
        private int ContinuationFrame(int currentFrame)
        {
            // currentFrame's own clip index, not _slotClipIndex[_activeSlot]: right after an event
            // ends, the slot still names the event's clip (index -1, the external-clip sentinel)
            // while currentFrame has already fallen back to the last known database frame. Reading
            // the slot index fed -1 into MapClipTimeToFrame and indexed the clip array out of range.
            MotionFrameInfo frameInfo = _database.GetFrame(currentFrame);
            int clipIndex = frameInfo.ClipIndex;
            MotionClipEntry clip = _database.GetClip(clipIndex);
            if (clip.IsLooping) return currentFrame; // looping clips can always continue.

            // The slot's own clock is precise (sub-frame) and is what normal play should use - but
            // it is only meaningful while the slot is actually playing that clip. Right after an
            // event ends the slot still reads event-clip time, so fall back to the frame's own
            // (quantized, but correct) recorded time in that case.
            float now = _slotClipIndex[_activeSlot] == clipIndex ? (float)_slotTime[_activeSlot] : frameInfo.Time;
            // Runway at the rate the clip is really played: stride warping can run it up to 30% fast.
            float aheadTime = now + _searchInterval * _playbackSpeed * Mathf.Max(1f, _currentStrideWarp);
            return aheadTime >= clip.Length ? -1 : currentFrame;
        }

        private void UpdateDebug(MotionMatchResult result, float continuationCost, bool jumped)
        {
            if (!result.IsValid) return;

            MotionFrameInfo frame = _database.GetFrame(result.FrameIndex);
            MotionClipEntry clip = _database.GetClip(frame.ClipIndex);

            _debug.HasData = true;
            _debug.SelectedFrame = result.FrameIndex;
            _debug.SelectedClipIndex = frame.ClipIndex;
            _debug.SelectedClipName = clip.Name;
            _debug.SelectedTime = frame.Time;
            _debug.TotalCost = result.TotalCost;
            _debug.TrajectoryCost = result.TrajectoryCost;
            _debug.PoseCost = result.PoseCost;
            _debug.ContinuationCost = continuationCost >= float.MaxValue ? -1f : continuationCost;
            _debug.CopyGroupCosts(result.GroupCosts);
            _debug.DidJump = jumped;
            _debug.SearchCount++;

            // Coverage: count what the matcher actually picked.
            if (_frameUsage != null && result.FrameIndex >= 0 && result.FrameIndex < _frameUsage.Length)
                _frameUsage[result.FrameIndex]++;
            _totalSearches++;
            _costSum += result.TotalCost;
            if (jumped) _totalJumps++;

            _database.GetTrajectory(result.FrameIndex, _candidateTrajectory);
            _database.GetBonePoseValues(result.FrameIndex, _candidateBones);
            if (_lastCurrentFrame >= 0) _database.GetBonePoseValues(_lastCurrentFrame, _currentBones);

            _snapshots?.Record(
                Time.time, result.FrameIndex, frame.ClipIndex, frame.Time,
                result.TotalCost, _debug.ContinuationCost, jumped,
                result.GroupCosts, _desiredTrajectory, _candidateTrajectory,
                transform.position, transform.rotation, _activeSlot,
                _slotClipIndex[0], _slotTime[0], _slotClipIndex[1], _slotTime[1],
                _blend01, _playingMirrored);
        }

        private void DrawDebugGizmos()
        {
            CharacterSpace space = CharacterSpace.FromTransform(transform);

            // Ground plane the arrows sit on: the character's feet, not the root pivot.
            Vector3 ground = new Vector3(transform.position.x, transform.position.y + 0.02f, transform.position.z);

            DrawTrajectory(space, ground, _debug.DesiredTrajectory, _desiredTrajectoryColor);
            DrawTrajectory(space, ground, _debug.CandidateTrajectory, _candidateTrajectoryColor);
            DrawBones(space, _candidateBones, _boneColor);

            // Current velocity (what the body is actually doing) vs desired (what was asked). The gap
            // between these two arrows is the responsiveness the matcher is delivering, at a glance.
            Vector3 desired = _locomotion != null ? _locomotion.DesiredVelocity : _desiredVelocity;
            DrawGroundArrow(ground, ground + Flatten(_measuredVelocity) * 0.35f, _boneColor, 0.12f);
            DrawGroundArrow(ground, ground + Flatten(desired) * 0.35f, _desiredTrajectoryColor, 0.12f);

            DrawStatsLabel(ground, desired);
        }

        /// <summary>
        /// Scene-view stats panel above the character - the live read the game view no longer shows
        /// (the overlay was removed to keep it clean), drawn as an editor label so it never ships in
        /// a build. Editor-only: Handles does not exist at runtime.
        /// </summary>
        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void DrawStatsLabel(Vector3 ground, Vector3 desired)
        {
#if UNITY_EDITOR
            if (_probe == null) _probe = GetComponent<MotionQualityProbe>();

            float speed = Flatten(_measuredVelocity).magnitude;
            float wanted = Flatten(desired).magnitude;
            var text = new System.Text.StringBuilder();
            text.AppendLine($"<b>{_debug.SelectedClipName}</b>  f{_debug.SelectedFrame}");
            text.AppendLine($"speed {speed:F1} / {wanted:F1} m/s   warp {_currentStrideWarp:F2}x");
            text.AppendLine($"cost {_debug.TotalCost:F2}  (traj {_debug.TrajectoryCost:F2} · pose {_debug.PoseCost:F2})");
            // Jump rate as a fraction of searches: needs no time base and is the flicker read - a calm
            // character sits near 0, a stuttering one climbs.
            float jumpRate = _totalSearches > 0 ? 100f * _totalJumps / _totalSearches : 0f;
            text.Append($"jump rate {jumpRate:F0}%   blending {BlendFraction * 100f:F0}%");
            if (_probe != null) text.Append($"   foot slide {_probe.FootSlideRate:F3} m/s");

            var style = new GUIStyle
            {
                richText = true,
                fontSize = 11,
                normal = { textColor = _debug.DidJump ? _candidateTrajectoryColor : Color.white },
                padding = new RectOffset(6, 6, 4, 4)
            };
            UnityEditor.Handles.BeginGUI();
            Vector2 screen = UnityEditor.HandleUtility.WorldToGUIPoint(ground + Vector3.up * 2.1f);
            var content = new GUIContent(text.ToString());
            Vector2 size = style.CalcSize(content);
            var rect = new Rect(screen.x - size.x * 0.5f, screen.y - size.y, size.x, size.y);
            UnityEditor.EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.55f));
            GUI.Label(rect, content, style);
            UnityEditor.Handles.EndGUI();
#endif
        }

        /// <summary>
        /// The rig's bone hierarchy: a line from every transform to its parent, with the schema's own
        /// bones marked. Walks the transforms rather than the Animator's humanoid map on purpose -
        /// the schema names bones by their transform name, so this shows the same hierarchy the bake
        /// and the query resolve against, including any bone the rig is missing.
        /// </summary>
        private void DrawSkeleton()
        {
            Transform root = _animator.transform;
            Gizmos.color = _skeletonColor;

            foreach (Transform bone in root.GetComponentsInChildren<Transform>(true))
            {
                if (bone == root || bone.parent == null) continue;
                Gizmos.DrawLine(bone.parent.position, bone.position);
            }

            // The bones the cost function actually looks at, called out from the rest.
            FeatureSchema schema = _database != null ? _database.Schema : null;
            if (schema?.BoneNames == null) return;

            Gizmos.color = _boneColor;
            foreach (string name in schema.BoneNames)
            {
                Transform bone = FindBoneByName(root, name);
                if (bone != null) Gizmos.DrawWireSphere(bone.position, 0.035f);
            }
        }

        private static Transform FindBoneByName(Transform root, string boneName)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                if (t.name == boneName) return t;
            return null;
        }

        private static void DrawBones(CharacterSpace space, Vector3[] localBones, Color color)
        {
            if (localBones == null) return;
            Gizmos.color = color;
            for (int i = 0; i < localBones.Length; i++)
                Gizmos.DrawWireSphere(space.ToWorldOffset3D(localBones[i]), 0.06f);
        }

        private static void DrawTrajectory(CharacterSpace space, Vector3 ground, TrajectorySample[] samples, Color color)
        {
            if (samples == null) return;
            Gizmos.color = color;

            Vector3 previous = ground;
            for (int i = 0; i < samples.Length; i++)
            {
                // Flatten each sample onto the ground plane so the whole path reads as a route drawn
                // on the floor, not a ribbon floating at hip height.
                Vector3 local = space.ToWorldPoint(samples[i].Position);
                Vector3 point = new Vector3(local.x, ground.y, local.z);

                Gizmos.DrawLine(previous, point);
                Gizmos.DrawSphere(point, 0.03f);

                // A facing arrowhead at each sample: direction is half the query, and without it the
                // path shows where the character goes but not which way it will be looking there.
                Vector3 dir = space.ToWorldDirection(samples[i].Direction);
                dir.y = 0f;
                if (dir.sqrMagnitude > 1e-5f)
                    DrawArrowHead(point, dir.normalized, 0.08f);

                previous = point;
            }
        }

        private static Vector3 Flatten(Vector3 v) { v.y = 0f; return v; }

        /// <summary>A flat arrow on the ground: shaft plus a V head, so direction reads without a 3D pivot.</summary>
        private static void DrawGroundArrow(Vector3 from, Vector3 to, Color color, float headSize)
        {
            Gizmos.color = color;
            Vector3 flat = to - from; flat.y = 0f;
            if (flat.sqrMagnitude < 1e-5f) { Gizmos.DrawSphere(from, 0.03f); return; }

            Gizmos.DrawLine(from, to);
            DrawArrowHead(to, flat.normalized, headSize);
        }

        private static void DrawArrowHead(Vector3 tip, Vector3 dir, float size)
        {
            Vector3 side = Vector3.Cross(dir, Vector3.up) * (size * 0.5f);
            Vector3 back = tip - dir * size;
            Gizmos.DrawLine(tip, back + side);
            Gizmos.DrawLine(tip, back - side);
        }

        #endregion
    }
}
