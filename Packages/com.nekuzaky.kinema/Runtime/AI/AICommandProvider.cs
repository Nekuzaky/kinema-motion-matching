using UnityEngine;

namespace Kinema.MotionMatching
{
    /// <summary>
    /// Turns an <see cref="IAIBrain"/>'s high-level command into locomotion intent, through the same
    /// <see cref="ILocomotionProvider"/> the player's input uses. The brain decides *what* (go here,
    /// follow that, flee); this decides *how fast and which way* and hands it to the matcher. Because
    /// the two are separate, the brain can be a rule set today and a language model tomorrow with no
    /// change here, and the NPC always runs the identical motion matching stack as the player.
    ///
    /// Add an <see cref="IAIBrain"/> component (e.g. ScriptedAIBrain or LLMAIBrain) beside this; if
    /// none is present it idles. The current command and status are exposed for the AI window.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/AI Command Provider")]
    [RequireComponent(typeof(MotionMatchingController))]
    public sealed class AICommandProvider : MonoBehaviour, ILocomotionProvider
    {
        #region Public

        // 3 m/s for the same reason the player's input provider caps there: it is the top speed the
        // baked set actually covers. Above it the search has almost nothing to match, so it flickers
        // between clips and the stride warp pins at its ceiling - the feet slide. An AI asking for a
        // speed the data does not hold looks worse than a slower one that does.
        [Tooltip("Top locomotion speed. Keep it inside the range the database covers; asking for more " +
                 "starves the search and slides the feet.")]
        [SerializeField, Min(0f)] private float _maxSpeed = 3f;

        [Tooltip("Stop when closer than this to a MoveTo/Follow goal (meters).")]
        [SerializeField, Min(0.1f)] private float _arriveDistance = 1f;

        [Tooltip("Automatic: advances itself every Update. Manual: call Step(dt) yourself - fixed-step or server-driven simulation, deterministic tests. Pair it with the controller's own Tick Mode.")]
        [SerializeField] private MotionMatchingController.TickMode _tickMode = MotionMatchingController.TickMode.Automatic;

        /// <summary>How this agent is advanced. See <see cref="Step"/>.</summary>
        public MotionMatchingController.TickMode Ticking
        {
            get => _tickMode;
            set => _tickMode = value;
        }

        [Tooltip("Ease speed down within this distance of the goal (meters).")]
        [SerializeField, Min(0.5f)] private float _slowRadius = 2.5f;

        // The ease used to run all the way to zero, so the last metre asked for 0.05-0.4 m/s: below
        // any walk clip, below the controller's turn and stride-warp floors. Idle and walk flickered
        // in turn on every arrival - and an agent a stop clip parked just outside the arrive radius
        // asked for a speed nothing in the data could deliver, and stood there for good.
        [Tooltip("Slowest an agent asks to move while it still has somewhere to go (m/s). Keep it at " +
                 "or above your slowest walk clip; below it the matcher has nothing to play.")]
        [SerializeField, Min(0f)] private float _minMoveSpeed = 0.8f;

        [Tooltip("After arriving, how much further the goal must get before the agent sets off " +
                 "again (meters). Without it an agent on the arrive radius toggles stop/go.")]
        [SerializeField, Min(0f)] private float _rearmDistance = 0.5f;

        [Tooltip("Flee/Follow: desired standoff distance from the target (meters).")]
        [SerializeField, Min(0.5f)] private float _standoff = 2f;

        // Assign this. The fallback below only works in a scene shaped like this package's demo, and
        // failing to find a player is not something an agent can report by behaving oddly - it just
        // stands still, forever, with a Follow goal it can never act on.
        [Tooltip("Who Follow and Flee target. Leave empty only if your player is itself a " +
                 "MotionMatchingController - the fallback search assumes that and nothing else.")]
        [SerializeField] private Transform _playerTarget;

        [Header("Obstacle avoidance")]

        [Tooltip("Steer around obstacles instead of walking into them. Off = straight line to the goal.")]
        [SerializeField] private bool _avoidObstacles = true;

        [Tooltip("How far ahead to look for obstacles (meters).")]
        [SerializeField, Min(0.5f)] private float _probeDistance = 2.5f;

        [Tooltip("Angle of the left/right feeler rays off the travel direction (degrees).")]
        [SerializeField, Range(10f, 80f)] private float _whiskerAngle = 35f;

