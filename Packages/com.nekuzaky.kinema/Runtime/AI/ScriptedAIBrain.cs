using UnityEngine;

namespace Kinema.MotionMatching
{
    /// <summary>
    /// A deterministic rule-set brain: patrol a set of waypoints, wander a radius, or follow the
    /// player. No dependencies, no network - the always-available default, and the fallback an
    /// <c>LLMAIBrain</c> drops back to when it has no answer yet. Same <see cref="IAIBrain"/>
    /// contract as any other brain, so the AI window drives it identically.
    /// </summary>
    [AddComponentMenu("Kinema/Motion Matching/Scripted AI Brain")]
    public sealed class ScriptedAIBrain : MonoBehaviour, IAIBrain
    {
        #region Public

        public enum Behaviour { Wander, Patrol, FollowPlayer }

        [SerializeField] private Behaviour _behaviour = Behaviour.Wander;

        [Tooltip("Wander radius around the spawn point (Wander).")]
        [SerializeField, Min(1f)] private float _wanderRadius = 8f;

        [Tooltip("Waypoints to patrol in order (Patrol). Empty falls back to Wander.")]
        [SerializeField] private Transform[] _waypoints;

        [Tooltip("Seconds to idle at each goal before moving on.")]
        [SerializeField, Min(0f)] private float _pause = 1.2f;

        // One fixed speed pins the search inside a single band of the data forever: the agent never
        // walks and never runs, it jogs from birth to death, and every stride-length clip in the bake
        // goes unused. Asking for a speed that tracks the distance left is what makes an NPC pull the
        // walk, run and start/stop clips out of the same database the player does.
        [Tooltip("Speed asked for when strolling in (0..1 of the agent's max speed).")]
        [SerializeField, Range(0f, 1f)] private float _walkScale = 0.35f;

        // 0.8, not 1.0. Full scale means the provider's top speed, which is itself the fastest the
        // bake covers - the edge of the data, where only a few percent of frames are within reach.
        // A player only touches that edge in bursts; an agent chasing a far goal would sit there
        // permanently, and the search starves the whole time. Measured on the demo at 1.0: the
        // follower's cost hit 202 (trajectory 159) while it sprinted, and fell to 8 the moment it
        // closed in and slowed down. It was picking crouch and jump clips to run with.
        [Tooltip("Speed asked for when the goal is far (0..1 of the agent's max speed). Keep it off " +
                 "1.0: the top of the range is the edge of what the bake covers.")]
        [SerializeField, Range(0f, 1f)] private float _runScale = 0.8f;

        [Tooltip("Beyond this distance from the goal the agent runs; inside it, it eases to a walk.")]
        [SerializeField, Min(0.5f)] private float _runDistance = 6f;

        public AIAgentCommand Command { get; private set; } = AIAgentCommand.Idle;
        /// <summary>
        /// What the brain is doing, for the AI window. Built when read, not every tick: the moving
        /// statuses carry a live distance, and formatting one per agent per frame was a steady
        /// string allocation on every AI character for a label only the editor ever looks at.
        /// </summary>
        public string Status => _statusKind switch
        {
            StatusKind.Following => $"{Gait(_statusSpeed)} after the player ({_statusDistance:F0} m)",
            StatusKind.MovingTo => $"{Gait(_statusSpeed)} ({_statusDistance:F0} m to go)",
            _ => _statusText
        };

        private enum StatusKind { Text, Following, MovingTo }
        private StatusKind _statusKind;
        private string _statusText = "idle";
        private float _statusSpeed, _statusDistance;

        private const string RunningAfterPlayer = "running after the player";
        private const string WalkingAfterPlayer = "walking after the player";

        private void SetStatus(string text) { _statusKind = StatusKind.Text; _statusText = text; }

        private void SetStatus(StatusKind kind, float speed, float distance)
        {
            _statusKind = kind;
            _statusSpeed = speed;
            _statusDistance = distance;
        }

        #endregion

        #region Private and Protected

        private Vector3 _home;
        private int _waypointIndex;
        private float _pauseUntil;
        private bool _started;

        #endregion

        #region Unity API

        private void Start()
        {
            _home = transform.position;
            _started = true;
            Repick(default);
        }

        #endregion

        #region IAIBrain

        public void Tick(in AIContext context)
        {
            if (!_started) return;

            if (_behaviour == Behaviour.FollowPlayer)
            {
                if (context.Player != null)
                {
                    float speed = SpeedFor(context.DistanceToPlayer);
                    Command = AIAgentCommand.FollowTarget(context.Player, speed,
                        IsRunning(speed) ? RunningAfterPlayer : WalkingAfterPlayer);
                    SetStatus(StatusKind.Following, speed, context.DistanceToPlayer);
                }
                else { Command = AIAgentCommand.Idle; SetStatus("no player to follow"); }
                return;
            }

            // Wander / Patrol keep their goal but re-ask for a speed as it closes, so the agent runs
            // the long leg and walks the last few meters instead of arriving at one flat pace.
            if (Command.Goal == AIGoal.MoveTo && !context.ReachedGoal)
            {
                Vector3 toGoal = Command.Position - context.Position;
                toGoal.y = 0f;
                float speed = SpeedFor(toGoal.magnitude);
                Command = AIAgentCommand.MoveTo(Command.Position, speed, Command.Reason);
                SetStatus(StatusKind.MovingTo, speed, toGoal.magnitude);
            }

            // Wander / Patrol: advance to the next goal once the current one is reached and the pause
            // has elapsed. The pause is what returns the agent to idle instead of pin-balling.
            if (context.ReachedGoal)
            {
                if (_pauseUntil <= 0f) { _pauseUntil = Time.time + _pause; SetStatus("arrived, pausing"); }
                else if (Time.time >= _pauseUntil) { _pauseUntil = 0f; Repick(context); }
            }
        }

        #endregion

        #region Tools and Utilities

        /// <summary>Walk when the goal is close, run when it is far, ramped between the two.</summary>
        private float SpeedFor(float distance) =>
            Mathf.Lerp(_walkScale, _runScale, Mathf.Clamp01(distance / _runDistance));

        private bool IsRunning(float speedScale) => speedScale > (_walkScale + _runScale) * 0.5f;

        private string Gait(float speedScale) => IsRunning(speedScale) ? "running" : "walking";

        private void Repick(in AIContext context)
        {
            if (_behaviour == Behaviour.Patrol && _waypoints != null && _waypoints.Length > 0)
            {
                _waypointIndex = (_waypointIndex + 1) % _waypoints.Length;
                Transform wp = _waypoints[_waypointIndex];
                if (wp != null)
                {
                    Command = AIAgentCommand.MoveTo(wp.position, _runScale,$"patrol to waypoint {_waypointIndex}");
                    SetStatus($"patrolling to WP{_waypointIndex}");
                    return;
                }
            }

            // Wander (also the patrol fallback when waypoints are missing).
            Vector2 disc = Random.insideUnitCircle * _wanderRadius;
            Vector3 goal = _home + new Vector3(disc.x, 0f, disc.y);
            Command = AIAgentCommand.MoveTo(goal, _runScale,"wandering");
            SetStatus("wandering");
        }

        #endregion
    }
}