        [Tooltip("Hardest the agent will steer away from an obstacle (degrees).")]
        [SerializeField, Range(0f, 90f)] private float _maxSteerAngle = 60f;

        // How high this particular agent can get over unaided - which genuinely differs per agent, so
        // it is a field and not a constant. An agent that auto-vaults should carry a value above its
        // vault trigger's ceiling, or avoidance steers it around the very walls it could vault and
        // the vault never fires. An agent that cannot vault must keep it low, or it walks into the
        // crate it has no way over and grinds along it.
        [Tooltip("Obstacles shorter than this are stepped over or vaulted, not walked around (meters " +
                 "above the agent's feet). Set it above the vault trigger's max height on an " +
                 "auto-vaulting agent; leave it low on one that cannot vault.")]
        [SerializeField, Min(0f)] private float _passableHeight = 0.3f;

        [Tooltip("Surfaces tilted less than this are ramps to walk up, not walls to walk around " +
                 "(degrees). Match the CharacterController's Slope Limit.")]
        [SerializeField, Range(0f, 89f)] private float _walkableSlopeLimit = 45f;

        [Tooltip("Stop at ledges instead of running off them. The feelers only see walls - a hole in " +
                 "the floor is invisible to a horizontal ray - so the ground ahead is probed too.")]
        [SerializeField] private bool _stopAtLedges = true;

        [Tooltip("How far ahead to check that there is still floor (meters).")]
        [SerializeField, Min(0.2f)] private float _groundCheckAhead = 1.2f;

        [Tooltip("A drop deeper than this counts as a ledge, not a step down (meters).")]
        [SerializeField, Min(0.2f)] private float _ledgeDrop = 1.5f;

        [Tooltip("Which layers count as obstacles.")]
        [SerializeField] private LayerMask _obstacleLayers = ~0;

        [Tooltip("How fast steering builds and releases. Low = smooth but late, high = sharp but " +
                 "jittery. Jitter here thrashes the matcher's trajectory, so err low.")]
        [SerializeField, Min(0.5f)] private float _steerSharpness = 6f;

        public Vector3 DesiredVelocity { get; private set; }
        public Vector3 DesiredFacing => Vector3.zero; // face travel

        /// <summary>The command currently being executed - a manual override if one is active, else the brain's.</summary>
        public AIAgentCommand Command =>
            OverrideActive ? _override : _brain != null ? _brain.Command : AIAgentCommand.Idle;

        /// <summary>Brain status line for the AI window, or "no brain" when none is attached.</summary>
        public string Status =>
            OverrideActive ? $"manual: {_override.Reason} ({_overrideUntil - Time.time:F0}s)" :
            _brain != null ? _brain.Status : "no brain (idle)";

        public bool ReachedGoal { get; private set; }

        /// <summary>True while the agent is steering around something, for the AI window.</summary>
        public bool Avoiding => Mathf.Abs(_steer) > 0.05f;

        /// <summary>True while a hand-issued command from the AI window is holding control.</summary>
        public bool OverrideActive => Time.time < _overrideUntil;

        /// <summary>
        /// Hand a one-off command to this agent for <paramref name="seconds"/>, from the AI window.
        /// The brain keeps running underneath and takes over again when the override lapses - a nudge,
        /// not a rewire.
        /// </summary>
        public void OverrideCommand(AIAgentCommand command, float seconds = 6f)
        {
            _override = command;
            _overrideUntil = Time.time + Mathf.Max(0.1f, seconds);
        }

        #endregion

        #region Private and Protected

        /// <summary>Chest height - the same the vault probe uses, and above any ground undulation.</summary>
        private const float ProbeHeight = 0.6f;

        private IAIBrain _brain;
        private Transform _player;
        private float _commandStart;
        private AIGoal _lastGoal;
        private AIAgentCommand _override;
        private float _overrideUntil;
        private float _steer;
        private int _preferredSide;
        // The side committed to while the way ahead stays blocked; 0 = not avoiding anything.
        private int _avoidSide;
        private float _avoidSpeedScale = 1f;
        private bool _resolved;

        #endregion

        #region Unity API

        private void Awake() => Resolve();

        /// <summary>
        /// Finds the brain and the target. Called from Awake, and again from the first Tick if Awake
        /// never ran - which it does not while a GameObject is inactive. An agent spawned disabled and
        /// enabled a frame later would otherwise wake with no brain and no target, and report that by
        /// standing still forever.
        /// </summary>
        private void Resolve()
        {
            _resolved = true;
            _brain = GetComponent<IAIBrain>();
            _player = _playerTarget != null ? _playerTarget : FindPlayerByConvention();

            // A fixed side per agent, not a random one: when a head-on obstacle blocks both feelers
            // equally there is no better choice, and re-rolling it every frame makes the agent shiver
            // in place. Parity of the instance id also splits a crowd both ways instead of herding it.
            _preferredSide = (GetInstanceID() & 1) == 0 ? 1 : -1;
        }

        private void Update()
        {
            if (_tickMode != MotionMatchingController.TickMode.Automatic) return;
            Tick(Time.deltaTime);
        }

        /// <summary>
        /// Advances the agent by <paramref name="dt"/> seconds - brain, command resolution, steering -
        /// exactly as an automatic Update tick would. Mirrors
        /// <see cref="MotionMatchingController.Step"/> and exists for the same reasons: a fixed-step
        /// or server-authoritative sim owns its own clock, and a test that owns the clock is
        /// deterministic. The steer is smoothed against dt, so driving it from real frame times is
        /// what makes an agent's turn-in look the same at 30 and 144 fps - and what makes it
        /// unreproducible in a headless test, where frames take microseconds and the smoothing never
        /// accumulates.
        ///
        /// Only does anything in Manual mode; in Automatic the component already ticks itself and an
        /// extra Step would double-advance the smoothing.
        /// </summary>
        public void Step(float dt)
        {
            if (_tickMode != MotionMatchingController.TickMode.Manual)
            {
                Debug.LogWarning($"[MotionMatching] '{name}': Step ignored - Tick Mode is Automatic, " +
                                 "so the component already ticks itself. Set Tick Mode to Manual to drive it.", this);
                return;
            }
            Tick(dt);
        }

        private void Tick(float dt)
        {
            if (!_resolved) Resolve();

            var context = new AIContext
            {
                Position = transform.position,
                Velocity = DesiredVelocity,
                Player = _player,
                DistanceToPlayer = _player != null ? Flat(_player.position - transform.position).magnitude : Mathf.Infinity,
                ReachedGoal = ReachedGoal,
                TimeSinceCommand = Time.time - _commandStart
            };
            _brain?.Tick(context);

            AIAgentCommand command = Command;
            if (command.Goal != _lastGoal)
            {
                // The brain's own words for why. An NPC doing something inexplicable is the most
                // common thing to have to explain, and the explanation already exists - it just never
                // left the AI tab.
                if (KinemaLog.Verbose)
                    KinemaLog.Event($"{name}: {_lastGoal} -> {command.Goal} @ {command.SpeedScale:F2}x " +
                                    $"(\"{command.Reason}\")", this);
                _lastGoal = command.Goal;
                _commandStart = Time.time;
            }

            Vector3 desired = Resolve(command);
            DesiredVelocity = _avoidObstacles ? Avoid(desired, command.Target, dt) : desired;
        }

        private void OnDrawGizmosSelected()
        {
            if (!_avoidObstacles) return;

            Vector3 forward = DesiredVelocity.sqrMagnitude > 1e-4f
                ? DesiredVelocity.normalized
                : Flat(transform.forward).normalized;
            Vector3 origin = transform.position + Vector3.up * ProbeHeight;

            Gizmos.color = Avoiding ? Color.red : Color.green;
            Gizmos.DrawLine(origin, origin + forward * _probeDistance);
            Gizmos.color = new Color(1f, 1f, 0f, 0.6f);
            Gizmos.DrawLine(origin, origin + Turn(forward, -_whiskerAngle) * _probeDistance);
            Gizmos.DrawLine(origin, origin + Turn(forward, _whiskerAngle) * _probeDistance);
        }

        #endregion

        #region Tools and Utilities

        private Vector3 Resolve(AIAgentCommand command)
        {
            float speed = _maxSpeed * Mathf.Clamp01(command.SpeedScale <= 0f ? 1f : command.SpeedScale);

            switch (command.Goal)
            {
                case AIGoal.MoveTo:
                case AIGoal.Patrol:
                case AIGoal.Wander:
                    return SeekTo(command.Position, speed, _arriveDistance);

                case AIGoal.Follow:
                    if (command.Target == null) { ReachedGoal = true; return Vector3.zero; }
                    return SeekTo(command.Target.position, speed, _standoff);

                case AIGoal.Flee:
                    if (command.Target == null) { ReachedGoal = true; return Vector3.zero; }
                    Vector3 away = Flat(transform.position - command.Target.position);
                    ReachedGoal = away.magnitude > _standoff * 3f;
                    return away.sqrMagnitude > 1e-4f ? away.normalized * speed : Vector3.zero;

                default:
                    ReachedGoal = true;
                    return Vector3.zero;
            }
        }

        private Vector3 SeekTo(Vector3 target, float speed, float stopDistance)
        {
            Vector3 toGoal = Flat(target - transform.position);
            float distance = toGoal.magnitude;

            // Hysteresis: once there, stay there until the goal is clearly elsewhere.
            float rearm = ReachedGoal ? _rearmDistance : 0f;
            if (distance <= stopDistance + rearm) { ReachedGoal = true; return Vector3.zero; }
            ReachedGoal = false;

            float eased = speed * Mathf.Clamp01((distance - stopDistance) / _slowRadius);
            eased = Mathf.Max(eased, Mathf.Min(_minMoveSpeed, speed));
            return toGoal / Mathf.Max(distance, 1e-4f) * eased;
        }

        /// <summary>
        /// Bends the desired velocity around whatever is ahead, using three feelers (centre and one
        /// each side). The agent steers toward the side with more room and slows as the obstacle
        /// closes, so it rounds a wall instead of grinding along it.
        ///
        /// The steer angle is smoothed rather than applied raw. The matcher predicts a future
        /// trajectory from this velocity and searches against it; a steer that snaps between frames
        /// rewrites that prediction every frame, and the search flips between clips - the same
        /// stutter a jittery input would cause. Smoothing keeps the intent legible to the search.
        /// </summary>
        private Vector3 Avoid(Vector3 desired, Transform goalTarget, float dt)
        {
            float speed = desired.magnitude;
            if (speed < 1e-3f)
            {
                _steer = Mathf.Lerp(_steer, 0f, Damp(dt));
                _avoidSpeedScale = Mathf.Lerp(_avoidSpeedScale, 1f, Damp(dt));
                _avoidSide = 0;
                return desired;
            }

            Vector3 forward = desired / speed;
            float centre = Probe(forward, goalTarget);

            float target = 0f;
            float speedScale = 1f;
            if (centre < _probeDistance)
            {
                float left = Probe(Turn(forward, -_whiskerAngle), goalTarget);
                float right = Probe(Turn(forward, _whiskerAngle), goalTarget);

                // Steer toward the roomier side; when they tie, this agent's fixed side breaks it.
                // Then keep that side while the way ahead stays blocked. Re-picking every frame let
                // two near-equal feelers flip it back and forth - the steer averaged out to nothing
                // and the agent wobbled straight into the wall. Only a clearly better other side
                // (a dead end on this one) changes its mind.
                float bias = right - left;
                int roomier = Mathf.Abs(bias) > 0.1f ? (bias > 0f ? 1 : -1) : _preferredSide;
                if (_avoidSide == 0 || bias * _avoidSide < -0.35f * _probeDistance) _avoidSide = roomier;

                // Urgency: touching the obstacle steers hardest, a distant one barely at all.
                target = _avoidSide * (1f - centre / _probeDistance);

                // Slow into the turn. Cornering at full speed either clips the obstacle or asks the
                // search for a hard turn at a speed the data has no clip for.
                speedScale = Mathf.Lerp(1f, 0.45f, 1f - centre / _probeDistance);
            }
            else
            {
                _avoidSide = 0;
            }

            // Smoothed like the steer, and for the same reason: the search reads this speed as intent,
            // and one that snaps with every feeler reading is a stop/go stutter.
            _avoidSpeedScale = Mathf.Lerp(_avoidSpeedScale, speedScale, Damp(dt));
            speed *= _avoidSpeedScale;

            _steer = Mathf.Lerp(_steer, target, Damp(dt));
            Vector3 heading = Turn(forward, _steer * _maxSteerAngle);

            // Backstop. Steering is smoothed, so it can fail to come round in time - and steering
            // away from a wall can itself aim the agent at a drop. Refusing the step is the last
            // thing between a chasing agent and the bottom of a gap.
            if (_stopAtLedges && !HasFloorAt(heading, _groundCheckAhead)) return Vector3.zero;

            return heading * speed;
        }

        /// <summary>
        /// Free distance along <paramref name="direction"/>, or <see cref="_probeDistance"/> when
        /// clear. Four things read as clear on purpose:
        /// <list type="bullet">
        /// <item>the agent's own colliders;</item>
        /// <item>anything low enough to vault or step over - steering around a knee-high crate is
        /// exactly what we do not want when the character can go over it;</item>
        /// <item>walkable slopes. The feeler is a flat ray at chest height, so on a ramp it strikes
        /// the slope face itself. Judging that by height would read a ramp as a wall - its collider
        /// reaches metres up - and the agent would refuse to climb anything. The face angle is what
        /// separates a ramp you walk up from a wall you walk around;</item>
        /// <item><paramref name="goalTarget"/>, the thing a Follow command is chasing. It stands
        /// closer than the feelers reach, so treating it as an obstacle would make the agent swerve
        /// away from the very target it is closing on, and orbit it forever.</item>
        /// </list>
        /// A missing floor blocks it just as a wall does. Nothing else would: these are horizontal
        /// rays, and a hole has nothing in it to hit - so obstacle avoidance alone reports a clear
        /// path into a gap precisely because the gap is empty.
        /// </summary>
        private float Probe(Vector3 direction, Transform goalTarget)
        {
            Vector3 origin = transform.position + Vector3.up * ProbeHeight;
            float wall = _probeDistance;

            if (Physics.Raycast(origin, direction, out RaycastHit hit, _probeDistance,
                    _obstacleLayers, QueryTriggerInteraction.Ignore)
                && !hit.collider.transform.IsChildOf(transform)
                && !(goalTarget != null && hit.collider.transform.IsChildOf(goalTarget))
                && hit.collider.bounds.max.y - transform.position.y > _passableHeight
                && Vector3.Angle(hit.normal, Vector3.up) > _walkableSlopeLimit)
                wall = hit.distance;

            // A missing floor reads exactly like a wall at the same distance, so the steering above
            // rounds a ledge the same way it rounds a crate. Stopping dead at one instead would
            // strand the agent: a brain only re-picks when it reaches a goal, so an agent frozen at
            // an edge stays frozen for good.
            if (_stopAtLedges && _groundCheckAhead < wall && !HasFloorAt(direction, _groundCheckAhead))
                wall = _groundCheckAhead;

            return wall;
        }

        private bool HasFloorAt(Vector3 direction, float distance)
        {
            Vector3 probe = transform.position + Vector3.up * 0.1f + direction * distance;
            return Physics.Raycast(probe, Vector3.down, _ledgeDrop + 0.1f,
                _obstacleLayers, QueryTriggerInteraction.Ignore);
        }

        private float Damp(float dt) => 1f - Mathf.Exp(-_steerSharpness * dt);

        private static Vector3 Turn(Vector3 v, float degrees) => Quaternion.AngleAxis(degrees, Vector3.up) * v;

        private static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

        /// <summary>
        /// Last resort when <see cref="_playerTarget"/> is empty: the one MotionMatchingController in
        /// the scene that is not itself AI-driven.
        ///
        /// That is a convenience for scenes shaped like this package's demo, where the player is a
        /// matched character too. It is wrong for most real games - an FPS player is a capsule and a
        /// camera, carries no MotionMatchingController, and this finds nothing. So it says so, loudly,
        /// once: an agent that cannot find its target does not misbehave in any visible way. It stands
        /// perfectly still holding a Follow goal it can never act on, which looks like the AI being
        /// broken rather than the AI having nothing to follow.
        /// </summary>
        private Transform FindPlayerByConvention()
        {
            foreach (var controller in UnityCompat.FindAll<MotionMatchingController>())
                if (controller.GetComponent<AICommandProvider>() == null)
                    return controller.transform;

            Debug.LogError($"[Kinema] '{name}': Player Target is empty and no non-AI " +
                           "MotionMatchingController exists to fall back on, so Follow and Flee have " +
                           "nothing to aim at and this agent will stand still. Assign Player Target.", this);
            return null;
        }

        #endregion
    }
}
